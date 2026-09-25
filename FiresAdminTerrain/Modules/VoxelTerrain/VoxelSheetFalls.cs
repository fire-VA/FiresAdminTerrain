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
            if (s_lips.Count == 0)
            {
                if (falls != null) falls.SetActive(false);
                return 0;
            }

            if (s_used.Length < s_lips.Count) s_used = new bool[s_lips.Count * 2];
            System.Array.Clear(s_used, 0, s_lips.Count);
            FiresWaterfalls.BeginMesh();
            s_candidates.Clear();
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
                renderer.sharedMaterial = material;
                VoxelRenderGroup.Apply(renderer);
            }
            int vertices = FiresWaterfalls.EndMesh(mesh);
            falls.SetActive(vertices > 0);

            s_candidates.Sort((a, b) => b.Key.CompareTo(a.Key));
            s_sites.Clear();
            for (int i = 0; i < s_candidates.Count && i < SoundsPerChunk; i++) s_sites.Add(s_candidates[i].Value);
            FiresWaterfalls.SetSounds(falls.transform, s_sites);
            return vertices;
        }

        // Every column of this chunk whose water stands above a neighbour: a lower wet column by StepMin, or dry
        // ground by DropMin. Its outward direction is the drop-weighted mean of those neighbours' directions, and it
        // falls into the lowest water it borders, else onto the lowest ground.
        private static void CollectLips(MeshBuffers buf)
        {
            s_lips.Clear();
            System.Array.Clear(s_lipAt, 0, s_lipAt.Length);
            int S = VoxelChunk.SAMPLES, C = VoxelChunk.CELLS;
            for (int lz = 0; lz < C; lz++)
            {
                for (int lx = 0; lx < C; lx++)
                {
                    int i = (lx + 1) + S * (lz + 1);
                    float hi = buf.WaterLvl[i];
                    if (hi <= 0f) continue;
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
                            outward += new Vector3(dx, 0f, dz).normalized * drop;
                            if (water)
                            {
                                if (!toWater || floor < target) target = floor;
                                toWater = true;
                            }
                            else if (!toWater && floor < target) target = floor;
                        }
                    }
                    if (outward.sqrMagnitude < 1e-4f) continue;
                    s_lips.Add(new Lip { X = lx, Z = lz, Level = hi, Target = target, Outward = outward.normalized, ToWater = toWater });
                    s_lipAt[i] = s_lips.Count;
                }
            }
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

        private static void AddColumn(MeshBuffers buf, in Lip lip, Vector3 shift)
        {
            s_columns.Add(new FiresWaterfalls.Column
            {
                Base = Centre(buf, lip) + lip.Outward * 0.5f + shift,
                LipY = lip.Level,
                FallLength = lip.Level - lip.Target + (lip.ToWater ? FiresWaterfalls.PlungeDepth : GroundPlunge),
                Outward = lip.Outward,
                OntoGround = !lip.ToWater,
            });
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
                AddColumn(buf, lip, -across * 0.5f);
                AddColumn(buf, lip, across * 0.5f);
            }
            else
            {
                var first = s_lips[s_path[0]];
                var second = s_lips[s_path[1]];
                AddColumn(buf, first, (Centre(buf, first) - Centre(buf, second)).normalized * 0.5f);
                foreach (int i in s_path) AddColumn(buf, s_lips[i], Vector3.zero);
                var last = s_lips[s_path[s_path.Count - 1]];
                var beforeLast = s_lips[s_path[s_path.Count - 2]];
                AddColumn(buf, last, (Centre(buf, last) - Centre(buf, beforeLast)).normalized * 0.5f);
            }

            float seed = Mathf.Repeat(buf.KeyX * 13.7f + buf.KeyZ * 7.3f + buf.OriginY * 0.19f + index * 3.1f, 97f);
            FiresWaterfalls.AppendCurtain(s_columns, mean, FiresWaterfalls.EdgeLipOut, 0f, seed, FiresWaterfalls.TerrainProfile);
            float width = s_columns.Count - 1;
            if (longest >= SoundMinLength && width >= SoundMinWidth)
                s_candidates.Add(new KeyValuePair<float, Vector3>(longest, FiresWaterfalls.SoundSite(s_columns, mean)));
        }
    }
}
