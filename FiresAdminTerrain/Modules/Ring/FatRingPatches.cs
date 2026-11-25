using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // 0.2.1479 RING SAFETY (Fire 11-18: "nothing we do has any effect on the current functions that already work for all of our other
    // worlds ... any patches that only apply to ringworld need to only run when we're actually connected to a ringworld").
    //
    // Every ring-only Harmony patch lives here as a DYNAMIC patch: Apply() on joining a ring world, Remove() on leaving it (FatHaloArc.Tick
    // sees the world change, or ZNet.OnDestroy, which is itself patched only while a ring world is up). On every other world none of these
    // methods is patched at all: vanilla IL, no early-return checks. Until 0.2.1478 they were [HarmonyPatch] attributes applied on every world
    // (Tools\PHASEPLAN_ringworld_A_20261118.md, "Existing Halo code against the rule").
    // Each patch is removed by its own method (Unpatch(target, method)), never "all of FAT's patches on that target": FAT has other patches on
    // several of these targets (e.g. RavenWaterPatch on SpawnSystem.IsSpawnPointGood). Patch methods keep their [HarmonyPriority] attributes
    // (HarmonyMethod(MethodInfo) reads them).
    internal static class FatRingPatches
    {
        private sealed class Entry
        {
            internal string Name;
            internal MethodBase Target;
            internal MethodInfo Prefix, Postfix, Transpiler;
        }

        private static List<Entry> s_entries;
        private static bool s_applied;

        internal static bool Applied => s_applied;

        /// <summary>The connected world is a ring world. The HaloFlat ring-only world (name + super flat profile, known to both
        /// peers at world load): these are the vertical-axis HALO patches. The RingTire (Option A, FiresWorldProfile.CurrentIsRing) gets its
        /// own patch set in the same dynamic way. Cached reads only (FatHaloArc.RingOnly).</summary>
        internal static bool RingWorld => FatHaloArc.RingOnly;

        private static List<Entry> Build()
        {
            var e = new List<Entry>
            {
                new Entry { Name = "Game.FindSpawnPoint (ring spawn)", Target = AccessTools.Method(typeof(Game), "FindSpawnPoint"),
                    Prefix = AccessTools.Method(typeof(FatHaloArc.Game_FindSpawnPoint_Halo), "Prefix") },
                new Entry { Name = "WorldGenerator.GetBiome (ring biomes)", Target = AccessTools.Method(typeof(WorldGenerator), nameof(WorldGenerator.GetBiome), new[] { typeof(Vector3) }),
                    Postfix = AccessTools.Method(typeof(FatHaloBiome.RingBiomeQuery), "Postfix") },
                new Entry { Name = "Minimap.WorldToMapPoint (strip map)", Target = AccessTools.Method(typeof(Minimap), "WorldToMapPoint"),
                    Prefix = AccessTools.Method(typeof(FatHaloMap.StripWorldToMapPoint), "Prefix") },
                new Entry { Name = "Minimap.WorldToPixel (strip map)", Target = AccessTools.Method(typeof(Minimap), "WorldToPixel"),
                    Prefix = AccessTools.Method(typeof(FatHaloMap.StripWorldToPixel), "Prefix") },
                new Entry { Name = "Minimap.MapPointToWorld (strip map)", Target = AccessTools.Method(typeof(Minimap), "MapPointToWorld"),
                    Postfix = AccessTools.Method(typeof(FatHaloMap.StripMapPointToWorld), "Postfix") },
                new Entry { Name = "Minimap.UpdatePlayerMarker (strip arrow)", Target = AccessTools.Method(typeof(Minimap), "UpdatePlayerMarker"),
                    Postfix = AccessTools.Method(typeof(FatHaloMap.StripPlayerArrow), "Postfix") },
                new Entry { Name = "SpawnSystem.IsSpawnPointGood (ring spawns)", Target = AccessTools.Method(typeof(SpawnSystem), "IsSpawnPointGood"),
                    Prefix = AccessTools.Method(typeof(FatHaloSpawns.SpawnSystem_IsSpawnPointGood), "Prefix") },
                new Entry { Name = "SpawnSystem.FindBaseSpawnPoint (ring spawns)", Target = AccessTools.Method(typeof(SpawnSystem), "FindBaseSpawnPoint"),
                    Prefix = AccessTools.Method(typeof(FatHaloSpawns.SpawnSystem_FindBaseSpawnPoint), "Prefix") },
                new Entry { Name = "SpawnSystem.Spawn (ring spawn rotation)", Target = AccessTools.Method(typeof(SpawnSystem), "Spawn"),
                    Prefix = AccessTools.Method(typeof(FatHaloSpawns.SpawnSystem_Spawn), "Prefix"),
                    Postfix = AccessTools.Method(typeof(FatHaloSpawns.SpawnSystem_Spawn), "Postfix"),
                    Transpiler = AccessTools.Method(typeof(FatHaloSpawns.SpawnSystem_Spawn), "Transpiler") },
                new Entry { Name = "CreatureSpawner.Spawn (ring floor)", Target = AccessTools.Method(typeof(CreatureSpawner), "Spawn"),
                    Postfix = AccessTools.Method(typeof(FatHaloSpawns.CreatureSpawner_Spawn), "Postfix") },
                new Entry { Name = "Character.InInterior(Vector3) (ring band)", Target = AccessTools.Method(typeof(Character), nameof(Character.InInterior), new[] { typeof(Vector3) }),
                    Prefix = AccessTools.Method(typeof(FatHaloSpawns.Character_InInterior), "Prefix") },
                new Entry { Name = "StaticPhysics.SUpdate (ring vegetation)", Target = AccessTools.Method(typeof(StaticPhysics), nameof(StaticPhysics.SUpdate)),
                    Prefix = AccessTools.Method(typeof(FatHaloVegetation.RingStaticPhysics), "Prefix") },
                new Entry { Name = "ZNet.OnDestroy (ring sectors cleared)", Target = AccessTools.Method(typeof(ZNet), "OnDestroy"),
                    Postfix = AccessTools.Method(typeof(FatHaloSectors.ZNet_OnDestroy_ClearSectors), "Postfix") },
                new Entry { Name = "ZNet.OnDestroy (ring patches removed)", Target = AccessTools.Method(typeof(ZNet), "OnDestroy"),
                    Postfix = AccessTools.Method(typeof(FatRingPatches), nameof(OnWorldShutdown)) },
            };
            return e;
        }

        /// <summary>Joining a ring world: patch every ring-only method. One Info line.</summary>
        internal static void Apply()
        {
            if (s_applied) return;
            s_applied = true;
            if (s_entries == null) s_entries = Build();
            var h = global::VerdantsAscent.FiresAdminTerrain.harmony;
            int ok = 0, bad = 0;
            foreach (var en in s_entries)
            {
                if (en.Target == null || (en.Prefix == null && en.Postfix == null && en.Transpiler == null))
                {
                    bad++; FiresLogger.LogWarning($"[FatRing] patch '{en.Name}': target or patch method missing on this game version; skipped");
                    continue;
                }
                try
                {
                    h.Patch(en.Target,
                        prefix: en.Prefix != null ? new HarmonyMethod(en.Prefix) : null,
                        postfix: en.Postfix != null ? new HarmonyMethod(en.Postfix) : null,
                        transpiler: en.Transpiler != null ? new HarmonyMethod(en.Transpiler) : null);
                    ok++;
                }
                catch (Exception ex) { bad++; FiresLogger.LogWarning($"[FatRing] patch '{en.Name}' threw: {ex.Message}"); }
            }
            FiresLogger.LogInfo($"[FatRing] ring world joined: {ok} ring-only patch(es) applied{(bad > 0 ? $", {bad} skipped" : "")} (0.2.1479: none of them exist on any other world)");
        }

        /// <summary>Leaving a ring world: remove each ring patch by its own method. One Info line.</summary>
        internal static void Remove()
        {
            if (!s_applied) return;
            s_applied = false;
            var h = global::VerdantsAscent.FiresAdminTerrain.harmony;
            int n = 0;
            foreach (var en in s_entries)
            {
                if (en.Target == null) continue;
                try
                {
                    if (en.Prefix != null) h.Unpatch(en.Target, en.Prefix);
                    if (en.Postfix != null) h.Unpatch(en.Target, en.Postfix);
                    if (en.Transpiler != null) h.Unpatch(en.Target, en.Transpiler);
                    n++;
                }
                catch (Exception ex) { FiresLogger.LogWarning($"[FatRing] unpatch '{en.Name}' threw: {ex.Message}"); }
            }
            FiresLogger.LogInfo($"[FatRing] ring world left: {n} ring-only patch(es) removed; vanilla methods unpatched again");
        }

        // ZNet.OnDestroy postfix, patched only while a ring world is up: the world is going away, so the ring goes with it.
        private static void OnWorldShutdown()
        {
            try { FatHaloArc.LeaveRingWorld("world shut down"); }
            catch (Exception ex) { FiresLogger.LogWarning($"[FatRing] leaving the ring world on shutdown threw: {ex.Message}"); }
        }
    }
}
