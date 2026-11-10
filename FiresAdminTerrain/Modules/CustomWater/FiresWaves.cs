using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    // 0.2.1175 (Fire: "vanilla waves with an intensity slider so we can override the sizes and work with vedr for tsunamis"):
    // one wave multiplier for the sea = slider x weather x max(1, overrides). The server computes it and sends it to every peer
    // (floaters and ships must ride the same sea), and it feeds the _FiresWaveMult / _FiresSeaState globals. The wave PHYSICS
    // scales only once the sea shader reads _FiresWaveMult, so what you see and what floats never disagree.
    internal static class FiresWaves
    {
        internal static ConfigEntry<float> Intensity;
        internal static ConfigEntry<string> WeatherMap;

        private const string RpcName = "FatWaveMult";
        private static float s_final = 1f, s_weather = 1f, s_seaState, s_override = 1f, s_sent = -1f;
        private static string s_env = "", s_source = "none";
        private static float s_nextPoll, s_nextSend, s_nextLog, s_nextShaderCheck;
        private static bool s_shaderReads, s_shaderSaid;
        private static int s_syncs;
        private static ZRoutedRpc s_rpcOn;
        private static readonly Dictionary<string, KeyValuePair<float, float>> s_overrides = new Dictionary<string, KeyValuePair<float, float>>();
        private static readonly List<string> s_expired = new List<string>();
        private static int s_propMult, s_propState;

        internal static void Bind(ConfigFile cfg)
        {
            Intensity = cfg.Bind("03 - Water Engine", "20 WaveIntensity", 1f, new ConfigDescription(
                "Wave size on the sea (1 = vanilla). Weather from Vedr and wave overrides (for example a Vedr tsunami) multiply on top. "
                + "The server value is used on every peer.", new AcceptableValueRange<float>(0f, 3f)));
            WeatherMap = cfg.Bind("03 - Water Engine", "21 WaveWeatherMap",
                "Clear=1,Twilight_Clear=1,Misty=1,Heath clear=1,Rain=1.2,LightRain=1.1,ThunderStorm=1.8,Twilight_SnowStorm=1.5,SnowStorm=1.5,Snow=1.2,Ashrain=1.2,Vedr_WinterStorm=1.6,Vedr_WinterSnow=1.2",
                "Weather name = wave multiplier, comma separated. A weather not listed counts as 1.");
        }

        // Read by the CalcWave patch: 1 until the sea shader reads _FiresWaveMult.
        internal static float PhysicsMult => s_shaderReads ? s_final : 1f;
        internal static float Final => s_final;

        internal static void SetOverride(string owner, float mult, float seconds)
        {
            if (string.IsNullOrEmpty(owner) || float.IsNaN(mult) || float.IsInfinity(mult)) return;
            s_overrides[owner] = new KeyValuePair<float, float>(Mathf.Clamp(mult, 0f, 10f), Time.time + Mathf.Max(0f, seconds));
            FiresLogger.LogDebug($"[Waves] override set: {owner} x{mult:0.00} for {seconds:0} s");
        }

        internal static void ClearOverride(string owner)
        {
            if (owner != null && s_overrides.Remove(owner)) FiresLogger.LogDebug($"[Waves] override cleared: {owner}");
        }

        // Main thread, every frame (from WaterStateField.Tick).
        internal static void Tick()
        {
            EnsureRpc(); AskUntilReceived();
            if (s_propMult == 0) { s_propMult = Shader.PropertyToID("_FiresWaveMult"); s_propState = Shader.PropertyToID("_FiresSeaState"); }
            bool server = ZNet.instance == null || ZNet.instance.IsServer();
            if (server && Time.time >= s_nextPoll)
            {
                s_nextPoll = Time.time + 1f;
                PollWeather();
                s_override = 1f;
                s_expired.Clear();
                foreach (var kv in s_overrides) { if (Time.time > kv.Value.Value) s_expired.Add(kv.Key); else s_override = Mathf.Max(s_override, kv.Value.Key); }
                foreach (var k in s_expired) { s_overrides.Remove(k); FiresLogger.LogDebug($"[Waves] override expired: {k}"); }
                s_final = (Intensity != null ? Intensity.Value : 1f) * s_weather * s_override;
                if (Mathf.Abs(s_final - s_sent) > 0.01f || Time.time >= s_nextSend) Send();
            }
            Shader.SetGlobalFloat(s_propMult, s_final);
            Shader.SetGlobalFloat(s_propState, s_seaState);
            if (Time.time >= s_nextShaderCheck) { s_nextShaderCheck = Time.time + 10f; CheckShader(); }
            if (Time.time >= s_nextLog && FiresLogger.DebugEnabled)
            {
                s_nextLog = Time.time + 30f;
                var owners = s_overrides.Count == 0 ? "none" : string.Join(",", s_overrides.Keys);
                int peers = ZNet.instance != null ? ZNet.instance.GetPeers().Count : 0;
                FiresLogger.LogDebug($"[Waves] mult {s_final:0.00} (slider {(Intensity != null ? Intensity.Value : 1f):0.00} x weather {s_weather:0.00} '{s_env}' (source {s_source}) x override {s_override:0.00} {owners}), synced to {peers} peer(s) ({s_syncs} send(s)), physics applied {(s_shaderReads ? "y" : "n")} (shader reads _FiresWaveMult {(s_shaderReads ? "y" : "n")}) (0.2.1175)");
            }
        }

        private static void PollWeather()
        {
            string name; float intensity;
            if (TryVedr(out name, out intensity)) s_source = "Vedr";
            else
            {
                s_source = "EnvMan";
                name = EnvMan.instance != null ? EnvMan.instance.GetCurrentEnvironment()?.m_name ?? "" : "";
                intensity = name.IndexOf("storm", System.StringComparison.OrdinalIgnoreCase) >= 0 ? 1f
                    : name.IndexOf("rain", System.StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("snow", System.StringComparison.OrdinalIgnoreCase) >= 0 ? 0.6f : 0f;
            }
            s_env = name ?? "";
            s_weather = MapWeather(s_env);
            s_seaState = Mathf.Clamp01(intensity);
        }

        private static string s_mapText;
        private static readonly Dictionary<string, float> s_map = new Dictionary<string, float>(System.StringComparer.OrdinalIgnoreCase);
        private static float MapWeather(string env)
        {
            string text = WeatherMap != null ? WeatherMap.Value : "";
            if (!ReferenceEquals(text, s_mapText))
            {
                s_mapText = text; s_map.Clear();
                foreach (var part in (text ?? "").Split(','))
                {
                    int eq = part.IndexOf('=');
                    if (eq <= 0) continue;
                    if (float.TryParse(part.Substring(eq + 1).Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v))
                        s_map[part.Substring(0, eq).Trim()] = Mathf.Clamp(v, 0f, 5f);
                }
            }
            return env != null && s_map.TryGetValue(env, out float m) ? m : 1f;
        }

        private static System.Reflection.MethodInfo s_shown;
        private static bool s_bound;
        private static bool TryVedr(out string name, out float intensity)
        {
            name = ""; intensity = 0f;
            if (!s_bound)
            {
                s_bound = true;
                try
                {
                    var api = System.Type.GetType("Vedr.VedrApi, Norger.Vedr");
                    var ver = api?.GetField("ApiVersion")?.GetValue(null) ?? api?.GetProperty("ApiVersion")?.GetValue(null);
                    if (ver != null && System.Convert.ToInt32(ver) >= 8) s_shown = api.GetMethod("GetShownWeather");
                    FiresLogger.LogDebug($"[Waves] VedrApi GetShownWeather {(s_shown != null ? "bound" : $"not available (ApiVersion {ver ?? "none"}): EnvMan fallback")}");
                }
                catch (System.Exception ex) { FiresLogger.LogDebug($"[Waves] GetShownWeather not bound: {ex.Message}"); }
            }
            if (s_shown == null) return false;
            try
            {
                var args = new object[] { null, false, 0f };
                if (!(bool)s_shown.Invoke(null, args)) return false;
                name = args[0] as string ?? ""; intensity = (float)args[2];
                return true;
            }
            catch { return false; }
        }

        private static void CheckShader()
        {
            bool reads = false;
            try
            {
                foreach (var wv in Object.FindObjectsByType<WaterVolume>(FindObjectsSortMode.None))
                {
                    var r = wv != null ? wv.m_waterSurface : null;
                    var m = r != null ? r.sharedMaterial : null;
                    // [seasons] water step 2: the new sea shader reads _FiresWaveMult as a GLOBAL (a Properties entry would override
                    // and freeze it), so it carries the marker _FiresWaveMultAware instead; older shaders lack it and read "no".
                    if (m != null && m.shader != null && m.HasProperty("_FiresWaveMultAware")) { reads = true; break; }
                }
            }
            catch { }
            if (reads != s_shaderReads || !s_shaderSaid)
            {
                s_shaderSaid = true;
                FiresLogger.LogDebug($"[Waves] sea shader reads _FiresWaveMult: {(reads ? "yes - wave physics scales with the multiplier" : "no - wave physics stays 1x so the floaters match the drawn sea")}");
            }
            s_shaderReads = reads;
        }

        // ---- sync: server -> everyone; a client asks once when it joins ----
        private static void EnsureRpc()
        {
            var r = ZRoutedRpc.instance;
            if (r == null || ReferenceEquals(r, s_rpcOn)) return;
            s_rpcOn = r;
            r.Register<float, float>(RpcName, OnValue);
            r.Register(RpcName + "Req", OnRequest);
            s_have = false; s_nextAsk = 0f;   // 0.2.1178: ask until the server answers (the join-time ask could precede the server peer)
        }

        private static bool s_have;
        private static float s_nextAsk;
        private static void AskUntilReceived()
        {
            var zn = ZNet.instance; var r = ZRoutedRpc.instance;
            if (zn == null || r == null || zn.IsServer() || s_have || Time.time < s_nextAsk) return;
            long server = r.GetServerPeerID();
            if (server == 0) return;
            s_nextAsk = Time.time + 10f;
            r.InvokeRoutedRPC(server, RpcName + "Req");
        }

        private static void Send()
        {
            if (ZRoutedRpc.instance == null) return;
            s_sent = s_final; s_nextSend = Time.time + 10f; s_syncs++;
            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, RpcName, s_final, s_seaState);
        }

        private static void OnRequest(long sender)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer() || ZRoutedRpc.instance == null) return;
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, RpcName, s_final, s_seaState);
        }

        private static void OnValue(long sender, float mult, float state)
        {
            if (ZNet.instance == null || ZNet.instance.IsServer()) return;   // the server owns it
            if (ZRoutedRpc.instance == null || sender != ZRoutedRpc.instance.GetServerPeerID()) { FiresLogger.LogDebug($"[Waves] value REJECTED: sender uid {sender} is not the server"); return; }   // only the server's value
            if (float.IsNaN(mult) || float.IsInfinity(mult) || float.IsNaN(state)) return;
            if (!s_have) FiresLogger.LogDebug($"[Waves] mult {mult:0.00} received from the server (uid {sender}), applied (first value since join)");
            s_have = true;
            s_final = Mathf.Clamp(mult, 0f, 30f); s_seaState = Mathf.Clamp01(state);
        }
    }
}
