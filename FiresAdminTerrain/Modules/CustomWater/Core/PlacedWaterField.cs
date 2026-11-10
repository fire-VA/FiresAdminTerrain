using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Terrain.Voxel;
using VerdantsAscent.Water.Tools;

namespace VerdantsAscent.Water.Core
{
    // 0.2.1091 (Fire: "why is there two water meshes?" ... GO on one implementation). THE placed-water surface. Every
    // placed fresh-water disk (Vriver, Vpool, Vpond, Vpour, WaterSheets) is answered here, and every consumer reads it:
    //   sheet - PlacedWaterSheets builds the drawn mesh from it (graphics peers);
    //   level - WaterMeshCombiner.GetWaterSurfaceAt (fresh water) returns it, so Floating / Character.GetLiquidLevel /
    //           swim triggers / fish GetWaterLevel / FatWaterChecks.SurfaceAt all see it (plus the swim waves on top);
    //   ecology - FatWaterChecks.TryWaterAt (lily, vass, fish spawns, wet mask, sand) reads it with no waves;
    //   paint / caves - WaterSheets.SurfaceAt (wet layer paint, cave signals, cave pools) returns it for fresh water;
    //   nav - a thin water-layer collider per 32 m tile, so the navmesh marks it water (Pathfinding.m_waterLayers).
    // A column's surface = the disks' level they can hold (WaterSheets.SupportedSurfaceAt), capped to its lower bank minus
    // RimFreeboard (the 1088/1090 rule: walk 4 axes over the voxel ground, each axis's lower bank, the max over axes), dry
    // where the cap sits on or under the column's ground. Ponds (combiner pond mode) keep their disk level uncapped.
    // Data, not a mesh: it runs on the dedicated server too. Caps are cached per 32 m tile and dropped when the disks change
    // (WaterSheets signature) or a voxel chunk under the tile remeshes.
    internal static class PlacedWaterField
    {
        internal const float RimReach = 8f, RimStride = 2f, RimFreeboard = 1.0f, RimProbeUp = 6f, FloorReach = 32f;
        // 0.2.1343: how far the bank walk may go along one direction (per axis, in the walk's own 2 m strides); see WalkSide.
        internal const float BankWalk = 64f;
        private const float CrestDrop = 0.5f;
        private const float Tile = 32f;
        private const float BlendSlack = 0.01f;   // 0.2.1321: raw IS the pond level inside a pond now; float slack only

        internal enum Consumer { Sheet, Level, Lily, Fish, Wet, Paint, Nav, Count }
        private static readonly bool[] s_bound = new bool[(int)Consumer.Count];
        private static readonly int[] s_calls = new int[(int)Consumer.Count];
        private static float s_boundReportAt = -1f;
        private static bool s_boundReported;

        // 0.2.1347: one bed read = (x, z, level) exactly, so a hit is the same answer the voxels would give while the ground has not changed.
        private struct BedKey : System.IEquatable<BedKey>
        {
            public float X, Z, L;
            public bool Equals(BedKey o) => X == o.X && Z == o.Z && L == o.L;
            public override bool Equals(object o) => o is BedKey b && Equals(b);
            public override int GetHashCode() { unchecked { return (X.GetHashCode() * 397 ^ Z.GetHashCode()) * 397 ^ L.GetHashCode(); } }
        }
        private const int BedPerTile = 16384;
        internal static long BedHits, BedMisses; internal static double BedMissWorstMs;
        private static bool s_bedProof;
        private static bool BedCacheOn => FiresVoxelConfig.WaterBedCache != null && FiresVoxelConfig.WaterBedCache.Value;

        private sealed class TileCache
        {
            public readonly ConcurrentDictionary<long, float> Ground = new ConcurrentDictionary<long, float>();
            public readonly ConcurrentDictionary<long, float> Cap = new ConcurrentDictionary<long, float>();
            public readonly Dictionary<BedKey, float> Bed = new Dictionary<BedKey, float>();   // 0.2.1347: main thread only
        }
        private static readonly ConcurrentDictionary<long, TileCache> s_tiles = new ConcurrentDictionary<long, TileCache>();
        private static long s_signature = long.MinValue;
        private static bool s_hooked;
        private static int s_offThreadRaw;

        private static bool IsMainThread => WaterSheets.IsMainThread;

        private static long TileKey(int tx, int tz) => ((long)tx << 32) ^ (uint)tz;
        private static long ColKey(int x, int z, int band) => (((long)x & 0xFFFFF) << 40) | (((long)z & 0xFFFFF) << 20) | ((long)band & 0xFFFFF);

        private static TileCache TileFor(int x, int z)
            => s_tiles.GetOrAdd(TileKey(Mathf.FloorToInt(x / Tile), Mathf.FloorToInt(z / Tile)), _ => new TileCache());

        // Drops every cache on a disk change; hooks the chunk remesh once. Main thread.
        private static void Refresh()
        {
            if (!IsMainThread) return;
            if (!s_hooked) { s_hooked = true; VoxelWaterSheetMesh.ChunkMeshed += OnChunkMeshed; }
            long sig = WaterSheets.Signature;
            if (sig != s_signature)
            {
                // 0.2.1102 (overnight #2: every Vpour/Vmop dab cleared every cached cap tile in the world): drop only the tiles a
                // changed disk can reach (its circle + RimReach, the cap's bank walk). The first pass, or a change too large to
                // diff, still clears everything as before.
                bool first = s_signature == long.MinValue;
                s_signature = sig;
                int dropped = DropChangedTiles();   // always refreshes the snapshot
                if (first || dropped < 0) s_tiles.Clear();
                PlacedWaterNav.MarkAllDirty();
            }
            ReportBound();
        }

        // Disks as last seen (stable hash -> circle, level, pond), to diff a signature change. Returns the tiles dropped, or -1 for
        // "clear everything" (the first snapshot, or more than MaxDiff disks changed at once).
        private struct SeenDisk { public float X, Z, Y, R; public bool Pond, Water; }
        private static readonly Dictionary<int, SeenDisk> s_seenDisks = new Dictionary<int, SeenDisk>();
        private const int MaxDiff = 256;
        private static int DropChangedTiles()
        {
            var now = new Dictionary<int, SeenDisk>();
            foreach (var d in WaterSheets.FreshDisks())
                now[d.Hash] = new SeenDisk { X = d.X, Z = d.Z, Y = d.Y, R = d.R, Pond = d.Pond, Water = d.Water };
            bool hadSnapshot = s_seenDisks.Count > 0;
            var changed = new List<SeenDisk>();
            foreach (var kv in now)
                if (!s_seenDisks.TryGetValue(kv.Key, out var old) || !Same(old, kv.Value)) { changed.Add(kv.Value); if (s_seenDisks.TryGetValue(kv.Key, out old)) changed.Add(old); }
            foreach (var kv in s_seenDisks)
                if (!now.ContainsKey(kv.Key)) changed.Add(kv.Value);
            s_seenDisks.Clear();
            foreach (var kv in now) s_seenDisks[kv.Key] = kv.Value;
            if (!hadSnapshot || changed.Count > MaxDiff) return -1;
            int dropped = 0;
            foreach (var d in changed)
            {
                float reach = d.R + RimReach + 1f;
                for (int tx = Mathf.FloorToInt((d.X - reach) / Tile); tx <= Mathf.FloorToInt((d.X + reach) / Tile); tx++)
                    for (int tz = Mathf.FloorToInt((d.Z - reach) / Tile); tz <= Mathf.FloorToInt((d.Z + reach) / Tile); tz++)
                        if (s_tiles.TryRemove(TileKey(tx, tz), out _)) dropped++;
                // 0.2.1343: a walk can read this disk from BankWalk away. Out there only the caps go: a disk does not move the ground.
                float far = d.R + BankWalk + 1f;
                for (int tx = Mathf.FloorToInt((d.X - far) / Tile); tx <= Mathf.FloorToInt((d.X + far) / Tile); tx++)
                    for (int tz = Mathf.FloorToInt((d.Z - far) / Tile); tz <= Mathf.FloorToInt((d.Z + far) / Tile); tz++)
                        if (s_tiles.TryGetValue(TileKey(tx, tz), out var kept)) kept.Cap.Clear();
            }
            if (FatDiag.WaterField) FiresLogger.LogDebug($"[WaterField] disk change: {changed.Count} disk record(s) changed, {dropped} cap tile(s) dropped (the rest kept)");
            return dropped;
        }

        private static bool Same(SeenDisk a, SeenDisk b)
            => Mathf.Abs(a.X - b.X) < 0.01f && Mathf.Abs(a.Z - b.Z) < 0.01f && Mathf.Abs(a.Y - b.Y) < 0.01f && Mathf.Abs(a.R - b.R) < 0.01f && a.Pond == b.Pond && a.Water == b.Water;

        // 0.2.1331: on heightmap ground (no voxel chunk, so no ChunkMeshed) the regenerate is the event: the cap tiles there drop and the
        // sheets over it rebuild (delayed: a stroke pokes every frame). This also picks up water built before the heightmap had loaded.
        internal static void OnHeightmapRegenerate(Heightmap hm)
        {
            if (hm == null || !s_hooked || !PlacedWaterSheets.Active || VoxelGroundAt(hm.transform.position)) return;
            // 0.2.1346: this ground answers no sheet if voxel is about to replace it, or if it regenerated to the same heights. A new heightmap object always counts (water built before it loaded).
            if (FiresVoxelConfig.WaterTrimFanOut != null && FiresVoxelConfig.WaterTrimFanOut.Value)
            {
                if (VoxelZoneStreamer.OwnsGround(hm.transform.position)) { RegenSkipped++; return; }
                var hs = HmHeights(hm);
                uint sig = 2166136261u;
                if (hs != null) for (int i = 0; i < hs.Count; i++) sig = (sig ^ (uint)Mathf.RoundToInt(hs[i] * 100f)) * 16777619u;
                int id = hm.GetInstanceID();
                if (s_regenSig.TryGetValue(id, out uint was) && was == sig) { RegenSkipped++; return; }
                s_regenSig[id] = sig;
                RegenFired++;
            }
            float half = hm.m_width * hm.m_scale * 0.5f;
            Vector3 c = hm.transform.position;
            DropTiles(c.x - half - RimReach, c.x + half + RimReach, c.z - half - RimReach, c.z + half + RimReach);
            DropFarCaps(c.x - half, c.x + half, c.z - half, c.z + half);   // 0.2.1343
            PlacedWaterSheets.MarkDirtyLater(c, half * 1.42f + RimReach);
            PlacedWaterSheets.MarkCapChunksLater(c, half * 1.42f + BankWalk);   // 0.2.1343: only chunks with capped water, out to the walk
        }

        internal static int RegenSkipped, RegenFired;   // 0.2.1346
        private static readonly Dictionary<int, uint> s_regenSig = new Dictionary<int, uint>();

        private static bool VoxelGroundAt(Vector3 at)
            => VoxelOnlyMode.Active || VoxelZoneStreamer.IsZoneKnown(ZoneSystem.GetZone(at));

        // 0.2.1331: where no voxel chunk answers and the zone is not voxel ground, the floor is the heightmap (a Terrain Mode Off peer, a
        // Studio heightmap carve). Main thread. False + loaded = not this path (voxel ground); false + !loaded = no heights here yet.
        private static readonly HarmonyLib.AccessTools.FieldRef<Heightmap, List<float>> HmHeights = HarmonyLib.AccessTools.FieldRefAccess<Heightmap, List<float>>("m_heights");
        private static Heightmap s_lastHm;
        private static bool TryHeightmapGround(float x, float z, out float h, out bool loaded)
        {
            h = 0f;
            loaded = true;
            var p = new Vector3(x, 0f, z);
            if (VoxelGroundAt(p)) return false;
            var hm = s_lastHm != null && s_lastHm.IsPointInside(p) ? s_lastHm : Heightmap.FindHeightmap(p);
            if (hm == null) { loaded = false; return false; }
            s_lastHm = hm;
            var heights = HmHeights(hm);
            if (heights == null || heights.Count == 0 || !hm.GetWorldHeight(p, out h)) { loaded = false; return false; }
            return true;
        }

        private static void OnChunkMeshed(Vector3Int key)
        {
            // the chunk plus the rim reach around it: drop the tiles a cap there could have read
            DropTiles(key.x * VoxelChunk.CELLS - RimReach, (key.x + 1) * VoxelChunk.CELLS + RimReach, key.z * VoxelChunk.CELLS - RimReach, (key.z + 1) * VoxelChunk.CELLS + RimReach);
            DropFarCaps(key.x * VoxelChunk.CELLS, (key.x + 1) * VoxelChunk.CELLS, key.z * VoxelChunk.CELLS, (key.z + 1) * VoxelChunk.CELLS);   // 0.2.1343
        }

        // 0.2.1343: ground under this rect changed, and a walk reads it from BankWalk away: the caps out there go (their Ground caches
        // hold other columns' heights and stay). The rim-reach tiles were already dropped whole; nav follows only tiles that had caps.
        private static void DropFarCaps(float x0, float x1, float z0, float z1)
        {
            for (int tx = Mathf.FloorToInt((x0 - BankWalk) / Tile); tx <= Mathf.FloorToInt((x1 + BankWalk) / Tile); tx++)
                for (int tz = Mathf.FloorToInt((z0 - BankWalk) / Tile); tz <= Mathf.FloorToInt((z1 + BankWalk) / Tile); tz++)
                    if (s_tiles.TryGetValue(TileKey(tx, tz), out var t) && !t.Cap.IsEmpty)
                    {
                        t.Cap.Clear();
                        PlacedWaterNav.MarkDirty(tx, tz);
                    }
        }

        private static void DropTiles(float x0, float x1, float z0, float z1)
        {
            for (int tx = Mathf.FloorToInt(x0 / Tile); tx <= Mathf.FloorToInt(x1 / Tile); tx++)
                for (int tz = Mathf.FloorToInt(z0 / Tile); tz <= Mathf.FloorToInt(z1 / Tile); tz++)
                {
                    s_tiles.TryRemove(TileKey(tx, tz), out _);
                    PlacedWaterNav.MarkDirty(tx, tz);
                }
        }

        internal static void Note(Consumer c)
        {
            int i = (int)c;
            if (!s_bound[i]) s_bound[i] = true;
            s_calls[i]++;
        }

        // Once, 60 s after the first answer: which consumers have asked the field.
        private static void ReportBound()
        {
            if (s_boundReported) return;
            float now = Time.realtimeSinceStartup;
            if (s_boundReportAt < 0f) { s_boundReportAt = now + 60f; return; }
            if (now < s_boundReportAt) return;
            s_boundReported = true;
            var sb = new System.Text.StringBuilder("[WaterField] consumers bound: ");
            for (int i = 0; i < (int)Consumer.Count; i++)
                sb.Append(((Consumer)i).ToString().ToLowerInvariant()).Append(s_bound[i] ? " bound (" + s_calls[i] + " call(s))" : " not yet").Append(i + 1 < (int)Consumer.Count ? ", " : "");
            sb.Append($"; {s_offThreadRaw} off-thread answer(s) used the uncapped level (cap not cached yet)");
            FiresLogger.LogInfo(sb.ToString());
        }

        // The placed fresh water over (x,z) whose surface lies in [yLow, yHigh]: surface (capped) and the floor under it.
        // False = no placed water there (dry, mopped, or capped dry). No waves.
        internal static bool TrySurface(float x, float z, float yLow, float yHigh, Consumer who, out float surface, out float bed)
        {
            Note(who);
            surface = bed = float.NaN;
            bool main = IsMainThread;
            if (main) Refresh();
            float raw = Canonical(x, z, yLow, yHigh, main);
            return SurfaceFromRaw(x, z, raw, who, main, out surface, out bed);
        }

        // 0.2.1347: a sheet build's column already asked Canonical for this band: the rest of TrySurface, from that answer.
        internal static bool TrySurfaceFromRaw(float x, float z, float raw, Consumer who, out float surface, out float bed)
        {
            Note(who);
            bool main = IsMainThread;
            if (main) Refresh();
            return SurfaceFromRaw(x, z, raw, who, main, out surface, out bed);
        }

        private static bool SurfaceFromRaw(float x, float z, float raw, Consumer who, bool main, out float surface, out float bed)
        {
            surface = bed = float.NaN;
            if (float.IsNaN(raw)) return false;
            // 0.2.1097 (Vmop): the mask may sit in the layer above the water too (a surface right under a chunk border).
            if (main && (VoxelWorld.IsMoppedAt(x, raw, z) || VoxelWorld.IsMoppedAt(x, raw + VoxelChunk.CELLS, z))) return false;
            // 0.2.1321: the pond test reads the pond disk's OWN level (the blended raw missed it by > 0.51 m and Capped lowered the pond).
            float level = WaterSheets.PondTopAt(x, z, raw - BlendSlack, raw + BlendSlack, out float pondY) && WaterSheets.PondCovers(x, z, pondY) ? pondY : Capped(x, z, raw, main);
            if (float.IsNaN(level)) return false;
            bed = BedUnder(x, z, level, main);
            if (!float.IsNaN(bed) && bed >= level) return false;
            // 0.2.1097: the per-column state (WaterStateField). The drawn sheet always gets the surface (the shader draws ice / lava /
            // crust from the state texture); the swim level and the navmesh see no water on solid ice, crust or hot lava; water life,
            // fish, wet ground and wet paint only on open water.
            if (who != Consumer.Sheet)
            {
                if (who == Consumer.Level || who == Consumer.Nav) { if (WaterStateField.NotLiquidAt(x, z, level, WaterStateField.GridSrc.Placed)) return false; }   // 0.2.1313: the placed sheet grid
                else if (!WaterStateField.PureWaterAt(x, z, level)) return false;
            }
            surface = level;
            return true;
        }

        // 0.2.1094 (Fire, shot at 37,598: "multiple water layers ... walking on the river bed" with a teal plane at the bank tops
        // overhead): the answer depended on the caller's y band. The drawn sheet asks with its chunk band (2 m under to 34 m over)
        // and found the highest disk there; a swimmer's band starts 0.8 m under him. Two disks over one bed at different levels
        // (an earlier, higher river placement and the new one) gave two answers. Now the caller's band only picks the FLOOR (the
        // bed under its first answer); the surface is then the highest supported level among all disks over that same floor
        // (bed within FloorTolerance), whatever band asked - one surface per column per floor, for every consumer.
        internal const float FloorTolerance = 1.0f, FloorSpan = 64f;

        internal static float Canonical(float x, float z, float yLow, float yHigh, bool main)
        {
            System.Func<float, float> bedFn = l => BedUnder(x, z, l, main);
            float first = WaterSheets.SupportedSurfaceAt(x, z, yLow, yHigh, bedFn);
            if (float.IsNaN(first)) return first;
            float floor = bedFn(first);
            if (float.IsNaN(floor)) return first;   // off the main thread / no floor found: the band's own answer
            float hi = floor + FloorSpan;
            for (int tries = 0; tries < 4; tries++)
            {
                float r = WaterSheets.SupportedSurfaceAt(x, z, floor + 0.01f, hi, bedFn);
                if (float.IsNaN(r)) break;
                float b = bedFn(r);
                if (float.IsNaN(b) || Mathf.Abs(b - floor) <= FloorTolerance)
                {
                    if (main) NoteStacked(x, z, floor, r);
                    return r;
                }
                hi = r - 0.001f;   // that level sits over another floor (a skyland/ledge above): look under it
            }
            return first;
        }

        // [WaterField] column line: 2+ disk levels over one floor (deduped per 4 m cell, at most one line a second).
        private static readonly HashSet<long> s_stackSaid = new HashSet<long>();
        private static readonly HashSet<long> s_stackOne = new HashSet<long>();   // 0.2.1347
        private static float s_stackLogAt;
        private static readonly List<float> s_levelScratch = new List<float>();

        private static void NoteStacked(float x, float z, float floor, float kept)
        {
            if (!FiresLogger.DebugEnabled || Time.unscaledTime < s_stackLogAt) return;
            long cell = TileKey(Mathf.FloorToInt(x / 4f), Mathf.FloorToInt(z / 4f));
            if (s_stackSaid.Contains(cell)) return;
            // 0.2.1347: with Debug on, a cell with one level walked EVERY disk in the world again on every Canonical (nothing noted it, and the 1 s limit only starts after a line).
            bool once = FiresVoxelConfig.WaterColumnAsksOnce != null && FiresVoxelConfig.WaterColumnAsksOnce.Value;
            if (once && s_stackOne.Contains(cell)) return;
            DiskLevelsOnFloor(x, z, floor, s_levelScratch);
            if (s_levelScratch.Count < 2)
            {
                if (once) { if (s_stackOne.Count > 20000) s_stackOne.Clear(); s_stackOne.Add(cell); }
                return;
            }
            s_stackSaid.Add(cell);
            s_stackLogAt = Time.unscaledTime + 1f;
            FiresLogger.LogDebug($"[WaterField] column ({x:0},{z:0}): {s_levelScratch.Count} disk levels on one floor (bed {floor:0.00}): {string.Join(", ", s_levelScratch.ConvertAll(l => l.ToString("0.00")))}, kept {kept:0.00} (fat_water_layers lists them; 'fat_water_layers remove-hidden' removes the shadowed ones)");
        }

        // Distinct fresh-water disk levels covering (x,z) whose bed is this floor (within FloorTolerance), highest first.
        internal static void DiskLevelsOnFloor(float x, float z, float floor, List<float> into)
        {
            into.Clear();
            foreach (var d in WaterSheets.FreshDisks())
            {
                if (!d.Water || d.Y <= floor) continue;
                float dx = x - d.X, dz = z - d.Z;
                if (dx * dx + dz * dz > d.R * d.R) continue;
                float b = BedUnder(x, z, d.Y, true);
                if (!float.IsNaN(b) && Mathf.Abs(b - floor) > FloorTolerance) continue;
                bool dup = false;
                foreach (var l in into) if (Mathf.Abs(l - d.Y) < 0.05f) { dup = true; break; }
                if (!dup) into.Add(d.Y);
            }
            into.Sort((a, b) => b.CompareTo(a));
        }

        // 0.2.1100 (Fire, shot 566 on live 1098: "we need to be careful were not culling or splitting meshes along river banks";
        // a rectangular notch cut out of the river at the bank, a dark seam, the foam band jumping). PROVEN from his log: every
        // river chunk line reads "capped 0 (kept K, dropped dry N)" with N up to 19 - the cap was solved per 1 m column on its
        // own (its own 8 m bank walk, rounded to the nearest column) and a column whose cap fell under its ground was dropped
        // DRY while its neighbour kept the full level: a hard per-cell cut at the bank. The cap is now one CONTINUOUS field:
        // each column's bank cap is still solved as before (cached per tile), and the answer at (x,z) is the bilinear blend of
        // the four surrounding columns' caps. No column is dropped by the cap any more; water ends only where the ground rises
        // above that smooth level (the bed check after it), which is the real shoreline, and the sheet's skirt buries it.
        internal static float Capped(float x, float z, float raw, bool main)
        {
            if (float.IsNaN(raw)) return raw;
            int x0 = Mathf.FloorToInt(x), z0 = Mathf.FloorToInt(z);
            float fx = x - x0, fz = z - z0;
            float c00 = ColumnCap(x0, z0, raw, main), c10 = ColumnCap(x0 + 1, z0, raw, main);
            float c01 = ColumnCap(x0, z0 + 1, raw, main), c11 = ColumnCap(x0 + 1, z0 + 1, raw, main);
            if (float.IsNaN(c00) || float.IsNaN(c10) || float.IsNaN(c01) || float.IsNaN(c11)) return raw;   // off-thread, not cached yet
            float cap = Mathf.Lerp(Mathf.Lerp(c00, c10, fx), Mathf.Lerp(c01, c11, fx), fz);
            if (main) NoteCapStep(x0, z0, c00, c10, c01, c11);
            return raw > cap ? cap : raw;
        }

        // [WaterEdge] a cap step between neighbouring columns > 0.5 m (now blended instead of cut), at most one line every 5 s.
        private static float s_capStepLogAt;
        private static void NoteCapStep(int x, int z, float a, float b, float c, float d)
        {
            if (!FiresLogger.DebugEnabled || Time.unscaledTime < s_capStepLogAt) return;
            float lo = Mathf.Min(Mathf.Min(a, b), Mathf.Min(c, d)), hi = Mathf.Max(Mathf.Max(a, b), Mathf.Max(c, d));
            if (hi - lo <= 0.5f) return;
            s_capStepLogAt = Time.unscaledTime + 5f;
            if (FatDiag.WaterField) FiresLogger.LogDebug($"[WaterEdge] cap step {hi - lo:0.00} m at ({x},{z}) between neighbouring bank columns - blended into one smooth surface (0.2.1100), no column cut");
        }

        // One 1 m column's bank cap (cached per tile): the level its lower bank holds, minus the freeboard. A column with no
        // ground under it caps nothing (raw). NaN = off the main thread and not cached yet.
        private static float ColumnCap(int ix, int iz, float raw, bool main)
        {
            int band = Mathf.FloorToInt(raw);
            var tile = TileFor(ix, iz);
            long key = ColKey(ix, iz, band);
            if (!tile.Cap.TryGetValue(key, out float cap))
            {
                if (!main) { System.Threading.Interlocked.Increment(ref s_offThreadRaw); return float.NaN; }
                float own = Ground(ix, iz, raw);
                if (float.IsNaN(own)) cap = raw;   // no ground: nothing to cap against
                else
                {
                    long walk0 = System.Diagnostics.Stopwatch.GetTimestamp();   // 0.2.1343 timer
                    float bank = float.MinValue, enough = raw + RimFreeboard;
                    for (int a = 0; a < 4 && bank < enough; a++)
                    {
                        int dx = a == 0 ? 1 : a == 1 ? 0 : 1, dz = a == 0 ? 0 : a == 1 ? 1 : a == 2 ? 1 : -1;
                        float r1 = WalkSide(ix, iz, dx, dz, raw, own, out _, out _, out bool open1);
                        if (open1) s_walkNoBank++;
                        if (r1 <= bank) continue;   // this axis cannot beat the bank already found
                        float r2 = WalkSide(ix, iz, -dx, -dz, raw, own, out _, out _, out bool open2);
                        if (open2) s_walkNoBank++;
                        float lower = Mathf.Min(r1, r2);
                        if (lower > bank) bank = lower;
                    }
                    NoteWalk(walk0);
                    CapTicks += System.Diagnostics.Stopwatch.GetTimestamp() - walk0;   // 0.2.1344
                    float fb = Mathf.Min(RimFreeboard, 0.5f * (bank - own));
                    cap = bank - Mathf.Max(fb, 0f);
                    if (cap > raw) cap = raw;   // 0.2.1100: never dropped here; the blend + the bed check decide the shoreline
                }
                tile.Cap[key] = cap;
            }
            return cap;
        }

        // 0.2.1343 (world SFSC3: "river end pool ... r 10.8 ... kind River, capped to its banks", chunk (96,1120) fill levels 46.01..49.00,
        // all disks at 49.0, bed 46): a fixed 8 m walk inside a pool wider than that saw only the flat floor, so bank = bed and the surface
        // sagged onto the bed. One direction's walk now reads:
        //  - it stops as soon as the ground reaches level + freeboard: a bank that holds the water;
        //  - it keeps going while the water body still covers the sample (a fresh disk within the blend band of this level, WaterSheets.BodyCovers);
        //    lower reaches of a river are not this body, so a downstream side ends as it always did;
        //  - past the water it goes the old RimReach further (at least the old 8 m from the column, so nothing the old walk saw is missed), and
        //    the highest ground seen is the bank. Ground under the level there = a REAL LOW BANK, and it caps as before;
        //  - past the water, beyond the old reach, ground dropping CrestDrop under the crest seen outside the water ends the walk: a low
        //    bank with a hill behind it still leaks, the hill does not count;
        //  - NO BANK: still covered at BankWalk. That side has no evidence, so it returns level + freeboard and never acts as a low bank.
        //    The other side of the axis still decides, so a real low outlet opposite it caps.
        // Unknown ground (NaN: unloaded, or over FloorReach down) past the water adds nothing, so it reads low, as the old walk did; the
        // chunk / heightmap event drops the cap when it loads. Each result stays cached in the cap tile. Main thread.
        private static float WalkSide(int ix, int iz, int dx, int dz, float raw, float own, out int hitX, out int hitZ, out bool noBank)
        {
            int st = (int)RimStride, old = Mathf.Max(1, (int)(RimReach / RimStride)), max = (int)(BankWalk / RimStride), past = 0;
            float r = own, enough = raw + RimFreeboard, crest = float.MinValue;
            bool covered = false;
            hitX = ix; hitZ = iz; noBank = false;
            for (int s = 1; s <= max; s++)
            {
                s_walkSteps++;
                int x = ix + dx * st * s, z = iz + dz * st * s;
                float g = Ground(x, z, raw);
                bool known = !float.IsNaN(g);
                if (known && g > r) { r = g; hitX = x; hitZ = z; }
                if (r >= enough) return r;
                covered = WaterSheets.BodyCovers(x, z, raw);
                if (covered) { past = 0; crest = float.MinValue; continue; }
                if (known)
                {
                    if (s > old && g < crest - CrestDrop) return r;
                    if (g > crest) crest = g;
                }
                if (++past >= old && s >= old) return r;
            }
            if (!covered) return r;
            noBank = true;
            return enough;
        }

        // 0.2.1343 [WaterField] cap walk timer (Debug, 10 s) + the one Info line.
        private static double s_walkMs, s_walkWorst;
        private static int s_walkCount, s_walkNoBank;
        private static long s_walkSteps;
        private static float s_walkLogAt;
        private static bool s_walkProof;
        private static void NoteWalk(long t0)
        {
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            s_walkMs += ms; s_walkCount++;
            if (ms > s_walkWorst) s_walkWorst = ms;
            if (!s_walkProof) { s_walkProof = true; FiresLogger.LogInfo($"[WaterField] bank walk reaches past the water to the real banks (up to {BankWalk:0} m, {RimReach:0} m past the water; no bank in reach = not a low bank) (0.2.1343)"); }
            float now = Time.realtimeSinceStartup;
            if (now < s_walkLogAt) return;
            s_walkLogAt = now + 10f;
            if (FiresLogger.DebugEnabled) FiresLogger.LogDebug($"[WaterField] cap walk last 10 s: {s_walkCount} column(s), total {s_walkMs:0.0} ms, worst {s_walkWorst:0.00} ms, {s_walkSteps} step(s), {s_walkNoBank} side(s) with no bank in reach (0.2.1343)");
            s_walkMs = s_walkWorst = 0; s_walkCount = s_walkNoBank = 0; s_walkSteps = 0;
        }

        // 0.2.1344: where a sheet build's time goes (PlacedWaterSheets' phase timer), main-thread accumulators.
        internal static long GroundTicks, BedTicks, CapTicks;

        private static float Ground(int x, int z, float level)
        {
            var tile = TileFor(x, z);
            long k = ColKey(x, z, Mathf.FloorToInt(level / 8f));
            if (tile.Ground.TryGetValue(k, out float g)) return g;
            long g0 = System.Diagnostics.Stopwatch.GetTimestamp();   // 0.2.1344
            float top = level + RimProbeUp;
            if (VoxelWorld.TrySampleWaterFloorY(x, z, top, level - FloorReach, out float y, out bool wall)) g = wall ? top : y;
            else if (TryHeightmapGround(x, z, out float hg, out bool loaded)) g = hg < level - FloorReach ? float.NaN : Mathf.Min(hg, top);   // 0.2.1331
            else if (!loaded) { GroundTicks += System.Diagnostics.Stopwatch.GetTimestamp() - g0; return float.NaN; }   // 0.2.1331: not cached; the heightmap's regenerate drops the tile anyway
            else g = float.NaN;
            tile.Ground[k] = g;
            GroundTicks += System.Diagnostics.Stopwatch.GetTimestamp() - g0;   // 0.2.1344
            return g;
        }

        // 0.2.1321 (Fire x242 z1027 / x243 z1021: a dry strip in a placed river, the bed ~3 m BELOW the disk level; PROBE ONLY,
        // the level rule is unchanged): explains one column under one disk - the disk, the canonical raw level, the four corner
        // column caps and their blend, for the nearest column every axis's two bank walks (the max ground each found and WHERE),
        // which axis set the bank, the column's own ground vs the cap, the bed, the final surface and why it is dry.
        // Returns true when the column is dry. Main thread only; reads the same caches TrySurface does.
        internal static bool Explain(float x, float z, WaterSheets.DiskInfo d, System.Text.StringBuilder sb)
        {
            Refresh();
            float floor = BedUnder(x, z, d.Y, true);
            float yLow = float.IsNaN(floor) ? d.Y - FloorReach : floor - 0.5f;
            float raw = Canonical(x, z, yLow, yLow + FloorSpan, true);
            sb.Append($"[WaterField] probe ({x:0.0},{z:0.0}) disk hash {d.Hash} kind {d.Kind}{(d.Pond ? " pond" : "")} level {d.Y:0.00} r {d.R:0.0} centre ({d.X:0.0},{d.Z:0.0}); ground under disk {Fmt(floor)}; canonical raw {Fmt(raw)}");
            if (float.IsNaN(raw)) { sb.Append(" -> DRY: no disk level SUPPORTED here (WaterSheets.SupportedSurfaceAt none)"); return true; }
            if (VoxelWorld.IsMoppedAt(x, raw, z) || VoxelWorld.IsMoppedAt(x, raw + VoxelChunk.CELLS, z)) { sb.Append(" -> DRY: mopped"); return true; }
            bool pond = WaterSheets.PondTopAt(x, z, raw - BlendSlack, raw + BlendSlack, out float pondY) && WaterSheets.PondCovers(x, z, pondY);   // 0.2.1321 same test as TrySurface
            int x0 = Mathf.FloorToInt(x), z0 = Mathf.FloorToInt(z);
            float c00 = ColumnCap(x0, z0, raw, true), c10 = ColumnCap(x0 + 1, z0, raw, true);
            float c01 = ColumnCap(x0, z0 + 1, raw, true), c11 = ColumnCap(x0 + 1, z0 + 1, raw, true);
            float level = pond ? pondY : Capped(x, z, raw, true);
            sb.Append($"; pond {pond}; corner caps ({x0},{z0}) {Fmt(c00)} ({x0 + 1},{z0}) {Fmt(c10)} ({x0},{z0 + 1}) {Fmt(c01)} ({x0 + 1},{z0 + 1}) {Fmt(c11)} -> level {Fmt(level)}");
            int ix = Mathf.RoundToInt(x), iz = Mathf.RoundToInt(z);
            float own = Ground(ix, iz, raw);
            sb.Append($"; column ({ix},{iz}) own ground {Fmt(own)}");
            if (!float.IsNaN(own))
            {
                float bank = float.MinValue; int bankAxis = -1;
                for (int a = 0; a < 4; a++)
                {
                    int dx = a == 0 ? 1 : a == 1 ? 0 : 1, dz = a == 0 ? 0 : a == 1 ? 1 : a == 2 ? 1 : -1;
                    float r1 = WalkSide(ix, iz, dx, dz, raw, own, out int h1x, out int h1z, out bool open1);   // 0.2.1343: the same walk as ColumnCap
                    float r2 = WalkSide(ix, iz, -dx, -dz, raw, own, out int h2x, out int h2z, out bool open2);
                    float lower = Mathf.Min(r1, r2);
                    sb.Append($"; axis {a} ({dx},{dz}): +side max ground {r1:0.00} at ({h1x},{h1z}){(open1 ? " (no bank in reach)" : "")}, -side {r2:0.00} at ({h2x},{h2z}){(open2 ? " (no bank in reach)" : "")}, lower bank {lower:0.00}");
                    if (lower > bank) { bank = lower; bankAxis = a; }
                }
                float fb = Mathf.Max(Mathf.Min(RimFreeboard, 0.5f * (bank - own)), 0f);
                float colCap = Mathf.Min(bank - fb, raw);
                sb.Append($"; bank {bank:0.00} set by axis {bankAxis}, freeboard {fb:0.00} -> column cap {colCap:0.00} (cap - own ground {colCap - own:+0.00;-0.00})");
            }
            float bed = float.IsNaN(level) ? float.NaN : BedUnder(x, z, level, true);
            sb.Append($"; bed under level {Fmt(bed)}");
            if (float.IsNaN(level)) { sb.Append(" -> DRY: no level"); return true; }
            if (!float.IsNaN(bed) && bed >= level) { sb.Append($" -> DRY: cap {level:0.00} sits on/under the bed {bed:0.00} (by {bed - level:0.00} m)"); return true; }
            bool notLiquid = WaterStateField.NotLiquidAt(x, z, level, WaterStateField.GridSrc.Placed);
            sb.Append($"; final surface {level:0.00} (depth {(float.IsNaN(bed) ? "?" : (level - bed).ToString("0.00"))}), state not-liquid {notLiquid} -> WET (sheet draws){(notLiquid ? ", no swim (ice / crust / lava state)" : "")}");
            return false;
        }

        private static string Fmt(float v) => float.IsNaN(v) ? "none" : v.ToString("0.00");

        // The floor under (x,z) below level (NaN when none within FloorReach, a wall, or off the main thread).
        internal static float BedUnder(float x, float z, float level, bool main)
        {
            if (!main) return float.NaN;
            long b0 = System.Diagnostics.Stopwatch.GetTimestamp();   // 0.2.1344
            // 0.2.1347 (1345 log: bed reads 19.8 s, the largest named column cost, 29-98% of the column pass by window; a column reads 2-3 levels about nine times, and a repeat rebuild reads them all again): per tile, dropped
            // with the tile by PlacedWaterField.OnChunkMeshed / OnHeightmapRegenerate, the events the Ground and Cap caches already trust. Only a heightmap not loaded yet is left uncached.
            TileCache tile = null; BedKey key = default;
            bool cache = BedCacheOn && !float.IsNaN(level);
            try {
            if (cache)
            {
                tile = TileFor(Mathf.FloorToInt(x), Mathf.FloorToInt(z));
                key = new BedKey { X = x, Z = z, L = level };
                if (tile.Bed.TryGetValue(key, out float hit)) { BedHits++; return hit; }
                BedMisses++;
                if (!s_bedProof) { s_bedProof = true; FiresLogger.LogInfo("[WaterField] placed-water bed reads are cached per tile until the ground there changes (0.2.1347)"); }
            }
            float answer; bool loaded = true;
            if (VoxelWorld.TrySampleWaterFloorY(x, z, level + 0.05f, level - FloorReach, out float bed, out bool wall)) answer = !wall ? bed : float.NaN;
            // 0.2.1331: ground over the level comes back as it is, so the caller's "bed >= level" reads this column dry.
            else answer = TryHeightmapGround(x, z, out float hg, out loaded) && hg >= level - FloorReach ? hg : float.NaN;
            if (cache && loaded)
            {
                if (tile.Bed.Count >= BedPerTile) tile.Bed.Clear();
                tile.Bed[key] = answer;
                double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - b0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                if (ms > BedMissWorstMs) BedMissWorstMs = ms;
            }
            return answer;
            } finally { BedTicks += System.Diagnostics.Stopwatch.GetTimestamp() - b0; }
        }
    }

    // 0.2.1321 (Fire x242 z1027 dry strip, PROBE ONLY): Debug, once a second at most, the DRY columns inside a placed disk's
    // footprint within 8 m of the local player (each explained by PlacedWaterField.Explain); console "fat_water_probe [r]" dumps
    // the player's column (r = 0) or every dry column within r m once at Info.
    internal static class PlacedWaterProbe
    {
        private const float Radius = 8f, Interval = 1f;
        private const int MaxLines = 6, MaxCommandLines = 40;
        private static float s_next;
        private static bool s_proof;
        private static readonly HashSet<long> s_said = new HashSet<long>();
        private static readonly List<WaterSheets.DiskInfo> s_disks = new List<WaterSheets.DiskInfo>();

        [HarmonyPatch(typeof(Player), "Update")]
        private static class Patch_PlayerUpdate
        {
            private static void Postfix(Player __instance)
            {
                // 0.2.1346 ([fgn]: 62-80 ms a run, a suspected 1 Hz stall with Debug on): the auto scan is opt-in; fat_water_probe stays.
                if (FiresVoxelConfig.WaterDryColumnAutoProbe == null || !FiresVoxelConfig.WaterDryColumnAutoProbe.Value) return;
                if (!FiresLogger.DebugEnabled || __instance != Player.m_localPlayer || Time.unscaledTime < s_next) return;
                s_next = Time.unscaledTime + Interval;
                try { Scan(__instance.transform.position, Radius, MaxLines, false); }
                catch (System.Exception e) { FiresLogger.LogDebug("[WaterField] probe threw: " + e.Message); }
            }
        }

        // Dry columns (1 m grid) inside any covering fresh disk within r of p. info: Info level + no dedupe.
        private static int Scan(Vector3 p, float r, int maxLines, bool info)
        {
            s_disks.Clear();
            foreach (var d in WaterSheets.FreshDisks())
            {
                if (!d.Water) continue;
                float dx = p.x - d.X, dz = p.z - d.Z, reach = d.R + r;
                if (dx * dx + dz * dz <= reach * reach) s_disks.Add(d);
            }
            if (s_disks.Count == 0) return 0;
            int lines = 0, ri = Mathf.CeilToInt(r);
            var sb = new System.Text.StringBuilder(1024);
            for (int oz = -ri; oz <= ri && lines < maxLines; oz++)
                for (int ox = -ri; ox <= ri && lines < maxLines; ox++)
                {
                    if (ox * ox + oz * oz > r * r) continue;
                    float x = Mathf.Floor(p.x) + ox + 0.5f, z = Mathf.Floor(p.z) + oz + 0.5f;
                    foreach (var d in s_disks)
                    {
                        float dx = x - d.X, dz = z - d.Z;
                        if (dx * dx + dz * dz > d.R * d.R) continue;
                        sb.Clear();
                        if (!PlacedWaterField.Explain(x, z, d, sb)) continue;
                        long key = (((long)Mathf.FloorToInt(x) & 0xFFFFF) << 40) | (((long)Mathf.FloorToInt(z) & 0xFFFFF) << 20) | ((long)d.Hash & 0xFFFFF);
                        if (!info && !s_said.Add(key)) continue;
                        if (s_said.Count > 20000) s_said.Clear();
                        sb.Append(" (0.2.1321)");
                        if (info) FiresLogger.LogInfo(sb.ToString()); else FiresLogger.LogDebug(sb.ToString());
                        lines++;
                        break;
                    }
                }
            if (lines > 0 && !s_proof) { s_proof = true; FiresLogger.LogInfo("[WaterField] dry-column probe running near the player (Debug, 1 s; fat_water_probe [r]) (0.2.1321)"); }
            return lines;
        }

        [HarmonyPatch(typeof(Terminal), "InitTerminal")]
        private static class Register
        {
            private static bool s_done;
            private static void Postfix()
            {
                if (s_done) return;
                s_done = true;
                new Terminal.ConsoleCommand("fat_water_probe",
                    "[FiresAdminTerrain] [r] placed-water column probe at your position (r = 0) or every dry column within r m: disk, axis caps and where each hit the bank, ground, surface, why dry (Info, once)",
                    args =>
                    {
                        var player = Player.m_localPlayer;
                        if (player == null) { args.Context?.AddString("[WaterField] probe: no local player"); return; }
                        float r = 0f;
                        if (args.Length > 1) float.TryParse(args[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out r);
                        r = Mathf.Clamp(r, 0f, 32f);
                        Vector3 p = player.transform.position;
                        if (r <= 0f)
                        {
                            var sb = new System.Text.StringBuilder(1024);
                            int n = 0;
                            foreach (var d in WaterSheets.FreshDisks())
                            {
                                if (!d.Water) continue;
                                float dx = p.x - d.X, dz = p.z - d.Z;
                                if (dx * dx + dz * dz > d.R * d.R) continue;
                                sb.Clear();
                                bool dry = PlacedWaterField.Explain(p.x, p.z, d, sb);
                                sb.Append($" [player y {p.y:0.00}, {(dry ? "dry" : "wet")}] (0.2.1321)");
                                FiresLogger.LogInfo(sb.ToString());
                                n++;
                            }
                            args.Context?.AddString(n == 0 ? $"[WaterField] probe ({p.x:0.0},{p.z:0.0}): no placed fresh disk covers you" : $"[WaterField] probe: {n} disk(s) explained in the log");
                            return;
                        }
                        int lines = Scan(p, r, MaxCommandLines, true);
                        args.Context?.AddString($"[WaterField] probe: {lines} dry column(s) within {r:0} m logged (max {MaxCommandLines})");
                    }, isCheat: true);
            }
        }
    }

    // 0.2.1091: the navmesh sees placed water. Per 32 m tile with placed water, one thin non-trigger MeshCollider at the field's
    // surface on layer "Water" (Pathfinding.m_waterLayers, vanilla bit 16), so BuildTile collects it as area 3 (water cost) for
    // swimmers and area 1 (not walkable) for AvoidWater agents - companions and wildlife route over bridges or around. Built
    // only when the physics matrix lets no character layer collide with Water (nobody stands on it); otherwise refused and
    // logged once. After a tile's box changes, the navmesh tiles over it are marked stale so the next poke rebuilds them.
    internal static class PlacedWaterNav
    {
        private const float Tile = 32f, Step = 2f, BuildEvery = 1f;
        private static readonly HashSet<long> s_dirty = new HashSet<long>();
        private static readonly Dictionary<long, GameObject> s_boxes = new Dictionary<long, GameObject>();
        private static bool s_allDirty = true, s_checked, s_allowed;
        private static long s_seenSig = long.MinValue;
        private static float s_nextBuild, s_logAt;
        private static int s_built, s_cleared;

        private static readonly FieldInfo TilesField = AccessTools.Field(typeof(Pathfinding), "m_tiles");

        internal static void MarkAllDirty() => s_allDirty = true;
        internal static void MarkDirty(int tx, int tz) => s_dirty.Add(((long)tx << 32) ^ (uint)tz);

        private static bool Allowed()
        {
            if (s_checked) return s_allowed;
            s_checked = true;
            int water = LayerMask.NameToLayer("Water");
            var hits = new List<string>();
            foreach (var n in new[] { "character", "character_net", "character_ghost", "character_noenv", "character_trigger" })
            {
                int l = LayerMask.NameToLayer(n);
                if (l >= 0 && water >= 0 && !Physics.GetIgnoreLayerCollision(water, l)) hits.Add(n);
            }
            s_allowed = water >= 0 && hits.Count == 0;
            FiresLogger.LogInfo(s_allowed
                ? "[WaterField] nav box allowed: layer Water ignores every character layer, so placed-water nav colliders are built"
                : $"[WaterField] nav box REFUSED: layer Water collides with {string.Join(", ", hits)} (characters would stand on it); placed water stays plain ground on the navmesh");
            return s_allowed;
        }

        // Wherever the navmesh is built (any peer that runs AI, the dedicated server included).
        [HarmonyPatch(typeof(Pathfinding), "Update")]
        private static class Patch_PathfindingUpdate
        {
            private static void Postfix()
            {
                try { Tick(); }
                catch (System.Exception e)
                {
                    if (Time.unscaledTime >= s_failLogAt) { s_failLogAt = Time.unscaledTime + 30f; FiresLogger.LogWarning($"[WaterField] nav tick failed: {e.GetType().Name}: {e.Message}"); }
                }
            }
        }
        private static float s_failLogAt;

        // One tile per BuildEvery.
        private static void Tick()
        {
            if (Pathfinding.instance == null || Time.unscaledTime < s_nextBuild) return;
            s_nextBuild = Time.unscaledTime + BuildEvery;
            if (!Allowed()) return;
            var disks = WaterSheets.FreshDisks();
            if (WaterSheets.Signature != s_seenSig) { s_seenSig = WaterSheets.Signature; s_allDirty = true; }
            if (s_allDirty)
            {
                s_allDirty = false;
                var want = new HashSet<long>();
                foreach (var d in disks)
                {
                    if (!d.Water) continue;
                    for (int tx = Mathf.FloorToInt((d.X - d.R) / Tile); tx <= Mathf.FloorToInt((d.X + d.R) / Tile); tx++)
                        for (int tz = Mathf.FloorToInt((d.Z - d.R) / Tile); tz <= Mathf.FloorToInt((d.Z + d.R) / Tile); tz++)
                            want.Add(((long)tx << 32) ^ (uint)tz);
                }
                foreach (var k in new List<long>(s_boxes.Keys)) if (!want.Contains(k)) { Clear(k); s_dirty.Remove(k); }
                foreach (var k in want) s_dirty.Add(k);
                s_tileY.Clear();
                foreach (var d in disks)
                {
                    if (!d.Water) continue;
                    long k = ((long)Mathf.FloorToInt(d.X / Tile) << 32) ^ (uint)Mathf.FloorToInt(d.Z / Tile);
                    if (!s_tileY.TryGetValue(k, out var yy)) s_tileY[k] = yy = new Vector2(d.Y, d.Y);
                    s_tileY[k] = new Vector2(Mathf.Min(yy.x, d.Y), Mathf.Max(yy.y, d.Y));
                }
            }
            if (s_dirty.Count == 0) return;
            long key = 0;
            foreach (var k in s_dirty) { key = k; break; }
            s_dirty.Remove(key);
            Build(key);
        }

        private static readonly Dictionary<long, Vector2> s_tileY = new Dictionary<long, Vector2>();

        private static void Build(long key)
        {
            int tx = (int)(key >> 32), tz = (int)(uint)key;
            float ox = tx * Tile, oz = tz * Tile;
            float yLow = -10000f, yHigh = 10000f;
            if (s_tileY.TryGetValue(key, out var yr)) { yLow = yr.x - 4f; yHigh = yr.y + 1f; }
            var verts = new List<Vector3>();
            var tris = new List<int>();
            float lo = float.MaxValue, hi = float.MinValue;
            int cells = 0;
            for (float z = oz; z < oz + Tile; z += Step)
                for (float x = ox; x < ox + Tile; x += Step)
                {
                    float cx = x + Step * 0.5f, cz = z + Step * 0.5f;
                    if (!PlacedWaterField.TrySurface(cx, cz, yLow, yHigh, PlacedWaterField.Consumer.Nav, out float s, out _)) continue;
                    int b = verts.Count;
                    verts.Add(new Vector3(x, s, z)); verts.Add(new Vector3(x + Step, s, z));
                    verts.Add(new Vector3(x + Step, s, z + Step)); verts.Add(new Vector3(x, s, z + Step));
                    tris.Add(b); tris.Add(b + 2); tris.Add(b + 1); tris.Add(b); tris.Add(b + 3); tris.Add(b + 2);
                    lo = Mathf.Min(lo, s); hi = Mathf.Max(hi, s); cells++;
                }
            if (cells == 0) { Clear(key); Stale(ox, oz); return; }
            if (!s_boxes.TryGetValue(key, out var go) || go == null)
            {
                go = new GameObject($"fires_placed_water_nav_{tx}_{tz}");
                Object.DontDestroyOnLoad(go);
                go.layer = LayerMask.NameToLayer("Water");
                go.transform.position = Vector3.zero;
                go.AddComponent<MeshCollider>();
                s_boxes[key] = go;
            }
            var mc = go.GetComponent<MeshCollider>();
            var old = mc.sharedMesh;
            var mesh = new Mesh { name = "placed_water_nav" };
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mc.sharedMesh = mesh;
            if (old != null) Object.Destroy(old);
            s_built++;
            Stale(ox, oz);
            if (FiresLogger.DebugEnabled && Time.unscaledTime >= s_logAt)
            {
                s_logAt = Time.unscaledTime + 5f;
                FiresLogger.LogDebug($"[WaterField] nav box chunk ({tx},{tz}): {cells} cell(s) of {Step:0} m at y {lo:0.00}..{hi:0.00}, layer Water, navmesh tiles marked stale ({s_built} built, {s_cleared} cleared this session)");
            }
        }

        private static void Clear(long key)
        {
            if (s_boxes.TryGetValue(key, out var go) && go != null)
            {
                var mc = go.GetComponent<MeshCollider>();
                if (mc != null && mc.sharedMesh != null) Object.Destroy(mc.sharedMesh);
                Object.Destroy(go);
                s_cleared++;
            }
            s_boxes.Remove(key);
        }

        // Pathfinding rebuilds a tile only when it is poked more than m_updateInterval after its last build: setting the
        // build time far back over this area makes the next poke (any agent nearby) rebuild it with the new water.
        private static void Stale(float ox, float oz)
        {
            if (Pathfinding.instance == null || TilesField == null) return;
            if (!(TilesField.GetValue(Pathfinding.instance) is System.Collections.IDictionary tiles)) return;
            float size = Pathfinding.instance.m_tileSize;
            foreach (System.Collections.DictionaryEntry e in tiles)
            {
                if (!(e.Value is Pathfinding.NavMeshTile t)) continue;
                if (t.m_center.x + size * 0.5f < ox - 1f || t.m_center.x - size * 0.5f > ox + Tile + 1f) continue;
                if (t.m_center.z + size * 0.5f < oz - 1f || t.m_center.z - size * 0.5f > oz + Tile + 1f) continue;
                t.m_buildTime = -1000f;
            }
        }
    }

    // 0.2.1094 (Fire, shot at 37,598: "multiple water layers"): fat_water_layers lists the placed fresh-water disks covering the
    // crosshair column (level, kind, combiner, placement order) and the one surface every consumer gets there. Nothing is ever
    // removed automatically (player data): "fat_water_layers remove-hidden" removes, through the tools' own synced removal
    // (WaterOperationRPC.RequestRemoveWater, as Undo does), only the disks at the crosshair that are fully shadowed - never the kept
    // surface at any 1 m point they cover.
    internal static class PlacedWaterLayersCommand
    {
        [HarmonyPatch(typeof(Terminal), "InitTerminal")]
        private static class Register
        {
            private static bool s_done;
            private static void Postfix()
            {
                if (s_done) return;
                s_done = true;
                new Terminal.ConsoleCommand("fat_water_layers",
                    "[FiresAdminTerrain] placed water disks stacked over the crosshair column and the surface kept; 'remove-hidden' removes the fully shadowed ones",
                    args => args.Context?.AddString(Run(args.Length > 1 && args[1] == "remove-hidden")), isCheat: true);
            }
        }

        private static string Run(bool removeHidden)
        {
            var cam = GameCamera.instance != null ? GameCamera.instance.transform : null;
            if (cam == null) return "[WaterField] layers: no camera";
            int mask = LayerMask.GetMask("terrain", "Default", "static_solid", "piece");
            if (!Physics.Raycast(cam.position, cam.forward, out RaycastHit hit, 80f, mask, QueryTriggerInteraction.Ignore))
                return "[WaterField] layers: nothing solid under the crosshair";
            float x = hit.point.x, z = hit.point.z, floor = hit.point.y;
            float kept = PlacedWaterField.Canonical(x, z, floor - 0.5f, floor + PlacedWaterField.FloorSpan, true);
            var sb = new System.Text.StringBuilder();
            sb.Append($"[WaterField] layers at ({x:0.0},{floor:0.00},{z:0.0}): kept surface {(float.IsNaN(kept) ? "none (dry)" : kept.ToString("0.00"))}");
            var covering = new List<WaterSheets.DiskInfo>();
            foreach (var d in WaterSheets.FreshDisks())
            {
                if (!d.Water || d.Y <= floor - 0.5f) continue;
                float dx = x - d.X, dz = z - d.Z;
                if (dx * dx + dz * dz <= d.R * d.R) covering.Add(d);
            }
            covering.Sort((a, b) => b.Y.CompareTo(a.Y));
            sb.Append($"; {covering.Count} disk(s) cover this column:");
            int removed = 0;
            foreach (var d in covering)
            {
                bool hidden = FullyShadowed(d);
                sb.Append($"\n  y {d.Y:0.00} r {d.R:0.0} kind {d.Kind}{(d.Pond ? " (pond)" : "")} combiner '{d.Combiner}' order {d.Order} hash {d.Hash}{(hidden ? " - HIDDEN (a higher disk wins everywhere it covers)" : "")}");
                if (removeHidden && hidden && d.Hash != 0)
                {
                    try { WaterOperationRPC.RequestRemoveWater(d.Hash); removed++; sb.Append(" -> removed"); }
                    catch (System.Exception e) { sb.Append(" -> removal threw: " + e.Message); }
                }
            }
            if (removeHidden) sb.Append($"\n  removed {removed} hidden disk(s)");
            string s = sb.ToString();
            FiresLogger.LogInfo(s);
            return s;
        }

        // True when, at every 1 m point the disk covers, a HIGHER disk wins there (kept surface more than the 0.5 m blend band above
        // this disk). Conservative: a disk that is dry somewhere, or blends anywhere, is never called hidden.
        private static bool FullyShadowed(WaterSheets.DiskInfo d)
        {
            int r = Mathf.CeilToInt(d.R);
            for (int iz = -r; iz <= r; iz++)
                for (int ix = -r; ix <= r; ix++)
                {
                    if (ix * ix + iz * iz > d.R * d.R) continue;
                    float x = d.X + ix, z = d.Z + iz;
                    float bed = PlacedWaterField.BedUnder(x, z, d.Y, true);
                    if (float.IsNaN(bed)) continue;
                    float kept = PlacedWaterField.Canonical(x, z, bed - 0.5f, bed + PlacedWaterField.FloorSpan, true);
                    if (float.IsNaN(kept) || kept <= d.Y + 0.5f) return false;
                }
            return true;
        }
    }
}
