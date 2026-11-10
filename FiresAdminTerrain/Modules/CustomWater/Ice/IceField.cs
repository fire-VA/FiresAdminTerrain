using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    // 0.2.1168 (Fire: "our fat water should slowly freeze over during storms and in the dead of winter"; his answers: all FAT water,
    // the sea stays open, a pond ices over in ~1 in-game day, storms ~x3, walkable from ~0.10 m). Ice used to appear at full strength
    // the moment Vedr's winter began (WaterStateField = a pure function of biome + season). Now it has TIME:
    //   - one world-wide freeze EXPOSURE F (in-game days of freezing weather), integrated by the server from in-game time (ZNet time),
    //     Vedr's season (VanillaIce.SeasonFreezing / SeasonThawing) and the server's shown weather (VedrApi.GetShownWeather: snow x1.8,
    //     storm / blizzard x3); it falls again when the season stops freezing (spring thaw ~1 day, summer x2);
    //   - per column the ice is DERIVED from F, so every peer computes the same field with no per-cell traffic: shallow water (the
    //     shore) needs little exposure, deep water more (need = 0.5 day x depth / 4 m), so ice grows from the shores inward; rivers
    //     freeze at 40 % and never above 70 % cover (open leads); thickness = 0.2 m/day past the need, slower over deep water;
    //   - the derived ice stays under the solid threshold until the ice is 0.10 m thick (walkable from 0.10 m);
    //   - fires keep holes open (FatFireBullseye melt core); Deep North stays always frozen (unchanged).
    // The server broadcasts F (RPC, on change, at most every 10 s) and saves it beside the world (FiresWorldGen\<world>\..._ice.txt).
    internal static class IceField
    {
        internal const float NeedDays = 0.5f, CoverDays = 0.5f, ThickPerDay = 0.2f, Walkable = 0.10f, RiverRate = 0.4f, RiverCap = 0.7f;
        // 0.2.1323: MaxF 4 -> 1.5. Cover is full at F = need (<= 0.5) + 0.5 = 1.0 at most; 1.5 still lets the deepest (4 m) pond reach
        // the walkable 0.10 m ((1.5 - 0.5) x 0.2 / 2), and winter no longer banks days of extra ice ahead of the spring thaw.
        private const float StepDays = 0.05f, MaxF = 1.5f;
        private const string RpcF = "FatIceExposure", RpcReq = "FatIceExposureReq";

        private static volatile float s_f;                 // the live exposure (server-integrated)
        // 0.2.1170: the APPLIED exposure every derived read uses (Sample, colliders, liquid level, TryIceAt). It follows s_f one ice step
        // at a time and at most once a minute, when generated water remeshes, so the ice colliders, the no-liquid columns and the look
        // always agree (1168 let the liquid level move on before the colliders were rebuilt).
        private static volatile float s_fApplied;
        private static float s_nextApply;
        private static bool s_applyPending;
        private static double s_lastT = -1;
        private static float s_nextTick, s_nextSync, s_nextSave, s_nextLog, s_sentF = -1f, s_savedF = -1f, s_rate, s_tickMs;
        private static int s_step = -1, s_syncs, s_revision;
        private static bool s_firstSaid, s_loaded;
        private static string s_season = "?", s_weather = "?", s_source = "?";
        private static ZRoutedRpc s_rpcOn;
        // 0.2.1178: the client asks until it has the server's value (the join-time ask could go out before the server peer existed)
        private static bool s_haveServerF;
        private static float s_nextAsk;
        private static int s_received, s_rejected;

        // test hooks (fat_water_ice_test): client-local, cleared by the test's finally
        internal static float TestRateScale = 1f;
        internal static bool TestForceFreezing;

        internal static float Exposure => s_f;
        internal static int Revision => s_revision;
        internal static float AppliedExposure => s_fApplied;
        internal static void BumpRevision() => s_revision++;
        internal static void RequestSave() { s_savedF = -1f; s_nextSave = 0f; }

        // ---- per-column derivation (any thread) ----
        internal static void Derive(float depth, bool river, out float cover, out float thick) => DeriveF(s_fApplied, depth, river, out cover, out thick);

        private static void DeriveF(float fIn, float depth, bool river, out float cover, out float thick)
        {
            float f = fIn * (river ? RiverRate : 1f);
            float d = Mathf.Max(0f, depth);
            float need = NeedDays * Mathf.Clamp01(d / 4f);
            float past = f - need;
            cover = Mathf.Clamp01(past / CoverDays);
            if (river) cover = Mathf.Min(cover, RiverCap);
            thick = Mathf.Max(0f, past) * ThickPerDay / (1f + d / 4f);
        }

        // 0.2.1170: Derive with the broken-hole override for this 1 m column (IceBreaks): a hole re-freezes from the exposure at its
        // break, so the ice there grows back from zero as winter goes on.
        internal static void DeriveAt(float x, float z, float depth, bool river, out float cover, out float thick)
        {
            if (IceBreaks.HoleExposure(x, z, out float fb))
            {
                DeriveF(Mathf.Max(0f, s_fApplied - fb), depth, river, out cover, out thick);   // thread-safe: no shared state touched
                return;
            }
            Derive(depth, river, out cover, out thick);
        }

        // The ice amount the state field uses: the cover, held under the solid threshold until the ice is walkable.
        internal static float IceAmount(float cover, float thick, float solid)
            => thick >= Walkable ? cover : Mathf.Min(cover, Mathf.Max(0f, solid - 0.1f));

        // ---- the tick (main thread, from WaterStateField.Tick) ----
        internal static void Tick()
        {
            EnsureRpc();
            AskUntilReceived();
            IceBreaks.Tick();   // 0.2.1170: thin ice breaking under characters + hole expiry (own 0.5 s gate)
            if (Time.time < s_nextTick) return;
            s_nextTick = Time.time + 2f;
            var zn = ZNet.instance;
            if (zn == null) return;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            if (zn.IsServer())
            {
                if (!s_loaded) { s_loaded = true; Load(); }
                double t = zn.GetTimeSeconds();
                if (s_lastT < 0) s_lastT = t;
                double dt = t - s_lastT; s_lastT = t;
                if (dt < 0) dt = 0;
                float dayLen = EnvMan.instance != null && EnvMan.instance.m_dayLengthSec > 0 ? EnvMan.instance.m_dayLengthSec : 1800f;
                float days = (float)Math.Min(dt / dayLen, 1.0) * Mathf.Max(0f, TestRateScale);
                bool freezing = TestForceFreezing || VanillaIce.SeasonFreezing, thawing = VanillaIce.SeasonThawing;
                float mult = WeatherMult();
                s_season = TestForceFreezing ? "test-forced freezing" : freezing ? "freezing" : thawing ? "thawing" : "not freezing";
                s_rate = freezing ? mult : (thawing ? -1f : -2f);
                // 0.2.1323 (Fire: "spring is when it should melt out back to water ... full water back by like day 3 of spring or day 4"):
                // the season from Vedr's calendar. Spring = a thaw scaled so F reaches 0 by 0.35 x the season length; Summer / Fall = no ice
                // (F 0 at once on entry, broadcast, held at 0); Winter / no calendar = as before.
                // 0.2.1326 (one thaw implementation): Core 0.2.1083's SnowField.ThawProgress drives the spring melt when present, so the
                // ice and Core's trail melt-out finish together; -1 / no Core = 1323's own Vedr read below, unchanged.
                if (CoreThaw(out int cIdx, out float tp))
                {
                    bool open = cIdx == 1 || cIdx == 2 || (cIdx != 3 && tp >= 1f);
                    int eIdx = open ? (cIdx == 0 ? 1 : cIdx) : cIdx;   // spring past the open fraction behaves as summer
                    if (cIdx == 0 && !open && (s_coreIdx != 0 || s_coreFStart < 0f))
                    {
                        // entry into spring: F at the start (a server start mid-spring: the start value that gives today's F)
                        s_coreFStart = s_coreIdx == 3 || s_coreIdx == -2 && tp <= 0f ? s_f : Mathf.Min(MaxF, s_f / Mathf.Max(0.01f, 1f - tp));
                    }
                    if (eIdx != s_coreEIdx || cIdx != s_coreIdx)
                    {
                        string fromL = SeasonLabel(s_coreIdx);
                        s_coreIdx = cIdx; s_coreEIdx = eIdx; s_seasonIdx = -2;   // the fallback re-logs if Core goes away
                        if (open) { s_f = 0f; s_sentF = -1f; s_nextSync = 0f; s_coreFStart = -1f; }
                        FiresLogger.LogInfo($"[IceField] season {fromL} → {SeasonLabel(cIdx)}: exposure F {s_f:0.00}, thaw progress {tp:0.00}, "
                            + (cIdx == 0 && !open ? $"spring start F {s_coreFStart:0.00}, F = start x (1 - progress), open water at progress 1 (Spring day {SpringOpenFrac:0.00} x season)" : open ? "no ice (cleared at entry)" : $"freezing rate {s_rate:0.00}/day")
                            + " via Core ThawProgress (0.2.1326)");
                    }
                    if (open) { s_rate = 0f; s_f = 0f; }
                    else if (cIdx == 0)
                    {
                        float nf = Mathf.Clamp(s_coreFStart * (1f - Mathf.Clamp01(tp)), 0f, MaxF);
                        s_rate = 0f;
                        if (nf < s_f) s_f = nf;   // F only ever decreases in spring
                        if (FiresLogger.DebugEnabled && Time.time >= s_nextThawLog) { s_nextThawLog = Time.time + 60f; FiresLogger.LogDebug($"[IceField] spring thaw: progress {tp:0.000}, start F {s_coreFStart:0.00}, F {s_f:0.00} (Core ThawProgress) (0.2.1326)"); }
                    }
                    else s_f = Mathf.Clamp(s_f + days * s_rate, 0f, MaxF);   // winter: unchanged freezing
                }
                else
                {
                if (s_coreIdx != -2) { s_coreIdx = -2; s_coreEIdx = -2; s_coreFStart = -1f; }
                int sIdx = VedrSeason(out int seasonDays, out float seasonElapsed, out string lenWhy);
                if (sIdx != s_seasonIdx || (sIdx == 0 && seasonDays != s_rateDays))
                {
                    int from = s_seasonIdx; s_seasonIdx = sIdx; s_rateDays = seasonDays;
                    float target = SpringOpenFrac * seasonDays;
                    if (sIdx == 0)
                    {
                        // computed once at the change into spring (or a length change / a server start mid-spring: the days left to the target)
                        float left = Mathf.Max(0.25f, target - seasonElapsed);
                        s_springRate = s_f > 0f ? s_f / left : 0f;
                    }
                    else if (sIdx == 1 || sIdx == 2) { s_f = 0f; s_sentF = -1f; s_nextSync = 0f; }   // no ice; the broadcast below sends it now
                    string line = $"[IceField] season {SeasonLabel(from)} → {SeasonLabel(sIdx)}: exposure F {s_f:0.00}, rate {(sIdx == 0 ? -s_springRate : sIdx == 1 || sIdx == 2 ? 0f : s_rate):0.00}/day, season length {seasonDays} days ({lenWhy}), expected open water by "
                        + (sIdx == 0 ? $"Spring day {target:0.0}" : sIdx == 1 || sIdx == 2 ? $"{SeasonLabel(sIdx)} day 0 (cleared at entry)" : "Spring day " + (SpringOpenFrac * seasonDays).ToString("0.0")) + " via FAT fallback (0.2.1323; source named 0.2.1326)";
                    FiresLogger.LogInfo(line);
                }
                if (sIdx == 0) s_rate = -s_springRate;
                else if (sIdx == 1 || sIdx == 2) s_rate = 0f;
                if (sIdx == 1 || sIdx == 2) s_f = 0f;
                else s_f = Mathf.Clamp(s_f + days * s_rate, 0f, MaxF);
                }
                IceFrost.ServerTick((float)Math.Min(dt, 60.0));   // 0.2.1179: frost forms only while snowing; polished cells regrow after 10 min
                if (Time.time >= s_nextSync && (Mathf.Abs(s_f - s_sentF) >= 0.005f || Time.time >= s_nextHeartbeat)) Broadcast(Mathf.Abs(s_f - s_sentF) >= 0.005f ? "change" : "heartbeat");
                if ((Mathf.Abs(s_f - s_savedF) >= 0.01f || IceBreaks.Dirty || IceFrost.Dirty) && Time.time >= s_nextSave) { IceBreaks.Dirty = false; IceFrost.Dirty = false; Save(); }
            }
            int step = Mathf.FloorToInt(s_f / StepDays);
            if (step != s_step) { if (s_step < 0) { s_fApplied = s_f; } else s_applyPending = true; s_step = step; }
            if (s_applyPending && Time.time >= s_nextApply)
            {
                s_applyPending = false; s_nextApply = Time.time + (TestRateScale != 1f ? 1f : 60f);   // the test runs time x120: apply each step
                s_fApplied = s_f;
                s_revision++; WaterStateField.IceChanged();
            }
            {
                if (!s_firstSaid && s_fApplied > 0f && step > 0)
                {
                    s_firstSaid = true;
                    var p = Player.m_localPlayer != null ? Player.m_localPlayer.transform.position : Vector3.zero;
                    FiresLogger.LogDebug($"[WaterIce] first ice formed: exposure {s_f:0.00} day(s) ({s_season}, weather {s_weather} via {s_source}); near ({p.x:0},{p.z:0}) zone {ZoneSystem.GetZone(p)}");
                }
            }
            s_tickMs = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            if (Time.time >= s_nextLog && FiresLogger.DebugEnabled)
            {
                s_nextLog = Time.time + 30f;
                Derive(0.3f, false, out float sc, out float st);
                Derive(2f, false, out float pc, out float pt);
                FiresLogger.LogDebug($"[WaterIce] tiles {WaterStateField.TileCount}, exposure {s_f:0.00} day(s) (step {s_step}), rate {s_rate:0.0}/day ({s_season}, weather {s_weather} source {s_source}{(TestRateScale != 1f ? $", test x{TestRateScale:0}" : "")}); "
                    + $"shore 0.3 m cover {sc:0.00} thick {st:0.00} m, 2 m pond cover {pc:0.00} thick {pt:0.00} m; tick ms {s_tickMs:0.000}, syncs sent {s_syncs} / received {s_received} / rejected {s_rejected}{(zn.IsServer() ? "" : (s_haveServerF ? ", server value held" : ", WAITING for the server value"))}, freeze rule: {VanillaIce.FreezeRuleWhy()}, IceRevision {s_revision}, sheltered skipped {WaterStateField.ShelteredSkipped}, shore guard: open-sky ponds below the generated ground kept freezing {WaterStateField.TakeOpenSkipped()} (0.2.1178); " + IceFrost.Summary() + $", TryIceAt flips {s_iceFlips} (held {s_iceHeld}) (0.2.1186)"); WaterStateField.ShelteredSkipped = 0; s_iceFlips = 0; s_iceHeld = 0;
            }
        }

        // 0.2.1323: Vedr's season (VedrApi.SeasonIndex 0 Spring / 1 Summer / 2 Fall / 3 Winter, DaysPerSeason, SeasonProgressSmooth;
        // ApiVersion >= 8) by reflection. -1 = no calendar (the old freezing / thawing rule alone). Length fallback 10 days.
        internal const float SpringOpenFrac = 0.35f;
        private static int s_seasonIdx = -2, s_rateDays;
        private static float s_springRate;
        private static PropertyInfo s_pIdx, s_pDays, s_pProg;
        private static bool s_seasonBound, s_lenFallbackSaid;
        private static int VedrSeason(out int days, out float elapsedDays, out string why)
        {
            days = 10; elapsedDays = 0f; why = "fallback 10";
            if (!s_seasonBound)
            {
                s_seasonBound = true;
                try
                {
                    var api = Type.GetType("Vedr.VedrApi, Norger.Vedr");
                    var ver = api?.GetField("ApiVersion")?.GetValue(null) ?? api?.GetProperty("ApiVersion")?.GetValue(null);
                    if (ver != null && Convert.ToInt32(ver) >= 8)
                    {
                        s_pIdx = api.GetProperty("SeasonIndex"); s_pDays = api.GetProperty("DaysPerSeason"); s_pProg = api.GetProperty("SeasonProgressSmooth");
                    }
                    FiresLogger.LogDebug($"[IceField] Vedr season bind: SeasonIndex {(s_pIdx != null ? "yes" : "no")}, DaysPerSeason {(s_pDays != null ? "yes" : "no")}, SeasonProgressSmooth {(s_pProg != null ? "yes" : "no")} (ApiVersion {ver ?? "none"}) (0.2.1323)");
                }
                catch (Exception e) { FiresLogger.LogDebug($"[IceField] Vedr season bind failed: {e.Message} (0.2.1323)"); }
            }
            int idx = -1;
            try
            {
                if (s_pIdx != null) idx = Convert.ToInt32(s_pIdx.GetValue(null));
                int d = s_pDays != null ? Convert.ToInt32(s_pDays.GetValue(null)) : 0;
                if (d > 0) { days = d; why = "Vedr DaysPerSeason"; }
                if (s_pProg != null) elapsedDays = Mathf.Clamp01(Convert.ToSingle(s_pProg.GetValue(null))) * days;
            }
            catch { idx = -1; }
            if (idx < -1 || idx > 3) idx = -1;
            if (why != "Vedr DaysPerSeason" && !s_lenFallbackSaid) { s_lenFallbackSaid = true; FiresLogger.LogDebug("[IceField] Vedr season length unavailable: spring thaw uses a 10-day season (0.2.1323)"); }
            return idx;
        }
        // 0.2.1326: Core 0.2.1083's FiresCore.Snow.SnowField.SeasonIndex / ThawProgress (FiresUnifiedCore), bound once by reflection.
        // false = Core absent / old, or ThawProgress -1 (no Vedr calendar): the caller uses 1323's own Vedr read.
        private static int s_coreIdx = -2, s_coreEIdx = -2;
        private static float s_coreFStart = -1f, s_nextThawLog;
        private static bool s_coreBound;
        private static Func<int> s_coreSeason;
        private static Func<float> s_coreThaw;
        private static bool CoreThaw(out int idx, out float tp)
        {
            idx = -1; tp = -1f;
            if (!s_coreBound)
            {
                s_coreBound = true;
                try
                {
                    var sf = Type.GetType("FiresCore.Snow.SnowField, FiresUnifiedCore");
                    var mi = sf?.GetMethod("SeasonIndex", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                    var mt = sf?.GetMethod("ThawProgress", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                    if (mi != null && mt != null && mi.ReturnType == typeof(int) && mt.ReturnType == typeof(float))
                    {
                        s_coreSeason = (Func<int>)Delegate.CreateDelegate(typeof(Func<int>), mi);
                        s_coreThaw = (Func<float>)Delegate.CreateDelegate(typeof(Func<float>), mt);
                    }
                    FiresLogger.LogDebug($"[IceField] Core thaw bind: SnowField {(sf != null ? "yes" : "no")}, SeasonIndex {(s_coreSeason != null ? "yes" : "no")}, ThawProgress {(s_coreThaw != null ? "yes" : "no")} (0.2.1326)");
                }
                catch (Exception e) { FiresLogger.LogDebug($"[IceField] Core thaw bind failed: {e.Message} (0.2.1326)"); s_coreSeason = null; s_coreThaw = null; }
            }
            if (s_coreSeason == null || s_coreThaw == null) return false;
            try { idx = s_coreSeason(); tp = s_coreThaw(); }
            catch { return false; }
            if (tp < 0f || idx < 0 || idx > 3) return false;
            return true;
        }
        private static string SeasonLabel(int i) => i == 0 ? "Spring" : i == 1 ? "Summer" : i == 2 ? "Fall" : i == 3 ? "Winter" : i == -2 ? "start" : "none";

        // ---- weather (the server's view of Vedr's shown weather; EnvMan name as the fallback) ----
        private static MethodInfo s_shown;
        private static bool s_shownBound;
        private static float WeatherMult()
        {
            if (!s_shownBound)
            {
                s_shownBound = true;
                try
                {
                    var api = Type.GetType("Vedr.VedrApi, Norger.Vedr");
                    var ver = api?.GetField("ApiVersion")?.GetValue(null) ?? api?.GetProperty("ApiVersion")?.GetValue(null);
                    if (ver != null && Convert.ToInt32(ver) >= 8) s_shown = api.GetMethod("GetShownWeather");
                }
                catch { }
            }
            if (s_shown != null)
            {
                try
                {
                    var a = new object[] { null, false, 0f };
                    if ((bool)s_shown.Invoke(null, a))
                    {
                        s_source = "Vedr"; s_weather = a[0] as string ?? "?";
                        bool snowing = (bool)a[1]; float inten = Convert.ToSingle(a[2]);
                        return !snowing ? 1f : inten >= 0.95f ? 3f : 1f + 1.33f * Mathf.Clamp01(inten);   // storm 3, snow (0.6) ~1.8, flurries ~1.4
                    }
                }
                catch { }
            }
            s_source = "EnvMan";
            string env = EnvMan.instance != null ? EnvMan.instance.GetCurrentEnvironment()?.m_name ?? "" : "";
            s_weather = env;
            bool storm = env.IndexOf("storm", StringComparison.OrdinalIgnoreCase) >= 0 || env.IndexOf("blizzard", StringComparison.OrdinalIgnoreCase) >= 0;
            bool snow = env.IndexOf("snow", StringComparison.OrdinalIgnoreCase) >= 0;
            return storm && snow ? 3f : snow ? 1.8f : 1f;
        }

        // ---- sync: server -> everyone; a client asks once when it joins ----
        private static void EnsureRpc()
        {
            var r = ZRoutedRpc.instance;
            if (r == null || ReferenceEquals(r, s_rpcOn)) return;
            s_rpcOn = r;
            r.Register<float>(RpcF, OnExposure);
            r.Register(RpcReq, OnRequest);
            IceBreaks.RegisterRpc(r);   // 0.2.1170: break requests + the hole list
            IceFrost.RegisterRpc(r);    // 0.2.1179: polish requests + the frost list
            s_haveServerF = false; s_nextAsk = 0f;   // 0.2.1178: a new routed-RPC instance = a new connection: ask again
        }

        private static void AskUntilReceived()
        {
            var zn = ZNet.instance; var r = ZRoutedRpc.instance;
            if (zn == null || r == null || zn.IsServer() || s_haveServerF || Time.time < s_nextAsk) return;
            long server = r.GetServerPeerID();
            if (server == 0) return;   // not connected yet
            s_nextAsk = Time.time + 10f;
            r.InvokeRoutedRPC(server, RpcReq);
            FiresLogger.LogDebug($"[WaterIce] asked the server (uid {server}) for the freeze exposure (no value held yet)");
        }

        private static float s_nextHeartbeat, s_nextSendLog;
        private static void Broadcast(string reason)
        {
            if (ZRoutedRpc.instance == null) return;
            s_sentF = s_f; s_nextSync = Time.time + 10f; s_nextHeartbeat = Time.time + 10f; s_syncs++;
            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, RpcF, s_f);
            if (reason != "heartbeat" || Time.time >= s_nextSendLog)
            {
                if (reason == "heartbeat") s_nextSendLog = Time.time + 60f;
                int peers = ZNet.instance != null ? ZNet.instance.GetPeers().Count : 0;
                FiresLogger.LogDebug($"[WaterIce] exposure {s_f:0.000} sent to {peers} peer(s) (reason {reason})");
            }
        }

        private static void OnRequest(long sender)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer() || ZRoutedRpc.instance == null) return;
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, RpcF, s_f);
            IceBreaks.SendHolesTo(sender);
            IceFrost.SendTo(sender);   // 0.2.1179: the frost base + polished cells
            s_syncs++;
            FiresLogger.LogDebug($"[WaterIce] exposure {s_f:0.000} sent to 1 peer(s) (reason join, uid {sender}) + the hole list");
        }

        private static void OnExposure(long sender, float f)
        {
            if (ZNet.instance == null || ZNet.instance.IsServer()) return;   // the server owns it
            if (ZRoutedRpc.instance == null || sender != ZRoutedRpc.instance.GetServerPeerID())   // only the server's value
            {
                s_rejected++;
                FiresLogger.LogDebug($"[WaterIce] exposure {f:0.000} REJECTED: sender uid {sender} is not the server (uid {(ZRoutedRpc.instance != null ? ZRoutedRpc.instance.GetServerPeerID() : 0)})");
                return;
            }
            if (float.IsNaN(f) || float.IsInfinity(f)) return;
            bool first = !s_haveServerF;
            s_haveServerF = true; s_received++;
            s_f = Mathf.Clamp(f, 0f, MaxF);
            if (s_step < 0) s_fApplied = s_f;
            if (first || Mathf.Abs(s_f - s_sentF) >= 0.005f) { s_sentF = s_f; FiresLogger.LogDebug($"[WaterIce] exposure {s_f:0.000} received from the server (uid {sender}), applied{(first ? " (first value since join)" : "")}"); }
        }

        // ---- persistence (server) ----
        private static string FilePath()
        {
            string w = ZNet.World != null ? ZNet.World.m_name : null;
            return string.IsNullOrEmpty(w) ? null : VerdantsAscent.Terrain.Voxel.FiresWorldRecords.PathFor(w, "_ice.txt");
        }

        private static void Load()
        {
            try
            {
                string p = FilePath();
                if (p == null || !File.Exists(p)) return;
                string[] lines = File.ReadAllLines(p);
                string[] parts = lines.Length > 0 ? lines[0].Trim().Split(' ') : new string[0];
                IceBreaks.LoadLines(lines);
                IceFrost.LoadLines(lines);
                if (parts.Length > 0 && float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float f) && !float.IsNaN(f))
                    s_f = s_savedF = s_fApplied = Mathf.Clamp(f, 0f, MaxF);
                FiresLogger.LogDebug($"[WaterIce] exposure {s_f:0.00} day(s) loaded from {p}");
            }
            catch (Exception e) { FiresLogger.LogWarning($"[WaterIce] could not read the ice exposure: {e.Message}"); }
        }

        private static void Save()
        {
            s_nextSave = Time.time + 30f;
            try
            {
                string p = FilePath();
                if (p == null) return;
                File.WriteAllText(p, s_f.ToString("0.0000", CultureInfo.InvariantCulture) + " v1" + IceBreaks.SaveLines() + IceFrost.SaveLines());
                s_savedF = s_f;
            }
            catch (Exception e) { FiresLogger.LogWarning($"[WaterIce] could not save the ice exposure: {e.Message}"); }
        }

        // test hook: set the exposure directly on the server (restored by the test's finally)
        internal static void TestSetExposure(float f)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            s_f = Mathf.Clamp(f, 0f, MaxF);
            s_nextSync = 0f; Broadcast("change");
            s_nextTick = 0f;
            s_fApplied = s_f; s_nextApply = 0f; s_revision++; WaterStateField.IceChanged();   // 0.2.1170: the test sees it now
        }

        // ---- the add-only API backing (FatApi.TryIceAt / IsFreezingWater) ----
        internal static bool TryWater(Vector3 pos, out float surface)
        {
            surface = float.NaN;
            if (GenWaterFieldAt(pos.x, pos.z, out float lvl) && Mathf.Abs(lvl - pos.y) < 6f) { surface = lvl; return true; }
            if (PlacedWaterField.TrySurface(pos.x, pos.z, pos.y - 6f, pos.y + 6f, PlacedWaterField.Consumer.Sheet, out float s, out _)) { surface = s; return true; }
            return false;
        }

        private static bool GenWaterFieldAt(float x, float z, out float level)
        {
            level = float.MinValue;
            try { return global::VerdantsAscent.Terrain.Voxel.GenWaterField.TryWaterSurfaceSmooth(x, z, out level, out _); }
            catch { return false; }
        }

        // 0.2.1186 ([seasons]: a frozen fish at (306,888) under FAT cover 0.71 read "no sheet" in blips, 41 flips in one session; the
        // cover itself never changed - no breaks, no polish, the same 0.71 / 0.07 m on every "stuck" read - so the drop is the LOOKUP,
        // not the ice): the water surface under a static point can come back missing for a call (the placed-water cap/tile rebuilt
        // after a chunk remesh, an off-main-thread caller that cannot read the cap, a mop or bed check racing a rebuild). TryIceAt now
        // remembers the last good answer per 1 m column and returns it for HoldSeconds when the fresh lookup fails while that column
        // last read cover > 0.5 - only a real change (a break/hole, thaw below cover 0.5, a fire) clears it. Thread-safe (no Unity calls
        // in the hold path); deterministic for a static point. Why each held miss happened is counted and logged (Debug).
        private const float HoldSeconds = 10f;
        private struct IceHold { public float Cover, Thick; public double At; public int Revision; }
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, IceHold> s_iceHold = new System.Collections.Concurrent.ConcurrentDictionary<long, IceHold>();
        internal static int s_iceFlips, s_iceHeld;
        private static double s_iceWhyAt;

        private static long ColKey(Vector3 p) => ((long)Mathf.FloorToInt(p.x) << 32) ^ (uint)Mathf.FloorToInt(p.z);
        private static double Now() => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

        internal static bool TryIceAt(Vector3 pos, out float cover, out float thick)
        {
            cover = 0f; thick = 0f;
            long k = ColKey(pos);
            string why = null;
            float y = float.NaN;
            // 0.2.1317 ([WaterIce] one TryIceAt 895.66 ms, round 20261030-115614 client.log:23745; the log did not say which part):
            // time each phase and name it on a slow call (> 10 ms, Debug). Same lookups in the same order; timing only.
            long p0 = System.Diagnostics.Stopwatch.GetTimestamp();
            WaterStateField.IceDetailLockWaitTicks = 0; WaterStateField.IceDetailBuildTicks = 0; WaterStateField.IceDetailTileBuilds = 0;
            bool water = TryWater(pos, out y);
            long p1 = System.Diagnostics.Stopwatch.GetTimestamp(), p2 = p1;
            try
            {
            if (!water) why = "no water surface found under the point this call (placed cap / gen field lookup missed)";
            else
            {
                WaterStateField.IceDetail(pos.x, pos.z, y, out cover, out thick);
                p2 = System.Diagnostics.Stopwatch.GetTimestamp();
                if (cover > 0.5f)
                {
                    s_iceHold[k] = new IceHold { Cover = cover, Thick = thick, At = Now(), Revision = s_revision };
                    return true;
                }
                if (cover <= 0f) why = "the column read cover 0 this call";
            }
            // the fresh read missed or dropped: hold the last good answer unless the ice really changed since (a step/break bumps IceRevision
            // and a step that thaws re-reads it anyway on the next good call)
            if (why != null && s_iceHold.TryGetValue(k, out var h) && Now() - h.At <= HoldSeconds && !HoleNear(pos))
            {
                cover = h.Cover; thick = h.Thick;
                System.Threading.Interlocked.Increment(ref s_iceHeld);
                System.Threading.Interlocked.Increment(ref s_iceFlips);
                double n = Now();
                if (FiresLogger.DebugEnabled && n >= s_iceWhyAt)
                {
                    s_iceWhyAt = n + 5.0;
                    FiresLogger.LogDebug($"[WaterIce] TryIceAt ({pos.x:0.0},{pos.z:0.0},{pos.y:0.0}): no sheet because {why} (surface {(float.IsNaN(y) ? "none" : y.ToString("0.00"))}, IceRevision {s_revision}, held cover {h.Cover:0.00} from {n - h.At:0.0} s ago) - returned the held answer (0.2.1186)");
                }
                return true;
            }
            if (why != null && s_iceHold.TryRemove(k, out _)) System.Threading.Interlocked.Increment(ref s_iceFlips);
            return why == null;
            }
            finally { SlowIceCall(pos, water, p0, p1, p2); }
        }

        // 0.2.1317: names what a slow TryIceAt spent its time on (Debug, > 10 ms, at most one line a second; one Info the first time).
        private static double s_slowIceAt;
        private static bool s_slowIceInfo;
        private static void SlowIceCall(Vector3 pos, bool water, long p0, long p1, long p2)
        {
            long end = System.Diagnostics.Stopwatch.GetTimestamp();
            double f = 1000.0 / System.Diagnostics.Stopwatch.Frequency, total = (end - p0) * f;
            if (total <= 10.0) return;
            double n = Now();
            bool info = !s_slowIceInfo;
            if (!info && (!FiresLogger.DebugEnabled || n < s_slowIceAt)) return;
            s_slowIceAt = n + 1.0; s_slowIceInfo = true;
            double surf = (p1 - p0) * f, detail = water ? (p2 - p1) * f : 0.0, rest = (end - (water ? p2 : p1)) * f;
            double wait = WaterStateField.IceDetailLockWaitTicks * f, build = WaterStateField.IceDetailBuildTicks * f;
            bool main = System.Threading.Thread.CurrentThread.ManagedThreadId == WaterStateField.MainThread;
            string line = $"[WaterIce] slow TryIceAt ({pos.x:0.0},{pos.z:0.0},{pos.y:0.0}) {total:0.0} ms on the {(main ? "main" : "worker")} thread: water-surface lookup (gen field + placed cap) {surf:0.0} ms, "
                + $"ice detail {detail:0.0} ms (tile lock wait {wait:0.0} ms, {WaterStateField.IceDetailTileBuilds} state tile build(s) {build:0.0} ms), hold/hole path {rest:0.0} ms (0.2.1317)";
            if (info) FiresLogger.LogInfo(line); else FiresLogger.LogDebug(line);
        }

        private static bool HoleNear(Vector3 pos)
        {
            try { return IceBreaks.HoleExposure(pos.x, pos.z, out _); } catch { return false; }
        }

        internal static bool IsFreezingWater(Vector3 pos)
        {
            if (!TryWater(pos, out float y)) return false;
            var wg = WorldGenerator.instance;
            if (wg == null) return false;
            // 0.2.1169: cave water is sheltered and never freezes (the cave camera's 1.5 m rule against the generated ground)
            if (VerdantsAscent.Terrain.Voxel.FiresCaveCamera.TryGenGround(pos.x, pos.z, out float g) && y < g - 1.5f) return false;
            var b = wg.GetBiome(new Vector3(pos.x, y, pos.z));
            if (!(TestForceFreezing && b != Heightmap.Biome.Ocean && b != Heightmap.Biome.AshLands) && !VanillaIce.BiomeFreezes(b)) return false;
            return !(VerdantsAscent.Terrain.Voxel.FatFireBullseye.HasHeat && VerdantsAscent.Terrain.Voxel.FatFireBullseye.InMeltCore(new Vector3(pos.x, y, pos.z)));
        }
    }
}
