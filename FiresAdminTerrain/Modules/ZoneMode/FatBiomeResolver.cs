using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Zones
{
    // 0.2.1145 (Fire: "unify the biome answers"): WorldGenerator.GetBiome has two overloads and FAT patched them with different
    // sets. Vanilla's GetBiome(Vector3) is `=> GetBiome(point.x, point.z)` (WorldGenerator.cs:633), so the Vector3 answer is
    // every float patch (Fires generator, super flat's BiomeAt, BiomeOverride) PLUS the Vector3-only ones on top:
    //   TileBiomeOverride (prefix), BiomeOverride (Low), super flat's BiomeAtPoint (Last), BiomePaint's layer biome (Last:
    //   skylands, painted floors, caves) and the halo ring (Last).
    // The float overload missed the Vector3-only ones, so code asking (x, z) - Core's snow? no: Core asks Vector3 - but FAT's own
    // 2D callers (vegetation impostors, sink falls, grottos, cave env, …) and the tests saw a different biome on painted /
    // tiled / super-flat ground. The Vector3 answer is what players, Core, spawns and the HUD already see, so it is THE answer:
    // a standalone (x, z) call is resolved as GetBiome(new Vector3(x, SurfaceY, z)), i.e. the overworld surface at that column.
    // Not redirected (the old 2D answer stands, by design):
    //   * inside a Vector3 call (that IS the float step of the Vector3 chain; redirecting would recurse);
    //   * under a sky island on super flat (the 2D answer is the island's own biome: its vegetation, clutter and spawns);
    //   * inside the world's own lookups (BiomePaint resolving / corner scope / zone-mode source, super flat's WorldBiome);
    //   * a non-default oceanLevel / waterAlwaysOcean call (the Vector3 overload can't carry those).
    // The height-layered answers (skylands, caves, painted floors) need a height the float overload doesn't have: there the
    // float answer is the overworld SURFACE answer, which is what the self-check compares against.
    internal static class FatBiomeResolver
    {
        // 0.2.1201 (lead / [ghost], fly55 at (280,998): 8x ArgumentOutOfRangeException out of WorldGenerator.GetBiome(Vector3), the
        // stack ending in the patched DMD, then "[Biome] unified resolver disabled"). Each FAT GetBiome(Vector3) patch body now names
        // itself, the point and its own full stack when it throws (rethrown: same behaviour), and a finalizer lists every patch owner
        // on the method when the throw came from none of ours (another mod's patch, or vanilla).
        [ThreadStatic] private static bool t_noted;
        private static long s_throwLogAt;   // Stopwatch ticks: GetBiome also runs on worker threads (Unity Time is main-thread only)
        private static bool ThrowLogDue() { long now = Stopwatch.GetTimestamp(); if (now < Interlocked.Read(ref s_throwLogAt)) return false; Interlocked.Exchange(ref s_throwLogAt, now + 10 * Stopwatch.Frequency); return true; }
        private static int s_throws;
        internal static void NoteThrow(string patch, Vector3 p, Exception e)
        {
            t_noted = true;
            Interlocked.Increment(ref s_throws);
            if (!ThrowLogDue()) return;
            FiresLogger.LogWarning($"[Biome] GetBiome(Vector3) threw in FAT patch '{patch}' at ({p.x:0.0},{p.y:0.0},{p.z:0.0}) on thread {Thread.CurrentThread.ManagedThreadId} ({s_throws} throw(s) so far): {e} (0.2.1201)");
        }

        [HarmonyPatch(typeof(WorldGenerator), nameof(WorldGenerator.GetBiome), typeof(Vector3))]
        private static class GetBiomeThrowFinalizer
        {
            private static Exception Finalizer(Exception __exception, Vector3 __0)
            {
                if (__exception == null) return null;
                if (t_noted) { t_noted = false; return __exception; }
                Interlocked.Increment(ref s_throws);
                if (ThrowLogDue())
                {
                    string owners = "?";
                    try
                    {
                        var info = Harmony.GetPatchInfo(AccessTools.Method(typeof(WorldGenerator), nameof(WorldGenerator.GetBiome), new[] { typeof(Vector3) }));
                        if (info != null)
                        {
                            var l = new List<string>();
                            foreach (var x in info.Prefixes) l.Add("pre " + x.owner + ":" + x.PatchMethod.DeclaringType?.Name);
                            foreach (var x in info.Postfixes) l.Add("post " + x.owner + ":" + x.PatchMethod.DeclaringType?.Name);
                            foreach (var x in info.Transpilers) l.Add("trans " + x.owner + ":" + x.PatchMethod.DeclaringType?.Name);
                            owners = string.Join(", ", l);
                        }
                    }
                    catch (Exception e2) { owners = "(patch list failed: " + e2.Message + ")"; }
                    FiresLogger.LogWarning($"[Biome] GetBiome(Vector3) threw OUTSIDE every FAT patch at ({__0.x:0.0},{__0.y:0.0},{__0.z:0.0}) on thread {Thread.CurrentThread.ManagedThreadId} ({s_throws} throw(s)): {__exception}; patches on it: {owners} (0.2.1201)");
                }
                return __exception;
            }
        }

        internal static bool Enabled = true;
        [ThreadStatic] private static int t_inPoint, t_redirect, t_legacy;
        internal static int Redirects;

        internal static float SurfaceY =>
            VerdantsAscent.Terrain.Voxel.FatSuperFlat.On ? VerdantsAscent.Terrain.Voxel.FiresWorldProfile.SuperFlatHeight : 10000f;

        [HarmonyPatch(typeof(WorldGenerator), nameof(WorldGenerator.GetBiome), typeof(Vector3))]
        private static class PointScope
        {
            [HarmonyPriority(Priority.First)]
            private static void Prefix() => t_inPoint++;
            private static Exception Finalizer(Exception __exception) { t_inPoint--; return __exception; }
        }

        [HarmonyPatch(typeof(WorldGenerator), nameof(WorldGenerator.GetBiome), typeof(float), typeof(float), typeof(float), typeof(bool))]
        private static class FloatToPoint
        {
            // after every other float postfix (super flat's BiomeAt is Priority.Last = 0), so the redirect has the final word
            [HarmonyPriority(-100)]
            private static void Postfix(WorldGenerator __instance, float wx, float wy, float oceanLevel, bool waterAlwaysOcean, ref Heightmap.Biome __result)
            {
                // 0.2.1160 (lead's profile of Fire's session: "[Biome] float redirect: 13082 calls/s, hit 76 %, ms 60.42/s"): the hit
                // path paid the island/ring/tile probes, two BitConverter allocations and the epoch reads on EVERY call, and a moving
                // caller's exact (x, z) rarely repeats. Now: the cheap flags, then a 4 m CELL cache whose entry is only made when
                // the cell is uniform (its centre and 4 corners agree on biome, island and tile) - edges keep the exact path.
                if (!Enabled || t_inPoint > 0 || t_redirect > 0 || t_legacy > 0) return;
                if (waterAlwaysOcean || Mathf.Abs(oceanLevel - 0.02f) > 1e-6f) return;
                if (ZoneSystem.instance == null || !BiomePaint.OverrideLookupAllowed || BiomePaint.InCornerScope) return;
                if (VerdantsAscent.Terrain.Voxel.FatSuperFlat.InWorldBiome) return;
                long t0 = Stopwatch.GetTimestamp();
                Interlocked.Increment(ref s_calls);
                Census();
                try
                {
                    ValidateEpoch(t0);
                    if (CellGet(wx, wy, out int cv))
                    {
                        if (cv >= 0) { __result = (Heightmap.Biome)cv; Interlocked.Increment(ref s_hits); Interlocked.Increment(ref s_cellHits); return; }
                        if (cv == CellNoRedirect) { Interlocked.Increment(ref s_hits); Interlocked.Increment(ref s_cellHits); return; }
                        // CellMixed: the exact path below
                    }
                    else if (!RingWorld && TryUniformCell(__instance, wx, wy, out int uv))
                    {
                        CellPut(wx, wy, uv);
                        if (uv == CellNoRedirect) return;
                        if (uv >= 0) { __result = (Heightmap.Biome)uv; return; }
                    }
                    if (!ShouldRedirect(wx, wy, oceanLevel, waterAlwaysOcean)) return;
                    // 0.2.1146 (lead: the hot loops in FarVegImpostors / VoxelUnderwaterClutter): the redirect runs the whole Vector3
                    // chain, so its answer is cached per EXACT (x, z) - the same bits, so a cached answer can never differ from a
                    // fresh one at that point (no cell rounding: paint circle edges and tile vertices resolve below any cell size).
                    // Tile columns are never cached (a tile's per-vertex biome brush edits don't announce themselves).
                    long key = ((long)Bits(wx) << 32) | (uint)Bits(wy);
                    bool cacheable = !TileColumn(wx, wy);
                    if (cacheable && s_cache.TryGetValue(key, out int hit)) { __result = (Heightmap.Biome)hit; Interlocked.Increment(ref s_hits); return; }
                    t_redirect++;
                    try { __result = __instance.GetBiome(new Vector3(wx, SurfaceY, wy)); Redirects++; }
                    finally { t_redirect--; }
                    if (cacheable)
                    {
                        if (Interlocked.Increment(ref s_cacheAdds) >= CacheCap) { s_cache.Clear(); Interlocked.Exchange(ref s_cacheAdds, 0); }   // 0.2.1163: no Count (all locks)
                        s_cache[key] = (int)__result;
                    }
                }
                catch (Exception e) { Enabled = false; FiresLogger.LogWarning($"[Biome] unified resolver disabled ({e.GetType().Name}: {e.Message}); the float overload keeps its old answer"); }
                finally { Interlocked.Add(ref s_ticks, Stopwatch.GetTimestamp() - t0); LogMinute(); }
            }
        }

        // ---- 0.2.1146 the redirect cache: exact (x, z) keys; cleared on a biome-data epoch change, every 10 s (covers the
        // providers that can't announce a change: territory ForceBiome, zone-mode swaps), and when full ----
        private const int CacheCap = 65536;
        private static readonly ConcurrentDictionary<long, int> s_cache = new ConcurrentDictionary<long, int>();
        private static object s_epochPaint; private static string s_epochWorld; private static int s_epochTiles = -1;
        private static long s_epochClearAt;
        private static long s_calls, s_hits, s_ticks, s_logAt, s_cacheAdds;

        internal static void InvalidateCache() { s_cache.Clear(); CellClear(); }

        // ---- 0.2.1160 the 4 m cell cache: biome >= 0, CellNoRedirect (island column: the 2D answer stands), CellMixed (exact path) ----
        private const float CellSize = 4f;
        private const int CellNoRedirect = -1, CellMixed = -2;
        // 0.2.1163: a fixed direct-mapped array (no locks, no Count, no allocation): one long per slot packs the cell coords (24 + 24
        // bits), the value (+2, 11 bits) and an epoch (5 bits); a clear bumps the epoch (a full wipe only when it wraps). A collision
        // just overwrites - a miss rebuilds the cell. ConcurrentDictionary.Count (all locks) ran on every cell build before.
        private const int CellSlots = 1 << 16;
        private static readonly long[] s_cellArr = new long[CellSlots];
        private static int s_cellEpoch = 1;
        private static long s_cellClearAt, s_cellClears;
        private static void Coords(float x, float z, out int cx, out int cz) { cx = Mathf.FloorToInt(x / CellSize); cz = Mathf.FloorToInt(z / CellSize); }
        private static int Slot(int cx, int cz) => (int)((uint)(cx * 73856093 ^ cz * 19349663) & (CellSlots - 1));
        private static long Pack(int cx, int cz, int v, int ep) => ((long)(cx & 0xFFFFFF) << 40) | ((long)(cz & 0xFFFFFF) << 16) | ((long)((v + 2) & 0x7FF) << 5) | (long)(ep & 31);
        private static bool CellGet(float x, float z, out int v)
        {
            Coords(x, z, out int cx, out int cz);
            long e = Interlocked.Read(ref s_cellArr[Slot(cx, cz)]);
            v = (int)((e >> 5) & 0x7FF) - 2;
            return e != 0 && (e & 31) == (Thread.VolatileRead(ref s_cellEpoch) & 31) && (int)((e >> 40) & 0xFFFFFF) == (cx & 0xFFFFFF) && (int)((e >> 16) & 0xFFFFFF) == (cz & 0xFFFFFF);
        }
        private static void CellPut(float x, float z, int v)
        {
            Coords(x, z, out int cx, out int cz);
            Interlocked.Exchange(ref s_cellArr[Slot(cx, cz)], Pack(cx, cz, v, Thread.VolatileRead(ref s_cellEpoch)));
        }
        private static void CellClear()
        {
            int ep = Interlocked.Increment(ref s_cellEpoch);
            if ((ep & 31) == 0) { Interlocked.Increment(ref s_cellEpoch); System.Array.Clear(s_cellArr, 0, CellSlots); }
            Interlocked.Increment(ref s_cellClears);
        }
        private static long s_cellHits, s_cellMakes;
        private static long CellKey(float x, float z) => ((long)Mathf.FloorToInt(x / CellSize) << 32) | (uint)Mathf.FloorToInt(z / CellSize);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
        private struct FloatBits { [System.Runtime.InteropServices.FieldOffset(0)] public float F; [System.Runtime.InteropServices.FieldOffset(0)] public int I; }
        private static int Bits(float f) => new FloatBits { F = f }.I;   // no BitConverter.GetBytes allocation per call

        // The cell's centre and 4 corners (inset 0.05 m) through the same answer the redirect gives; uniform = all five agree.
        private static bool TryUniformCell(WorldGenerator wg, float x, float z, out int value)
        {
            value = CellMixed;
            Interlocked.Increment(ref s_cellMakes);
            float x0 = Mathf.Floor(x / CellSize) * CellSize, z0 = Mathf.Floor(z / CellSize) * CellSize;
            int first = int.MinValue;
            for (int i = 0; i < 5; i++)
            {
                float px = i == 0 ? x0 + CellSize * 0.5f : (i & 1) == 1 ? x0 + 0.05f : x0 + CellSize - 0.05f;
                float pz = i == 0 ? z0 + CellSize * 0.5f : i <= 2 ? z0 + 0.05f : z0 + CellSize - 0.05f;
                if (TileColumn(px, pz)) return true;   // tiles are never cached (CellMixed)
                int v;
                if (VerdantsAscent.Terrain.Voxel.FatSuperFlat.On && VerdantsAscent.Terrain.Voxel.FatSuperFlat.IslandColumn(px, pz)) v = CellNoRedirect;
                else
                {
                    t_redirect++;
                    try { v = (int)wg.GetBiome(new Vector3(px, SurfaceY, pz)); }
                    finally { t_redirect--; }
                }
                if (i == 0) first = v; else if (v != first) return true;   // an edge in the cell: CellMixed
            }
            value = first;
            return true;
        }

        // Epoch: the paint snapshot ref every call (one reference compare); the world name, the tile count and the 10 s clear at
        // most every 0.25 s (a string compare and a registry read per call were part of the 60 ms/s).
        private static long s_epochCheckAt;
        private static void ValidateEpoch(long now)
        {
            object paint = BiomePaint.SnapshotRef;
            bool check = now >= Interlocked.Read(ref s_epochCheckAt);
            if (ReferenceEquals(paint, s_epochPaint) && !check) return;
            Interlocked.Exchange(ref s_epochCheckAt, now + Stopwatch.Frequency / 4);
            string world = VerdantsAscent.Terrain.Voxel.FiresWorldProfile.WorldName;
            int tiles = VerdantsAscent.Terrain.Persistence.TileAnchorRegistry.Count;
            if (!ReferenceEquals(paint, s_epochPaint) || !string.Equals(world, s_epochWorld, StringComparison.Ordinal) || tiles != s_epochTiles
                || now >= Interlocked.Read(ref s_epochClearAt))
            {
                bool paintOrWorld = !ReferenceEquals(paint, s_epochPaint) || !string.Equals(world, s_epochWorld, StringComparison.Ordinal) || tiles != s_epochTiles;
                s_cache.Clear();
                // 0.2.1163: the 10 s blanket clear wiped every 4 m cell too (939 cells rebuilt/s in Fire's session = most of the 30 ms/s):
                // cells now clear on a real change (paint snapshot, world, tile count) and otherwise once a minute.
                if (paintOrWorld || now >= Interlocked.Read(ref s_cellClearAt)) { CellClear(); Interlocked.Exchange(ref s_cellClearAt, now + Stopwatch.Frequency * 60); }
                s_epochPaint = paint; s_epochWorld = world; s_epochTiles = tiles;
                Interlocked.Exchange(ref s_epochClearAt, now + Stopwatch.Frequency * 10);
            }
        }

        // Hot callers (Debug): one call in 2048 records its caller (the first frame outside WorldGenerator / this class / Harmony).
        private static readonly ConcurrentDictionary<string, int> s_callers = new ConcurrentDictionary<string, int>();
        private static long s_censusN;
        private static void Census()
        {
            if (!FiresLogger.DebugEnabled || (Interlocked.Increment(ref s_censusN) & 2047) != 0) return;
            try
            {
                var st = new System.Diagnostics.StackTrace(2, false);
                for (int i = 0; i < st.FrameCount && i < 12; i++)
                {
                    var m = st.GetFrame(i)?.GetMethod();
                    var t = m?.DeclaringType;
                    if (m == null || t == null || t == typeof(WorldGenerator) || t == typeof(FatBiomeResolver) || t.Name.Contains("DMD") || m.Name.Contains("DMD") || (t.Namespace ?? "").StartsWith("HarmonyLib")) continue;
                    string k = t.Name + "." + m.Name;
                    s_callers.AddOrUpdate(k, 1, (_, n) => n + 1);
                    break;
                }
            }
            catch { }
        }

        private static bool TileColumn(float x, float z)
        {
            try
            {
                var p = new Vector3(x, 0f, z);
                return VerdantsAscent.Terrain.Persistence.TileAnchorRegistry.GetAt(VerdantsAscent.Terrain.Persistence.TileAnchorRegistry.WorldToTileIndex(p)) != null;
            }
            catch { return true; }
        }

        // "[Biome] float redirect: n calls/s, hit h %, ms m/s", once a minute while redirects happen (any thread).
        private static void LogMinute()
        {
            long now = Stopwatch.GetTimestamp(), at = Interlocked.Read(ref s_logAt);
            if (at == 0) { Interlocked.CompareExchange(ref s_logAt, now + Stopwatch.Frequency * 30, 0); return; }
            if (now < at || Interlocked.CompareExchange(ref s_logAt, now + Stopwatch.Frequency * 30, at) != at) return;
            long calls = Interlocked.Exchange(ref s_calls, 0), hits = Interlocked.Exchange(ref s_hits, 0), ticks = Interlocked.Exchange(ref s_ticks, 0);
            long cellHits = Interlocked.Exchange(ref s_cellHits, 0), makes = Interlocked.Exchange(ref s_cellMakes, 0);
            double secs = 30.0;
            string top = "";
            try
            {
                var list = new List<KeyValuePair<string, int>>(s_callers);
                list.Sort((a, b) => b.Value.CompareTo(a.Value));
                var parts = new List<string>();
                for (int i = 0; i < list.Count && i < 6; i++) parts.Add($"{list[i].Key} {list[i].Value * 2048 / secs:0}/s");
                top = parts.Count > 0 ? "; hot callers (1-in-2048 sample, est.): " + string.Join(", ", parts) : "";
                s_callers.Clear();
            }
            catch { }
            FiresLogger.LogDebug($"[Biome] float redirect: {calls / secs:0} calls/s, hit {(calls > 0 ? 100.0 * hits / calls : 0):0} % (4 m cell {(calls > 0 ? 100.0 * cellHits / calls : 0):0} %, cells made {makes / secs:0.0}/s, cells cleared {Interlocked.Exchange(ref s_cellClears, 0)}x), ms {ticks * 1000.0 / Stopwatch.Frequency / secs:0.00}/s (exact cache {s_cache.Count}){top} (0.2.1163)");
        }

        // 0.2.1146 (lead: on a halo-ring world the y 10000 probe missed the ring): a ring world keeps the float overload's
        // pre-1145 answer everywhere (the ring biome is a Vector3-only, height-dependent field; a float call can't carry the
        // height), exactly like the sky-island columns.
        private static bool RingWorld
        {
            get { try { return VerdantsAscent.Terrain.Voxel.FatHaloArc.RingOnly; } catch { return false; } }
        }

        private static bool ShouldRedirect(float x, float z, float oceanLevel, bool waterAlwaysOcean)
        {
            if (!Enabled || t_inPoint > 0 || t_redirect > 0 || t_legacy > 0) return false;
            if (waterAlwaysOcean || Mathf.Abs(oceanLevel - 0.02f) > 1e-6f) return false;
            if (ZoneSystem.instance == null || !BiomePaint.OverrideLookupAllowed || BiomePaint.InCornerScope) return false;
            if (VerdantsAscent.Terrain.Voxel.FatSuperFlat.InWorldBiome) return false;
            if (VerdantsAscent.Terrain.Voxel.FatSuperFlat.On && VerdantsAscent.Terrain.Voxel.FatSuperFlat.IslandColumn(x, z)) return false;
            if (RingWorld) return false;
            return true;
        }

        // the float overload's answer from before 0.2.1145 (the "what changed" report)
        internal static Heightmap.Biome LegacyFloat(WorldGenerator wg, float x, float z)
        {
            t_legacy++;
            try { return wg.GetBiome(x, z); }
            finally { t_legacy--; }
        }

        // ---- the self-check: float vs Vector3 at the surface on a grid + every paint circle's centre and edge ----
        internal struct CheckResult
        {
            internal int Points, Disagree, IslandSkipped, RingSkipped, Changed;
            internal List<string> FirstDisagree;
            internal Dictionary<string, int> ChangedPairs;
        }

        internal static CheckResult Check(WorldGenerator wg, float half = 3000f, float step = 64f)
        {
            var r = new CheckResult { FirstDisagree = new List<string>(), ChangedPairs = new Dictionary<string, int>() };
            var pts = new List<Vector2>();
            for (float x = -half; x <= half; x += step) for (float z = -half; z <= half; z += step) pts.Add(new Vector2(x, z));
            try
            {
                foreach (var c in BiomePaint.CircleSnapshot)
                {
                    pts.Add(new Vector2(c.X, c.Z));
                    for (int a = 0; a < 8; a++)
                    {
                        float ang = a * Mathf.PI / 4f;
                        for (float k = -2f; k <= 2f; k += 4f)
                            pts.Add(new Vector2(c.X + Mathf.Cos(ang) * (c.R + k), c.Z + Mathf.Sin(ang) * (c.R + k)));
                    }
                }
            }
            catch { }
            bool flat = VerdantsAscent.Terrain.Voxel.FatSuperFlat.On;
            bool ring = RingWorld;
            float y = SurfaceY;
            InvalidateCache();   // 0.2.1146: the check compares fresh answers
            foreach (var p in pts)
            {
                if (ring) { r.RingSkipped++; continue; }   // 0.2.1146: a ring world keeps the pre-1145 float answers (not redirected)
                if (flat && VerdantsAscent.Terrain.Voxel.FatSuperFlat.IslandColumn(p.x, p.y)) { r.IslandSkipped++; continue; }
                r.Points++;
                var f = wg.GetBiome(p.x, p.y);
                var v = wg.GetBiome(new Vector3(p.x, y, p.y));
                if (f != v)
                {
                    r.Disagree++;
                    if (r.FirstDisagree.Count < 10) r.FirstDisagree.Add($"({p.x:0},{p.y:0}) float={f} vector={v}");
                }
                var old = LegacyFloat(wg, p.x, p.y);
                if (old != f)
                {
                    r.Changed++;
                    string key = $"{old}->{f}";
                    r.ChangedPairs.TryGetValue(key, out int n);
                    r.ChangedPairs[key] = n + 1;
                }
            }
            return r;
        }

        internal static string Describe(CheckResult r)
        {
            var pairs = new List<string>();
            foreach (var kv in r.ChangedPairs) pairs.Add($"{kv.Key} at {kv.Value}");
            return $"overload self-check: {r.Disagree} of {r.Points} points disagree (island columns skipped {r.IslandSkipped}, ring-world points skipped {r.RingSkipped}, surface y {SurfaceY:0})"
                + (r.FirstDisagree.Count > 0 ? "; first: " + string.Join("; ", r.FirstDisagree) : "")
                + $" | float path changed at {r.Changed} points vs the pre-1145 answer" + (pairs.Count > 0 ? " (by biome pair: " + string.Join(", ", pairs) + ")" : "");
        }


        // once per world load, on every peer, 20 s after the zone system starts (paint, tiles and islands are loaded by then)
        [HarmonyPatch(typeof(ZoneSystem), "Start")]
        private static class SelfCheckOnLoad
        {
            private static void Postfix(ZoneSystem __instance) { if (__instance != null) __instance.StartCoroutine(Run()); }

            private static IEnumerator Run()
            {
                yield return new WaitForSecondsRealtime(20f);
                var wg = WorldGenerator.instance;
                if (wg == null) yield break;
                CheckResult r;
                try { r = Check(wg); }
                catch (Exception e) { FiresLogger.LogWarning($"[Biome] overload self-check threw: {e.Message}"); yield break; }
                FiresLogger.LogInfo("[Biome] " + Describe(r));
            }
        }
    }

    internal static partial class BiomePaint
    {
        internal static Circle[] CircleSnapshot => s_snapshot.Circles;   // 0.2.1145: FatBiomeResolver's self-check samples paint edges
        internal static object SnapshotRef => s_snapshot;                 // 0.2.1146: FatBiomeResolver's cache epoch (a new snapshot = paint changed)
    }
}
