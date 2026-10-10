using System.Collections;
using System.Collections.Generic;
using System.IO;
using FiresCore.UI;
using FiresCore.UI.ContextMenu;
using VerdantsAscent.Async;
using VerdantsAscent.Terrain.Zones;

namespace VerdantsAscent.Terrain.Voxel
{
    // Select World's right-click rows: clone a world as one zone at a time or onto other terrain. Each row opens one Core
    // MenuDialog preset for it (name, terrain, one zone at a time, sky islands, rivers and lakes, caves); FiresWorldClone
    // makes the copy and the original is never touched. "As the original" keeps what the world has, so a split is faithful.
    // The copy is made from the row's save (its file name); the dialog and FAT's records use the world's own name, which
    // a renamed world no longer shares with its file. Full voxel is offered for any world: on a stock one the vanilla
    // heightmap sets the ground and the voxel field is built from it, with only the river carve, caves and sky islands
    // added on top.
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
            // Full voxel no longer needs the Fires generator. On a stock world it means the vanilla heightmap sets the
            // ground, the voxel field is built from it, and the river carve, caves and sky islands run on that data -
            // the world keeps its own shape instead of a new one being generated from the seed.
            // ONE row, not four. The four were presets into this same dialog - each one pre-picked a terrain and
            // then hid the other three choices behind a different right-click, so whichever you took you lost the
            // rest. The dialog already offers all of them, so the preset is "as the original" and every choice is
            // made in one place.
            // The "Clone..." row is GONE, merged into BcWorldListMenu's "Convert..." (2026-09-26, Fire's call:
            // "instead of clone and convert being two separate options they both handle either converting a bc
            // world or cloning/updating a normal world"). That row already clones first, never touches the
            // original, and offers this same set of terrain options, so two entries was one too many - and a
            // normal world could be greyed out by both at once for different reasons.
            //
            // This provider contributes NO ROW now. It must not yield its own "Convert..." or the world list
            // would show two identical entries from two owners - which is the two-rows problem again wearing
            // one name. ShowCloneDialog below stays reachable for callers that want the clone dialog directly.
            _ = refusal;
            yield break;
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

        // Run, not block. A 451 MB world copied synchronously froze the game for the length of the copy; the
        // coroutine yields between chunks and the popup shows how far it has got, so a long copy looks like a long
        // copy instead of a hang.
        private static void Clone(string saveName, string cloneName, WorldCloneOptions options)
        {
            // CancelableTaskPopup is vanilla's own long-task popup: it re-reads these every frame, so the progress
            // line updates itself. WarningPopup's text is readonly and cannot.
            string status = $"Copying '{saveName}' as '{cloneName}'...";
            bool finished = false;
            UnifiedPopup.Push(new CancelableTaskPopup(() => "Cloning world", () => status, () => finished, () => { }));

            MainThreadDispatcher.StartRoutine(FiresWorldClone.CloneAsync(saveName, cloneName, options,
                text => status = text,
                (cloned, report) =>
                {
                    finished = true;
                    FiresLogger.LogInfo("[FiresWorldGen] " + report);
                    WorldListContextMenu.RefreshWorldList();
                    MainThreadDispatcher.StartRoutine(ShowResultWhenTaskPopupIsGone(cloned, report));
                }));
        }

        // The task popup does not leave the stack the moment its work is done: its UpdateRoutine notices
        // shouldClose on its NEXT tick, and UnifiedPopup pops it after that. Pushing the result straight away put
        // it ON TOP of a popup that was still there, so closing the result re-showed the finished task - which
        // relabels the button "Cancel", leaves the result's text on screen, and whose coroutine has already ended,
        // so vanilla's cancel handler ran StopCoroutine(null) and threw on every click with no way to close it.
        private static IEnumerator ShowResultWhenTaskPopupIsGone(bool cloned, string report)
        {
            while (UnifiedPopup.IsVisible()) yield return null;
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
