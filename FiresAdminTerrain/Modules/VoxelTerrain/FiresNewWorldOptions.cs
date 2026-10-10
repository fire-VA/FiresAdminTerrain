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
    //
    // Opened from Select World's "From Gaea..." button (FiresGaeaWorldButton) the same panel is in GAEA MODE: one more row
    // on top takes the Gaea export folder, the generator is Fires (a bake is a Fires world), and Done first has World Gen
    // Studio import and bake the folder (FiresGaeaStudio, behind a cancelable progress popup), then creates the world
    // exactly as Done always does. Tools\GAEA_WORLD_BUTTON.md.
    [HarmonyPatch]
    internal static class FiresNewWorldOptions
    {
        private const int MinWorldNameLength = 5;
        private const string GaeaFolderName = "GaeaFolder";
        private const string GaeaFolderLabel = "Gaea folder";
        private const string GaeaFolderPlaceholder = "the Gaea export folder";
        private const int GaeaFolderCharacterLimit = 260;
        private const string GaeaPeakName = "GaeaPeak";
        private const string GaeaPeakLabel = "Tallest peak (y)";
        private const string GaeaSizeName = "GaeaWorldSize";
        private const string GaeaSizeLabel = "World size (m)";
        private const int GaeaNumberCharacterLimit = 6;
        private const int GaeaRowCount = 3;
        private const string GaeaFailedHeader = "Gaea import failed";
        private const string GaeaNote = "World Gen Studio imports this folder and bakes the world first; that takes a minute or two.";
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
        private static Toggle s_islandWater;
        private static GameObject s_islandWaterLabel;
        private static Toggle s_rivers;
        private static Toggle s_caves;
        private static TextMeshProUGUI s_status;
        private static string s_conflict;
        private static string s_newName;
        private static FiresWorldProfile s_pendingProfile;
        private static RectTransform s_panel;
        private static TMP_InputField s_gaeaFolder;
        private static TMP_InputField s_gaeaPeak;
        private static TMP_InputField s_gaeaSize;
        private static readonly List<GameObject> s_gaeaRow = new List<GameObject>();
        private static readonly List<KeyValuePair<RectTransform, float>> s_laidOut = new List<KeyValuePair<RectTransform, float>>();
        private static bool s_openingGaea;
        private static bool s_gaea;
        private static bool s_gaeaBaked;
        private static bool s_layoutGaea;

        private static float RowStep => RowHeight + RowGap;

        // Select World's "From Gaea..." button: New World, with the Gaea folder row and a Done that imports it first.
        internal static void OpenForGaea(FejdStartup fejd)
        {
            s_openingGaea = true;
            try { fejd.OnWorldNew(); }
            finally { s_openingGaea = false; }
        }

        [HarmonyPatch(typeof(FejdStartup), nameof(FejdStartup.OnWorldNew))]
        [HarmonyPostfix]
        private static void ShowOptions(FejdStartup __instance)
        {
            if (s_container == null) Build(__instance);
            if (s_container == null) return;
            s_gaea = s_openingGaea && s_gaeaFolder != null;
            s_gaeaBaked = false;
            ResetToDefaults();
            ApplyGaeaLayout(s_gaea);
        }

        [HarmonyPatch(typeof(FejdStartup), nameof(FejdStartup.OnNewWorldBack))]
        [HarmonyPostfix]
        private static void LeaveGaeaMode() => s_gaea = false;

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
        private static bool ReadOptions(FejdStartup __instance, bool forceLocal)
        {
            s_pendingProfile = null;
            string name = __instance.m_newWorldName.text ?? "";
            s_newName = name.Length >= MinWorldNameLength && !World.HaveWorld(name) ? name : null;
            if (s_newName == null || s_container == null) return true;
            var profile = ChosenProfile();
            if (profile.Conflict() != null) return false;
            if (s_gaea && !s_gaeaBaked)
            {
                if (GaeaProblem() == null) BakeFromGaea(__instance, name, forceLocal);
                return false;
            }
            s_pendingProfile = profile;
            return true;
        }

        // The Studio imports and bakes on its own thread behind a cancelable popup; once the bake is installed Done runs
        // again, and this time creates the world.
        private static void BakeFromGaea(FejdStartup fejd, string name, bool forceLocal)
        {
            if (!FiresGaeaStudio.Available)
            {
                UnifiedPopup.Push(new WarningPopup(GaeaFailedHeader, "World Gen Studio is not available here.", UnifiedPopup.Pop, localizeText: false));
                return;
            }
            FiresGaeaStudio.PeakProblem(s_gaeaPeak.text, out float peakY);
            FiresGaeaStudio.WorldSizeProblem(s_gaeaSize.text, out float worldSize);
            var job = FiresGaeaStudio.Start(s_gaeaFolder.text, name, peakY, worldSize);
            UnifiedPopup.Push(new CancelableTaskPopup(
                () => $"Making '{name}' from Gaea",
                () => $"{job.Stage}... {job.Clock.Elapsed.TotalSeconds:0} s",
                () => job.Finished,
                () =>
                {
                    FiresGaeaStudio.Cancel(job);
                    UnifiedPopup.Pop();
                }));
            fejd.StartCoroutine(CreateWhenBaked(fejd, job, forceLocal));
        }

        private static System.Collections.IEnumerator CreateWhenBaked(FejdStartup fejd, FiresGaeaStudio.Job job, bool forceLocal)
        {
            while (!job.Finished) yield return null;
            if (job.Failure != null)
            {
                if (!job.Cancelled) UnifiedPopup.Push(new WarningPopup(GaeaFailedHeader, job.Failure, UnifiedPopup.Pop, localizeText: false));
                yield break;
            }
            s_gaeaBaked = true;
            fejd.OnNewWorldDone(forceLocal);
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
            s_gaea = false;
            s_gaeaBaked = false;
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
            s_laidOut.Clear();
            s_gaeaRow.Clear();
            s_gaeaFolder = null;
            s_gaeaPeak = null;
            s_gaeaSize = null;
            s_layoutGaea = false;
            s_panel = panel;
            var template = panel.Find(SeedLabelName)?.GetComponent<TMP_Text>();
            bool offerZones = ZoneModeConfig.OfferAtWorldCreation == null || ZoneModeConfig.OfferAtWorldCreation.Value;
            int rows = offerZones ? 7 : 6;
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
            // 0.2.710 (Fire, 10-05, a player's ask): only under Sky islands. Off = islands are born dry (no ponds, rivers or
            // falls carved, no water placed), to shape water on them by hand with the chaos hammer.
            s_islandWater = Check("SkyIslandWater", fieldLeft, fieldRight, RowCenter(top, row));
            Label(template, "Auto-place water on sky islands", labelLeft, labelRight, RowCenter(top, row++));
            s_islandWaterLabel = s_laidOut[s_laidOut.Count - 1].Key.gameObject;
            s_rivers = Check("Rivers", fieldLeft, fieldRight, RowCenter(top, row));
            Label(template, "Rivers and lakes", labelLeft, labelRight, RowCenter(top, row++));
            s_caves = Check("Caves", fieldLeft, fieldRight, RowCenter(top, row));
            Label(template, "Caves", labelLeft, labelRight, RowCenter(top, row++));

            float statusCenter = RowCenter(top, row) + (RowHeight - StatusHeight) * 0.5f;
            s_status = UIBuilderHelper.CreateLabel(Cell("Status", labelLeft, fieldRight, statusCenter, StatusHeight), "Text", "",
                StatusFontSize, NoteInk, Vector2.zero, Vector2.one, TextAlignmentOptions.Center);
            s_status.raycastTarget = false;
            BuildGaeaRows(fejd, template, labelLeft, labelRight, fieldLeft, fieldRight, top);
            FiresLogger.LogInfo("[FiresWorldGen] New World shows the world options (generator, terrain"
                + (offerZones ? ", one zone at a time" : "") + ", sky islands, rivers and lakes, caves).");
        }

        // Gaea mode's extra top rows: the export folder, the tallest peak and the world size, each in a copy of vanilla's own
        // name field. Hidden until Gaea mode, when ApplyGaeaLayout makes room for them.
        private static void BuildGaeaRows(FejdStartup fejd, TMP_Text template, float labelLeft, float labelRight, float fieldLeft,
            float fieldRight, float top)
        {
            int mark = s_laidOut.Count;
            float raise = GaeaRowCount * RowStep * 0.5f;
            s_gaeaFolder = GaeaInput(fejd, template, GaeaFolderName, GaeaFolderLabel, labelLeft, labelRight, fieldLeft, fieldRight,
                RowCenter(top, 0) + raise, TMP_InputField.ContentType.Standard, GaeaFolderCharacterLimit, GaeaFolderPlaceholder, "");
            s_gaeaPeak = GaeaInput(fejd, template, GaeaPeakName, GaeaPeakLabel, labelLeft, labelRight, fieldLeft, fieldRight,
                RowCenter(top, 1) + raise, TMP_InputField.ContentType.DecimalNumber, GaeaNumberCharacterLimit, "",
                FiresGaeaStudio.DefaultPeakY.ToString("0", System.Globalization.CultureInfo.InvariantCulture));
            s_gaeaSize = GaeaInput(fejd, template, GaeaSizeName, GaeaSizeLabel, labelLeft, labelRight, fieldLeft, fieldRight,
                RowCenter(top, 2) + raise, TMP_InputField.ContentType.DecimalNumber, GaeaNumberCharacterLimit, "",
                FiresGaeaStudio.DefaultWorldSize.ToString("0", System.Globalization.CultureInfo.InvariantCulture));
            for (int i = mark; i < s_laidOut.Count; i++) s_gaeaRow.Add(s_laidOut[i].Key.gameObject);
            s_laidOut.RemoveRange(mark, s_laidOut.Count - mark);
            foreach (var part in s_gaeaRow) part.SetActive(false);
        }

        private static TMP_InputField GaeaInput(FejdStartup fejd, TMP_Text template, string name, string label, float labelLeft,
            float labelRight, float fieldLeft, float fieldRight, float centerY, TMP_InputField.ContentType contentType, int limit,
            string placeholderText, string text)
        {
            var field = Object.Instantiate(fejd.m_newWorldName.gameObject, Cell(name, fieldLeft, fieldRight, centerY)).GetComponent<TMP_InputField>();
            var rect = (RectTransform)field.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            field.name = name;
            foreach (var localize in field.GetComponentsInChildren<Localize>(true)) Object.Destroy(localize);
            field.onValueChanged = new TMP_InputField.OnChangeEvent();
            field.onEndEdit = new TMP_InputField.SubmitEvent();
            field.onSubmit = new TMP_InputField.SubmitEvent();
            field.onSelect = new TMP_InputField.SelectionEvent();
            field.onDeselect = new TMP_InputField.SelectionEvent();
            field.characterLimit = limit;
            field.contentType = contentType;
            field.text = text;
            if (field.placeholder is TMP_Text placeholder) placeholder.text = placeholderText;
            field.onValueChanged.AddListener(_ => Refresh());
            Label(template, label, labelLeft, labelRight, centerY);
            return field;
        }

        // Why the Gaea rows can't be imported yet, or null.
        private static string GaeaProblem()
            => FiresGaeaStudio.FolderProblem(s_gaeaFolder.text)
               ?? FiresGaeaStudio.PeakProblem(s_gaeaPeak.text, out _)
               ?? FiresGaeaStudio.WorldSizeProblem(s_gaeaSize.text, out _);

        // Gaea mode shows its rows on top: the panel grows by them (Grow keeps Seed above and the buttons below), the
        // container grows with it, and every other row keeps its place under them. The generator is Fires there.
        private static void ApplyGaeaLayout(bool gaea)
        {
            if (gaea != s_layoutGaea)
            {
                float rows = GaeaRowCount * RowStep;
                float delta = gaea ? rows : -rows;
                Grow(s_panel, delta, s_container.localPosition.y);
                s_container.sizeDelta += new Vector2(0f, delta);
                s_container.anchoredPosition += new Vector2(0f, delta * 0.5f);
                s_layoutGaea = gaea;
                float shift = gaea ? -rows * 0.5f : 0f;
                foreach (var laid in s_laidOut)
                    laid.Key.anchoredPosition = new Vector2(laid.Key.anchoredPosition.x, laid.Value + shift);
            }
            foreach (var part in s_gaeaRow) part.SetActive(gaea);
            s_generator.interactable = !gaea;
            if (gaea)
            {
                s_generator.SetValueWithoutNotify(FiresGenerator);
                if (s_terrain.value == HeightmapChoice) s_terrain.SetValueWithoutNotify(VoxelWrapChoice);
            }
            Refresh();
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
            s_laidOut.Add(new KeyValuePair<RectTransform, float>(cell, centerY));
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
            s_islandWater.SetIsOnWithoutNotify(true);
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
            SkyIslandWater = s_skyIslands.isOn && !s_islandWater.isOn ? false : (bool?)null,
            Hydrology = s_rivers.isOn,
            Caves = s_caves.isOn,
        };

        private static void Refresh()
        {
            bool islands = s_skyIslands.isOn;
            s_islandWater.gameObject.SetActive(islands);
            s_islandWater.interactable = islands;
            if (s_islandWaterLabel != null) s_islandWaterLabel.SetActive(islands);
            var profile = ChosenProfile();
            s_conflict = profile.Conflict() ?? (s_gaea && s_gaeaFolder != null ? GaeaProblem() : null);
            s_status.color = s_conflict != null ? ConflictInk : NoteInk;
            s_status.text = s_conflict ?? LocalNote(profile) ?? (s_gaea ? GaeaNote : "");
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
