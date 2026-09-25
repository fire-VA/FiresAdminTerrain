using System.Collections.Generic;
using FiresCore.UI;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace VerdantsAscent.Terrain.BcImport
{
    // Right-click a world in the world list to convert a BetterContinents world to 1.0. Conversion clones first,
    // so the row you right-clicked is never modified. Built with Core's UIBuilderHelper.
    // Spec: Docs/BETTERCONTINENTS_IMPORT.md
    [HarmonyPatch(typeof(FejdStartup), "UpdateWorldList")]
    internal static class Patch_FejdStartup_UpdateWorldList_BcImport
    {
        private static readonly AccessTools.FieldRef<FejdStartup, List<World>> WorldsRef =
            AccessTools.FieldRefAccess<FejdStartup, List<World>>("m_worlds");
        private static readonly AccessTools.FieldRef<FejdStartup, List<GameObject>> RowsRef =
            AccessTools.FieldRefAccess<FejdStartup, List<GameObject>>("m_worldListElements");

        private static void Postfix(FejdStartup __instance)
        {
            List<World> worlds = WorldsRef(__instance);
            List<GameObject> rows = RowsRef(__instance);
            if (worlds == null || rows == null) return;

            for (int index = 0; index < rows.Count; index++)
            {
                BcWorldRowMenu menu = rows[index].GetComponent<BcWorldRowMenu>() ?? rows[index].AddComponent<BcWorldRowMenu>();
                menu.World = index < worlds.Count ? worlds[index] : null;
            }
        }
    }

    internal class BcWorldRowMenu : MonoBehaviour, IPointerClickHandler
    {
        public World World;

        public void OnPointerClick(PointerEventData eventData)
        {
            if (eventData == null || eventData.button != PointerEventData.InputButton.Right) return;
            if (World == null) return;
            BcConvertDialog.Open(World);
        }
    }

    internal class BcConvertDialog : MonoBehaviour
    {
        private const string RootName = "FAT_BcConvertDialog";
        private const int SortOrder = 7200;
        private const int TitleFontSize = 20;
        private const int BodyFontSize = 14;
        private const int StatusFontSize = 13;

        private static BcConvertDialog _open;

        private string _sourceName;
        private GameObject _canvasRoot;
        private TMP_InputField _cloneName;
        private TextMeshProUGUI _status;

        public static void Open(World world)
        {
            Close();
            string sourceName = string.IsNullOrEmpty(world.m_worldName) ? world.m_name : world.m_worldName;

            var host = new GameObject(RootName);
            _open = host.AddComponent<BcConvertDialog>();
            _open._sourceName = sourceName;
            _open.Build(world);
        }

        public static void Close()
        {
            if (_open == null) return;
            if (_open._canvasRoot != null) Destroy(_open._canvasRoot);
            Destroy(_open.gameObject);
            _open = null;
        }

        private void Build(World world)
        {
            _canvasRoot = new GameObject(RootName + "_Canvas");
            Canvas canvas = _canvasRoot.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = SortOrder;
            CanvasScaler scaler = _canvasRoot.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            _canvasRoot.AddComponent<GraphicRaycaster>();

            GameObject panel = UIBuilderHelper.CreatePanel(_canvasRoot.transform, "Panel",
                new Vector2(0.3f, 0.32f), new Vector2(0.7f, 0.68f), new Color(0.06f, 0.05f, 0.04f, 0.97f));
            var panelRect = panel.GetComponent<RectTransform>();

            UIBuilderHelper.CreateLabel(panelRect, "Title", "Convert to Valheim 1.0", TitleFontSize,
                new Color(0.95f, 0.8f, 0.45f), new Vector2(0f, 0.86f), new Vector2(1f, 0.98f),
                TextAlignmentOptions.Center);

            bool canConvert = BcConvert.CanConvert(_sourceName, out BcSaveLocation source, out string reason);
            string body = canConvert
                ? $"'{_sourceName}' carries BetterContinents data:\n{source.Path}\n\n"
                    + "Converting CLONES the world first and converts the copy. This original is never modified - "
                    + "open it afterwards and it is exactly what it was."
                : reason;

            UIBuilderHelper.CreateLabel(panelRect, "Body", body, BodyFontSize, new Color(0.85f, 0.75f, 0.55f),
                new Vector2(0.05f, 0.42f), new Vector2(0.95f, 0.84f), TextAlignmentOptions.TopLeft);

            if (canConvert)
            {
                UIBuilderHelper.CreateLabel(panelRect, "NameLabel", "New world name", BodyFontSize,
                    new Color(0.85f, 0.75f, 0.55f), new Vector2(0.05f, 0.32f), new Vector2(0.4f, 0.4f),
                    TextAlignmentOptions.Left);
                _cloneName = UIBuilderHelper.CreateInputField(panelRect, "CloneName", "world name",
                    new Vector2(0.4f, 0.32f), new Vector2(0.95f, 0.4f));
                _cloneName.text = BcConvert.ProposeCloneName(_sourceName);

                UIBuilderHelper.CreateButton(panelRect, "Clone and convert",
                    new Vector2(0.05f, 0.04f), new Vector2(0.45f, 0.14f), Convert);
            }

            _status = UIBuilderHelper.CreateLabel(panelRect, "Status", string.Empty, StatusFontSize,
                new Color(0.6f, 0.5f, 0.35f), new Vector2(0.05f, 0.16f), new Vector2(0.95f, 0.3f),
                TextAlignmentOptions.TopLeft);

            UIBuilderHelper.CreateButton(panelRect, "Close", new Vector2(0.55f, 0.04f), new Vector2(0.95f, 0.14f), Close);
        }

        private void Convert()
        {
            string cloneName = (_cloneName?.text ?? string.Empty).Trim();
            _status.text = $"Cloning '{_sourceName}' as '{cloneName}'...";

            bool converted = BcConvert.TryConvert(_sourceName, cloneName, out string report);
            _status.text = report;
            FiresLogger.LogInfo($"[BcImport] {report}");
            if (converted) SaveSystem.InvalidateCache(SaveDataType.World);
        }
    }
}
