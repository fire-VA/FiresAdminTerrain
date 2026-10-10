using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // ── VOXEL CHUNK CACHE (Stage 1 — generation output) ──────────────────
    //
    // Chunks are regenerated from scratch on EVERY visit: VoxelChunk has no
    // save path, and eviction just nulls the arrays. Deterministic, so it is
    // correct — but the generate cost repeats forever, and the dedi measured
    // zones stuck at `swapped=0 pendingSwap=44` with chunks queued behind the
    // apply pipeline. Caching the generation output removes that work instead
    // of merely scheduling it better.
    //
    // WHAT IS CACHED: the post-generate arrays — _seed (ground baseline),
    // _density (seed + cave carve) and _biome — plus the chunk Kind. Player
    // EDITS are deliberately not in here; they persist separately through the
    // existing ZDO snapshot path, and the cache must only ever hold the
    // deterministic generator output so a stale entry can never resurrect a
    // deleted carve.
    //
    // Three common cache patterns (the same ideas BetterContinents' world cache uses):
    //   * content-addressed key — the directory name IS the hash of every
    //     input, so changing a cave/river config silently re-derives instead
    //     of serving stale terrain. No version field to forget to bump.
    //   * atomic write — .tmp then File.Move, so a half-written entry can
    //     never be read as valid.
    //   * verify on read — length check, and any failure deletes the entry
    //     and falls through to generation rather than throwing.
    //
    // And four things it does that we deliberately do NOT:
    //   * it never evicts (its cache grows forever) — ours is LRU-bounded.
    //   * it never compresses — ours does; density is smooth and packs hard.
    //   * it "compacted" a biome map to 1 byte/pixel and made it 16MB, bigger
    //     than the PNG it replaced — so the size is measured, not assumed.
    //   * its minimap cache keys on a hardcoded world version rather than the
    //     settings hash, so edits leave stale entries — everything the data is
    //     derived from is in our key.
    internal static class VoxelChunkCache
    {
        // Bump when the GENERATOR changes shape in a way the config hash below
        // cannot see (new carve stage, different sample layout, array sizes).
        // 2 = payload stored RAW (v1 was compressed). 3 = the gen-water level
        // joined the blob (carve+fill water). 4 = the reconciled surface moved
        // to the decimated 8m build — the ALGORITHM behind the stored levels
        // changed, so every cached level must re-derive or cached and fresh
        // chunks disagree at their seams. Bumping this changes the KEY, so the
        // old directory is pruned wholesale rather than every stale entry
        // failing its length check and logging a warning on the way out.
        // 5 = the water store gate unified with the swim gate (>=0.15) — the
        // stored columns changed, so cached chunks must re-derive.
        // 7 = seat-down moved from the shared surface (which dragged the bed
        // carve down with it — no relief gained) to the STORED level only:
        // banks keep their height, water sits 0.4m lower against them.
        // 8 = reconciled surface v2: governance smoothing (2x3x3 + re-clamp),
        //     waterfall break + mid-air suppression sentinels — stored water
        //     columns change wherever governance wobbled or a face was wet.
        // 9 = cave-shaft columns store NO water (sinkhole mouths stay open).
        // 10 = basin composition (pond keep-chances, uncapped deep-lake rule)
        //      changes which columns are wet.
        // 11 = per-biome water profiles + lake districts.
        // 12 = slope-basin surgery (smear min-depth + rim cut-in) reshapes
        //      ground and wet columns around kept basins.
        // 13 = sky islands union into density; swamp pond seeding.
        // 14 = walk overhaul (size-based keep, mask-2 drops, bowl excavation)
        //      + mouth seat-down taper.
        // 15 = sky island reshape (edge taper, rolling tops, random depth).
        // 16 = island placement v2: ascending clusters + 30-300m band.
        // 17 = basin keep on AREA (deep-cell counter kept 0 basins twice),
        //      centroid biome + elevation-cold classification.
        // 18 = still-water basins FLATTENED to one level (lowest bank).
        // 19 = classification from the HIGH RIM (water-margin swamp paint
        //      made every basin self-classify swamp = keep-all + liquid).
        // 20 = sky island archetypes (mountain/crater/grotto/rolling).
        // 21 = PHANTOM WATER PURGE (wet-aware reconcile — dry terrain no
        //      longer reads as spill level) + seeded mountain tarns.
        // 22 = seeded basins bypass the composition roll; island footprint
        //      terrain clearance (no more slope-fused cliff shelves).
        // 23 = rarity un-starved (post-phantom recalibration: districts
        //      1/0.5/0.2, tarn chance 0.5).
        // 24 = cirque tarns (slope shelves + moraine lip), seeded keep bypass,
        //      cave entrance wet veto + near-surface min-bite, class sampler
        //      floor alignment, shore distance field.
        // 25 = archipelago clusters (satellites + mother), island water
        //      (grotto pools with ramp entrances, crater/rolling ponds,
        //      outlet rills), erosion brush deposition + biome edge jitter.
        // 26 = DIG-IN tarns/swamp ponds (rim-ring anchored level, no lip dam,
        //      12m headwall cap) + walk-basin override in the reconcile
        //      (perched basins keep their level and their FROZEN class).
        // 27 = ALL slope/headwall gates removed (any site digs in), tarn
        //      bowls to ~5m, walk excavation 0.8/1.6/2.6/3.6/4.2 + 4.8m
        //      interior — real lakes, flat at the lowest bank, period.
        // 28 = slope-gated dome deposition (slide aprons at the base, no
        //      mid-slope cones) + radius-2 cone-rounder in the despike.
        // 29 = CONTINUOUS cave min-bite (the binary gate aliased into diamond
        //      lattice pits), both-sides basins (dig high + berm the low
        //      bank), mountain wet paint tight/faint.
        // 30 = berm crest +0.9 with 2-cell plateau (sheet seats INSIDE the
        //      bowl) + one-class-per-body unification (no half-frozen lakes).
        // 31 = outlet streams: berm spillway notches + brooks carved from
        //      lake/pond/swamp rims down to the nearest lower water.
        // 32 = headwater streams: carved meandering mountain brooks from
        //      high sources down to lower water (no catchment threshold).
        // 33 = lowland lake/pond seeder (meadows/BF/plains/mistlands) +
        //      ring-2 berms on wide streams and outlet brooks (audit fix).
        // 34 = biome water-style table (per-biome depth/berm/paint) + river
        //      shore paint minimal with noise+slope beach pockets.
        // 35 = sheets flush to banks (nearest-cell edge heal), class-gated
        //      store (no floaters past berms), fall/mid-air cuts DELETED —
        //      river sheets bend down slopes at their carved offset.
        // 36 = store gate = RAW 2m wet field (IsWetRawAt) — exact shoreline
        //      fill, no 8m stair-step, shallow rim ring included.
        // 37 = store also accepts CHANNEL columns (mask incl. margins) — the
        //      wetted bench fills again and river sheets clip into banks.
        // 38 = stream/brook berms BOUNDED (no cliff curtain walls), ridged
        //      amps tamed + pre-carve thermal relaxation (no spike fins),
        //      deposition gate loosened to steep-only.
        // 39 = river level never clamps up to deposit humps (monotone
        //      downstream) — gorge channels carve clean through aprons.
        // 40 = LPA-style zone survey + quota placement for lowland lakes
        //      (candidate pools, exact counts, spacing, shortfall log).
        // 41 = SWAMP FLOOD: regions are mostly-water sheets with hummock
        //      islands (pond seeder deleted); shallow swamp excavation;
        //      flood guard exempts swamps; direct swamp classification.
        // 42 = headwater pools carve at EXACT polyline sources only (the
        //      level heuristic beaded pit-trenches along clamped runs).
        // 43 = REVERT river level rule to the original hOrig clamp (the
        //      monotone run-min trenched mountainsides); exact-source
        //      pools kept.
        // 44 = seeded basins keep their stamped level (walk trusts
        //      s_seedLevels); swamp flood: exact boundary containment +
        //      feathered murk shore; berms feathered 0.32 m/m with 3-cell
        //      plateau + toe dither.
        // 45 = SWAMP TERRAFORM: regions DIG DOWN to a border-derived level
        //      (distance-field dip, murk beds, sinking hummocks) instead of
        //      fitting the sheet to the terrain; seeded levels hold through
        //      river crossings; berms build for seeded river-fed swamps.
        // 46 = wet water level sampled at FULL 2m res (8m block-max surface
        //      caused stair plates on steep streams + sheets riding lake
        //      banks); mountain streams carve a real cross-section channel
        //      with cut banks; cliff-straddle veto on tarn/lake seed sites
        //      (the 20-50m dry quarry craters).
        // 47 = outlet brooks use the shared channel stamp (the center-cell
        //      dig beaded diagonal descents into pit chains); streams carve
        //      flush to pools (truncation lip removed); cold biomes feed no
        //      shader sand band.
        // 48 = swamp interior is one continuous field: island rims blend
        //      from murk to top through the noise gradient (no more 2m rim
        //      steps) + vanilla-style smooth mounds dotted in the sheet.
        // 49 = stream/brook carve is a SET DISTANCE from the PRISTINE
        //      surface: path heights captured before digging (the disk
        //      stamp's forward overlap fed dug beds back into the level
        //      ratchet = runaway 20m trenches), + hard per-cell removal
        //      caps (bed 4m, bank 2.5m).
        // 50 = crest lift blends the TWO nearest distinct range identities
        //      (nearest-segment-only stepped the 200-440m profile along
        //      equidistance lines = zone-spanning sheer mesa walls).
        // 51 = PIT VETO in the walk (floor >7m under the lowest rim = a
        //      noise funnel, no water — kills the 40m shaft ponds with berm
        //      lips) + stream stamp re-levels own-channel flanks to the
        //      minimum (flat cross-section, no U-trough sheet).
        // 52 = RIVER POOL pass: slope-break pools along carved runs get the
        //      pond treatment at their own level (flatten, bowl, local
        //      berm); river-fed global rim clamp DELETED (it drained upper
        //      gully sections to one bottom-of-chain cap).
        // 53 = pool pass skips seeded sheets + medians its level (max
        //      built water MOUNDS above swamp banks); streams/brooks walk
        //      and carve THROUGH river bench margins (confluences merge);
        //      swamps flood higher/tighter (p25+1.2, span 14, islands
        //      trimmed).
        // 54 = W5 CLIMATE BIOMES: temperature (latitude+lapse+maritime),
        //      wind-swept moisture with orographic rain shadow, HAND
        //      floodplains and valley enclosure resolve biomes (Whittaker)
        //      instead of radius rings + noise pockets.
        // 55 = climate uses the base noise family (Fbm/Hash01/Smooth made
        //      internal; duplicate local noise deleted) — same values,
        //      one implementation.
        // 56 = FLOW AUDIT: despike moved to GEN before biomes/climate/cache
        //      (one terrain truth, no per-load pass); river pools shaped
        //      AFTER outlet brooks; climate sampler half-cell alignment.
        // 57 = phantom fill sheets die by DEPTH in the walk (<0.45m film →
        //      dry; the deleted rim clamp was load-bearing for flats);
        //      climate drainage ignores the film (≥0.6m only); swamp
        //      terraform digs an ABSOLUTE basin (span capped 60m — whole
        //      region under one sheet, mounds rise); pool-pass flood guard.
        // 58 = mountain talus tightens to 45° above the snow line + ridged
        //      amps 24/8 (spike fins + crater Vs shed into landslide
        //      aprons); stream/brook levels use the pristine BOX MIN (river
        //      rule — mesh clips into banks, no floating shards); lava
        //      baked as an 8m field with real downhill runs to the ocean.
        // 59 = swamp rim apron FIXED-NARROW (full dig past 30m — ribbons no
        //      longer float a film over undug ground); TERMINAL POOLS: any
        //      stream/brook that dies on land ends in a stamped pond, never
        //      a hanging face.
        // 60 = BANK ENFORCEMENT fixpoint after all water passes: every wet
        //      cell's dry neighbours must stand above it — banks built
        //      where a berm fits, hanging edges retreat into the carve
        //      (channels bend instead). Hanging mesh is now impossible by
        //      construction; the audit verifies.
        // 61 = THE FILL TABLE IS NOT WATER: inland fill spills dried on
        //      every load before seeding — water is rivers + seeded bodies
        //      + sea, nothing else (kills the 2.19M-cell phantom lakes and
        //      the mass-retreat pockmark fields at the root).
        // 62 = bank enforcement requires a REAL LIP (shore ≥0.25 proud of
        //      the water, else the bank is built) — ice/water sheets always
        //      have a bank to clip into, never a flush open edge.
        // 63 = DELIBERATE DEPRESSIONS ONLY: the walk never blesses unseeded
        //      non-river basins (natural-dip water = logged LEAK, dropped);
        //      keep-roll profiles/districts/flood-guard/pit-veto deleted —
        //      every bowl is dug by a seeder, bank logic ours by
        //      construction.
        // 64 = SWAMP "regional sea level": biome = chunky lowland DISTRICTS
        //      (not HAND ribbons); sheet level = lowest border − 0.8 (BELOW
        //      every border cell → no floating edge at any biome boundary,
        //      vanilla-sea model at altitude); ponds level = ringMin − 0.35
        //      strictly below the lowest bank.
        // 65 = swamp sheet level = 15th-PERCENTILE border − 0.3 (strict-min
        //      sank below SEA on any lowland swamp with one low outlet →
        //      whole region skipped, stayed dry LAND: 11.4% biome / 30K wet);
        //      droplet erosion carve steep-damped (>31°) + HARD DEPTH CAP
        //      (≤6m below the 8m pre-erosion snapshot) — no bottomless holes;
        //      canyons bottom out at the cap.
        // 66 = river CONFLUENCE mouths curve into the trunk's flow (Y-joins,
        //      not grid-angle 90° meetings) + Chaikin 3→4 passes (rounds the
        //      staircase kinks that read as hard 90° turns).
        // 67 = swamp flood level from the INTERIOR 60th percentile (border-
        //      keyed level pinned perched swamps to their low drainage outlet
        //      → elevated interior never flooded); LOW swamps (level ≤SEA+2.5)
        //      skipped — the ocean floods them. Islands/mounds rise as land.
        // 68 = swamp flood level RE-CONTAINED to the RIM (15th-pct of the dry
        //      perimeter, not the interior): GEN 67's interior level stood the
        //      sheet ABOVE its own banks, so the bank fixpoint drained every
        //      perched swamp (221k above-bank violations, dry y:130). Rim level
        //      + a 10m feather + UNCONDITIONAL full-basin carve = the sheet
        //      covers the biome and survives ("extend the carve and flood it").
        // 69 = entries carry the hash of the ground they were generated from
        //      (a hoe/pickaxe edit after caching kept serving the old terrain),
        //      and caves/rivers/islands key on their per-Terrain-Mode gates.
        // 70 = ponds/lakes are CARVED BOWLS from a basin site (radial profile,
        //      spill-point level) instead of the basin's raw 20m cell footprint,
        //      and the hydrology flood treats the ocean as its outlet — both
        //      change the carved ground. The seven river/estuary shape knobs
        //      joined the key at the same time; they shaped the carve and were
        //      NOT in it, so changing a width or a meander served stale terrain.
        // 71 = vanilla-world hydrology water STORES into _genWater (the chunk
        //      sheet + swim router), the same path FullVoxel uses — cached
        //      chunks from 70 carry no water columns at all.
        // 72 = bowl geometry: radius capped at 80m, depth from the basin's OWN
        //      biome style (was hardcoded meadows) and a min-hold floor, plus
        //      full zone coverage of the bowl's bounding box — the carve moves
        //      wherever any of those changed.
        // 74 = the sheet's smooth corner grid reads the PROVIDER (a converted
        //      world got zeros and fell back to flat per-cell lids: terraced
        //      rivers), vanilla river corridors are outlets we do not re-carve,
        //      bounded surface descent, and the coarse-grid bank relief.
        // 75 = one-zone-at-a-time: closed columns store no water and carve no
        //      cave or river bed (0.2.442 cached water boxes over closed sea).
        // 76 = skyland bellies: superellipse profile (steep at the rim, round to
        //      pointed per island), the island hash doesn't cover the shape function.
        // 77 = skyland roundness and ribs come from the island hash (the float seed gave every
        //      skyland the same needle), skyland chunks wear their own biome, and a skyland holding a
        //      location is levelled around it.
        // 78 = skyland rims roll over into the underside (cutout, slope or cliff round the rim), big
        //      skylands carry caves, and new skylands (kind 5) stand on shapes, some in archipelagos.
        // 79 = skyland caves from a 20 m heart half-width (was 30), so more skylands carry them.
        // 80 = big skylands carry ground (hills, cliff terraces and tiers, ponds, rivers cut to the rim), great
        //      skylands of four to six zones, and every skyland's underside hangs from its plain dome.
        // 81 = the start cave: a chunk world's hill beside the StartTemple, with its cavern carved into the density.
        // 82 = skyland hollows dip only as deep as the keel below allows (a thin lobe kept a floating plate under a pond).
        // 83 = still water kept off the start location and the start cave (HydrologyGuards), and the start hill now
        //      stands in the hydrology's own ground sample.
        // 84 = water guards: channels, bowls, still water and springs keep off a world's frozen builds and placed
        //      locations (FiresWaterGuards), and a channel's carve closes into the ground where it meets them.
        // 85 = sinkholes are a true distance field (FAT 0.2.751): open mouth, irregular leaning walls, a floor with a mound.
        // 86 = stranded still bodies lowered to the river run they lie in at bake load (FAT 0.2.849b): the water columns change.
        private const int GEN_VERSION = 86;
        private const uint MAGIC = 0x56435A31;   // 'VCZ1'
        private const byte FMT = 2;
        private const int HeaderBytes = 10;      // magic(4) fmt(1) kind(1) genHash(4)

        private static string s_root;
        private static string s_key;
        private static bool s_ready;

        // For VoxelCacheSync. The key is the generation-settings hash the root is named after, so two machines
        // only ever exchange chunks that were generated the same way - a settings change gives a different key,
        // a different directory, and no chance of a stale chunk crossing the wire.
        internal static string Root => s_root;
        internal static string Key => s_key;
        internal static bool Ready => s_ready;

        internal static int HeaderSize => HeaderBytes;
        internal static uint HeaderMagic => MAGIC;
        internal static byte HeaderFormat => FMT;

        /// <summary>The genHash a .vcz on disk was written with, or false when the file is not a readable chunk.</summary>
        internal static bool TryReadHeaderHash(string path, out uint genHash)
        {
            genHash = 0;
            try
            {
                var header = new byte[HeaderBytes];
                using (var stream = File.OpenRead(path))
                    if (stream.Read(header, 0, HeaderBytes) != HeaderBytes) return false;
                if (BitConverter.ToUInt32(header, 0) != MAGIC || header[4] != FMT) return false;
                genHash = BitConverter.ToUInt32(header, 6);
                return true;
            }
            catch { return false; }
        }

        /// <summary>Where a chunk key lives on disk, for the sync to read and write whole files.</summary>
        internal static string FilePathFor(Vector3Int key) => PathFor(key);
        private static long s_bytes;             // running size estimate of the store
        private static readonly object s_gate = new object();
        // In-memory LRU stamps, so a cache hit costs no syscall beyond the read.
        private static readonly Dictionary<string, DateTime> s_access =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        // One reusable read buffer per worker thread — the payload is copied
        // straight into the chunk under its lock, so it never outlives the call.
        [ThreadStatic] private static byte[] t_readBuf;

        // Timing — the open question the plan says to answer before Stage 2:
        // is generation actually the expensive stage, or is it meshing / the
        // collider bake? These counters make that a measurement.
        private static long s_hits, s_misses, s_stores, s_evicted;
        private static double s_msLoad, s_msGen;
        private static float s_nextReport;

        private static bool Enabled =>
            !s_slowerThanGenerating && FiresVoxelConfig.ChunkCacheEnabled != null && FiresVoxelConfig.ChunkCacheEnabled.Value;

        // A cache is only worth reading while it beats generating. On a disk that makes each small read slow (the
        // 15:17 run measured 8.7 ms per load against 3.3 ms to generate, and the reads contended with other I/O),
        // it stands down for the session once enough of both have been timed.
        private const int StandDownMinLoads = 64;
        private const int StandDownMinGenerates = 16;
        private const double StandDownRatio = 1.1;
        private static volatile bool s_slowerThanGenerating;

        private static void CheckWorthReading()
        {
            if (s_slowerThanGenerating) return;
            long hits = Interlocked.Read(ref s_hits), misses = Interlocked.Read(ref s_misses);
            if (hits < StandDownMinLoads || misses < StandDownMinGenerates) return;
            double load, gen;
            lock (s_gate) { load = s_msLoad / hits; gen = s_msGen / misses; }
            if (load <= gen * StandDownRatio) return;
            s_slowerThanGenerating = true;
            FiresLogger.LogInfo($"[FiresVoxelCache] standing down for this session: a cached chunk takes {load:0.00} ms to "
                + $"load against {gen:0.00} ms to generate ({hits} loads, {misses} generates timed). Chunks now generate.");
        }

        private static long CapBytes =>
            (long)((FiresVoxelConfig.ChunkCacheSizeMB != null
                ? FiresVoxelConfig.ChunkCacheSizeMB.Value : 512) * 1024L * 1024L);

        // ── Key ──────────────────────────────────────────────────────────
        // Everything the density derives from. Miss one and the cache serves
        // terrain that no longer matches the settings that made it.
        private static string BuildKey()
        {
            unchecked
            {
                int h = 17;
                void Mix(float v) { h = h * 31 + v.GetHashCode(); }
                void MixI(int v) { h = h * 31 + v; }
                void MixB(bool v) { h = h * 31 + (v ? 1 : 0); }

                MixI(GEN_VERSION);
                MixI(WorldGenerator.instance != null ? WorldGenerator.instance.GetSeed() : 0);
                MixB(FiresWorldGenBase.Active);

                MixB(FiresVoxelConfig.GeneratesCaves);
                Mix(FiresVoxelConfig.CaveMinY?.Value ?? 0f);
                Mix(FiresVoxelConfig.CaveMaxDepth?.Value ?? 0f);
                Mix(FiresVoxelConfig.CaveFrequency?.Value ?? 0f);
                Mix(FiresVoxelConfig.CaveFrequencyY?.Value ?? 0f);
                Mix(FiresVoxelConfig.CaveWidth?.Value ?? 0f);
                Mix(FiresVoxelConfig.CavernRarity?.Value ?? 0f);
                Mix(FiresVoxelConfig.CavernBoost?.Value ?? 0f);
                Mix(FiresVoxelConfig.EntranceRarity?.Value ?? 0f);
                Mix(FiresVoxelConfig.SinkholeRarity?.Value ?? 0f);
                Mix(FiresVoxelConfig.SinkholeSpacing?.Value ?? 0f);
                Mix(FiresVoxelConfig.SinkholeRadius?.Value ?? 0f);
                Mix(FiresVoxelConfig.SinkholeDepth?.Value ?? 0f);
                Mix(FiresVoxelConfig.WaterMinChannelDepth?.Value ?? 0f);
                Mix(FiresVoxelConfig.WaterBankFreeboard?.Value ?? 0f);
                MixB(FiresVoxelConfig.BuildsSkyIslands);
                // NOT the island set, and NOT the rarity that once produced it. A
                // seeded world's islands are frozen in its registry, so rarity no
                // longer alters density. The set itself is guarded per chunk instead
                // (FiresSkyIslands.ColumnSignature folded into the generation hash of
                // chunks inside an island's band): this key is minted ONCE and
                // PruneOldKeys deletes every other one, so putting the set here would
                // wipe the world's whole cache every time an island is edited.
                Mix(FiresVoxelConfig.RiverThreshold?.Value ?? 0f);
                Mix(FiresVoxelConfig.RiverMaxDepth?.Value ?? 0f);
                Mix(FiresVoxelConfig.RiverWidthMin?.Value ?? 0f);
                Mix(FiresVoxelConfig.RiverWidthMax?.Value ?? 0f);
                Mix(FiresVoxelConfig.RiverFreeboard?.Value ?? 0f);
                Mix(FiresVoxelConfig.BankRelief?.Value ?? 0f);
                Mix(FiresVoxelConfig.RiverMaxNewCut?.Value ?? 0f);
                Mix(FiresVoxelConfig.RiverMeander?.Value ?? 0f);
                Mix(FiresVoxelConfig.EstuaryLength?.Value ?? 0f);
                Mix(FiresVoxelConfig.EstuaryFlare?.Value ?? 0f);
                MixB(FiresVoxelConfig.CarvesRiverBeds);
                MixI(FiresWaterGuards.Hash);
                // A faithful bake's chunks store water only with Faithful Bake Water on. Mixed in only then, so every other
                // world (and a faithful one with it off) keeps its key and its cache.
                if (FiresWorldGenBase.LoadedFaithful && FiresWorldGenBase.FaithfulBakeWaterOn) MixI(0x46425731);   // 'FBW1'

                return ((uint)h).ToString("x8");
            }
        }

        // Drop the latched key so the next EnsureReady mints a fresh one. The key is
        // computed ONCE, so without this a second world loaded in the same process
        // kept the first world's directory and key (reads stay correct — every
        // entry carries its column hash — but writes land in the wrong world's
        // store), and a sky-island set that changes mid-session would keep being
        // cached under the old set's identity.
        internal static void Reset()
        {
            lock (s_gate)
            {
                if (!s_ready) return;
                s_ready = false;
                s_root = null;
                s_key = null;
                s_generation++;
                s_measured = false;
            }
            lock (s_access) s_access.Clear();
        }

        internal static void EnsureReady()
        {
            if (s_ready || !Enabled) return;
            // A client holding sky islands off does not yet know which island set
            // this world has, and the key below is minted ONCE and latched — so a
            // key minted now would describe the wrong set for the whole session,
            // and PruneOldKeys would then delete the world's real island-bearing
            // cache as a stale key. Stay unready until the set is known; TryLoad and
            // Store no-op meanwhile, so chunks in that window are simply uncached.
            if (FiresSkyIslands.AwaitingServer) return;
            lock (s_gate)
            {
                if (s_ready) return;
                try
                {
                    string worldName = ZNet.instance != null && ZNet.World != null
                        ? ZNet.World.m_name : "unknown";
                    string baseDir = Path.Combine(
                        Utils.GetSaveDataPath(FileHelpers.FileSource.Local), "FiresVoxelCache");
                    s_key = BuildKey();
                    s_root = Path.Combine(Path.Combine(baseDir, worldName), s_key);
                    Directory.CreateDirectory(s_root);
                    // Measuring the store walks every cached file and the prune deletes whole directories: on the
                    // dedi that was 565 ms of a 624 ms main-thread stall as a client joined (R29a, [lead] 10-01). Both
                    // run on the thread pool; the cache is usable at once, and the LRU sweep waits for the measure.
                    s_bytes = 0;
                    s_measured = false;
                    int generation = ++s_generation;
                    string root = s_root, key = s_key, worldDir = Path.Combine(baseDir, worldName);
                    s_ready = true;
                    FiresLogger.LogInfo($"[FiresVoxelCache] ready — key={s_key} world='{worldName}' cap={CapBytes / (1024f * 1024f):0}MB; "
                        + "measuring the store in the background.");
                    ThreadPool.QueueUserWorkItem(_ => MeasureInBackground(generation, root, worldDir, key));
                }
                catch (Exception ex)
                {
                    FiresLogger.LogWarning($"[FiresVoxelCache] disabled — init failed: {ex.Message}");
                    s_ready = false; s_root = null;
                }
            }
        }

        // Bumped by every EnsureReady and Reset, so a measure that finishes after its world was dropped adds nothing.
        private static int s_generation;
        private static volatile bool s_measured;

        // Stores made while this runs may be counted twice (once by Store, once here); the estimate errs high, and the
        // first sweep sets the exact size.
        private static void MeasureInBackground(int generation, string root, string worldDir, string key)
        {
            PruneOldKeys(worldDir, key);
            long n = MeasureStore(root);
            lock (s_gate)
            {
                if (generation != s_generation) return;
                s_bytes += n;
                s_measured = true;
            }
            FiresLogger.LogInfo($"[FiresVoxelCache] store measured: existing={n / (1024f * 1024f):0.0}MB.");
        }

        private static long MeasureStore(string root)
        {
            long n = 0;
            try { foreach (var f in Directory.GetFiles(root, "*.vcz", SearchOption.AllDirectories)) n += new FileInfo(f).Length; }
            catch { }
            return n;
        }

        // A settings change mints a new key directory; the old one is dead
        // weight. BetterContinents never does this and its cache grows without
        // bound — orphaned entries from every settings revision, forever.
        private static void PruneOldKeys(string worldDir, string liveKey)
        {
            try
            {
                foreach (var d in Directory.GetDirectories(worldDir))
                {
                    if (string.Equals(Path.GetFileName(d), liveKey, StringComparison.OrdinalIgnoreCase)) continue;
                    Directory.Delete(d, true);
                    FiresLogger.LogInfo($"[FiresVoxelCache] pruned stale key dir '{Path.GetFileName(d)}' (generation settings changed).");
                }
            }
            catch (Exception ex) { FiresLogger.LogWarning($"[FiresVoxelCache] prune failed: {ex.Message}"); }
        }

        // Sharded so no single directory holds tens of thousands of entries.
        private static string PathFor(Vector3Int k)
        {
            string shard = $"{k.x >> 4}_{k.z >> 4}";
            return Path.Combine(Path.Combine(s_root, shard), $"{k.x}_{k.y}_{k.z}.vcz");
        }

        // ── Read ─────────────────────────────────────────────────────────
        // A stored ground hash that differs from the chunk's current one is a
        // plain miss: the ground changed under the entry (terrain edit), and
        // the fresh generate overwrites it.
        internal static bool TryLoad(Vector3Int key, uint genHash, out ChunkKind kind, out byte[] payload)
        {
            kind = ChunkKind.Full; payload = null;
            if (!Enabled || !s_ready) return false;
            string path;
            try { path = PathFor(key); } catch { return false; }
            if (!File.Exists(path)) { Interlocked.Increment(ref s_misses); return false; }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                byte[] raw = File.ReadAllBytes(path);
                if (raw.Length < HeaderBytes) throw new InvalidDataException("truncated header");
                uint magic = BitConverter.ToUInt32(raw, 0);
                if (magic != MAGIC) throw new InvalidDataException("bad magic");
                if (raw[4] != FMT) throw new InvalidDataException("format version");
                if (BitConverter.ToUInt32(raw, 6) != genHash) { Interlocked.Increment(ref s_misses); return false; }
                kind = (ChunkKind)raw[5];

                if (kind == ChunkKind.Full)
                {
                    // RAW, NOT COMPRESSED, and into a POOLED buffer.
                    //
                    // v1 compressed and let Utils.Decompress allocate a fresh
                    // 83KB array per hit. At 1585 hits that is ~130MB of
                    // garbage, and it showed: avg load climbed 1.03 -> 9.32ms as
                    // hits accumulated (past the 6.24ms it costs to just
                    // generate), alongside starved=15816 and 22fps. The decompress
                    // was buying disk space we were never short of — the store
                    // sat at 17MB against a 512MB cap — and paying for it in GC
                    // on the exact threads the pipeline depends on.
                    int want = VoxelChunk.CacheBlobBytes;
                    if (raw.Length != HeaderBytes + want)
                        throw new InvalidDataException($"body {raw.Length - HeaderBytes} != {want}");
                    var buf = t_readBuf ?? (t_readBuf = new byte[want]);
                    Buffer.BlockCopy(raw, HeaderBytes, buf, 0, want);
                    payload = buf;
                }
                // Stop the clock BEFORE the LRU bookkeeping. The first version
                // called File.SetLastAccessTimeUtc here, inside the timed
                // region — a filesystem METADATA WRITE on every hit, measured
                // as if it were read cost. It reported 12.53ms/load against
                // 5.31ms to generate, and the 0.52 -> 2.96 -> 12.53 climb as the
                // store grew is what mtime writes against a growing tree look
                // like, not what ReadAllBytes looks like. The benchmark was
                // measuring the instrumentation.
                lock (s_gate) s_msLoad += sw.Elapsed.TotalMilliseconds;
                Interlocked.Increment(ref s_hits);
                CheckWorthReading();
                // LRU in MEMORY: no syscall on the hot path. The sweep reads
                // this map and only falls back to file times for entries it has
                // never seen this session.
                lock (s_access) s_access[path] = DateTime.UtcNow;
                return true;
            }
            catch (Exception ex)
            {
                // Corrupt or stale-format entry: drop it and generate. Never
                // throw out of a worker thread over a cache miss.
                FiresLogger.LogWarning($"[FiresVoxelCache] discarding {Path.GetFileName(path)}: {ex.Message}");
                try { File.Delete(path); } catch { }
                Interlocked.Increment(ref s_misses);
                payload = null;
                return false;
            }
        }

        // ── Write ────────────────────────────────────────────────────────
        internal static void Store(Vector3Int key, uint genHash, ChunkKind kind, byte[] payload, double genMs)
        {
            lock (s_gate) s_msGen += genMs;
            if (!Enabled || !s_ready) return;
            try
            {
                string path = PathFor(key);
                Directory.CreateDirectory(Path.GetDirectoryName(path));

                // Stored RAW — see the read path. Compression cost more in GC on
                // the pipeline's own threads than the disk it saved.
                byte[] body = kind == ChunkKind.Full && payload != null ? payload : new byte[0];
                var outBuf = new byte[HeaderBytes + body.Length];
                Buffer.BlockCopy(BitConverter.GetBytes(MAGIC), 0, outBuf, 0, 4);
                outBuf[4] = FMT;
                outBuf[5] = (byte)kind;
                Buffer.BlockCopy(BitConverter.GetBytes(genHash), 0, outBuf, 6, 4);
                if (body.Length > 0) Buffer.BlockCopy(body, 0, outBuf, HeaderBytes, body.Length);

                // Atomic: a torn write must never be readable as valid.
                string tmp = path + ".tmp";
                File.WriteAllBytes(tmp, outBuf);
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);

                Interlocked.Increment(ref s_stores);
                lock (s_gate) s_bytes += outBuf.Length;
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[FiresVoxelCache] store failed for {key}: {ex.Message}");
            }
        }

        // ── LRU eviction ─────────────────────────────────────────────────
        // Off the hot path entirely: called from the streamer tick, and only
        // does work when the store is actually over its cap.
        private static float s_nextSweep;
        internal static void Tick()
        {
            if (!Enabled || !s_ready) return;

            if (Time.time >= s_nextReport)
            {
                s_nextReport = Time.time + 60f;
                long h = Interlocked.Read(ref s_hits), m = Interlocked.Read(ref s_misses);
                if (h + m > 0)
                {
                    double load, gen;
                    lock (s_gate) { load = s_msLoad; gen = s_msGen; }
                    FiresLogger.LogVerbose($"[FiresVoxelCache] {h} hit / {m} miss({(h * 100f / Mathf.Max(1, h + m)):0}%) " +
                              $"| stored {Interlocked.Read(ref s_stores)} evicted {Interlocked.Read(ref s_evicted)} " +
                              $"| {s_bytes / (1024f * 1024f):0.0}MB of {CapBytes / (1024f * 1024f):0}MB " +
                              $"| avg load {(h > 0 ? load / h : 0):0.00}ms vs generate {(m > 0 ? gen / m : 0):0.00}ms");
                }
            }

            if (Time.time < s_nextSweep) return;
            s_nextSweep = Time.time + 30f;
            if (!s_measured || s_bytes <= CapBytes) return;
            ThreadPool.QueueUserWorkItem(_ => Sweep());
        }

        private static void Sweep()
        {
            try
            {
                var files = new List<FileInfo>();
                foreach (var f in Directory.GetFiles(s_root, "*.vcz", SearchOption.AllDirectories))
                    files.Add(new FileInfo(f));
                // Sort by our in-memory stamp where we have one (hits this
                // session), falling back to the file's own time for entries
                // written by a previous run. Keeps the hot path syscall-free.
                DateTime Stamp(FileInfo f)
                {
                    lock (s_access)
                        if (s_access.TryGetValue(f.FullName, out var t)) return t;
                    return f.LastWriteTimeUtc;
                }
                files.Sort((a, b) => Stamp(a).CompareTo(Stamp(b)));

                long target = (long)(CapBytes * 0.85f);   // trim below the cap, not to it
                long size = 0;
                foreach (var f in files) size += f.Length;

                int removed = 0;
                foreach (var f in files)
                {
                    if (size <= target) break;
                    long len = f.Length;
                    try
                    {
                        f.Delete(); size -= len; removed++;
                        Interlocked.Increment(ref s_evicted);
                        lock (s_access) s_access.Remove(f.FullName);
                    }
                    catch { }
                }
                lock (s_gate) s_bytes = size;
                if (removed > 0)
                    FiresLogger.LogInfo($"[FiresVoxelCache] LRU evicted {removed} entr(ies) → {size / (1024f * 1024f):0.0}MB.");
            }
            catch (Exception ex) { FiresLogger.LogWarning($"[FiresVoxelCache] sweep failed: {ex.Message}"); }
        }
    }
}
