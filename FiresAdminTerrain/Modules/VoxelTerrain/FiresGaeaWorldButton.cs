using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace VerdantsAscent.Terrain.Voxel
{
    // The start panel's "New from Gaea" button: a copy of its Start/Join tab buttons, right of Join Game along the top
    // (Fire, 2026-09-27: "next to the start/join tabs, theres room there"). It is a plain button, not a third tab: its
    // "Selected" look stays off and TabHandler never knows it. It hangs off the start panel itself, not the tab row, so the
    // row's layout and any clipping leave it alone, and it is placed from Join Game's corners as drawn, after a layout pass,
    // each time the panel opens (0.2.594 read the tab's position before its row was laid out and landed out of sight). It
    // opens New World in Gaea mode (FiresNewWorldOptions.OpenForGaea) and exists only while World Gen Studio is hosted
    // here: FDT hosts it behind Core's WorldGenStudioBridge (FiresGaeaStudio.Available). Tools\GAEA_WORLD_BUTTON.md.
    [HarmonyPatch]
    internal static class FiresGaeaWorldButton
    {
        private const string ButtonName = "FiresNewFromGaea";
        private const string Caption = "New from Gaea";
        private const string SelectedChild = "Selected";
        private const float Gap = 8f;
        private const float MinimumWidthShare = 0.5f;
        private const float MinimumFontSize = 12f;

        private static readonly Vector3[] s_corners = new Vector3[4];
        private static Button s_button;
        private static RectTransform s_joinTab;
        private static RectTransform s_panel;
        private static RectTransform s_rightEdge;
        private static bool s_searched;
        private static bool s_placementLogged;

        [HarmonyPatch(typeof(FejdStartup), nameof(FejdStartup.ShowStartGame))]
        [HarmonyPostfix]
        private static void ShowWhenStudioInstalled(FejdStartup __instance)
        {
            if (s_button == null && !s_searched) Build(__instance);
            if (s_button == null) return;
            bool installed = FiresGaeaStudio.Available;
            s_button.gameObject.SetActive(installed);
            if (installed) Place();
        }

        private static void Build(FejdStartup fejd)
        {
            s_searched = true;
            Canvas.ForceUpdateCanvases();
            var panel = fejd.m_startGamePanel;
            var join = RightmostTab(StartTabs(fejd));
            if (panel == null || join == null)
            {
                FiresLogger.LogWarning("[FiresWorldGen] The start panel's Start/Join tabs were not found; the New from Gaea button is not shown.");
                return;
            }
            s_panel = (RectTransform)panel.transform;
            s_joinTab = (RectTransform)join.transform;
            s_rightEdge = fejd.m_worldListPanel != null ? (RectTransform)fejd.m_worldListPanel.transform : s_panel;
            var clone = Object.Instantiate(join.gameObject, s_panel);
            clone.name = ButtonName;
            foreach (var localize in clone.GetComponentsInChildren<Localize>(true)) Object.Destroy(localize);
            foreach (var text in clone.GetComponentsInChildren<TMP_Text>(true))
            {
                text.text = Caption;
                text.fontSizeMax = text.fontSize;
                text.fontSizeMin = Mathf.Min(MinimumFontSize, text.fontSize);
                text.enableAutoSizing = true;
            }
            clone.transform.Find(SelectedChild)?.gameObject.SetActive(false);
            var layout = clone.GetComponent<LayoutElement>() ?? clone.AddComponent<LayoutElement>();
            layout.ignoreLayout = true;
            var rect = (RectTransform)clone.transform;
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            s_button = clone.GetComponent<Button>();
            s_button.onClick = new Button.ButtonClickedEvent();
            s_button.onClick.AddListener(() => FiresNewWorldOptions.OpenForGaea(fejd));
        }

        // Join Game's drawn corners, after a layout pass, decide where the button goes: the same height and row, a gap to
        // its right, and no wider than what is left before the world list's right edge, which is the panel's inner edge.
        private static void Place()
        {
            Canvas.ForceUpdateCanvases();
            s_joinTab.GetWorldCorners(s_corners);
            Vector3 joinLeftBottom = s_panel.InverseTransformPoint(s_corners[0]);
            Vector3 joinRightTop = s_panel.InverseTransformPoint(s_corners[2]);
            float width = joinRightTop.x - joinLeftBottom.x;
            float height = joinRightTop.y - joinLeftBottom.y;
            s_rightEdge.GetWorldCorners(s_corners);
            float panelRight = s_panel.InverseTransformPoint(s_corners[2]).x;
            float room = panelRight - joinRightTop.x - Gap * 2f;
            float buttonWidth = Mathf.Clamp(room, width * MinimumWidthShare, width);
            var rect = (RectTransform)s_button.transform;
            rect.sizeDelta = new Vector2(buttonWidth, height);
            rect.localPosition = new Vector3(joinRightTop.x + Gap + buttonWidth * 0.5f, (joinLeftBottom.y + joinRightTop.y) * 0.5f, 0f);
            rect.SetAsLastSibling();
            if (s_placementLogged) return;
            s_placementLogged = true;
            FiresLogger.LogInfo($"[FiresWorldGen] New from Gaea sits right of '{s_joinTab.name}' on the start panel: button "
                + $"x {rect.localPosition.x - buttonWidth * 0.5f:0}..{rect.localPosition.x + buttonWidth * 0.5f:0}, "
                + $"y {rect.localPosition.y:0} (Join x {joinLeftBottom.x:0}..{joinRightTop.x:0}, right edge {panelRight:0} from '{s_rightEdge.name}').");
        }

        // Vanilla reaches the Start/Join tabs as the start panel's first child; any TabHandler under the panel is the fallback.
        private static TabHandler StartTabs(FejdStartup fejd)
        {
            var panel = fejd.m_startGamePanel;
            if (panel == null) return null;
            var first = panel.transform.childCount > 0 ? panel.transform.GetChild(0).GetComponent<TabHandler>() : null;
            return first != null ? first : panel.GetComponentInChildren<TabHandler>(true);
        }

        private static Button RightmostTab(TabHandler tabs)
        {
            if (tabs == null) return null;
            Button rightmost = null;
            foreach (var tab in tabs.m_tabs)
                if (tab.m_button != null && (rightmost == null || tab.m_button.transform.position.x > rightmost.transform.position.x))
                    rightmost = tab.m_button;
            return rightmost;
        }
    }
}
