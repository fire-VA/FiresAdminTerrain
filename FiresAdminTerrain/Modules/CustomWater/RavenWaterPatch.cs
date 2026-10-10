using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    // ── Where vanilla lets water clutter grow (0.2.683; Fire 10-01 on lilies: "Stay vanilla") ─────────────────────────────
    // Lilies and vass follow vanilla's position-only gates in ClutterSystem.GenerateVegPatch (ClutterSystem.cs:285-297):
    // the forest factor (m_inForest rules) and the fractal patchiness mask (Utils.Fbm of the world position). H11: a 6 m
    // pool where 129/129 points failed the lily fractal grows no lily however it is shaped. These answer, for a spot, the
    // share of a 1 m grid within radius that passes them, so a pond can be put where vanilla allows lilies (and vass).
    // Depth, altitude, tilt, ocean depth, vegetation mask and cleared paint are the pond's own and are NOT judged here.
    // Public and stable for [visual]'s homestead site search (soft lookup by name):
    //   float FatWaterLife.LilyFractalPass(float x, float z, float radius)   0..1, or -1 without a ClutterSystem / rule
    //   float FatWaterLife.VassFractalPass(float x, float z, float radius)   same for instanced_vass
    //   float FatWaterLife.ClutterFractalPass(string prefab, float x, float z, float radius)
    //   float FatWaterLife.UnclearedShare(float x, float z, float radius)   0.2.684: share not "cleared" by paint now, -1 unknown
    public static class FatWaterLife
    {
        public const string LilyPrefab = "instanced_waterlilies", VassPrefab = "instanced_vass";
        private const float GridStep = 1f;

        public static float LilyFractalPass(float x, float z, float radius) => ClutterFractalPass(LilyPrefab, x, z, radius);

        public static float VassFractalPass(float x, float z, float radius) => ClutterFractalPass(VassPrefab, x, z, radius);

        public static float ClutterFractalPass(string prefab, float x, float z, float radius)
        {
            var cs = ClutterSystem.instance;
            if (cs == null || cs.m_clutter == null || string.IsNullOrEmpty(prefab)) return -1f;
            ClutterSystem.Clutter rule = null;
            foreach (var c in cs.m_clutter)
                if (c != null && c.m_enabled && c.m_prefab != null && c.m_prefab.name == prefab) { rule = c; break; }
            if (rule == null) return -1f;
            radius = Mathf.Max(0f, radius);
            int points = 0, pass = 0;
            for (float dz = -radius; dz <= radius; dz += GridStep)
                for (float dx = -radius; dx <= radius; dx += GridStep)
                {
                    if (dx * dx + dz * dz > radius * radius) continue;
                    points++;
                    if (PassesPositionGates(rule, new Vector3(x + dx, 0f, z + dz))) pass++;
                }
            return points > 0 ? (float)pass / points : -1f;
        }

        // 0.2.684 ([visual] 10-01): the share of a 1 m grid within radius whose ground is NOT "cleared" right now, by vanilla's
        // Heightmap.IsCleared (paint mask r/g/b > 0.5: dirt, cultivate, paved, and FAT's [FiresWetMask] shore sand along
        // generated water). Vass is onUncleared-only (H11: 15/129 pond points refused on cleared paint), so a pond spot is
        // worth lily share × this. Reads the live paint, so call it on the client with the ground loaded. -1 = unknown:
        // no loaded near heightmap at (x, z) (none, or only a distant LOD, which carries no player paint), or no point
        // within radius had one. Points without a loaded near heightmap are left out of the share.
        //   float FatWaterLife.UnclearedShare(float x, float z, float radius)
        private static readonly AccessTools.FieldRef<Heightmap, bool> DistantLod = AccessTools.FieldRefAccess<Heightmap, bool>("m_isDistantLod");
        private static readonly AccessTools.FieldRef<Heightmap, Texture2D> PaintMask = AccessTools.FieldRefAccess<Heightmap, Texture2D>("m_paintMask");

        public static float UnclearedShare(float x, float z, float radius)
        {
            if (NearHeightmap(new Vector3(x, 0f, z)) == null) return -1f;
            radius = Mathf.Max(0f, radius);
            int points = 0, uncleared = 0;
            for (float dz = -radius; dz <= radius; dz += GridStep)
                for (float dx = -radius; dx <= radius; dx += GridStep)
                {
                    if (dx * dx + dz * dz > radius * radius) continue;
                    var p = new Vector3(x + dx, 0f, z + dz);
                    var hm = NearHeightmap(p);
                    if (hm == null) continue;
                    points++;
                    if (!hm.IsCleared(p)) uncleared++;
                }
            return points > 0 ? (float)uncleared / points : -1f;
        }

        private static Heightmap NearHeightmap(Vector3 p)
        {
            var hm = Heightmap.FindHeightmap(p);
            return hm != null && !DistantLod(hm) && PaintMask(hm) != null ? hm : null;
        }

        // ClutterSystem.cs:285-297, verbatim in order: forest factor, then fractal.
        internal static bool PassesPositionGates(ClutterSystem.Clutter c, Vector3 p)
        {
            if (c.m_inForest)
            {
                float ff = WorldGenerator.GetForestFactor(p);
                if (ff < c.m_forestTresholdMin || ff > c.m_forestTresholdMax) return false;
            }
            if (c.m_fractalScale > 0f)
            {
                float fb = Utils.Fbm(p * 0.01f * c.m_fractalScale + new Vector3(c.m_fractalOffset, 0f, 0f), 3, 1.6f, 0.7f);
                if (fb < c.m_fractalTresholdMin || fb > c.m_fractalTresholdMax) return false;
            }
            return true;
        }
    }

    // Vanilla judges "is there water here" in places that only know vanilla water (its sea and lakes sit at 30 m, its
    // WaterVolumes, its water layer). FAT's lakes, rivers and pours stand higher, so those checks missed them (Fire
    // 2026-09-30 23:0x: a raven landed under a FAT lake and the bot swam ~4 min trying to read it). Every check here asks
    // FatWaterChecks.SurfaceAt, the same water a swimming character feels.
    internal static class FatWaterChecks
    {
        // The generated-water field answers this below any lake or river: "no water here".
        private const float NoWater = -9999f;

        // The highest water surface at p that FAT knows: vanilla's own and FAT's pours and custom water through FAT's
        // Floating.GetWaterLevel postfix, plus the generated lakes and rivers (VoxelWaterSurface), which that postfix does
        // not include but Character.InWater does.
        internal static float SurfaceAt(Vector3 p)
        {
            WaterVolume volume = null;
            float level = Floating.GetWaterLevel(p, ref volume);
            float generated = VerdantsAscent.Terrain.Voxel.VoxelWaterSurface.SurfaceAt(p);
            return generated > NoWater ? Mathf.Max(level, generated) : level;
        }

        // ── ONE inland-water query (0.2.673, Fire 10-01: "our carved ponds and the water fill properly spawn fish and
        // vegetation") ─────────────────────────────────────────────────────────────────────────────────────────────
        // The generated field (GenWaterField: hydrology or the Fires generator) first. Otherwise every other water FAT
        // knows, i.e. tool pours, WaterDisk ponds (the Vpool tool), the voxel sheet and vanilla's own, through SurfaceAt,
        // measured over the SOLID ground under it (a downward ray on vanilla's solid mask, ignoring triggers and water,
        // which live on the Water layer). The fish gate, the clutter ecology, fishwater_test and the Vpool tool all ask
        // here, so a tool-carved, tool-filled pond counts exactly like hydrology water. includeSea admits the 30 m sea
        // (the Vpool tool connects to it); every ecology caller leaves it out, so seaside ground stays vanilla's.
        private const float SeaSheetGate = 30.3f;
        private const float BedProbeTop = 1000f, BedProbeLength = 2000f, BedProbeLift = 0.1f;
        private static int s_solidMask;

        internal static bool TryWaterAt(float x, float z, float minDepth, bool includeSea, out float surface, out float bed)
        {
            if (VerdantsAscent.Terrain.Voxel.GenWaterField.Ready
                && VerdantsAscent.Terrain.Voxel.GenWaterField.TryWaterSurfaceSmooth(x, z, out float level, out float depth)
                && level > SeaSheetGate && depth >= minDepth)
            {
                surface = level;
                bed = level - depth;
                return true;
            }
            surface = bed = 0f;
            if (!SolidGround(x, z, out float ground)) return false;
            float s = SurfaceAt(new Vector3(x, ground + BedProbeLift, z));
            if (s <= NoWater || s - ground < minDepth || (!includeSea && s <= SeaSheetGate)) return false;
            surface = s;
            bed = ground;
            return true;
        }

        // The top solid surface at (x, z) as vanilla's ZoneSystem.GetSolidHeight finds it, but ignoring trigger colliders.
        internal static bool SolidGround(float x, float z, out float ground)
        {
            if (s_solidMask == 0) s_solidMask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "terrain");
            if (Physics.Raycast(new Vector3(x, BedProbeTop, z), Vector3.down, out RaycastHit hit, BedProbeLength, s_solidMask, QueryTriggerInteraction.Ignore)
                && hit.collider.attachedRigidbody == null)
            {
                ground = hit.point.y;
                return true;
            }
            ground = 0f;
            return false;
        }

        // From FAT's one Player.UpdatePlacementGhost hook (FatVanillaHooks): a piece that may not stand in water (campfires,
        // beds, stations: Piece.m_noInWater) turns red in FAT water as vanilla turns it red in its own.
        internal static void RedGhostInFatWater(Player player)
        {
            if (player != Player.m_localPlayer || VerdantsAscent.Terrain.Zones.ZoneModeRefusals.PlacementStatus(player) != Player.PlacementStatus.Valid) return;
            var ghost = VerdantsAscent.Terrain.Zones.ZoneModeRefusals.PlacementGhost(player);
            if (ghost == null || !ghost.activeSelf) return;
            var piece = ghost.GetComponent<Piece>();
            if (piece == null || !piece.m_noInWater) return;
            Vector3 p = ghost.transform.position;
            if (SurfaceAt(p) <= p.y) return;
            VerdantsAscent.Terrain.Zones.ZoneModeRefusals.RefuseGhost(player, Player.PlacementStatus.Invalid);
        }
    }

    // Vanilla's tutorial raven (Raven.FindSpawnPoint) lands on solid ground above 30 m and never asks about water. A spot
    // under water is refused; the raven tries again on its next attempt with fresh random spots, as vanilla does when no
    // spot fits.
    [HarmonyPatch(typeof(Raven), "FindSpawnPoint")]
    internal static class RavenWaterPatch
    {
        private const float DryMargin = 0.2f;
        private static bool s_logged;

        private static void Postfix(ref bool __result, ref Vector3 point)
        {
            if (!__result) return;
            float surface = FatWaterChecks.SurfaceAt(point);
            if (surface <= point.y + DryMargin) return;
            __result = false;
            if (s_logged) return;
            s_logged = true;
            FiresLogger.LogDebug($"[FiresWater] a raven's landing spot ({point.x:0}, {point.y:0.0}, {point.z:0}) lies under water "
                + $"(surface {surface:0.0}); it looks again for a dry one.");
        }
    }

    // SpawnSystem judges water by Heightmap.GetOceanDepth (against 30 m), so a land creature could roll a spot on a FAT lake bed
    // under water. A spawn entry without an ocean-depth band is a land entry: refused under FAT water deeper than LandDepth.
    // Entries with a band (fish, serpents) are vanilla's own call, unchanged.
    [HarmonyPatch(typeof(SpawnSystem), "IsSpawnPointGood")]
    internal static class LandSpawnsStayOutOfWater
    {
        private const float LandDepth = 0.5f;

        private static void Postfix(SpawnSystem.SpawnData spawn, ref Vector3 spawnPoint, ref bool __result)
        {
            if (!__result || spawn == null || spawn.m_minOceanDepth != spawn.m_maxOceanDepth || InlandFishSpawns.IsFish(spawn)) return;
            if (FatWaterChecks.SurfaceAt(spawnPoint) > spawnPoint.y + LandDepth) __result = false;
        }
    }

    // Fire 2026-09-30: inland water must "spawn fish to begin with so that they CAN fish in it". SpawnSystem judges a fish
    // entry's altitude and ocean depth against the 30 m sea, so a lake or river above it never qualified. A fish entry
    // vanilla refused gets a second look at FAT inland water: the same gates, with altitude measured from the water's own
    // surface and the ocean depth being that water's depth. Fish only (a Fish component on the prefab): serpents and other
    // sea entries stay at sea. First, so FAT's refusing postfixes (closed zones, painted biomes, land out of water) still
    // judge the point after it.
    [HarmonyPatch(typeof(SpawnSystem), "IsSpawnPointGood")]
    internal static class InlandFishSpawns
    {
        private const float SeaSheetGate = 30.3f;
        private const float MinDepth = 1f;
        private static readonly System.Collections.Generic.Dictionary<GameObject, bool> s_isFish = new System.Collections.Generic.Dictionary<GameObject, bool>();
        private static bool s_logged;

        internal static bool IsFish(SpawnSystem.SpawnData spawn)
        {
            var prefab = spawn.m_prefab;
            if (prefab == null) return false;
            if (!s_isFish.TryGetValue(prefab, out bool fish))
                s_isFish[prefab] = fish = prefab.GetComponent<Fish>() != null;
            return fish;
        }

        // 0.2.663 diagnostics, one line each per session: every fish entry's bands when first seen, and the first time
        // each (entry, reason) refuses FAT inland water, so a round with no inland fish says why.
        private static readonly System.Collections.Generic.HashSet<string> s_said = new System.Collections.Generic.HashSet<string>();

        private static void SayOnce(string key, string line)
        {
            if (s_said.Add(key)) FiresLogger.LogInfo(line);
        }

        [HarmonyPriority(Priority.First)]
        private static void Postfix(SpawnSystem.SpawnData spawn, ref Vector3 spawnPoint, ref bool __result)
        {
            if (spawn == null || !IsFish(spawn)) return;
            SayOnce("bands|" + spawn.m_name + "|" + spawn.m_prefab.name, $"[FiresWater] fish entry {spawn.m_name} ({spawn.m_prefab.name}): "
                + $"biome {spawn.m_biome}, area {spawn.m_biomeArea}, altitude {spawn.m_minAltitude:0.##}..{spawn.m_maxAltitude:0.##}, "
                + $"ocean depth {spawn.m_minOceanDepth:0.##}..{spawn.m_maxOceanDepth:0.##}"
                + (spawn.m_minOceanDepth == spawn.m_maxOceanDepth ? " (no band)" : "")
                + $", tilt {spawn.m_minTilt:0}..{spawn.m_maxTilt:0}, ground offset {spawn.m_groundOffset:0.##}+{spawn.m_groundOffsetRandom:0.##}.");
            if (__result) return;
            if (!TryInlandWater(spawnPoint, out float surface, out float bed))
            {
                SayOnce("dry|" + spawn.m_name, $"[FiresWater] inland fish check: {spawn.m_name} refused at ({spawnPoint.x:0}, {spawnPoint.y:0.0}, "
                    + $"{spawnPoint.z:0}): no FAT inland water >= {MinDepth:0} m there (first such point).");
                return;
            }
            string refusal = Refusal(spawn, spawnPoint, surface, bed);
            if (refusal != null)
            {
                SayOnce("why|" + spawn.m_name + "|" + refusal.Split(' ')[0], $"[FiresWater] inland fish check: {spawn.m_name} refused at "
                    + $"({spawnPoint.x:0}, {spawnPoint.z:0}), surface {surface:0.0}, depth {surface - bed:0.0}: {refusal}.");
                return;
            }
            spawnPoint.y = bed;
            __result = true;
            if (s_logged) return;
            s_logged = true;
            FiresLogger.LogDebug($"[FiresWater] {spawn.m_name} spawns in inland water at ({spawnPoint.x:0}, {spawnPoint.z:0}): "
                + $"surface {surface:0.0}, depth {surface - bed:0.0} (first of this session).");
        }

        // FatWaterChecks.TryWaterAt: generated lakes and rivers by their surface and depth (a dedicated server's ground is
        // vanilla's heightmap, not the carved bed), else tool ponds and every other FAT water over the solid ground under
        // it. Last, any water over the ground point vanilla found.
        internal static bool TryInlandWater(Vector3 p, out float surface, out float bed)
        {
            if (FatWaterChecks.TryWaterAt(p.x, p.z, MinDepth, false, out surface, out bed)) return true;
            surface = FatWaterChecks.SurfaceAt(p);
            bed = p.y;
            return surface > SeaSheetGate && surface - bed >= MinDepth;
        }

        // Vanilla IsSpawnPointGood's gates, in its order, with the water's own surface for the sea. Tilt is left out: a fish
        // swims, and the bed under a carved lake is not the ground vanilla's normal describes.
        // Null when the point passes; otherwise the gate that refused it, its first word being the gate's name.
        internal static string Refusal(SpawnSystem.SpawnData spawn, Vector3 p, float surface, float bed)
        {
            Vector3 q = p;
            ZoneSystem.instance.GetGroundData(ref q, out _, out var biome, out var area, out _);
            if ((spawn.m_biome & biome) == Heightmap.Biome.None) return $"biome {biome} not in {spawn.m_biome}";
            if ((spawn.m_biomeArea & area) == 0) return $"area {area} not in {spawn.m_biomeArea}";
            if (ZoneSystem.instance.IsBlocked(p)) return "blocked (a piece or object over the point)";
            float alt = bed - surface;
            if (alt < spawn.m_minAltitude || alt > spawn.m_maxAltitude)
                return $"altitude {alt:0.0} outside {spawn.m_minAltitude:0.##}..{spawn.m_maxAltitude:0.##}";
            if (spawn.m_minDistanceFromCenter > 0f || spawn.m_maxDistanceFromCenter > 0f)
            {
                float d = Utils.LengthXZ(p);
                if (spawn.m_minDistanceFromCenter > 0f && d < spawn.m_minDistanceFromCenter
                    || spawn.m_maxDistanceFromCenter > 0f && d > spawn.m_maxDistanceFromCenter) return $"distance {d:0} from the centre";
            }
            float range = spawn.m_spawnRadiusMin > 0f ? spawn.m_spawnRadiusMin : 40f;
            if (!spawn.m_canSpawnCloseToPlayer && Player.IsPlayerInRange(p, range)) return $"player within {range:0} m";
            if (!spawn.m_insidePlayerBase && EffectArea.IsPointInsideArea(p, EffectArea.Type.PlayerBase)) return "base inside a player base";
            if (!spawn.m_inForest || !spawn.m_outsideForest)
            {
                bool forest = WorldGenerator.InForest(p);
                if (!spawn.m_inForest && forest || !spawn.m_outsideForest && !forest) return forest ? "forest (in forest)" : "forest (outside forest)";
            }
            if (spawn.m_minOceanDepth != spawn.m_maxOceanDepth)
            {
                float depth = surface - bed;
                if (depth < spawn.m_minOceanDepth || depth > spawn.m_maxOceanDepth)
                    return $"depth {depth:0.0} outside {spawn.m_minOceanDepth:0.##}..{spawn.m_maxOceanDepth:0.##}";
            }
            return null;
        }
    }

    // Generated lakes and rivers tell Characters they are in water (CustomWaterPatches), but nothing told a Fish or a Floating
    // body (the fishing float, a dropped item): a fish in a FAT lake counted as out of water and flopped, and a float never
    // read water, so nothing could be fished there. Each is given the generated surface while it is in that water, from FAT's
    // Fish/Floating CustomFixedUpdate prefixes (FatVanillaHooks). An object vanilla or a pour already feeds is left to them.
    internal static class GeneratedWaterFeeder
    {
        private const int EveryFrames = 4;
        private const float NoLevel = -10000f;
        private const float NoWater = -9999f;
        // Above the sheet by this much an object is out of it, as a vanilla water trigger ends a little above its surface.
        private const float AboveSurface = 1.5f;
        // Past this many entries, those not fed for StaleFrames (destroyed or unloaded objects) are dropped.
        private const int PruneAt = 4096;
        private const int StaleFrames = 600;

        // Instance id -> the frame it was last given the generated surface.
        private static readonly System.Collections.Generic.Dictionary<int, int> s_fed = new System.Collections.Generic.Dictionary<int, int>();
        private static readonly System.Collections.Generic.List<int> s_stale = new System.Collections.Generic.List<int>();
        internal static readonly AccessTools.FieldRef<Fish, float> FishLevel = AccessTools.FieldRefAccess<Fish, float>("m_inWater");
        internal static readonly AccessTools.FieldRef<Floating, float> FloatLevel = AccessTools.FieldRefAccess<Floating, float>("m_waterLevel");

        internal static void Feed(Fish fish) => Feed(fish, fish, FishLevel(fish));

        internal static void Feed(Floating floating) => Feed(floating, floating, FloatLevel(floating));

        private static void Feed(IWaterInteractable who, Component c, float current)
        {
            int id = c.GetInstanceID();
            if ((Time.frameCount + id) % EveryFrames != 0) return;
            bool ours = s_fed.ContainsKey(id);
            Vector3 p = c.transform.position;
            // 0.2.685 (H12, 10-01: a Fish2 on a tool pond's floor still read "given level never set" with the swim trigger
            // reaching the bed): the generated sheet AND tool water (WaterMeshCombiner disks), through the same reader the
            // player's swim patches use, instead of relying on a physics trigger contact a resting fish never makes.
            float surface = global::VerdantsAscent.Water.Core.CustomWaterManager.GetWaterLevelAt(p);
            // 0.2.687 (H13, 10-01: "Fish2 … given level 29.51 … OUT of water; FAT surface here 37.37"): an object vanilla
            // already gave a level (the zone-wide ocean WaterVolume, sea + tide) was left to vanilla. FAT water standing
            // higher than that level now wins, so a pond above sea level is the water an inland fish or drop is in.
            if (surface > NoWater && p.y < surface + AboveSurface && (ours || surface > current + LevelEpsilon))
            {
                who.SetLiquidLevel(surface, LiquidType.Water, null);
                if (!ours && s_fed.Count >= PruneAt) Prune();
                s_fed[id] = Time.frameCount;
            }
            else if (ours)
            {
                who.SetLiquidLevel(NoLevel, LiquidType.Water, null);
                s_fed.Remove(id);
            }
        }

        private const float LevelEpsilon = 0.01f;

        // 0.2.687: vanilla's WaterVolume rewrites its level every update (WaterVolume.cs:256), so between feeds a fish in
        // a pond above the sea would be handed the sea's 29.5 again. These raise any Water level vanilla (or anything
        // else) writes to the FAT surface at the object when that stands higher and the object is in it.
        internal static float RaiseToFatWater(Component c, float level)
        {
            if (c == null) return level;
            Vector3 p = c.transform.position;
            float surface = global::VerdantsAscent.Water.Core.CustomWaterManager.GetWaterLevelAt(p);
            return surface > NoWater && surface > level + LevelEpsilon && p.y < surface + AboveSurface ? surface : level;
        }

        private static void Prune()
        {
            int before = Time.frameCount - StaleFrames;
            s_stale.Clear();
            foreach (var kv in s_fed)
                if (kv.Value < before) s_stale.Add(kv.Key);
            foreach (int id in s_stale) s_fed.Remove(id);
        }
    }

    // 0.2.687: the level vanilla hands a fish or a floating object is raised to FAT water standing higher there
    // (GeneratedWaterFeeder.RaiseToFatWater), so the ocean WaterVolume's every-update write cannot put a pond fish "out".
    [HarmonyPatch(typeof(Fish), nameof(Fish.SetLiquidLevel))]
    internal static class FishLevelSeesFatWater
    {
        private static void Postfix(Fish __instance, float level, LiquidType type)
        {
            if (type != LiquidType.Water) return;
            float raised = GeneratedWaterFeeder.RaiseToFatWater(__instance, level);
            if (raised != level) GeneratedWaterFeeder.FishLevel(__instance) = raised;
        }
    }

    [HarmonyPatch(typeof(Floating), nameof(Floating.SetLiquidLevel))]
    internal static class FloatingLevelSeesFatWater
    {
        private static void Postfix(Floating __instance, float level, LiquidType type)
        {
            if (type != LiquidType.Water) return;
            float raised = GeneratedWaterFeeder.RaiseToFatWater(__instance, level);
            if (raised != level) GeneratedWaterFeeder.FloatLevel(__instance) = raised;
        }
    }

    // Fish steer for Fish.GetWaterLevel, which falls back to vanilla's 30 m when the fish has no vanilla WaterVolume, as in
    // FAT water: there the surface comes from FatWaterChecks.
    [HarmonyPatch(typeof(Fish), "GetWaterLevel")]
    internal static class FishSeeFatWater
    {
        private static readonly AccessTools.FieldRef<Fish, WaterVolume> VolumeOf = AccessTools.FieldRefAccess<Fish, WaterVolume>("m_waterVolume");
        private const float NoWater = -1000f;

        private static void Postfix(Fish __instance, Vector3 point, ref float __result)
        {
            if (VolumeOf(__instance) != null) return;
            float surface = FatWaterChecks.SurfaceAt(point);
            if (surface > NoWater) __result = surface;
        }
    }
}
