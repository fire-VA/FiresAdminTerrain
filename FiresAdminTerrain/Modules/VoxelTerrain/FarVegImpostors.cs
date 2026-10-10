using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // FAR VEGETATION BILLBOARDS — past the real vegetation ring the terrain
    // ran bald to the horizon. Each species is BAKED ONCE at load (prefab →
    // ortho render → cutout texture) and drawn as GPU-instanced CROSSED QUADS
    // (16 tris/tree vs. a few-hundred-tri LOD mesh).
    //
    // Cost model (the 22-FPS regression was all here):
    //  • Tiles build ONCE and PERSIST — a 128m tile's placement is a
    //    deterministic per-8m-cell hash over the analytic gen surface, so it
    //    never changes until the terrain is edited. Built tiles stay cached
    //    (LRU-capped); re-entering a zone never rebuilds.
    //  • The build (256 cells × biome/height/forest/wetness sampling) runs on
    //    a BACKGROUND THREAD — the gen samplers are pure reads over immutable
    //    load-time arrays with per-thread memos, so no main-thread hitch.
    //  • Draw batches (per-species Matrix4x4[] chunks) are CACHED and rebuilt
    //    only when the player crosses a tile boundary or a tile arrives — a
    //    standing/turning player just issues a few DrawMeshInstanced calls per
    //    frame with zero allocation. Rendering is GPU-instanced end to end.
    internal static class FarVegImpostors
    {
        public static bool Enabled = true;
        public static float RadiusMeters = 2400f;
        public static float DensityMul = 1f;

        private const float TILE = 128f;
        private const float CELL = 8f;
        private const int BAKE_RES = 256;
        private const int DefaultDrawLayer = 0;
        private const string BakeLightName = "FiresImpostorBakeLight";
        // Direct light plus ambient fill sum to exactly 1.0 on purpose: a diffuse
        // surface cannot then exceed the ARGB32 bake target and clamp towards white,
        // whatever its albedo.
        private const float BakeLightIntensity = 0.8f;
        private const float BakeAmbient = 0.2f;
        private const float BakeCutoff = 0.35f;
        private const byte BakeOpaqueAlpha = 90;     // BakeCutoff in bytes (0.35 * 255 = 89.25)
        private const int BakeBleedPasses = 6;
        private const int BATCH = 1023;      // DrawMeshInstanced hard cap

        // Tile-scan throttle. The heading gate is a dot product against the last scanned forward: 0.9995 is
        // about 1.8 degrees, tight enough that the prebuild cone (already widened by 0.15 over the draw cone)
        // cannot be swept past before the next scan. The safety interval covers LRU eviction, which creates
        // work without moving the player or the camera.
        private const float ScanHeadingCos = 0.9995f;
        private const float ScanSafetySeconds = 0.5f;
        private static int s_scanTx = int.MinValue, s_scanTz = int.MinValue;
        private static Vector2 s_scanFwd = Vector2.zero;
        private static float s_nextScan;
        private const int TILE_CAP_MIN = 1024;
        private const int TILE_CAP_MAX = 24000;  // memory backstop (~roaming safety)

        // The LRU ceiling must be able to HOLD what the radius asks for, or the
        // slider silently stops working: the draw disk alone needs π·r²/TILE²
        // tiles — at the 6000m slider max that's ~6900 against the old fixed
        // 6000 — so the far ring was evicted as fast as it was built and the
        // view distance never grew no matter how far the slider went.
        private static int TileCapFor(float radius, float fwdRadius)
        {
            float disk = Mathf.PI * radius * radius;
            float cone = Mathf.PI * fwdRadius * fwdRadius * 0.25f;   // forward cone ≈ quarter disk
            int need = Mathf.CeilToInt((disk + cone) / (TILE * TILE)) + 256;   // slack for roaming
            return Mathf.Clamp(need, TILE_CAP_MIN, TILE_CAP_MAX);
        }

        private sealed class Species
        {
            public string Prefab;
            public Heightmap.Biome Biomes;
            public float Density;      // chance per 8m cell (pre-DensityMul)
            public float AltMin, AltMax;      // absolute metres (vanilla stores them against sea level)
            public float NormalYMin, NormalYMax;   // cos(m_maxTilt) .. cos(m_minTilt), vanilla's own tilt gate
            public bool Forest;               // m_inForest: the forest factor has to fall in the band below
            public float ForestMin, ForestMax;
            public float Height;              // how tall it stands, for picking which species are worth drawing
            public Mesh Quad;          // crossed quads sized to the prefab (main thread)
            public Material Mat;       // baked billboard, instancing on (main thread)
            public volatile bool Broken;
        }

        // ── THE SPECIES COME FROM THE GAME, NOT FROM A LIST HERE ────────────
        // `ZoneSystem.ZoneVegetation` already carries every rule this needs — the prefab, the biome mask, min/max
        // altitude against sea level, min/max tilt, how many go in a zone, and the forest band — so the species are
        // DERIVED from it at load instead of hand-copied. That covers the Ashlands trees, the Deep North snow firs and
        // the Mistlands shoots without naming any of them, tracks a patch that retunes vanilla's own numbers, and picks
        // up vegetation other mods add. A hand-written table was eight entries, five biomes and already stale.
        private const float SeaLevel = 30f;
        private const float MinImpostorHeight = 4f;   // a fern is knee high; at a kilometre only the big things read
        private const int MaxSpeciesPerBiome = 6;     // the dominant few — every species costs a bake and a draw call
        private const float CellsPerZone = 64f;       // a 64m zone over 8m cells
        private const float SlopeProbe = 8.485f;      // the (+6,+6) probe BuildTile walks

        private static Species[] s_species = new Species[0];

        // Every vegetation entry the game will actually place, tall enough to read at a kilometre, kept to the dominant
        // few per biome. Runs once, on the main thread, before the bake.
        private static bool BuildSpecies()
        {
            var zs = ZoneSystem.instance;
            if (zs == null || zs.m_vegetation == null || zs.m_vegetation.Count == 0) return false;

            var tall = new List<Species>();
            foreach (var veg in zs.m_vegetation)
            {
                if (veg == null || !veg.m_enable || veg.m_prefab == null || veg.m_biome == Heightmap.Biome.None) continue;
                float height = PrefabHeight(veg.m_prefab);
                if (height < MinImpostorHeight) continue;
                float group = Mathf.Max(1f, (veg.m_groupSizeMin + veg.m_groupSizeMax) * 0.5f);
                float perZone = (veg.m_max < 1f ? veg.m_max : (veg.m_min + veg.m_max) * 0.5f) * group;
                float chance = Mathf.Clamp01(perZone / CellsPerZone);
                if (chance <= 0.002f) continue;
                tall.Add(new Species
                {
                    Prefab = veg.m_prefab.name,
                    Biomes = veg.m_biome,
                    Density = chance,
                    AltMin = veg.m_minAltitude + SeaLevel,
                    AltMax = veg.m_maxAltitude + SeaLevel,
                    NormalYMin = Mathf.Cos(Mathf.Deg2Rad * veg.m_maxTilt),
                    NormalYMax = Mathf.Cos(Mathf.Deg2Rad * veg.m_minTilt),
                    Forest = veg.m_inForest,
                    ForestMin = veg.m_forestTresholdMin,
                    ForestMax = veg.m_forestTresholdMax,
                    Height = height,
                });
            }

            var kept = new List<Species>();
            foreach (var biome in s_biomes)
            {
                s_perBiome.Clear();
                foreach (var sp in tall) if ((sp.Biomes & biome) != 0) s_perBiome.Add(sp);
                // The ones that fill a horizon: tall and common beat tall and rare.
                s_perBiome.Sort((a, b) => (b.Height * b.Density).CompareTo(a.Height * a.Density));
                for (int i = 0; i < s_perBiome.Count && i < MaxSpeciesPerBiome; i++)
                    if (!kept.Contains(s_perBiome[i])) kept.Add(s_perBiome[i]);
            }
            if (kept.Count == 0) return false;

            s_species = kept.ToArray();
            s_chunks = new List<Matrix4x4[]>[s_species.Length];
            s_chunkCounts = new List<int>[s_species.Length];
            s_flat = new List<Matrix4x4>[s_species.Length];
            s_byBiome.Clear();
            foreach (var biome in s_biomes)
            {
                var list = new List<int>();
                for (int i = 0; i < s_species.Length; i++) if ((s_species[i].Biomes & biome) != 0) list.Add(i);
                if (list.Count > 0) s_byBiome[biome] = list.ToArray();
            }
            FiresLogger.LogInfo($"[FiresVoxel] far billboards: {s_species.Length} species taken from the game's own "
                + $"{zs.m_vegetation.Count} vegetation entries ({tall.Count} stand over {MinImpostorHeight:0} m).");
            return true;
        }

        private static readonly Heightmap.Biome[] s_biomes =
        {
            Heightmap.Biome.Meadows, Heightmap.Biome.BlackForest, Heightmap.Biome.Swamp, Heightmap.Biome.Mountain,
            Heightmap.Biome.Plains, Heightmap.Biome.Mistlands, Heightmap.Biome.AshLands, Heightmap.Biome.DeepNorth,
        };

        private static readonly List<Species> s_perBiome = new List<Species>();
        private static readonly Dictionary<Heightmap.Biome, int[]> s_byBiome = new Dictionary<Heightmap.Biome, int[]>();

        private static float PrefabHeight(GameObject prefab)
        {
            float low = float.MaxValue, high = float.MinValue;
            foreach (var renderer in prefab.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer is ParticleSystemRenderer) continue;
                var bounds = renderer.bounds;
                low = Mathf.Min(low, bounds.min.y);
                high = Mathf.Max(high, bounds.max.y);
            }
            return high > low ? high - low : 0f;
        }

        private sealed class Tile
        {
            public Matrix4x4[][] PerSpecies;   // built once off-thread — never mutated after
            public Vector3 Center;
            public bool Any;
        }

        private static readonly Dictionary<Vector2Int, Tile> s_tiles = new Dictionary<Vector2Int, Tile>();

        // ── off-thread build plumbing ──
        private static readonly ConcurrentQueue<Vector2Int> s_buildQueue = new ConcurrentQueue<Vector2Int>();
        private static readonly ConcurrentQueue<KeyValuePair<Vector2Int, Tile>> s_doneQueue = new ConcurrentQueue<KeyValuePair<Vector2Int, Tile>>();
        private static readonly HashSet<Vector2Int> s_inFlight = new HashSet<Vector2Int>();
        private static readonly AutoResetEvent s_wake = new AutoResetEvent(false);
        private static Thread s_worker;
        private static volatile bool s_running;

        // ── cached draw batches (rebuilt only on tile-cross / arrival) ──
        // Chunk arrays are POOLED (persistent, always BATCH-sized) and reused;
        // s_chunkCounts holds the live instance count per chunk. Rebuilding no
        // longer allocates (the old `new Matrix4x4[n]` per rebuild churned GC
        // on every camera turn — a real stutter source).
        private static List<Matrix4x4[]>[] s_chunks = new List<Matrix4x4[]>[0];
        private static List<int>[] s_chunkCounts = new List<int>[0];
        private static List<Matrix4x4>[] s_flat = new List<Matrix4x4>[0];
        private static bool s_batchesDirty;
        private static Vector2Int s_lastBatchTile = new Vector2Int(int.MinValue, int.MinValue);
        private static Vector2 s_lastBatchFwd = Vector2.up;
        private static int s_drawnInst;
        private static int s_bandNear, s_bandMid, s_bandFar;
        private static string s_shaderName = "?";
        private static bool s_baked;
        private static long s_bakeOpaquePixels, s_bakeRedSum, s_bakeGreenSum, s_bakeBlueSum;

        // Config hook: Tick already honours Enabled and drops its tiles when it goes false, but
        // nothing ever set the flag, so far billboards could not be turned off at all. The other
        // four settings are re-read every tick; marking the batches dirty makes a change show up
        // on the next frame instead of waiting for the player to move or turn.
        public static void MarkDirty() => s_batchesDirty = true;

        // The per-cell body lifted verbatim out of the old triple loop, so the ring walk that replaced it does
        // exactly the same test in the same order: circular radius disk, else the forward cone, else skip.
        private static void TryQueueTile(int tx, int tz, Vector3 pp, float rad2, float fwd2, Vector2 fwd, float prebuildCos)
        {
            float ex = tx * TILE + TILE * 0.5f - pp.x;
            float ez = tz * TILE + TILE * 0.5f - pp.z;
            float ed2 = ex * ex + ez * ez;
            if (ed2 > rad2)
            {
                if (ed2 > fwd2) return;
                float inv = 1f / Mathf.Sqrt(ed2 > 1e-4f ? ed2 : 1f);
                if (ex * fwd.x * inv + ez * fwd.y * inv < prebuildCos) return;
            }
            var key = new Vector2Int(tx, tz);
            if (s_tiles.ContainsKey(key) || s_inFlight.Contains(key)) return;
            s_inFlight.Add(key);
            s_buildQueue.Enqueue(key);
        }

        public static void Tick()
        {
            s_gen = WorldGenerator.instance;
            if (!Enabled || ZNetScene.instance == null || (!FiresWorldGenBase.Active && s_gen == null))
            {
                if (s_tiles.Count > 0) { s_tiles.Clear(); s_batchesDirty = true; }
                // Forget where the scan last ran. Without this, toggling impostors back on while standing
                // still matches the cached tile and heading, so the first scan waits for the safety interval
                // and the billboards take up to half a second to reappear.
                s_scanTx = int.MinValue; s_scanTz = int.MinValue; s_scanFwd = Vector2.zero; s_nextScan = 0f;
                return;
            }
            var cam = Utils.GetMainCamera();
            var player = Player.m_localPlayer;
            if (cam == null || player == null) return;

            if (!BakeBillboards()) { DrainDone(); return; }
            EnsureWorker();

            Vector3 pp = player.transform.position;
            int ptx = Mathf.FloorToInt(pp.x / TILE), ptz = Mathf.FloorToInt(pp.z / TILE);
            float radius = FiresVoxelConfig.ImpostorRadius?.Value ?? RadiusMeters;
            float fwdRadius = Mathf.Max(radius, FiresVoxelConfig.ImpostorForwardRadius?.Value ?? radius);
            int range = Mathf.CeilToInt(fwdRadius / TILE);
            // Density read on the main thread; the worker's BuildTile uses it.
            DensityMul = FiresVoxelConfig.ImpostorDensity?.Value ?? 0.65f;
            // Camera forward on the XZ plane — drives the "see further where I'm
            // looking" cone below.
            Vector3 f3 = cam.transform.forward;
            Vector2 fwd = new Vector2(f3.x, f3.z);
            fwd = fwd.sqrMagnitude < 1e-4f ? Vector2.up : fwd.normalized;

            // Enqueue any missing tile once (ring order → nearest first), but
            // only the CIRCULAR radius disk plus the forward cone beyond it —
            // NOT the whole forward-radius square in every direction (that built
            // 5000+ tiles, blew the LRU cap, and churned). The prebuild cone is
            // a bit wider than the draw cone so tiles are ready before a turn
            // sweeps them into view.
            // ═══ THE SCAN IS THROTTLED; THE DRAW IS NOT ═══════════════════════
            // Draw batches are already cached and only rebuilt on a tile crossing (see the header), so a
            // standing player's per-frame cost was ENTIRELY this enumeration - and its result depends on
            // nothing but the player's tile, the radius config and the camera heading. Standing still and
            // looking one way, it re-derived the same answer every frame and enqueued nothing.
            //
            // Measured 2026-09-25 on the BlueHills rig: StreamerDriver.Update cost 4.1 ms on EVERY one of
            // 5,076 frames, the second-largest site in the game behind object creation, and this scan is its
            // unthrottled half (FiresVoxel's own Tick is already behind a 0.25 s gate).
            //
            // The periodic re-scan is not belt-and-braces, it is load-bearing: EvictFarTiles can drop a tile
            // that is still in range, which creates new work with no tile crossing and no turn to trigger on.
            bool tileMoved = ptx != s_scanTx || ptz != s_scanTz;
            bool turned = Vector2.Dot(fwd, s_scanFwd) < ScanHeadingCos;
            bool scanDue = Time.time >= s_nextScan;
            if (WorldReady && (tileMoved || turned || scanDue))
            {
                s_scanTx = ptx; s_scanTz = ptz; s_scanFwd = fwd;
                s_nextScan = Time.time + ScanSafetySeconds;

                float rad2 = radius * radius, fwd2 = fwdRadius * fwdRadius;
                const float PREBUILD_COS = CONE_COS - 0.15f;
                // WALK THE RING, DO NOT SCAN THE SQUARE. The old loop iterated (2r+1)^2 cells per ring and
                // threw away everything not on the perimeter: at the 3000 m forward radius (range 24) that is
                // 20,825 iterations to visit the 2,401 cells that are actually on a ring - 8.7x the work, and
                // it ran every frame. Ring order (nearest first) is preserved, which the build queue relies on.
                for (int ring = 0; ring <= range; ring++)
                {
                    if (ring == 0)
                    {
                        TryQueueTile(ptx, ptz, pp, rad2, fwd2, fwd, PREBUILD_COS);
                        continue;
                    }
                    for (int dx = -ring; dx <= ring; dx++)
                    {
                        TryQueueTile(ptx + dx, ptz - ring, pp, rad2, fwd2, fwd, PREBUILD_COS);
                        TryQueueTile(ptx + dx, ptz + ring, pp, rad2, fwd2, fwd, PREBUILD_COS);
                    }
                    for (int dz = -ring + 1; dz <= ring - 1; dz++)
                    {
                        TryQueueTile(ptx - ring, ptz + dz, pp, rad2, fwd2, fwd, PREBUILD_COS);
                        TryQueueTile(ptx + ring, ptz + dz, pp, rad2, fwd2, fwd, PREBUILD_COS);
                    }
                }
                if (!s_buildQueue.IsEmpty) s_wake.Set();
            }

            DrainDone();

            // LRU: over the cap, drop the tiles farthest from the player.
            int tileCap = TileCapFor(radius, fwdRadius);
            if (s_tiles.Count > tileCap) EvictFarTiles(ptx, ptz, tileCap);

            // Rebuild the draw batches on a tile crossing OR when the camera has
            // turned enough that the forward cone points somewhere new (~12°).
            var curTile = new Vector2Int(ptx, ptz);
            if (curTile != s_lastBatchTile) { s_lastBatchTile = curTile; s_batchesDirty = true; }
            if (Vector2.Dot(fwd, s_lastBatchFwd) < 0.978f) { s_lastBatchFwd = fwd; s_batchesDirty = true; }

            if (s_batchesDirty) RebuildBatches(pp, fwd, radius, fwdRadius);

            // Per-frame: just replay the cached instanced batches. No culling,
            // no copies — 16-tri billboards are trivial on the GPU and the
            // stable batch means turning the camera costs nothing.
            for (int si = 0; si < s_species.Length; si++)
            {
                var sp = s_species[si];
                var chunks = s_chunks[si];
                var counts = s_chunkCounts[si];
                if (sp.Broken || sp.Quad == null || sp.Mat == null || chunks == null || counts == null) continue;
                for (int c = 0; c < counts.Count; c++)
                {
                    int n = counts[c];
                    if (n <= 0) continue;
                    try
                    {
                        // LightProbeUsage.Off. The lighting is already IN the baked
                        // sheet, so the default (BlendProbes) re-lights the quad a
                        // second time from whatever ambient probe state the instanced
                        // path is carrying - which Unity does not define for
                        // DrawMeshInstanced without a probe provider.
                        Graphics.DrawMeshInstanced(sp.Quad, 0, sp.Mat, chunks[c], n, null,
                            UnityEngine.Rendering.ShadowCastingMode.Off, false, DefaultDrawLayer, null,
                            UnityEngine.Rendering.LightProbeUsage.Off);
                    }
                    catch (System.Exception ex)
                    {
                        sp.Broken = true;
                        FiresLogger.LogWarning($"[FiresVoxel] far billboard species '{sp.Prefab}' disabled: {ex.Message}");
                        break;
                    }
                }
            }

            // (30s impostor stats + fwd-probe retired — they solved the
            // bare-distant-terrain investigation; re-add from git history if
            // the treeline coverage regresses)
        }

        // Terrain edit → the overlapping tile's placement is stale. Drop it;
        // it rebuilds off-thread on the next Tick. (Impostors live far past
        // reach so this rarely fires, but the contract is "persist until edited".)
        public static void Invalidate(float wx, float wz)
        {
            var key = new Vector2Int(Mathf.FloorToInt(wx / TILE), Mathf.FloorToInt(wz / TILE));
            if (s_tiles.Remove(key)) s_batchesDirty = true;
        }

        private static void DrainDone()
        {
            while (s_doneQueue.TryDequeue(out var kv))
            {
                s_inFlight.Remove(kv.Key);
                s_tiles[kv.Key] = kv.Value;
                s_batchesDirty = true;
            }
        }

        private static readonly List<Vector2Int> s_evict = new List<Vector2Int>();
        private static void EvictFarTiles(int ptx, int ptz, int tileCap)
        {
            s_evict.Clear();
            int overflow = s_tiles.Count - tileCap;
            float fwdRadius = Mathf.Max(FiresVoxelConfig.ImpostorRadius?.Value ?? RadiusMeters,
                                        FiresVoxelConfig.ImpostorForwardRadius?.Value ?? RadiusMeters);
            int keepRing = Mathf.CeilToInt(fwdRadius / TILE) + 2;
            // Cheap approximate LRU: collect everything past the keep ring and
            // drop the overflow-worst. Runs only when the cap is exceeded.
            foreach (var kv in s_tiles)
            {
                int d = Mathf.Max(Mathf.Abs(kv.Key.x - ptx), Mathf.Abs(kv.Key.y - ptz));
                if (d > keepRing) s_evict.Add(kv.Key);
            }
            s_evict.Sort((a, b) =>
                Mathf.Max(Mathf.Abs(b.x - ptx), Mathf.Abs(b.y - ptz))
                    .CompareTo(Mathf.Max(Mathf.Abs(a.x - ptx), Mathf.Abs(a.y - ptz))));
            for (int i = 0; i < s_evict.Count && i < overflow; i++) s_tiles.Remove(s_evict[i]);
            s_batchesDirty = true;
        }

        // CONE_COS = cos(~62°): a ~124° forward wedge gets the extended reach.
        private const float CONE_COS = 0.47f;

        private static void RebuildBatches(Vector3 pp, Vector2 fwd, float radius, float fwdRadius)
        {
            s_batchesDirty = false;
            var zs = ZoneSystem.instance;
            // Start past where the real trees render, else the near billboards
            // just hide behind the real forest and waste frames.
            float nearSkip = Mathf.Max((FiresCore.Bridge.SimulationDistanceAccess.ZoneLoadingNear + 1) * 64f,
                                       FiresVoxelConfig.ImpostorNearSkip?.Value ?? 300f);
            float nearEdge = Mathf.Max(0f, nearSkip - TILE);   // one-tile skirt
            float near2 = nearEdge * nearEdge;
            float near2All = radius * radius, far2 = fwdRadius * fwdRadius;

            for (int si = 0; si < s_species.Length; si++)
                (s_flat[si] ?? (s_flat[si] = new List<Matrix4x4>(8192))).Clear();

            int bandNear = 0, bandMid = 0, bandFar = 0;   // 0-1km / 1-2km / 2km+ verify
            foreach (var kv in s_tiles)
            {
                var tile = kv.Value;
                if (tile == null || !tile.Any) continue;
                float ddx = tile.Center.x - pp.x, ddz = tile.Center.z - pp.z;
                float d2 = ddx * ddx + ddz * ddz;
                if (d2 < near2) continue;                       // inside real veg ring
                // Draw the full circle out to `radius`; between `radius` and
                // `fwdRadius` draw ONLY the forward cone — trees/rocks reach up
                // the mountain you're facing, nothing rendered behind you. Fog
                // (FiresImpostor shader) fades the far edge into haze.
                if (d2 > near2All)
                {
                    if (d2 > far2) continue;
                    float inv = 1f / Mathf.Sqrt(d2 > 1e-4f ? d2 : 1f);
                    if (ddx * fwd.x * inv + ddz * fwd.y * inv < CONE_COS) continue;
                }
                int tileCount = 0;
                for (int si = 0; si < s_species.Length; si++)
                {
                    var arr = tile.PerSpecies[si];
                    if (arr != null && arr.Length > 0) { s_flat[si].AddRange(arr); tileCount += arr.Length; }
                }
                if (d2 < 1_000_000f) bandNear += tileCount;
                else if (d2 < 4_000_000f) bandMid += tileCount;
                else bandFar += tileCount;
            }
            s_bandNear = bandNear; s_bandMid = bandMid; s_bandFar = bandFar;

            int total = 0;
            for (int si = 0; si < s_species.Length; si++)
            {
                var list = s_chunks[si] ?? (s_chunks[si] = new List<Matrix4x4[]>());
                var counts = s_chunkCounts[si] ?? (s_chunkCounts[si] = new List<int>());
                counts.Clear();
                var flat = s_flat[si];
                int ci = 0;
                for (int off = 0; off < flat.Count; off += BATCH, ci++)
                {
                    int n = Mathf.Min(BATCH, flat.Count - off);
                    Matrix4x4[] arr;
                    if (ci < list.Count) arr = list[ci];             // reuse pooled array
                    else { arr = new Matrix4x4[BATCH]; list.Add(arr); }
                    flat.CopyTo(off, arr, 0, n);
                    counts.Add(n);
                }
                total += flat.Count;
            }
            s_drawnInst = total;
        }

        // ── background worker: pure sampling → matrices ──
        private static void EnsureWorker()
        {
            if (s_worker != null) return;
            s_running = true;
            s_worker = new Thread(WorkerLoop) { IsBackground = true, Name = "FiresImpostorBuild" };
            s_worker.Start();
        }

        private static void WorkerLoop()
        {
            while (s_running)
            {
                if (s_buildQueue.IsEmpty) s_wake.WaitOne(1000);
                while (s_buildQueue.TryDequeue(out var key))
                {
                    Tile tile;
                    try { tile = BuildTile(key); }
                    catch { tile = new Tile { PerSpecies = new Matrix4x4[s_species.Length][], Center = TileCenter(key) }; }
                    s_doneQueue.Enqueue(new KeyValuePair<Vector2Int, Tile>(key, tile));
                }
            }
        }

        private static Vector3 TileCenter(Vector2Int key)
            => new Vector3(key.x * TILE + TILE * 0.5f, 0f, key.y * TILE + TILE * 0.5f);

        // The ground under a tile cell, off the main thread. A Fires-generated world answers from its own grid; every
        // other world — vanilla, and every converted chunk world — answers from `WorldGenerator`, which is what made
        // this whole system dead weight before: it stood behind `FiresWorldGenBase.Active` and never ran anywhere else.
        private static volatile WorldGenerator s_gen;

        private static bool SampleGround(float wx, float wz, out Heightmap.Biome biome, out float y)
        {
            if (FiresWorldGenBase.Active)
            {
                biome = FiresWorldGenBase.SampleBiome(wx, wz);
                y = FiresWorldGenBase.SampleFinalMeters(wx, wz);
                return !FiresWorldGenBase.IsWetAt(wx, wz);
            }
            var gen = s_gen;
            if (gen == null) { biome = Heightmap.Biome.None; y = 0f; return false; }
            biome = gen.GetBiome(wx, wz);
            y = gen.GetHeight(wx, wz);
            return biome != Heightmap.Biome.Ocean && biome != Heightmap.Biome.None;
        }

        private static bool WorldReady => FiresWorldGenBase.Active ? FiresWorldGenBase.GridReady : s_gen != null;

        [ThreadStatic] private static List<Matrix4x4>[] t_build;

        private static Tile BuildTile(Vector2Int key)
        {
            var tile = new Tile
            {
                PerSpecies = new Matrix4x4[s_species.Length][],
                Center = TileCenter(key),
            };
            var build = t_build ?? (t_build = new List<Matrix4x4>[s_species.Length]);
            for (int si = 0; si < s_species.Length; si++)
                (build[si] ?? (build[si] = new List<Matrix4x4>(64))).Clear();

            int cells = (int)(TILE / CELL);
            float hSum = 0f; int hCnt = 0;
            for (int cz = 0; cz < cells; cz++)
            {
                for (int cx = 0; cx < cells; cx++)
                {
                    float wx = key.x * TILE + (cx + 0.5f) * CELL;
                    float wz = key.y * TILE + (cz + 0.5f) * CELL;
                    uint h = Hash((int)wx, (int)wz);
                    float r01 = (h & 0xFFFF) / 65536f;

                    if (!SampleGround(wx, wz, out var biome, out float y)) continue;
                    if (y < 28f) continue;
                    if (!s_byBiome.TryGetValue(biome, out var here)) continue;
                    SampleGround(wx + 6f, wz + 6f, out _, out float y2);
                    float dy = Mathf.Abs(y2 - y);
                    // Vanilla gates on the ground NORMAL, so the probe's rise over its run becomes one: a flat cell
                    // reads 1, a cliff reads towards 0, and each species keeps its own cos(m_maxTilt) band.
                    float normalY = SlopeProbe / Mathf.Sqrt(SlopeProbe * SlopeProbe + dy * dy);

                    float forestFactor = WorldGenerator.GetForestFactor(new Vector3(wx, 0f, wz));
                    float biomeCov = BiomeCoverage(biome);
                    float pickR = ((h >> 16) & 0xFFFF) / 65536f;
                    for (int hi = 0; hi < here.Length; hi++)
                    {
                        int si = here[hi];
                        var sp = s_species[si];
                        if (sp.Broken) continue;
                        if (y < sp.AltMin || y > sp.AltMax) continue;
                        if (normalY < sp.NormalYMin || normalY > sp.NormalYMax) continue;
                        if (sp.Forest && (forestFactor < sp.ForestMin || forestFactor > sp.ForestMax)) continue;
                        float density = sp.Density * DensityMul * biomeCov;
                        if (r01 >= density) continue;
                        float jx = (((h >> 8) & 0xFF) / 255f - 0.5f) * CELL;
                        float jz = (((h >> 20) & 0xFF) / 255f - 0.5f) * CELL;
                        float yaw = pickR * 360f;
                        float scale = 0.85f + pickR * 0.5f;
                        build[si].Add(Matrix4x4.TRS(new Vector3(wx + jx, y - 0.2f, wz + jz),
                                      Quaternion.Euler(0f, yaw, 0f), Vector3.one * scale));
                        tile.Any = true;
                        hSum += y; hCnt++;
                        break;
                    }
                }
            }
            for (int si = 0; si < s_species.Length; si++)
                if (build[si].Count > 0) tile.PerSpecies[si] = build[si].ToArray();
            if (hCnt > 0) tile.Center = new Vector3(tile.Center.x, hSum / hCnt, tile.Center.z);
            return tile;
        }

        // ── one-time billboard bake: prefab → ortho render → cutout quad ──
        // A FEW SPECIES A TICK, never the whole list at once. Deriving the species from the game turned a fixed eight
        // into as many as forty, and forty ortho renders plus their readbacks in one frame is a hitch at exactly the
        // moment a join is already expensive. Drawing starts as soon as the first one is baked; the rest arrive over
        // the next couple of seconds ([perf], 2026-09-22).
        private const int BakePerTick = 3;

        private static int s_bakeCursor;
        private static int s_bakedCount;

        // Made on first use, never in a field initializer: a marker built before the profiler subsystem is up is a
        // handle to nothing ([perf], who hit it in EBM).
        private static bool s_markerMade;
        private static Unity.Profiling.ProfilerMarker s_bakeMarkerStorage;

        private static Unity.Profiling.ProfilerMarker BakeMarker()
        {
            if (!s_markerMade)
            {
                s_bakeMarkerStorage = new Unity.Profiling.ProfilerMarker("FAT impostor bake");
                s_markerMade = true;
            }
            return s_bakeMarkerStorage;
        }

        private static bool BakeBillboards()
        {
            if (s_bakeCursor >= s_species.Length && s_species.Length > 0) return s_baked;
            var zns = ZNetScene.instance;
            if (zns == null) return false;
            if (s_species.Length == 0 && !BuildSpecies()) return false;

            // Our own bundle shader first — it is the only one GUARANTEED to
            // carry instancing variants (stock legacy cutouts may not, and a
            // non-instancing material makes DrawMeshInstanced a silent no-op).
            Shader shader = null;
            try { shader = VerdantsAscent.FiresAdminTerrain.Instance?.GetAssetBundle()?.LoadAsset<Shader>("FiresImpostor"); }
            catch { }
            shader = shader
                        ?? Shader.Find("Unlit/Transparent Cutout")
                        ?? Shader.Find("Legacy Shaders/Transparent/Cutout/Diffuse")
                        ?? Shader.Find("Sprites/Default");
            if (shader == null) { FiresLogger.LogWarning("[FiresVoxel] no billboard shader available"); return false; }
            s_shaderName = shader.name;

            const int BAKE_LAYER = 30;
            var camGO = new GameObject("FiresImpostorBakeCam");
            var cam = camGO.AddComponent<Camera>();
            cam.enabled = false;
            cam.orthographic = true;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
            cam.cullingMask = 1 << BAKE_LAYER;
            cam.nearClipPlane = 0.1f;
            cam.allowHDR = false;
            cam.allowMSAA = false;

            var lightGO = new GameObject(BakeLightName);
            var light = lightGO.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = BakeLightIntensity;
            light.color = Color.white;
            light.cullingMask = 1 << BAKE_LAYER;
            light.transform.rotation = Quaternion.Euler(40f, 205f, 0f);

            var rt = new RenderTexture(BAKE_RES, BAKE_RES, 24, RenderTextureFormat.ARGB32);
            int ok = 0;
            int baked = 0;

            // A billboard carries its lighting BAKED IN, so it has to be lit the same
            // way whatever the sky happened to be doing when the bake ran - otherwise
            // a world baked at noon and the same world baked at dusk disagree, and a
            // bright enough moment pushes foliage past 1.0 on an ARGB32 target that has
            // no headroom to absorb it. RGB clamps towards white while the cutout alpha,
            // untouched by lighting, stays perfect - it fails as a WHITE SILHOUETTE.
            //
            // cullingMask above is NOT enough on its own: per-light culling masks are
            // only honoured on the FORWARD path, and this camera inherits whatever path
            // the player settings resolve to. Forcing the path is the wrong fix - a
            // vanilla foliage shader with no forward pass would then bake nothing at
            // all - so BakeEnvironment switches the scene's own directionals off for the
            // render instead, which is correct on either path.
            BakeEnvironment env = null;
            try
            {
                env = BakeEnvironment.Capture();
                env.Apply();
                using (BakeMarker().Auto())
                for (; s_bakeCursor < s_species.Length && baked < BakePerTick; s_bakeCursor++)
                {
                    var sp = s_species[s_bakeCursor];
                    if (sp.Broken || sp.Quad != null) continue;
                    baked++;
                    var prefab = zns.GetPrefab(sp.Prefab);
                    if (prefab == null) { sp.Broken = true; continue; }
                    GameObject inst = null, bakeRoot = null;
                    try
                    {
                        // Instantiate under an INACTIVE root so no Awake runs - a
                        // live ZNetView registers in ZNetScene.m_instances and our
                        // Destroy would orphan the entry (stale-instance diagnostic
                        // spam for every species). Strip all MonoBehaviours before
                        // activating; renderers are not MonoBehaviours, so the mesh
                        // still bakes.
                        bakeRoot = new GameObject("FiresImpostorBakeRoot");
                        bakeRoot.transform.position = new Vector3(0f, -6000f, 0f);
                        bakeRoot.SetActive(false);
                        inst = UnityEngine.Object.Instantiate(prefab, bakeRoot.transform);
                        inst.transform.localPosition = Vector3.zero;
                        foreach (var mb in inst.GetComponentsInChildren<MonoBehaviour>(true))
                            try { UnityEngine.Object.DestroyImmediate(mb); } catch { }
                        foreach (var t in inst.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = BAKE_LAYER;
                        bakeRoot.SetActive(true);

                        var bounds = new Bounds(inst.transform.position, Vector3.zero);
                        bool got = false;
                        foreach (var r in inst.GetComponentsInChildren<Renderer>())
                        {
                            if (r is ParticleSystemRenderer) continue;
                            if (!got) { bounds = r.bounds; got = true; } else bounds.Encapsulate(r.bounds);
                        }
                        if (!got) { sp.Broken = true; continue; }

                        float w = Mathf.Max(bounds.size.x, bounds.size.z);
                        float hgt = bounds.size.y;
                        float half = Mathf.Max(w, hgt) * 0.5f * 1.05f;
                        cam.orthographicSize = half;
                        cam.farClipPlane = w * 4f + 50f;
                        cam.transform.position = bounds.center - new Vector3(0f, 0f, w * 2f + 10f);
                        cam.transform.rotation = Quaternion.identity;
                        cam.targetTexture = rt;
                        cam.Render();

                        RenderTexture.active = rt;
                        var tex = new Texture2D(BAKE_RES, BAKE_RES, TextureFormat.RGBA32, true);
                        tex.ReadPixels(new Rect(0, 0, BAKE_RES, BAKE_RES), 0, 0);
                        RenderTexture.active = null;

                        var pixels = tex.GetPixels32();
                        Measure(pixels);
                        BleedIntoSurround(pixels);
                        tex.SetPixels32(pixels);
                        tex.Apply(true, false);
                        tex.wrapMode = TextureWrapMode.Clamp;

                        sp.Mat = new Material(shader) { mainTexture = tex };
                        if (sp.Mat.HasProperty("_Cutoff")) sp.Mat.SetFloat("_Cutoff", BakeCutoff);
                        sp.Mat.enableInstancing = true;

                        // Crossed quads sized to the baked frame (square, side =
                        // 2*half), pivot at the prefab's base so y sits on ground.
                        float baseY = bounds.min.y - inst.transform.position.y;
                        float side = half * 2f;
                        float y0 = baseY + (bounds.center.y - inst.transform.position.y) - half;
                        sp.Quad = BuildCrossedQuads(side, y0);
                        ok++;
                    }
                    catch (System.Exception ex)
                    {
                        sp.Broken = true;
                        FiresLogger.LogWarning($"[FiresVoxel] billboard bake failed for '{sp.Prefab}': {ex.Message}");
                    }
                    finally
                    {
                        if (bakeRoot != null) UnityEngine.Object.Destroy(bakeRoot);
                    }
                }
            }
            finally
            {
                if (env != null) env.Restore();
                cam.targetTexture = null;
                UnityEngine.Object.Destroy(rt);
                UnityEngine.Object.Destroy(camGO);
                UnityEngine.Object.Destroy(lightGO);
            }
            s_bakedCount += ok;
            if (ok > 0) s_baked = true;
            if (s_bakeCursor >= s_species.Length)
                FiresLogger.LogInfo($"[FiresVoxel] far billboards baked: {s_bakedCount} of {s_species.Length} species "
                    + $"(16 tris each, {BakePerTick} a tick), shader={s_shaderName}{ColourSummary()}.");
            return s_baked;
        }

        // The scene lighting a bake must NOT inherit. Ambient and fog are global, and
        // EnvMan's sun reaches the subject regardless of culling mask on the deferred
        // path, so all of it is captured, overridden for the render, and put back.
        // EVERY scene directional is taken, not just EnvMan.m_dirLight, because our own
        // sky mods add their own. Point lights are ignored on purpose: the subject sits
        // 6000m down, far outside any torch's range.
        private sealed class BakeEnvironment
        {
            private bool m_fog;
            private UnityEngine.Rendering.AmbientMode m_ambientMode;
            private Color m_ambientLight;
            private float m_ambientIntensity;
            private readonly List<Light> m_switchedOff = new List<Light>();

            internal static BakeEnvironment Capture()
            {
                return new BakeEnvironment
                {
                    m_fog = RenderSettings.fog,
                    m_ambientMode = RenderSettings.ambientMode,
                    m_ambientLight = RenderSettings.ambientLight,
                    m_ambientIntensity = RenderSettings.ambientIntensity,
                };
            }

            internal void Apply()
            {
                RenderSettings.fog = false;
                RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
                RenderSettings.ambientLight = new Color(BakeAmbient, BakeAmbient, BakeAmbient, 1f);
                RenderSettings.ambientIntensity = 1f;

                foreach (Light other in UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
                {
                    if (other == null || !other.enabled) continue;
                    if (other.type != LightType.Directional) continue;
                    if (other.gameObject.name == BakeLightName) continue;
                    other.enabled = false;
                    m_switchedOff.Add(other);
                }
            }

            internal void Restore()
            {
                foreach (Light other in m_switchedOff)
                    if (other != null) other.enabled = true;
                m_switchedOff.Clear();
                RenderSettings.fog = m_fog;
                RenderSettings.ambientMode = m_ambientMode;
                RenderSettings.ambientLight = m_ambientLight;
                RenderSettings.ambientIntensity = m_ambientIntensity;
            }
        }

        // Pushes opaque colour outwards into the transparent surround, leaving alpha
        // alone. The bake clears to transparent BLACK and Apply builds the mip chain by
        // averaging RGB across every pixel regardless of alpha - so a billboard, which
        // at a kilometre is nearly always sampled from a high mip, faded towards black
        // around its edges. Filling the surround with the colour beside it makes those
        // mips average foliage with foliage instead of foliage with black.
        private static void BleedIntoSurround(Color32[] pixels)
        {
            var filled = new bool[pixels.Length];
            for (int i = 0; i < pixels.Length; i++) filled[i] = pixels[i].a >= BakeOpaqueAlpha;

            var spread = new List<int>();
            for (int pass = 0; pass < BakeBleedPasses; pass++)
            {
                spread.Clear();
                for (int y = 0; y < BAKE_RES; y++)
                    for (int x = 0; x < BAKE_RES; x++)
                    {
                        int i = y * BAKE_RES + x;
                        if (filled[i]) continue;
                        int r = 0, g = 0, b = 0, n = 0;
                        AddNeighbour(pixels, filled, x - 1, y, ref r, ref g, ref b, ref n);
                        AddNeighbour(pixels, filled, x + 1, y, ref r, ref g, ref b, ref n);
                        AddNeighbour(pixels, filled, x, y - 1, ref r, ref g, ref b, ref n);
                        AddNeighbour(pixels, filled, x, y + 1, ref r, ref g, ref b, ref n);
                        if (n == 0) continue;
                        pixels[i] = new Color32((byte)(r / n), (byte)(g / n), (byte)(b / n), pixels[i].a);
                        spread.Add(i);
                    }
                // Marked only AFTER the pass, so colour advances one ring at a time
                // instead of smearing across the sheet in scan order.
                if (spread.Count == 0) break;
                for (int k = 0; k < spread.Count; k++) filled[spread[k]] = true;
            }
        }

        private static void AddNeighbour(Color32[] pixels, bool[] filled, int x, int y,
                                        ref int r, ref int g, ref int b, ref int n)
        {
            if (x < 0 || y < 0 || x >= BAKE_RES || y >= BAKE_RES) return;
            int i = y * BAKE_RES + x;
            if (!filled[i]) return;
            r += pixels[i].r; g += pixels[i].g; b += pixels[i].b; n++;
        }

        // What the bake camera actually produced, counting only the pixels the cutout
        // keeps so the transparent surround cannot drag the average. Reported once on
        // the bake summary line: foliage reads as greens and browns well short of 255,
        // and a mean near white is the signature of a bake that clamped.
        private static void Measure(Color32[] pixels)
        {
            for (int i = 0; i < pixels.Length; i++)
            {
                Color32 p = pixels[i];
                if (p.a < BakeOpaqueAlpha) continue;
                s_bakeOpaquePixels++;
                s_bakeRedSum += p.r;
                s_bakeGreenSum += p.g;
                s_bakeBlueSum += p.b;
            }
        }

        private static string ColourSummary()
        {
            if (s_bakeOpaquePixels <= 0) return ", no opaque pixels sampled";
            long n = s_bakeOpaquePixels;
            return $", mean baked RGB ({s_bakeRedSum / n},{s_bakeGreenSum / n},{s_bakeBlueSum / n})";
        }

        // Two perpendicular quads, both windings (visible from every side):
        // 16 triangles total.
        private static Mesh BuildCrossedQuads(float side, float y0)
        {
            float hw = side * 0.5f;
            float y1 = y0 + side;
            var verts = new Vector3[16];
            var uvs = new Vector2[16];
            var tris = new int[24];
            int v = 0, t = 0;
            void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
            {
                verts[v] = a; verts[v + 1] = b; verts[v + 2] = c; verts[v + 3] = d;
                uvs[v] = new Vector2(0, 0); uvs[v + 1] = new Vector2(1, 0);
                uvs[v + 2] = new Vector2(1, 1); uvs[v + 3] = new Vector2(0, 1);
                tris[t] = v; tris[t + 1] = v + 2; tris[t + 2] = v + 1;
                tris[t + 3] = v; tris[t + 4] = v + 3; tris[t + 5] = v + 2;
                v += 4; t += 6;
            }
            Quad(new Vector3(-hw, y0, 0), new Vector3(hw, y0, 0), new Vector3(hw, y1, 0), new Vector3(-hw, y1, 0));
            Quad(new Vector3(hw, y0, 0), new Vector3(-hw, y0, 0), new Vector3(-hw, y1, 0), new Vector3(hw, y1, 0));
            Quad(new Vector3(0, y0, -hw), new Vector3(0, y0, hw), new Vector3(0, y1, hw), new Vector3(0, y1, -hw));
            Quad(new Vector3(0, y0, hw), new Vector3(0, y0, -hw), new Vector3(0, y1, -hw), new Vector3(0, y1, hw));
            var mesh = new Mesh { name = "FiresImpostorQuads" };
            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.triangles = tris;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        // Per-biome continuous-coverage multiplier (DonegalHorizonLift model —
        // each biome gets its own treeline density instead of one flat rate).
        // Mountain is lifted because sparse rock on white snow read as bare;
        // open Plains is thinned. Multiplies the per-species chance.
        private static float BiomeCoverage(Heightmap.Biome b)
        {
            switch (b)
            {
                case Heightmap.Biome.Mountain:    return 1.35f;
                case Heightmap.Biome.BlackForest: return 1.15f;
                case Heightmap.Biome.Mistlands:   return 1.1f;
                case Heightmap.Biome.Swamp:       return 1.05f;
                case Heightmap.Biome.Plains:      return 0.8f;
                case Heightmap.Biome.AshLands:    return 0.7f;
                default:                          return 1f;   // Meadows / DeepNorth / rest
            }
        }

        private static uint Hash(int x, int z)
        {
            unchecked
            {
                uint h = (uint)(x * 374761393) ^ (uint)(z * 668265263) ^ 0x9E3779B9u;
                h ^= h >> 13; h *= 1274126177u; h ^= h >> 16;
                return h;
            }
        }
    }
}
