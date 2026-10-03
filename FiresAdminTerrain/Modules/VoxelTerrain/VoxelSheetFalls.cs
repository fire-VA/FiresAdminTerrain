using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace VerdantsAscent.Terrain.Voxel
{
    // Falling water wherever a chunk's stored water steps down: a pond into the lake beside it, a channel dropping to
    // a lower reach, a sheet ending over an edge. It reads the column store the sheet and the swim gate use, so every
    // client hangs the same curtains, and each curtain is shaped to the terrain below by FiresWaterfalls' profile.
    // Built inside the sheet's own apply and parented to its object, so it lives and dies with the sheet.
    internal static class VoxelSheetFalls
    {
        private const float StepMin = 0.6f;
        private const float DropMin = 1f;
        private const float GroundPlunge = 0.3f;
        private const float SoundMinLength = 1.5f;
        private const float SoundMinWidth = 2f;
        private const int SoundsPerChunk = 3;
        private const float SkylandLevelMatch = 0.01f;
        private const float SkylandStepReach = 20f;
        private const float SkylandReportSeconds = 10f;
        private const int MistSitesPerChunk = 3;

        private static readonly List<FiresWaterfallMist.Site> s_mistSites = new List<FiresWaterfallMist.Site>(16);

        private static int s_skylandSheets, s_skylandKept, s_skylandBuried;
        private static float s_nextSkylandReport;

        private struct Lip
        {
            public int X, Z;
            public float Level, Target;
            public Vector3 Outward;
            public bool ToWater;
        }

        private static readonly List<Lip> s_lips = new List<Lip>(128);
        private static readonly int[] s_lipAt = new int[VoxelChunk.SAMPLES * VoxelChunk.SAMPLES];
        private static bool[] s_used = new bool[256];
        private static readonly List<int> s_path = new List<int>(64);
        private static readonly List<int> s_next = new List<int>(8);
        private static readonly List<FiresWaterfalls.Column> s_columns = new List<FiresWaterfalls.Column>(64);
        private static readonly List<Vector3> s_sites = new List<Vector3>(SoundsPerChunk);
        private static readonly List<KeyValuePair<float, Vector3>> s_candidates = new List<KeyValuePair<float, Vector3>>(16);

        // The curtains of one chunk's water, as a mesh on `falls` (created under `water` on first use). Returns the
        // vertex count; 0 leaves the falls object inactive.
        internal static int Build(MeshBuffers buf, GameObject water, ref GameObject falls, ref Mesh mesh)
        {
            var material = FiresWaterfalls.Material;
            if (material == null || water == null) return 0;
            CollectLips(buf);
            ReportSkylands();
            if (s_lips.Count == 0)
            {
                if (falls != null) falls.SetActive(false);
                return 0;
            }

            if (s_used.Length < s_lips.Count) s_used = new bool[s_lips.Count * 2];
            System.Array.Clear(s_used, 0, s_lips.Count);
            FiresWaterfalls.BeginMesh();
            s_candidates.Clear();
            s_mistSites.Clear();
            int curtains = 0;
            for (int pass = 0; pass < 2; pass++)
            {
                for (int i = 0; i < s_lips.Count; i++)
                {
                    if (s_used[i]) continue;
                    if (pass == 0 && CountUnusedNeighbours(i) > 1) continue;
                    Walk(i);
                    Append(buf, curtains++);
                }
            }

            if (falls == null)
            {
                falls = new GameObject("falls");
                falls.layer = 0;
                falls.transform.SetParent(water.transform, false);
                mesh = new Mesh { name = "genwater_falls" };
                falls.AddComponent<MeshFilter>().sharedMesh = mesh;
                var renderer = falls.AddComponent<MeshRenderer>();
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                renderer.sharedMaterials = FiresWaterfalls.Materials;
                VoxelRenderGroup.Apply(renderer);
            }
            int vertices = FiresWaterfalls.EndMesh(mesh);
            falls.SetActive(vertices > 0);

            s_candidates.Sort((a, b) => b.Key.CompareTo(a.Key));
            s_sites.Clear();
            for (int i = 0; i < s_candidates.Count && i < SoundsPerChunk; i++) s_sites.Add(s_candidates[i].Value);
            FiresWaterfalls.SetSounds(falls.transform, s_sites);

            s_mistSites.Sort((a, b) => (b.Drop * b.Width).CompareTo(a.Drop * a.Width));
            if (s_mistSites.Count > MistSitesPerChunk) s_mistSites.RemoveRange(MistSitesPerChunk, s_mistSites.Count - MistSitesPerChunk);
            FiresWaterfallMist.SetEmitters(falls.transform, s_mistSites);
            return vertices;
        }

        // Every column of this chunk whose water stands above a neighbour: a lower wet column by StepMin, or dry
        // ground by DropMin. Its outward direction is the drop-weighted mean of those neighbours' directions, and it
        // falls into the lowest water it borders, else onto the lowest ground. Skyland water only steps down into its
        // own lower water: see IsSkylandWater.
        private static void CollectLips(MeshBuffers buf)
        {
            s_lips.Clear();
            System.Array.Clear(s_lipAt, 0, s_lipAt.Length);
            int S = VoxelChunk.SAMPLES, C = VoxelChunk.CELLS;
            bool skyChunk = FiresSkyIslands.ChunkOverlaps(new Vector3(buf.OriginX, buf.OriginY, buf.OriginZ));
            bool skylandSheet = false;
            for (int lz = 0; lz < C; lz++)
            {
                for (int lx = 0; lx < C; lx++)
                {
                    int i = (lx + 1) + S * (lz + 1);
                    float hi = buf.WaterLvl[i];
                    if (hi <= 0f) continue;
                    bool skyland = skyChunk && IsSkylandWater(buf, lx, lz, hi);
                    if (skyland && !OwnsLevel(buf, hi)) continue;
                    skylandSheet |= skyland;
                    bool buried = false;
                    Vector3 outward = Vector3.zero;
                    float target = float.MaxValue;
                    bool toWater = false;
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dz == 0) continue;
                            int n = (lx + 1 + dx) + S * (lz + 1 + dz);
                            float lo = buf.WaterLvl[n];
                            bool water = lo > 0f;
                            float floor = water ? lo : buf.Ground[n];
                            float drop = hi - floor;
                            if (drop < (water ? StepMin : DropMin)) continue;
                            if (skyland && (!water || drop > SkylandStepReach))
                            {
                                buried = true;
                                continue;
                            }
                            outward += new Vector3(dx, 0f, dz).normalized * drop;
                            if (water)
                            {
                                if (!toWater || floor < target) target = floor;
                                toWater = true;
                            }
                            else if (!toWater && floor < target) target = floor;
                        }
                    }
                    if (outward.sqrMagnitude < 1e-4f)
                    {
                        if (buried) s_skylandBuried++;
                        continue;
                    }
                    if (skyland) s_skylandKept++;
                    s_lips.Add(new Lip { X = lx, Z = lz, Level = hi, Target = target, Outward = outward.normalized, ToWater = toWater });
                    s_lipAt[i] = s_lips.Count;
                }
            }
            if (skylandSheet) s_skylandSheets++;
        }

        // Water the store took from a zone skyland's own ponds and rivers. The store's ground under it is the world far
        // below, so every bank looked like a cliff and hung a curtain out of the rock. It leaves the skyland only at a
        // river or rill mouth, where ZoneModeSkylands hangs the skyland's own fall.
        private static bool IsSkylandWater(MeshBuffers buf, int lx, int lz, float level)
        {
            float wx = buf.OriginX + lx, wz = buf.OriginZ + lz;
            return FiresSkyIslands.WaterLevelAt(wx, wz, out float island) && Mathf.Abs(island - level) < SkylandLevelMatch
                && FiresSkyIslands.TrySkylandTopAt(new Vector3(wx, level, wz), out _);
        }

        // Skyland water is stored in every chunk of the skyland's height band; only the chunk the level lies in hangs its
        // falls, so stacked chunks never draw the same curtain twice.
        private static bool OwnsLevel(MeshBuffers buf, float level)
            => level >= buf.OriginY && level < buf.OriginY + VoxelChunk.CELLS;

        private static void ReportSkylands()
        {
            if (s_skylandSheets == 0 || Time.realtimeSinceStartup < s_nextSkylandReport) return;
            s_nextSkylandReport = Time.realtimeSinceStartup + SkylandReportSeconds;
            FiresLogger.LogInfo($"[FiresWaterfall] skyland water in {s_skylandSheets} chunk sheet(s): drew {s_skylandKept} step lip(s) into "
                + $"lower water, buried {s_skylandBuried} bank/rim edge(s) (a skyland spills only at its mouth falls).");
            s_skylandSheets = s_skylandKept = s_skylandBuried = 0;
        }

        // Lips that continue this one: 8-connected, facing roughly the same way (so the two sides of a pool never join).
        private static void Neighbours(int lip, List<int> into)
        {
            into.Clear();
            var l = s_lips[lip];
            int S = VoxelChunk.SAMPLES;
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dz == 0) continue;
                    int x = l.X + dx, z = l.Z + dz;
                    if (x < 0 || z < 0 || x >= VoxelChunk.CELLS || z >= VoxelChunk.CELLS) continue;
                    int other = s_lipAt[(x + 1) + S * (z + 1)] - 1;
                    if (other < 0 || s_used[other]) continue;
                    if (Vector3.Dot(l.Outward, s_lips[other].Outward) < 0.3f) continue;
                    into.Add(other);
                }
        }

        private static int CountUnusedNeighbours(int lip)
        {
            Neighbours(lip, s_next);
            return s_next.Count;
        }

        // From `start`, follow unused neighbours, preferring the straightest continuation, into s_path.
        private static void Walk(int start)
        {
            s_path.Clear();
            int cur = start;
            Vector3 prev = Vector3.zero;
            while (true)
            {
                s_used[cur] = true;
                s_path.Add(cur);
                Neighbours(cur, s_next);
                if (s_next.Count == 0) break;
                int best = -1;
                float bestDot = float.MinValue;
                foreach (int candidate in s_next)
                {
                    Vector3 step = new Vector3(s_lips[candidate].X - s_lips[cur].X, 0f, s_lips[candidate].Z - s_lips[cur].Z).normalized;
                    float dot = prev == Vector3.zero ? 0f : Vector3.Dot(step, prev);
                    if (dot > bestDot) { bestDot = dot; best = candidate; }
                }
                prev = new Vector3(s_lips[best].X - s_lips[cur].X, 0f, s_lips[best].Z - s_lips[cur].Z).normalized;
                cur = best;
            }
        }

        private static Vector3 Centre(MeshBuffers buf, in Lip lip)
            => new Vector3(buf.OriginX + lip.X + 0.5f, 0f, buf.OriginZ + lip.Z + 0.5f);

        private static void AddColumn(MeshBuffers buf, in Lip lip, Vector3 shift, Vector3 outward)
        {
            s_columns.Add(new FiresWaterfalls.Column
            {
                Base = Centre(buf, lip) + outward * 0.5f + shift,
                LipY = lip.Level,
                FallLength = lip.Level - lip.Target + (lip.ToWater ? FiresWaterfalls.PlungeDepth : GroundPlunge),
                Outward = outward,
                OntoGround = !lip.ToWater,
            });
        }

        // A lip cell's outward direction averaged with its neighbours along the path: cell-to-cell steps of 45 degrees
        // folded the curtain's front face into flat panels.
        private static Vector3 SmoothedOutward(int k)
        {
            Vector3 sum = s_lips[s_path[k]].Outward;
            if (k > 0) sum += s_lips[s_path[k - 1]].Outward;
            if (k < s_path.Count - 1) sum += s_lips[s_path[k + 1]].Outward;
            return sum.sqrMagnitude > 1e-4f ? sum.normalized : s_lips[s_path[k]].Outward;
        }

        // One curtain for the path in s_path, with half-metre caps at both ends so a one-cell lip is still a curtain.
        private static void Append(MeshBuffers buf, int index)
        {
            s_columns.Clear();
            Vector3 mean = Vector3.zero;
            float longest = 0f;
            foreach (int i in s_path)
            {
                mean += s_lips[i].Outward;
                longest = Mathf.Max(longest, s_lips[i].Level - s_lips[i].Target);
            }
            mean = mean.sqrMagnitude > 1e-4f ? mean.normalized : s_lips[s_path[0]].Outward;

            if (s_path.Count == 1)
            {
                var lip = s_lips[s_path[0]];
                var across = new Vector3(-lip.Outward.z, 0f, lip.Outward.x);
                AddColumn(buf, lip, -across * 0.5f, lip.Outward);
                AddColumn(buf, lip, across * 0.5f, lip.Outward);
            }
            else
            {
                int lastK = s_path.Count - 1;
                var first = s_lips[s_path[0]];
                var second = s_lips[s_path[1]];
                AddColumn(buf, first, (Centre(buf, first) - Centre(buf, second)).normalized * 0.5f, SmoothedOutward(0));
                for (int k = 0; k <= lastK; k++) AddColumn(buf, s_lips[s_path[k]], Vector3.zero, SmoothedOutward(k));
                var last = s_lips[s_path[lastK]];
                var beforeLast = s_lips[s_path[lastK - 1]];
                AddColumn(buf, last, (Centre(buf, last) - Centre(buf, beforeLast)).normalized * 0.5f, SmoothedOutward(lastK));
            }

            float seed = Mathf.Repeat(buf.KeyX * 13.7f + buf.KeyZ * 7.3f + buf.OriginY * 0.19f + index * 3.1f, 97f);
            if (FiresWaterfalls.AppendCurtain(s_columns, mean, FiresWaterfalls.EdgeLipOut, 0f, seed, FiresWaterfalls.TerrainProfile))
                s_mistSites.Add(FiresWaterfalls.LastSite);
            float width = s_columns.Count - 1;
            if (longest >= SoundMinLength && width >= SoundMinWidth)
                s_candidates.Add(new KeyValuePair<float, Vector3>(longest, FiresWaterfalls.SoundSite(s_columns, mean)));
        }
    }
}
