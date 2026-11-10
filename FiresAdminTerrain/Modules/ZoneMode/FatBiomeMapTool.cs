using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using FiresCore.UI;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace VerdantsAscent.Terrain.Zones
{
    // 0.2.1393 (Fire: "biome drawing needs its own pill button and ui popup for the map so that it works on its own when rpg maker isnt installed since its a fat function";
    // "same style pill button and ui but just stack one on the other with a bit of padding"). Admins on the large map: a pill that opens the brush panel; Shift+Alt+drag draws on the map.
    // With RPG Maker installed the pill stacks under its Territory pill; alone it sits under the biome box. Only draws while the panel is open, so the Territory brush keeps Shift+Alt otherwise.
    internal sealed class FatBiomeMapTool : MonoBehaviour
    {
        private const int WindowId = 0x7E46;
        private const float WindowAlpha = 0.95f, PillHeight = 30f, StackGap = 6f, SampleDist = 6f;
        private const float MaxRadius = 96f, MinRadius = 2f, MaxOutlineRadius = 512f;
        private const int CirclesPerFrame = 3, MaxOutlinePoints = 2000;   // FAT reads circles under 1.5 s apart as one stroke
        private static readonly Vector2 Size = new Vector2(330f, 470f);

        private static FatBiomeMapTool s_instance;
        private static bool s_open;
        internal static bool PanelOpen => s_open && MapOpen();

        private bool _show, _striking, _fill = true, _wipe;
        private float _radius = 48f, _blend = 32f, _scale, _nextRefresh;
        private string[] _keys = new string[0], _names = new string[0];
        private bool[] _custom = new bool[0];
        private int _selected;
        private Vector2 _scroll;
        private Rect _rect = new Rect(-1f, -1f, 0f, 0f), _pillRect;
        private readonly List<Vector3> _stroke = new List<Vector3>();
        private readonly List<GameObject> _dots = new List<GameObject>();

        private static bool s_paletteLooked;
        private static FieldInfo s_paletteInstance, s_paletteRect;

        internal static void Ensure()
        {
            if (s_instance != null || Application.isBatchMode || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return;
            var key = VerdantsAscent.Terrain.Voxel.FiresVoxelConfig.BiomePaintMapTool;
            if (key != null && !key.Value) return;
            var go = new GameObject("FatBiomeMapTool");
            DontDestroyOnLoad(go);
            s_instance = go.AddComponent<FatBiomeMapTool>();
            FiresLogger.LogDebug("[BiomePaint] map tool ready (0.2.1393)");
        }

        private static bool MapOpen() => Minimap.instance != null && Minimap.instance.m_mode == Minimap.MapMode.Large;
        private static bool ShiftAlt() => (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) && (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt));
        private static bool Admin() => FiresCore.Sync.AdminSyncing.IsLocalAdmin();
        private static void Hud(string message) { try { MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, message); } catch (Exception ex) { FiresLogger.LogDebug($"[BiomePaint] map tool message: {ex.Message}"); } }

        internal static bool GestureActive => s_instance != null && s_open && MapOpen() && ShiftAlt() && Input.GetMouseButton(0) && CursorOverMap() && !s_instance.CursorOverUi() && Admin();

        private static bool CursorOverMap()
        {
            var map = Minimap.instance;
            var rt = map != null && map.m_mapImageLarge != null ? map.m_mapImageLarge.transform as RectTransform : null;
            return rt != null && RectTransformUtility.RectangleContainsScreenPoint(rt, ZInput.pointerPosition, null);
        }

        private static bool CursorToWorld(out Vector3 world)
        {
            world = Vector3.zero;
            var map = Minimap.instance;
            var rt = map != null && map.m_mapImageLarge != null ? map.m_mapImageLarge.transform as RectTransform : null;
            if (rt == null || !RectTransformUtility.ScreenPointToLocalPointInRectangle(rt, ZInput.pointerPosition, null, out var local)) return false;
            Vector2 norm = Rect.PointToNormalized(rt.rect, local);
            Rect uv = map.m_mapImageLarge.uvRect;
            int half = map.m_textureSize / 2;
            world = new Vector3(((uv.xMin + norm.x * uv.width) * map.m_textureSize - half) * map.m_pixelSize, 0f, ((uv.yMin + norm.y * uv.height) * map.m_textureSize - half) * map.m_pixelSize);
            return true;
        }

        private static bool WorldToMap(Vector3 world, out Vector2 anchored, out RectTransform rt)
        {
            anchored = Vector2.zero;
            var map = Minimap.instance;
            rt = map != null && map.m_mapImageLarge != null ? map.m_mapImageLarge.transform as RectTransform : null;
            if (rt == null) return false;
            int half = map.m_textureSize / 2;
            Rect uv = map.m_mapImageLarge.uvRect;
            anchored = new Vector2(((world.x / map.m_pixelSize + half) / map.m_textureSize - uv.xMin) / uv.width * rt.rect.width, ((world.z / map.m_pixelSize + half) / map.m_textureSize - uv.yMin) / uv.height * rt.rect.height);
            return true;
        }

        private bool CursorOverUi()
        {
            float sc = _scale <= 0f ? 1f : _scale;
            var p = new Vector2(Input.mousePosition.x / sc, (Screen.height - Input.mousePosition.y) / sc);
            return _pillRect.Contains(p) || (s_open && _rect.Contains(p));
        }

        private void Update()
        {
            bool map = MapOpen(), admin = map && Admin();
            _show = admin;
            if (!admin) { if (_stroke.Count > 0) Clear(); _striking = false; UpdateDots(); return; }
            if (!s_open) { _striking = false; UpdateDots(); return; }
            bool gesture = ShiftAlt() && Input.GetMouseButton(0) && CursorOverMap() && !CursorOverUi();
            if (gesture)
            {
                if (!_striking)
                {
                    _striking = true;
                    if (_fill && _stroke.Count > 0)
                    {
                        if (CursorToWorld(out var start) && Vector3.Distance(start, _stroke[0]) < Vector3.Distance(start, _stroke[_stroke.Count - 1])) _stroke.Reverse();
                        Hud($"Continuing the outline ({_stroke.Count} points so far)");
                    }
                    else _stroke.Clear();
                }
                if (CursorToWorld(out var w) && (_stroke.Count == 0 || Vector3.Distance(_stroke[_stroke.Count - 1], w) >= SampleDist)) _stroke.Add(w);
            }
            else if (_striking)
            {
                _striking = false;
                if (_stroke.Count > 0) Hud(_fill ? "Outline kept: Shift+Alt+drag again to keep drawing, then Paint or Clear" : "Stroke kept: Paint or Clear");
            }
            if (Input.GetKeyDown(KeyCode.Escape) && _stroke.Count > 0) Clear();
            UpdateDots();
        }

        private void Clear() { _striking = false; _stroke.Clear(); }

        private void UpdateDots()
        {
            while (_dots.Count < _stroke.Count)
            {
                var go = new GameObject("BiomePaintDot", typeof(RectTransform), typeof(Image));
                var rt = go.GetComponent<RectTransform>();
                rt.anchorMin = rt.anchorMax = Vector2.zero;
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.sizeDelta = new Vector2(7f, 7f);
                var img = go.GetComponent<Image>();
                img.color = new Color(1f, 0.35f, 0.2f, 0.9f);
                img.raycastTarget = false;
                _dots.Add(go);
            }
            while (_dots.Count > _stroke.Count)
            {
                var last = _dots[_dots.Count - 1];
                _dots.RemoveAt(_dots.Count - 1);
                if (last != null) Destroy(last);
            }
            for (int i = 0; i < _stroke.Count; i++)
            {
                if (_dots[i] == null) continue;
                if (!WorldToMap(_stroke[i], out var at, out var parent)) { _dots[i].SetActive(false); continue; }
                var rt = _dots[i].GetComponent<RectTransform>();
                if (rt.parent != parent) rt.SetParent(parent, false);
                rt.anchoredPosition = at;
                _dots[i].SetActive(true);
            }
        }

        // The Territory pill's rect when RPG Maker is installed and has drawn it. Read by name, so FAT loads without RPG Maker.
        private static bool TerritoryPill(out Rect rect)
        {
            rect = default;
            if (!s_paletteLooked)
            {
                s_paletteLooked = true;
                var type = FatSoftTypes.Find("FiresRPGmaker.Modules.Territories.Paint.TerritoryPaintPalette");
                s_paletteInstance = type != null ? AccessTools.Field(type, "_instance") : null;
                s_paletteRect = type != null ? AccessTools.Field(type, "_collapsedRect") : null;
                FiresLogger.LogDebug($"[BiomePaint] map tool: Territory pill {(s_paletteInstance != null && s_paletteRect != null ? "found, stacking under it" : "not installed, standing alone")}");
            }
            if (s_paletteInstance == null || s_paletteRect == null) return false;
            var instance = s_paletteInstance.GetValue(null);
            if (instance == null) return false;
            rect = (Rect)s_paletteRect.GetValue(instance);
            return rect.width > 0f;
        }

        private void PlacePill()
        {
            float w = 220f, x = Screen.width / _scale - w - 16f, y = 120f;
            try
            {
                if (TerritoryPill(out var territory)) { _pillRect = new Rect(territory.x, territory.yMax + StackGap, territory.width, territory.height); return; }
                var anchor = Minimap.instance != null && Minimap.instance.m_biomeNameLarge != null ? Minimap.instance.m_biomeNameLarge.rectTransform : null;
                if (anchor != null)
                {
                    var c = new Vector3[4];
                    anchor.GetWorldCorners(c);   // overlay canvas: world == screen px
                    float left = c[0].x / _scale, right = c[2].x / _scale;
                    w = Mathf.Max(120f, right - left);
                    x = right - w;
                    y = (Screen.height - c[0].y) / _scale + 8f;
                }
            }
            catch (Exception ex) { FiresLogger.LogDebug($"[BiomePaint] map tool: pill place: {ex.Message}"); }
            _pillRect = new Rect(x, y, w, PillHeight);
        }

        private void OnGUI()
        {
            if (!_show) return;
            if (_scale <= 0f) _scale = Mathf.Clamp(Screen.height / 1080f, 0.85f, 1.7f);
            var matrix = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(_scale, _scale, 1f));
            try
            {
                FiresRoundedSkin.Ensure(WindowAlpha);
                PlacePill();
                if (GUI.Button(_pillRect, s_open ? "<b>« Biome</b>" : "<b>» Biome</b>", s_open ? FiresRoundedSkin.NavSel : FiresRoundedSkin.Button))
                {
                    s_open = !s_open;
                    if (!s_open) Clear();
                }
                if (!s_open) return;
                float logH = Screen.height / _scale, logW = Screen.width / _scale;
                if (_rect.x < 0f) _rect = new Rect(Mathf.Max(16f, _pillRect.x - Size.x - 12f), Mathf.Clamp(_pillRect.y, 20f, Mathf.Max(20f, logH - Size.y - 20f)), Size.x, Size.y);
                _rect.width = Size.x; _rect.height = Size.y;
                _rect = GUILayout.Window(WindowId, _rect, DrawWindow, "", FiresRoundedSkin.Window);
                _rect.x = Mathf.Clamp(_rect.x, 0f, logW - 60f);
                _rect.y = Mathf.Clamp(_rect.y, 0f, logH - 40f);
            }
            finally { GUI.matrix = matrix; }
        }

        private void Refresh()
        {
            if (Time.unscaledTime < _nextRefresh) return;
            _nextRefresh = Time.unscaledTime + 2f;
            string current = _selected < _keys.Length ? _keys[_selected] : null;
            if (!global::VerdantsAscent.FatApi.TryGetBiomeBrushes(out var keys, out var names, out var custom)) return;
            _keys = keys ?? new string[0];
            _names = names ?? new string[0];
            _custom = custom ?? new bool[0];
            _selected = Mathf.Max(0, Array.IndexOf(_keys, current));
        }

        private void DrawWindow(int id)
        {
            Refresh();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Biome Brush", FiresRoundedSkin.Title);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("<b>–</b>", FiresRoundedSkin.ButtonSmall, GUILayout.Width(28f))) { s_open = false; Clear(); }
            GUILayout.EndHorizontal();
            GUILayout.Space(3f);
            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(190f));
            if (_keys.Length == 0) GUILayout.Label("No brushes yet: the world is still loading.", FiresRoundedSkin.Hint);
            for (int i = 0; i < _keys.Length; i++)
            {
                string name = i < _names.Length ? _names[i] : _keys[i];
                if (i < _custom.Length && _custom[i]) name += "  (custom)";
                if (GUILayout.Button(name, i == _selected ? FiresRoundedSkin.NavSel : FiresRoundedSkin.Button)) _selected = i;
            }
            GUILayout.EndScrollView();
            GUILayout.Space(4f);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Fill outline", _fill ? FiresRoundedSkin.NavSel : FiresRoundedSkin.Button)) _fill = true;
            if (GUILayout.Button("Strip", !_fill ? FiresRoundedSkin.NavSel : FiresRoundedSkin.Button)) _fill = false;
            GUILayout.EndHorizontal();
            GUILayout.Space(4f);
            GUILayout.Label($"Blend: {_blend:0} m (custom biomes)", FiresRoundedSkin.Hint);
            _blend = Mathf.Round(GUILayout.HorizontalSlider(_blend, 0f, 96f, FiresRoundedSkin.Slider, FiresRoundedSkin.SliderThumb));
            GUILayout.Space(4f);
            if (!_fill)
            {
                GUILayout.Label($"Radius: {_radius:0} m", FiresRoundedSkin.Hint);
                _radius = Mathf.Round(GUILayout.HorizontalSlider(_radius, MinRadius, MaxRadius, FiresRoundedSkin.Slider, FiresRoundedSkin.SliderThumb));
                GUILayout.Space(4f);
            }
            if (GUILayout.Button(_wipe ? "<b>Wipe builds in the area: ON</b>" : "Wipe builds in the area: off", _wipe ? FiresRoundedSkin.NavSel : FiresRoundedSkin.Button)) _wipe = !_wipe;
            GUILayout.Space(4f);
            GUILayout.Label(_fill ? "Shift+Alt+drag an outline on the map (again to continue it), then Paint." : "Shift+Alt+drag on the map, then Paint.", FiresRoundedSkin.Hint);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Paint", FiresRoundedSkin.Button, GUILayout.Height(30f))) Paint();
            if (GUILayout.Button("Clear", FiresRoundedSkin.Button, GUILayout.Height(30f))) { bool had = _stroke.Count > 0; Clear(); Hud(had ? "Cleared the drawn shape" : "Nothing to clear"); }
            GUILayout.EndHorizontal();
            GUI.DragWindow();
        }

        private void Paint()
        {
            if (_stroke.Count == 0) { Hud("Nothing drawn: Shift+Alt+drag on the map first"); return; }
            if (_selected >= _keys.Length) { Hud("Pick a biome brush first"); return; }
            string key = _keys[_selected];
            if (_fill) { Hud(SendArea(key)); Clear(); return; }
            var circles = new List<Vector3> { _stroke[0] };
            float step = Mathf.Max(1f, _radius), since = 0f;
            for (int i = 1; i < _stroke.Count; i++)
            {
                Vector3 from = _stroke[i - 1], dir = _stroke[i] - from;
                float length = dir.magnitude;
                if (length <= 0f) continue;
                float at = step - since;
                for (; at <= length; at += step) circles.Add(from + dir * (at / length));
                since = length - (at - step);
            }
            StartCoroutine(SendCircles(key, circles, _radius, _blend, _wipe));
            Clear();
        }

        private string SendArea(string key)
        {
            var points = new List<Vector3>(_stroke);
            if (points.Count > 1 && Vector3.Distance(points[0], points[points.Count - 1]) < 1f) points.RemoveAt(points.Count - 1);
            if (points.Count < 3) return "Draw an outline first: at least 3 points";
            if (points.Count > MaxOutlinePoints)
            {
                var thin = new List<Vector3>(MaxOutlinePoints);
                for (int i = 0; i < MaxOutlinePoints; i++) thin.Add(points[i * points.Count / MaxOutlinePoints]);
                points = thin;
            }
            Vector3 centre = Vector3.zero;
            foreach (var p in points) centre += p;
            centre /= points.Count;
            foreach (var p in points) if (Vector3.Distance(p, centre) > MaxOutlineRadius) return "Outline too big: about 1 km across at most. Paint it in parts.";
            var xz = new float[points.Count * 2];
            for (int i = 0; i < points.Count; i++) { xz[i * 2] = points[i].x; xz[i * 2 + 1] = points[i].z; }
            bool sent = global::VerdantsAscent.FatApi.RequestBiomePaintArea(key, xz, _blend, _wipe, out string report);
            FiresLogger.LogDebug($"[BiomePaint] map tool: outline of {points.Count} point(s), brush {key}, blend {_blend:0}, wipe {_wipe}, sent {sent}: {report}");
            return sent ? (string.IsNullOrEmpty(report) ? "Biome paint sent" : report) : "Biome paint refused: " + report;
        }

        private static IEnumerator SendCircles(string key, List<Vector3> circles, float radius, float blend, bool wipe)
        {
            for (int i = 0; i < circles.Count; i++)
            {
                if (!global::VerdantsAscent.FatApi.RequestBiomePaint(key, circles[i].x, circles[i].z, radius, blend, wipe, out string report))
                {
                    Hud("Biome paint refused: " + report);
                    FiresLogger.LogDebug($"[BiomePaint] map tool: refused at circle {i + 1} of {circles.Count}: {report}");
                    yield break;
                }
                if ((i + 1) % CirclesPerFrame == 0) yield return null;
            }
            FiresLogger.LogDebug($"[BiomePaint] map tool: sent {circles.Count} circle(s) of {radius:0} m, brush {key}, wipe {wipe}");
            Hud($"Biome paint sent: {circles.Count} circle(s)");
        }
    }

    // While drawing, the drag paints instead of panning the map.
    [HarmonyPatch(typeof(Minimap), "Update")]
    internal static class Minimap_Update_FatBiomeMapTool
    {
        private static bool Prefix() => !FatBiomeMapTool.GestureActive;
    }
}
