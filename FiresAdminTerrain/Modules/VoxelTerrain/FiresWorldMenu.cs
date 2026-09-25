using System.Collections.Generic;
using System.IO;
using FiresCore.UI;
using FiresCore.UI.ContextMenu;
using VerdantsAscent.Terrain.Zones;

namespace VerdantsAscent.Terrain.Voxel
{
    // Select World's right-click rows: clone a world as one zone at a time or onto other terrain. Each row opens one Core
    // MenuDialog preset for it (name, terrain, one zone at a time, sky islands, rivers and lakes, caves); FiresWorldClone
    // makes the copy and the original is never touched. "As the original" keeps what the world has, so a split is faithful.
    // The copy is made from the row's save (its file name); the dialog and FAT's records use the world's own name, which
    // a renamed world no longer shares with its file. Full voxel is the Fires generator's terrain, so a world stock
    // Valheim made offers it greyed, with voxel wrap named instead.
    internal static class FiresWorldMenu
    {
        private const string Owner = "FiresAdminTerrain";
        private const int MaxNameAttempts = 99;
        private const int KeepOriginal = 0;
        private const int SwitchOn = 1;
        private const int SwitchOff = 2;
        private const int HeightmapChoice = 1;
        private const int VoxelWrapChoice = 2;
        private const int FullVoxelChoice = 3;

        private static readonly string[] SwitchChoices = { "As the original", "On", "Off" };
        private static readonly ProfileTerrain?[] TerrainByChoice = { null, ProfileTerrain.Heightmap, ProfileTerrain.VoxelWrap, ProfileTerrain.VoxelOnly };

        internal static void Register() => WorldListContextMenu.Register(Owner, RowsFor);

        private static IEnumerable<ContextMenuItem> RowsFor(World world)
        {
            if (world == null) yield break;
            string refusal = world.m_dataError != World.SaveDataError.None ? "This world could not be read."
                : File.Exists(ZoneModeRegistry.RegistryPath(world.m_name)) ? "Already played one zone at a time." : null;
            string fullVoxelRefusal = refusal ?? (FiresWorldClone.OriginalProfile(world.m_name).Generator == ProfileGenerator.Vanilla
                ? "Full voxel terrain comes from the Fires generator. Stock Valheim made this world: clone it as voxel wrap."
                : null);
            yield return Row("Clone as one zone at a time...", refusal, () => ShowCloneDialog(world, "-Chunks", KeepOriginal, true));
            yield return Row("Clone as full voxel...", fullVoxelRefusal, () => ShowCloneDialog(world, "-Voxel", FullVoxelChoice, false));
            yield return Row("Clone as voxel wrap...", refusal, () => ShowCloneDialog(world, "-VoxelWrap", VoxelWrapChoice, false));
            yield return Row("Clone as heightmap...", refusal, () => ShowCloneDialog(world, "-Heightmap", HeightmapChoice, false));
        }

        private static ContextMenuItem Row(string label, string refusal, System.Action onPick)
        {
            var row = ContextMenuItem.Row(label, onPick, refusal == null);
            row.Tooltip = refusal;
            return row;
        }

        private static void ShowCloneDialog(World world, string suffix, int terrainChoice, bool oneZoneAtATime)
        {
            string source = world.m_name;
            string saveName = world.m_worldName;
            var original = FiresWorldClone.OriginalProfile(source);
            var name = new MenuDialog.TextField("New world name", FreeName(source + suffix));
            var terrain = new MenuDialog.ChoiceField("Terrain",
                new[] { $"As the original ({TerrainTitle(original.Terrain)})", "Heightmap", "Voxel wrap", "Full voxel" }, terrainChoice);
            var zones = new MenuDialog.CheckField("One zone at a time", oneZoneAtATime);
            var sky = new MenuDialog.ChoiceField("Sky islands", SwitchChoices, KeepOriginal);
            var rivers = new MenuDialog.ChoiceField("Rivers and lakes", SwitchChoices, KeepOriginal);
            var caves = new MenuDialog.ChoiceField("Caves", SwitchChoices, KeepOriginal);

            WorldCloneOptions Options() => new WorldCloneOptions
            {
                ChunkByChunk = zones.Value,
                Terrain = TerrainByChoice[terrain.Index],
                SkyIslands = Switch(sky.Index),
                Hydrology = Switch(rivers.Index),
                Caves = Switch(caves.Index),
            };

            FiresVanillaDialog.Show($"Clone '{source}'", new MenuDialog.Field[] { name, terrain, zones, sky, rivers, caves },
                () => Clone(saveName, name.Value.Trim(), Options()),
                validate: () => FiresWorldClone.IsUsableName(name.Value.Trim(), out string problem) ? FiresWorldClone.ProfileFor(source, Options()).Conflict() : problem,
                okLabel: "Clone");
        }

        private static void Clone(string saveName, string cloneName, WorldCloneOptions options)
        {
            bool cloned = FiresWorldClone.TryClone(saveName, cloneName, options, out string report);
            FiresLogger.LogInfo("[FiresWorldGen] " + report);
            WorldListContextMenu.RefreshWorldList();
            UnifiedPopup.Push(new WarningPopup(cloned ? "World cloned" : "Not cloned", report, UnifiedPopup.Pop, localizeText: false));
        }

        private static bool? Switch(int choice) => choice == SwitchOn ? true : choice == SwitchOff ? false : (bool?)null;

        private static string TerrainTitle(ProfileTerrain terrain)
            => terrain == ProfileTerrain.VoxelOnly ? "full voxel" : terrain == ProfileTerrain.VoxelWrap ? "voxel wrap" : "heightmap";

        private static string FreeName(string wanted)
        {
            if (FiresWorldClone.IsUsableName(wanted, out _)) return wanted;
            for (int attempt = 2; attempt <= MaxNameAttempts; attempt++)
            {
                string numbered = wanted + attempt;
                if (FiresWorldClone.IsUsableName(numbered, out _)) return numbered;
            }
            return wanted;
        }
    }
}
