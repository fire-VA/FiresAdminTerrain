using System.Collections.Generic;
using FiresCore.UI;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using VerdantsAscent.Terrain.Zones;

namespace VerdantsAscent.Terrain.Voxel
{
    // ═══ WORLD GENERATION, CHOSEN ON NEW WORLD ═══════════════════════════════
    // Vanilla's New World panel grows rows under Seed: the terrain generator (stock Valheim or Fires), the terrain
    // (heightmap, voxel wrap, full voxel), one zone at a time, and the world's own features (sky islands, rivers and lakes,
    // caves). Done creates the world with them as its profile (FiresWorldProfile), which the dedicated server and every
    // client follow on every load. FiresWorldProfile.Conflict() holds Done and says why in red; an amber line says what
    // this installation's own config switches off here. Vanilla's panel keeps its own controls; it only grows to fit.
    [HarmonyPatch]
    internal static class FiresNewWorldOptions
    {
        private const int MinWorldNameLength = 5;
        private const string ContainerName = "FiresWorldOptions";
        private const string SeedLabelName = "seed";
        private const int FiresGenerator = 1;
        private const int HeightmapChoice = 0;
        private const int VoxelWrapChoice = 1;
        private const int FullVoxelChoice = 2;
        private const float RowHeight = 34f;
        private const float RowGap = 6f;
        private const float StatusHeight = 24f;
        private const float Padding = 10f;
        private const float LabelGap = 6f;
        private const float EdgeMargin = 12f;
        private const float ControlFontSize = 15f;
        private const float StatusFontSize = 15f;
        private const float FallbackLabelFontSize = 18f;

        private static readonly string[] GeneratorChoices = { "Stock Valheim", "Fires" };
        private static readonly string[] TerrainChoices = { "Heightmap", "Voxel wrap", "Full voxel" };
        private static readonly ProfileTerrain[] TerrainByChoice = { ProfileTerrain.Heightmap, ProfileTerrain.VoxelWrap, ProfileTerrain.VoxelOnly };
        private static readonly Color FallbackLabelInk = new Color(0.93f, 0.85f, 0.66f);
        private static readonly Color ConflictInk = new Color(0.95f, 0.3f, 0.22f);
        private static readonly Color NoteInk = new Color(0.98f, 0.72f, 0.3f);

        private static readonly AccessTools.FieldRef<FejdStartup, World> CreatedWorldRef =
            AccessTools.FieldRefAccess<FejdStartup, World>("m_world");

        private static RectTransform s_container;
        private static TMP_Dropdown s_generator;
        private static TMP_Dropdown s_terrain;
        private static Toggle s_oneZoneAtATime;
        private static Toggle s_skyIslands;
        private static Toggle s_rivers;
        private static Toggle s_caves;
        private static TextMeshProUGUI s_status;
        private static string s_conflict;
        private static string s_newName;
        private static FiresWorldProfile s_pendingProfile;

        [HarmonyPatch(typeof(FejdStartup), nameof(FejdStartup.OnWorldNew))]
        [HarmonyPostfix]
        private static void ShowOptions(FejdStartup __instance)
        {
            if (s_container == null) Build(__instance);
            if (s_container == null) return;
            ResetToDefaults();
        }

        // Vanilla re-enables Done every frame once the name is long enough, so a conflict disables it again after that.
        [HarmonyPatch(typeof(FejdStartup), "Update")]
        [HarmonyPostfix]
        private static void HoldDoneOnConflict(FejdStartup __instance)
        {
            if (s_conflict == null || s_container == null || !__instance.m_createWorldPanel.activeInHierarchy) return;
            __instance.m_newWorldDone.interactable = false;
        }

        [HarmonyPatch(typeof(FejdStartup), nameof(FejdStartup.OnNewWorldDone))]
        [HarmonyPrefix]
        private static bool ReadOptions(FejdStartup __instance)
        {
            s_pendingProfile = null;
            string name = __instance.m_newWorldName.text ?? "";
            s_newName = name.Length >= MinWorldNameLength && !World.HaveWorld(name) ? name : null;
            if (s_newName == null || s_container == null) return true;
            var profile = ChosenProfile();
            if (profile.Conflict() != null) return false;
            s_pendingProfile = profile;
            return true;
        }

        // A deleted world's records under this name go first, then the rows become the new world's profile, and one zone
        // at a time also gets its registry.
        [HarmonyPatch(typeof(FejdStartup), nameof(FejdStartup.OnNewWorldDone))]
        [HarmonyPostfix]
        private static void RecordProfileAfterCreating(FejdStartup __instance)
        {
            string name = s_newName;
            if (name == null) return;
            var world = CreatedWorldRef(__instance);
            if (world == null || world.m_name != name || !World.HaveWorld(name)) return;
            s_newName = null;
            var profile = s_pendingProfile;
            s_pendingProfile = null;
            FiresWorldRecords.ClearLeftovers(name);
            if (profile == null) return;
            if (profile.Write(name))
                FiresLogger.LogInfo($"[FiresWorldGen] new world '{name}' profiled: {profile.Describe()}.");
            if (!profile.ZoneMode) return;
            if (ZoneModeRegistry.CreateForNewWorld(world, out string path))
                FiresLogger.LogInfo($"[FiresZones] new world '{name}' created as ONE ZONE AT A TIME ({path}). "
                    + "Its start area opens the first time it loads.");
            else
                FiresLogger.LogError($"[FiresZones] could not write the zone registry for '{name}' — it will load as a normal world. "
                    + "Delete the world and create it again.");
        }

        // ── Building the rows ───────────────────────────────────────────────
        private static void Build(FejdStartup fejd)
        {
            var seed = (RectTransform)fejd.m_newWorldSeed.transform;
            var done = (RectTransform)fejd.m_newWorldDone.transform;
            var panel = seed.parent as RectTransform;
            if (panel == null || done.parent != panel)
            {
                FiresLogger.LogWarning("[FiresWorldGen] New World's panel is not laid out as expected; its world options are not shown.");
                return;
            }
            var template = panel.Find(SeedLabelName)?.GetComponent<TMP_Text>();
            bool offerZones = ZoneModeConfig.OfferAtWorldCreation == null || ZoneModeConfig.OfferAtWorldCreation.Value;
            int rows = offerZones ? 6 : 5;
            float height = Padding * 2f + rows * RowHeight + (rows - 1) * RowGap + StatusHeight;
            float seedBottom = seed.localPosition.y - seed.rect.height * seed.pivot.y;
            float doneTop = done.localPosition.y + done.rect.height * (1f - done.pivot.y);
            float fieldLeft = seed.localPosition.x - seed.rect.width * seed.pivot.x - panel.rect.center.x;
            float pivotY = panel.pivot.y;
            Grow(panel, height, (seedBottom + doneTop) * 0.5f);

            float gapCenter = (seedBottom + height * (1f - pivotY) + doneTop - height * pivotY) * 0.5f;
            s_container = new GameObject(ContainerName, typeof(RectTransform)).GetComponent<RectTransform>();
            s_container.SetParent(panel, false);
            s_container.anchorMin = s_container.anchorMax = new Vector2(0.5f, pivotY);
            s_container.pivot = new Vector2(0.5f, 0.5f);
            s_container.sizeDelta = new Vector2(panel.rect.width, height);
            s_container.anchoredPosition = new Vector2(0f, gapCenter);

            float fieldRight = fieldLeft + seed.rect.width;
            float labelLeft = -panel.rect.width * 0.5f + EdgeMargin;
            float labelRight = fieldLeft - LabelGap;
            float top = height * 0.5f - Padding;
            int row = 0;

            s_generator = UIBuilderHelper.CreateDropdown(Cell("Generator", fieldLeft, fieldRight, RowCenter(top, row)), "Choice",
                GeneratorChoices, 0, Vector2.zero, Vector2.one, _ => Refresh(), ControlFontSize);
            Label(template, "Terrain generator", labelLeft, labelRight, RowCenter(top, row++));
            s_terrain = UIBuilderHelper.CreateDropdown(Cell("Terrain", fieldLeft, fieldRight, RowCenter(top, row)), "Choice",
                TerrainChoices, 0, Vector2.zero, Vector2.one, _ => Refresh(), ControlFontSize);
            Label(template, "Terrain", labelLeft, labelRight, RowCenter(top, row++));
            if (offerZones)
            {
                s_oneZoneAtATime = Check("OneZoneAtATime", fieldLeft, fieldRight, RowCenter(top, row));
                Label(template, "One zone at a time", labelLeft, labelRight, RowCenter(top, row++));
            }
            s_skyIslands = Check("SkyIslands", fieldLeft, fieldRight, RowCenter(top, row));
            Label(template, "Sky islands", labelLeft, labelRight, RowCenter(top, row++));
            s_rivers = Check("Rivers", fieldLeft, fieldRight, RowCenter(top, row));
            Label(template, "Rivers and lakes", labelLeft, labelRight, RowCenter(top, row++));
            s_caves = Check("Caves", fieldLeft, fieldRight, RowCenter(top, row));
            Label(template, "Caves", labelLeft, labelRight, RowCenter(top, row++));

            float statusCenter = RowCenter(top, row) + (RowHeight - StatusHeight) * 0.5f;
            s_status = UIBuilderHelper.CreateLabel(Cell("Status", labelLeft, fieldRight, statusCenter, StatusHeight), "Text", "",
                StatusFontSize, NoteInk, Vector2.zero, Vector2.one, TextAlignmentOptions.Center);
            s_status.raycastTarget = false;
            FiresLogger.LogInfo("[FiresWorldGen] New World shows the world options (generator, terrain"
                + (offerZones ? ", one zone at a time" : "") + ", sky islands, rivers and lakes, caves).");
        }

        // Grows the panel by `height` around its pivot. Children above `split` stay with the top edge and those below with
        // the bottom edge, whatever their anchors, so the new rows fit between Seed and the buttons.
        private static void Grow(RectTransform panel, float height, float split)
        {
            float pivotY = panel.pivot.y;
            var children = new List<RectTransform>();
            foreach (Transform child in panel)
                if (child is RectTransform rect && Mathf.Approximately(rect.anchorMin.y, rect.anchorMax.y)) children.Add(rect);
            var above = new Dictionary<RectTransform, bool>();
            foreach (var rect in children) above[rect] = rect.localPosition.y > split;
            panel.sizeDelta += new Vector2(0f, height);
            foreach (var rect in children)
            {
                float anchorY = rect.anchorMin.y;
                float automatic = height * (anchorY - pivotY);
                float wanted = above[rect] ? height * (1f - pivotY) : -height * pivotY;
                rect.anchoredPosition += new Vector2(0f, wanted - automatic);
            }
        }

        private static float RowCenter(float top, int row) => top - row * (RowHeight + RowGap) - RowHeight * 0.5f;

        private static RectTransform Cell(string name, float left, float right, float centerY, float height = RowHeight)
        {
            var cell = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
            cell.SetParent(s_container, false);
            cell.anchorMin = cell.anchorMax = new Vector2(0.5f, 0.5f);
            cell.pivot = new Vector2(0.5f, 0.5f);
            cell.sizeDelta = new Vector2(right - left, height);
            cell.anchoredPosition = new Vector2((left + right) * 0.5f, centerY);
            return cell;
        }

        private static void Label(TMP_Text template, string text, float left, float right, float centerY)
        {
            var label = UIBuilderHelper.CreateLabel(Cell(text, left, right, centerY), "Text", text,
                template != null ? template.fontSize : FallbackLabelFontSize, template != null ? template.color : FallbackLabelInk,
                Vector2.zero, Vector2.one, template != null ? template.alignment : TextAlignmentOptions.MidlineRight);
            if (template != null) label.font = template.font;
            label.raycastTarget = false;
        }

        private static Toggle Check(string name, float left, float right, float centerY)
            => UIBuilderHelper.CreateToggle(Cell(name, left, right, centerY), "Check", false, Vector2.zero, Vector2.one, _ => Refresh());

        // ── Values ──────────────────────────────────────────────────────────
        private static void ResetToDefaults()
        {
            var genMode = FiresVoxelConfig.WorldGenMode?.Value ?? FiresWorldGenMode.Auto;
            bool fires = genMode == FiresWorldGenMode.ForceOn;
            s_generator.SetValueWithoutNotify(fires ? FiresGenerator : 0);
            int terrain = DefaultTerrainChoice();
            s_terrain.SetValueWithoutNotify(!fires && terrain == FullVoxelChoice ? VoxelWrapChoice : terrain);
            s_oneZoneAtATime?.SetIsOnWithoutNotify(false);
            s_skyIslands.SetIsOnWithoutNotify(FiresVoxelConfig.SkyIslands == null || FiresVoxelConfig.SkyIslands.Value);
            s_rivers.SetIsOnWithoutNotify(FiresVoxelConfig.HydrologyEnabled != null && FiresVoxelConfig.HydrologyEnabled.Value);
            s_caves.SetIsOnWithoutNotify(FiresVoxelConfig.GenerateCaves != null && FiresVoxelConfig.GenerateCaves.Value);
            Refresh();
        }

        private static int DefaultTerrainChoice()
        {
            if ((FiresVoxelConfig.TerrainMode?.Value ?? FiresTerrainMode.Off) != FiresTerrainMode.FullVoxel) return HeightmapChoice;
            return (FiresVoxelConfig.WorldMode?.Value ?? VoxelWorldMode.VoxelWrap) == VoxelWorldMode.VoxelOnly ? FullVoxelChoice : VoxelWrapChoice;
        }

        private static FiresWorldProfile ChosenProfile() => new FiresWorldProfile
        {
            Generator = s_generator.value == FiresGenerator ? ProfileGenerator.Fires : ProfileGenerator.Vanilla,
            Terrain = TerrainByChoice[Mathf.Clamp(s_terrain.value, 0, TerrainByChoice.Length - 1)],
            ZoneMode = s_oneZoneAtATime != null && s_oneZoneAtATime.isOn,
            SkyIslands = s_skyIslands.isOn,
            Hydrology = s_rivers.isOn,
            Caves = s_caves.isOn,
        };

        private static void Refresh()
        {
            var profile = ChosenProfile();
            s_conflict = profile.Conflict();
            s_status.color = s_conflict != null ? ConflictInk : NoteInk;
            s_status.text = s_conflict ?? LocalNote(profile) ?? "";
        }

        // What this installation's own config switches off for the chosen world; the world keeps its choices elsewhere.
        private static string LocalNote(FiresWorldProfile profile)
        {
            bool voxel = profile.Terrain != ProfileTerrain.Heightmap;
            bool voxelModule = FiresVoxelConfig.Enabled != null && FiresVoxelConfig.Enabled.Value
                               && (FiresVoxelConfig.CustomGeneration == null || FiresVoxelConfig.CustomGeneration.Value);
            if (voxel && !voxelModule)
                return "This installation has voxel terrain switched off, so it builds this world without it.";
            if (profile.Generator == ProfileGenerator.Fires && (FiresVoxelConfig.WorldGenMode?.Value ?? FiresWorldGenMode.Auto) == FiresWorldGenMode.Off)
                return "This installation has WorldGen Mode Off, so it builds this world with stock terrain.";
            if (!voxel && (profile.SkyIslands == true || profile.Caves == true))
                return "Sky islands and caves need voxel terrain; on heightmap they stay off.";
            return null;
        }
    }
}
