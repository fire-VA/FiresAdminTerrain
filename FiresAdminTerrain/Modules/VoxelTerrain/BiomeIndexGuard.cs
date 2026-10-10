using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace VerdantsAscent.Terrain.Voxel
{
    // ── 1.0 custom-biome crash guard ─────────────────────────────────────
    // BiomeHelpers.ToBiomeIndex is a switch over the TEN vanilla Heightmap.Biome
    // flags ending in `default: throw new NotImplementedException()`. A custom
    // biome takes its own bit, so it hits the default — and AltBiomeWorldData
    // .GenerateBiomePoints calls it on every sample of a 2048² grid, reached from
    // ZNet.ServerLoadWorld -> VerifyBiomeData whenever the save's world version
    // differs from the current one. The throw unwinds out of ZNet.Start and the
    // server never listens, which reads as a network fault.
    // Diagnosis and the three-day repro: MODSTACK_COORDINATION.md under [c68b48].
    // Sibling of AltBiomePointGuard, which guards the same 1.0 location system.
    //
    // Expand World Data 1.73+ prefixes this method itself and gives every bit its
    // own BiomeIndex. HarmonyX runs every prefix, so this one must never write over
    // that answer: when an earlier prefix has already decided (__runOriginal is
    // false) it does nothing. It only maps when the vanilla switch would otherwise
    // throw with nobody else answering.
    [HarmonyPatch]
    internal static class BiomeIndexGuard
    {
        private static readonly Dictionary<int, Heightmap.BiomeIndex> s_resolved = new Dictionary<int, Heightmap.BiomeIndex>();
        private static readonly object s_lock = new object();
        private static bool s_probed;
        private static MethodInfo s_toTerrainMethod;
        private static IDictionary s_toTerrainDict;

        internal static void Reset()
        {
            lock (s_lock) { s_resolved.Clear(); s_probed = false; s_toTerrainMethod = null; s_toTerrainDict = null; }
        }

        [HarmonyPatch(typeof(BiomeHelpers), nameof(BiomeHelpers.ToBiomeIndex))]
        [HarmonyPrefix]
        private static bool Prefix(Heightmap.Biome b, ref Heightmap.BiomeIndex __result, bool __runOriginal)
        {
            if (!__runOriginal) return true;

            switch (b)
            {
                case Heightmap.Biome.None:
                case Heightmap.Biome.Meadows:
                case Heightmap.Biome.Swamp:
                case Heightmap.Biome.Mountain:
                case Heightmap.Biome.BlackForest:
                case Heightmap.Biome.Plains:
                case Heightmap.Biome.AshLands:
                case Heightmap.Biome.DeepNorth:
                case Heightmap.Biome.Ocean:
                case Heightmap.Biome.Mistlands:
                    return true;
            }

            int key = (int)b;
            lock (s_lock)
            {
                if (!s_resolved.TryGetValue(key, out Heightmap.BiomeIndex cached))
                {
                    cached = Resolve(b);
                    s_resolved[key] = cached;
                }
                __result = cached;
            }
            return false;
        }

        /// <summary>Caller holds s_lock. Runs once per distinct unrecognised value, then it is cached.</summary>
        private static Heightmap.BiomeIndex Resolve(Heightmap.Biome b)
        {
            Heightmap.Biome baseBiome = AskExpandWorld(b);
            string how = "ExpandWorldData terrain base";

            if (!IsSingleVanilla(baseBiome))
            {
                baseBiome = CarriedVanillaBit(b);
                how = "vanilla bit carried in the value";
            }

            Heightmap.BiomeIndex index = ToIndex(baseBiome);
            if (index == Heightmap.BiomeIndex.None)
            {
                FiresLogger.LogWarning($"[FiresBiomeIndexGuard] biome value {(int)b} is not one of vanilla's ten and nothing "
                    + "could tell us what it is built on, so it maps to None and contributes no alt-biome points. Without "
                    + "this guard BiomeHelpers.ToBiomeIndex would throw NotImplementedException and take ZNet.Start with "
                    + "it. If this is an ExpandWorldData custom biome, give it a `terrain:` in its yaml.");
            }
            else
            {
                FiresLogger.LogInfo($"[FiresBiomeIndexGuard] biome value {(int)b} is not one of vanilla's ten and no other mod "
                    + $"mapped it; mapped to {baseBiome} via {how}. Vanilla would have thrown NotImplementedException here.");
            }
            return index;
        }

        /// <summary>EWD keeps a custom biome's vanilla base in BiomeManager.BiomeToTerrain - the yaml's
        /// `terrain:`. Tolerated as either a method or a dictionary, and absent when EWD is not installed.</summary>
        private static Heightmap.Biome AskExpandWorld(Heightmap.Biome b)
        {
            if (!s_probed)
            {
                s_probed = true;
                try
                {
                    Type t = AccessTools.TypeByName("ExpandWorldData.BiomeManager");
                    if (t != null)
                    {
                        s_toTerrainMethod = AccessTools.Method(t, "BiomeToTerrain", new[] { typeof(Heightmap.Biome) });
                        if (s_toTerrainMethod == null)
                        {
                            object raw = AccessTools.Field(t, "BiomeToTerrain")?.GetValue(null)
                                      ?? AccessTools.Property(t, "BiomeToTerrain")?.GetValue(null);
                            s_toTerrainDict = raw as IDictionary;
                        }
                    }
                }
                catch (Exception ex)
                {
                    FiresLogger.LogWarning($"[FiresBiomeIndexGuard] could not read ExpandWorldData's biome table: {ex.Message}");
                }
            }

            try
            {
                if (s_toTerrainMethod != null)
                    return (Heightmap.Biome)s_toTerrainMethod.Invoke(null, new object[] { b });
                if (s_toTerrainDict != null && s_toTerrainDict.Contains(b))
                    return (Heightmap.Biome)s_toTerrainDict[b];
            }
            catch { }
            return Heightmap.Biome.None;
        }

        private static Heightmap.Biome CarriedVanillaBit(Heightmap.Biome b)
        {
            Heightmap.Biome carried = b & Heightmap.Biome.All;
            for (int bit = 1; bit <= (int)Heightmap.Biome.Mistlands; bit <<= 1)
            {
                if ((carried & (Heightmap.Biome)bit) != 0) return (Heightmap.Biome)bit;
            }
            return Heightmap.Biome.None;
        }

        private static bool IsSingleVanilla(Heightmap.Biome b) =>
            b != Heightmap.Biome.None && ToIndex(b) != Heightmap.BiomeIndex.None;

        /// <summary>Mapped inline rather than by calling ToBiomeIndex: that would re-enter this very patch,
        /// and GenerateBiomePoints runs it over 4.2M grid samples.</summary>
        private static Heightmap.BiomeIndex ToIndex(Heightmap.Biome b)
        {
            switch (b)
            {
                case Heightmap.Biome.Meadows: return Heightmap.BiomeIndex.Meadows;
                case Heightmap.Biome.Swamp: return Heightmap.BiomeIndex.Swamp;
                case Heightmap.Biome.Mountain: return Heightmap.BiomeIndex.Mountain;
                case Heightmap.Biome.BlackForest: return Heightmap.BiomeIndex.BlackForest;
                case Heightmap.Biome.Plains: return Heightmap.BiomeIndex.Plains;
                case Heightmap.Biome.AshLands: return Heightmap.BiomeIndex.AshLands;
                case Heightmap.Biome.DeepNorth: return Heightmap.BiomeIndex.DeepNorth;
                case Heightmap.Biome.Ocean: return Heightmap.BiomeIndex.Ocean;
                case Heightmap.Biome.Mistlands: return Heightmap.BiomeIndex.Mistlands;
                default: return Heightmap.BiomeIndex.None;
            }
        }
    }
}
