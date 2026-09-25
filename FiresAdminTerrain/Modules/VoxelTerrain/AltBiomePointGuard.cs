using System.Collections.Generic;
using HarmonyLib;

namespace VerdantsAscent.Terrain.Voxel
{
    // ── 1.0 biome-point crash guard ──────────────────────────────────────
    // Valheim 1.0 places locations through AltBiomeWorldData, which precomputes
    // a point list per biome by sampling WorldGenerator.GetBiome over a 2048²
    // grid. The lookup is:
    //
    //     Biomes[biome].AllPoints[Random.Range(0, Biomes[biome].AllPoints.Count)]
    //
    // with NO empty check. Random.Range(0, 0) returns 0, so an empty list
    // indexes [0] and throws ArgumentOutOfRangeException — inside the
    // GenerateLocationsTimeSliced coroutine, which kills it outright. One
    // missing biome therefore costs you EVERY remaining location in the world.
    //
    // A world only has to lack ONE biome for this to fire. Ours did on
    // 2026-09-19 (Meadows 0.0%, Plains 0.0%): StartTemple 0/1, Eikthyrnir 0/3,
    // and the world's biome cache was never written because VerifyBiomeData
    // never reached SaveCache. The generator bug behind that is fixed
    // separately — this is the seatbelt, because the vanilla defect bites any
    // world whose biome coverage is incomplete, ours or anyone else's.
    //
    // The guard substitutes the nearest populated biome rather than throwing,
    // so placement degrades to "this location spawned somewhere sensible"
    // instead of "no locations exist at all", and says so once per biome.
    //
    // Expand World Data and Expand World Size prefix GetRandomPointByBiome too
    // (missing/empty -> warn, no point). HarmonyX runs every prefix, so each guard
    // below does nothing once an earlier prefix has answered (__runOriginal false).
    [HarmonyPatch]
    internal static class AltBiomePointGuard
    {
        private static readonly HashSet<Heightmap.Biome> s_warned = new HashSet<Heightmap.Biome>();
        private static bool s_seededLogged;

        internal static void Reset() { lock (s_warned) s_warned.Clear(); }

        // The constructor seeds Biomes from Enum.GetValues — vanilla's members only.
        // Expand World Data 1.73+ gives every custom biome its own BiomeIndex, and
        // GenerateSectors -> new BiomeSector(this, index.ToBiome()) then reads
        // Biomes[customBiome] with no guard: KeyNotFoundException inside ZNet.Start,
        // the same silent dead server as the ToBiomeIndex throw. One empty
        // BiomeTypeInfo per single-bit value makes the table complete for anything
        // ToBiome can return; the cache file stores points only, so the extra keys
        // never reach disk.
        [HarmonyPatch(typeof(AltBiomeWorldData), MethodType.Constructor, new[] { typeof(int) })]
        [HarmonyPostfix]
        private static void SeedAllBiomeKeys(AltBiomeWorldData __instance)
        {
            if (__instance == null || __instance.Biomes == null) return;
            int added = 0;
            for (int bit = 0; bit < 32; bit++)
            {
                var biome = (Heightmap.Biome)(1u << bit);
                if (__instance.Biomes.ContainsKey(biome)) continue;
                __instance.Biomes[biome] = new BiomeTypeInfo(biome);
                added++;
            }
            if (added > 0 && !s_seededLogged)
            {
                s_seededLogged = true;
                FiresLogger.LogInfo($"[FiresBiomeGuard] seeded {added} non-vanilla biome key(s) into the alt-biome table so a custom "
                    + "biome (Expand World Data) cannot throw KeyNotFoundException in sector generation or location placement.");
            }
        }

        // NOT sufficient on its own: GetRandomPointByBiomeAboveSeaLevel and
        // GetRandomSectorByBiome index this.Biomes[biome] DIRECTLY, so a biome
        // the map omits throws KeyNotFoundException in them before this ever
        // runs. Each has its own prefix below.
        [HarmonyPatch(typeof(AltBiomeWorldData), "GetRandomPointByBiome")]
        [HarmonyPrefix]
        private static bool Prefix(AltBiomeWorldData __instance, Heightmap.Biome biome,
                                   ref BiomePointCoordinate __result, bool __runOriginal)
        {
            if (!__runOriginal) return true;
            if (__instance == null || __instance.Biomes == null) return true;
            if (__instance.Biomes.TryGetValue(biome, out var info)
                && info != null && info.AllPoints != null && info.AllPoints.Count > 0)
                return true;   // populated — vanilla runs untouched

            // Fall back to the biome with the most points, so the location still
            // lands on real ground instead of aborting the whole pass.
            BiomeTypeInfo best = null;
            Heightmap.Biome bestBiome = Heightmap.Biome.None;
            foreach (var kv in __instance.Biomes)
            {
                var v = kv.Value;
                if (v == null || v.AllPoints == null || v.AllPoints.Count == 0) continue;
                if (best == null || v.AllPoints.Count > best.AllPoints.Count) { best = v; bestBiome = kv.Key; }
            }

            bool first;
            lock (s_warned) first = s_warned.Add(biome);
            if (first)
            {
                if (best == null)
                    FiresLogger.LogError($"[FiresBiomeGuard] biome {biome} has NO points and neither does any other biome — " +
                        "the world's biome data is empty. Locations cannot place. Vanilla would have thrown here and " +
                        "killed location generation entirely.");
                else
                    FiresLogger.LogWarning($"[FiresBiomeGuard] biome {biome} has NO points in this world — vanilla indexes an " +
                        $"empty list here and throws, taking ALL remaining location placement with it. Substituting " +
                        $"{bestBiome} ({best.AllPoints.Count} point(s)). If this is a Fires-generated world, its biome " +
                        "coverage is wrong — check the WORLD REPORT percentages.");
            }

            if (best == null) { __result = default; return false; }
            __result = best.AllPoints[UnityEngine.Random.Range(0, best.AllPoints.Count)];
            return false;
        }

        // GetRandomPointByBiomeAboveSeaLevel reads this.Biomes[biome] to test its
        // AllPointsAboveSeaLevel count, so a MISSING key throws there before its own
        // fallback to GetRandomPointByBiome can run. Only the missing key needs
        // handling: present-but-empty already falls back on its own.
        [HarmonyPatch(typeof(AltBiomeWorldData), "GetRandomPointByBiomeAboveSeaLevel")]
        [HarmonyPrefix]
        private static bool PrefixAboveSeaLevel(AltBiomeWorldData __instance, Heightmap.Biome biome,
                                                ref BiomePointCoordinate __result, bool __runOriginal)
        {
            if (!__runOriginal) return true;
            if (__instance == null || __instance.Biomes == null) return true;
            if (__instance.Biomes.ContainsKey(biome)) return true;

            BiomeTypeInfo best = BestPopulated(__instance, out Heightmap.Biome bestBiome);
            WarnOnce(biome, bestBiome, best == null ? 0 : best.AllPoints.Count, "above-sea-level point");
            if (best == null) { __result = default; return false; }

            List<BiomePointCoordinate> from = best.AllPointsAboveSeaLevel != null && best.AllPointsAboveSeaLevel.Count > 0
                ? best.AllPointsAboveSeaLevel
                : best.AllPoints;
            __result = from[UnityEngine.Random.Range(0, from.Count)];
            return false;
        }

        // GetRandomSectorByBiome is unguarded on BOTH counts: this.Biomes[biome] throws
        // on a missing key, and Sectors[Random.Range(0, 0)] indexes an empty list. Its
        // caller GetRandomSectorByBiomes feeds location placement the same way the point
        // readers do.
        [HarmonyPatch(typeof(AltBiomeWorldData), "GetRandomSectorByBiome")]
        [HarmonyPrefix]
        private static bool PrefixSector(AltBiomeWorldData __instance, Heightmap.Biome biome,
                                         ref BiomeSector __result, bool __runOriginal)
        {
            if (!__runOriginal) return true;
            if (__instance == null || __instance.Biomes == null) return true;
            if (__instance.Biomes.TryGetValue(biome, out BiomeTypeInfo info)
                && info != null && info.Sectors != null && info.Sectors.Count > 0)
                return true;

            BiomeTypeInfo best = null;
            Heightmap.Biome bestBiome = Heightmap.Biome.None;
            foreach (KeyValuePair<Heightmap.Biome, BiomeTypeInfo> kv in __instance.Biomes)
            {
                BiomeTypeInfo v = kv.Value;
                if (v == null || v.Sectors == null || v.Sectors.Count == 0) continue;
                if (best == null || v.Sectors.Count > best.Sectors.Count) { best = v; bestBiome = kv.Key; }
            }

            WarnOnce(biome, bestBiome, best == null ? 0 : best.Sectors.Count, "sector");
            if (best == null) { __result = null; return false; }
            __result = best.Sectors[UnityEngine.Random.Range(0, best.Sectors.Count)];
            return false;
        }

        private static BiomeTypeInfo BestPopulated(AltBiomeWorldData instance, out Heightmap.Biome bestBiome)
        {
            BiomeTypeInfo best = null;
            bestBiome = Heightmap.Biome.None;
            foreach (KeyValuePair<Heightmap.Biome, BiomeTypeInfo> kv in instance.Biomes)
            {
                BiomeTypeInfo v = kv.Value;
                if (v == null || v.AllPoints == null || v.AllPoints.Count == 0) continue;
                if (best == null || v.AllPoints.Count > best.AllPoints.Count) { best = v; bestBiome = kv.Key; }
            }
            return best;
        }

        private static void WarnOnce(Heightmap.Biome missing, Heightmap.Biome substitute, int count, string what)
        {
            bool first;
            lock (s_warned) first = s_warned.Add(missing);
            if (!first) return;

            if (count == 0)
                FiresLogger.LogError($"[FiresBiomeGuard] biome {missing} has no {what} data and neither does any other "
                    + "biome — the world's biome data is empty, so locations cannot place. Vanilla would have thrown "
                    + "here and killed location generation entirely.");
            else
                FiresLogger.LogWarning($"[FiresBiomeGuard] biome {missing} is ABSENT from this world's biome table, so "
                    + $"vanilla's {what} lookup would throw KeyNotFoundException and take all remaining location "
                    + $"placement with it. Substituting {substitute} ({count}). If this is a Fires-generated or "
                    + "migrated world, its biome coverage is wrong — check the WORLD REPORT percentages.");
        }
    }
}
