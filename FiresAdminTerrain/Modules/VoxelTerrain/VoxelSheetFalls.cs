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
        private const int MistSitesPerChunk = 6;   // 0.2.839: was 3; stacked falls read as mist (Fire 10-07)
        private const float StackMistPerCurtain = 0.5f, StackMistMax = 3f;
        private const float StackFadeMin = 0.5f;   // 0.2.840
        private static readonly MaterialPropertyBlock s_stackBlock = new MaterialPropertyBlock();
        private static readonly int P_StackFade = Shader.PropertyToID("_StackFade");

        private static readonly List<FiresWaterfallMist.Site> s_mistSites = new List<FiresWaterfallMist.Site>(16);
        private static readonly List<FiresWaterfallMist.Site> s_rapidSites = new List<FiresWaterfallMist.Site>(16);
        // A step into lower water shorter than this is a rapid, sprayed rather than hung with a curtain.
        private const float RapidsMaxDrop = 2.5f;
        private const int RapidSitesPerChunk = 4;
        // This chunk's evidence (Debug): curtains drawn / skipped, rapids, needle triangles, worst lip / length steps.
        private static int s_drawn, s_skipped, s_rapids, s_needles;
        private static float s_rawLip, s_lip, s_rawLength, s_length;

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
            s_mergedSteps = 0;
            CollectLips(buf);
            ReportSkylands();
            if (s_lips.Count == 0)
            {
                // 0.2.851 (the lead, SinkTestGen8 after the stranded-pond clamp: 6 mist emitters still listed at pre-clamp
                // heights over flat water): a chunk that no longer has any lip kept its previous mist and sounds under the
                // inactive falls object. They go with the lips.
                if (falls != null)
                {
                    s_mistSites.Clear();
                    s_sites.Clear();
                    FiresWaterfallMist.SetEmitters(falls.transform, s_mistSites);
                    FiresWaterfalls.SetSounds(falls.transform, s_sites);
                    falls.SetActive(false);
                }
                return 0;
            }

            if (s_used.Length < s_lips.Count) s_used = new bool[s_lips.Count * 2];
            System.Array.Clear(s_used, 0, s_lips.Count);
            FiresWaterfalls.BeginMesh();
            s_candidates.Clear();
            s_mistSites.Clear();
            s_rapidSites.Clear();
            s_drawn = s_skipped = s_rapids = s_needles = 0;
            s_rawLip = s_lip = s_rawLength = s_length = 0f;
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
                renderer.sharedMaterials = FatSinkFalls.UnderRiverMaterials();   // 0.2.834: drawn before the water (a sheet in front fogs it, never shows it through)
                VoxelRenderGroup.Apply(renderer);
            }
            int vertices = FiresWaterfalls.EndMesh(mesh);
            // 0.2.840 (Fire 10-07, 117.webp): where several curtains hang in one chunk they overlap at the drop; each is thinned
            // by 1/sqrt(curtains) (at least StackFadeMin) through the shader's per-renderer _StackFade, and the mist takes over.
            var fallsRenderer = falls.GetComponent<MeshRenderer>();
            if (fallsRenderer != null)
            {
                fallsRenderer.GetPropertyBlock(s_stackBlock);
                s_stackBlock.SetFloat(P_StackFade, s_drawn > 1 ? Mathf.Max(StackFadeMin, 1f / Mathf.Sqrt(s_drawn)) : 1f);
                fallsRenderer.SetPropertyBlock(s_stackBlock);
            }
            // The mist lives under `falls`: a chunk with rapids but no curtain keeps it active for their spray.
            falls.SetActive(vertices > 0 || s_rapidSites.Count > 0);

            s_candidates.Sort((a, b) => b.Key.CompareTo(a.Key));
            s_sites.Clear();
            for (int i = 0; i < s_candidates.Count && i < SoundsPerChunk; i++) s_sites.Add(s_candidates[i].Value);
            FiresWaterfalls.SetSounds(falls.transform, s_sites);

            // 0.2.839 (Fire 10-07: stacked falls "should form more MIST and less hard white geometry"): where several curtains hang
            // in one chunk, each mist site is widened (more emission over a wider box) by StackMistPerCurtain per extra curtain.
            if (s_drawn > 1)
            {
                float boost = Mathf.Min(StackMistMax, 1f + StackMistPerCurtain * (s_drawn - 1));
                for (int m = 0; m < s_mistSites.Count; m++) { var site = s_mistSites[m]; site.Width *= boost; s_mistSites[m] = site; }
            }
            s_mistSites.Sort((a, b) => (b.Drop * b.Width).CompareTo(a.Drop * a.Width));
            if (s_mistSites.Count > MistSitesPerChunk) s_mistSites.RemoveRange(MistSitesPerChunk, s_mistSites.Count - MistSitesPerChunk);
            s_rapidSites.Sort((a, b) => (b.Drop * b.Width).CompareTo(a.Drop * a.Width));
            for (int i = 0; i < s_rapidSites.Count && i < RapidSitesPerChunk; i++) s_mistSites.Add(s_rapidSites[i]);
            FiresWaterfallMist.SetEmitters(falls.transform, s_mistSites);
            FiresLogger.LogDebug($"[FiresWaterfall] chunk ({buf.KeyX},{buf.OriginY},{buf.KeyZ}) at ({buf.OriginX},{buf.OriginZ}): {s_lips.Count} lip cell(s) -> "
                + $"{s_drawn} curtain(s) drawn (no approach rows), {s_skipped} too short, {s_rapids} rapid(s) as mist, {s_mergedSteps} stair step(s) merged into the fall above; needle triangles {s_needles}; "
                + $"worst lip step {s_rawLip:0.00} -> {s_lip:0.00} m, worst length step {s_rawLength:0.00} -> {s_length:0.00} m; {vertices} vertices");
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
                            float floor = water ? lo : SolidBelow(buf, lx + dx, lz + dz, hi, buf.Ground[n]);
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
            MergeStairs();
        }

        // 0.2.858 (Fire 10-07, 117.webp: stacked falls "far too white and hard geometry"; the lead: one curtain per drop): a
        // stepped drop made a lip on every step, each hung its own curtain over the next, and they overlapped. A lip whose next
        // cell downstream (along its outward direction) is another lip facing the same way and falling into water is one drop:
        // the upper lip falls all the way to the lowest step's target, and the steps below it are dropped. One curtain per drop.
        private const float StairDot = 0.7f;
        private static readonly List<Lip> s_merged = new List<Lip>(128);
        private static bool[] s_dropped = new bool[256];
        private static int s_mergedSteps;

        private static void MergeStairs()
        {
            int n = s_lips.Count;
            if (n < 2) return;
            if (s_dropped.Length < n) s_dropped = new bool[n * 2];
            System.Array.Clear(s_dropped, 0, n);
            int S = VoxelChunk.SAMPLES, C = VoxelChunk.CELLS;
            for (int i = 0; i < n; i++)
            {
                if (s_dropped[i]) continue;
                var lip = s_lips[i];
                int x = lip.X, z = lip.Z;
                for (int guard = 0; guard < C; guard++)
                {
                    int nx = x + Mathf.RoundToInt(lip.Outward.x), nz = z + Mathf.RoundToInt(lip.Outward.z);
                    if (nx < 0 || nz < 0 || nx >= C || nz >= C || (nx == x && nz == z)) break;
                    int j = s_lipAt[(nx + 1) + S * (nz + 1)] - 1;
                    if (j < 0 || j == i || s_dropped[j]) break;
                    var below = s_lips[j];
                    if (!below.ToWater || below.Level >= lip.Level || Vector3.Dot(below.Outward, lip.Outward) < StairDot) break;
                    s_dropped[j] = true;
                    s_mergedSteps++;
                    lip.Target = below.Target;
                    x = nx; z = nz;
                }
                s_lips[i] = lip;
            }
            s_merged.Clear();
            System.Array.Clear(s_lipAt, 0, s_lipAt.Length);
            for (int i = 0; i < n; i++)
            {
                if (s_dropped[i]) continue;
                s_merged.Add(s_lips[i]);
                s_lipAt[(s_lips[i].X + 1) + S * (s_lips[i].Z + 1)] = s_merged.Count;
            }
            s_lips.Clear();
            s_lips.AddRange(s_merged);
        }

        // 0.2.809 (Fire 10-06, SinkTestGen: "a ton of floating waterfalls everywhere"; the log had falls of 71 m off a river
        // at y 72): a dry neighbour's floor was buf.Ground, the chunk's GENERATED ground baseline (VoxelChunk._genGround),
        // not the voxels. Wherever the voxels differ from it (a cave or a carve under the bank, an edited or bake-shaped
        // bank) the drop was measured to the wrong height and a curtain hung in the air or through the rock. Now the floor
        // is the first SOLID voxel below the water level in that column of this chunk's own density; only when the column
        // is air all the way down through this chunk does the baseline (no higher than the chunk's bottom) stand in.
        private static float SolidBelow(MeshBuffers buf, int x, int z, float level, float baseline)
        {
            int S = VoxelChunk.SAMPLES;
            int top = Mathf.Clamp(Mathf.FloorToInt(level - buf.OriginY), -1, VoxelChunk.CELLS);
            for (int y = top; y >= -1; y--)
            {
                sbyte d = buf.Density[(x + 1) + S * ((y + 1) + S * (z + 1))];
                if (d <= 0) continue;
                // Interpolate the crossing between this solid sample and the air above it.
                if (y + 1 <= VoxelChunk.CELLS)
                {
                    sbyte above = buf.Density[(x + 1) + S * ((y + 2) + S * (z + 1))];
                    if (above <= 0) return buf.OriginY + y + d / (float)(d - above);
                }
                return buf.OriginY + y;
            }
            return Mathf.Min(baseline, buf.OriginY - 1f);
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

        // 0.2.809: two passes of a 1-2-1 average over the inner columns' lip positions (x, z) and fall lengths; the end
        // columns stay put, so a run still meets its neighbour chunk's run at the border.
        private static void SmoothColumns()
        {
            int n = s_columns.Count;
            if (n < 3) return;
            for (int pass = 0; pass < 2; pass++)
            {
                var prev = s_columns[0];
                for (int i = 1; i < n - 1; i++)
                {
                    var cur = s_columns[i];
                    var next = s_columns[i + 1];
                    var smoothed = cur;
                    smoothed.Base.x = (prev.Base.x + 2f * cur.Base.x + next.Base.x) * 0.25f;
                    smoothed.Base.z = (prev.Base.z + 2f * cur.Base.z + next.Base.z) * 0.25f;
                    smoothed.FallLength = (prev.FallLength + 2f * cur.FallLength + next.FallLength) * 0.25f;
                    prev = cur;
                    s_columns[i] = smoothed;
                }
            }
        }

        // A lip on the chunk's border whose water continues, at the same level, in the ring column across that border.
        private const float BorderLevelMatch = 0.3f;

        private static bool OnBorderWithWater(MeshBuffers buf, Lip lip)
        {
            int S = VoxelChunk.SAMPLES, last = VoxelChunk.CELLS - 1;
            bool Same(int x, int z) { float w = buf.WaterLvl[(x + 1) + S * (z + 1)]; return w > 0f && Mathf.Abs(w - lip.Level) < BorderLevelMatch; }
            return (lip.X == 0 && Same(-1, lip.Z)) || (lip.X == last && Same(last + 1, lip.Z))
                || (lip.Z == 0 && Same(lip.X, -1)) || (lip.Z == last && Same(lip.X, last + 1));
        }

        // One curtain for the path in s_path, with half-metre caps at both ends so a one-cell lip is still a curtain.
        private static void Append(MeshBuffers buf, int index)
        {
            // 0.2.809: one canonical order (along +x+z), so a run that crosses a chunk border is walked the same way on both
            // sides and its world-anchored texture (AcrossStart) runs on instead of mirroring.
            if (s_path.Count > 1)
            {
                var a = s_lips[s_path[0]]; var b = s_lips[s_path[s_path.Count - 1]];
                if ((b.X - a.X) + (b.Z - a.Z) < 0) s_path.Reverse();
            }
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

            // Fire 10-05: "the rapids should be mist like we have at the bottom of our waterfalls". A short step from water
            // into lower water is a rapid: no curtain (it hung a small slab with rounded ends inside the river), only spray.
            bool allToWater = true;
            foreach (int i in s_path) allToWater &= s_lips[i].ToWater;
            if (allToWater && longest < RapidsMaxDrop)
            {
                Vector3 lipAt = Vector3.zero;
                foreach (var column in s_columns) lipAt += new Vector3(column.Base.x, column.LipY, column.Base.z);
                lipAt /= s_columns.Count;
                s_rapidSites.Add(new FiresWaterfallMist.Site
                {
                    Lip = lipAt + mean * 0.5f, Foot = lipAt + mean * 0.5f + Vector3.down * longest, Outward = mean,
                    Width = s_columns.Count - 1, Drop = longest, Rapids = true,
                });
                s_rapids++;
                return;
            }

            // 0.2.809 (Fire 10-06: "smooth them together and blend them so you don't see hard seam edges on the ribbon"):
            // the lip positions and fall lengths are smoothed along the run (cell-centre steps of 45 degrees and one-cell
            // jumps in length made the angular shards), and an end that sits on the chunk border with water continuing in
            // the next chunk is left OPEN: no cap, no fade, the texture anchored to the run's world position, so the two
            // chunks' curtains read as one ribbon.
            SmoothColumns();
            var firstLip = s_lips[s_path[0]];
            var lastLip = s_lips[s_path[s_path.Count - 1]];
            FiresWaterfalls.OpenStart = OnBorderWithWater(buf, firstLip);
            FiresWaterfalls.OpenEnd = OnBorderWithWater(buf, lastLip);
            var runDir = new Vector3(1f, 0f, 1f).normalized;
            if (s_columns.Count > 1)
            {
                var span = s_columns[s_columns.Count - 1].Base - s_columns[0].Base;
                span.y = 0f;
                if (span.sqrMagnitude > 1e-4f) runDir = span.normalized;
            }
            FiresWaterfalls.AcrossStart = Vector3.Dot(s_columns[0].Base, runDir);
            // 0.2.809: the seed follows the water level, not the chunk, so the same step on both sides of a border matches.
            float seed = Mathf.Repeat(firstLip.Level * 3.7f, 97f);
            if (FiresWaterfalls.AppendCurtain(s_columns, mean, FiresWaterfalls.EdgeLipOut, 0f, seed, FiresWaterfalls.TerrainProfile,
                approach: false))
            {
                s_mistSites.Add(FiresWaterfalls.LastSite);
                var shape = FiresWaterfalls.LastShape;
                s_drawn++;
                s_needles += shape.Needles;
                s_rawLip = Mathf.Max(s_rawLip, shape.RawLipSpread);
                s_lip = Mathf.Max(s_lip, shape.LipSpread);
                s_rawLength = Mathf.Max(s_rawLength, shape.RawLengthSpread);
                s_length = Mathf.Max(s_length, shape.LengthSpread);
            }
            else s_skipped++;
            float width = s_columns.Count - 1;
            if (longest >= SoundMinLength && width >= SoundMinWidth)
                s_candidates.Add(new KeyValuePair<float, Vector3>(longest, FiresWaterfalls.SoundSite(s_columns, mean)));
        }
    }
}
