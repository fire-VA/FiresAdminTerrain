using System;
using System.Globalization;
using System.Reflection;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // The world's size, in one place (Fire 10-05: "make sure that our worlds can generate based off the expand world configs,
    // so if they are using a 20km world they can still convert it to voxel wrap etc"). Tools\FAT_WORLD_SIZE_PLAN.md.
    //
    // THE PROFILE IS THE ONLY SOURCE. A world's size is recorded in its FiresWorldProfile (worldradius= worldedge=
    // worldstretch= biomestretch=, only when not vanilla); the server sends the profile to every client in the handshake,
    // so server and clients always agree, whatever each machine's Expand World Size (EWS) config says. EWS is read in one
    // place only: by the server, when a world without a recorded size is initialised (RecordFromExpandWorldSize). A later
    // EWS change does not reshape a world: the recorded size wins, with a Warning naming both.
    //
    // At vanilla size every value is the exact vanilla float, so a 10 km world computes what it always did.
    internal static class FatWorldSize
    {
        internal const float VanillaRadius = 10000f;
        internal const float VanillaEdge = 10500f;

        private static float s_radius = VanillaRadius, s_edge = VanillaEdge, s_stretch = 1f, s_biomeStretch = 1f;
        private static string s_source = "vanilla";
        private static string s_logged;

        /// <summary>Playable radius (vanilla 10000).</summary>
        internal static float Radius => s_radius;
        /// <summary>Where the world ends, the radius plus its edge band (vanilla 10500).</summary>
        internal static float EdgeRadius => s_edge;
        /// <summary>EWS "Stretch world" (vanilla 1).</summary>
        internal static float Stretch => s_stretch;
        /// <summary>EWS "Stretch biomes" (vanilla 1).</summary>
        internal static float BiomeStretch => s_biomeStretch;
        /// <summary>Radius / 10000.</summary>
        internal static float Scale => s_radius / VanillaRadius;
        internal static bool IsVanilla => s_radius == VanillaRadius && s_edge == VanillaEdge && s_stretch == 1f && s_biomeStretch == 1f;
        internal static string Source => s_source;

        internal static string Describe()
            => string.Format(CultureInfo.InvariantCulture, "radius {0:0}, edge {1:0}, stretch {2:0.##}, biome stretch {3:0.##} ({4})",
                s_radius, s_edge, s_stretch, s_biomeStretch, s_source);

        // From FiresWorldProfile.Set / Clear / OnWorldInitialize: the current profile's size, or vanilla without one.
        internal static void Refresh()
        {
            var profile = FiresWorldProfile.Current;
            if (profile != null && profile.HasRecordedSize)
            {
                s_radius = profile.WorldRadius.Value;
                s_edge = profile.WorldEdge.Value;
                s_stretch = profile.WorldStretch.Value;
                s_biomeStretch = profile.BiomeStretch.Value;
                s_source = "recorded in the world profile";
            }
            else
            {
                s_radius = VanillaRadius;
                s_edge = VanillaEdge;
                s_stretch = 1f;
                s_biomeStretch = 1f;
                s_source = profile != null ? "vanilla (the world profile records no size)" : "vanilla (no world profile)";
            }
            string line = Describe();
            if (line == s_logged) return;
            s_logged = line;
            if (IsVanilla) FiresLogger.LogDebug($"[FatWorldSize] {line}");
            else FiresLogger.LogInfo($"[FatWorldSize] {line}");
        }

        // ── Expand World Size, read by reflection (soft dependency: no reference, no patch) ──────────────────────
        // EWS 1.40.0 (the Valheim 1.0 build) keeps its settings as static properties on ExpandWorldSize.Configuration.

        internal struct Size
        {
            internal float Radius, Edge, Stretch, BiomeStretch;
            internal bool IsVanilla => Radius == VanillaRadius && Edge == VanillaEdge && Stretch == 1f && BiomeStretch == 1f;
            internal bool SameAs(Size other)
                => Mathf.Abs(Radius - other.Radius) < 0.5f && Mathf.Abs(Edge - other.Edge) < 0.5f
                   && Mathf.Abs(Stretch - other.Stretch) < 0.001f && Mathf.Abs(BiomeStretch - other.BiomeStretch) < 0.001f;
            public override string ToString()
                => string.Format(CultureInfo.InvariantCulture, "radius {0:0}, edge {1:0}, stretch {2:0.##}, biome stretch {3:0.##}",
                    Radius, Edge, Stretch, BiomeStretch);
        }

        private static bool s_ewsWarned;

        internal static bool TryReadExpandWorldSize(out Size size, out string version)
        {
            size = default;
            version = null;
            try
            {
                Type config = null;
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (assembly.GetName().Name != "ExpandWorldSize") continue;
                    config = assembly.GetType("ExpandWorldSize.Configuration");
                    version = assembly.GetName().Version?.ToString();
                    break;
                }
                if (config == null) return false;
                float radius = Read(config, "WorldRadius", float.NaN);
                if (float.IsNaN(radius) || radius < 1000f)
                {
                    WarnOnce($"Expand World Size {version} is installed but its WorldRadius could not be read ({radius}); FAT records no size from it.");
                    return false;
                }
                float total = Read(config, "WorldTotalRadius", float.NaN);
                float edge = !float.IsNaN(total) && total >= radius ? total : radius + Read(config, "WorldEdgeSize", VanillaEdge - VanillaRadius);
                size = new Size
                {
                    Radius = radius,
                    Edge = edge,
                    Stretch = Read(config, "WorldStretch", 1f),
                    BiomeStretch = Read(config, "BiomeStretch", 1f),
                };
                return true;
            }
            catch (Exception ex)
            {
                WarnOnce($"reading Expand World Size's settings threw ({ex.GetType().Name}: {ex.Message}); FAT records no size from it.");
                return false;
            }
        }

        private static float Read(Type config, string name, float fallback)
        {
            var property = config.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (property == null) return fallback;
            object value = property.GetValue(null, null);
            return value is float f ? f : value is double d ? (float)d : value is int i ? i : fallback;
        }

        private static void WarnOnce(string what)
        {
            if (s_ewsWarned) return;
            s_ewsWarned = true;
            FiresLogger.LogWarning("[FatWorldSize] " + what);
        }

        // Server, as a world initialises. A profile with no recorded size takes EWS's (when EWS makes a non-vanilla world);
        // a profile with one keeps it, and a differing EWS config is named. Returns whether the profile changed.
        // An EXISTING world records EWS's size only when vanilla's generator makes its ground (EWS already shapes that
        // ground, so the record matches what is there). A world FAT's own generator (or an image) already made at vanilla
        // size keeps it: recording EWS's size would regenerate its terrain under whatever stands on it.
        internal static bool RecordFromExpandWorldSize(FiresWorldProfile profile, string worldName, bool existingWorld)
        {
            if (profile == null || !TryReadExpandWorldSize(out var ews, out string version)) return false;
            if (profile.HasRecordedSize)
            {
                var recorded = profile.RecordedSize;
                if (!recorded.SameAs(ews))
                    FiresLogger.LogWarning($"[FatWorldSize] world '{worldName}' was made at {recorded}; Expand World Size {version} now says "
                        + $"{ews}. FAT keeps the recorded size: its terrain, rivers and caches were built for it.");
                return false;
            }
            if (ews.IsVanilla) return false;
            if (existingWorld && profile.Generator != ProfileGenerator.Vanilla)
            {
                FiresLogger.LogWarning($"[FatWorldSize] world '{worldName}' was generated by FAT ({profile.Generator}) at vanilla size before "
                    + $"Expand World Size {version} ({ews}) was in use; FAT keeps it at vanilla size so its terrain is not regenerated.");
                return false;
            }
            profile.RecordSize(ews);
            FiresLogger.LogInfo($"[FatWorldSize] world '{worldName}' records Expand World Size {version}'s {ews} in its profile; "
                + "from now on that size is the world's own, whatever the config says later.");
            return true;
        }

        // A named world's size when it is NOT the loaded one (offline bakes from the menu): its profile's record, else what
        // Expand World Size would record for it now, else vanilla.
        internal static Size SizeForWorld(string worldName, out string source)
        {
            var profile = string.IsNullOrEmpty(worldName) ? null : FiresWorldProfile.Read(worldName);
            if (profile != null && profile.HasRecordedSize) { source = "recorded in its profile"; return profile.RecordedSize; }
            if (TryReadExpandWorldSize(out var ews, out string version) && !ews.IsVanilla) { source = $"Expand World Size {version}"; return ews; }
            source = "vanilla";
            return new Size { Radius = VanillaRadius, Edge = VanillaEdge, Stretch = 1f, BiomeStretch = 1f };
        }

        // ── fat_worldsize_probe [bearing=0] [to=21000] [step=250]: the proof along a ray ───────────────────────
        internal static void RegisterProbe()
        {
            new Terminal.ConsoleCommand("fat_worldsize_probe",
                "[bearing deg=0] [to m=21000] [step m=250] - FAT's world size, then height / voxel / water / river / edge along a ray (Debug lines)",
                args =>
                {
                    var inv = CultureInfo.InvariantCulture;
                    float bearing = 0f, to = 21000f, step = 250f;
                    if (args.Args.Length > 1) float.TryParse(args.Args[1], NumberStyles.Float, inv, out bearing);
                    if (args.Args.Length > 2) float.TryParse(args.Args[2], NumberStyles.Float, inv, out to);
                    if (args.Args.Length > 3) float.TryParse(args.Args[3], NumberStyles.Float, inv, out step);
                    step = Mathf.Max(10f, step);
                    if (to > MaxProbeReach) to = MaxProbeReach;
                    string head = $"[FatWorldSize] probe: {Describe()}; ray at bearing {bearing:0} to {to:0} m every {step:0} m, one sample a frame";
                    FiresLogger.LogInfo(head);
                    args.Context?.AddString(head);
                    var host = (MonoBehaviour)ZNet.instance ?? Player.m_localPlayer;
                    if (host == null) { args.Context?.AddString("[FatWorldSize] probe: join a world first."); return; }
                    host.StartCoroutine(ProbeRay(bearing, to, step, args.Context));
                });
        }

        // 0.2.777 (the lead 10-06, EWS 20 km test: two of these probes right after spawn froze the client for about 8 minutes, the
        // server dropped it, and its respawn then waited forever). The ray ran every sample in ONE frame, each asking the generator,
        // the voxel store and the water checks at unloaded points out to 21 km. It now takes one sample a frame and times each call;
        // a sample slower than SlowSampleMs is a Warning naming the call, so whatever is slow out there is named instead of hidden.
        private const float MaxProbeReach = 100000f;   // past the dedi's parking spot is never a world
        private const float SlowSampleMs = 250f;

        private static System.Collections.IEnumerator ProbeRay(float bearing, float to, float step, Terminal context)
        {
            var inv = CultureInfo.InvariantCulture;
            float rad = bearing * Mathf.Deg2Rad, dx = Mathf.Sin(rad), dz = Mathf.Cos(rad);
            int land = 0, wet = 0, samples = 0, slow = 0;
            float lastLand = 0f, lastWet = 0f, worstMs = 0f, worstAt = 0f;
            var clock = new System.Diagnostics.Stopwatch();
            int count = Mathf.FloorToInt(to / step + 0.001f);
            for (int k = 0; k <= count; k++)
            {
                var generator = WorldGenerator.instance;
                if (generator == null) { FiresLogger.LogInfo("[FatWorldSize] probe stopped: the world went away"); yield break; }
                float r = k * step, x = dx * r, z = dz * r;
                clock.Restart();
                float gen = generator.GetHeight(x, z);
                float genMs = Ms(clock);
                // The voxel store and the water checks only know loaded zones ([worldgen] 10-06): an unloaded point is "unloaded",
                // so the probe's cost never depends on where the player stands.
                bool zoneLoaded = ZoneSystem.instance != null && ZoneSystem.instance.IsZoneLoaded(new Vector3(x, 0f, z));
                clock.Restart();
                string voxel = !zoneLoaded ? "unloaded"
                    : VoxelWorld.TryFindSurfaceY(x, z, float.IsNaN(gen) ? 30f : gen, 24f, out float surface) ? surface.ToString("0.0", inv) : "-";
                float voxelMs = Ms(clock);
                clock.Restart();
                float level = 0f;
                bool water = zoneLoaded && VerdantsAscent.Water.Core.FatWaterChecks.TryWaterAt(x, z, 0.05f, false, out level, out _);
                float waterMs = Ms(clock);
                clock.Restart();
                string biome = generator.GetBiome(x, z).ToString();
                float biomeMs = Ms(clock);
                float total = genMs + voxelMs + waterMs + biomeMs;
                string band = r <= s_radius ? "inside" : r <= s_edge ? "edge band" : "past the edge";
                samples++;
                if (!float.IsNaN(gen) && gen > ZoneSystem.c_WaterLevel) { land++; lastLand = r; }
                if (water) { wet++; lastWet = r; }
                if (total > worstMs) { worstMs = total; worstAt = r; }
                string line = string.Format(inv,
                    "[FatWorldSize] probe r={0:0} ({1:0},{2:0}): generated {3:0.0}, voxel {4}, water {5}, biome {6}, {7}; {8:0.0} ms (height {9:0.0}, voxel {10:0.0}, water {11:0.0}, biome {12:0.0})",
                    r, x, z, gen, voxel, water ? level.ToString("0.0", inv) : zoneLoaded ? "-" : "unloaded", biome, band, total, genMs, voxelMs, waterMs, biomeMs);
                if (total > SlowSampleMs) { slow++; FiresLogger.LogWarning(line + " - SLOW"); }
                else FiresLogger.LogDebug(line);
                yield return null;
            }
            string done = string.Format(inv,
                "[FatWorldSize] probe done: {0} sample(s), {1} over sea level (last at r={2:0}), {3} with water (last at r={4:0}); slowest {5:0.0} ms at r={6:0}, {7} over {8:0} ms",
                samples, land, lastLand, wet, lastWet, worstMs, worstAt, slow, SlowSampleMs);
            FiresLogger.LogInfo(done);
            context?.AddString(done);
        }

        private static float Ms(System.Diagnostics.Stopwatch clock) => (float)clock.Elapsed.TotalMilliseconds;
    }
}
