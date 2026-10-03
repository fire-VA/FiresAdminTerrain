using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Zones
{
    // A painted blend's creatures spawn on its painted ground and nowhere else (Fire, 2026-09-27 22:5x, by popup: "Inside
    // the circle"). Vanilla runs a zone's alt-biome spawn lists, and lets their blocks refuse the zone's other creatures,
    // for the whole zone from its four corners' sectors: a circle that missed every corner brought nothing, and one over a
    // corner brought its blend to the whole zone. In a zone a circle touches, the heightmap's corner list is set aside
    // while the zone spawns, so vanilla's own lists run with no zone-wide blocks, and each blend's list then runs once: the
    // world's own corner blends and every painted blend touching the zone. Every spawn point belongs to the newest circle
    // holding it (its blend; none for a plain brush; the world's corner blends for Restore) or, outside every circle, to
    // the world's corner blends as vanilla has them. A blend's creatures spawn only where it governs, and its blocks refuse
    // creatures only there. A zone no circle touches spawns exactly as vanilla.
    internal static partial class BiomePaint
    {
        private const string BlendListSalt = "a_";

        private static readonly AccessTools.FieldRef<SpawnSystem, Heightmap> SpawnHeightmap =
            AccessTools.FieldRefAccess<SpawnSystem, Heightmap>("m_heightmap");
        private static readonly AccessTools.FieldRef<SpawnSystem, ZNetView> SpawnView =
            AccessTools.FieldRefAccess<SpawnSystem, ZNetView>("m_nview");
        private static readonly FieldInfo SpawnNearPlayers = AccessTools.Field(typeof(SpawnSystem), "m_tempNearPlayers");
        private static readonly Action<SpawnSystem, List<SpawnSystem.SpawnData>, DateTime, bool, string> RunSpawnList =
            AccessTools.MethodDelegate<Action<SpawnSystem, List<SpawnSystem.SpawnData>, DateTime, bool, string>>(
                AccessTools.Method(typeof(SpawnSystem), "UpdateSpawnList"));

        private sealed class SpawnScope
        {
            internal readonly List<AltBiome> World = new List<AltBiome>();
            internal readonly List<KeyValuePair<Circle, List<AltBiome>>> Circles = new List<KeyValuePair<Circle, List<AltBiome>>>();
            internal readonly List<AltBiome> Lists = new List<AltBiome>();
            internal List<AltBiome> SetAside;
            internal AltBiome Running;
        }

        [ThreadStatic] private static SpawnScope t_spawnScope;

        // The blends governing a point: the newest circle holding it decides; outside every circle, the world's corners.
        private static List<AltBiome> Governing(SpawnScope scope, float wx, float wz)
        {
            foreach (var entry in scope.Circles)
            {
                if (!entry.Key.Holds(wx, wz)) continue;
                return entry.Key.Brush == Brush.Restore ? scope.World : entry.Value;
            }
            return scope.World;
        }

        // Whether a creature of `name`, from the list of blend `running` (null: a base or event list), may spawn where
        // `governing` rules.
        private static bool SpawnAllowed(List<AltBiome> governing, AltBiome running, string name)
        {
            if (running != null && !governing.Contains(running)) return false;
            foreach (var blend in governing)
                if (blend.m_blockSpawnNames.Contains(name)) return false;
            return true;
        }

        // The circles touching a zone heightmap's square (newest first), the world's own corner blends (the heightmap's
        // corner sectors, a painted one asked again with the paint left out, in the builder's corner order), and the blend
        // lists to run; false when no circle touches it.
        private static bool TryScope(Heightmap hmap, out SpawnScope scope)
        {
            scope = null;
            var snapshot = s_snapshot;
            var generator = WorldGenerator.instance;
            if (snapshot.Circles.Length == 0 || generator == null) return false;
            var centre = hmap.transform.position;
            float size = hmap.m_width * hmap.m_scale, half = size * 0.5f;
            for (int i = snapshot.Circles.Length - 1; i >= 0; i--)
            {
                var c = snapshot.Circles[i];
                float nx = Mathf.Clamp(c.X, centre.x - half, centre.x + half) - c.X;
                float nz = Mathf.Clamp(c.Z, centre.z - half, centre.z + half) - c.Z;
                if (nx * nx + nz * nz > c.R * c.R) continue;
                if (scope == null) scope = new SpawnScope();
                var blends = new List<AltBiome>();
                var sector = snapshot.Sectors[i];
                if (c.Brush == Brush.Blend && sector != null) blends.AddRange(sector.AltBiomes);
                scope.Circles.Add(new KeyValuePair<Circle, List<AltBiome>>(c, blends));
            }
            if (scope == null) return false;
            var corners = CornerBiomes(hmap);
            float x0 = centre.x - half, z0 = centre.z - half;
            for (int corner = 0; corner < CornerCount; corner++)
            {
                var sector = corners != null && corners.Length == CornerCount ? corners[corner] : null;
                if (sector != null && Array.IndexOf(snapshot.Sectors, sector) < 0)
                {
                    AddAlts(scope.World, sector);
                    continue;
                }
                t_resolving++;
                try { AddAlts(scope.World, generator.GetBiomeSector(x0 + (corner & 1) * size, z0 + (corner >> 1) * size)); }
                finally { t_resolving--; }
            }
            foreach (var blend in scope.World)
                if (blend.m_spawn.Count > 0 && !scope.Lists.Contains(blend)) scope.Lists.Add(blend);
            foreach (var entry in scope.Circles)
                foreach (var blend in entry.Value)
                    if (blend.m_spawn.Count > 0 && !scope.Lists.Contains(blend)) scope.Lists.Add(blend);
            return true;
        }

        private static void AddAlts(List<AltBiome> into, BiomeSector sector)
        {
            if (sector == null) return;
            foreach (var blend in sector.AltBiomes)
                if (blend != null && !into.Contains(blend)) into.Add(blend);
        }

        [HarmonyPatch(typeof(SpawnSystem), "UpdateSpawning")]
        private static class SpawnSystem_UpdateSpawning
        {
            private static void Prefix(SpawnSystem __instance)
            {
                t_spawnScope = null;
                var hmap = SpawnHeightmap(__instance);
                if (!Any || hmap == null || !TryScope(hmap, out var scope)) return;
                scope.SetAside = hmap.m_cornerAltBiomes;
                hmap.m_cornerAltBiomes = new List<AltBiome>();
                t_spawnScope = scope;
            }

            private static void Postfix(SpawnSystem __instance)
            {
                var scope = t_spawnScope;
                var view = SpawnView(__instance);
                if (scope == null || view == null || !view.IsValid() || !view.IsOwner() || Player.m_localPlayer == null) return;
                if (!(SpawnNearPlayers.GetValue(null) is List<Player> players) || players.Count == 0) return;
                var time = ZNet.instance.GetTime();
                try
                {
                    foreach (var blend in scope.Lists)
                    {
                        scope.Running = blend;
                        RunSpawnList(__instance, blend.m_spawn, time, false, BlendListSalt + blend.m_name);
                    }
                }
                finally { scope.Running = null; }
            }

            private static Exception Finalizer(SpawnSystem __instance, Exception __exception)
            {
                var scope = t_spawnScope;
                t_spawnScope = null;
                var hmap = SpawnHeightmap(__instance);
                if (scope?.SetAside != null && hmap != null) hmap.m_cornerAltBiomes = scope.SetAside;
                return __exception;
            }
        }

        [HarmonyPatch(typeof(SpawnSystem), "IsSpawnPointGood")]
        private static class SpawnSystem_IsSpawnPointGood
        {
            private static void Postfix(SpawnSystem.SpawnData spawn, ref Vector3 spawnPoint, ref bool __result)
            {
                var scope = t_spawnScope;
                if (!__result || scope == null) return;
                __result = SpawnAllowed(Governing(scope, spawnPoint.x, spawnPoint.z), scope.Running, spawn.m_name);
            }
        }
    }
}
