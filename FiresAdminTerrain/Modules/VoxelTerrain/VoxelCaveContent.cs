using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // Phase 2 — populate generated caves.
    //
    // The reusable piece here is the VOID SCAN: walking a column of voxel
    // density and reporting every air pocket between solid, with its floor,
    // ceiling and headroom. Everything cave-interior (this, and the Phase 3
    // water pools) places off that one query rather than re-deriving geometry.
    //
    // Ownership: the SERVER places, once per zone, and clients receive the
    // spawns through normal ZDO replication — the same contract as vanilla
    // vegetation. Two peers must never both spawn, and a restart must not
    // double up, so placement is idempotent: before touching a zone we ask
    // ZDOMan whether our prefabs are already present in that sector.
    internal static class VoxelCaveContent
    {
        // A single air pocket in a column.
        internal struct Void
        {
            public float Floor;      // world Y of the solid surface underfoot
            public float Ceiling;    // world Y of the rock overhead
            public float Headroom => Ceiling - Floor;
        }

        private const float STEP = 1f;          // column scan resolution (m)
        private const float PASSABLE = 2.0f;    // headroom a player needs
        private const float CEILING_CLEAR = 2.6f; // hang roots only above this, so they don't block
        private const float CAVERN_CLEAR = 5.5f;  // "larger cavern" headroom for boulders
        private const float SAMPLE_SPACING = 4f;  // XZ spacing of candidate columns

        private static readonly HashSet<Vector2i> s_done = new HashSet<Vector2i>();
        private static readonly List<Void> s_voids = new List<Void>(8);
        private static readonly List<ZDO> s_zdoScratch = new List<ZDO>();
        private static int[] s_markerHashes;

        // Every air pocket in the column at (wx,wz) between the surface and the
        // cave floor limit, deepest-first scan. Reads the live voxel density —
        // the same data the mesh was built from — so a reported floor is the
        // floor the player will actually stand on.
        internal static void ScanVoids(float wx, float wz, float surfaceY, List<Void> into)
        {
            into.Clear();
            float bottom = FiresVoxelConfig.CaveMinY.Value - 4f;
            float top = surfaceY - 0.5f;   // never report the open sky as a void
            if (top <= bottom) return;

            bool inAir = false;
            float airTop = 0f;
            for (float y = top; y >= bottom; y -= STEP)
            {
                bool solid = IsSolid(wx, y, wz);
                if (!solid && !inAir) { inAir = true; airTop = y; }
                else if (solid && inAir)
                {
                    inAir = false;
                    // y is the first solid sample going down = the floor.
                    into.Add(new Void { Floor = y + STEP * 0.5f, Ceiling = airTop });
                }
            }
        }

        private static bool IsSolid(float wx, float wy, float wz)
        {
            var key = new Vector3Int(
                FloorDiv(Mathf.FloorToInt(wx), VoxelChunk.CELLS),
                FloorDiv(Mathf.FloorToInt(wy), VoxelChunk.CELLS),
                FloorDiv(Mathf.FloorToInt(wz), VoxelChunk.CELLS));
            if (!VoxelWorld.TryGetChunk(key, out var chunk) || chunk == null) return true;
            sbyte d = chunk.SampleDensityWorld(new Vector3(wx, wy, wz));
            if (d == sbyte.MinValue) return true;   // outside the field — treat as rock
            return d > 0;
        }

        private static int FloorDiv(int a, int b) => a >= 0 ? a / b : ~(~a / b);

        // ScanVoids works at 1m granularity, so the floor it reports can sit up
        // to a metre under the real surface — anything placed there visibly
        // hangs in the air. Ask the voxel field for the exact solid→air crossing
        // in that band and settle onto THAT. Everything placed on a cave floor
        // (mushrooms, bones, boulders, and chests when they land) goes through
        // this; nothing should use the raw scan floor.
        private static float SettleToFloor(float wx, float wz, float scanFloor, float ceiling)
        {
            float top = Mathf.Min(ceiling - 0.1f, scanFloor + 1.5f);
            float bot = scanFloor - 1.5f;
            if (top > bot
                && VoxelWorld.TrySampleSurfaceY(wx, wz, top, bot, out float surf, out _))
                return surf;
            return scanFloor;
        }

        public static void PlaceForZone(Vector2i zone)
        {
            if (FiresVoxelConfig.CaveContentEnabled == null || !FiresVoxelConfig.CaveContentEnabled.Value) return;
            if (!FiresVoxelConfig.GenerateCaves.Value) return;
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;   // ONE writer
            if (ZNetScene.instance == null) return;
            if (!s_done.Add(zone)) return;

            var floorPrefabs = Resolve(FiresVoxelConfig.CaveMushroomPrefabs?.Value);
            var ceilPrefabs = Resolve(FiresVoxelConfig.CaveCeilingPrefabs?.Value);
            var rockPrefabs = Resolve(FiresVoxelConfig.CaveRockPrefabs?.Value);
            var bonePrefabs = Resolve(FiresVoxelConfig.CaveBonePrefabs?.Value);
            if (floorPrefabs.Count == 0 && ceilPrefabs.Count == 0
                && rockPrefabs.Count == 0 && bonePrefabs.Count == 0) return;

            // Restart safety: if this zone already holds our markers, a previous
            // session placed them and the ZDOs persisted. Never place twice.
            if (AlreadyPopulated(zone, floorPrefabs, ceilPrefabs, rockPrefabs, bonePrefabs)) return;

            float density = FiresVoxelConfig.CaveContentDensity?.Value ?? 1f;
            if (density <= 0f) return;

            // Phase 3 — the cavern low point this zone would pool at. Tracked
            // during the scan below rather than in a second pass: the deepest
            // floor with real headroom IS the basin, and water finds the low
            // point on its own from there.
            bool havePool = false;
            Vector3 poolAt = Vector3.zero;
            float poolHeadroom = FiresVoxelConfig.CaveWaterMinHeadroom?.Value ?? 5.5f;

            // Deterministic per zone so a re-place after a wipe reproduces the
            // same cave dressing rather than reshuffling it.
            var rng = new System.Random(zone.x * 73856093 ^ zone.y * 19349663);
            float minX = zone.x * 64f - 32f, minZ = zone.y * 64f - 32f;
            int placed = 0;

            for (float oz = 2f; oz < 64f; oz += SAMPLE_SPACING)
            {
                for (float ox = 2f; ox < 64f; ox += SAMPLE_SPACING)
                {
                    float wx = minX + ox + (float)rng.NextDouble() * 2f - 1f;
                    float wz = minZ + oz + (float)rng.NextDouble() * 2f - 1f;
                    if (!Heightmap.GetHeight(new Vector3(wx + 0.013f, 0f, wz + 0.013f), out float surf)) continue;

                    ScanVoids(wx, wz, surf, s_voids);
                    for (int i = 0; i < s_voids.Count; i++)
                    {
                        var v = s_voids[i];
                        if (v.Headroom < PASSABLE) continue;   // not a space a player can use

                        // Deepest cavern floor in the zone wins the pool.
                        if (v.Headroom >= poolHeadroom && (!havePool || v.Floor < poolAt.y))
                        {
                            havePool = true;
                            poolAt = new Vector3(wx, v.Floor, wz);
                        }

                        // Floor dressing: mushrooms in clumps of 1-3. Each stalk
                        // settles at ITS OWN jittered position — a clump sharing
                        // one height straddles uneven rock and half of it floats.
                        float mushChance = FiresVoxelConfig.CaveMushroomChance?.Value ?? 0.10f;
                        if (floorPrefabs.Count > 0 && rng.NextDouble() < mushChance * density)
                        {
                            int clump = 1 + rng.Next(3);
                            var pf = floorPrefabs[rng.Next(floorPrefabs.Count)];
                            for (int c = 0; c < clump; c++)
                            {
                                float jx = wx + (float)(rng.NextDouble() * 2.0 - 1.0);
                                float jz = wz + (float)(rng.NextDouble() * 2.0 - 1.0);
                                float jy = SettleToFloor(jx, jz, v.Floor, v.Ceiling);
                                if (Spawn(pf, new Vector3(jx, jy, jz), RandomYaw(rng))) placed++;
                            }
                        }

                        // Skeletal remains — a rare find, not decoration.
                        float boneChance = FiresVoxelConfig.CaveBoneChance?.Value ?? 0.05f;
                        if (bonePrefabs.Count > 0 && rng.NextDouble() < boneChance * density)
                        {
                            var pf = bonePrefabs[rng.Next(bonePrefabs.Count)];
                            float by = SettleToFloor(wx, wz, v.Floor, v.Ceiling);
                            if (Spawn(pf, new Vector3(wx, by, wz), RandomYaw(rng))) placed++;
                        }

                        // Boulders: larger caverns only, shrunk to rubble scale —
                        // surface rock prefabs are sized for open terrain.
                        if (rockPrefabs.Count > 0 && v.Headroom >= CAVERN_CLEAR
                            && rng.NextDouble() < 0.10 * density)
                        {
                            var pf = rockPrefabs[rng.Next(rockPrefabs.Count)];
                            float ry = SettleToFloor(wx, wz, v.Floor, v.Ceiling);
                            float scale = FiresVoxelConfig.CaveRockScale?.Value ?? 0.45f;
                            if (Spawn(pf, new Vector3(wx, ry, wz), RandomYaw(rng), scale)) placed++;
                        }

                        // Ceiling roots — only where the player still fits under.
                        if (ceilPrefabs.Count > 0 && v.Headroom >= CEILING_CLEAR
                            && rng.NextDouble() < 0.16 * density)
                        {
                            var pf = ceilPrefabs[rng.Next(ceilPrefabs.Count)];
                            if (Spawn(pf, new Vector3(wx, v.Ceiling - 0.15f, wz), RandomYaw(rng))) placed++;
                        }
                    }
                }
            }

            bool pooled = havePool && TrySeedCavePool(poolAt, rng);

            if (placed > 0 || pooled)
                Debug.Log($"[FiresVoxel] zone ({zone.x},{zone.y}) cave content: {placed} object(s) placed"
                    + (pooled ? $", pool seeded at ({poolAt.x:0},{poolAt.y:0},{poolAt.z:0})" : "") + ".");
        }

        // Seed a FiresLiquid pour at a cavern's low point. Deliberately reuses
        // the normal pour rather than inventing cave-specific water: a pour
        // drapes the carved floor through the terrain layer, settles to the
        // surrounding rock, and is a CustomWaterBase — so it reports through
        // CustomWaterManager.GetWaterLevelAt and swimming works with no extra
        // code, identically to every other water in the world. Passages lying
        // below the settled level flood on their own, which is where the
        // submerged routes come from.
        private static bool TrySeedCavePool(Vector3 at, System.Random rng)
        {
            if (FiresVoxelConfig.CaveWaterEnabled == null || !FiresVoxelConfig.CaveWaterEnabled.Value) return false;
            float chance = FiresVoxelConfig.CaveWaterChance?.Value ?? 0.35f;
            if (chance <= 0f || rng.NextDouble() >= chance) return false;

            // Idempotent like every other pour placement — a pour already near
            // this spot means a previous session (or the hydrology pass) owns it.
            try
            {
                if (VerdantsAscent.Water.Core.FiresLiquidWaterPrefab.HasPourNear(at, 24f)) return false;
                float radius = FiresVoxelConfig.CaveWaterRadius?.Value ?? 8f;
                // Sit the source just off the floor so it settles downward into
                // the basin instead of spawning inside the rock.
                var p = new Vector3(at.x, at.y + 0.6f, at.z);
                return VerdantsAscent.Water.Core.FiresLiquidWaterPrefab.Spawn(p, radius) != null;
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[FiresVoxel] cave pool seed failed at ({at.x:0},{at.z:0}): {ex.Message}");
                return false;
            }
        }

        private static Quaternion RandomYaw(System.Random rng)
            => Quaternion.Euler(0f, (float)rng.NextDouble() * 360f, 0f);

        private static bool Spawn(GameObject prefab, Vector3 pos, Quaternion rot, float scale = 1f)
        {
            if (prefab == null) return false;
            try
            {
                var go = Object.Instantiate(prefab, pos, rot);
                if (go == null) return false;
                if (scale != 1f) go.transform.localScale = prefab.transform.localScale * scale;
                return true;
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[FiresVoxel] cave content spawn failed for '{prefab.name}': {ex.Message}");
                return false;
            }
        }

        private static readonly Dictionary<string, List<GameObject>> s_resolved = new Dictionary<string, List<GameObject>>();

        private static List<GameObject> Resolve(string csv)
        {
            if (string.IsNullOrEmpty(csv)) return new List<GameObject>();
            if (s_resolved.TryGetValue(csv, out var cached)) return cached;

            var list = new List<GameObject>();
            foreach (var raw in csv.Split(','))
            {
                var name = raw.Trim();
                if (name.Length == 0) continue;
                var pf = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(name) : null;
                if (pf == null)
                {
                    Debug.LogWarning($"[FiresVoxel] cave content: prefab '{name}' not found — skipped.");
                    continue;
                }
                list.Add(pf);
            }
            s_resolved[csv] = list;
            return list;
        }

        // Idempotency against a restart: our spawns persist as ZDOs, so if the
        // zone's sector already holds any of them we must not place again.
        private static bool AlreadyPopulated(Vector2i zone, params List<GameObject>[] sets)
        {
            if (ZDOMan.instance == null) return true;   // can't verify → don't risk doubling
            if (s_markerHashes == null)
            {
                var hashes = new List<int>();
                foreach (var set in sets)
                    foreach (var pf in set)
                        if (pf != null) hashes.Add(pf.name.GetStableHashCode());
                s_markerHashes = hashes.ToArray();
            }
            if (s_markerHashes.Length == 0) return false;

            s_zdoScratch.Clear();
            ZDOMan.instance.FindSectorObjects(zone, 0, 0, s_zdoScratch);
            for (int i = 0; i < s_zdoScratch.Count; i++)
            {
                var z = s_zdoScratch[i];
                if (z == null) continue;
                int p = z.GetPrefab();
                for (int h = 0; h < s_markerHashes.Length; h++)
                    if (s_markerHashes[h] == p) return true;
            }
            return false;
        }

        public static void ForgetZone(Vector2i zone) => s_done.Remove(zone);
    }
}
