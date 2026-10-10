using System;
using System.Globalization;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Utilities
{
    // Where the undead come from (Fire 2026-09-30 20:0x, the long-form co-op run: "Keep it, deaths are data"; a Skeleton ★2
    // one-shot the bot 19 m from the CoopStream hall, and neither log said which spawner made it). Log-only: one line per
    // Skeleton / Ghost / Greyling spawn within WatchMetres of a player, from each vanilla spawn path, on whichever peer
    // runs it (the dedi under server-side simulation, else the zone's owner). Nothing about spawning changes.
    //   [SpawnOrigin] Skeleton ★2 at (x, z) from CreatureSpawner at (x, z) in Dolmen02 | from SpawnSystem, biome Meadows ...
    [HarmonyPatch]
    internal static class SpawnOriginLog
    {
        private const float WatchMetres = 200f;
        private static readonly string[] Watched = { "Skeleton", "Ghost", "Greyling", "Seeker" };
        // 0.2.786 ([lead] 10-06, the Seekers multiplying round the Infested Mine): Seekers are watched too, their lines at Debug.
        private const string DebugOnly = "Seeker";

        [HarmonyPatch(typeof(CreatureSpawner), "Spawn")]
        [HarmonyPostfix]
        private static void FromCreatureSpawner(CreatureSpawner __instance, ZNetView __result)
        {
            try
            {
                if (__result == null || !IsWatched(__result.gameObject.name)) return;
                Vector3 at = __result.transform.position;
                if (!NearPlayer(at)) return;
                Vector3 from = __instance.transform.position;
                var character = __result.GetComponent<Character>();
                Location location = Location.GetLocation(from);
                Log(Utils.GetPrefabName(__result.gameObject), character != null ? character.GetLevel() : 1, at,
                    string.Format(CultureInfo.InvariantCulture, "CreatureSpawner '{0}' at ({1:0}, {2:0}){3}, respawn {4:0} min",
                        Utils.GetPrefabName(__instance.gameObject), from.x, from.z,
                        location != null ? $" in {Utils.GetPrefabName(location.gameObject)}" : ", in no location",
                        __instance.m_respawnTimeMinuts));
            }
            catch (Exception ex) { FiresLogger.LogVerbose($"[SpawnOrigin] creature spawner line skipped: {ex.Message}"); }
        }

        [HarmonyPatch(typeof(SpawnSystem), "Spawn")]
        [HarmonyPostfix]
        private static void FromSpawnSystem(SpawnSystem.SpawnData critter, Vector3 spawnPoint, bool eventSpawner)
        {
            try
            {
                if (critter?.m_prefab == null || !IsWatched(critter.m_prefab.name) || !NearPlayer(spawnPoint)) return;
                Heightmap.Biome biome = WorldGenerator.instance != null ? WorldGenerator.instance.GetBiome(spawnPoint) : Heightmap.Biome.None;
                Log(critter.m_prefab.name, -1, spawnPoint, string.Format(CultureInfo.InvariantCulture,
                    "SpawnSystem{0} entry '{1}', biome {2}, {3}, levels {4}-{5}",
                    eventSpawner ? " (event)" : "", critter.m_name, biome,
                    critter.m_spawnAtNight && !critter.m_spawnAtDay ? "night only" : critter.m_spawnAtDay && !critter.m_spawnAtNight ? "day only" : "day and night",
                    critter.m_minLevel, critter.m_maxLevel));
            }
            catch (Exception ex) { FiresLogger.LogVerbose($"[SpawnOrigin] spawn system line skipped: {ex.Message}"); }
        }

        [HarmonyPatch(typeof(SpawnArea), "SpawnOne")]
        [HarmonyPostfix]
        private static void FromSpawnArea(SpawnArea __instance, bool __result)
        {
            try
            {
                if (!__result || __instance == null) return;
                Vector3 from = __instance.transform.position;
                if (!NearPlayer(from)) return;
                bool watched = false, seekers = false;
                foreach (var data in __instance.m_prefabs)
                    if (data?.m_prefab != null && IsWatched(data.m_prefab.name)) { watched = true; seekers |= IsDebugOnly(data.m_prefab.name); }
                if (!watched) return;
                Log(seekers ? "Seeker (one of its prefabs)" : "(one of its prefabs)", -1, from, string.Format(CultureInfo.InvariantCulture, "SpawnArea '{0}' at ({1:0}, {2:0}, y {3:0})",
                    Utils.GetPrefabName(__instance.gameObject), from.x, from.z, from.y));
            }
            catch (Exception ex) { FiresLogger.LogVerbose($"[SpawnOrigin] spawn area line skipped: {ex.Message}"); }
        }

        private static bool IsWatched(string name)
        {
            foreach (string w in Watched)
                if (name.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private static bool NearPlayer(Vector3 at)
        {
            foreach (var player in Player.GetAllPlayers())
                if (player != null && Flat(player.transform.position, at) <= WatchMetres) return true;
            if (ZNet.instance != null)
                foreach (var peer in ZNet.instance.GetPeers())
                    if (peer != null && Flat(peer.m_refPos, at) <= WatchMetres) return true;
            return false;
        }

        private static bool IsDebugOnly(string name) => name.IndexOf(DebugOnly, StringComparison.OrdinalIgnoreCase) >= 0;

        private static float Flat(Vector3 a, Vector3 b) => new Vector2(a.x - b.x, a.z - b.z).magnitude;

        // level -1 = not known at this point (SpawnSystem rolls it inside Spawn): the line gives the entry's range instead.
        private static void Log(string prefab, int level, Vector3 at, string from)
        {
            string stars = level > 1 ? " ★" + (level - 1).ToString(CultureInfo.InvariantCulture) : level == 1 ? " ★0" : "";
            string line = string.Format(CultureInfo.InvariantCulture, "[SpawnOrigin] {0}{1} at ({2:0}, {3:0}, y {4:0}) from {5}", prefab, stars, at.x, at.z, at.y, from);
            if (IsDebugOnly(prefab)) FiresLogger.LogDebug(line);
            else FiresLogger.LogInfo(line);
        }
    }
}
