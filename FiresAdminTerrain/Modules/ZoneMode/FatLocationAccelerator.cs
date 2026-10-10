using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Terrain.Voxel;

namespace VerdantsAscent.Terrain.Zones
{
    // FAT's location placement accelerator (0.2.788; Fire 10-06 via the lead: "create our own accelerator ... it will use
    // his algorithms because math is math"; multithreaded, free-running; default ON since 0.2.794, after the fit-bake A/B:
    // three fresh worlds at 8.8 s vs 22.5 s, 0 worker errors, Bonemass 5/5, StartTemple 1/1). Ideas credited to
    // Location Placement Accelerator by NickPappas (MIT); this is our own code.
    //
    // It replaces only vanilla's per-location attempt loop (ZoneSystem.GenerateLocationsTimeSliced(location, stopwatch,
    // pkg)); vanilla's outer frame stays: loading screen, ClearNonPlacedLocations, the prioritized order, progress, and
    // LocationsGenerated + GenerateLocationsCompleted. Per location type, worker threads draw sites and run every vanilla
    // 1.0.15 check in vanilla's order (ZoneSystem.cs:1209-1327) plus FAT's under-water refusal. The order-dependent part
    // (zone already holds a location, min/max distance from similar, the surround-vegetation history, the quota) runs
    // under ONE lock, so two workers can never place into one zone or break a distance. Workers never touch vanilla's
    // collections: accepted sites queue up and the main thread registers them (RegisterLocation) each frame.
    // Placement differs run to run (free-running threads), never its rules.
    [HarmonyPatch]
    internal static class FatLocationAccelerator
    {
        private const string Section = "09 - World generation";
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<int> Threads;
        internal static ConfigEntry<bool> RelaxVital;
        internal static ConfigEntry<string> HalfQuotaLocations;

        private const int PointsPerZone = 6, RelaxSteps = 4, TerrainSamples = 10;
        private const float RelaxPerStep = 0.05f, Altitude0 = 30f, FrameBudgetMs = 12f;

        internal static void Bind(ConfigFile cfg)
        {
            Enabled = cfg.Bind(Section, "Accelerate location placement", true,
                "Place a new world's locations on several threads (FAT's accelerator). Every vanilla placement rule is kept; "
                + "which spots win differs from run to run. Off = vanilla placement.");
            Threads = cfg.Bind(Section, "Accelerator threads", 0,
                "Worker threads for the accelerator; 0 = one per CPU core minus one.");
            RelaxVital = cfg.Bind(Section, "Relax vital locations", false,
                "When a boss altar, trader or other prioritized location (or one listed below) falls short, loosen its altitude "
                + "and terrain rules 5% per step, up to 4 steps, never its biome. Prioritized: at least 1; listed: at least half.");
            HalfQuotaLocations = cfg.Bind(Section, "Relax to half quota", "Crypt2,Crypt3,Crypt4,SunkenCrypt4,TrollCave02,"
                + "Mistlands_DvergrTownEntrance1,Mistlands_DvergrTownEntrance2",
                "Location prefab names the relaxation tops up to at least half their quota (with 'Relax vital locations' on).");
        }

        // ── Vanilla access ───────────────────────────────────────────────────────────────────────────────────────────
        private static readonly AccessTools.FieldRef<ZoneSystem, HashSet<Vector2s>> GeneratedZones =
            AccessTools.FieldRefAccess<ZoneSystem, HashSet<Vector2s>>("m_generatedZones");
        private static readonly System.Reflection.FieldInfo IdCacheField = AccessTools.Field(typeof(ZoneSystem), "m_locationIDCache");
        private static readonly AccessTools.FieldRef<ZoneSystem, Dictionary<string, List<ZoneSystem.LocationInstance>>> GroupCache =
            AccessTools.FieldRefAccess<ZoneSystem, Dictionary<string, List<ZoneSystem.LocationInstance>>>("m_locationGroupCache");
        private static readonly AccessTools.FieldRef<ZoneSystem, Dictionary<string, List<ZoneSystem.LocationInstance>>> MaxGroupCache =
            AccessTools.FieldRefAccess<ZoneSystem, Dictionary<string, List<ZoneSystem.LocationInstance>>>("m_locationMaxGroupCache");
        private static readonly Func<ZoneSystem, ZoneSystem.ZoneLocation, int> CountPlaced =
            AccessTools.MethodDelegate<Func<ZoneSystem, ZoneSystem.ZoneLocation, int>>(AccessTools.Method(typeof(ZoneSystem), "CountNrOfLocation"));
        private static readonly Action<ZoneSystem, ZoneSystem.ZoneLocation, Vector3, bool> Register =
            AccessTools.MethodDelegate<Action<ZoneSystem, ZoneSystem.ZoneLocation, Vector3, bool>>(AccessTools.Method(typeof(ZoneSystem), "RegisterLocation"));

        [HarmonyPatch(typeof(ZoneSystem), "GenerateLocationsTimeSliced", typeof(ZoneSystem.ZoneLocation), typeof(Stopwatch), typeof(ZPackage))]
        [HarmonyPrefix]
        private static bool Prefix(ZoneSystem __instance, ZoneSystem.ZoneLocation location, ZPackage iterationsPkg, ref IEnumerator __result)
        {
            if (t_vanilla || Enabled == null || !Enabled.Value || location == null || WorldGenerator.instance == null || ZNet.World?.m_biomeData == null) return true;
            if (!ReferenceEquals(s_areaCacheFor, WorldGenerator.instance)) { s_areaCache.Clear(); s_areaCacheFor = WorldGenerator.instance; }
            __result = Place(__instance, location, iterationsPkg);
            return false;
        }

        // ── One location type ────────────────────────────────────────────────────────────────────────────────────────
        private sealed class Rules
        {
            internal float MinAlt, MaxAlt, MinDelta, MaxDelta;
            internal static Rules Of(ZoneSystem.ZoneLocation l, int step)
            {
                float altSlack = step * RelaxPerStep * Mathf.Max(10f, Mathf.Abs(l.m_maxAltitude - l.m_minAltitude));
                float deltaScale = 1f + step * RelaxPerStep;
                return new Rules
                {
                    MinAlt = l.m_minAltitude - altSlack, MaxAlt = l.m_maxAltitude + altSlack,
                    MinDelta = l.m_minTerrainDelta / deltaScale, MaxDelta = l.m_maxTerrainDelta * deltaScale,
                };
            }
        }

        // Errors by vanilla's names, summed over workers with Interlocked.
        private sealed class Tally
        {
            internal int Iterations, LocationInZone, CenterDistance, Biome, BiomeArea, Altitude, Forest, CenterRing, TerrainDelta,
                UnderWater, Similar, NotSimilar, Vegetation, AltBiomeMissing, AltBiomeBlock, Surround;
            public override string ToString()
                => $"zone taken {LocationInZone}, centre distance {CenterDistance}, biome {Biome}, biome area {BiomeArea}, altitude {Altitude}, "
                + $"forest {Forest}, centre ring {CenterRing}, terrain delta {TerrainDelta} (FAT water {UnderWater}), similar {Similar}, "
                + $"not similar {NotSimilar}, vegetation {Vegetation}, alt biome missing {AltBiomeMissing}, alt biome block {AltBiomeBlock}, "
                + $"surround {Surround}";
        }

        // Shared state of one pass; everything order-dependent is read and written under Gate.
        private sealed class Pass
        {
            internal ZoneSystem.ZoneLocation Location;
            internal Rules Rules;
            internal int Attempts, Quota, Placed, AttemptCursor, MaxRangeBase;
            internal float MaxRadius;
            internal readonly object Gate = new object();
            internal HashSet<Vector2s> Taken, Generated;
            internal List<Vector3> SameId, SameGroup, SameMaxGroup;
            internal readonly List<float> SurroundHistory = new List<float>();
            internal readonly ConcurrentQueue<Vector3> Accepted = new ConcurrentQueue<Vector3>();
            internal readonly Tally Tally = new Tally();
            internal int Failed;
            internal bool Abort;
            internal string FailMessage, FailStack;
        }

        // Vanilla WorldGenerator.GetBiomeArea(Vector2s) (WorldGenerator.cs:598): Edge unless the zone centre and its 8
        // neighbours 64 m away share one biome. Vanilla caches it in a plain static Dictionary (s_cachedBiomeAreas, and
        // s_cachedBiomes under it), which workers must not write (0.2.788's A/B: "An item with the same key has already been
        // added. Key: -64,0"); this is the same rule on a concurrent cache of our own.
        private static readonly ConcurrentDictionary<Vector2s, Heightmap.BiomeArea> s_areaCache = new ConcurrentDictionary<Vector2s, Heightmap.BiomeArea>();
        private static WorldGenerator s_areaCacheFor;
        private static readonly Vector2s[] AreaOffsets =
        {
            new Vector2s(-64, -64), new Vector2s(64, -64), new Vector2s(64, 64), new Vector2s(-64, 64),
            new Vector2s(-64, 0), new Vector2s(64, 0), new Vector2s(0, -64), new Vector2s(0, 64),
        };

        private static Heightmap.BiomeArea BiomeAreaAt(WorldGenerator wg, Vector2s zone)
        {
            var centre = new Vector2s(zone.x * 64, zone.y * 64);
            if (s_areaCache.TryGetValue(centre, out var cached)) return cached;
            var own = wg.GetBiome(centre.x, centre.y);
            var area = Heightmap.BiomeArea.Median;
            foreach (var o in AreaOffsets)
                if (wg.GetBiome(centre.x - o.x, centre.y - o.y) != own) { area = Heightmap.BiomeArea.Edge; break; }
            s_areaCache[centre] = area;
            return area;
        }

        // Runs vanilla's own per-location loop (this prefix stands aside for it).
        [ThreadStatic] private static bool t_vanilla;
        private static readonly System.Reflection.MethodInfo VanillaLoop =
            AccessTools.Method(typeof(ZoneSystem), "GenerateLocationsTimeSliced", new[] { typeof(ZoneSystem.ZoneLocation), typeof(Stopwatch), typeof(ZPackage) });

        private static IEnumerator Place(ZoneSystem zs, ZoneSystem.ZoneLocation location, ZPackage pkg)
        {
            var clock = Stopwatch.StartNew();
            int placed = CountPlaced(zs, location);
            int iterations = 0;
            var tallies = new List<Tally>();
            int threads = WorkerCount(out string single);
            bool failed = false;
            if (!location.m_unique || placed <= 0)
            {
                int steps = RelaxVital != null && RelaxVital.Value ? RelaxSteps : 0;
                for (int step = 0; step <= steps && placed < Target(location, step); step++)
                {
                    var pass = Start(zs, location, placed, step);
                    var workers = new Thread[threads];
                    for (int t = 0; t < threads; t++)
                    {
                        int index = t;
                        workers[t] = new Thread(() => Work(pass, index)) { IsBackground = true, Name = $"FatLocations-{index}" };
                        workers[t].Start();
                    }
                    var frame = Stopwatch.StartNew();
                    while (workers.Any(w => w.IsAlive))
                    {
                        Drain(zs, pass);
                        if (frame.Elapsed.TotalMilliseconds >= FrameBudgetMs) { yield return null; frame.Restart(); }
                        else Thread.Sleep(1);
                    }
                    Drain(zs, pass);
                    placed = CountPlaced(zs, location);
                    iterations += pass.Tally.Iterations;
                    tallies.Add(pass.Tally);
                    if (pass.Failed > 0)
                    {
                        FiresLogger.LogWarning($"[LocationAccel] {location.m_prefab.Name}: the threaded pass stopped on an error ({pass.FailMessage}); "
                            + $"{placed}/{location.m_quantity} placed, vanilla's own loop finishes it. First error:\n{pass.FailStack}");
                        failed = true;
                        break;
                    }
                    if (step > 0)
                        FiresLogger.LogDebug($"[LocationAccel] {location.m_prefab.Name} relaxed (step {step}: altitude {pass.Rules.MinAlt:0.#}..{pass.Rules.MaxAlt:0.#}, "
                            + $"terrain delta {pass.Rules.MinDelta:0.##}..{pass.Rules.MaxDelta:0.##}): now {placed}/{location.m_quantity}.");
                }
            }
            // A threaded pass that stopped on an error never leaves a type short: vanilla's own loop (main thread, its own
            // RNG and caches) runs on top of what was placed, to the same quota, exactly as with the accelerator off.
            // Vital (prioritized: bosses, the start temple, traders) are topped up the same way whenever they fall short.
            if ((failed || location.m_prioritized) && VanillaLoop != null && placed < location.m_quantity && !(location.m_unique && placed > 0))
            {
                var vanillaPkg = new ZPackage();
                IEnumerator loop;
                t_vanilla = true;
                try { loop = (IEnumerator)VanillaLoop.Invoke(zs, new object[] { location, Stopwatch.StartNew(), vanillaPkg }); }
                finally { t_vanilla = false; }
                yield return loop;
                vanillaPkg.SetPos(0);
                iterations += vanillaPkg.ReadInt();
                int before = placed;
                placed = CountPlaced(zs, location);
                FiresLogger.LogWarning($"[LocationAccel] {location.m_prefab.Name}: vanilla's loop placed {placed - before} more, now {placed}/{location.m_quantity}.");
            }
            pkg.Write(iterations);
            pkg.SetPos(0);
            string line = $"[LocationAccel] {location.m_prefab.Name}: placed {placed}/{location.m_quantity} in {clock.Elapsed.TotalMilliseconds:0} ms "
                + $"on {threads} thread(s){single}, {iterations:N0} site check(s); refused by: {tallies.FirstOrDefault()}";
            if (placed < location.m_quantity) FiresLogger.LogDebug(line + " (short)");
            else FiresLogger.LogDebug(line);
        }

        private static int Target(ZoneSystem.ZoneLocation l, int step)
        {
            if (step == 0) return l.m_quantity;
            if (l.m_prioritized) return Math.Min(1, l.m_quantity);
            var names = (HalfQuotaLocations?.Value ?? "").Split(',').Select(s => s.Trim());
            return names.Contains(l.m_prefab.Name) ? (l.m_quantity + 1) / 2 : 0;
        }

        private static Pass Start(ZoneSystem zs, ZoneSystem.ZoneLocation l, int placed, int step)
        {
            var pass = new Pass
            {
                Location = l,
                Rules = Rules.Of(l, step),
                Attempts = l.m_prioritized ? 60000 : 12000,
                Quota = step == 0 ? l.m_quantity : Target(l, step),
                Placed = placed,
                MaxRadius = Mathf.Max(l.m_exteriorRadius, l.m_interiorRadius),
                MaxRangeBase = l.m_centerFirst ? (int)l.m_minDistance : 10000,
                Taken = new HashSet<Vector2s>(zs.m_locationInstances.Keys),
                Generated = new HashSet<Vector2s>(GeneratedZones(zs) ?? new HashSet<Vector2s>()),
                SameId = IdPositions(zs, l),
                SameGroup = l.m_group.Length > 0 ? Positions(GroupCache(zs), l.m_group) : new List<Vector3>(),
                SameMaxGroup = l.m_groupMax.Length > 0 ? Positions(MaxGroupCache(zs), l.m_groupMax) : new List<Vector3>(),
            };
            return pass;
        }

        // The asset-ID cache is keyed by a type from an assembly FAT does not reference: read it as a plain dictionary.
        private static List<Vector3> IdPositions(ZoneSystem zs, ZoneSystem.ZoneLocation l)
            => IdCacheField?.GetValue(zs) is System.Collections.IDictionary d && d.Contains(l.m_prefab.m_assetID) && d[l.m_prefab.m_assetID] is List<ZoneSystem.LocationInstance> list
                ? list.Select(i => i.m_position).ToList() : new List<Vector3>();

        private static List<Vector3> Positions<TKey>(Dictionary<TKey, List<ZoneSystem.LocationInstance>> cache, TKey key)
            => cache != null && cache.TryGetValue(key, out var list) ? list.Select(i => i.m_position).ToList() : new List<Vector3>();

        // Main thread: register what the workers accepted (vanilla's own RegisterLocation fills the instance + caches).
        private static void Drain(ZoneSystem zs, Pass pass)
        {
            while (pass.Accepted.TryDequeue(out var p)) Register(zs, pass.Location, p, false);
        }

        private static int WorkerCount(out string reason)
        {
            reason = "";
            // Two biome readers outside FAT's control read state the main thread may change: with any present, one worker.
            if (Persistence.TileAnchorRegistry.Count > 0) { reason = " (tile anchors present: one worker)"; return 1; }
            if (ForeignForceBiome()) { reason = " (force-biome territories present: one worker)"; return 1; }
            int n = Threads != null && Threads.Value > 0 ? Threads.Value : Environment.ProcessorCount - 1;
            return Mathf.Clamp(n, 1, 64);
        }

        private static bool? s_foreignChecked;
        private static System.Reflection.FieldInfo s_forceBiomeFlag;
        private static bool ForeignForceBiome()
        {
            if (s_foreignChecked == null)
            {
                s_foreignChecked = true;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var t = asm.GetType("FiresNPCs.Territories.TerritoryRuntime", false) ?? asm.GetTypes().FirstOrDefault(x => x.Name == "TerritoryRuntime");
                    if (t == null) continue;
                    s_forceBiomeFlag = AccessTools.Field(t, "_hasAnyForceBiomeTerritories");
                    break;
                }
            }
            try { return s_forceBiomeFlag != null && s_forceBiomeFlag.GetValue(null) is bool b && b; }
            catch { return false; }
        }

        // ── Worker ───────────────────────────────────────────────────────────────────────────────────────────────────
        private static void Work(Pass pass, int index)
        {
            ZoneModeGate.EnterPlanning();   // closed zones read as the real world while planning, as on the main thread
            var tally = new Tally();
            try
            {
                var rng = new System.Random(unchecked(WorldGenerator.instance.GetSeed() * 31 + pass.Location.m_prefab.Name.GetStableHashCode() * 7
                    + index * 7919 + Environment.TickCount));
                var l = pass.Location;
                var wg = WorldGenerator.instance;
                var biomeData = ZNet.World.m_biomeData;
                while (true)
                {
                    int attempt = Interlocked.Increment(ref pass.AttemptCursor);
                    if (attempt > pass.Attempts || Volatile.Read(ref pass.Placed) >= pass.Quota) break;
                    Vector2s zone = l.m_centerFirst ? RandomZone(rng, pass.MaxRangeBase + attempt)
                        : l.AltBiomeParent != null && TryAltBiomeZone(rng, l.AltBiomeParent, out var altZone) ? altZone
                        : ZoneSystem.GetZone(AltBiomeWorldData.MapSpaceToWorldSpace(RandomPoint(rng, biomeData, l.m_biome, l.m_minAltitude >= 0f)));
                    bool taken;
                    lock (pass.Gate) taken = pass.Taken.Contains(zone);
                    if (taken) { tally.LocationInZone++; continue; }
                    if (pass.Generated.Contains(zone)) continue;
                    if (Volatile.Read(ref pass.Abort)) break;
                    if ((l.m_biomeArea & BiomeAreaAt(wg, zone)) == 0) { tally.BiomeArea++; continue; }
                    for (int j = 0; j < PointsPerZone; j++)
                    {
                        tally.Iterations++;
                        if (TryPoint(pass, rng, wg, zone, tally)) break;
                    }
                }
            }
            catch (Exception ex)
            {
                // 0.2.791: the first error stops every worker of this pass (a shared cache it touched may be inconsistent);
                // the type is then finished by vanilla's own loop on the main thread (Place), and the full stack is logged once.
                Volatile.Write(ref pass.Abort, true);
                if (Interlocked.Increment(ref pass.Failed) == 1)
                {
                    pass.FailMessage = ex.GetType().Name + ": " + ex.Message;
                    pass.FailStack = ex.ToString();
                }
            }
            finally
            {
                ZoneModeGate.ExitPlanning();
                Merge(pass.Tally, tally);
            }
        }

        // Vanilla's checks, in vanilla's order (ZoneSystem.cs:1234-1332). True when the site was accepted.
        private static bool TryPoint(Pass pass, System.Random rng, WorldGenerator wg, Vector2s zone, Tally tally)
        {
            var l = pass.Location;
            var r = pass.Rules;
            float half = 32f - pass.MaxRadius;
            Vector3 p = ZoneSystem.GetZonePos(zone) + new Vector3(Range(rng, -half, half), 0f, Range(rng, -half, half));
            float magnitude = p.magnitude;
            if ((l.m_minDistance != 0f && magnitude < l.m_minDistance) || (l.m_maxDistance != 0f && magnitude > l.m_maxDistance)) { tally.CenterDistance++; return false; }
            if ((l.m_biome & wg.GetBiome(p)) == Heightmap.Biome.None) { tally.Biome++; return false; }
            p.y = wg.GetHeight(p.x, p.z, out Color mask);
            float altitude = p.y - Altitude0;
            if (altitude < r.MinAlt || altitude > r.MaxAlt) { tally.Altitude++; return false; }
            if (l.m_inForest)
            {
                float forest = WorldGenerator.GetForestFactor(p);
                if (forest < l.m_forestTresholdMin || forest > l.m_forestTresholdMax) { tally.Forest++; return false; }
            }
            if (l.m_minDistanceFromCenter > 0f || l.m_maxDistanceFromCenter > 0f)
            {
                float flat = Utils.LengthXZ(p);
                if ((l.m_minDistanceFromCenter > 0f && flat < l.m_minDistanceFromCenter) || (l.m_maxDistanceFromCenter > 0f && flat > l.m_maxDistanceFromCenter))
                { tally.CenterRing++; return false; }
            }
            float delta = TerrainDelta(wg, rng, p, l.m_exteriorRadius);
            // 0.2.841 (Fire 10-07: "relax the terrain delta, and just level the area the location is placed"): a site over the
            // location's own max but within LevelableDelta passes, and its footprint is levelled by our pad at spawn
            // (FiresLocationPatches, via TakeLevelSite). LevelableDelta keeps cliff edges and overhangs out.
            bool level = false;
            if (delta < r.MinDelta || delta > Mathf.Max(r.MaxDelta, LevelableDelta)) { tally.TerrainDelta++; return false; }
            if (delta > r.MaxDelta) level = true;
            if (FatUnderWaterPlacement.RefusesSite(p, l.m_exteriorRadius)) { tally.TerrainDelta++; tally.UnderWater++; return false; }
            // Order-free checks ahead of the lock (vanilla runs them after the similarity tests; the verdict is the same).
            float vegetation = mask.a;
            if ((l.m_minimumVegetation > 0f && vegetation <= l.m_minimumVegetation) || (l.m_maximumVegetation < 1f && vegetation >= l.m_maximumVegetation))
            { tally.Vegetation++; return false; }
            var sector = wg.GetBiomeSector(p);
            if (l.AltBiomeParent != null && !sector.AltBiomes.Any(x => x.m_name == l.AltBiomeParent)) { tally.AltBiomeMissing++; return false; }
            if (sector.AltBiomes.Any(x => x.m_blockLocationNames.Contains(l.m_name))) { tally.AltBiomeBlock++; return false; }
            float surround = l.m_surroundCheckVegetation ? Surround(wg, l, p) : 0f;

            lock (pass.Gate)
            {
                if (pass.Placed >= pass.Quota) return true;
                if (pass.Taken.Contains(zone)) { tally.LocationInZone++; return true; }
                if (l.m_minDistanceFromSimilar > 0f && (AnyWithin(pass.SameId, p, l.m_minDistanceFromSimilar) || AnyWithin(pass.SameGroup, p, l.m_minDistanceFromSimilar)))
                { tally.Similar++; return false; }
                if (l.m_maxDistanceFromSimilar > 0f && !(AnyWithin(pass.SameId, p, l.m_maxDistanceFromSimilar) || AnyWithin(pass.SameMaxGroup, p, l.m_maxDistanceFromSimilar)))
                { tally.NotSimilar++; return false; }
                if (l.m_surroundCheckVegetation)
                {
                    pass.SurroundHistory.Add(surround);
                    if (pass.SurroundHistory.Count < 10) { tally.Surround++; return false; }
                    float max = pass.SurroundHistory.Max(), avg = pass.SurroundHistory.Average();
                    if (surround < avg + (max - avg) * l.m_surroundBetterThanAverage) { tally.Surround++; return false; }
                }
                pass.Taken.Add(zone);
                pass.SameId.Add(p);
                if (l.m_group.Length > 0) pass.SameGroup.Add(p);
                if (l.m_groupMax.Length > 0) pass.SameMaxGroup.Add(p);
                pass.Accepted.Enqueue(p);
                pass.Placed++;
                if (level) s_levelSites[zone] = new LevelSite { Centre = p, Delta = delta, Radius = l.m_exteriorRadius };
            }
            return true;
        }

        private static bool AnyWithin(List<Vector3> list, Vector3 p, float radius)
        {
            float r2 = radius * radius;
            for (int i = 0; i < list.Count; i++)
                if ((list[i] - p).sqrMagnitude < r2) return true;
            return false;
        }

        // ── 0.2.841: sites accepted over their own terrain delta, levelled at spawn ───────────────────────────────────
        internal struct LevelSite { public Vector3 Centre; public float Delta, Radius; }
        private const float LevelableDelta = 16f;
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Vector2s, LevelSite> s_levelSites
            = new System.Collections.Concurrent.ConcurrentDictionary<Vector2s, LevelSite>();

        // 0.2.841b (the lead for Fire: Fire's own worlds stay untouched): levelled sites and Ghost-spawn pads apply only to a world
        // FIRST PLAYED on 0.2.841+. The marker FiresWorldGen\<world>\firesgen_<world>_locationlevel.txt is written once, at the
        // first check of a session where it is missing: "on" when the world has no saved .db yet (a new world), else "off".
        // Existing worlds keep the 0.2.840 behaviour (vanilla modifiers, pads on Full spawns only) forever.
        private static string s_levelWorld;
        private static bool s_levelOn;
        internal static bool LevelingOn
        {
            get
            {
                var world = ZNet.World;
                if (world == null) return false;
                if (s_levelWorld == world.m_name) return s_levelOn;
                s_levelWorld = world.m_name;
                s_levelOn = false;
                try
                {
                    string path = VerdantsAscent.Terrain.Voxel.FiresWorldRecords.PathFor(world.m_name, "_locationlevel.txt");
                    if (System.IO.File.Exists(path)) s_levelOn = System.IO.File.ReadAllText(path).Trim() == "on";
                    else
                    {
                        string db = world.GetDBPath();
                        s_levelOn = string.IsNullOrEmpty(db) || !System.IO.File.Exists(db);
                        System.IO.File.WriteAllText(path, s_levelOn ? "on" : "off");
                    }
                    FiresLogger.LogDebug($"[LocationLevel] world '{world.m_name}': levelled location sites {(s_levelOn ? "ON (first played on 0.2.841+)" : "OFF (an existing world: vanilla behaviour kept)")}");
                }
                catch (Exception ex) { FiresLogger.LogWarning($"[LocationLevel] marker: {ex.Message}; levelled sites off"); s_levelOn = false; }
                return s_levelOn;
            }
        }

        // From SpawnLocation's prefix (FatVanillaHooks), every spawn and every session: a surface location whose footprint's
        // generated ground spreads more than its own max terrain delta is a level site for this session. Deterministic (fixed
        // samples, not the placer's random ones), so a reload levels the same sites the placement did.
        private const int LevelRingSamples = 12;
        internal static void NoteSpawn(ZoneSystem.ZoneLocation l, Vector3 p)
        {
            if (!LevelingOn) return;
            if (l == null || l.m_snapToWater || l.m_maxTerrainDelta <= 0f || WorldGenerator.instance == null) return;
            if (VerdantsAscent.Terrain.Voxel.FatUndergroundLocations.IsUnderground(p)) return;
            var wg = WorldGenerator.instance;
            float r = l.m_exteriorRadius, high = wg.GetHeight(p.x, p.z), low = high;
            for (int ring = 1; ring <= 2; ring++)
                for (int i = 0; i < LevelRingSamples; i++)
                {
                    float a = i * Mathf.PI * 2f / LevelRingSamples, d = r * ring * 0.5f;
                    float h = wg.GetHeight(p.x + Mathf.Cos(a) * d, p.z + Mathf.Sin(a) * d);
                    if (h > high) high = h;
                    if (h < low) low = h;
                }
            float delta = high - low;
            if (delta <= l.m_maxTerrainDelta) return;
            s_levelSites[ZoneSystem.GetZone(p)] = new LevelSite { Centre = p, Delta = delta, Radius = r };
        }

        // The site to level at a location spawned at p (its zone, within a few metres), or false.
        internal static bool TryLevelSite(Vector3 p, out LevelSite site)
        {
            if (s_levelSites.TryGetValue(ZoneSystem.GetZone(p), out site) && Utils.DistanceXZ(site.Centre, p) < 4f) return true;
            site = default;
            return false;
        }

        // A point inside a site that is levelled by our pad (its vanilla terrain modifiers are suppressed there).
        internal static bool InLevelSite(Vector3 p)
        {
            if (s_levelSites.IsEmpty) return false;
            var zone = ZoneSystem.GetZone(p);
            for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++)
                    if (s_levelSites.TryGetValue(new Vector2s(zone.x + dx, zone.y + dz), out var s) && Utils.DistanceXZ(s.Centre, p) <= s.Radius + 2f) return true;
            return false;
        }

        // 0.2.841: an alt-biome variant samples zones inside the sectors that carry its alt biome (vanilla's rule, fewer wasted tries).
        private static bool TryAltBiomeZone(System.Random rng, string altBiome, out Vector2s zone)
        {
            zone = default;
            foreach (var alt in AltBiomeList.m_altBiomes)
            {
                if (alt == null || alt.m_name != altBiome || alt.Sectors == null || alt.Sectors.Count == 0) continue;
                var s = alt.Sectors[rng.Next(alt.Sectors.Count)];
                zone = new Vector2s(rng.Next(s.MinZone.x, s.MaxZone.x + 1), rng.Next(s.MinZone.y, s.MaxZone.y + 1));
                return true;
            }
            return false;
        }

        // Vanilla WorldGenerator.GetTerrainDelta (10 random points inside the radius, max − min height) on our own RNG.
        private static float TerrainDelta(WorldGenerator wg, System.Random rng, Vector3 c, float radius)
        {
            float high = -999999f, low = 999999f;
            for (int i = 0; i < TerrainSamples; i++)
            {
                double a = rng.NextDouble() * Math.PI * 2.0, d = Math.Sqrt(rng.NextDouble()) * radius;
                float h = wg.GetHeight(c.x + (float)(Math.Cos(a) * d), c.z + (float)(Math.Sin(a) * d));
                if (h < low) low = h;
                if (h > high) high = h;
            }
            return high - low;
        }

        // Vanilla's surround-vegetation score (ZoneSystem.cs:1298-1316).
        private static float Surround(WorldGenerator wg, ZoneSystem.ZoneLocation l, Vector3 p)
        {
            float sum = 0f;
            for (int layer = 0; layer < l.m_surroundCheckLayers; layer++)
            {
                float ring = (float)(layer + 1) / l.m_surroundCheckLayers * l.m_surroundCheckDistance;
                for (int k = 0; k < 6; k++)
                {
                    float f = k / 6f * Mathf.PI * 2f;
                    wg.GetHeight(p.x + Mathf.Sin(f) * ring, p.z + Mathf.Cos(f) * ring, out Color m);
                    sum += m.a * ((l.m_surroundCheckDistance - ring) / (l.m_surroundCheckDistance * 2f));
                }
            }
            return sum;
        }

        // Vanilla GetRandomZone (ZoneSystem.cs:1407) on our RNG.
        private static Vector2s RandomZone(System.Random rng, float range)
        {
            int max = Math.Max(1, (int)range / 64);
            Vector2s id;
            do id = new Vector2s(rng.Next(-max, max), rng.Next(-max, max));
            while (ZoneSystem.GetZonePos(id).magnitude >= 10000f);
            return id;
        }

        // Vanilla AltBiomeWorldData.GetRandomPointByBiomes[AboveSeaLevel] (AltBiomeWorldData.cs:309-376) on our RNG, with
        // FAT's guard (AltBiomePointGuard): an empty biome falls back to the biome with the most points.
        private static BiomePointCoordinate RandomPoint(System.Random rng, AltBiomeWorldData data, Heightmap.Biome mask, bool aboveSea)
        {
            var biome = PickBiome(rng, mask);
            if (!data.Biomes.TryGetValue(biome, out var info) || info.AllPoints.Count == 0)
                info = data.Biomes.Values.Where(v => v != null && v.AllPoints.Count > 0).OrderByDescending(v => v.AllPoints.Count).FirstOrDefault();
            if (info == null) return default;
            var from = aboveSea && info.AllPointsAboveSeaLevel.Count > 0 ? info.AllPointsAboveSeaLevel : info.AllPoints;
            return from[rng.Next(0, from.Count)];
        }

        private static readonly Heightmap.Biome[] PickOrder =
        {
            Heightmap.Biome.Meadows, Heightmap.Biome.Swamp, Heightmap.Biome.Mountain, Heightmap.Biome.BlackForest, Heightmap.Biome.Plains,
            Heightmap.Biome.AshLands, Heightmap.Biome.DeepNorth, Heightmap.Biome.Ocean, Heightmap.Biome.Mistlands,
        };

        // One biome out of a mask, uniformly (vanilla's RandomBiomeFromBiomes has an off-by-one that never picks the last;
        // a uniform pick keeps every biome of the mask reachable).
        private static Heightmap.Biome PickBiome(System.Random rng, Heightmap.Biome mask)
        {
            if ((mask & (mask - 1)) == 0) return mask;
            var present = PickOrder.Where(b => (mask & b) != 0).ToArray();
            return present.Length == 0 ? mask : present[rng.Next(0, present.Length)];
        }

        private static float Range(System.Random rng, float a, float b) => a + (float)rng.NextDouble() * (b - a);

        private static void Merge(Tally into, Tally from)
        {
            Interlocked.Add(ref into.Iterations, from.Iterations);
            Interlocked.Add(ref into.LocationInZone, from.LocationInZone);
            Interlocked.Add(ref into.CenterDistance, from.CenterDistance);
            Interlocked.Add(ref into.Biome, from.Biome);
            Interlocked.Add(ref into.BiomeArea, from.BiomeArea);
            Interlocked.Add(ref into.Altitude, from.Altitude);
            Interlocked.Add(ref into.Forest, from.Forest);
            Interlocked.Add(ref into.CenterRing, from.CenterRing);
            Interlocked.Add(ref into.TerrainDelta, from.TerrainDelta);
            Interlocked.Add(ref into.UnderWater, from.UnderWater);
            Interlocked.Add(ref into.Similar, from.Similar);
            Interlocked.Add(ref into.NotSimilar, from.NotSimilar);
            Interlocked.Add(ref into.Vegetation, from.Vegetation);
            Interlocked.Add(ref into.AltBiomeMissing, from.AltBiomeMissing);
            Interlocked.Add(ref into.AltBiomeBlock, from.AltBiomeBlock);
            Interlocked.Add(ref into.Surround, from.Surround);
        }
    }
}
