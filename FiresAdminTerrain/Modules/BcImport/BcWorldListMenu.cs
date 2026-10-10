using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;
using FiresCore.UI.ContextMenu;
using UnityEngine.Events;
using UnityEngine.UI;
using VerdantsAscent.Terrain.Voxel;

namespace VerdantsAscent.Terrain.BcImport
{
    // One row on Select World's ONE right-click menu, beside FiresWorldMenu's. This used to install its own
    // IPointerClickHandler on every world row, which is why right-clicking a world could open either this dialog
    // or the clone menu depending on which handler won - two paths to the same job, each missing what the other
    // offered. The convert dialog now carries every clone option, so there is one menu and one dialog.
    // Spec: Docs/BETTERCONTINENTS_IMPORT.md
    internal static class BcWorldListMenu
    {
        private const string Owner = "FiresAdminTerrain.BcImport";

        internal static void Register() => WorldListContextMenu.Register(Owner, RowsFor);

        private static IEnumerable<ContextMenuItem> RowsFor(World world)
        {
            if (world == null) yield break;
            string name = string.IsNullOrEmpty(world.m_worldName) ? world.m_name : world.m_worldName;
            // ONE row, not two. "Clone..." and "Convert to Valheim 1.0..." were separate entries that both
            // cloned the world and both offered the same terrain options, and between them a normal world
            // could be refused by BOTH: Convert wanted BetterContinents data the world did not have, and
            // Clone greyed out for its own reasons. So the menu offered a world two ways to do nothing.
            //
            // This row takes every world. BetterContinents data is an EXTRA the converter carries across
            // when it is there, not a precondition for using it - the clone, the 1.0 save format and the
            // terrain options are the same work either way. Only a world the game itself cannot read is
            // refused, and "already converted" is kept because re-converting one is genuinely a no-op.
            string refusal = world.m_dataError != World.SaveDataError.None
                ? "This world could not be read."
                : BcWorldData.Exists(name, out BcSaveLocation existing)
                    ? $"'{name}' is already converted ({existing.Path})."
                    : null;
            var row = ContextMenuItem.Row("Convert...", () => BcConvertDialog.Open(world), refusal == null);
            row.Tooltip = refusal;
            yield return row;
        }
    }

    // Vanilla's own RemoveWorldDialog, cloned and refilled, so this reads as part of the game rather than as a mod
    // panel painted next to it. Nothing here builds a frame, a button, a field or a checkbox: every one of those is
    // an Instantiate of the object the menu is already using on this screen, which is also why the text is legible -
    // it keeps whatever colour, font and size the game gives it.
    //
    // Structure read from Assets/Scenes/start.unity in the 1.0 rip, not guessed:
    //   m_removeWorldDialog "RemoveWorldDialog" -> "Dialog" -> bkg, topic, name, ButtonYes(Text), ButtonNo(Text)
    //   m_newWorldName      "NameField"         -> "Text Area" -> Placeholder, Text
    //   m_publicServerToggle "PublicGameToggle" -> Background(Checkmark), Label, gamepad_hint
    // Spec: Docs/BETTERCONTINENTS_IMPORT.md
    internal class BcConvertDialog : MonoBehaviour
    {
        private const string RootName = "FAT_BcConvertDialog";
        private const string DialogPath = "Dialog";
        private const string TopicPath = "Dialog/topic";
        private const string BodyPath = "Dialog/name";
        private const string YesPath = "Dialog/ButtonYes";
        private const string NoPath = "Dialog/ButtonNo";
        private const string BackgroundName = "bkg";
        private const string GamepadHintName = "gamepad_hint";

        // Vanilla's RemoveWorldDialog is sized for one short line, so the clone is grown and then laid out from
        // scratch: every child is positioned here rather than kept where the prefab had it. SideMargin is the
        // left/right padding everything stays inside of.
        private const float ExtraWidth = 460f;
        private const float SideMargin = 44f;
        private const float TopicHeight = 56f;
        private const float MinBodyHeight = 60f;
        private const float RowsGap = 18f;
        private const float ButtonGap = 34f;
        private const int ControlRows = 5;
        private const float BodyGap = 10f;
        private const float RowHeight = 34f;
        private const float RowGap = 10f;
        private const float LabelWidth = 190f;
        private const float ColumnGap = 24f;
        private const float StatusHeight = 72f;
        private const int MaxWorldNameLength = 48;

        private static BcConvertDialog _open;

        private string _sourceName;
        private GameObject _root;
        private TMP_InputField _cloneName;
        private TMP_Text _status;
        private Toggle _voxel, _fullVoxel, _caves, _skyIslands, _skyWater, _hydrology, _oneZone;

        public static void Open(World world)
        {
            Close();
            var fejd = FejdStartup.instance;
            if (fejd == null || fejd.m_removeWorldDialog == null)
            {
                FiresLogger.LogError("[BcImport] the menu has no RemoveWorldDialog to build the convert dialog from; "
                    + "the convert dialog cannot open.");
                return;
            }

            var host = new GameObject(RootName);
            _open = host.AddComponent<BcConvertDialog>();
            _open._sourceName = string.IsNullOrEmpty(world.m_worldName) ? world.m_name : world.m_worldName;
            if (!_open.Build(fejd)) Close();
        }

        public static void Close()
        {
            if (_open == null) return;
            if (_open._root != null) Destroy(_open._root);
            Destroy(_open.gameObject);
            _open = null;
        }

        private bool Build(FejdStartup fejd)
        {
            GameObject template = fejd.m_removeWorldDialog;
            _root = Instantiate(template, template.transform.parent);
            _root.name = RootName;
            _root.SetActive(true);

            var dialog = _root.transform.Find(DialogPath) as RectTransform;
            var topic = Find<TMP_Text>(TopicPath);
            var body = Find<TMP_Text>(BodyPath);
            var yes = Find<Button>(YesPath);
            var no = Find<Button>(NoPath);
            if (dialog == null || topic == null || body == null || yes == null || no == null)
            {
                FiresLogger.LogError($"[BcImport] vanilla's RemoveWorldDialog is not laid out as expected "
                    + $"({DialogPath}/{TopicPath}/{BodyPath}/{YesPath}/{NoPath}); the convert dialog cannot open.");
                return false;
            }

            // The BetterContinents line is now an EXTRA the world may or may not have, not the reason the dialog
            // exists. A plain world - pre-1.0 or post-1.0 - gets the same clone, the same 1.0 save format and the
            // same terrain options, so it gets the same dialog with that one line left out rather than a refusal
            // where the explanation used to be.
            bool hasBc = BcSaveLocator.TryLocate(_sourceName, out BcSaveLocation source);

            // Every readable world can convert now; the only thing that makes it pointless is a world that
            // already IS converted. The row greys that case out, so this is normally true - it stays computed
            // rather than hardcoded so opening the dialog directly still shows the right buttons.
            bool canConvert = !BcWorldData.Exists(_sourceName, out BcSaveLocation already);
            if (!canConvert)
                source = already;

            topic.text = "Convert";
            body.text = !canConvert
                ? $"'{_sourceName}' is already converted ({source.Path})."
                : (hasBc ? $"'{_sourceName}' carries BetterContinents data:\n{source.Path}\n\n" : string.Empty)
                    + "Converting CLONES the world first and converts the copy. This original is never modified - "
                    + "open it afterwards and it is exactly what it was. Items from mods that are not installed now "
                    + "do not carry into the copy (Valheim drops them as it loads it); the original keeps them.\n\n"
                    + (hasBc
                        ? "With nothing ticked below, the copy is the same map on stock Valheim terrain."
                        : "With nothing ticked below, the copy is the same world in the Valheim 1.0 save format.");

            // The panel is sized to its CONTENT, not grown by a guess. The body is the variable part - a long save
            // path wraps to three lines on one world and one line on another - so it is measured at the width it
            // will actually be drawn at, and everything below it is laid out from that. A fixed +260 is what left
            // the text overlapping the rows.
            float width = dialog.rect.width + ExtraWidth;
            float inner = width - SideMargin * 2f;
            Fit(topic, TextAlignmentOptions.Center);
            Fit(body, TextAlignmentOptions.TopLeft);
            float bodyHeight = Mathf.Max(MinBodyHeight, body.GetPreferredValues(body.text, inner, 0f).y);
            float buttonBand = ((RectTransform)yes.transform).rect.height + ButtonGap;

            int rows = canConvert ? ControlRows : 0;
            float rowsHeight = rows > 0 ? rows * RowHeight + (rows - 1) * RowGap + RowsGap * 2f + StatusHeight : 0f;
            float height = SideMargin + TopicHeight + BodyGap + bodyHeight + rowsHeight + buttonBand + SideMargin;

            dialog.sizeDelta = new Vector2(width, height);
            Stretch(dialog.Find(BackgroundName) as RectTransform);

            float top = height * 0.5f - SideMargin;
            Place(topic.rectTransform, 0f, top - TopicHeight * 0.5f, inner, TopicHeight);

            float bodyTop = top - TopicHeight - BodyGap;
            Place(body.rectTransform, 0f, bodyTop - bodyHeight * 0.5f, inner, bodyHeight);

            Rewire(yes, canConvert ? "Clone and convert" : "Close", canConvert ? (UnityAction)Convert : Close);
            Rewire(no, "Close", Close);
            no.gameObject.SetActive(canConvert);
            float buttonY = -height * 0.5f + buttonBand * 0.5f;
            MoveTo((RectTransform)yes.transform, inner * 0.25f, buttonY);
            MoveTo((RectTransform)no.transform, -inner * 0.25f, buttonY);

            if (canConvert) BuildRows(fejd, dialog, bodyTop - bodyHeight - RowsGap, inner);
            return true;
        }

        // Every choice the world-list's own "Clone as ..." rows offered, on the one dialog, so a converted world
        // carries everything the original had instead of the caller picking a path up front and losing the rest.
        // Terrain is two exclusive ticks rather than a dropdown because the dialog is built from vanilla objects
        // and vanilla has no dropdown on this screen to clone.
        private void BuildRows(FejdStartup fejd, RectTransform dialog, float top, float inner)
        {
            float fieldWidth = inner - LabelWidth - ColumnGap;
            float fieldX = (inner - fieldWidth) * 0.5f;
            float labelX = -(inner - LabelWidth) * 0.5f;
            float columnWidth = (inner - ColumnGap) * 0.5f;
            float columnX = (columnWidth + ColumnGap) * 0.5f;
            int row = 0;

            _cloneName = CloneField(fejd, dialog, RowY(top, row), fieldX, fieldWidth);
            if (_cloneName != null) _cloneName.text = BcConvert.ProposeCloneName(_sourceName);
            LabelFor(fejd, dialog, "New world name", RowY(top, row++), labelX);

            _voxel = CloneToggle(fejd, dialog, "Voxel terrain (wrap)", RowY(top, row), -columnX, columnWidth);
            _fullVoxel = CloneToggle(fejd, dialog, "Full voxel terrain", RowY(top, row++), columnX, columnWidth);
            _caves = CloneToggle(fejd, dialog, "Caves", RowY(top, row), -columnX, columnWidth);
            _skyIslands = CloneToggle(fejd, dialog, "Sky islands", RowY(top, row++), columnX, columnWidth);
            _hydrology = CloneToggle(fejd, dialog, "Rivers and lakes", RowY(top, row), -columnX, columnWidth);
            // 0.2.727 (Fire, 10-05): New World's dependent box, here too, right under Sky islands: shown only while Sky
            // islands is ticked, ticked by default. Off = the copy's islands are born dry (no ponds, rivers, falls or water).
            _skyWater = CloneToggle(fejd, dialog, "Auto-place water on sky islands", RowY(top, row++), columnX, columnWidth);
            _oneZone = CloneToggle(fejd, dialog, "One zone at a time", RowY(top, row++), -columnX, columnWidth);
            if (_skyWater != null)
            {
                _skyWater.isOn = true;
                bool skyOn = _skyIslands != null && _skyIslands.isOn;
                _skyWater.gameObject.SetActive(skyOn);
                _skyWater.interactable = skyOn;
                if (_skyIslands != null)
                    _skyIslands.onValueChanged.AddListener(on => { _skyWater.gameObject.SetActive(on); _skyWater.interactable = on; });
            }

            OnlyOneTerrain(_voxel, _fullVoxel);
            OnlyOneTerrain(_fullVoxel, _voxel);
            if (_fullVoxel != null)
                _fullVoxel.onValueChanged.AddListener(on => Say(on
                    ? "Full voxel CONVERTS the map into a voxel field once, at the resolution it was made at. That "
                        + "takes a while and reports progress here; the copy is voxel-native afterwards."
                    : string.Empty));

            _status = CloneText(fejd, dialog, string.Empty,
                RowY(top, row) - (StatusHeight - RowHeight) * 0.5f, inner, StatusHeight);
            if (_status != null) Fit(_status, TextAlignmentOptions.TopLeft);
        }

        /// <summary>Wrap and full voxel are one choice, not two: ticking either clears the other.</summary>
        private static void OnlyOneTerrain(Toggle picked, Toggle other)
        {
            if (picked == null || other == null) return;
            picked.onValueChanged.AddListener(on => { if (on) other.SetIsOnWithoutNotify(false); });
        }

        // UnityEvent.RemoveAllListeners() removes RUNTIME listeners ONLY. A listener wired in the scene is
        // PERSISTENT and survives it, so a cloned vanilla control keeps doing vanilla's job as well as ours.
        // This cost a world: the cloned ButtonYes still carried FejdStartup.OnButtonRemoveWorldYes, so every
        // "Clone and convert" click also ran World.RemoveWorld -> SaveSystem.Delete on the selected world and
        // deleted its .db and .fwl. Persistent calls can only be switched off one by one, by index.
        private static void Silence(UnityEventBase raised)
        {
            if (raised == null) return;
            for (int index = 0; index < raised.GetPersistentEventCount(); index++)
                raised.SetPersistentListenerState(index, UnityEventCallState.Off);
        }

        private void Say(string text)
        {
            if (_status != null) _status.text = text;
        }

        private static float RowY(float top, int row) => top - row * (RowHeight + RowGap) - RowHeight * 0.5f;

        /// <summary>Text that wraps inside its rect instead of running off the edge of the panel. Vanilla's own
        /// labels are single short lines, so the clones arrive set to overflow on one line - which is what cut
        /// "carries BetterContinents data" off mid-word.</summary>
        private static void Fit(TMP_Text text, TextAlignmentOptions alignment)
        {
            if (text == null) return;
            text.enableWordWrapping = true;
            text.overflowMode = TextOverflowModes.Overflow;
            text.alignment = alignment;
        }

        private static void Stretch(RectTransform rect)
        {
            if (rect == null) return;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        /// <summary>Moves a vanilla control without touching the size the prefab gave it.</summary>
        private static void MoveTo(RectTransform rect, float x, float y)
        {
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = new Vector2(x, y);
        }

        // Vanilla's New World field is serialised CharacterValidation.Alphanumeric with a 20 character limit, which
        // silently swallows the spaces and apostrophes a world name is allowed to have - "Verdant's Ascent" cannot be
        // typed into it. A converted world is named by whoever converts it, so the clone takes none of that.
        private TMP_InputField CloneField(FejdStartup fejd, RectTransform dialog, float y, float x, float width)
        {
            if (fejd.m_newWorldName == null) return null;
            var field = Instantiate(fejd.m_newWorldName.gameObject, dialog).GetComponent<TMP_InputField>();
            Silence(field.onValueChanged);
            Silence(field.onEndEdit);
            Silence(field.onSubmit);
            Silence(field.onSelect);
            Silence(field.onDeselect);
            field.onValueChanged.RemoveAllListeners();
            field.onEndEdit.RemoveAllListeners();
            field.onSubmit.RemoveAllListeners();
            field.onValidateInput = null;
            field.contentType = TMP_InputField.ContentType.Standard;
            field.characterValidation = TMP_InputField.CharacterValidation.None;
            field.inputValidator = null;
            field.characterLimit = MaxWorldNameLength;
            Place((RectTransform)field.transform, x, y, width, RowHeight);
            return field;
        }

        private Toggle CloneToggle(FejdStartup fejd, RectTransform dialog, string caption, float y, float x, float width)
        {
            if (fejd.m_publicServerToggle == null) return null;
            var toggle = Instantiate(fejd.m_publicServerToggle.gameObject, dialog).GetComponent<Toggle>();
            Silence(toggle.onValueChanged);
            toggle.onValueChanged.RemoveAllListeners();
            toggle.isOn = false;
            toggle.interactable = true;
            var hint = toggle.transform.Find(GamepadHintName);
            if (hint != null) Destroy(hint.gameObject);
            var label = toggle.GetComponentInChildren<TMP_Text>(true);
            if (label != null) label.text = caption;
            Place((RectTransform)toggle.transform, x, y, width, RowHeight);
            return toggle;
        }

        private TMP_Text LabelFor(FejdStartup fejd, RectTransform dialog, string text, float y, float x)
            => CloneText(fejd, dialog, text, y, LabelWidth, RowHeight, x);

        private TMP_Text CloneText(FejdStartup fejd, RectTransform dialog, string text, float y, float width, float height, float x = 0f)
        {
            TMP_Text source = fejd.m_worldSourceInfo;
            if (source == null) return null;
            var label = Instantiate(source.gameObject, dialog).GetComponent<TMP_Text>();
            label.text = text;
            label.raycastTarget = false;
            Place((RectTransform)label.transform, x, y, width, height);
            return label;
        }

        private static void Place(RectTransform rect, float x, float y, float width, float height)
        {
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(width, height);
            rect.anchoredPosition = new Vector2(x, y);
        }

        // Vanilla's buttons carry their own listeners (OnButtonRemoveWorldYes/No) and a gamepad hint that would
        // promise the wrong key, so both go before ours are attached.
        private static void Rewire(Button button, string caption, UnityAction action)
        {
            Silence(button.onClick);
            button.onClick.RemoveAllListeners();
            button.onClick.AddListener(action);
            var label = button.GetComponentInChildren<TMP_Text>(true);
            if (label != null) label.text = caption;
            var hint = button.transform.Find(GamepadHintName);
            if (hint != null) Destroy(hint.gameObject);
        }

        private T Find<T>(string path) where T : Component
        {
            var found = _root.transform.Find(path);
            return found != null ? found.GetComponent<T>() : null;
        }

        /// <summary>Every field is stated, never left null: a null field falls through to this installation's own
        /// voxel config, which has no say over a map that already exists.</summary>
        private WorldCloneOptions ChosenOptions()
        {
            WorldCloneOptions options = BcConvert.VanillaTerrain();
            if (_voxel != null && _voxel.isOn) options.Terrain = ProfileTerrain.VoxelWrap;
            if (_caves != null) options.Caves = _caves.isOn;
            if (_skyIslands != null) options.SkyIslands = _skyIslands.isOn;
            // Stated only with sky islands on: water on writes skyislandwater=true, off writes false (the islands born dry).
            if (_skyIslands != null && _skyIslands.isOn && _skyWater != null) options.SkyIslandWater = _skyWater.isOn;
            if (_hydrology != null) options.Hydrology = _hydrology.isOn;
            if (_oneZone != null) options.ChunkByChunk = _oneZone.isOn;
            return options;
        }

        // Full voxel is earned, not asserted. The copy is made as voxel WRAP first, which is valid whatever the
        // source is, and only once the bake has actually written a voxel field is the profile rewritten to say so.
        // A bake that fails therefore leaves a world that loads correctly instead of one claiming a field it has
        // no cache for, which would generate procedurally and look nothing like the map.
        private void Convert()
        {
            string cloneName = (_cloneName != null ? _cloneName.text : string.Empty).Trim();
            bool toVoxel = _fullVoxel != null && _fullVoxel.isOn;
            WorldCloneOptions options = ChosenOptions();
            if (toVoxel) options.Terrain = ProfileTerrain.VoxelWrap;

            Say($"Cloning '{_sourceName}' as '{cloneName}'...");
            bool converted = BcConvert.TryConvert(_sourceName, cloneName, options, out string report);
            Say(report);
            FiresLogger.LogInfo($"[BcImport] {report}");
            if (!converted) return;

            ShowAndSelect(cloneName);
            if (toVoxel) StartCoroutine(BakeToVoxel(cloneName, report));
        }

        private static readonly AccessTools.FieldRef<FejdStartup, World> SelectedWorldRef =
            AccessTools.FieldRefAccess<FejdStartup, World>("m_world");

        // The new world was missing from Select World until the menu was left and re-entered (Fire, 2026-09-27): the save
        // cache was dropped but vanilla's list was never rebuilt. Rebuild it now with the copy selected, so Start plays it.
        private static void ShowAndSelect(string worldName)
        {
            SaveSystem.InvalidateCache(SaveDataType.World);
            var fejd = FejdStartup.instance;
            if (fejd == null) return;
            List<World> worlds = SaveSystem.GetWorldList();
            World made = worlds?.Find(world => world != null && (world.m_worldName == worldName || world.m_name == worldName));
            if (made != null) SelectedWorldRef(fejd) = made;
            WorldListContextMenu.RefreshWorldList();
            FiresLogger.LogInfo($"[BcImport] world list refreshed; '{worldName}' "
                + (made != null ? "is selected, so Start plays it." : "was not found in the list to select."));
        }

        private IEnumerator BakeToVoxel(string cloneName, string cloneReport)
        {
            if (!FiresWorldBake.TryFindWorld(cloneName, out World clone))
            {
                Say($"{cloneReport} The copy could not be found to convert its terrain to voxel, so it stays as "
                    + "voxel wrap - which keeps the same map.");
                yield break;
            }

            bool ok = false;
            string outcome = null;
            yield return FiresWorldBake.Run(clone, Say, (success, message) => { ok = success; outcome = message; });

            if (!ok)
            {
                Say($"{outcome} '{cloneName}' is still voxel wrap, which keeps the same map.");
                FiresLogger.LogWarning($"[BcImport] {outcome}");
                yield break;
            }

            var profile = FiresWorldProfile.Read(cloneName) ?? FiresWorldClone.OriginalProfile(cloneName);
            profile.Generator = ProfileGenerator.Fires;
            profile.Terrain = ProfileTerrain.VoxelOnly;
            profile.Write(cloneName);
            Say($"{outcome} '{cloneName}' is now a full voxel world.");
            FiresLogger.LogInfo($"[BcImport] {outcome} '{cloneName}' profiled as full voxel.");
        }
    }
}
