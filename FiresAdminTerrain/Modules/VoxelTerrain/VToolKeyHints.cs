using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // ── V-tool keys in vanilla's key hints (0.2.1057; Fire's Voxheim pick 6, our own code) ──────────────────────────────────
    // While a V-tool with listed keys is the selected build piece and vanilla shows its build hints, one extra line sits just
    // above them with that tool's keys, in the hints' own font and size. Any other moment it is hidden. Text only: vanilla's
    // hint rows and their layout are not touched.
    internal static class VToolKeyHints
    {
        private static readonly Dictionary<string, string> s_keys = new Dictionary<string, string>
        {
            { "Vramp", "Click: lay ramp   Wheel: width   Shift+Wheel: rise   Q/E: turn   Numpad 7/9: undo/redo" },
            { "Vriver", "Click: start   Shift+Click: spine point   Click: end   Wheel: width   Shift+Wheel: depth   Numpad 7/9: undo/redo   Esc: cancel" },
        };

        private static TextMeshProUGUI s_line;
        private static readonly HashSet<string> s_logged = new HashSet<string>();

        [HarmonyPatch(typeof(KeyHints), "UpdateHints")]
        private static class KeyHints_UpdateHints
        {
            private static void Postfix(KeyHints __instance)
            {
                try
                {
                    var player = Player.m_localPlayer;
                    var piece = player != null && player.InPlaceMode() ? player.m_buildPieces?.GetSelectedPiece() : null;
                    string keys = null;
                    bool want = piece != null && __instance.m_buildHints != null && __instance.m_buildHints.activeSelf
                        && s_keys.TryGetValue(piece.m_name, out keys);
                    if (!want) { if (s_line != null && s_line.gameObject.activeSelf) s_line.gameObject.SetActive(false); return; }
                    if (s_line == null && !Make(__instance)) return;
                    if (!s_line.gameObject.activeSelf) s_line.gameObject.SetActive(true);
                    if (s_line.text != keys) s_line.text = keys;
                    if (s_logged.Add(piece.m_name)) FiresLogger.LogDebug($"[FiresVoxel] key hints shown for {piece.m_name}");
                }
                catch (System.Exception ex)
                {
                    if (s_logged.Add("!error")) FiresLogger.LogDebug($"[FiresVoxel] key hints skipped: {ex.Message}");
                }
            }
        }

        private static bool Make(KeyHints hints)
        {
            var style = hints.m_buildHints.GetComponentInChildren<TMP_Text>(true);
            var anchor = hints.m_buildHints.transform as RectTransform;
            if (style == null || anchor == null) return false;
            var go = new GameObject("FAT_VToolKeys", typeof(RectTransform));
            go.transform.SetParent(anchor.parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = anchor.anchorMin; rt.anchorMax = anchor.anchorMax; rt.pivot = new Vector2(anchor.pivot.x, 0f);
            rt.anchoredPosition = anchor.anchoredPosition + new Vector2(0f, anchor.rect.height * (1f - anchor.pivot.y) + 8f);
            rt.sizeDelta = new Vector2(Mathf.Max(anchor.rect.width, 900f), style.fontSize * 1.6f);
            s_line = go.AddComponent<TextMeshProUGUI>();
            s_line.font = style.font;
            s_line.fontSize = style.fontSize;
            s_line.color = style.color;
            s_line.alignment = anchor.pivot.x > 0.5f ? TextAlignmentOptions.BottomRight : TextAlignmentOptions.BottomLeft;
            s_line.raycastTarget = false;
            s_line.enableWordWrapping = false;
            return true;
        }
    }
}
