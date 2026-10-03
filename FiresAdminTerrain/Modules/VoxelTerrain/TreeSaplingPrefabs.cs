using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;
using VerdantsAscent.Terrain.Zones;

namespace VerdantsAscent.Terrain.Voxel
{
    // Saplings for the trees vanilla never made plantable: the Ashlands hardwoods and the Deep North snow firs.
    //
    // WHAT IS BAKED AND WHAT IS NOT. The three prefabs ship in the fireszones bundle as STRUCTURE only -
    // FiresCharredSeed (the Ashlands seed item, which has no vanilla equivalent) and two sapling pieces. Their
    // Plant.m_grownPrefabs and Piece.m_resources are left EMPTY in the bundle on purpose: an asset reference to
    // AshlandsTree3 or to FirConeFrost would drag vanilla assets into our bundle as dependencies and ship broken
    // copies of them, which is the same reason a vanilla material cannot be baked. Both are filled here from the
    // live ZNetScene and ObjectDB, so the sapling grows the game's own tree and costs the game's own item.
    //
    // THE DEEP NORTH NEEDS NO NEW ITEM. FirConeFrost already exists - vanilla's own FirTree_big_Sapling is bought
    // with it - so the snow firs are planted with that, and only the Ashlands trees get an invented seed. Their
    // trees all drop Blackwood, which no vanilla seed grows, and AshlandsTree1 carries no scripts at all, so it is
    // scenery rather than a tree and is not listed.
    //
    // Once registered, nothing here teaches TreeSeedDrops anything: these saplings declare m_grownPrefabs and
    // m_resources like any vanilla sapling, so the drop side derives the new seeds through the same path it uses
    // for beech and pine.
    internal static class TreeSaplingPrefabs
    {
        internal const string CharredSeedItem = "FiresCharredSeed";
        internal const string AshlandsSapling = "FiresAshlandsSapling";
        internal const string SnowFirSapling = "FiresSnowFirSapling";

        internal const string FrostConeItem = "FirConeFrost";

        private const string Section = "VoxelTerrain Tree Seeds";
        private const string DefaultAshlandsTrees = "AshlandsTree3, AshlandsTree4, AshlandsTree5, AshlandsTree6";
        // SnowFirTree_small carries no TreeBase in 1.0 (R45), so it was always skipped; listing it only logged a warning.
        private const string DefaultSnowFirTrees = "SnowFirTree, SnowFirTree 2";
        private const int SeedCost = 1;
        private const string CultivatorItem = "Cultivator";

        private static ConfigEntry<bool> s_enabled;
        private static ConfigEntry<string> s_ashlandsTrees, s_snowFirTrees;

        private static readonly HashSet<string> s_reportedMissing = new HashSet<string>();
        private static bool s_wired, s_ashlandsWired, s_snowFirWired;

        internal static void Bind(ConfigFile cfg)
        {
            s_enabled = cfg.Bind(Section, "Saplings Enabled", true,
                "Ashlands trees and Deep North snow firs become plantable, with a cultivator sapling each.");
            s_ashlandsTrees = cfg.Bind(Section, "Ashlands Trees", DefaultAshlandsTrees,
                "Prefabs the Ashlands sapling grows, comma separated. One is picked per planting.");
            s_snowFirTrees = cfg.Bind(Section, "Snow Fir Trees", DefaultSnowFirTrees,
                "Prefabs the snow fir sapling grows, comma separated. Bought with FirConeFrost.");
        }

        // The pieces are wired once a world is loaded, never swapped afterwards: a Plant whose m_grownPrefabs changed
        // under a placement ghost is the mid-session prefab swap that leaves dangling references.
        internal static void OnZNetSceneAwake()
        {
            if (s_wired || s_enabled == null || !s_enabled.Value) return;
            ZNetScene scene = ZNetScene.instance;
            if (scene == null) return;

            s_ashlandsWired = Wire(scene, AshlandsSapling, Names(s_ashlandsTrees), CharredSeedItem);
            s_snowFirWired = Wire(scene, SnowFirSapling, Names(s_snowFirTrees), FrostConeItem);
            s_wired = s_ashlandsWired || s_snowFirWired;
            if (s_wired) ListOnCultivator();
        }

        // ZNetScene and ObjectDB wake in either order, so whichever comes second lists the saplings. Listed only from
        // ZNetScene, a world whose ObjectDB woke later never had them on the cultivator (SanctuaryAshlands, 2026-09-27).
        internal static void OnObjectDBAwake()
        {
            if (s_wired) ListOnCultivator();
        }

        private static void ListOnCultivator()
        {
            PieceTable table = CultivatorTable();
            if (table == null)
            {
                if (ObjectDB.instance != null && ObjectDB.instance.m_items.Count > 0)
                    Missing("the cultivator has no build table, so the saplings are not listed on it.");
                return;
            }
            if (s_ashlandsWired) AddSapling(table, AshlandsSapling);
            if (s_snowFirWired) AddSapling(table, SnowFirSapling);
        }

        // A tool's pieces live on the tool's own item, so the cultivator's table comes from the cultivator rather than
        // from a table asset - the same route Hammer pieces take through Core, which is why AddToTable takes the table.
        private static PieceTable CultivatorTable()
        {
            GameObject cultivator = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(CultivatorItem) : null;
            ItemDrop item = cultivator != null ? cultivator.GetComponent<ItemDrop>() : null;
            return item != null ? item.m_itemData.m_shared.m_buildPieces : null;
        }

        private static void AddSapling(PieceTable table, string saplingName)
        {
            if (!ZoneModePrefabs.TryGet(saplingName, out GameObject sapling) || sapling == null) return;
            if (FiresCore.Pieces.HammerPieces.AddToTable(table, sapling))
                FiresLogger.LogInfo($"[FiresZones] {saplingName}: listed on the cultivator.");
        }

        private static bool Wire(ZNetScene scene, string saplingName, IList<string> treeNames, string seedName)
        {
            if (!ZoneModePrefabs.TryGet(saplingName, out GameObject sapling) || sapling == null)
            {
                Missing($"{saplingName}: the fireszones bundle did not supply it, so those trees stay unplantable.");
                return false;
            }

            Plant plant = sapling.GetComponent<Plant>();
            Piece piece = sapling.GetComponent<Piece>();
            if (plant == null || piece == null)
            {
                Missing($"{saplingName}: baked without a {(plant == null ? "Plant" : "Piece")} component.");
                return false;
            }

            List<GameObject> grown = new List<GameObject>();
            foreach (string name in treeNames)
            {
                GameObject tree = scene.GetPrefab(name);
                if (tree == null) { Missing($"{saplingName}: no prefab named '{name}'."); continue; }
                if (tree.GetComponent<TreeBase>() == null)
                {
                    Missing($"{saplingName}: '{name}' is not a tree, it carries no TreeBase.");
                    continue;
                }
                grown.Add(tree);
            }
            if (grown.Count == 0) return false;

            GameObject seed = scene.GetPrefab(seedName);
            ItemDrop seedItem = seed != null ? seed.GetComponent<ItemDrop>() : null;
            if (seedItem == null)
            {
                Missing($"{saplingName}: no seed item named '{seedName}', so it could not be priced.");
                return false;
            }

            plant.m_grownPrefabs = grown.ToArray();
            piece.m_resources = new[]
            {
                new Piece.Requirement { m_resItem = seedItem, m_amount = SeedCost, m_recover = true },
            };

            FiresLogger.LogInfo($"[FiresZones] {saplingName}: grows {grown.Count} tree"
                + (grown.Count == 1 ? "" : "s") + $" for {SeedCost} {seedName}.");
            return true;
        }

        private static IList<string> Names(ConfigEntry<string> entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.Value)) return new string[0];
            return entry.Value.Split(',').Select(name => name.Trim()).Where(name => name.Length > 0).ToList();
        }

        private static void Missing(string message)
        {
            if (s_reportedMissing.Add(message)) FiresLogger.LogWarning("[FiresZones] " + message);
        }
    }
}
