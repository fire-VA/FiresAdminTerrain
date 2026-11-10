using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using VerdantsAscent.Modules.Utilities;

namespace VerdantsAscent.Terrain.Voxel
{
    // ORE IN CAVES (FAT 0.2.1133, Fire 10-27): "MineRock copper and MineRock iron... and any of the other mineable resources like that
    // should be able to spawn within cave systems here and there... So some caves are mineral rich and become 'mines'".
    //  * A 64 m cave zone is MINERAL RICH when hash(world seed, zone, salt) < "Mineral-rich cave chance" (Fire: 10 %): the same on
    //    every peer and every run. Whole-cave mines (connected caves) come later.
    //  * A rich zone rolls its ore at OreRichFactor x the normal chance, up to clamp(round(floor spots / 8), 6, 10) nodes; normal zones
    //    are unchanged ('Ore node chance' / 'Ore nodes per zone').
    //  * Existing worlds: VoxelCaveContent v11 ONLY ADDS ore to an already-dressed zone that rolls rich (never re-dresses).
    //  * Regrow lives in Core (FiresCore.Loot.CaveOreRegrow, "[CaveOre] regrow"): FAT sets Core's RichCaveAt hook at startup by
    //    reflection, so neither mod needs the other to load. Cave ore ZDOs carry "fires_cave" = 4 (CaveContentKind.Ore).
    //
    // EXTENSION API (contracts are add-only): another mod (e.g. FiresLootOverhaul's gem rocks) adds its own mine rocks to the cave
    // ore pool, without referencing FAT:
    //    VerdantsAscent.FatCaveOreApi.Register(string prefab, Heightmap.Biome biomes, float weight, bool richOnly, string mod = null)
    //    VerdantsAscent.FatCaveOreApi.RegisterByName(string prefab, int biomeMask, int weight, bool richOnly)   // primitives, for reflection
    //  Entries merge into each biome's 'Ore' list when a zone is dressed; an unknown prefab is skipped with one warning; a re-register of
    //  the same prefab replaces its entry. richOnly = only in mineral-rich zones.
    public static class FatCaveOre
    {
        internal const float OreRichFactor = 6f;
        internal const int RichCapMin = 6, RichCapMax = 10, RichSpotsPerOre = 8;
        private const int RichSalt = 0x0C0FFE;
        internal const int OreSalt = 0x0BE5;
        internal const float BuildClearance = 4f;

        // ── the rich roll ─────────────────────────────────────────────────────────────────────────────────────────────────
        public static bool IsRichZone(int zoneX, int zoneZ)
        {
            float chance = CaveContentConfig.RichCaveChance;
            if (chance <= 0f) return false;
            int seed = WorldGenerator.instance != null ? WorldGenerator.instance.GetSeed() : 0;
            uint h = unchecked((uint)(zoneX * 73856093 ^ zoneZ * 19349663 ^ seed ^ RichSalt));
            h ^= h >> 16; h = unchecked(h * 0x7feb352d); h ^= h >> 15; h = unchecked(h * 0x846ca68b); h ^= h >> 16;
            return (h & 0xFFFFFF) / (float)0x1000000 < chance;
        }

        internal static bool IsRichZone(Vector2s zone) => IsRichZone(zone.x, zone.y);

        public static bool IsRichAt(Vector3 p) { var z = ZoneSystem.GetZone(p); return IsRichZone(z.x, z.y); }

        // Core's regrow hook (server + owner): a point in a rich zone, under rock cover (a cave, not the surface).
        public static bool RichCaveAt(Vector3 p) => IsRichAt(p) && VoxelCaveContent.IsInsideRock(p + Vector3.up * 0.5f, 2f);

        internal static int RichCap(int spots) => Mathf.Clamp(Mathf.RoundToInt(spots / (float)RichSpotsPerOre), RichCapMin, RichCapMax);

        // ── the deposits this session placed or found (for the test and Core) ─────────────────────────────────────────────
        public static readonly Dictionary<Vector2s, List<KeyValuePair<string, Vector3>>> RichDeposits = new Dictionary<Vector2s, List<KeyValuePair<string, Vector3>>>();

        internal static void Remember(Vector2s zone, string prefab, Vector3 at)
        {
            if (!RichDeposits.TryGetValue(zone, out var list)) RichDeposits[zone] = list = new List<KeyValuePair<string, Vector3>>();
            list.Add(new KeyValuePair<string, Vector3>(prefab, at));
        }

        internal static void LogRichZone(Vector2s zone, Heightmap.Biome biome, Dictionary<string, int> counts, int total, string how)
        {
            var parts = new List<string>();
            foreach (var kv in counts) parts.Add($"{kv.Key}:{kv.Value}");
            FiresLogger.LogDebug($"[CaveOre] rich zone ({zone.x},{zone.y}) biome {biome}: {total} deposits ({string.Join(", ", parts)}){how}");
        }

        // ── the pick: the biome's 'Ore' list + registered entries ─────────────────────────────────────────────────────────
        private static readonly List<KeyValuePair<GameObject, float>> s_pool = new List<KeyValuePair<GameObject, float>>();

        internal static bool TryPick(Heightmap.Biome biome, bool rich, System.Random rng, out GameObject prefab)
        {
            prefab = null;
            s_pool.Clear();
            var table = CaveContentConfig.For(biome);
            if (table != null) s_pool.AddRange(table.Ores.Entries());
            foreach (var e in s_registry.Values)
            {
                if ((e.Biomes & biome) == 0 || (e.RichOnly && !rich) || e.Weight <= 0f) continue;
                var go = e.Resolve();
                if (go != null) s_pool.Add(new KeyValuePair<GameObject, float>(go, e.Weight));
            }
            float total = 0f;
            foreach (var kv in s_pool) total += kv.Value;
            if (total <= 0f) return false;
            float roll = (float)rng.NextDouble() * total;
            foreach (var kv in s_pool)
            {
                roll -= kv.Value;
                if (roll > 0f) continue;
                prefab = kv.Key;
                return true;
            }
            prefab = s_pool[s_pool.Count - 1].Key;
            return true;
        }

        // ── the registry ──────────────────────────────────────────────────────────────────────────────────────────────────
        private sealed class Entry
        {
            public string Prefab, Mod;
            public Heightmap.Biome Biomes;
            public float Weight;
            public bool RichOnly, Warned;
            public GameObject Resolve()
            {
                var go = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(Prefab) : null;
                if (go != null && go.GetComponent<ZNetView>() != null) return go;
                if (!Warned && ZNetScene.instance != null)
                {
                    Warned = true;
                    FiresLogger.LogWarning($"[CaveOre] registry: '{Prefab}' ({Mod ?? "?"}) is not a known networked prefab; skipped");
                }
                return null;
            }
        }

        private static readonly Dictionary<string, Entry> s_registry = new Dictionary<string, Entry>(StringComparer.Ordinal);

        internal static void Register(string prefab, Heightmap.Biome biomes, float weight, bool richOnly, string mod)
        {
            if (string.IsNullOrEmpty(prefab)) return;
            s_registry[prefab] = new Entry { Prefab = prefab, Biomes = biomes, Weight = weight, RichOnly = richOnly, Mod = mod };
            FiresLogger.LogDebug($"[CaveOre] registry: {mod ?? "?"} added {prefab} biomes {biomes} weight {weight:0.##} richOnly {(richOnly ? "y" : "n")}");
        }

        // ── Core's regrow hook, set by reflection (no compile dependency either way) ───────────────────────────────────────
        private static bool s_hooked;

        internal static void TryHookCore()
        {
            if (s_hooked) return;
            try
            {
                var t = Type.GetType("FiresCore.Loot.CaveOreRegrow, FiresUnifiedCore");
                var f = t?.GetField("RichCaveAt", BindingFlags.Public | BindingFlags.Static);
                if (f == null) return;
                f.SetValue(null, new Func<Vector3, bool>(RichCaveAt));
                s_hooked = true;
                FiresLogger.LogInfo("[CaveOre] Core regrow hook set (FiresCore.Loot.CaveOreRegrow.RichCaveAt)");
            }
            catch (Exception ex) { FiresLogger.LogDebug($"[CaveOre] Core regrow hook not set: {ex.Message}"); }
        }

        // Every peer sets the hook (the regrow trigger runs on the deposit's owner, which may be a client).
        [HarmonyLib.HarmonyPatch(typeof(ZNet), "Start")]
        private static class ZNet_Start_CaveOreHook
        {
            private static void Postfix() => TryHookCore();
        }

        // ── the per-world tally (once per world, at the first cave pass) ───────────────────────────────────────────────────
        private static string s_talliedWorld;

        internal static void TallyOnce()
        {
            TryHookCore();
            string world = ZNet.World?.m_name;
            if (world == null || world == s_talliedWorld) return;
            s_talliedWorld = world;
            RichDeposits.Clear();
            int dressed = 0, rich = 0;
            foreach (var z in VoxelCaveRecord.DressedZones()) { dressed++; if (IsRichZone(z)) rich++; }
            FiresLogger.LogInfo($"[CaveOre] world '{world}': {rich} of {dressed} dressed cave zone(s) are mineral-rich (chance {CaveContentConfig.RichCaveChance:0.##}; "
                + $"rich: ore x{OreRichFactor:0}, {RichCapMin}-{RichCapMax} nodes; {s_registry.Count} registered extra ore prefab(s))");
        }
    }
}

namespace VerdantsAscent
{
    /// <summary>FAT's cave-ore registry for other mods (add-only contract). See FatCaveOre's header.</summary>
    public static class FatCaveOreApi
    {
        public static void Register(string prefab, Heightmap.Biome biomes, float weight, bool richOnly, string mod = null)
            => Terrain.Voxel.FatCaveOre.Register(prefab, biomes, weight, richOnly, mod ?? Assembly.GetCallingAssembly().GetName().Name);

        /// <summary>Primitive-argument form for reflection callers: biomeMask = (int)Heightmap.Biome flags.</summary>
        public static void RegisterByName(string prefab, int biomeMask, int weight, bool richOnly)
            => Terrain.Voxel.FatCaveOre.Register(prefab, (Heightmap.Biome)biomeMask, weight, richOnly, Assembly.GetCallingAssembly().GetName().Name);

        public static bool IsRichZone(int zoneX, int zoneZ) => Terrain.Voxel.FatCaveOre.IsRichZone(zoneX, zoneZ);
    }
}
