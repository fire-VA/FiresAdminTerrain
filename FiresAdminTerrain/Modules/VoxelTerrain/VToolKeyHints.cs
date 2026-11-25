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
        // 0.2.1454 ([ghost], lead 11-17 Voxheim diff P3: hints existed for Vramp and Vriver only): every V-tool's keys, ONE table, read by
        // the hint line here AND by the help panel (FatHelpContent.BuildVTools), so the two can't drift apart. Each line is what that
        // tool's driver does (VoxelBrushDrag / .Strokes, VoxelRamp, the water tools); {H} = the height keys, {F} = freeze, {U} = undo.
        private static readonly Dictionary<string, string> s_keys = new Dictionary<string, string>
        {
            { "Vcarve", "Click / drag: carve   Wheel: size   {H}: push in   {S}   {F}   {U}" },
            { "Vfill", "Click / drag: fill   Wheel: width   {H}: height   {S}   {F}   {U}" },
            { "Vdelete", "Click / drag: back to the generated ground   Wheel: width   {H}: reach above and below   {F}   {U}" },
            { "Vcube", "Click / drag: stamp cubes   Hold Left Alt: carve   Q/E: turn 15 deg   Wheel: size   {H}: lift   {S}   {F}   {U}" },
            { "Vsphere", "Click / drag: stamp spheres   Hold Left Alt: carve   Wheel: size   {H}: lift   {S}   {F}   {U}" },
            { "Vlevel", "Click / drag: level   Left Alt + drag: smooth   {T}: smooth strength   Wheel: size   {H}: offset   {F}   {U}" },
            { "Vskin", "Click / drag over holes: lay a shell   Wheel: size   {H}: thickness   {F}   {U}" },
            { "Vramp", "Click: lay ramp   Wheel: width   Shift+Wheel: rise   Q/E: turn   {A}: slope 10/20/30/45 deg   Numpad 7/9: undo/redo" },
            { "Vpour", "Click / drag: lay water   Wheel: width   {H}: water height   {U}" },
            { "Vriver", "Click: start   Shift+Click: spine point   Click: end   Wheel: width   Shift+Wheel: depth   Numpad 7/9: undo/redo   Esc: cancel" },
            { "Vpond", "Click: dig a lake   Left Alt + Click: river point   Click: end lake   Wheel: size   {H}: depth   {U}" },
            { "Vfall", "Click: waterfall top here, ground or water (as the preview shows)   Wheel: turn   Shift+Wheel: width   Middle mouse: remove falls here   Numpad 7/9: undo/redo" },
            { "Vmop", "Hold + drag: remove water   Middle mouse: restore it in the ring   Wheel: size   {U}" },
        };

        /// <summary>The keys line for a V-tool (null when it has none), with this machine's configured ghost keys filled in.</summary>
        internal static string KeysFor(string toolName)
        {
            if (string.IsNullOrEmpty(toolName) || !s_keys.TryGetValue(toolName, out var line)) return null;
            string up = FiresVoxelConfig.GhostUpKey != null ? FiresVoxelConfig.GhostUpKey.Value.ToString() : "PageUp";
            string down = FiresVoxelConfig.GhostDownKey != null ? FiresVoxelConfig.GhostDownKey.Value.ToString() : "PageDown";
            string freeze = FiresVoxelConfig.GhostFreezeKey != null ? FiresVoxelConfig.GhostFreezeKey.Value.ToString() : "Keypad0";
            string softer = VoxelBrushSoft.SofterKey != null ? VoxelBrushSoft.SofterKey.Value.ToString() : "KeypadPlus";
            string harder = VoxelBrushSoft.HarderKey != null ? VoxelBrushSoft.HarderKey.Value.ToString() : "KeypadMinus";
            string strength = VoxelBrushSoft.StrengthKey != null ? VoxelBrushSoft.StrengthKey.Value.ToString() : "KeypadDivide";
            return line.Replace("{S}", $"{softer}/{harder}: softness   {strength}: strength")
                       .Replace("{T}", strength)   // 0.2.1455: Vlevel's smooth strength key
                       .Replace("{A}", VoxelBrushSoft.RampAngleKey != null ? VoxelBrushSoft.RampAngleKey.Value.ToString() : "KeypadMultiply")   // 0.2.1455: Vramp slope steps
                       .Replace("{H}", $"Shift+Wheel or {up}/{down}")
                       .Replace("{F}", $"{freeze}: freeze ghost")
                       .Replace("{U}", "Numpad 7/9: undo/redo");
        }

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
                        && (keys = KeysFor(piece.m_name)) != null;
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
