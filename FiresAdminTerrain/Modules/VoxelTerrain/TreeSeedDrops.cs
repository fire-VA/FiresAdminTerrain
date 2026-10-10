using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // Seeds from felled trees. Which seed belongs to which tree is DERIVED, not listed: vanilla's sapling pieces
    // declare it themselves - a piece whose Plant.m_grownPrefabs names the tree is paid for with one item in
    // Piece.m_resources, and that item is the seed. So beech, birch, fir, oak and pine map themselves, and any
    // modded tree shipping a sapling maps itself too, with no name list here to fall out of date.
    //
    // The roll is vanilla's own DropTable - m_dropChance, m_dropMin, m_dropMax carry the chance and the 1..3 count -
    // so a felled tree drops seeds through the same path as its wood, and a drop-table editor sees an ordinary table.
    //
    // BOTH the standing tree and the felled log roll, which is two rolls per tree. Sub-logs are TreeLogs as well, so
    // walking every TreeLog in the scene would silently make it three; everything reachable through
    // TreeBase.m_logPrefab -> TreeLog.m_subLogPrefab is collected first and excluded.
    //
    // Trees with no sapling anywhere - the five Ashlands species and the Deep North snow firs - have no vanilla seed
    // to drop. They are counted and named in one line, and left for the invented-seed stage.
    internal static class TreeSeedDrops
    {
        private const string Section = "VoxelTerrain Tree Seeds";
        private const float DefaultChance = 0.3f;
        private const int DefaultMin = 1;
        private const int DefaultMax = 3;
        private const float SeedWeight = 1f;
        private const int MaxNamesLogged = 12;

        private static ConfigEntry<bool> s_enabled;
        private static ConfigEntry<float> s_chance;
        private static ConfigEntry<int> s_min, s_max;
        private static ConfigEntry<string> s_skip;

        private static readonly List<DropTable> s_touched = new List<DropTable>();
        private static readonly HashSet<GameObject> s_seeds = new HashSet<GameObject>();

        internal static void Bind(ConfigFile cfg)
        {
            s_enabled = cfg.Bind(Section, "Enabled", true,
                "Felled trees drop seeds. Each tree's seed is read from that tree's own sapling piece.");
            s_chance = cfg.Bind(Section, "Chance", DefaultChance,
                new ConfigDescription("Chance that a felled tree or log drops seeds at all.",
                    new AcceptableValueRange<float>(0f, 1f)));
            s_min = cfg.Bind(Section, "CountMin", DefaultMin,
                new ConfigDescription("Fewest seeds when the roll succeeds.", new AcceptableValueRange<int>(1, 20)));
            s_max = cfg.Bind(Section, "CountMax", DefaultMax,
                new ConfigDescription("Most seeds when the roll succeeds.", new AcceptableValueRange<int>(1, 20)));
            s_skip = cfg.Bind(Section, "Skip", "",
                "Prefab names that never drop seeds, comma separated. Names a modlist lacks are ignored.");

            s_enabled.SettingChanged += OnSettingChanged;
            s_chance.SettingChanged += OnSettingChanged;
            s_min.SettingChanged += OnSettingChanged;
            s_max.SettingChanged += OnSettingChanged;
            s_skip.SettingChanged += OnSettingChanged;
        }

        private static void OnSettingChanged(object sender, System.EventArgs args) => Apply();

        internal static void OnZNetSceneAwake() => Apply();

        private static void Apply()
        {
            ZNetScene scene = ZNetScene.instance;
            if (scene == null || scene.m_prefabs == null) return;

            Withdraw();
            if (s_enabled == null || !s_enabled.Value) return;

            Dictionary<string, GameObject> seedByTree = BuildSeedMap(scene);
            if (seedByTree.Count == 0) return;

            HashSet<string> skip = Skipped();
            HashSet<GameObject> subLogs = SubLogs(scene);
            int trees = 0, logs = 0;
            List<string> unseeded = new List<string>();

            foreach (GameObject prefab in scene.m_prefabs)
            {
                if (prefab == null) continue;
                TreeBase tree = prefab.GetComponent<TreeBase>();
                if (tree == null || skip.Contains(prefab.name)) continue;

                GameObject seed;
                if (!seedByTree.TryGetValue(prefab.name, out seed))
                {
                    unseeded.Add(prefab.name);
                    continue;
                }

                if (Inject(tree.m_dropWhenDestroyed, seed)) trees++;

                TreeLog log = tree.m_logPrefab != null ? tree.m_logPrefab.GetComponent<TreeLog>() : null;
                if (log != null && !skip.Contains(tree.m_logPrefab.name) && !subLogs.Contains(tree.m_logPrefab)
                    && Inject(log.m_dropWhenDestroyed, seed)) logs++;
            }

            FiresLogger.LogInfo($"[FiresWorldGen] tree seeds: {trees} trees and {logs} logs drop "
                + $"{s_min.Value}-{s_max.Value} at {s_chance.Value:P0}, mapped from {seedByTree.Count} sapling"
                + (seedByTree.Count == 1 ? "" : "s") + Unseeded(unseeded));
        }

        private static string Unseeded(List<string> names)
        {
            if (names.Count == 0) return ".";
            names.Sort();
            string listed = string.Join(", ", names.Take(MaxNamesLogged));
            string more = names.Count > MaxNamesLogged ? $" and {names.Count - MaxNamesLogged} more" : "";
            return $"; {names.Count} tree" + (names.Count == 1 ? "" : "s")
                + " have no sapling and so no seed: " + listed + more + ".";
        }

        private static Dictionary<string, GameObject> BuildSeedMap(ZNetScene scene)
        {
            Dictionary<string, GameObject> map = new Dictionary<string, GameObject>();
            foreach (GameObject prefab in scene.m_prefabs)
            {
                if (prefab == null) continue;
                Plant plant = prefab.GetComponent<Plant>();
                Piece piece = prefab.GetComponent<Piece>();
                if (plant == null || piece == null || plant.m_grownPrefabs == null) continue;

                GameObject seed = SeedOf(piece);
                if (seed == null) continue;

                foreach (GameObject grown in plant.m_grownPrefabs)
                {
                    if (grown == null || grown.GetComponent<TreeBase>() == null) continue;
                    map[grown.name] = seed;
                }
            }
            return map;
        }

        private static GameObject SeedOf(Piece piece)
        {
            if (piece.m_resources == null) return null;
            foreach (Piece.Requirement requirement in piece.m_resources)
            {
                if (requirement == null || requirement.m_resItem == null) continue;
                return requirement.m_resItem.gameObject;
            }
            return null;
        }

        private static HashSet<GameObject> SubLogs(ZNetScene scene)
        {
            HashSet<GameObject> subLogs = new HashSet<GameObject>();
            foreach (GameObject prefab in scene.m_prefabs)
            {
                if (prefab == null) continue;
                TreeBase tree = prefab.GetComponent<TreeBase>();
                TreeLog log = tree != null && tree.m_logPrefab != null
                    ? tree.m_logPrefab.GetComponent<TreeLog>() : null;
                while (log != null && log.m_subLogPrefab != null && subLogs.Add(log.m_subLogPrefab))
                    log = log.m_subLogPrefab.GetComponent<TreeLog>();
            }
            return subLogs;
        }

        private static HashSet<string> Skipped()
        {
            HashSet<string> skip = new HashSet<string>();
            if (s_skip == null || string.IsNullOrEmpty(s_skip.Value)) return skip;
            foreach (string name in s_skip.Value.Split(','))
            {
                string trimmed = name.Trim();
                if (trimmed.Length > 0) skip.Add(trimmed);
            }
            return skip;
        }

        private static bool Inject(DropTable table, GameObject seed)
        {
            if (table == null || table.m_drops == null) return false;

            table.m_drops.Add(new DropTable.DropData
            {
                m_item = seed,
                m_stackMin = 1,
                m_stackMax = 1,
                m_weight = SeedWeight,
            });
            table.m_dropChance = Mathf.Clamp01(s_chance.Value);
            table.m_dropMin = Mathf.Min(s_min.Value, s_max.Value);
            table.m_dropMax = Mathf.Max(s_min.Value, s_max.Value);

            s_touched.Add(table);
            s_seeds.Add(seed);
            return true;
        }

        private static void Withdraw()
        {
            foreach (DropTable table in s_touched)
            {
                if (table == null || table.m_drops == null) continue;
                table.m_drops.RemoveAll(drop => drop.m_item != null && s_seeds.Contains(drop.m_item));
            }
            s_touched.Clear();
            s_seeds.Clear();
        }
    }
}
