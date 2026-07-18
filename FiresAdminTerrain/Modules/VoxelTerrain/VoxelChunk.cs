using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // Voxel P0 — one 32m³ chunk of signed-distance density.
    //
    // Convention: density > 0 = SOLID, < 0 = air, surface at 0. Quantized to
    // sbyte at QUANT units/m (±6.35m band) — marching interpolates crossings,
    // so the surface is smooth despite 1m cells.
    //
    // Sample grid: [-1..32] per axis (34³) — a 1-sample apron duplicated from
    // the neighbourhood, the TerrainTile border-duplication pattern in 3D.
    // Each chunk can mesh its border cells (index -1) itself with vertices
    // that coincide bit-exactly with the neighbour's (same densities, same
    // math) → no cross-chunk indexing, no cracks.
    //
    // Threading: brush writes + mesher snapshot both go through _lock. The
    // mesher copies the array under the lock (39KB — microseconds) and works
    // on the snapshot, so the worker never sees a torn write.
    // Brush modes. Restore writes the SEED value back (un-carve/un-fill —
    // the "delete voxel edits" tool); a fully-restored chunk is dropped by
    // VoxelWorld so nothing renders.
    public enum VoxelBrushMode { Carve, Fill, Restore }

    // Brush shapes: Cylinder = the flat-ruler tools (radius × height disc),
    // Sphere/Cube = the 3D gizmo tools (radius = half-extent).
    public enum VoxelBrushShape { Sphere, Cylinder, Cube }

    public sealed class VoxelChunk
    {
        public const int CELLS = 32;              // cells per axis (1m each)
        public const int SAMPLES = CELLS + 2;     // sample grid [-1..32] = 34
        public const float QUANT = 20f;           // sbyte units per metre (0.05m)

        public readonly Vector3Int Key;           // chunk coords (world / 32)
        public readonly Vector3 WorldOrigin;      // sample (0,0,0) world position

        // _seed = the ground-derived baseline; _density = seed + edits. The
        // mesher only emits cells whose corners DIFFER from seed, so unedited
        // ground never grows a duplicate skin over the vanilla terrain (no
        // z-fighting), and restoring everything makes the chunk disappear.
        private readonly sbyte[] _density = new sbyte[SAMPLES * SAMPLES * SAMPLES];
        private readonly sbyte[] _seed = new sbyte[SAMPLES * SAMPLES * SAMPLES];
        // Per-COLUMN biome blend color (34×34, sampled from WorldGenerator at
        // seed time) so voxel surfaces auto-match the surrounding biome —
        // plains grass in Plains, meadows in Meadows — instead of a fixed
        // Meadows mask. Same encoding the tiles use (Heightmap.GetBiomeColor).
        private readonly Color32[] _biome = new Color32[SAMPLES * SAMPLES];
        private readonly object _lock = new object();

        // Bumped on every mutation; stale mesh/bake results are dropped by
        // comparing against the version they were built from.
        public int Version;

        // Version of the data the LIVE render mesh was built from (set by the
        // pipeline on apply). Version != AppliedVersion = a stale mesh on
        // converged data — the seam class the sweep's data-diff test could
        // never see.
        public int AppliedVersion = -1;

        // Set while the chunk sits in the remesh queue (dedup).
        public bool Queued;

        // Worker-throw retry budget for pending generates (reset on arm and
        // on success) — bounds the retry loop the pipeline catch runs.
        public int GenFailCount;

        // W1 — streamed (zone-wrap) chunks mesh the WHOLE surface, not just
        // edited cells: in a wrapped zone the vanilla heightmap render is
        // hidden, so the voxel mesh IS the ground. Brush-only chunks keep the
        // edited-cell gate (they overlay visible vanilla terrain).
        public bool MeshAll;

        // Crossing Y-band (local voxel Y): the mesher scans only this range —
        // everything above is air, below is solid. hi < lo ⇒ uniform chunk.
        // Defaults mesh the full column until RunPendingGenerate narrows them.
        public int MeshYLo = -1, MeshYHi = CELLS + 1;

        // Faces whose in-plane quads NO neighbour will emit (absent, pending,
        // or gated): each border quad has exactly one owner — the chunk on the
        // plane's positive side — so a MeshAll chunk's skin stops half a cell
        // short of an uncovered face (the dark trench at the wrap frontier).
        // Recomputed at every enqueue; the mesher seals flagged faces with
        // shingle lips. Bits: 1=+X, 2=−X, 4=+Z, 8=−Z.
        public byte FrontierMask;

        // +faces whose OWNER CHUNK doesn't exist (1=+X, 4=+Z): this chunk
        // meshes the plane itself (orphan-face emission) until the owner
        // appears — recomputed at every enqueue, compared by the heal.
        public byte OrphanMask;

        // W1.2 — streamed chunks keep their RENDERER hidden until the zone's
        // swap moment (colliders bake first, then voxel-on/vanilla-off flips in
        // ONE frame). Kills the z-fight window where both terrains rendered
        // coplanar during wrap-in. Copied onto the view at creation.
        public bool SuppressRenderUntilSwap;

        // P1 — set the moment a BRUSH mutates this chunk. Edited chunks are
        // FROZEN: zone generation never overwrites them (PrepareGenerate
        // refuses), and they persist via VoxelChunkAnchor ZDOs so brushwork
        // survives relog and syncs to other clients.
        public bool HasBrushEdits;

        // W1 — deferred generation payload (filled by the zone streamer on the
        // main thread, executed on the WORKER before the first mesh): ground +
        // biome per column, then the deterministic cave-noise carve.
        private float[] _genGround;    // SAMPLES×SAMPLES world ground heights
        private Color32[] _genBiome;   // SAMPLES×SAMPLES biome colors
        private bool _genCaves;
        public bool PendingGenerate { get; private set; }

        // Change-gate: hash of the last executed generate inputs. A zone
        // rewrap whose gather hashes identically is a NO-OP — skipping it
        // avoids remesh + collider swaps under the player (rubber-banding).
        public uint LastGenHash;

        public void PrepareGenerate(float[] ground, Color32[] biome, bool caves)
        {
            lock (_lock)
            {
                if (HasBrushEdits) return;   // frozen — generation never eats brushwork
                _genGround = ground;
                _genBiome = biome;
                _genCaves = caves;
                _genOverrides = null;
                GenFailCount = 0;
                PendingGenerate = true;
            }
        }

        // ── Canonical border ownership (the REAL seam fix) ───────────────────
        // Every world sample is owned by exactly one chunk: the one holding it
        // in its [0..31] interior. The -1/32 border rings of every other chunk
        // MIRROR the owner — density AND the per-column biome mask (vertex
        // colors drive splat blending; a border biome tint diff paints a line
        // even on perfect geometry). Duplicated boundary cells then mesh
        // identically on both sides regardless of frozen edits, regen timing
        // or legacy saves.
        public sealed class BorderOverrideSet
        {
            public List<KeyValuePair<int, sbyte>> Dens;
            // Owner's SEED for the same samples, index-parallel with Dens.
            // Mirroring density alone used to write seed := density, erasing
            // the ring's edited-status on the receiving side — the two chunks
            // then disagreed at the mesher's edited-cell gate and the quad
            // owner silently dropped rows exactly on the 32m plane.
            public List<sbyte> Seed;
            public List<KeyValuePair<int, Color32>> Biome;
        }

        private BorderOverrideSet _genOverrides;

        public void SetBorderOverrides(BorderOverrideSet overrides)
        {
            lock (_lock)
            {
                if (PendingGenerate) _genOverrides = overrides;
            }
        }

        // Main-thread only (reads neighbour arrays without their locks — all
        // mutation paths for generated chunks are main-thread serialized or
        // pending-gated). Returns null when no generated neighbour is loaded.
        public static BorderOverrideSet CaptureBorderOverrides(Vector3Int key)
        {
            BorderOverrideSet set = null;
            var cache = new VoxelChunk[27];
            var seen = new bool[27];
            for (int z = -1; z <= CELLS; z++)
            {
                bool zEdge = z == -1 || z == CELLS;
                for (int y = -1; y <= CELLS; y++)
                {
                    bool yEdge = y == -1 || y == CELLS;
                    for (int x = -1; x <= CELLS; x++)
                    {
                        if (!zEdge && !yEdge && x != -1 && x != CELLS) continue;   // interior — we own it
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
                        if (set == null) set = new BorderOverrideSet
                        {
                            Dens = new List<KeyValuePair<int, sbyte>>(1024),
                            Seed = new List<sbyte>(1024),
                            Biome = new List<KeyValuePair<int, Color32>>(256),
                        };
                        int ownerIdx = Idx(x - ox * CELLS, y - oy * CELLS, z - oz * CELLS);
                        set.Dens.Add(new KeyValuePair<int, sbyte>(Idx(x, y, z), owner._density[ownerIdx]));
                        set.Seed.Add(owner._seed[ownerIdx]);
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
                    if (set == null) set = new BorderOverrideSet
                    {
                        Dens = new List<KeyValuePair<int, sbyte>>(0),
                        Seed = new List<sbyte>(0),
                        Biome = new List<KeyValuePair<int, Color32>>(256),
                    };
                    int colIdx = (x + 1) + SAMPLES * (z + 1);
                    int ownerCol = ((x - ox * CELLS) + 1) + SAMPLES * ((z - oz * CELLS) + 1);
                    set.Biome.Add(new KeyValuePair<int, Color32>(colIdx, colOwner._biome[ownerCol]));
                }
            }
            return set;
        }

        // Diagnostic probe: density at a world position (-128 = outside).
        public sbyte SampleDensityWorld(Vector3 w)
        {
            int lx = Mathf.FloorToInt(w.x) - Key.x * CELLS;
            int ly = Mathf.FloorToInt(w.y) - Key.y * CELLS;
            int lz = Mathf.FloorToInt(w.z) - Key.z * CELLS;
            if (lx < -1 || lx > CELLS || ly < -1 || ly > CELLS || lz < -1 || lz > CELLS)
                return sbyte.MinValue;
            return _density[Idx(lx, ly, lz)];
        }

        // Re-mirror this chunk's border rings from loaded owners RIGHT NOW.
        // apply=false audits only. Returns the number of mismatched samples
        // (density + biome columns).
        public int MirrorBordersNow(bool apply)
        {
            var overrides = CaptureBorderOverrides(Key);
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
                    if (apply)
                    {
                        _density[kv.Key] = kv.Value;
                        _seed[kv.Key] = sv;
                    }
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
        // thread (brush guard) — whoever gets there first runs it.
        public void RunPendingGenerate()
        {
            lock (_lock)
            {
                if (!PendingGenerate) return;
                // W2's voxel-side river carve exists for VANILLA-generated
                // worlds (wrap-and-subtract). On FiresWorldGen worlds the
                // drainage is carved into the BASE heightfield itself — the
                // vanilla surface already has the channels. Carving again in
                // the voxel layer double-cuts the beds AND makes the voxel
                // surface diverge from the vanilla surface, which shows as
                // steps/tears at every streaming frontier along rivers.
                bool rivers = FiresVoxelConfig.HydrologyEnabled != null
                    && FiresVoxelConfig.HydrologyEnabled.Value && VoxelHydrology.Ready
                    && !FiresWorldGenBase.Active;
                for (int z = -1; z <= CELLS; z++)
                {
                    for (int x = -1; x <= CELLS; x++)
                    {
                        int col = (x + 1) + SAMPLES * (z + 1);
                        float g = _genGround[col];
                        _biome[col] = _genBiome[col];
                        float wx = WorldOrigin.x + x;
                        float wz = WorldOrigin.z + z;

                        // W2 — carve the river bed: where hydrology says a
                        // river runs, the effective ground drops to the bed
                        // (wrap-and-subtract, like caves — the vanilla
                        // heightmap data stays untouched underneath).
                        float bankCarve = 0f;   // >0 = this column is in a river/bank zone
                        if (rivers)
                        {
                            float bed = VoxelHydrology.SampleBedY(wx, wz);
                            if (bed < g) { bankCarve = g - bed; g = bed; }
                        }
                        for (int y = -1; y <= CELLS; y++)
                        {
                            float wy = WorldOrigin.y + y;
                            float d = (g - wy) * QUANT;
                            var q = (sbyte)Mathf.Clamp(Mathf.RoundToInt(d), -127, 127);
                            int i = Idx(x, y, z);
                            _seed[i] = q;
                            sbyte dens = q;
                            if (_genCaves && g - wy > 0.5f)
                            {
                                float c = VoxelCaveNoise.CarveSdf(wx, wy, wz, g - wy);
                                if (c > -1.5f)
                                {
                                    int carved = Mathf.Clamp(Mathf.RoundToInt(-c * QUANT), -127, 127);
                                    if (carved < dens) dens = (sbyte)carved;
                                }
                            }
                            // W2.1 — UNDERCUT pockets: in the shallow BANK zone
                            // of a river (carve 0.15–2.2m), noise bites the
                            // wall just above the bed → eroded, overhanging
                            // bank pockets at waterline (the voxel-only look).
                            else if (bankCarve > 0.15f && bankCarve < 2.2f
                                && wy > g + 0.2f && wy < g + 1.6f
                                && VoxelCaveNoise.Noise01(wx * 0.33f, wy * 0.33f, wz * 0.33f) > 0.63f)
                            {
                                if (dens > -6) dens = -6;   // ~0.3m of air bitten into the bank
                            }
                            _density[i] = dens;
                        }
                    }
                }
                // Canonical borders LAST: rings adopt whatever a loaded,
                // generated owner holds (frozen edits included), so duplicated
                // boundary cells mesh identically on both sides — density AND
                // the biome mask that drives splat-blend vertex colors.
                if (_genOverrides != null)
                {
                    for (int i = 0; i < _genOverrides.Dens.Count; i++)
                    {
                        var kv = _genOverrides.Dens[i];
                        _density[kv.Key] = kv.Value;
                        _seed[kv.Key] = _genOverrides.Seed[i];
                    }
                    foreach (var kv in _genOverrides.Biome)
                        _biome[kv.Key] = kv.Value;
                    _genOverrides = null;
                }
                // (Crossing Y-band for the mesher is computed at SNAPSHOT time
                // in VoxelMeshPipeline — over the live density so post-gen brush
                // edits like vcarve are always included.)
                _genGround = null;
                _genBiome = null;
                PendingGenerate = false;
                GenFailCount = 0;
                Version++;
            }
        }

        public VoxelChunk(Vector3Int key)
        {
            Key = key;
            WorldOrigin = new Vector3(key.x * CELLS, key.y * CELLS, key.z * CELLS);
        }

        private static int Idx(int x, int y, int z)
            => (x + 1) + SAMPLES * ((y + 1) + SAMPLES * (z + 1));

        // ── Seeding — density from the CURRENT ground surface ───────────────
        // d = groundY − sampleY (positive below ground = solid), so an unedited
        // chunk reproduces the existing terrain exactly. One ground probe per
        // column (34×34 = 1156). MAIN THREAD (ZoneSystem API). P0 uses the
        // heightmap query; FAT tile surfaces are invisible to it (documented
        // P0 limitation — P2 seeds from tile heights too).
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
                        // colliders (terrain layer) once neighbours existed —
                        // a chunk seeded next to a voxel build inherited the
                        // BUILD as its "ground" → neighbouring chunks disagreed
                        // on shared border samples → cracks/split seams, and
                        // the edited-cell gate misfired. Heightmap.GetHeight
                        // reads the height array (incl. hoe/TerrainComp mods),
                        // is immune to colliders, and is faster.
                        //
                        // +ε bias (round-89): a sample exactly ON a zone-edge
                        // plane is inside TWO heightmaps and FindHeightmap
                        // returns whichever registered FIRST — two chunks could
                        // seed the SAME world sample from DIFFERENT heightmaps
                        // (stale vs fresh terraform data) → hairline splits on
                        // zone borders. The bias makes every reader resolve the
                        // same (positive-side) heightmap deterministically.
                        float groundY = 30f;
                        var probe = new Vector3(wx + 0.013f, 0f, wz + 0.013f);
                        if (Heightmap.GetHeight(probe, out float hh)) groundY = hh;
                        else if (wg != null) groundY = wg.GetHeight(wx, wz);
                        // Same single biome authority as GatherColumns —
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

        // ── Brush — SDF shape ops ────────────────────────────────────────────
        // Sphere: radius. Cylinder: radius × height (total), centered — flat
        // caps → discs, shafts, pillars. Cube: radius = half edge length.
        //
        // Solid-positive SDF ops: fill = max(d, s), carve = min(d, −s) where
        // s > 0 inside the shape. Restore hard-resets density to seed inside.
        // Returns true if any sample changed (→ caller enqueues a remesh).
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
                            int old = _density[i];
                            int now = old;
                            switch (mode)
                            {
                                case VoxelBrushMode.Fill:
                                    now = Mathf.Max(old, Mathf.Clamp(Mathf.RoundToInt(s * QUANT), -127, 127));
                                    break;
                                case VoxelBrushMode.Carve:
                                    now = Mathf.Min(old, Mathf.Clamp(Mathf.RoundToInt(-s * QUANT), -127, 127));
                                    break;
                                case VoxelBrushMode.Restore:
                                    if (s > 0f) now = _seed[i];
                                    break;
                            }
                            if (now != old) { _density[i] = (sbyte)now; changed = true; }
                        }
                    }
                }
                if (changed)
                {
                    Version++;
                    HasBrushEdits = true;   // P1 — freeze vs generation + persist
                }
            }
            return changed;
        }

        // ── P1 persistence — full density blob (compressed by the caller's
        // Utils.Compress; RLE-friendly, few KB typical). Seed/biome recompute
        // from world data on restore, so only density needs to travel.
        public byte[] ExportDensity()
        {
            lock (_lock)
            {
                var copy = new byte[_density.Length];
                System.Buffer.BlockCopy(_density, 0, copy, 0, _density.Length);
                return copy;
            }
        }

        public bool ImportDensity(byte[] raw)
        {
            if (raw == null || raw.Length != _density.Length)
            {
                Debug.LogWarning($"[FiresVoxel] ImportDensity rejected for {Key}: " +
                    $"{(raw == null ? "null blob" : $"length {raw.Length} != {_density.Length}")} — restore will retry on next revision.");
                return false;
            }
            lock (_lock)
            {
                System.Buffer.BlockCopy(raw, 0, _density, 0, raw.Length);
                HasBrushEdits = true;
                PendingGenerate = false;
                _genGround = null; _genBiome = null;
                Version++;
            }
            return true;
        }

        // ── Column surface query (for the liquid-water bridge) ──────────────
        // Highest solid→air crossing in this chunk's column nearest (wx,wz),
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

            int yTop = Mathf.Min(CELLS, Mathf.FloorToInt(topY - WorldOrigin.y));
            int yBot = Mathf.Max(-1, Mathf.CeilToInt(botY - WorldOrigin.y) - 1);
            if (yTop <= yBot) return false;

            lock (_lock)
            {
                if (_density[Idx(lx, yTop, lz)] > 0) { solidAtTop = true; return false; }
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

        // True when every sample matches the seed — the chunk holds no edits
        // and can be dropped entirely (VoxelWorld GC after Restore brushes).
        public bool IsFullyRestored()
        {
            lock (_lock)
            {
                for (int i = 0; i < _density.Length; i++)
                    if (_density[i] != _seed[i]) return false;
                return true;
            }
        }

        // Copy density + seed + column biome for the worker-thread mesher
        // (edited-cell gate needs seed; vertex colors need biome). Returns
        // the version the snapshot represents.
        public int Snapshot(sbyte[] intoDensity, sbyte[] intoSeed, Color32[] intoBiome)
        {
            lock (_lock)
            {
                System.Array.Copy(_density, intoDensity, _density.Length);
                System.Array.Copy(_seed, intoSeed, _seed.Length);
                System.Array.Copy(_biome, intoBiome, _biome.Length);
                return Version;
            }
        }
    }
}
