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
    // exactly as Done always does.
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
        // 0.2.1510 ([saddles], Fire 11-21: New World panel unreadable): two lines of status, never into the buttons.
        private const float StatusHeight = 44f;
        private const float StatusMinFontSize = 11f;
        private const float LabelRowPad = 4f;   // above + below a wrapped label
        private const float Padding = 10f;
        private const float LabelGap = 6f;
        private const float EdgeMargin = 12f;
        private const float LabelShare = 0.45f;   // 0.2.1512: label column = 45% of the row (was ~30%, set by vanilla's seed field)
        private const float MinFieldWidth = 180f;   // 0.2.1512: room for the longest dropdown choice; the shot confirms
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
        private static Toggle s_rivers;
        private static Toggle s_caves;
        private static Toggle s_superFlat;   // The admin build world
        private static TextMeshProUGUI s_status;
        private static string s_conflict;
        // 0.2.1513 ([saddles], [lead] 11-21 shots 08/09): a row that can't be changed right now stays in place, greyed (label and
        // control); nothing is hidden, so no gap. Each control's own label, recorded as the row is built.
        private const float DimAlpha = 0.4f;
        private const string GeneratorLockedCaption = "Set by Super flat";
        private static readonly Dictionary<Selectable, TMP_Text> s_rowLabel = new Dictionary<Selectable, TMP_Text>();
        private static TMP_Text s_lastLabel;
        private static readonly Dictionary<TMP_Text, Color> s_builtInk = new Dictionary<TMP_Text, Color>();
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

        // 0.2.1512 ([lead] 11-21, kept from the 0.2.1511 diagnostic at Debug): Fire's 1510 session hit a NullReferenceException in
        // TMP_Dropdown.AlphaFadeList < Show twice; TMP creates m_AlphaTweenRunner only in Start (Unity.TextMeshPro decomp :440-443)
        // and the install-copy repro never reproduced it (Start ran 1 frame after open). This names the state on the next hit.
        private static readonly System.Reflection.FieldInfo DropdownRunner = AccessTools.Field(typeof(TMP_Dropdown), "m_AlphaTweenRunner");

        [HarmonyPatch(typeof(TMP_Dropdown), nameof(TMP_Dropdown.Show))]
        private static class DropdownShowState
        {
            private static void Prefix(TMP_Dropdown __instance)
            {
                if (__instance == null || (__instance != s_generator && __instance != s_terrain)) return;
                FiresLogger.LogDebug($"[FiresWorldGen] New World dropdown Show ({(__instance == s_generator ? "generator" : "terrain")}, frame " +
                    $"{Time.frameCount}): enabled {__instance.enabled}, activeInHierarchy {__instance.gameObject.activeInHierarchy}, " +
                    $"isActiveAndEnabled {__instance.isActiveAndEnabled}, interactable {__instance.interactable}, " +
                    $"Start ran {(DropdownRunner != null && DropdownRunner.GetValue(__instance) != null)}.");
            }
        }

        // 0.2.1512 ([lead] 11-21, kept from the 0.2.1511 diagnostic at Debug): Fire's 1510 session, "pressed start on my existing world
        // and nothing happened", logged no world load at all. First / last around vanilla's OnWorldStart: no 'called' line = the click
        // never reached it; 'called' with loading off = an early out or a prefix (listed with owner + priority).
        [HarmonyPatch(typeof(FejdStartup), nameof(FejdStartup.OnWorldStart))]
        private static class WorldStartState
        {
            private static readonly System.Reflection.FieldInfo WorldRef = AccessTools.Field(typeof(FejdStartup), "m_world");

            [HarmonyPriority(Priority.First)]
            private static void Prefix(FejdStartup __instance)
            {
                var w = WorldRef?.GetValue(__instance) as World;
                var info = Harmony.GetPatchInfo(AccessTools.Method(typeof(FejdStartup), nameof(FejdStartup.OnWorldStart)));
                var owners = new System.Text.StringBuilder();
                if (info != null) foreach (var p in info.Prefixes) owners.Append(owners.Length > 0 ? ", " : "").Append(p.owner).Append(" (").Append(p.priority).Append(')');
                FiresLogger.LogDebug($"[FiresWorldGen] OnWorldStart called (frame {Time.frameCount}): world '{(w != null ? w.m_name : "null")}' " +
                    $"dataError {(w != null ? w.m_dataError.ToString() : "-")}; prefixes [{owners}].");
            }

            [HarmonyPriority(Priority.Last)]
            private static void Postfix(FejdStartup __instance) =>
                FiresLogger.LogDebug($"[FiresWorldGen] OnWorldStart after (frame {Time.frameCount}): loading panel " +
                    $"{(__instance.m_loading != null && __instance.m_loading.activeInHierarchy)}.");
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
            // Runs on every New World Done, the panel-failed-to-build case included (ReadOptions sets s_newName before it
            // checks s_container), so a deleted world's leftovers never reach a new world made here.
            FiresWorldRecords.ClearLeftovers(name);
            if (profile == null) return;
            profile.WorldUid = world.m_uid;   // 0.2.1327: the profile names its world; a later same-name world's load rejects it
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

        // 0.2.1327 (Fire 10-31, audit): vanilla's world delete (World.RemoveWorld, from the world list's Remove) leaves FAT's
        // records behind, and a new world under the same name could pick them up. Removing them with the world is the
        // same file set New World clears. Only once the save is really gone (a failed delete keeps the world and its records).
        [HarmonyPatch(typeof(World), nameof(World.RemoveWorld))]
        [HarmonyPostfix]
        private static void ClearRecordsOnWorldDelete(string name)
        {
            try
            {
                if (string.IsNullOrEmpty(name)) return;
                if (World.HaveWorld(name))
                {
                    FiresLogger.LogDebug($"[FiresWorldGen] '{name}' still exists after the world delete: its records are kept.");
                    return;
                }
                FiresLogger.LogDebug($"[FiresWorldGen] world '{name}' deleted: removing its FAT records (ClearLeftovers).");
                FiresWorldRecords.ClearLeftovers(name);
            }
            catch (System.Exception ex)
            {
                FiresLogger.LogWarning($"[FiresWorldGen] could not remove the records of deleted world '{name}': {ex.Message}");
            }
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
            s_rowLabel.Clear();
            s_builtInk.Clear();
            s_lastLabel = null;
            s_gaeaRow.Clear();
            s_gaeaFolder = null;
            s_gaeaPeak = null;
            s_gaeaSize = null;
            s_layoutGaea = false;
            s_panel = panel;
            var template = panel.Find(SeedLabelName)?.GetComponent<TMP_Text>();
            bool offerZones = ZoneModeConfig.OfferAtWorldCreation == null || ZoneModeConfig.OfferAtWorldCreation.Value;
            float seedBottom = seed.localPosition.y - seed.rect.height * seed.pivot.y;
            float doneTop = done.localPosition.y + done.rect.height * (1f - done.pivot.y);
            float fieldLeft = seed.localPosition.x - seed.rect.width * seed.pivot.x - panel.rect.center.x;
            float pivotY = panel.pivot.y;

            // 0.2.1510 ([saddles], Fire 11-21: New World panel unreadable): rows are laid out first with a running cursor
            // from the container's top edge (y = 0), each row as tall as its measured label; the panel then grows by the
            // measured sum and every cell is moved into the container's centre-based frame (s_laidOut too, so the Gaea shift
            // at ApplyGaeaLayout still restores the right places). All sizes are canvas units, so any resolution fits.
            s_container = new GameObject(ContainerName, typeof(RectTransform)).GetComponent<RectTransform>();
            s_container.SetParent(panel, false);
            s_container.anchorMin = s_container.anchorMax = new Vector2(0.5f, pivotY);
            s_container.pivot = new Vector2(0.5f, 0.5f);
            s_container.sizeDelta = new Vector2(panel.rect.width, 0f);

            float fieldRight = fieldLeft + seed.rect.width;
            float labelLeft = -panel.rect.width * 0.5f + EdgeMargin;
            // 0.2.1512 ([saddles], Fire 11-21 "still shit": labels wrapped to 3 lines): our rows' controls start at LabelShare of
            // the row width instead of under vanilla's seed field (~30 %), so the label column is wider and labels take 1-2 lines.
            // fieldRight stays vanilla's; fieldLeft moves RIGHT to LabelShare, capped so a field keeps MinFieldWidth ([2ic]).
            fieldLeft = Mathf.Min(Mathf.Max(fieldLeft, labelLeft + (fieldRight - labelLeft) * LabelShare), fieldRight - MinFieldWidth);
            float labelRight = fieldLeft - LabelGap;
            float cursor = -Padding;
            Color valueInk = SeedTextInk(fejd, template);

            LabelRow(template, "Terrain generator", labelLeft, labelRight, ref cursor, out float c);
            s_generator = Dropdown("Generator", GeneratorChoices, fieldLeft, fieldRight, c, valueInk);
            LabelRow(template, "Terrain", labelLeft, labelRight, ref cursor, out c);
            s_terrain = Dropdown("Terrain", TerrainChoices, fieldLeft, fieldRight, c, valueInk);
            if (offerZones)
            {
                LabelRow(template, "One zone at a time", labelLeft, labelRight, ref cursor, out c);
                s_oneZoneAtATime = Check(fejd, "OneZoneAtATime", labelLeft, fieldLeft, fieldRight, c);
            }
            LabelRow(template, "Sky islands", labelLeft, labelRight, ref cursor, out c);
            s_skyIslands = Check(fejd, "SkyIslands", labelLeft, fieldLeft, fieldRight, c);
            // Only under Sky islands. Off = islands are born dry (no ponds, rivers or
            // falls carved, no water placed), to shape water on them by hand with the chaos hammer.
            LabelRow(template, "Auto-place water on sky islands", labelLeft, labelRight, ref cursor, out c);
            s_islandWater = Check(fejd, "SkyIslandWater", labelLeft, fieldLeft, fieldRight, c);
            LabelRow(template, "Rivers and lakes", labelLeft, labelRight, ref cursor, out c);
            s_rivers = Check(fejd, "Rivers", labelLeft, fieldLeft, fieldRight, c);
            LabelRow(template, "Caves", labelLeft, labelRight, ref cursor, out c);
            s_caves = Check(fejd, "Caves", labelLeft, fieldLeft, fieldRight, c);
            // An admin build / test / blueprint world, linked through Dimensions so build yards and big ZDO counts
            // stay off the main server. It overrides every row above (stock heightmap, nothing extra) and the world's size.
            LabelRow(template, "Super flat (admin build world)", labelLeft, labelRight, ref cursor, out c);
            s_superFlat = Check(fejd, "SuperFlat", labelLeft, fieldLeft, fieldRight, c);

            // 0.2.1510 ([saddles], Fire 11-21: New World panel unreadable): two-line status box that wraps, shrinks to
            // StatusMinFontSize and then ellipsizes, so it stays inside its own cell above the buttons.
            s_status = UIBuilderHelper.CreateLabel(Cell("Status", labelLeft, fieldRight, cursor - StatusHeight * 0.5f, StatusHeight), "Text", "",
                StatusFontSize, NoteInk, Vector2.zero, Vector2.one, TextAlignmentOptions.Center);
            s_status.raycastTarget = false;
            s_status.enableWordWrapping = true;      // UNCONFIRMED: TMP version may name it textWrappingMode instead
            s_status.enableAutoSizing = true;
            s_status.fontSizeMin = StatusMinFontSize;
            s_status.fontSizeMax = StatusFontSize;
            s_status.overflowMode = TextOverflowModes.Ellipsis;
            cursor -= StatusHeight + Padding;

            // 0.2.1510: the measured height; the panel grows by it and every cell moves from the top-edge frame to the
            // container's centre frame (+height/2).
            float height = -cursor;
            Grow(panel, height, (seedBottom + doneTop) * 0.5f);
            float gapCenter = (seedBottom + height * (1f - pivotY) + doneTop - height * pivotY) * 0.5f;
            s_container.sizeDelta = new Vector2(panel.rect.width, height);
            s_container.anchoredPosition = new Vector2(0f, gapCenter);
            for (int i = 0; i < s_laidOut.Count; i++)
            {
                var laid = s_laidOut[i];
                float y = laid.Value + height * 0.5f;
                laid.Key.anchoredPosition = new Vector2(laid.Key.anchoredPosition.x, y);
                s_laidOut[i] = new KeyValuePair<RectTransform, float>(laid.Key, y);
            }

            float top = height * 0.5f - Padding;
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

        private static TextMeshProUGUI Label(TMP_Text template, string text, float left, float right, float centerY, float height = RowHeight)
        {
            var label = UIBuilderHelper.CreateLabel(Cell(text, left, right, centerY, height), "Text", text,
                template != null ? template.fontSize : FallbackLabelFontSize, template != null ? template.color : FallbackLabelInk,
                Vector2.zero, Vector2.one, template != null ? template.alignment : TextAlignmentOptions.MidlineRight);
            if (template != null) label.font = template.font;
            label.raycastTarget = false;
            return label;
        }

        // 0.2.1510 ([saddles], Fire 11-21: New World panel unreadable): one row at the running cursor (top-edge frame,
        // y down from 0). The label is measured at its cell width; the row is max(RowHeight, wrapped height + pad), so a
        // wrapped label never overlaps the next row. `center` is where the row's control goes.
        private static float LabelRow(TMP_Text template, string text, float left, float right, ref float cursor, out float center)
        {
            var label = Label(template, text, left, right, 0f);
            s_lastLabel = label;   // 0.2.1513: the next control built (Check / Dropdown) takes it as its row label; a row without one is overwritten by the next LabelRow
            label.enableWordWrapping = true;  // [2ic] MUST: measure what draws (wrapped). UNCONFIRMED name, as the status's (:310)
            // UNCONFIRMED: TMP_Text.GetPreferredValues(string, float, float) measures without changing the label's text;
            // it is the standard TMP API but its exact behaviour in Valheim's TMP build is not read here.
            float wrapped = label.GetPreferredValues(text, right - left, 0f).y;
            float height = Mathf.Max(RowHeight, wrapped + LabelRowPad * 2f);
            center = cursor - height * 0.5f;
            var cell = s_laidOut[s_laidOut.Count - 1].Key;
            cell.sizeDelta = new Vector2(cell.sizeDelta.x, height);
            cell.anchoredPosition = new Vector2(cell.anchoredPosition.x, center);
            s_laidOut[s_laidOut.Count - 1] = new KeyValuePair<RectTransform, float>(cell, center);
            cursor -= height + RowGap;
            return height;
        }

        // 0.2.1510 ([saddles], Fire 11-21: New World panel unreadable): vanilla's seed input text colour, the light ink
        // vanilla uses on this same wood. Falls back to the seed label's colour, then FallbackLabelInk.
        private static Color SeedTextInk(FejdStartup fejd, TMP_Text template)
        {
            // UNCONFIRMED: m_newWorldSeed's component type; GetComponent<TMP_InputField> works whatever field type it is.
            var input = fejd.m_newWorldSeed.GetComponent<TMP_InputField>();
            if (input != null && input.textComponent != null) return input.textComponent.color;
            return template != null ? template.color : FallbackLabelInk;
        }

        // 0.2.1510 ([saddles], Fire 11-21: New World panel unreadable): UIBuilderHelper.CreateDropdown paints caption and
        // arrow ParchmentInk (dark, UIBuilderHelper.cs:395,402) over ParchmentField (0.22 alpha, :391) = dark on wood.
        // Caption, arrow and item text take vanilla's light seed ink (item background is ParchmentButton dark brown, :463).
        private static TMP_Dropdown Dropdown(string name, string[] choices, float left, float right, float centerY, Color ink)
        {
            var dropdown = UIBuilderHelper.CreateDropdown(Cell(name, left, right, centerY), "Choice",
                choices, 0, Vector2.zero, Vector2.one, _ => Refresh(), ControlFontSize);
            if (dropdown.captionText != null) dropdown.captionText.color = ink;
            if (dropdown.itemText != null) dropdown.itemText.color = ink;
            var arrow = dropdown.transform.Find("Arrow")?.GetComponent<TMP_Text>();   // name from UIBuilderHelper.cs:402
            if (arrow != null) arrow.color = ink;
            TakeRowLabel(dropdown);
            return dropdown;
        }

        // 0.2.1510 ([saddles], Fire 11-21: New World panel unreadable): a clone of vanilla's own public-server toggle (clear
        // on/off checkbox), the same way BcWorldListMenu.CloneToggle does (BcWorldListMenu.cs:335-350): scene listeners
        // silenced, gamepad hint removed, its own caption emptied (our label sits left of it). Sits at the cell's left at
        // vanilla's own size. Falls back to UIBuilderHelper.CreateToggle if vanilla's toggle is missing.
        private static Toggle Check(FejdStartup fejd, string name, float rowLeft, float left, float right, float centerY)
        {
            var cell = Cell(name, left, right, centerY);
            var toggle = VerdantsAscent.Terrain.BcImport.BcConvertDialog.CloneVanillaToggle(fejd, cell);   // [2ic]: one method
            if (toggle == null)
            {
                var fallback = UIBuilderHelper.CreateToggle(cell, "Check", false, Vector2.zero, Vector2.one, _ => Refresh());
                TakeRowLabel(fallback);
                return fallback;
            }
            toggle.name = "Check";
            // [2ic] SHOULD 2: vanilla's toggle may carry Explicit navigation to its own neighbours; gamepad focus must stay on our rows.
            toggle.navigation = new Navigation { mode = Navigation.Mode.Automatic };
            // 0.2.1512 ([saddles], 1511 dump on the install copy: isOn True but the Checkmark's alpha 0.17 at panel open): the clone is set
            // off then on in the same frame (CloneVanillaToggle, ResetToDefaults) under vanilla's Fade transition; whether Fire's faint
            // ticks were a fade caught mid-way or a frozen fade is UNCONFIRMED ([2ic]). No transition = the tick shows at once (no fade),
            // so the Checkmark always matches isOn.
            toggle.toggleTransition = Toggle.ToggleTransition.None;
            var caption = toggle.GetComponentInChildren<TMP_Text>(true);
            if (caption != null) caption.text = "";
            // UNCONFIRMED: vanilla's toggle child Localize could re-write the emptied caption; GaeaInput (:343) strips Localize
            // the same way, so do it here too.
            foreach (var localize in toggle.GetComponentsInChildren<Localize>(true)) Object.Destroy(localize);
            var rect = (RectTransform)toggle.transform;
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 0.5f);
            rect.pivot = new Vector2(0f, 0.5f);
            rect.anchoredPosition = Vector2.zero;   // keeps vanilla's sizeDelta
            // 0.2.1513 ([lead] 11-21: a Super flat off-click got eaten once [generator]): the clone keeps vanilla's small box as
            // its only click target. An invisible Image under the toggle spans the whole row (label + field cell), so a click
            // anywhere on the row reaches the Toggle (the pointer event goes up to the first handler in the parents).
            // Whether the eaten click was a miss beside the box is UNCONFIRMED; the row-wide target covers it either way.
            var hit = new GameObject("RowHitArea", typeof(RectTransform), typeof(Image)).GetComponent<RectTransform>();
            hit.SetParent(rect, false);
            hit.SetAsFirstSibling();   // drawn first = under the box and tick
            hit.anchorMin = hit.anchorMax = new Vector2(0f, 0.5f);
            hit.pivot = new Vector2(0f, 0.5f);
            hit.sizeDelta = new Vector2(right - rowLeft, cell.sizeDelta.y);
            hit.anchoredPosition = new Vector2(rowLeft - left, 0f);
            var hitImage = hit.GetComponent<Image>();
            hitImage.color = new Color(0f, 0f, 0f, 0f);   // invisible; an Image at alpha 0 still takes raycasts
            hitImage.raycastTarget = true;
            toggle.onValueChanged.AddListener(_ => Refresh());
            TakeRowLabel(toggle);
            return toggle;
        }

        // 0.2.1513: links a control to the label LabelRow just made, for Dim.
        private static void TakeRowLabel(Selectable control)
        {
            if (s_lastLabel != null)
            {
                s_rowLabel[control] = s_lastLabel;
                s_builtInk[s_lastLabel] = s_lastLabel.color;   // [2ic]: usable = exactly the colour as built
            }
            if (control is TMP_Dropdown dropdown && dropdown.captionText != null)
                s_builtInk[dropdown.captionText] = dropdown.captionText.color;   // the seed ink Dropdown() set
            s_lastLabel = null;
        }

        // 0.2.1513 ([lead] 11-21: greyed dependent rows): interactable plus the row label's alpha, so an unavailable row reads as
        // unavailable. The control's stored value is never changed here ([2ic]): turning the parent back on restores the choice.
        private static void Dim(Selectable control, bool usable)
        {
            if (control == null) return;
            control.interactable = usable;
            if (s_rowLabel.TryGetValue(control, out var label)) Shade(label, usable);
            if (control is TMP_Dropdown dropdown) Shade(dropdown.captionText, usable);   // the caption greys with its row
        }

        // Built colour when usable; the built colour at DimAlpha x its own alpha when not.
        private static void Shade(TMP_Text text, bool usable)
        {
            if (text == null || !s_builtInk.TryGetValue(text, out var built)) return;
            if (!usable) built.a *= DimAlpha;
            text.color = built;
        }

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
            s_superFlat?.SetIsOnWithoutNotify(false);   // opt-in per world, always off when the screen opens
            Refresh();
        }

        private static int DefaultTerrainChoice()
        {
            if (!FiresVoxelConfig.LegacyConfigFullVoxel) return HeightmapChoice;   // 0.2.1336: only an old cfg's preselect; the row is the choice
            return (FiresVoxelConfig.WorldMode?.Value ?? VoxelWorldMode.VoxelWrap) == VoxelWorldMode.VoxelOnly ? FullVoxelChoice : VoxelWrapChoice;
        }

        private static FiresWorldProfile ChosenProfile()
        {
            if (s_superFlat != null && s_superFlat.isOn)
            {
                // Stock heightmap ground (the flat height comes from FatSuperFlat), none of the extras, 25% of the vanilla size.
                // Built by FiresWorldProfile.NewSuperFlat, the same profile the unattended -fat-new-superflat path makes.
                // The Terrain row + the Sky islands / Caves rows are kept (voxel choices only)
                return FiresWorldProfile.NewSuperFlat("created", TerrainByChoice[Mathf.Clamp(s_terrain.value, 0, TerrainByChoice.Length - 1)], s_skyIslands.isOn, s_caves.isOn);
            }
            return ChosenFromRows();
        }

        private static FiresWorldProfile ChosenFromRows() => new FiresWorldProfile
        {
            SkySites2 = true,   // 0.2.1102: a world made on the New World screen takes the new skyland site rule
            SkySites3 = true,   // 0.2.1137: and the pond-aware / mini site rule
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
            // 0.2.1513 ([lead] 11-21, shot 08): the auto-water row stays in place, greyed while Sky islands is off (was hidden,
            // leaving a gap). Its value is kept; ChosenFromRows reads it only when Sky islands is on (SkyIslandWater below).
            bool islands = s_skyIslands.isOn;
            // Super flat decides every other row, so they stop taking input while it is on
            bool flat = s_superFlat != null && s_superFlat.isOn;
            // 0.2.1513: Gaea mode's generator lock (ApplyGaeaLayout) is kept here too; this line used to undo it (!flat only).
            Dim(s_generator, !flat && !s_gaea);   // Super flat keeps the Terrain row (heightmap / voxel wrap / full voxel)
            // 0.2.1513 ([lead] 11-21, shot 09: "never opens" looked broken): while Super flat locks it, the caption says so.
            if (s_generator.captionText != null)
            {
                if (flat) s_generator.captionText.text = GeneratorLockedCaption;
                else s_generator.RefreshShownValue();
            }
            Dim(s_oneZoneAtATime, !flat);
            Dim(s_rivers, !flat);   // Sky islands + Caves stay choosable on super flat (voxel terrain only); rivers never
            Dim(s_islandWater, islands && !flat);
            var profile = ChosenProfile();
            s_conflict = profile.Conflict() ?? (s_gaea && s_gaeaFolder != null ? GaeaProblem() : null);
            s_status.color = s_conflict != null ? ConflictInk : NoteInk;
            s_status.text = s_conflict ?? LocalNote(profile) ?? (s_gaea ? GaeaNote : "");
        }

        // What this installation's own config switches off for the chosen world; the world keeps its choices elsewhere.
        private static string LocalNote(FiresWorldProfile profile)
        {
            if (profile.SuperFlat)
                return profile.Terrain == ProfileTerrain.Heightmap
                    ? "Super flat: flat Meadows at y 50, 2.5 km radius, the start temple + the Mausoleum only, no plants or spawns. Sky islands and caves need voxel terrain."
                    : $"Super flat ({(profile.Terrain == ProfileTerrain.VoxelOnly ? "full voxel" : "voxel wrap")}): flat Meadows at y 50, 2.5 km radius, the start temple + the Mausoleum only; sky islands {(profile.SkyIslands == true ? "on" : "off")}, caves {(profile.Caves == true ? "on" : "off")}.";
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
