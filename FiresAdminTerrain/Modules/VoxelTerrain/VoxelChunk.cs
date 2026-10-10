using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // Voxel P0 - one 32m^3 chunk of signed-distance density.
    //
    // Convention: density > 0 = SOLID, < 0 = air, surface at 0. Quantized to
    // sbyte at QUANT units/m (+-6.35m band) - marching interpolates crossings,
    // so the surface is smooth despite 1m cells.
    //
    // Sample grid: [-1..32] per axis (34^3) - a 1-sample apron duplicated from
    // the neighbourhood, the TerrainTile border-duplication pattern in 3D.
    // Each chunk can mesh its border cells (index -1) itself with vertices
    // that coincide bit-exactly with the neighbour's (same densities, same
    // math) -> no cross-chunk indexing, no cracks.
    //
    // Threading: brush writes + mesher snapshot both go through _lock. The
    // mesher copies the array under the lock (39KB - microseconds) and works
    // on the snapshot, so the worker never sees a torn write.
    // Brush modes. Restore clears the brush edits under the shape so the ground
    // the chunk was generated or seeded with shows again (the "delete voxel
    // edits" tool); a fully-restored brush-only chunk is dropped by VoxelWorld
    // so nothing renders.
    public enum VoxelBrushMode { Carve, Fill, Restore }

    // Brush shapes: Cylinder = the flat-ruler tools (radius x height disc),
    // Sphere/Cube = the 3D gizmo tools (radius = half-extent).
    public enum VoxelBrushShape { Sphere, Cylinder, Cube }

    // A Full chunk holds the density arrays + (once meshed) a mesh/collider.
    // A UNIFORM chunk is entirely solid (below all cave reach) or entirely air
    // (above the surface) - the plan's "one RLE run": no arrays, no GameObject,
    // no mesh, no collider. It stays registered so the Y-range union + border
    // ownership hold, and serves its constant density/seed/biome to neighbours.
    public enum ChunkKind : byte { Full, UniformSolid, UniformAir }

    public sealed class VoxelChunk
    {
        public const int CELLS = 32;              // cells per axis (1m each)
        public const int SAMPLES = CELLS + 2;     // sample grid [-1..32] = 34
        public const float QUANT = 20f;           // sbyte units per metre (0.05m)

        // How far the WATER SHEET (and swim) sit below the reconciled surface
        // the bed carve is cut for — visible bank relief, tuned on screen.
        private const float WATER_SEAT_DOWN = 0.4f;
        private const float SpreadBankSlack = 0.5f;   // 0.2.846: a spread column's generated surface may sit this far under the level (seat-down 0.4)

        // Saturated density a uniform chunk serves at its border ring (>0 solid).
        private const sbyte UniformSolidDensity = 127;
        private const sbyte UniformAirDensity = -127;

        public readonly Vector3Int Key;           // chunk coords (world / 32)
        public readonly Vector3 WorldOrigin;      // sample (0,0,0) world position

        public ChunkKind Kind { get; private set; }

        // Governor accounting: only Full chunks carry the ~83KB arrays (and a
        // mesh/collider once built); uniform chunks are effectively free.
        public bool HoldsArrays => Kind == ChunkKind.Full;

        // _seed = the ground-derived baseline; _density = seed + edits. The
        // mesher only emits cells whose corners DIFFER from seed, so unedited
        // ground never grows a duplicate skin over the vanilla terrain (no
        // z-fighting), and restoring everything makes the chunk disappear.
        // NULL on uniform chunks - every read path is Kind-gated first.
        private sbyte[] _density;
        private sbyte[] _seed;
        // Per-COLUMN biome blend color (34x34, sampled from WorldGenerator at
        // seed time) so voxel surfaces auto-match the surrounding biome -
        // plains grass in Plains, meadows in Meadows - instead of a fixed
        // Meadows mask. Same encoding the tiles use (Heightmap.GetBiomeColor).
        private Color32[] _biome;
        // Per-column RECONCILED water level (0 = dry) — carve + fill in one
        // pass. Written during generate under _lock from the same surface the
        // bed carve was cut for; the view emits the water sheet from it, and
        // it rides the chunk cache. NULL when no column in this chunk is wet.
        private float[] _genWater;
        private readonly object _lock = new object();

        // Bumped on every mutation; stale mesh/bake results are dropped by
        // comparing against the version they were built from.
        public int Version;

        // Version of the data the LIVE render mesh was built from (set by the
        // pipeline on apply). Version != AppliedVersion = a stale mesh on
        // converged data - the seam class the sweep's data-diff test could
        // never see.
        public int AppliedVersion = -1;

        // Set while the chunk sits in the remesh queue (dedup).
        public bool Queued;

        // DROPPED — the zone unwrapped (player flew past) while this chunk was
        // still queued/generating. The worker skips it instantly on dequeue
        // instead of running the expensive generate+mesh for terrain nobody is
        // near, so the zones the player IS near get worker headway. A rewrap
        // creates a FRESH chunk object (Dropped=false); this stale one dies.
        public volatile bool Dropped;

        // Worker-throw retry budget for pending generates (reset on arm and
        // on success) - bounds the retry loop the pipeline catch runs.
        public int GenFailCount;

        // W1 - streamed (zone-wrap) chunks mesh the WHOLE surface, not just
        // edited cells: in a wrapped zone the vanilla heightmap render is
        // hidden, so the voxel mesh IS the ground. Brush-only chunks keep the
        // edited-cell gate (they overlay visible vanilla terrain).
        public bool MeshAll;

        // Crossing Y-band (local voxel Y): the mesher scans only this range -
        // everything above is air, below is solid. hi < lo => uniform chunk.
        // Defaults mesh the full column until RunPendingGenerate narrows them.
        public int MeshYLo = -1, MeshYHi = CELLS + 1;

        // Faces whose in-plane quads NO neighbour will emit (absent, pending,
        // or gated): each border quad has exactly one owner - the chunk on the
        // plane's positive side - so a MeshAll chunk's skin stops half a cell
        // short of an uncovered face (the dark trench at the wrap frontier).
        // Recomputed at every enqueue; the mesher seals flagged faces with
        // shingle lips. Bits: 1=+X, 2=-X, 4=+Z, 8=-Z.
        public byte FrontierMask;

        // +faces whose OWNER CHUNK doesn't exist (1=+X, 4=+Z): this chunk
        // meshes the plane itself (orphan-face emission) until the owner
        // appears - recomputed at every enqueue, compared by the heal.
        public byte OrphanMask;

        // W1.2 - streamed chunks keep their RENDERER hidden until the zone's
        // swap moment (colliders bake first, then voxel-on/vanilla-off flips in
        // ONE frame). Kills the z-fight window where both terrains rendered
        // coplanar during wrap-in. Copied onto the view at creation.
        public bool SuppressRenderUntilSwap;

        // P1 — set while this chunk holds brush edits (bounds or mopped water);
        // they persist via VoxelChunkAnchor ZDOs so brushwork survives relog
        // and syncs to other clients.
        public bool HasBrushEdits;

        // Brush edits as per-sample clamp bounds over the ground the chunk was
        // generated or seeded with: density = clamp(base, lo, hi). A carve lowers
        // both bounds and a fill raises them, which composes any carve/fill
        // sequence exactly, so regeneration (a hoe edit under a carve) re-applies
        // the edits to the new ground. Null until the first brush edit.
        private const sbyte EditLoNone = -127;
        private const sbyte EditHiNone = 127;
        private sbyte[] _editLo;
        private sbyte[] _editHi;
        private sbyte[] _editBase;

        // W1 - deferred generation payload (filled by the zone streamer on the
        // main thread, executed on the WORKER before the first mesh): ground +
        // biome per column, then the deterministic cave-noise carve.
        private float[] _genGround;    // SAMPLESxSAMPLES world ground heights
        private Color32[] _genBiome;   // SAMPLESxSAMPLES biome colors
        private bool _genCaves;
        // Ground carrying a Vedr quake scar or crater heals back, so it is neither
        // read from nor written to the cache: the chunk's one cache file keeps
        // the lasting ground.
        private bool _genCacheable;
        // Vedr's open quake fissures over this chunk's column (FatCracks), the
        // same array the streamer folded into LastGenHash; null when none.
        private float[] _genCracks;
        public bool PendingGenerate { get; private set; }

        // Change-gate: hash of the last executed generate inputs. A zone
        // rewrap whose gather hashes identically is a NO-OP - skipping it
        // avoids remesh + collider swaps under the player (rubber-banding).
        public uint LastGenHash;

        public void PrepareGenerate(float[] ground, Color32[] biome, bool caves, float[] cracks)
        {
            EnterTimed(nameof(PrepareGenerate));
            try
            {
                _genGround = ground;
                _genBiome = biome;
                _genCaves = caves;
                _genCracks = cracks;
                _genCacheable = !FatSeasons.IsScarred(WorldOrigin) && cracks == null;
                _genOverrides = null;
                GenFailCount = 0;
                PendingGenerate = true;
            }
            finally { System.Threading.Monitor.Exit(_lock); }
        }

        // Diagnostic (R8a, 2026-09-28: a 1.8 s dedi stall in BuildZoneChunks, 98% of it blocked). The streamer re-arms chunks
        // on the main thread through here and SetBorderOverrides, while a worker holds the same lock for a whole generate or
        // mesh, so a wait at or over LockWaitReportMs is logged with the chunk: that tells a lock wait from page-ins.
        private const long LockWaitReportMs = 50;

        private void EnterTimed(string caller)
        {
            if (System.Threading.Monitor.TryEnter(_lock)) return;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            System.Threading.Monitor.Enter(_lock);
            long waited = clock.ElapsedMilliseconds;
            if (waited < LockWaitReportMs) return;
            try { FiresLogger.LogWarning($"[FiresVoxel] {caller} waited {waited} ms for chunk {Key}'s lock, held by a worker."); }
            catch (System.Exception) { }
        }

        // -- Canonical border ownership (the REAL seam fix) -------------------
        // Every world sample is owned by exactly one chunk: the one holding it
        // in its [0..31] interior. The -1/32 border rings of every other chunk
        // MIRROR the owner - density AND the per-column biome mask (vertex
        // colors drive splat blending; a border biome tint diff paints a line
        // even on perfect geometry). Duplicated boundary cells then mesh
        // identically on both sides regardless of frozen edits, regen timing
        // or legacy saves.
        public sealed class BorderOverrideSet
        {
            public List<KeyValuePair<int, sbyte>> Dens;
            // Owner's SEED for the same samples, index-parallel with Dens.
            // Mirroring density alone used to write seed := density, erasing
            // the ring's edited-status on the receiving side - the two chunks
            // then disagreed at the mesher's edited-cell gate and the quad
            // owner silently dropped rows exactly on the 32m plane.
            public List<sbyte> Seed;
            public List<KeyValuePair<int, Color32>> Biome;
        }

        private BorderOverrideSet _genOverrides;

        public void SetBorderOverrides(BorderOverrideSet overrides)
        {
            EnterTimed(nameof(SetBorderOverrides));
            try
            {
                if (PendingGenerate) _genOverrides = overrides;
            }
            finally { System.Threading.Monitor.Exit(_lock); }
        }

        // Main-thread only (reads neighbour arrays without their locks - all
        // mutation paths for generated chunks are main-thread serialized or
        // pending-gated). Returns null when no generated neighbour is loaded.
        // Allocating variant - used by the PERSIST callers (SetBorderOverrides
        // -> _genOverrides holds the set across frames, so it must own its lists).
        public static BorderOverrideSet CaptureBorderOverrides(Vector3Int key)
        {
            var set = new BorderOverrideSet
            {
                Dens = new List<KeyValuePair<int, sbyte>>(1024),
                Seed = new List<sbyte>(1024),
                Biome = new List<KeyValuePair<int, Color32>>(256),
            };
            return FillBorderOverrides(key, set) ? set : null;
        }

        // Reused per-tick heal buffer (main-thread; MirrorBordersNow consumes it
        // synchronously and never stores it). The rolling auto-heal ran the
        // allocating capture every streamer tick - three fresh Lists per healed
        // chunk - and mono grows its heap under that churn and never hands it
        // back, inflating to a full-world OutOfMemory over a play session (the
        // border-mirror OOM stack). Refilling one buffer removes that churn.
        [System.ThreadStatic] private static BorderOverrideSet t_healBuf;

        // The 26-neighbour owner cache, reused per thread. FillBorderOverrides
        // runs on every mirror (heal drain, EnqueueRemesh, PokeStaleNeighbours)
        // - allocating a fresh VoxelChunk[27]+bool[27] each call was the second
        // border-mirror heap-churn source the t_healBuf List pooling missed.
        [System.ThreadStatic] private static VoxelChunk[] t_ownerCache;
        [System.ThreadStatic] private static bool[] t_ownerSeen;

        // Core capture: fill `set` (already allocated, caller-cleared) from the
        // loaded generated neighbours. Returns true if any neighbour contributed.
        private static bool FillBorderOverrides(Vector3Int key, BorderOverrideSet set)
        {
            bool any = false;
            var cache = t_ownerCache ?? (t_ownerCache = new VoxelChunk[27]);
            var seen = t_ownerSeen ?? (t_ownerSeen = new bool[27]);
            System.Array.Clear(cache, 0, 27);
            System.Array.Clear(seen, 0, 27);
            for (int z = -1; z <= CELLS; z++)
            {
                bool zEdge = z == -1 || z == CELLS;
                for (int y = -1; y <= CELLS; y++)
                {
                    bool yEdge = y == -1 || y == CELLS;
                    for (int x = -1; x <= CELLS; x++)
                    {
                        if (!zEdge && !yEdge && x != -1 && x != CELLS) continue;   // interior - we own it
                        int ox = x == -1 ? -1 : (x == CELLS ? 1 : 0);
                        int oy = y == -1 ? -1 : (y == CELLS ? 1 : 0);
                        int oz = z == -1 ? -1 : (z == CELLS ? 1 : 0);
                        int slot = (ox + 1) + 3 * (oy + 1) + 9 * (oz + 1);
                        if (!seen[slot])
                        {
                            seen[slot] = true;
                            VoxelWorld.TryGetChunk(new Vector3Int(key.x + ox, key.y + oy, key.z + oz), out cache[slot]);
                            if (cache[slot] != null && cache[slot].PendingGenerate) cache[slot] = null;
                        }
                        var owner = cache[slot];
                        if (owner == null) continue;
                        any = true;
                        sbyte od, os;
                        if (owner.Kind == ChunkKind.Full)
                        {
                            int ownerIdx = Idx(x - ox * CELLS, y - oy * CELLS, z - oz * CELLS);
                            od = owner._density[ownerIdx];
                            os = owner._seed[ownerIdx];
                        }
                        else od = os = owner.Kind == ChunkKind.UniformSolid ? UniformSolidDensity : UniformAirDensity;
                        set.Dens.Add(new KeyValuePair<int, sbyte>(Idx(x, y, z), od));
                        set.Seed.Add(os);
                    }
                }
            }

            // Biome is per-COLUMN (x,z): mirror ring columns from the
            // horizontal neighbour that owns them (same Y layer).
            for (int z = -1; z <= CELLS; z++)
            {
                bool zEdge = z == -1 || z == CELLS;
                for (int x = -1; x <= CELLS; x++)
                {
                    if (!zEdge && x != -1 && x != CELLS) continue;
                    int ox = x == -1 ? -1 : (x == CELLS ? 1 : 0);
                    int oz = z == -1 ? -1 : (z == CELLS ? 1 : 0);
                    if (!VoxelWorld.TryGetChunk(new Vector3Int(key.x + ox, key.y, key.z + oz), out var colOwner)
                        || colOwner == null || colOwner.PendingGenerate) continue;
                    any = true;
                    int colIdx = (x + 1) + SAMPLES * (z + 1);
                    int ownerLocalX = x - ox * CELLS, ownerLocalZ = z - oz * CELLS;
                    Color32 ob;
                    if (colOwner.Kind == ChunkKind.Full)
                        ob = colOwner._biome[(ownerLocalX + 1) + SAMPLES * (ownerLocalZ + 1)];
                    else
                        ob = BiomeCornerBlend.Sample(colOwner.WorldOrigin.x + ownerLocalX, colOwner.WorldOrigin.z + ownerLocalZ);
                    set.Biome.Add(new KeyValuePair<int, Color32>(colIdx, ob));
                }
            }
            return any;
        }

        // Diagnostic probe: density at a world position (-128 = outside).
        public sbyte SampleDensityWorld(Vector3 w)
        {
            int lx = Mathf.FloorToInt(w.x) - Key.x * CELLS;
            int ly = Mathf.FloorToInt(w.y) - Key.y * CELLS;
            int lz = Mathf.FloorToInt(w.z) - Key.z * CELLS;
            if (lx < -1 || lx > CELLS || ly < -1 || ly > CELLS || lz < -1 || lz > CELLS)
                return sbyte.MinValue;
            if (Kind != ChunkKind.Full)
                return Kind == ChunkKind.UniformSolid ? UniformSolidDensity : UniformAirDensity;
            return _density[Idx(lx, ly, lz)];
        }

        // Re-mirror this chunk's border rings from loaded owners RIGHT NOW.
        // apply=false audits only. Returns the number of mismatched samples
        // (density + biome columns). Uniform chunks have no ring to receive -
        // they SERVE their constant to neighbours instead (FillBorderOverrides).
        public int MirrorBordersNow(bool apply)
        {
            if (Kind != ChunkKind.Full) return 0;
            // Reuse one buffer per thread instead of allocating three Lists each
            // heal tick - this is consumed synchronously below and never stored.
            var buf = t_healBuf;
            if (buf == null)
                buf = t_healBuf = new BorderOverrideSet
                {
                    Dens = new List<KeyValuePair<int, sbyte>>(1024),
                    Seed = new List<sbyte>(1024),
                    Biome = new List<KeyValuePair<int, Color32>>(256),
                };
            else { buf.Dens.Clear(); buf.Seed.Clear(); buf.Biome.Clear(); }
            var overrides = FillBorderOverrides(Key, buf) ? buf : null;
            if (overrides == null) return 0;
            int changed = 0;
            lock (_lock)
            {
                for (int i = 0; i < overrides.Dens.Count; i++)
                {
                    var kv = overrides.Dens[i];
                    sbyte sv = overrides.Seed[i];
                    // Seed diffs count too: they flip the mesher's edited-cell
                    // verdict on duplicated border cells even when density
                    // already matches.
                    if (_density[kv.Key] == kv.Value && _seed[kv.Key] == sv) continue;
                    changed++;
                    if (apply) AdoptRingSampleLocked(kv.Key, kv.Value, sv);
                }
                foreach (var kv in overrides.Biome)
                {
                    var cur = _biome[kv.Key];
                    var nv = kv.Value;
                    if (cur.r == nv.r && cur.g == nv.g && cur.b == nv.b && cur.a == nv.a) continue;
                    changed++;
                    if (apply) _biome[kv.Key] = nv;
                }
                if (apply && changed > 0) Version++;
            }
            return changed;
        }

        // Worker-safe (pure math + our noise). Also callable on the main
        // thread (brush guard) - whoever gets there first runs it.
        // Sky-island chunks this session (Fire 10-05, "nothing regenerates on load"): restored from the chunk cache (the island's
        // shape was generated once and saved) vs generated from its shape here. FiresSkylandHold logs them at Debug.
        internal static int IslandChunksRestored, IslandChunksGenerated;

        public void RunPendingGenerate()
        {
            // CACHE READ - outside the lock's work, but the restore itself
            // re-takes it. A hit skips the whole generate below: noise, cave
            // SDF and carves over 34^3 samples. Any failure just falls through
            // and generates, so a bad entry can never block terrain.
            if (PendingGenerate && _genCacheable && VoxelChunkCache.TryLoad(Key, LastGenHash, out var cachedKind, out var cachedBlob)
                && CacheRestore(cachedKind, cachedBlob))
            {
                if (FiresSkyIslands.ChunkOverlaps(WorldOrigin)) System.Threading.Interlocked.Increment(ref IslandChunksRestored);
                return;
            }

            var swGen = System.Diagnostics.Stopwatch.StartNew();
            lock (_lock)
            {
                if (!PendingGenerate) return;
                // W2's voxel-side river carve exists for VANILLA-generated
                // worlds (wrap-and-subtract). On FiresWorldGen worlds the
                // drainage is carved into the BASE heightfield itself - the
                // vanilla surface already has the channels. Carving again in
                // the voxel layer double-cuts the beds AND makes the voxel
                // surface diverge from the vanilla surface, which shows as
                // steps/tears at every streaming frontier along rivers.
                bool rivers = FiresVoxelConfig.CarvesRiverBeds && VoxelHydrology.Ready
                    && !FiresWorldGenBase.Active;
                // SKY ISLANDS — one overlap test per chunk; only overlapping
                // chunks pay the per-voxel SDF union below.
                bool skyIsle = FiresSkyIslands.ChunkOverlaps(WorldOrigin);
                if (skyIsle) System.Threading.Interlocked.Increment(ref IslandChunksGenerated);
                // BED RE-CARVE (Fires worlds). Generated water seats its surface
                // just under the LOWER bank; where that level drops to meet a low
                // bank the bed has to follow, or the channel becomes too shallow
                // to hold the flow and the run thins out or goes dry. Deepen the
                // bed so at least MinChannelDepth of water stands over it.
                // Worker-safe: the gen water field is immutable load-time arrays.
                float channelDepth = FiresVoxelConfig.WaterMinChannelDepth != null
                    ? FiresVoxelConfig.WaterMinChannelDepth.Value : 0f;
                for (int z = -1; z <= CELLS; z++)
                {
                    for (int x = -1; x <= CELLS; x++)
                    {
                        int col = (x + 1) + SAMPLES * (z + 1);
                        float g = _genGround[col];
                        _biome[col] = _genBiome[col];
                        float wx = WorldOrigin.x + x;
                        float wz = WorldOrigin.z + z;
                        // One-zone-at-a-time: a column of a closed zone is plain sea floor — no
                        // river bed, river water or cave. Decided per COLUMN, because the column on
                        // an open zone's edge is real terrain yet belongs to the closed chunk.
                        bool columnReal = !Zones.ZoneModeGate.IsClosedForTerrain(wx, wz);

                        // W2 - carve the river bed: where hydrology says a
                        // river runs, the effective ground drops to the bed
                        // (wrap-and-subtract, like caves - the vanilla
                        // heightmap data stays untouched underneath).
                        float bankCarve = 0f;   // >0 = this column is in a river/bank zone
                        bool inChannel = false;
                        if (rivers && columnReal)
                        {
                            // Cross-section against the nearest thalweg segment:
                            // the floor rises to meet THIS column's ground at the
                            // bank edge, so the channel has banks and no rim lip.
                            if (VoxelRiverNetwork.TryCarveAt(wx, wz, g, out float carved, out _))
                            {
                                bankCarve = g - carved;
                                g = carved;
                                inChannel = true;
                            }
                            // Pond and lake BOWLS: the basin is carved so the
                            // water has something to sit in, instead of a 20m
                            // fill level floating over real 1m ground.
                            if (VoxelLakeBasins.TryCarveAt(wx, wz, g, out float bowl, out _))
                            {
                                bankCarve = g - bowl;
                                g = bowl;
                            }
                        }
                        else if (FiresWorldGenBase.Active)
                        {
                            inChannel = FiresWorldGenBase.IsChannelAt(wx, wz);
                        }

                        // ── GENERATED WATER, ONE PATH ────────────────────────
                        // This per-column store IS the water: the chunk sheet
                        // and the swim router both read it, and it is the logic
                        // the FullVoxel worldgen has always used. A vanilla
                        // world converted by the hydrology solve gets the SAME
                        // code, sourced through GenWaterField — the earlier
                        // split (carve here, fill with contained pours) is what
                        // left carved basins with no mesh laid across them.
                        // BURY THE EDGE IN THE BANK. The bowl/channel profile
                        // tapers its depth to zero at the rim, so gating the
                        // store on that depth alone ended the sheet a little
                        // short of the real waterline — the mesh stopped in
                        // mid-air with its polygon edges showing instead of
                        // running into the shore. The column is also wet when
                        // THIS column's ground is under the surface, which is
                        // the honest test and carries the sheet into the bank
                        // until the ground rises through it.
                        if (columnReal && GenWaterField.TryWaterSurfaceSmooth(wx, wz, out float wlvl, out float wdep)
                            && (wdep >= 0.15f || g < wlvl - 0.05f) && wlvl > 30.3f
                            // TRUE WET COLUMNS (the generator's raw 2m field —
                            // exact pond shorelines, no floaters past berms) OR
                            // CHANNEL columns (mask incl. bank margins — the
                            // wetted bench below the waterline that the snap
                            // marks dry; the sheet runs over it INTO the bank).
                            // On a vanilla world the field only answers inside a
                            // channel or a carved bowl, so its depth IS the mask.
                            && (!FiresWorldGenBase.Active
                                || FiresWorldGenBase.IsWetRawAt(wx, wz) || inChannel))
                        {
                            // ONE gate for water, then carve as a consequence.
                            // The old split (carve+store behind >=0.25, swim at
                            // >=0.15) manufactured a band that swam with no
                            // sheet — the exact "swim trigger without water"
                            // defect. Store first; deepen the bed only where
                            // there is enough water to justify it.
                            //
                            // CHANNELS ONLY. A bowl already carries its own
                            // radial profile; clamping it to a flat minimum
                            // depth would cut a vertical wall at the waterline
                            // and throw away the shoreline taper.
                            if (channelDepth > 0f && inChannel && wdep >= 0.25f)
                            {
                                float wantBed = wlvl - channelDepth;
                                if (g > wantBed) { bankCarve = g - wantBed; g = wantBed; }
                            }
                            // Carve decided FIRST so the shaft probe sees the
                            // final column ground. A sinkhole/cave mouth under
                            // a water region stays OPEN: no stored column over
                            // the void, so the sheet AND the swim hole together
                            // by construction and the player drops in.
                            if (!IsCaveShaftUnder(wx, wz, wlvl, g))
                            {
                                if (_genWater == null) _genWater = new float[SAMPLES * SAMPLES];
                                // SEAT-DOWN on the STORED level only, and only
                                // for generator water: the sheet and swim read
                                // this, while the bed carve stays keyed to the
                                // undropped surface, so banks keep their height
                                // and gain 0.4m of visible relief. It tapers to
                                // zero at the sea gate — a full seat put mouth
                                // cells UNDER the vanilla ocean level, so the
                                // ocean's own 30.0 swim trigger floated above
                                // our sheet. Hydrology levels arrive already
                                // seated (a bowl's level is its spill point less
                                // the freeboard), so they take no second drop.
                                float seat = GenWaterField.FromGenerator
                                    ? WATER_SEAT_DOWN * Mathf.Clamp01((wlvl - 30.4f) / 0.6f)
                                    : 0f;
                                _genWater[col] = wlvl - seat;
                            }
                        }
                        // SKY ISLAND WATER — grotto pools, crater/rolling
                        // ponds and outlet rills fill the SAME per-column
                        // store the sheet and swim read. Ground water owns
                        // its columns: island levels only land where the base
                        // below is dry, so the one-level-per-column contract
                        // holds.
                        if (skyIsle && FiresSkyIslands.WaterLevelAt(wx, wz, out float isLvl))
                        {
                            bool baseWet = FiresWorldGenBase.TryGetWaterSurfaceSmooth(wx, wz, out float bl2, out float bd2)
                                           && bd2 >= 0.15f && bl2 > 30.3f;
                            if (!baseWet)
                            {
                                if (_genWater == null) _genWater = new float[SAMPLES * SAMPLES];
                                _genWater[col] = isLvl;
                            }
                        }
                        // QUAKE CRACKS - dry, open, real columns only: a crack
                        // under stored water would open a void below the sheet
                        // (swim trigger without water), and river banks and
                        // bowls keep their own profile.
                        bool cracked = _genCracks != null && columnReal && !inChannel && bankCarve <= 0f
                            && (_genWater == null || _genWater[col] == 0f)
                            && FatCracks.Reaches(_genCracks, col, g, WorldOrigin.y);
                        for (int y = -1; y <= CELLS; y++)
                        {
                            float wy = WorldOrigin.y + y;
                            float d = (g - wy) * QUANT;
                            var q = (sbyte)Mathf.Clamp(Mathf.RoundToInt(d), -127, 127);
                            // SKY ISLANDS — union the island body into the
                            // column well above the 2D ground (seed AND
                            // density, so brush-Restore rebuilds the island).
                            if (skyIsle && wy > g + 8f)
                            {
                                float di = FiresSkyIslands.Sdf(wx, wy, wz);
                                if (di > -100f)
                                {
                                    var qi = (sbyte)Mathf.Clamp(Mathf.RoundToInt(di * QUANT), -127, 127);
                                    if (qi > q) q = qi;
                                }
                            }
                            int i = Idx(x, y, z);
                            _seed[i] = q;
                            sbyte dens = q;
                            if (_genCaves && columnReal && g - wy > 0.5f)
                            {
                                float c = VoxelCaveNoise.CarveSdf(wx, wy, wz, g - wy);
                                if (c > -1.5f)
                                {
                                    int carved = Mathf.Clamp(Mathf.RoundToInt(-c * QUANT), -127, 127);
                                    if (carved < dens) dens = (sbyte)carved;
                                }
                            }
                            // W2.1 - UNDERCUT pockets: in the shallow BANK zone
                            // of a river (carve 0.15-2.2m), noise bites the
                            // wall just above the bed -> eroded, overhanging
                            // bank pockets at waterline (the voxel-only look).
                            else if (bankCarve > 0.15f && bankCarve < 2.2f
                                && wy > g + 0.2f && wy < g + 1.6f
                                && VoxelCaveNoise.Noise01(wx * 0.33f, wy * 0.33f, wz * 0.33f) > 0.63f)
                            {
                                if (dens > -6) dens = -6;   // ~0.3m of air bitten into the bank
                            }
                            if (cracked) dens = FatCracks.Carve(dens, _genCracks, col, wx, wy, wz, g);
                            _density[i] = dens;
                        }
                    }
                }
                ApplyBrushEditsLocked();
                // Canonical borders LAST: rings adopt whatever a loaded,
                // generated owner holds (brush edits included), so duplicated
                // boundary cells mesh identically on both sides - density AND
                // the biome mask that drives splat-blend vertex colors.
                if (_genOverrides != null)
                {
                    for (int i = 0; i < _genOverrides.Dens.Count; i++)
                    {
                        var kv = _genOverrides.Dens[i];
                        AdoptRingSampleLocked(kv.Key, kv.Value, _genOverrides.Seed[i]);
                    }
                    foreach (var kv in _genOverrides.Biome)
                        _biome[kv.Key] = kv.Value;
                    _genOverrides = null;
                }
                // (Crossing Y-band for the mesher is computed at SNAPSHOT time
                // in VoxelMeshPipeline - over the live density so post-gen brush
                // edits like vcarve are always included.)
                _genGround = null;
                _genBiome = null;
                _genCracks = null;
                PendingGenerate = false;
                GenFailCount = 0;
                Version++;
            }

            // CACHE WRITE - the deterministic generator output only. Brush
            // edits are NOT in here: they arrive later and persist through the
            // ZDO snapshot path, and a cached edit could otherwise resurrect a
            // carve the player deleted.
            if (!HasBrushEdits && _genCacheable)
                VoxelChunkCache.Store(Key, LastGenHash, Kind, CacheCapture(), swGen.Elapsed.TotalMilliseconds);
        }

        private void EnsureBrushEditsLocked()
        {
            if (_editLo != null) return;
            int n = _density.Length;
            _editLo = new sbyte[n];
            _editHi = new sbyte[n];
            _editBase = new sbyte[n];
            for (int i = 0; i < n; i++) { _editLo[i] = EditLoNone; _editHi[i] = EditHiNone; }
            System.Buffer.BlockCopy(_density, 0, _editBase, 0, n);
        }

        private static sbyte ClampToBounds(sbyte value, sbyte lo, sbyte hi)
            => value < lo ? lo : (value > hi ? hi : value);

        // The freshly generated/restored density becomes the edit base, then
        // the bounds re-apply on top of it.
        private void ApplyBrushEditsLocked()
        {
            if (_editLo == null || _density == null) return;
            System.Buffer.BlockCopy(_density, 0, _editBase, 0, _density.Length);
            for (int i = 0; i < _density.Length; i++)
                _density[i] = ClampToBounds(_editBase[i], _editLo[i], _editHi[i]);
            SpreadGenWaterLocked();
        }

        // Border rings mirror their owner chunk, whose density already carries
        // its own brush edits, so the ring sample holds no bounds of its own.
        private void AdoptRingSampleLocked(int idx, sbyte density, sbyte seed)
        {
            _density[idx] = density;
            _seed[idx] = seed;
            if (_editLo == null) return;
            _editBase[idx] = density;
            _editLo[idx] = EditLoNone;
            _editHi[idx] = EditHiNone;
        }

        private bool AnyBrushEditsLocked()
        {
            if (HasLayerPaint) return true;
            if (_genWaterMop != null)
                for (int i = 0; i < _genWaterMop.Length; i++)
                    if (_genWaterMop[i] != 0) return true;
            if (_editLo == null) return false;
            for (int i = 0; i < _editLo.Length; i++)
                if (_editLo[i] != EditLoNone || _editHi[i] != EditHiNone) return true;
            return false;
        }

        // Full chunk (brush/restore + streamer surface band) - arrays live.
        public VoxelChunk(Vector3Int key)
        {
            Key = key;
            WorldOrigin = new Vector3(key.x * CELLS, key.y * CELLS, key.z * CELLS);
            Kind = ChunkKind.Full;
            AllocateArrays();
        }

        // Uniform chunk - no arrays, no view. The streamer builds these for the
        // deep-solid and high-air stack so they cost almost nothing.
        private VoxelChunk(Vector3Int key, ChunkKind kind)
        {
            Key = key;
            WorldOrigin = new Vector3(key.x * CELLS, key.y * CELLS, key.z * CELLS);
            Kind = kind;
        }

        internal static VoxelChunk NewUniform(Vector3Int key, ChunkKind kind) => new VoxelChunk(key, kind);

        // -- Cache blob ---------------------------------------------------
        // Capture/restore live HERE, inside the chunk's own lock, so the cache
        // never reaches into the arrays behind it. Layout is fixed-size:
        //   _seed  sbyte[SAMPLES^3]
        //   _density sbyte[SAMPLES^3]
        //   _biome Color32[SAMPLES^2]  (r,g,b,a)
        //   _genWater float[SAMPLES^2] (0 = dry; zeros when chunk has none)
        internal const int CacheBlobBytes =
            SAMPLES * SAMPLES * SAMPLES * 2 + SAMPLES * SAMPLES * 4 + SAMPLES * SAMPLES * 4;

        public bool HasGenWater { get { lock (_lock) return _genWater != null; } }

        // Player-authored water removal (the MOP tool on generated water):
        // 1 = column mopped dry. Persisted as a sparse tail on the anchor
        // density blob; restored BEFORE baseline+spread re-derive, so a
        // mopped cave stays empty across relogs.
        private byte[] _genWaterMop;

        internal int MopGenWater(Vector3 center, float radius)
        {
            lock (_lock)
            {
                if (Kind != ChunkKind.Full || _genWater == null) return 0;
                float r2 = radius * radius;
                int changed = 0;
                for (int z = -1; z <= CELLS; z++)
                    for (int x = -1; x <= CELLS; x++)
                    {
                        int col = (x + 1) + SAMPLES * (z + 1);
                        if (_genWater[col] <= 0f) continue;
                        if (_genWaterMop != null && _genWaterMop[col] != 0) continue;
                        float dx = WorldOrigin.x + x - center.x;
                        float dz = WorldOrigin.z + z - center.z;
                        if (dx * dx + dz * dz > r2) continue;
                        if (_genWaterMop == null) _genWaterMop = new byte[SAMPLES * SAMPLES];
                        _genWaterMop[col] = 1;
                        changed++;
                    }
                if (changed > 0)
                {
                    Version++;
                    HasBrushEdits = true;   // rides the anchor persistence + cache exclusion
                }
                return changed;
            }
        }

        // 0.2.715 Vmop (Fire 10-05: "remove our generated or drawn water permanently ... and redraw it ourselves"): the mop
        // mask now marks EVERY column in the disc, dry ones too, because the same persisted mask keeps hand-drawn water
        // (WaterMeshCombiner grids) out of those columns. Returns columns newly marked; wetGen counts the ones that held
        // generated water.
        internal int MopColumns(Vector3 center, float radius, out int wetGen)
        {
            wetGen = 0;
            int changed = 0;
            lock (_lock)
            {
                if (Kind != ChunkKind.Full) return 0;
                float r2 = radius * radius;
                for (int z = -1; z <= CELLS; z++)
                    for (int x = -1; x <= CELLS; x++)
                    {
                        float dx = WorldOrigin.x + x - center.x, dz = WorldOrigin.z + z - center.z;
                        if (dx * dx + dz * dz > r2) continue;
                        int col = (x + 1) + SAMPLES * (z + 1);
                        if (_genWaterMop != null && _genWaterMop[col] != 0) continue;
                        if (_genWaterMop == null) _genWaterMop = new byte[SAMPLES * SAMPLES];
                        _genWaterMop[col] = 1;
                        changed++;
                        if (_genWater != null && _genWater[col] > 0f) wetGen++;
                    }
                if (changed > 0)
                {
                    Version++;
                    HasBrushEdits = true;
                }
            }
            if (changed > 0) VoxelWorld.AnyMops = true;
            return changed;
        }

        // LeftAlt + Vmop: clear the mask in the disc, so generated water there shows again (its stored level was never
        // erased) and drawn water regenerates from its disks.
        internal int UnmopColumns(Vector3 center, float radius)
        {
            lock (_lock)
            {
                if (_genWaterMop == null) return 0;
                float r2 = radius * radius;
                int changed = 0;
                for (int z = -1; z <= CELLS; z++)
                    for (int x = -1; x <= CELLS; x++)
                    {
                        float dx = WorldOrigin.x + x - center.x, dz = WorldOrigin.z + z - center.z;
                        if (dx * dx + dz * dz > r2) continue;
                        int col = (x + 1) + SAMPLES * (z + 1);
                        if (_genWaterMop[col] == 0) continue;
                        _genWaterMop[col] = 0;
                        changed++;
                    }
                if (changed > 0)
                {
                    Version++;
                    HasBrushEdits = AnyBrushEditsLocked();
                }
                return changed;
            }
        }

        internal bool IsColumnMopped(float wx, float wz)
        {
            lock (_lock)
            {
                if (_genWaterMop == null) return false;
                int lx = Mathf.FloorToInt(wx - WorldOrigin.x + 0.5f), lz = Mathf.FloorToInt(wz - WorldOrigin.z + 0.5f);
                if (lx < -1 || lx > CELLS || lz < -1 || lz > CELLS) return false;
                return _genWaterMop[(lx + 1) + SAMPLES * (lz + 1)] != 0;
            }
        }

        // Changes when the mask does (VoxelChunkAnchor.Apply refreshes drawn water only then).
        internal int MopSignature
        {
            get
            {
                lock (_lock)
                {
                    if (_genWaterMop == null) return 0;
                    int h = 17;
                    for (int i = 0; i < _genWaterMop.Length; i++)
                        if (_genWaterMop[i] != 0) h = h * 31 + i + 1;
                    return h;
                }
            }
        }

        // ── CARVE-AND-FILL SELF-REPAIR ───────────────────────────────────
        // Digging into a bank opens AIR below the neighbouring water level —
        // the sheet must extend to the new edge, or the invariant (stored
        // columns == mesh == swim) breaks the moment a player uses a tool.
        // Spread the stored level into any dry column whose voxel is air just
        // below an adjacent wet column's level. The apron columns carry the
        // neighbour chunks' levels, so spreading crosses chunk borders from
        // this side. Deterministic from density + stored levels, so calling
        // it again after an anchor restore re-derives the same water — the
        // dug pocket stays wet across relogs with no extra persistence.
        private void SpreadGenWaterLocked()
        {
            if (_genWater == null || _density == null) return;
            bool grew = true;
            int guard = 0;
            while (grew && guard++ < SAMPLES)
            {
                grew = false;
                for (int z = -1; z <= CELLS; z++)
                {
                    for (int x = -1; x <= CELLS; x++)
                    {
                        int col = (x + 1) + SAMPLES * (z + 1);
                        if (_genWater[col] > 0f) continue;
                        if (_genWaterMop != null && _genWaterMop[col] != 0) continue;   // player mopped — stays dry
                        if (Zones.ZoneModeGate.IsClosedForTerrain(WorldOrigin.x + x, WorldOrigin.z + z)) continue;
                        float lvl = 0f;
                        if (x > -1 && _genWater[col - 1] > lvl) lvl = _genWater[col - 1];
                        if (x < CELLS && _genWater[col + 1] > lvl) lvl = _genWater[col + 1];
                        if (z > -1 && _genWater[col - SAMPLES] > lvl) lvl = _genWater[col - SAMPLES];
                        if (z < CELLS && _genWater[col + SAMPLES] > lvl) lvl = _genWater[col + SAMPLES];
                        if (lvl <= 0f) continue;
                        int ly = Mathf.FloorToInt(lvl - 0.25f - WorldOrigin.y);
                        if (ly < -1 || ly > CELLS) continue;        // level outside this band — its owner layer spreads
                        if (_density[Idx(x, ly, z)] >= 0) continue; // still solid — bank intact, no water here
                        // 0.2.846 (Fire 10-07, 125-127.webp: "a bump of water and rapids forming in our river for no reason"; the
                        // lead's probe at (-503,-3563): one sheet spanning 2.6 m, 1.2-2.5 m over its neighbours, 8 rapid emitters):
                        // the spread took the HIGHEST neighbour level into any dry column with air under it, so on a sloping river
                        // the upstream level flooded naturally LOWER dry ground and stood metres over it. It is for a dug bank: the
                        // column's generated surface must have been at or over the level (it was bank), never lower ground.
                        if (FiresWorldGenBase.Active && FiresWorldGenBase.SampleFinalMeters(WorldOrigin.x + x, WorldOrigin.z + z) < lvl - SpreadBankSlack) continue;
                        _genWater[col] = lvl;
                        grew = true;
                    }
                }
            }
        }

        // Baseline for a chunk whose anchor restore landed BEFORE its generate
        // ran (PendingGenerate=false blocks the generate that would have stored
        // the levels). Same pure function, same gates, same seat-down as the
        // generate path — identical numbers by construction.
        // Void just below the level, probed with the SAME cave SDF the density
        // fill carves with — if the surface would span a shaft, the water is
        // a lid you cannot drop through. Two probes cover the lip band.
        private bool IsCaveShaftUnder(float wx, float wz, float lvl, float ground)
        {
            if (!_genCaves) return false;
            for (int k = 0; k < 2; k++)
            {
                float wy = lvl - (k == 0 ? 0.75f : 1.75f);
                float below = ground - wy;
                if (below <= 0.5f) continue;   // cave field only exists 0.5m under ground
                if (VoxelCaveNoise.CarveSdf(wx, wy, wz, below) > 0.25f) return true;
            }
            return false;
        }

        private bool _genWaterBaseTried;

        private void RebuildGenWaterBaselineLocked()
        {
            if (_genWaterBaseTried) return;
            _genWaterBaseTried = true;
            // 0.2.715: island levels only in chunks inside an island's band, as the generate path does (skyIsle). Ungated,
            // a brush rebuild of a GROUND chunk under an island stored the island pond's level (~y 600) in the ground's
            // column, i.e. water hundreds of metres above the ground the chunk holds.
            bool skyIsle = FiresSkyIslands.ChunkOverlaps(WorldOrigin);
            for (int z = -1; z <= CELLS; z++)
                for (int x = -1; x <= CELLS; x++)
                {
                    float bwx = WorldOrigin.x + x, bwz = WorldOrigin.z + z;
                    // Same provider and same gate as the generate path, so
                    // digging a bank cannot permanently drain a river or a pond
                    // on either world type.
                    if (!Zones.ZoneModeGate.IsClosedForTerrain(bwx, bwz)
                        && GenWaterField.TryWaterSurfaceSmooth(bwx, bwz, out float wlvl, out float wdep)
                        && (wdep >= 0.15f || (_genGround != null && _genGround[(x + 1) + SAMPLES * (z + 1)] < wlvl - 0.05f))   // 0.2.810: _genGround is dropped once a chunk has generated (:642); a brush near shallow water threw here (SinkTestGen sinkhole + grotto, Fire 10-06)
                        && wlvl > 30.3f
                        && (!FiresWorldGenBase.Active
                            || FiresWorldGenBase.IsWetRawAt(bwx, bwz)
                            || FiresWorldGenBase.IsChannelAt(bwx, bwz)))
                    {
                        if (_genWater == null) _genWater = new float[SAMPLES * SAMPLES];
                        float seatB = GenWaterField.FromGenerator
                            ? WATER_SEAT_DOWN * Mathf.Clamp01((wlvl - 30.4f) / 0.6f)
                            : 0f;
                        _genWater[(x + 1) + SAMPLES * (z + 1)] = wlvl - seatB;
                        continue;
                    }
                    // Island ponds/rills restore through the edit path too —
                    // a brush hit near a grotto pool must not drain it.
                    if (skyIsle && FiresSkyIslands.WaterLevelAt(bwx, bwz, out float isLvl))
                    {
                        if (_genWater == null) _genWater = new float[SAMPLES * SAMPLES];
                        _genWater[(x + 1) + SAMPLES * (z + 1)] = isLvl;
                    }
                }
        }

        // Swim batch: the 3x3 neighbourhood around (lx,lz) in ONE lock — the
        // router used to take up to 13 separate lock acquisitions per query
        // per frame per swimmer. k = (dz+1)*3+(dx+1); mopped columns read 0.
        internal bool TryGetWater3x3(int lx, int lz, float[] nine)
        {
            lock (_lock)
            {
                if (_genWater == null) return false;
                int k = 0;
                for (int dz = -1; dz <= 1; dz++)
                    for (int dx = -1; dx <= 1; dx++, k++)
                    {
                        int x = lx + dx, z = lz + dz;
                        if (x < -1 || x > CELLS || z < -1 || z > CELLS) { nine[k] = 0f; continue; }
                        int col = (x + 1) + SAMPLES * (z + 1);
                        nine[k] = (_genWaterMop != null && _genWaterMop[col] != 0) ? 0f : _genWater[col];
                    }
                return true;
            }
        }

        internal float GenWaterAtLocal(int lx, int lz)
        {
            lock (_lock)
            {
                if (_genWater == null) return 0f;
                if (lx < -1 || lx > CELLS || lz < -1 || lz > CELLS) return 0f;
                int col = (lx + 1) + SAMPLES * (lz + 1);
                if (_genWaterMop != null && _genWaterMop[col] != 0) return 0f;
                return _genWater[col];
            }
        }

        internal bool TryCopyGenWater(float[] dst)
        {
            lock (_lock)
            {
                if (_genWater == null || dst == null || dst.Length < SAMPLES * SAMPLES) return false;
                System.Buffer.BlockCopy(_genWater, 0, dst, 0, SAMPLES * SAMPLES * 4);
                return true;
            }
        }

        internal byte[] CacheCapture()
        {
            lock (_lock)
            {
                if (Kind != ChunkKind.Full || _density == null || _seed == null || _biome == null) return null;
                var blob = new byte[CacheBlobBytes];
                int n = SAMPLES * SAMPLES * SAMPLES;
                System.Buffer.BlockCopy(_seed, 0, blob, 0, n);
                System.Buffer.BlockCopy(_density, 0, blob, n, n);
                int o = n * 2;
                for (int i = 0; i < SAMPLES * SAMPLES; i++)
                {
                    var c = _biome[i];
                    blob[o++] = c.r; blob[o++] = c.g; blob[o++] = c.b; blob[o++] = c.a;
                }
                if (_genWater != null)
                    System.Buffer.BlockCopy(_genWater, 0, blob,
                        n * 2 + SAMPLES * SAMPLES * 4, SAMPLES * SAMPLES * 4);
                return blob;
            }
        }

        internal bool CacheRestore(ChunkKind kind, byte[] blob)
        {
            lock (_lock)
            {
                if (kind != ChunkKind.Full)
                {
                    if (_editLo != null) return false;
                    Kind = kind;
                    _density = null; _seed = null; _biome = null; _genWater = null; _genWaterMop = null;
                    _genGround = null; _genBiome = null; _genOverrides = null; _genCracks = null;
                    PendingGenerate = false;
                    Version++;
                    return true;
                }
                if (blob == null || blob.Length != CacheBlobBytes) return false;
                AllocateArrays();
                int n = SAMPLES * SAMPLES * SAMPLES;
                System.Buffer.BlockCopy(blob, 0, _seed, 0, n);
                System.Buffer.BlockCopy(blob, n, _density, 0, n);
                int o = n * 2;
                for (int i = 0; i < SAMPLES * SAMPLES; i++)
                    _biome[i] = new Color32(blob[o++], blob[o++], blob[o++], blob[o++]);
                var wtr = new float[SAMPLES * SAMPLES];
                System.Buffer.BlockCopy(blob, n * 2 + SAMPLES * SAMPLES * 4, wtr, 0, SAMPLES * SAMPLES * 4);
                bool anyW = false;
                for (int i = 0; i < wtr.Length && !anyW; i++) if (wtr[i] != 0f) anyW = true;
                _genWater = anyW ? wtr : null;
                Kind = ChunkKind.Full;
                PendingGenerate = false;
                ApplyBrushEditsLocked();
                Version++;
                return true;
            }
        }

        private void AllocateArrays()
        {
            _density = new sbyte[SAMPLES * SAMPLES * SAMPLES];
            _seed = new sbyte[SAMPLES * SAMPLES * SAMPLES];
            _biome = new Color32[SAMPLES * SAMPLES];
        }

        // Uniform -> Full: materialize the saturated density/seed + per-column
        // biome the uniform state stood for, so brush carves and terraform edits
        // land on a real array. Caller (VoxelWorld.PromoteToFull) owns the
        // governor counter bump.
        internal void MakeFull()
        {
            if (Kind == ChunkKind.Full) return;
            sbyte fill = Kind == ChunkKind.UniformSolid ? UniformSolidDensity : UniformAirDensity;
            var wg = WorldGenerator.instance;
            lock (_lock)
            {
                AllocateArrays();
                for (int i = 0; i < _density.Length; i++) { _density[i] = fill; _seed[i] = fill; }
                for (int z = -1; z <= CELLS; z++)
                    for (int x = -1; x <= CELLS; x++)
                        _biome[(x + 1) + SAMPLES * (z + 1)] = wg != null
                            ? BiomeCornerBlend.Sample(WorldOrigin.x + x, WorldOrigin.z + z)
                            : new Color32(0, 0, 0, 0);
                Kind = ChunkKind.Full;
                Version++;
            }
        }

        // Full -> Uniform: a no-edit chunk that terraform reclassified out of the
        // surface band drops its arrays. Never called on a brush-edited chunk
        // (those stay Full and frozen). Caller owns the counter + view drop.
        internal void MakeUniform(ChunkKind kind)
        {
            lock (_lock)
            {
                Kind = kind;
                _density = null; _seed = null; _biome = null; _genWater = null;
                _genGround = null; _genBiome = null; _genOverrides = null; _genCracks = null;
                PendingGenerate = false;
                Version++;
            }
        }

        internal void RetuneUniform(ChunkKind kind) { if (Kind != ChunkKind.Full) Kind = kind; }

        private static int Idx(int x, int y, int z)
            => (x + 1) + SAMPLES * ((y + 1) + SAMPLES * (z + 1));

        // Local sample coordinates run -1..CELLS, the ring included.
        internal static int SampleIndex(int x, int y, int z) => Idx(x, y, z);

        // -- Seeding - density from the CURRENT ground surface ---------------
        // d = groundY - sampleY (positive below ground = solid), so an unedited
        // chunk reproduces the existing terrain exactly. One ground probe per
        // column (34x34 = 1156). MAIN THREAD (ZoneSystem API). P0 uses the
        // heightmap query; FAT tile surfaces are invisible to it (documented
        // P0 limitation - P2 seeds from tile heights too).
        public void SeedFromGround()
        {
            var wg = WorldGenerator.instance;
            lock (_lock)
            {
                for (int z = -1; z <= CELLS; z++)
                {
                    for (int x = -1; x <= CELLS; x++)
                    {
                        float wx = WorldOrigin.x + x;
                        float wz = WorldOrigin.z + z;
                        // HEIGHTMAP DATA read, NOT a physics raycast. The old
                        // ZoneSystem.GetGroundHeight raycast hit our own voxel
                        // colliders (terrain layer) once neighbours existed -
                        // a chunk seeded next to a voxel build inherited the
                        // BUILD as its "ground" -> neighbouring chunks disagreed
                        // on shared border samples -> cracks/split seams, and
                        // the edited-cell gate misfired. Heightmap.GetHeight
                        // reads the height array (incl. hoe/TerrainComp mods),
                        // is immune to colliders, and is faster.
                        //
                        // +eps bias (round-89): a sample exactly ON a zone-edge
                        // plane is inside TWO heightmaps and FindHeightmap
                        // returns whichever registered FIRST - two chunks could
                        // seed the SAME world sample from DIFFERENT heightmaps
                        // (stale vs fresh terraform data) -> hairline splits on
                        // zone borders. The bias makes every reader resolve the
                        // same (positive-side) heightmap deterministically.
                        float groundY = 30f;
                        var probe = new Vector3(wx + 0.013f, 0f, wz + 0.013f);
                        if (Heightmap.GetHeight(probe, out float hh)) groundY = hh;
                        else if (wg != null) groundY = wg.GetHeight(wx, wz);
                        // Same single biome authority as GatherColumns -
                        // painted overlays tint brush chunks too. Corner-
                        // blended (vanilla parity), matching the gather path
                        // exactly so mirrors never disagree on biome color.
                        _biome[(x + 1) + SAMPLES * (z + 1)] = wg != null
                            ? BiomeCornerBlend.Sample(wx, wz)
                            : new Color32(0, 0, 0, 0);
                        for (int y = -1; y <= CELLS; y++)
                        {
                            float d = (groundY - (WorldOrigin.y + y)) * QUANT;
                            var q = (sbyte)Mathf.Clamp(Mathf.RoundToInt(d), -127, 127);
                            int i = Idx(x, y, z);
                            _seed[i] = q;
                            _density[i] = q;
                        }
                    }
                }
                Version++;
            }
        }

        // -- Brush - SDF shape ops --------------------------------------------
        // Sphere: radius. Cylinder: radius x height (total), centered - flat
        // caps -> discs, shafts, pillars. Cube: radius = half edge length.
        //
        // Solid-positive SDF ops: fill = max(d, s), carve = min(d, -s) where
        // s > 0 inside the shape. Restore hard-resets density to seed inside.
        // Returns true if any sample changed (-> caller enqueues a remesh).
        public bool ApplyShape(Vector3 center, float radius, float height, VoxelBrushShape shape, VoxelBrushMode mode)
        {
            float halfH = shape == VoxelBrushShape.Cylinder ? height * 0.5f : radius;

            bool changed = false;
            // Sample-space AABB of the shape (+1 so boundary lerps update).
            int minX = Mathf.FloorToInt(center.x - radius - 1f - WorldOrigin.x);
            int maxX = Mathf.CeilToInt(center.x + radius + 1f - WorldOrigin.x);
            int minY = Mathf.FloorToInt(center.y - halfH - 1f - WorldOrigin.y);
            int maxY = Mathf.CeilToInt(center.y + halfH + 1f - WorldOrigin.y);
            int minZ = Mathf.FloorToInt(center.z - radius - 1f - WorldOrigin.z);
            int maxZ = Mathf.CeilToInt(center.z + radius + 1f - WorldOrigin.z);
            if (minX > CELLS || maxX < -1 || minY > CELLS || maxY < -1 || minZ > CELLS || maxZ < -1)
                return false;
            minX = Mathf.Max(minX, -1); maxX = Mathf.Min(maxX, CELLS);
            minY = Mathf.Max(minY, -1); maxY = Mathf.Min(maxY, CELLS);
            minZ = Mathf.Max(minZ, -1); maxZ = Mathf.Min(maxZ, CELLS);

            lock (_lock)
            {
                for (int z = minZ; z <= maxZ; z++)
                {
                    for (int y = minY; y <= maxY; y++)
                    {
                        for (int x = minX; x <= maxX; x++)
                        {
                            float wx = WorldOrigin.x + x, wy = WorldOrigin.y + y, wz = WorldOrigin.z + z;
                            float dx = wx - center.x, dy = wy - center.y, dz = wz - center.z;
                            float s;   // metres, > 0 inside the shape
                            switch (shape)
                            {
                                case VoxelBrushShape.Cylinder:
                                    s = Mathf.Min(radius - Mathf.Sqrt(dx * dx + dz * dz),
                                                  halfH - Mathf.Abs(dy));
                                    break;
                                case VoxelBrushShape.Cube:
                                    s = Mathf.Min(radius - Mathf.Abs(dx),
                                        Mathf.Min(radius - Mathf.Abs(dy), radius - Mathf.Abs(dz)));
                                    break;
                                default:
                                    s = radius - Mathf.Sqrt(dx * dx + dy * dy + dz * dz);
                                    break;
                            }

                            int i = Idx(x, y, z);
                            sbyte old = _density[i];
                            switch (mode)
                            {
                                case VoxelBrushMode.Fill:
                                {
                                    var bound = (sbyte)Mathf.Clamp(Mathf.RoundToInt(s * QUANT), EditLoNone, EditHiNone);
                                    if (bound <= old) continue;
                                    EnsureBrushEditsLocked();
                                    if (bound > _editLo[i]) _editLo[i] = bound;
                                    if (bound > _editHi[i]) _editHi[i] = bound;
                                    break;
                                }
                                case VoxelBrushMode.Carve:
                                {
                                    var bound = (sbyte)Mathf.Clamp(Mathf.RoundToInt(-s * QUANT), EditLoNone, EditHiNone);
                                    if (bound >= old) continue;
                                    EnsureBrushEditsLocked();
                                    if (bound < _editLo[i]) _editLo[i] = bound;
                                    if (bound < _editHi[i]) _editHi[i] = bound;
                                    break;
                                }
                                default:
                                    if (s <= 0f || _editLo == null
                                        || (_editLo[i] == EditLoNone && _editHi[i] == EditHiNone)) continue;
                                    _editLo[i] = EditLoNone;
                                    _editHi[i] = EditHiNone;
                                    break;
                            }
                            _density[i] = ClampToBounds(_editBase[i], _editLo[i], _editHi[i]);
                            changed = true;
                        }
                    }
                }
                if (changed)
                {
                    Version++;
                    // Chunks that generated dry (or expanded from uniform) have
                    // no baseline to spread from — derive it once, then spread.
                    if (_genWater == null) RebuildGenWaterBaselineLocked();
                    SpreadGenWaterLocked();   // dug into a bank? the water follows
                    HasBrushEdits = mode != VoxelBrushMode.Restore || AnyBrushEditsLocked();
                }
            }
            return changed;
        }

        // The chunk's whole volume for a blueprint: the density every sample holds right now, caves and overhangs
        // included, ring samples and all, so it lays back down exactly as it was.
        public sbyte[] ExportDensity()
        {
            lock (_lock)
            {
                if (_density == null || _density.Length < SampleCount) return null;
                var copy = new sbyte[SampleCount];
                System.Buffer.BlockCopy(_density, 0, copy, 0, SampleCount);
                return copy;
            }
        }

        // A saved volume, laid down: both edit bounds take the saved density, so the land is the blueprint's whatever
        // was here. That is an absolute override, which a brush's one-sided bound cannot express, and it persists and
        // travels the way every other edit does.
        public bool ApplyDensityStamp(sbyte[] density)
        {
            if (density == null || density.Length != SampleCount) return false;
            bool changed = false;
            lock (_lock)
            {
                EnsureBrushEditsLocked();
                for (int i = 0; i < SampleCount; i++)
                {
                    sbyte wanted = density[i];
                    if (_editLo[i] == wanted && _editHi[i] == wanted && _density[i] == wanted) continue;
                    _editLo[i] = wanted;
                    _editHi[i] = wanted;
                    _density[i] = wanted;
                    changed = true;
                }
                if (changed)
                {
                    Version++;
                    if (_genWater == null) RebuildGenWaterBaselineLocked();
                    SpreadGenWaterLocked();
                    HasBrushEdits = true;
                }
            }
            return changed;
        }

        // A sample an area stamp leaves exactly as it is. No stored density is ever this value.
        internal const sbyte KeepSample = sbyte.MinValue;

        // An area blueprint laid down: like ApplyDensityStamp, but only the samples it names change; KeepSample ones are
        // left alone, edit bounds and all, so the land outside the saved cylinder stays free to regenerate.
        public bool ApplyMaskedStamp(sbyte[] density)
        {
            if (density == null || density.Length != SampleCount) return false;
            bool changed = false;
            lock (_lock)
            {
                EnsureBrushEditsLocked();
                for (int i = 0; i < SampleCount; i++)
                {
                    sbyte wanted = density[i];
                    if (wanted == KeepSample) continue;
                    if (_editLo[i] == wanted && _editHi[i] == wanted && _density[i] == wanted) continue;
                    _editLo[i] = wanted;
                    _editHi[i] = wanted;
                    _density[i] = wanted;
                    changed = true;
                }
                if (changed)
                {
                    Version++;
                    if (_genWater == null) RebuildGenWaterBaselineLocked();
                    SpreadGenWaterLocked();
                    HasBrushEdits = true;
                }
            }
            return changed;
        }

        // ── Skyland blueprints: edits as bounds, sparse ─────────────────────
        // A skyland's edits are its brush bounds over the density it generates, so they travel as bounds: laid down on a
        // copy of the island, they clamp the same rock the copy regenerates.
        internal static bool IsEdited(sbyte lo, sbyte hi) => lo != EditLoNone || hi != EditHiNone;

        // The bounds of an anchor blob (ExportBrushEdits' layout), read without a chunk.
        internal static bool TryReadEditBounds(byte[] raw, out sbyte[] lo, out sbyte[] hi)
        {
            lo = hi = null;
            if (raw == null || raw.Length < SampleCount * 2) return false;
            lo = new sbyte[SampleCount];
            hi = new sbyte[SampleCount];
            System.Buffer.BlockCopy(raw, 0, lo, 0, SampleCount);
            System.Buffer.BlockCopy(raw, SampleCount, hi, 0, SampleCount);
            return true;
        }

        // Bounds set on the samples named and on no others. `clear` takes the named samples' bounds off instead.
        public bool ApplyEditBounds(List<int> samples, List<sbyte> lo, List<sbyte> hi, bool clear)
        {
            bool changed = false;
            lock (_lock)
            {
                if (_density == null) return false;
                EnsureBrushEditsLocked();
                for (int k = 0; k < samples.Count; k++)
                {
                    int i = samples[k];
                    sbyte wantLo = clear ? EditLoNone : lo[k], wantHi = clear ? EditHiNone : hi[k];
                    if (_editLo[i] == wantLo && _editHi[i] == wantHi) continue;
                    _editLo[i] = wantLo;
                    _editHi[i] = wantHi;
                    _density[i] = ClampToBounds(_editBase[i], wantLo, wantHi);
                    changed = true;
                }
                if (changed)
                {
                    Version++;
                    if (_genWater == null) RebuildGenWaterBaselineLocked();
                    SpreadGenWaterLocked();
                    HasBrushEdits = AnyBrushEditsLocked();
                }
            }
            return changed;
        }

        // P1 persistence — the brush edit bounds (lo then hi, one sbyte per
        // sample; the caller compresses) plus the water-mop tail
        // [0xF9][count lo][count hi][col lo, col hi]*. The ground re-derives on
        // restore, so only the edits travel.
        private const byte MopTailMarker = 0xF9;
        internal const int SampleCount = SAMPLES * SAMPLES * SAMPLES;

        // ── Layered paint (FAT 0.2.758; Fire 10-06: paint keeps to the layer it is aimed at, like every tool) ──────────
        // One byte a sample: the low nibble the ground paint (VoxelLayerPaint.Dirt / Cultivated / Paved, 0 none), the high
        // nibble the biome it is turned to (VoxelLayerPaint.BiomeCode, 0 its own). Null until painted. A cave floor or a
        // skyland wears it instead of the ground column's paint mask; it rides the anchor blob as [0xFA] + SampleCount bytes
        // after the mop tail, so the paint syncs, saves and undoes with the chunk's edits.
        private const byte PaintTailMarker = 0xFA;
        private byte[] _paint;
        internal bool HasLayerPaint { get; private set; }

        private static bool AnyPaint(byte[] paint)
        {
            for (int i = 0; i < paint.Length; i++) if (paint[i] != 0) return true;
            return false;
        }

        // The paint of the sample at local (x, y, z) (-1..CELLS each), 0 when none. Main thread (render, biome lookups).
        internal byte PaintAt(int x, int y, int z)
        {
            var paint = _paint;
            if (paint == null || x < -1 || x > CELLS || y < -1 || y > CELLS || z < -1 || z > CELLS) return 0;
            return paint[Idx(x, y, z)];
        }

        // Paints the samples of local column (x, z) from y0 to y1 (local, clipped to the chunk): the ground paint when
        // setMask (0 clears it), the biome when setBiome (0 clears it). True when a sample changed.
        internal bool PaintColumn(int x, int z, int y0, int y1, bool setMask, byte mask, bool setBiome, byte biome)
        {
            if (x < -1 || x > CELLS || z < -1 || z > CELLS) return false;
            y0 = Mathf.Max(y0, -1);
            y1 = Mathf.Min(y1, CELLS);
            if (y1 < y0) return false;
            lock (_lock)
            {
                if (_density == null || Kind != ChunkKind.Full) return false;
                bool changed = false;
                for (int y = y0; y <= y1; y++)
                {
                    int i = Idx(x, y, z);
                    byte was = _paint != null ? _paint[i] : (byte)0;
                    byte now = was;
                    if (setMask) now = (byte)((now & 0xF0) | (mask & 0x0F));
                    if (setBiome) now = (byte)((now & 0x0F) | ((biome & 0x0F) << 4));
                    if (now == was) continue;
                    if (_paint == null) _paint = new byte[SampleCount];
                    _paint[i] = now;
                    changed = true;
                }
                if (!changed) return false;
                EnsureBrushEditsLocked();
                HasLayerPaint = AnyPaint(_paint);
                HasBrushEdits = AnyBrushEditsLocked();
                if (HasLayerPaint) VoxelLayerPaint.NoteChunkPainted();
                Version++;
                return true;
            }
        }

        // The density of the sample at local (x, y, z), sbyte.MinValue outside or when the chunk holds no arrays. Main thread.
        internal sbyte DensityAt(int x, int y, int z)
        {
            if (x < -1 || x > CELLS || y < -1 || y > CELLS || z < -1 || z > CELLS) return sbyte.MinValue;
            if (Kind != ChunkKind.Full) return Kind == ChunkKind.UniformSolid ? UniformSolidDensity : UniformAirDensity;
            var density = _density;
            return density != null ? density[Idx(x, y, z)] : sbyte.MinValue;
        }

        public byte[] ExportBrushEdits()
        {
            lock (_lock)
            {
                int mops = 0;
                if (_genWaterMop != null)
                    for (int i = 0; i < _genWaterMop.Length; i++) if (_genWaterMop[i] != 0) mops++;
                var paint = HasLayerPaint ? _paint : null;
                int paintAt = SampleCount * 2 + (mops > 0 ? 3 + mops * 2 : 0);
                var blob = new byte[paintAt + (paint != null ? 1 + SampleCount : 0)];
                if (_editLo != null)
                {
                    System.Buffer.BlockCopy(_editLo, 0, blob, 0, SampleCount);
                    System.Buffer.BlockCopy(_editHi, 0, blob, SampleCount, SampleCount);
                }
                else
                {
                    for (int i = 0; i < SampleCount; i++)
                    {
                        blob[i] = unchecked((byte)EditLoNone);
                        blob[SampleCount + i] = (byte)EditHiNone;
                    }
                }
                if (mops > 0)
                {
                    int o = SampleCount * 2;
                    blob[o++] = MopTailMarker;
                    blob[o++] = (byte)(mops & 0xFF);
                    blob[o++] = (byte)((mops >> 8) & 0xFF);
                    for (int i = 0; i < _genWaterMop.Length; i++)
                        if (_genWaterMop[i] != 0) { blob[o++] = (byte)(i & 0xFF); blob[o++] = (byte)((i >> 8) & 0xFF); }
                }
                if (paint != null)
                {
                    blob[paintAt] = PaintTailMarker;
                    System.Buffer.BlockCopy(paint, 0, blob, paintAt + 1, SampleCount);
                }
                return blob;
            }
        }

        public bool ImportBrushEdits(byte[] raw)
        {
            if (raw == null || raw.Length < SampleCount * 2)
            {
                FiresLogger.LogWarning($"[FiresVoxel] brush edit import rejected for {Key}: " +
                    $"{(raw == null ? "null blob" : $"length {raw.Length} < {SampleCount * 2}")} — restore will retry on next revision.");
                return false;
            }
            lock (_lock)
            {
                if (_density == null) return false;
                EnsureBrushEditsLocked();
                System.Buffer.BlockCopy(raw, 0, _editLo, 0, SampleCount);
                System.Buffer.BlockCopy(raw, SampleCount, _editHi, 0, SampleCount);
                // Water-mop tail — parse BEFORE the baseline+spread below so
                // mopped columns stay dry.
                _genWaterMop = null;
                int tail = SampleCount * 2;
                int paintAt = tail;
                if (raw.Length > tail + 2 && raw[tail] == MopTailMarker)
                {
                    int cnt = raw[tail + 1] | (raw[tail + 2] << 8);
                    int o = tail + 3;
                    paintAt = o + cnt * 2;
                    if (raw.Length >= o + cnt * 2)
                        for (int k = 0; k < cnt; k++)
                        {
                            int col = raw[o] | (raw[o + 1] << 8);
                            o += 2;
                            if (col < SAMPLES * SAMPLES)
                            {
                                if (_genWaterMop == null) _genWaterMop = new byte[SAMPLES * SAMPLES];
                                _genWaterMop[col] = 1;
                            }
                        }
                }
                if (_genWaterMop != null) VoxelWorld.AnyMops = true;   // 0.2.715: drawn water consults the mask from now on
                // 0.2.758: the layered paint tail ([0xFA] + one byte a sample) after the mop tail; none = no paint.
                if (raw.Length >= paintAt + 1 + SampleCount && raw[paintAt] == PaintTailMarker)
                {
                    var paint = new byte[SampleCount];
                    System.Buffer.BlockCopy(raw, paintAt + 1, paint, 0, SampleCount);
                    _paint = paint;
                    HasLayerPaint = AnyPaint(paint);
                }
                else { _paint = null; HasLayerPaint = false; }
                if (HasLayerPaint) VoxelLayerPaint.NoteChunkPainted();
                for (int i = 0; i < SampleCount; i++)
                    _density[i] = ClampToBounds(_editBase[i], _editLo[i], _editHi[i]);
                HasBrushEdits = AnyBrushEditsLocked();
                // Restored edits include dug-out banks: re-derive the water
                // baseline if generate never ran, then re-spread into the digs
                // so the pocket is wet again after relog.
                if (_genWater == null) RebuildGenWaterBaselineLocked();
                SpreadGenWaterLocked();
                Version++;
            }
            return true;
        }

        // -- Column surface query (for the liquid-water bridge) --------------
        // Highest solid->air crossing in this chunk's column nearest (wx,wz),
        // within world-Y [botY, topY]. Reads the EDITED density (post-carve/
        // fill), interpolated like the mesher, so water reads the exact same
        // surface the mesh shows. solidAtTop = the column is still solid at
        // topY (a wall taller than the probe range). Main-thread safe; locks.
        public bool SampleColumnSurface(float wx, float wz, float topY, float botY,
            out float surfY, out bool solidAtTop)
        {
            surfY = float.MinValue;
            solidAtTop = false;

            int lx = Mathf.RoundToInt(wx - WorldOrigin.x);
            int lz = Mathf.RoundToInt(wz - WorldOrigin.z);
            if (lx < -1 || lx > CELLS || lz < -1 || lz > CELLS) return false;

            // 0.2.752: "solid at the top" is this chunk's answer only when topY lies INSIDE it. A uniform-solid chunk or a
            // full chunk wholly below topY (the rock under any ground) used to report it too, so TrySampleSurfaceY read a wall
            // under almost every column (Fire 10-05: every Vpond "no voxel ground", placed water clipped away as "bank above").
            int rawTop = Mathf.FloorToInt(topY - WorldOrigin.y);
            bool topInside = rawTop >= 0 && rawTop <= CELLS;

            // Uniform chunks carry no crossing: solid = a wall spanning the whole
            // probe range, air = nothing.
            if (Kind != ChunkKind.Full)
            {
                solidAtTop = topInside && Kind == ChunkKind.UniformSolid;
                return false;
            }

            int yTop = Mathf.Min(CELLS, rawTop);
            int yBot = Mathf.Max(-1, Mathf.CeilToInt(botY - WorldOrigin.y) - 1);
            if (yTop <= yBot) return false;

            lock (_lock)
            {
                // Atomic re-check: this method is reachable off the main thread
                // (the liquid bridge), and the Kind fast-path above is not atomic
                // with the deref - a main-thread MakeUniform could have nulled the
                // arrays in the gap. Both Kind and _density flip under this lock.
                if (_density == null) { solidAtTop = topInside && Kind == ChunkKind.UniformSolid; return false; }
                if (topInside && _density[Idx(lx, yTop, lz)] > 0) { solidAtTop = true; return false; }
                for (int y = yTop; y > yBot; y--)
                {
                    float dAbove = _density[Idx(lx, y, lz)];
                    float dBelow = _density[Idx(lx, y - 1, lz)];
                    if (dBelow > 0f && dAbove <= 0f)
                    {
                        float t = dBelow / (dBelow - dAbove);
                        surfY = WorldOrigin.y + (y - 1) + t;
                        return true;
                    }
                }
            }
            return false;
        }

        // True when no brush edits remain (VoxelWorld GC after Restore brushes).
        public bool IsFullyRestored()
        {
            lock (_lock) return !AnyBrushEditsLocked();
        }

        // Sentinel: the chunk reclassified to uniform (arrays nulled) after it
        // was queued but before the worker snapshotted it. The worker drops the
        // job; the view was already torn down by the downgrade.
        public const int SnapshotUniform = int.MinValue;

        // Copy density + seed + column biome for the worker-thread mesher
        // (edited-cell gate needs seed; vertex colors need biome). Returns
        // the version the snapshot represents.
        public int Snapshot(sbyte[] intoDensity, sbyte[] intoSeed, Color32[] intoBiome,
            float[] intoWater, out bool hasWater, float[] intoGround = null)
        {
            lock (_lock)
            {
                hasWater = false;
                if (_density == null) return SnapshotUniform;
                System.Array.Copy(_density, intoDensity, _density.Length);
                System.Array.Copy(_seed, intoSeed, _seed.Length);
                System.Array.Copy(_biome, intoBiome, _biome.Length);
                // The 2D ground baseline rides the same snapshot as the density
                // and the water level. The sheet's lip guard needs the ground at
                // COLUMN resolution to know a bank crest from an undercut, and
                // querying a field for it per skirt cell is both slow and, on a
                // converted world, answered by a 20m grid that cannot see a 1m
                // crest at all.
                if (intoGround != null && _genGround != null)
                    System.Array.Copy(_genGround, intoGround, _genGround.Length);
                if (_genWater != null && intoWater != null)
                {
                    System.Array.Copy(_genWater, intoWater, _genWater.Length);
                    // Mopped columns carry a NEGATIVE sentinel: dry for the
                    // mesh AND immune to the skirt dilation (a plain 0 would
                    // let neighbours re-lid the mopped area with visual-only
                    // water).
                    if (_genWaterMop != null)
                        for (int i = 0; i < intoWater.Length; i++)
                            if (_genWaterMop[i] != 0) intoWater[i] = -1f;
                    hasWater = true;
                }
                return Version;
            }
        }
    }
}
