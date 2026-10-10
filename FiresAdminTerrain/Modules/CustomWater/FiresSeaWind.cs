using System;
using System.Collections.Generic;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// The wave field's wind on a peer without a camera: the dedicated server.
    /// Vanilla's EnvMan takes its biome from the main camera, so the server never picks a weather and its sea blows the
    /// default environment's wind everywhere (R36b boat_test: server wind 1.00, both clients 0.50). There each water point
    /// gets the wind a client standing on it works out: the point's biome sector, vanilla's seeded weather pick for the
    /// period (GetAvailableEnvironments, so Vedr's seasons apply) and vanilla's four seeded octaves. A client starts a
    /// transition the moment its target changes, and a new weather reaches its wind one transition later (its first
    /// transition still sees the weather blend barely begun), so the transitions here start on the same seconds. A change
    /// arriving mid-transition starts from where the last one had got to. Peers with a camera keep vanilla's wind untouched.
    /// Left out, as each belongs to one player: events' forced weather, EnvZones, the world's edge and Moder's power.
    /// </summary>
    internal static class FiresSeaWind
    {
        private const string OffArgument = "-firesseawind-off";
        private const float OctaveStart = 0.5f;
        private const float OctaveCentre = 0.5f;
        private const float MinIntensity = 0.05f;
        private const float MaxIntensity = 1f;
        private const int CacheLimit = 256;
        private const int MaxChain = 8;
        private static readonly int[] Octaves = { 1, 2, 4, 8 };

        private sealed class SectorWind
        {
            internal long Change = long.MinValue;
            internal int Forcing = -1;
            internal Vector4 Previous;
            internal Vector4 Current;
            internal bool Valid;
            internal string EnvName;
        }

        private static readonly Dictionary<(BiomeSector sector, bool ashlands, bool deepnorth), SectorWind> s_cache =
            new Dictionary<(BiomeSector, bool, bool), SectorWind>();

        private static readonly AccessTools.FieldRef<EnvMan, List<EnvSetup>> s_environments =
            AccessTools.FieldRefAccess<EnvMan, List<EnvSetup>>("m_environments");
        private static readonly AccessTools.FieldRef<EnvMan, string> s_forceEnv = AccessTools.FieldRefAccess<EnvMan, string>("m_forceEnv");
        private static readonly Func<EnvMan, List<EnvEntry>, EnvSetup> s_selectWeighted =
            AccessTools.MethodDelegate<Func<EnvMan, List<EnvEntry>, EnvSetup>>(AccessTools.Method(typeof(EnvMan), "SelectWeightedEnvironment"));

        private static readonly List<long> s_chain = new List<long>();
        private static bool? s_enabled;
        private static int s_mainThread = -1;
        private static bool s_announced;
        private static Heightmap s_heightmap;
        private static int s_frame = -1;
        private static bool s_hasCamera;
        private static bool s_timed;
        private static float s_transition;
        private static float s_blend;
        private static string s_forced = "";
        private static string s_forcedBefore = "";
        private static long s_forcedSince = long.MinValue;
        private static int s_forcing;
        private static int s_chainForcing;

        private static bool Enabled
        {
            get
            {
                if (s_enabled == null) s_enabled = Array.IndexOf(Environment.GetCommandLineArgs(), OffArgument) < 0;
                return s_enabled.Value;
            }
        }

        // From FAT's ZNet.Awake postfix, so the thread the wave calls are checked against is known to be Unity's main one.
        // A peer with no graphics device (the dedi) says so here, since the periods line waits for its first wave sample.
        internal static void Init()
        {
            s_mainThread = Thread.CurrentThread.ManagedThreadId;
            if (s_armedLogged || !Enabled || SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null) return;
            s_armedLogged = true;
            FiresLogger.LogInfo("[SeaWind] armed on a peer with no graphics device: if it has no camera either (a dedicated server), the wave "
                                + "field's wind comes from each water point's biome and net time, and its first wave sample logs the periods.");
        }

        private static bool s_armedLogged;

        // Swaps vanilla's global wind for the wind at this point, on a peer with no camera to give vanilla a biome.
        internal static void Apply(Vector3 point, ref Vector4 wind1, ref Vector4 wind2, ref float alpha)
        {
            if (!Enabled || Thread.CurrentThread.ManagedThreadId != s_mainThread) return;
            Refresh();
            if (s_hasCamera || !TrySample(point, out Vector4 previous, out Vector4 current, out _)) return;
            wind1 = previous;
            wind2 = current;
            alpha = s_blend;
        }

        // What Apply blows at this point on this peer, for tests comparing the peers' seas.
        public static string Describe(Vector3 point)
        {
            if (!Enabled) return $"sea wind: off ({OffArgument})";
            if (s_mainThread < 0) return "sea wind: vanilla (not started: no ZNet yet)";
            Refresh();
            if (s_hasCamera) return "sea wind: vanilla (this peer has a camera)";
            if (!TrySample(point, out Vector4 previous, out Vector4 current, out string env))
                return "sea wind: vanilla (no biome or weather here yet)";
            Vector4 wind = Vector4.Lerp(previous, current, s_blend);
            return $"sea wind: env {env}, intensity {wind.w:0.00}, heading {Mathf.Atan2(wind.x, wind.z) * Mathf.Rad2Deg:0} deg, "
                   + $"blend {s_blend:0.00}";
        }

        // Once a frame: the camera check, and the transition this second is in (the same for every point).
        private static void Refresh()
        {
            if (s_frame == Time.frameCount) return;
            s_frame = Time.frameCount;
            s_hasCamera = Utils.GetMainCamera() != null;
            EnvMan envMan = EnvMan.instance;
            s_timed = !s_hasCamera && envMan != null && ZNet.instance != null;
            if (!s_timed) return;
            double now = ZNet.instance.GetTimeSeconds();
            string forced = s_forceEnv(envMan) ?? "";
            if (forced != s_forced)
            {
                s_forcedBefore = s_forced;
                s_forced = forced;
                s_forcedSince = (long)Math.Floor(now);
                s_forcing++;
            }
            s_transition = Mathf.Max(envMan.m_windTransitionDuration, Time.fixedDeltaTime);
            long change = LastChange(envMan, (long)Math.Floor(now));
            s_blend = Mathf.Clamp01((float)((now - change) / s_transition));
            if (s_chain.Count > 0 && s_chain[0] == change && s_chainForcing == s_forcing) return;
            s_chainForcing = s_forcing;
            s_chain.Clear();
            s_chain.Add(change);
            while (s_chain.Count <= MaxChain)
            {
                long last = s_chain[s_chain.Count - 1];
                long before = LastChange(envMan, last - 1);
                if (last - before >= s_transition) break;
                s_chain.Add(before);
            }
        }

        // The latest second at or before this one on which a client's wind target can change: an octave's period rolling
        // over, a new weather period one transition in, or a weather forced or released (a client takes that at once).
        private static long LastChange(EnvMan envMan, long second)
        {
            long change = s_forcedSince <= second ? s_forcedSince : long.MinValue;
            foreach (int octave in Octaves)
            {
                long period = OctavePeriod(envMan, octave);
                change = Math.Max(change, FloorDiv(second, period) * period);
            }
            long weather = WeatherPeriod(envMan);
            long lag = WeatherLag();
            return Math.Max(change, FloorDiv(second - lag, weather) * weather + lag);
        }

        private static bool TrySample(Vector3 point, out Vector4 previous, out Vector4 current, out string env)
        {
            previous = current = default;
            env = null;
            EnvMan envMan = EnvMan.instance;
            if (!s_timed || envMan == null || WorldGenerator.instance == null || s_chain.Count == 0) return false;

            bool ashlands = WorldGenerator.IsAshlands(point.x, point.z);
            // Vanilla EnvMan passes the height as the second coordinate here; kept so the pick matches a client's.
            bool deepnorth = WorldGenerator.IsDeepnorth(point.x, point.y);
            BiomeSector sector = Sector(envMan, point, ashlands, deepnorth);
            if (sector == null) return false;

            var key = (sector, ashlands, deepnorth);
            if (!s_cache.TryGetValue(key, out SectorWind wind))
            {
                if (s_cache.Count >= CacheLimit) s_cache.Clear();
                wind = new SectorWind();
                s_cache[key] = wind;
            }
            if (wind.Change != s_chain[0] || wind.Forcing != s_chainForcing)
            {
                wind.Change = s_chain[0];
                wind.Forcing = s_chainForcing;
                wind.Valid = Chain(envMan, sector, ashlands, deepnorth, out wind.Previous, out wind.Current, out wind.EnvName);
            }
            if (!wind.Valid) return false;
            previous = wind.Previous;
            current = wind.Current;
            env = wind.EnvName;
            Announce(envMan);
            return true;
        }

        // The wind the latest transition starts from and heads to. It starts from the last settled target, carried through
        // each change that came before the transition in progress had finished.
        private static bool Chain(EnvMan envMan, BiomeSector sector, bool ashlands, bool deepnorth,
                                  out Vector4 previous, out Vector4 current, out string envName)
        {
            previous = current = default;
            envName = null;
            int oldest = s_chain.Count - 1;
            if (!Target(envMan, sector, ashlands, deepnorth, s_chain[oldest] - 1, out previous, out _)) return false;
            for (int i = oldest; i > 0; i--)
            {
                if (!Target(envMan, sector, ashlands, deepnorth, s_chain[i], out Vector4 target, out _)) return false;
                previous = Blend(previous, target, (s_chain[i - 1] - s_chain[i]) / s_transition);
            }
            return Target(envMan, sector, ashlands, deepnorth, s_chain[0], out current, out envName);
        }

        private static Vector4 Blend(Vector4 from, Vector4 to, float t)
        {
            t = Mathf.Clamp01(t);
            Vector3 direction = Vector3.Slerp(new Vector3(from.x, 0f, from.z), new Vector3(to.x, 0f, to.z), t).normalized;
            return new Vector4(direction.x, 0f, direction.z, Mathf.Lerp(from.w, to.w, t));
        }

        // Vanilla's EnvMan.GetBiome, for a point instead of the camera.
        private static BiomeSector Sector(EnvMan envMan, Vector3 point, bool ashlands, bool deepnorth)
        {
            BiomeSector sector = WorldGenerator.instance.GetBiomeSector(point);
            if (!(ashlands || deepnorth)) return sector;
            if (s_heightmap == null || !s_heightmap.IsPointInside(point)) s_heightmap = Heightmap.FindHeightmap(point);
            if (s_heightmap == null) return sector;
            s_heightmap.GetWorldHeight(point, out float height);
            if (height > envMan.m_oceanLevelEnvCheckAshlandsDeepnorth) return sector;
            var biomes = ZNet.World?.m_biomeData?.Biomes;
            if (biomes == null || !biomes.TryGetValue(ashlands ? Heightmap.Biome.AshLands : Heightmap.Biome.DeepNorth, out var info)
                || info.Sectors.Count == 0) return sector;
            return info.Sectors[0];
        }

        // A client's wind target on this second: vanilla's UpdateWind octaves on the weather its UpdateEnvironment picked.
        private static bool Target(EnvMan envMan, BiomeSector sector, bool ashlands, bool deepnorth, long second,
                                   out Vector4 wind, out string envName)
        {
            wind = default;
            envName = null;
            UnityEngine.Random.State saved = UnityEngine.Random.state;
            try
            {
                EnvSetup env = Weather(envMan, sector, ashlands, deepnorth, second);
                if (env == null) return false;
                float angle = 0f;
                float intensity = OctaveStart;
                foreach (int octave in Octaves)
                {
                    UnityEngine.Random.InitState((int)(second / OctavePeriod(envMan, octave)));
                    angle += UnityEngine.Random.value * (Mathf.PI * 2f / octave);
                    intensity += (UnityEngine.Random.value - OctaveCentre) / octave;
                }
                intensity = Mathf.Clamp(Mathf.Lerp(env.m_windMin, env.m_windMax, intensity), MinIntensity, MaxIntensity);
                wind = new Vector4(Mathf.Sin(angle), 0f, Mathf.Cos(angle), intensity);
                envName = env.m_name;
                return true;
            }
            finally
            {
                UnityEngine.Random.state = saved;
            }
        }

        // A weather forced on every peer (vanilla's 'env' command, Vedr's forced weather) first, as GetCurrentEnvironment does.
        private static EnvSetup Weather(EnvMan envMan, BiomeSector sector, bool ashlands, bool deepnorth, long second)
        {
            EnvSetup forcedEverywhere = Named(envMan, second >= s_forcedSince ? s_forced : s_forcedBefore);
            if (forcedEverywhere != null) return forcedEverywhere;
            foreach (AltBiome altBiome in sector.AltBiomes)
            {
                EnvSetup forced = Named(envMan, altBiome.m_forceEnvironment);
                if (forced != null) return forced;
            }
            UnityEngine.Random.InitState((int)FloorDiv(second - WeatherLag(), WeatherPeriod(envMan)));
            List<EnvEntry> entries = envMan.GetAvailableEnvironments(sector);
            if (entries == null || entries.Count == 0) return null;
            EnvSetup chosen = s_selectWeighted(envMan, entries);
            foreach (EnvEntry entry in entries)
            {
                if (entry.m_ashlandsOverride && ashlands) chosen = entry.m_env;
                if (entry.m_deepnorthOverride && deepnorth) chosen = entry.m_env;
            }
            return chosen;
        }

        private static EnvSetup Named(EnvMan envMan, string name) =>
            string.IsNullOrEmpty(name) ? null : s_environments(envMan)?.Find(e => e.m_name == name);

        private static long OctavePeriod(EnvMan envMan, int octave) => Math.Max(1L, envMan.m_windPeriodDuration / octave);

        private static long WeatherPeriod(EnvMan envMan) => Math.Max(1L, envMan.m_environmentDuration);

        private static long WeatherLag() => (long)Math.Ceiling(s_transition);

        private static long FloorDiv(long value, long divisor) => (long)Math.Floor((double)value / divisor);

        private static void Announce(EnvMan envMan)
        {
            if (s_announced) return;
            s_announced = true;
            FiresLogger.LogInfo($"[SeaWind] no camera on this peer: the wave field's wind comes from each water point's biome and "
                                + $"net time (weather period {envMan.m_environmentDuration} s, wind period {envMan.m_windPeriodDuration} s, "
                                + $"transition {envMan.m_windTransitionDuration:0.#} s). {OffArgument} turns it off.");
        }
    }
}
