using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Terrain.Voxel;

namespace VerdantsAscent.Terrain.FarForest
{
    // 0.2.1415 FarLands bridge ([lead] 11-08: "feed FAT's EXISTING far-forest placement into [visual]'s Core impostor engine as a
    // bridge, then switch to the vanilla replay when its MatchTest passes"). FAT decides where trees stand, Core draws them.
    //   - Source: FarVegImpostors' placement (its species + its pure BuildTile), 512 m cells built on a worker, nearest first.
    //   - Sink 1: Core's impostor engine (Tools\SPEC_Core_ImpostorEngine.md draft 2), bound BY PRESENCE: the type
    //     FiresCore.Far.ImpostorEngine, ApiVersion >= 1, RegisterKind / SetCell / ClearCell, gated on the LIVE flag
    //     CoreFeatures.IsLive("FarImpostors") re-read every pass (never on ApiVersion alone). Core's own [Far Impostors] Enabled
    //     queues our calls while off (its spec §1), so this does not read it.
    //   - Sink 2: [generator]'s FatFarCanopy (#10 canopy shade), by presence: SetCell(long, CanopyTree[]) / ClearCell(long).
    //   cellKey = ((long)cx << 32) | (uint)cz; SetCell on the main thread, once per cell per build; ClearCell beyond reach + 1024 m.
    // [Far Forest] Enable gates it (default off). With no sink present it stands down with one Info WHY and builds nothing.
    internal static class FarBridge
    {
        private const string Owner = "FAT";
        private const float Cell = 512f, ClearMargin = 1024f;

        private static bool s_started, s_saidDown;
        private static float s_nextScan, s_nextLog, s_nextTry;
        private static ImpostorSink s_core;
        private static CanopySink s_canopy;
        private static int[] s_kindOfSpecies = new int[0];
        private static readonly HashSet<long> s_live = new HashSet<long>(), s_queued = new HashSet<long>();
        private static readonly ConcurrentQueue<long> s_todo = new ConcurrentQueue<long>();
        private static readonly ConcurrentQueue<Built> s_done = new ConcurrentQueue<Built>();
        private static readonly AutoResetEvent s_wake = new AutoResetEvent(false);
        private static Thread s_worker;
        private static int s_cellsSent, s_instancesSent;
        private static double s_workerMs; private static int s_workerCells;

        private sealed class Built { public long Key; public Bounds Bounds; public object CoreArray; public object CanopyArray; public int Count; }

        // 0.2.1415 ([visual]'s final API, Core 0.2.1203): FAT, an owner of FarImpostors, must call each Core far system's
        // Initialize(ConfigFile) once, so their [Far ...] sections bind (all default off). By presence: an older Core has none of them.
        // 0.2.1425 ([generator] M22: Core's Far\ moves into EBM 1.2.210 and Core deletes it): Core's types are looked up quietly in Core's own
        // assembly with GetType(name, false). AccessTools.TypeByName logged "Could not find type named ..." for each missing one.
        private static System.Reflection.Assembly s_coreAsm;
        internal static Type CoreType(string name)
        {
            if (s_coreAsm == null)
            {
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies()) { try { if (a.GetName().Name == "FiresUnifiedCore") { s_coreAsm = a; break; } } catch { } }
            }
            try { return s_coreAsm?.GetType(name, false); } catch { return null; }
        }

        internal static void InitCoreFar(BepInEx.Configuration.ConfigFile cfg)
        {
            var done = new List<string>();
            foreach (var name in new[] { "FiresCore.Far.ImpostorEngine", "FiresCore.Far.FarCache", "FiresCore.Far.FarLights", "FiresCore.Far.FarBuildings" })
            {
                var t = CoreType(name);
                var m = t?.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(BepInEx.Configuration.ConfigFile) }, null);
                if (m == null) continue;
                try { m.Invoke(null, new object[] { cfg }); done.Add(t.Name); }
                catch (Exception e) { FiresLogger.LogWarning($"[FarForest] {name}.Initialize threw: {e.GetBaseException().Message}"); }
            }
            FiresLogger.LogInfo(done.Count > 0 ? $"[FarForest] Core far systems initialized for FAT: {string.Join(", ", done)}." : "[FarForest] no Core far systems in this Core build (older Core, or EBM 1.2.210+ owns them): nothing to initialize.");
        }

        // ---------------------------------------------------------------- 0.2.1417 seen trees (spec §3, [lead] approved 11-08)
        // Every zone this client has loaded (settled 5 s) is read from its real ZDOs: the bridge species' trees there REPLACE the
        // placement's trees for that zone, for the rest of the session. Felled trees stay gone, planted / regrown ones appear, and the
        // far tree is the real tree wherever Fire has been, whatever EWD or the generator did. Loaded zones are re-read every 10 s
        // (felling); a changed zone re-sends its cell at most once per 2 s; Core gets MarkZoneStanding(zone, true) after each read.
        private static readonly ConcurrentDictionary<long, FarVegImpostors.BridgeTree[]> s_seen = new ConcurrentDictionary<long, FarVegImpostors.BridgeTree[]>();
        private static readonly Dictionary<long, float> s_zoneFirst = new Dictionary<long, float>(), s_zoneNextRead = new Dictionary<long, float>();
        private static readonly Dictionary<long, float> s_cellSentAt = new Dictionary<long, float>();
        private static readonly HashSet<long> s_dirty = new HashSet<long>();
        private static readonly List<ZDO> s_zdos = new List<ZDO>();
        private static readonly List<FarVegImpostors.BridgeTree> s_read = new List<FarVegImpostors.BridgeTree>();
        private static Dictionary<int, int> s_speciesByHash = new Dictionary<int, int>();
        private static float s_nextObserve;
        private static int s_zonesSeen, s_zoneReads, s_zoneChanges;
        private const float ZoneSize = 64f, Settle = 5f, Reread = 10f, Resend = 2f;

        private static long ZoneKey(int zx, int zz) => ((long)zx << 32) | (uint)zz;
        private static int ZoneOf(float w) => Mathf.FloorToInt((w + ZoneSize * 0.5f) / ZoneSize);

        private static void Observe(Vector3 c)
        {
            var zs = ZoneSystem.instance; var zdm = ZDOMan.instance;
            if (zs == null || zdm == null || s_speciesByHash.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            int czx = ZoneOf(c.x), czz = ZoneOf(c.z), r = 2;
            for (int dz = -r; dz <= r; dz++)
                for (int dx = -r; dx <= r; dx++)
                {
                    var z = new Vector2s(czx + dx, czz + dz);
                    long zk = ZoneKey(z.x, z.y);
                    if (!zs.IsZoneLoaded(z)) { s_zoneFirst.Remove(zk); continue; }
                    if (!s_zoneFirst.TryGetValue(zk, out float t0)) { s_zoneFirst[zk] = now; continue; }
                    if (now - t0 < Settle) continue;
                    if (s_zoneNextRead.TryGetValue(zk, out float next) && now < next) continue;
                    s_zoneNextRead[zk] = now + Reread;
                    ReadZone(z, zk, zdm);
                }
        }

        private static void ReadZone(Vector2s z, long zk, ZDOMan zdm)
        {
            s_zdos.Clear(); s_read.Clear();
            zdm.FindSectorObjects(z, new SimulationDistance(0, 0), s_zdos);
            foreach (var zdo in s_zdos)
            {
                if (zdo == null || !s_speciesByHash.TryGetValue(zdo.GetPrefab(), out int si)) continue;
                Vector3 p = zdo.GetPosition();
                if (ZoneOf(p.x) != z.x || ZoneOf(p.z) != z.y) continue;
                Vector3 sc = zdo.GetVec3(ZDOVars.s_scaleHash, Vector3.one);
                s_read.Add(new FarVegImpostors.BridgeTree { Species = si, Pos = p, Yaw = zdo.GetRotation().eulerAngles.y, Scale = sc.x > 0f ? sc.x : 1f });
            }
            s_zoneReads++;
            var arr = s_read.ToArray();
            bool had = s_seen.TryGetValue(zk, out var old);
            if (had && Same(old, arr)) return;
            s_seen[zk] = arr;
            if (!had) s_zonesSeen++; else s_zoneChanges++;
            s_core?.MarkZoneStanding(z);
            long cell = Key(Mathf.FloorToInt((z.x * ZoneSize) / Cell), Mathf.FloorToInt((z.y * ZoneSize) / Cell));
            // a zone straddles at most one cell edge per axis: mark every cell its square touches
            for (int ex = -1; ex <= 1; ex += 2)
                for (int ez = -1; ez <= 1; ez += 2)
                    s_dirty.Add(Key(Mathf.FloorToInt((z.x * ZoneSize + ex * (ZoneSize * 0.5f - 0.01f)) / Cell), Mathf.FloorToInt((z.y * ZoneSize + ez * (ZoneSize * 0.5f - 0.01f)) / Cell)));
            s_dirty.Add(cell);
        }

        private static bool Same(FarVegImpostors.BridgeTree[] a, FarVegImpostors.BridgeTree[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i].Species != b[i].Species || (a[i].Pos - b[i].Pos).sqrMagnitude > 0.0001f) return false;
            return true;
        }

        // Main thread, from Scan: live cells whose zones changed go back to the worker (at most once per 2 s each).
        private static void RequeueDirty()
        {
            if (s_dirty.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            var done = new List<long>();
            foreach (var k in s_dirty)
            {
                if (!s_live.Contains(k)) { if (!s_queued.Contains(k)) done.Add(k); continue; }   // not built yet: its first build reads the seen zones anyway
                if (s_queued.Contains(k)) continue;
                if (s_cellSentAt.TryGetValue(k, out float at) && now - at < Resend) continue;
                s_queued.Add(k); s_todo.Enqueue(k); done.Add(k);
            }
            foreach (var k in done) s_dirty.Remove(k);
            if (done.Count > 0) s_wake.Set();
        }

        // Worker: the cell's trees = the placement's trees outside seen zones + the real trees of every seen zone inside the cell.
        private static void MergeSeen(int cx, int cz, List<FarVegImpostors.BridgeTree> trees)
        {
            if (s_seen.IsEmpty) return;
            trees.RemoveAll(t => s_seen.ContainsKey(ZoneKey(ZoneOf(t.Pos.x), ZoneOf(t.Pos.z))));
            float x0 = cx * Cell, z0 = cz * Cell;
            int zx0 = ZoneOf(x0), zx1 = ZoneOf(x0 + Cell - 0.01f), zz0 = ZoneOf(z0), zz1 = ZoneOf(z0 + Cell - 0.01f);
            for (int zz = zz0; zz <= zz1; zz++)
                for (int zx = zx0; zx <= zx1; zx++)
                {
                    if (!s_seen.TryGetValue(ZoneKey(zx, zz), out var arr)) continue;
                    foreach (var t in arr)
                        if (t.Pos.x >= x0 && t.Pos.x < x0 + Cell && t.Pos.z >= z0 && t.Pos.z < z0 + Cell) trees.Add(t);
                }
        }

        internal static long Key(int cx, int cz) => ((long)cx << 32) | (uint)cz;
        private static void Unkey(long k, out int cx, out int cz) { cx = (int)(k >> 32); cz = (int)(uint)k; }

        // From FarMatchTest's ZoneSystem.Update postfix (one hook).
        internal static void Tick()
        {
            if (FarForest.Enable == null || !FarForest.Enable.Value) { if (s_started) Stop("Enable turned off"); return; }
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return;   // a dedicated server draws nothing
            var cam = Camera.main;
            if (cam == null || ZoneSystem.instance == null) return;
            if (!s_started) { if (Time.realtimeSinceStartup < s_nextTry) return; s_nextTry = Time.realtimeSinceStartup + 10f; if (!Start()) return; }

            DrainDone();
            float now = Time.realtimeSinceStartup;
            if (now >= s_nextScan) { s_nextScan = now + 1f; Scan(cam.transform.position); }
            if (now >= s_nextObserve && Player.m_localPlayer != null) { s_nextObserve = now + 1f; Observe(Player.m_localPlayer.transform.position); RequeueDirty(); }   // 0.2.1417 seen trees
            if (now >= s_nextLog && s_cellsSent > 0)
            {
                s_nextLog = now + 30f;
                FiresLogger.LogDebug($"[FarForest] bridge: {s_live.Count} cell(s) live, {s_cellsSent} sent, {s_instancesSent} instance(s) sent; worker {(s_workerCells > 0 ? s_workerMs / s_workerCells : 0):0.0} ms/cell avg; " +
                                     $"sinks: core {(s_core != null ? "on" : "none")}, canopy {(s_canopy != null ? "on" : "none")}; seen trees: {s_zonesSeen} zone(s) read from real ZDOs ({s_zoneReads} reads, {s_zoneChanges} changed since)");
            }
        }

        private static bool Start()
        {
            bool coreLive = false;
            try { coreLive = FiresCore.Config.CoreFeatures.IsLive("FarImpostors"); } catch { }
            s_core = coreLive ? ImpostorSink.TryBind(out string coreWhy) : null;
            string why = coreLive ? (s_core == null ? ImpostorSink.LastWhy : null) : "CoreFeatures FarImpostors is not live (no owner / older Core)";
            s_canopy = CanopySink.TryBind();
            if (s_core == null && s_canopy == null)
            {
                if (!s_saidDown) { s_saidDown = true; FiresLogger.LogInfo($"[FarForest] bridge stands down: no sink. Core impostor engine: {why}; FatFarCanopy: not present."); }
                return false;
            }
            if (!FarVegImpostors.BridgeWorldReady || !FarVegImpostors.EnsureBridgeSpecies()) return false;   // retried next tick
            int n = FarVegImpostors.SpeciesCount;
            s_kindOfSpecies = new int[n];
            var byHash = new Dictionary<int, int>();
            for (int si = 0; si < n; si++) { var pn = FarVegImpostors.SpeciesPrefab(si); if (pn != null) byHash[pn.GetStableHashCode()] = si; }
            s_speciesByHash = byHash;
            int registered = 0;
            for (int si = 0; si < n; si++)
            {
                s_kindOfSpecies[si] = -1;
                if (s_core == null) continue;
                var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(FarVegImpostors.SpeciesPrefab(si)) : null;
                if (prefab == null) continue;
                s_kindOfSpecies[si] = s_core.RegisterKind(Owner, prefab);
                if (s_kindOfSpecies[si] >= 0) registered++;
            }
            s_started = true;
            s_saidDown = false;
            if (s_worker == null) { s_worker = new Thread(WorkerLoop) { IsBackground = true, Name = "FAT FarBridge", Priority = System.Threading.ThreadPriority.BelowNormal }; s_worker.Start(); }
            FiresLogger.LogInfo($"[FarForest] bridge on: FarVegImpostors placement -> {(s_core != null ? $"Core impostor engine (ApiVersion {s_core.ApiVersion}, {registered} of {n} kind(s) registered)" : "no Core engine (" + why + ")")}" +
                                $"{(s_canopy != null ? " + FatFarCanopy" : "")}; 512 m cells to {FarForest.ReachMeters.Value:0} m, built on a worker.");
            return true;
        }

        private static void Stop(string why)
        {
            foreach (var k in s_live) { s_core?.ClearCell(Owner, k); s_canopy?.ClearCell(k); }
            s_live.Clear(); s_queued.Clear();
            while (s_todo.TryDequeue(out _)) { }
            while (s_done.TryDequeue(out _)) { }
            s_started = false;
            FiresLogger.LogInfo($"[FarForest] bridge off ({why}): every cell cleared.");
        }

        private static readonly List<(long key, float d)> s_scan = new List<(long, float)>();

        private static void Scan(Vector3 c)
        {
            float reach = FarForest.ReachMeters.Value;
            int a0 = Mathf.FloorToInt((c.x - reach) / Cell), a1 = Mathf.FloorToInt((c.x + reach) / Cell);
            int b0 = Mathf.FloorToInt((c.z - reach) / Cell), b1 = Mathf.FloorToInt((c.z + reach) / Cell);
            s_scan.Clear();
            for (int z = b0; z <= b1; z++)
                for (int x = a0; x <= a1; x++)
                {
                    long k = Key(x, z);
                    if (s_live.Contains(k) || s_queued.Contains(k)) continue;
                    float dx = (x + 0.5f) * Cell - c.x, dz = (z + 0.5f) * Cell - c.z;
                    s_scan.Add((k, dx * dx + dz * dz));
                }
            s_scan.Sort((p, q) => p.d.CompareTo(q.d));
            foreach (var t in s_scan) { s_queued.Add(t.key); s_todo.Enqueue(t.key); }
            if (s_scan.Count > 0) s_wake.Set();
            // Clear beyond reach + 1024 m (hysteresis).
            float far = reach + ClearMargin;
            var drop = new List<long>();
            foreach (var k in s_live)
            {
                Unkey(k, out int x, out int z);
                float dx = Mathf.Abs((x + 0.5f) * Cell - c.x), dz = Mathf.Abs((z + 0.5f) * Cell - c.z);
                if (dx > far || dz > far) drop.Add(k);
            }
            foreach (var k in drop) { s_live.Remove(k); s_core?.ClearCell(Owner, k); s_canopy?.ClearCell(k); }
        }

        private static void DrainDone()
        {
            int budget = 2;   // SetCell calls a frame; Core time-slices the upload itself (UploadBudgetMs)
            while (budget-- > 0 && s_done.TryDequeue(out var b))
            {
                s_queued.Remove(b.Key);
                if (!s_started) continue;
                try
                {
                    if (s_core != null && b.CoreArray != null) s_core.SetCell(Owner, b.Key, b.Bounds, b.CoreArray);
                    if (s_canopy != null && b.CanopyArray != null) s_canopy.SetCell(b.Key, b.CanopyArray);
                }
                catch (Exception e) { FiresLogger.LogWarning($"[FarForest] bridge SetCell threw (cell {b.Key}): {e.GetBaseException().Message}"); continue; }
                s_live.Add(b.Key);
                s_cellSentAt[b.Key] = Time.realtimeSinceStartup;
                s_cellsSent++; s_instancesSent += b.Count;
            }
        }

        private static void WorkerLoop()
        {
            var trees = new List<FarVegImpostors.BridgeTree>(4096);
            while (true)
            {
                if (!s_todo.TryDequeue(out long key)) { s_wake.WaitOne(1000); continue; }
                if (!s_started) continue;
                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    Unkey(key, out int cx, out int cz);
                    trees.Clear();
                    FarVegImpostors.BuildBridgeCell(cx, cz, trees);
                    MergeSeen(cx, cz, trees);   // 0.2.1417: real trees of seen zones replace the placement there
                    float lo = float.MaxValue, hi = float.MinValue;
                    foreach (var t in trees) { lo = Mathf.Min(lo, t.Pos.y); hi = Mathf.Max(hi, t.Pos.y + FarVegImpostors.SpeciesHeight(t.Species) * t.Scale); }
                    if (trees.Count == 0) { lo = 0f; hi = 0f; }
                    var b = new Built { Key = key, Count = trees.Count, Bounds = new Bounds(new Vector3((cx + 0.5f) * Cell, (lo + hi) * 0.5f, (cz + 0.5f) * Cell), new Vector3(Cell, Mathf.Max(1f, hi - lo), Cell)) };
                    var core = s_core; var canopy = s_canopy; var kinds = s_kindOfSpecies;
                    if (core != null) b.CoreArray = core.MakeArray(trees, kinds);
                    if (canopy != null) b.CanopyArray = canopy.MakeArray(trees);
                    s_done.Enqueue(b);
                    s_workerMs += sw.Elapsed.TotalMilliseconds; s_workerCells++;
                }
                catch (Exception e) { FiresLogger.LogWarning($"[FarForest] bridge worker threw (cell {key}): {e.GetBaseException().Message}"); s_done.Enqueue(new Built { Key = key }); }
            }
        }

        // ---------------------------------------------------------------- Core impostor engine, by presence
        private sealed class ImpostorSink
        {
            internal static string LastWhy = "";
            public int ApiVersion;
            private MethodInfo m_register, m_setCell, m_clearCell;
            private Type m_inst, m_opts;
            private FieldInfo f_pos, f_scale, f_yaw, f_kind, f_tx, f_tz, o_mid, o_distant, o_reach;

            private Type m_engine;

            internal static ImpostorSink TryBind(out string why)
            {
                why = LastWhy = "";
                var t = CoreType("FiresCore.Far.ImpostorEngine");   // 0.2.1425: quiet (was AccessTools.TypeByName: a warning per retry once Core's Far\ is gone)
                if (t == null) { why = LastWhy = "FiresCore.Far.ImpostorEngine not in this Core build (EBM 1.2.210+ owns the far systems)"; return null; }
                var s = new ImpostorSink { m_engine = t };
                var apiF = t.GetField("ApiVersion", BindingFlags.Public | BindingFlags.Static);
                var apiP = t.GetProperty("ApiVersion", BindingFlags.Public | BindingFlags.Static);
                object api = apiF != null ? apiF.GetValue(null) : apiP?.GetValue(null);
                s.ApiVersion = api is int i ? i : 0;
                if (s.ApiVersion < 1) { why = LastWhy = $"ApiVersion {s.ApiVersion} < 1"; return null; }
                var ms = t.GetMethods(BindingFlags.Public | BindingFlags.Static);
                s.m_register = ms.FirstOrDefault(m => m.Name == "RegisterKind" && m.GetParameters().Length == 3 && m.GetParameters()[1].ParameterType == typeof(GameObject));
                s.m_setCell = ms.FirstOrDefault(m => m.Name == "SetCell" && m.GetParameters().Length == 4 && m.GetParameters()[3].ParameterType.IsArray);
                s.m_clearCell = ms.FirstOrDefault(m => m.Name == "ClearCell" && m.GetParameters().Length == 2);
                if (s.m_register == null || s.m_setCell == null || s.m_clearCell == null)
                { why = LastWhy = $"API members missing (RegisterKind {s.m_register != null}, SetCell(…, T[]) {s.m_setCell != null}, ClearCell {s.m_clearCell != null})"; return null; }
                s.m_opts = s.m_register.GetParameters()[2].ParameterType;
                s.m_inst = s.m_setCell.GetParameters()[3].ParameterType.GetElementType();
                FieldInfo F(Type ty, string n) => ty.GetField(n, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase);
                s.f_pos = F(s.m_inst, "position"); s.f_scale = F(s.m_inst, "scale"); s.f_yaw = F(s.m_inst, "yaw"); s.f_kind = F(s.m_inst, "kind");
                s.f_tx = F(s.m_inst, "tiltX"); s.f_tz = F(s.m_inst, "tiltZ");
                if (s.f_pos == null || s.f_scale == null || s.f_yaw == null || s.f_kind == null)
                { why = LastWhy = $"{s.m_inst.Name} fields missing (position {s.f_pos != null}, scale {s.f_scale != null}, yaw {s.f_yaw != null}, kind {s.f_kind != null})"; return null; }
                s.o_mid = F(s.m_opts, "MidLodSource"); s.o_distant = F(s.m_opts, "Distant"); s.o_reach = F(s.m_opts, "ReachScale");
                return s;
            }

            public int RegisterKind(string owner, GameObject prefab)
            {
                var defF = m_opts.GetField("Default", BindingFlags.Public | BindingFlags.Static); var defP = m_opts.GetProperty("Default", BindingFlags.Public | BindingFlags.Static);
                object opts = defF != null ? defF.GetValue(null) : defP != null ? defP.GetValue(null) : Activator.CreateInstance(m_opts);   // KindOptions.Default (Core 0.2.1203)
                try
                {
                    if (o_mid != null && o_mid.FieldType.IsEnum && Enum.GetNames(o_mid.FieldType).Contains("AuthoredFarLod")) o_mid.SetValue(opts, Enum.Parse(o_mid.FieldType, "AuthoredFarLod"));
                    var nv = prefab.GetComponent<ZNetView>();
                    if (o_distant != null) o_distant.SetValue(opts, nv != null && nv.m_distant);
                    if (o_reach != null) o_reach.SetValue(opts, 1f);
                }
                catch { }
                object r = m_register.Invoke(null, new object[] { owner, prefab, opts });
                return r is int id ? id : -1;
            }

            // Worker thread: the engine's own instance array (reflection over plain fields is thread-safe).
            public object MakeArray(List<FarVegImpostors.BridgeTree> trees, int[] kindOfSpecies)
            {
                int n = 0;
                foreach (var t in trees) if (t.Species < kindOfSpecies.Length && kindOfSpecies[t.Species] >= 0) n++;
                var arr = Array.CreateInstance(m_inst, n);
                int i = 0;
                foreach (var t in trees)
                {
                    int kind = t.Species < kindOfSpecies.Length ? kindOfSpecies[t.Species] : -1;
                    if (kind < 0) continue;
                    object o = Activator.CreateInstance(m_inst);
                    f_pos.SetValue(o, t.Pos); f_scale.SetValue(o, t.Scale); f_yaw.SetValue(o, t.Yaw * Mathf.Deg2Rad); f_kind.SetValue(o, kind);
                    f_tx?.SetValue(o, 0f); f_tz?.SetValue(o, 0f);
                    arr.SetValue(o, i++);
                }
                return arr;
            }

            public void SetCell(string owner, long key, Bounds b, object arr) => m_setCell.Invoke(null, new object[] { owner, key, b, arr });
            public void ClearCell(string owner, long key) { try { m_clearCell.Invoke(null, new object[] { owner, key }); } catch { } }

            // 0.2.1417: MarkZoneStanding(Vector2s, bool), by presence (it only ever makes a zone stand sooner; Core stays the authority).
            private MethodInfo m_mark; private bool m_markLooked;
            public void MarkZoneStanding(Vector2s zone)
            {
                if (!m_markLooked) { m_markLooked = true; m_mark = m_engine?.GetMethod("MarkZoneStanding", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Vector2s), typeof(bool) }, null); }
                if (m_mark == null) return;
                try { m_mark.Invoke(null, new object[] { zone, true }); } catch { }
            }
        }

        // ---------------------------------------------------------------- [generator]'s FatFarCanopy (#10), by presence
        private sealed class CanopySink
        {
            private MethodInfo m_set, m_clear;
            private Type m_tree;
            private FieldInfo f_ground, f_height, f_crown;

            internal static CanopySink TryBind()
            {
                var t = typeof(FarBridge).Assembly.GetTypes().FirstOrDefault(x => x.Name == "FatFarCanopy");
                if (t == null) return null;
                var ms = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                var s = new CanopySink
                {
                    m_set = ms.FirstOrDefault(m => m.Name == "SetCell" && m.GetParameters().Length == 2 && m.GetParameters()[1].ParameterType.IsArray),
                    m_clear = ms.FirstOrDefault(m => m.Name == "ClearCell" && m.GetParameters().Length == 1),
                };
                if (s.m_set == null || s.m_clear == null) { FiresLogger.LogInfo("[FarForest] FatFarCanopy found but SetCell(long, T[]) / ClearCell(long) missing: canopy sink off."); return null; }
                s.m_tree = s.m_set.GetParameters()[1].ParameterType.GetElementType();
                FieldInfo F(string n) => s.m_tree.GetField(n, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase);
                s.f_ground = F("ground"); s.f_height = F("height"); s.f_crown = F("crownRadius");
                if (s.f_ground == null || s.f_height == null || s.f_crown == null) { FiresLogger.LogInfo($"[FarForest] FatFarCanopy {s.m_tree.Name} fields missing (ground/height/crownRadius): canopy sink off."); return null; }
                return s;
            }

            public object MakeArray(List<FarVegImpostors.BridgeTree> trees)
            {
                var arr = Array.CreateInstance(m_tree, trees.Count);
                for (int i = 0; i < trees.Count; i++)
                {
                    var t = trees[i];
                    float h = FarVegImpostors.SpeciesHeight(t.Species);
                    object o = Activator.CreateInstance(m_tree);
                    f_ground.SetValue(o, t.Pos); f_height.SetValue(o, h * t.Scale); f_crown.SetValue(o, h * 0.3f * t.Scale);   // crown ~0.3 x height until the kind bounds are passed
                    arr.SetValue(o, i);
                }
                return arr;
            }
            public void SetCell(long key, object arr) => m_set.Invoke(null, new object[] { key, arr });
            public void ClearCell(long key) { try { m_clear.Invoke(null, new object[] { key }); } catch { } }
        }
    }
}
