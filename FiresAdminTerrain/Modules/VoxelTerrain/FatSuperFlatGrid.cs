using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    /// <summary>
    /// The super flat BUILD GRID. Super flat worlds only, client
    /// only, nothing networked or saved in the world.
    ///   - Grid overlay (Alt+G): one flat quad over the LOADED zones (vanilla 1.0's active area: the 3 x 3 zones around the player's zone), with
    ///     a generated one-zone grid texture tiled per zone; cyan at 35% by default, the 64 m zone lines brighter; fades over the last
    ///     zone. One draw call, no per-line objects.
    ///   - Snap (Alt+H): one client-only Piece anchor whose snappoint children sit on the grid crossings around the CURSOR (at most
    ///     1,024, clipped to the loaded area), so vanilla's own snapping (and IH / FAP placement) lands pieces on the grid. Its collider
    ///     sits just under the flat ground (piece_nonsolid), so the place ray still hits the ground and Piece.GetSnapPoints' overlap
    ///     sphere finds it.
    ///   - Zone-aligned: zone edges sit at 32 + 64k, so lines are at 32 + k*size. The size snaps to a divisor of 64 so every zone edge
    ///     is always a line.
    /// </summary>
    internal static class FatSuperFlatGrid
    {
        private const string S = "Super flat grid";
        internal static ConfigEntry<bool> ShowGrid, SnapToGrid;
        internal static ConfigEntry<float> GridSize, LineWidth, LineOpacity, ZoneEmphasis;
        internal static ConfigEntry<Color> LineColour;
        internal static ConfigEntry<KeyboardShortcut> ToggleGridKey, ToggleSnapKey;
        private static bool s_bound;

        internal static readonly float[] Sizes = { 0.25f, 0.5f, 1f, 2f, 4f, 8f, 16f, 32f, 64f };
        private const float ZoneSize = 64f, ZoneEdgeOffset = 32f, Lift = 0.02f, MaxSnapPoints = 1024;

        internal static void Bind(ConfigFile cfg)
        {
            if (s_bound) return;   // ConfigSync rule: bind once per process
            s_bound = true;
            ShowGrid = cfg.Bind(S, "Show grid", false, "Super flat worlds: draw the build grid over the loaded zones (also toggled by the Toggle grid key).");
            SnapToGrid = cfg.Bind(S, "Snap to grid", false, "Super flat worlds: every grid crossing near the cursor is a snap point for placing pieces (also toggled by the Toggle snap key).");
            GridSize = cfg.Bind(S, "Grid size (m)", 2f, new ConfigDescription("Grid spacing; snaps to 0.25, 0.5, 1, 2, 4, 8, 16, 32 or 64 m so every 64 m zone edge stays a grid line.", new AcceptableValueRange<float>(0.25f, 64f)));
            LineWidth = cfg.Bind(S, "Line width (m)", 0.08f, new ConfigDescription("Line thickness in world metres (0.08 = a fine grid; the zone lines draw wider by Zone line emphasis). Live.", new AcceptableValueRange<float>(0.02f, 1f)));   // Metres (the old texel "Line width" key is left unbound)
            LineOpacity = cfg.Bind(S, "Line opacity", 0.35f, new ConfigDescription("0 = invisible, 1 = solid.", new AcceptableValueRange<float>(0f, 1f)));
            LineColour = cfg.Bind(S, "Line colour", new Color(0f, 1f, 1f, 1f), "The grid lines' colour (cyan by default).");
            ZoneEmphasis = cfg.Bind(S, "Zone line emphasis", 1.8f, new ConfigDescription("How much brighter and wider the 64 m zone lines draw.", new AcceptableValueRange<float>(1f, 4f)));
            ToggleGridKey = cfg.Bind(S, "Toggle grid key", new KeyboardShortcut(KeyCode.G, KeyCode.LeftAlt), "Shows / hides the build grid (super flat worlds).");
            ToggleSnapKey = cfg.Bind(S, "Toggle snap key", new KeyboardShortcut(KeyCode.H, KeyCode.LeftAlt), "Turns grid snapping on / off (super flat worlds).");
        }

        /// <summary>The configured size snapped to the nearest divisor of 64.</summary>
        internal static float Size
        {
            get
            {
                float want = GridSize != null ? GridSize.Value : 2f, best = 2f, d = float.MaxValue;
                foreach (float s in Sizes) { float e = Mathf.Abs(Mathf.Log(s) - Mathf.Log(Mathf.Max(0.01f, want))); if (e < d) { d = e; best = s; } }
                if (!Mathf.Approximately(best, s_loggedSize) && !Mathf.Approximately(best, want)) FiresLogger.LogInfo($"[FAT] super flat grid: size {want:0.##} m snapped to {best:0.##} m (a divisor of 64, so zone edges stay grid lines)");
                s_loggedSize = best;
                return best;
            }
        }
        private static float s_loggedSize = -1f;

        /// <summary>Unit check for tests: is world coordinate v on a grid line of the current size (zone-aligned)?</summary>
        internal static bool IsGridLine(float v, float size) { float r = Mathf.Repeat(v - ZoneEdgeOffset, size); return r < 0.001f || size - r < 0.001f; }
        internal static float Snap(float v, float size) => Mathf.Round((v - ZoneEdgeOffset) / size) * size + ZoneEdgeOffset;

        // ---- the per-frame driver ----
        private static GameObject s_quad, s_anchor;
        private static Material s_mat;
        private static Texture2D s_tex;
        private static string s_texKey;
        private static Vector2Int s_quadZone = new Vector2Int(int.MinValue, 0);
        private static int s_quadArea = -1;
        private static Vector2Int s_snapCell = new Vector2Int(int.MinValue, 0);
        private static float s_snapSize = -1f;
        private static readonly List<Transform> s_points = new List<Transform>();
        internal static int SnapPointCount { get; private set; }
        internal static bool QuadShown => s_quad != null && s_quad.activeSelf;
        internal static float LastRebuildMs { get; private set; }

        [HarmonyPatch(typeof(GameCamera), "LateUpdate")]
        private static class Driver
        {
            private static void Postfix() { try { Tick(); } catch (System.Exception e) { FiresLogger.LogWarning("[FAT] super flat grid: " + e.Message); Hide(); } }
        }

        [HarmonyPatch(typeof(ZNet), "OnDestroy")]
        private static class Teardown
        {
            private static void Postfix() { if (s_quad != null) Object.Destroy(s_quad); if (s_mat != null) Object.Destroy(s_mat); s_mat = null; if (s_anchor != null) Object.Destroy(s_anchor); s_quad = s_anchor = null; s_points.Clear(); s_lines.Clear(); s_linesKey = null; s_quadZone = new Vector2Int(int.MinValue, 0); s_snapCell = new Vector2Int(int.MinValue, 0); }
        }

        private static void Hide()
        {
            if (s_quad != null && s_quad.activeSelf) s_quad.SetActive(false);
            if (s_anchor != null && s_anchor.activeSelf) s_anchor.SetActive(false);
        }

        private static bool Typing() => Console.IsVisible() || TextInput.IsVisible() || (Chat.instance != null && Chat.instance.HasFocus()) || Menu.IsVisible() || InventoryGui.IsVisible() || Minimap.IsOpen();

        private static void Tick()
        {
            var player = Player.m_localPlayer;
            if (!s_bound || !FatSuperFlat.On || FatHaloArc.RingOnly || player == null || SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) { Hide(); return; }
            if (!Typing())
            {
                if (ToggleGridKey.Value.IsDown()) { ShowGrid.Value = !ShowGrid.Value; player.Message(MessageHud.MessageType.Center, ShowGrid.Value ? $"Build grid ON - {Size:0.##} m" : "Build grid OFF"); FiresLogger.LogVerbose($"[FAT] super flat grid: grid {(ShowGrid.Value ? "on" : "off")} ({Size:0.##} m)"); }
                if (ToggleSnapKey.Value.IsDown()) { SnapToGrid.Value = !SnapToGrid.Value; player.Message(MessageHud.MessageType.Center, SnapToGrid.Value ? $"Grid snap ON - {Size:0.##} m" : "Grid snap OFF"); FiresLogger.LogVerbose($"[FAT] super flat grid: snap {(SnapToGrid.Value ? "on" : "off")}"); }
            }
            int area = 1;   // Valheim 1.0: the active area is the simulation distance, zones within 1.5 zones (Chebyshev) of the player's zone = 3 x 3 zones (ZNetScene.PointInsideActiveArea)
            Vector3 p = player.transform.position;
            var zone = new Vector2Int(Mathf.FloorToInt((p.x + ZoneEdgeOffset) / ZoneSize), Mathf.FloorToInt((p.z + ZoneEdgeOffset) / ZoneSize));
            float size = Size;

            // overlay
            if (ShowGrid.Value)
            {
                EnsureQuad();
                RefreshTexture(size);
                if (zone != s_quadZone || area != s_quadArea) PlaceQuad(zone, area);
                UpdateLines(zone, area, size);
                if (!s_quad.activeSelf) s_quad.SetActive(true);
            }
            else if (s_quad != null && s_quad.activeSelf) s_quad.SetActive(false);

            // snap - only while the player is placing (1.0.1020: the snap slab is a piece_nonsolid collider, which vanilla's grow
            // check counts; outside the hammer's place mode there is nothing to snap, so the slab is not there at all)
            if (SnapToGrid.Value && player.InPlaceMode())
            {
                Vector3 aim = CursorPoint(player);
                EnsureAnchor();
                var cell = new Vector2Int(Mathf.FloorToInt((aim.x - ZoneEdgeOffset) / size), Mathf.FloorToInt((aim.z - ZoneEdgeOffset) / size));
                if (cell != s_snapCell || !Mathf.Approximately(size, s_snapSize)) RebuildSnap(aim, size, zone, area);
                if (!s_anchor.activeSelf) s_anchor.SetActive(true);
            }
            else if (s_anchor != null && s_anchor.activeSelf) s_anchor.SetActive(false);
        }

        // where the player is building: the placement ghost when there is one, else the camera's aim on the flat ground
        private static Vector3 CursorPoint(Player player)
        {
            var ghost = Traverse.Create(player).Field("m_placementGhost").GetValue<GameObject>();
            if (ghost != null && ghost.activeInHierarchy) return ghost.transform.position;
            var cam = GameCamera.instance != null ? GameCamera.instance.transform : player.transform;
            float y = FiresWorldProfile.SuperFlatHeight;
            Vector3 d = cam.forward;
            if (d.y < -0.05f) { float t = (y - cam.position.y) / d.y; if (t > 0f && t < 200f) return cam.position + d * t; }
            return player.transform.position;
        }

        private static void EnsureQuad()
        {
            if (s_quad != null) return;
            s_quad = new GameObject("FAT_SuperFlatGrid");
            Object.DontDestroyOnLoad(s_quad);
            s_quad.AddComponent<MeshFilter>();
            var mr = s_quad.AddComponent<MeshRenderer>();
            var shader = Shader.Find("Sprites/Default") ?? Shader.Find("UI/Default");
            s_mat = new Material(shader) { renderQueue = 3000, name = "FAT_SuperFlatGrid" };
            mr.sharedMaterial = s_mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            FiresLogger.LogInfo($"[FAT] super flat grid: overlay built (shader '{(shader != null ? shader.name : "none")}')");
        }

        // 64 m lines at eye height were invisible: a texture line on the ground is foreshortened at a grazing view (a 0.14 m
        // line 30 m off is ~1/4 px deep) and anisotropic filtering averages it away whatever the mips hold. Reused from FAT's PondTool
        // (CustomWater\Tools\PondTool.NewLine): camera-facing LineRenderers keep their world width from any angle (0.14 m at 30 m = several
        // px). The zone lines are always drawn this way; at 8 m and up every grid line is (at most ~50). The texture keeps the fine near grid.
        private static readonly List<LineRenderer> s_lines = new List<LineRenderer>();
        private static Material s_lineMat;
        private static string s_linesKey;
        private const float LinesFromSize = 8f;

        private static LineRenderer NewLine()
        {
            if (s_lineMat == null) s_lineMat = new Material(Shader.Find("Sprites/Default") ?? Shader.Find("UI/Default")) { renderQueue = 3001, name = "FAT_SuperFlatGridLines" };
            var go = new GameObject("line");
            go.transform.SetParent(s_quad.transform, false);
            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = true;
            lr.loop = false;
            lr.material = s_lineMat;
            lr.alignment = LineAlignment.View;
            lr.textureMode = LineTextureMode.Stretch;
            lr.numCapVertices = 0;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            return lr;
        }

        private static void UpdateLines(Vector2Int zone, int area, float size)
        {
            Color c = LineColour.Value; float op = LineOpacity.Value, w = Mathf.Clamp(LineWidth.Value, 0.02f, 1f), em = ZoneEmphasis.Value;
            string key = $"{zone}|{area}|{size}|{c}|{op}|{w}|{em}";
            if (key == s_linesKey) return;
            s_linesKey = key;
            float lo = (zone.x - area) * ZoneSize - ZoneEdgeOffset, hi = (zone.x + area + 1) * ZoneSize - ZoneEdgeOffset;
            float loZ = (zone.y - area) * ZoneSize - ZoneEdgeOffset, hiZ = (zone.y + area + 1) * ZoneSize - ZoneEdgeOffset;
            float y = FiresWorldProfile.SuperFlatHeight + Lift + 0.01f;
            float step = size >= LinesFromSize ? size : ZoneSize;
            // the lines strictly inside the faded outer ring: the quad's alpha is 1 only between the inner zone edges
            float inLo = lo + ZoneSize, inHi = hi - ZoneSize, inLoZ = loZ + ZoneSize, inHiZ = hiZ - ZoneSize;
            var xs = new List<float>(); var zs = new List<float>();
            for (float v = inLo; v <= inHi + 0.001f; v += step) xs.Add(v);
            for (float v = inLoZ; v <= inHiZ + 0.001f; v += step) zs.Add(v);
            int need = xs.Count + zs.Count, used = 0;
            while (s_lines.Count < need) s_lines.Add(NewLine());
            void Set(Vector3 a, Vector3 b, bool zoneLine)
            {
                var lr = s_lines[used++];
                lr.gameObject.SetActive(true);
                lr.positionCount = 4;
                // ends fade over the outer zone, as the quad does
                lr.SetPositions(new[] { a, Vector3.Lerp(a, b, 1f / 3f), Vector3.Lerp(a, b, 2f / 3f), b });
                float alpha = zoneLine ? Mathf.Min(1f, op * em) : op;
                lr.widthMultiplier = zoneLine ? w * em : w;
                var g = new Gradient();
                g.SetKeys(new[] { new GradientColorKey(c, 0f), new GradientColorKey(c, 1f) },
                          new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(alpha, 1f / 3f), new GradientAlphaKey(alpha, 2f / 3f), new GradientAlphaKey(0f, 1f) });
                lr.colorGradient = g;
            }
            foreach (float x in xs) Set(new Vector3(x, y, loZ), new Vector3(x, y, hiZ), Mathf.Abs(Mathf.Repeat(x - ZoneEdgeOffset, ZoneSize)) < 0.01f);
            foreach (float z in zs) Set(new Vector3(lo, y, z), new Vector3(hi, y, z), Mathf.Abs(Mathf.Repeat(z - ZoneEdgeOffset, ZoneSize)) < 0.01f);
            for (int i = used; i < s_lines.Count; i++) s_lines[i].gameObject.SetActive(false);
            FiresLogger.LogInfo($"[FAT] super flat grid: {used} camera-facing line(s) ({(size >= LinesFromSize ? $"every {size:0.##} m line" : "zone lines")}), width {w:0.###} m (zone x{em:0.#}), opacity {op:0.##}");
        }

        // a (n+1) x (n+1) vertex grid on the zone corners: uv = zones (the texture is one zone), alpha 0 on the outer ring = fade over the last zone
        private static void PlaceQuad(Vector2Int zone, int area)
        {
            s_quadZone = zone; s_quadArea = area;
            int n = area * 2 + 1;
            float x0 = (zone.x - area) * ZoneSize - ZoneEdgeOffset, z0 = (zone.y - area) * ZoneSize - ZoneEdgeOffset;
            var verts = new Vector3[(n + 1) * (n + 1)]; var uv = new Vector2[verts.Length]; var cols = new Color[verts.Length];
            var tris = new int[n * n * 6];
            float y = FiresWorldProfile.SuperFlatHeight + Lift;
            for (int j = 0; j <= n; j++)
                for (int i = 0; i <= n; i++)
                {
                    int k = j * (n + 1) + i;
                    verts[k] = new Vector3(x0 + i * ZoneSize, y, z0 + j * ZoneSize);
                    uv[k] = new Vector2(i, j);
                    bool edge = i == 0 || j == 0 || i == n || j == n;
                    cols[k] = new Color(1f, 1f, 1f, edge ? 0f : 1f);
                }
            int t = 0;
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    int a = j * (n + 1) + i, b = a + 1, c = a + n + 1, d = c + 1;
                    tris[t++] = a; tris[t++] = c; tris[t++] = b; tris[t++] = b; tris[t++] = c; tris[t++] = d;
                }
            var mf = s_quad.GetComponent<MeshFilter>();
            var mesh = mf.sharedMesh ?? new Mesh { name = "FAT_SuperFlatGrid" };
            mesh.Clear(); mesh.vertices = verts; mesh.uv = uv; mesh.colors = cols; mesh.triangles = tris; mesh.RecalculateBounds();
            mf.sharedMesh = mesh;
        }

        // Lines read 0.5-1 m wide, so 2 m squares couldn't be marked out: the width is in WORLD metres
        // ("Line width (m)", default 0.08). The texture is one zone; its resolution follows the width so a line spans >= ~2.5 texels
        // (2048 px max = 3.1 cm a texel), and each texel's alpha is its analytic coverage of the line, so edges are anti-aliased and
        // the mip chain fades a distant line instead of smearing it wide. Rebuilt when a value has been steady for 0.25 s (the slider).
        private static string s_texPending; private static float s_texPendingAt;
        private static void RefreshTexture(float size)
        {
            Color c = LineColour.Value; float op = LineOpacity.Value, w = Mathf.Clamp(LineWidth.Value, 0.02f, 1f), em = ZoneEmphasis.Value;
            string key = $"{size}|{c}|{op}|{w}|{em}";
            if (key == s_texKey && s_tex != null)
            {
                // The 2nd transfer back to the super flat world showed a white ground with dark blobs: the
                // texture outlives the world (it is no scene object) but the overlay quad and its material are rebuilt per world
                // (Teardown + EnsureQuad), so the new material kept Sprites/Default's plain white texture over the ground.
                if (s_mat != null && s_mat.mainTexture != s_tex) { s_mat.mainTexture = s_tex; FiresLogger.LogInfo($"[FAT] super flat grid: texture re-attached to the rebuilt overlay material ({s_tex.width} px, kept from the previous world)"); }
                return;
            }
            if (s_tex != null)
            {
                if (key != s_texPending) { s_texPending = key; s_texPendingAt = Time.unscaledTime; return; }
                if (Time.unscaledTime - s_texPendingAt < 0.25f) return;
            }
            s_texKey = key; s_texPending = null;
            int cells = Mathf.RoundToInt(ZoneSize / size);
            int px = Mathf.Clamp(Mathf.NextPowerOfTwo(Mathf.CeilToInt(ZoneSize / (w / 2.5f))), 256, 2048);
            px = Mathf.Max(px, Mathf.Min(2048, Mathf.NextPowerOfTwo(cells * 8)));   // never fewer than 8 texels a cell
            float texPerM = px / ZoneSize, perCell = px / (float)cells;
            if (s_tex == null || s_tex.width != px) { if (s_tex != null) Object.Destroy(s_tex); s_tex = new Texture2D(px, px, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear, anisoLevel = 16, name = "FAT_SuperFlatGrid" }; }
            var pix = new Color32[px * px];
            float half = w * 0.5f * texPerM, zoneHalf = half * em;   // half widths in texels
            byte r = (byte)(c.r * 255f), g = (byte)(c.g * 255f), b = (byte)(c.b * 255f);
            for (int yy = 0; yy < px; yy++)
            {
                float cy = yy + 0.5f;
                float dy = Mathf.Min(Mathf.Repeat(cy, perCell), perCell - Mathf.Repeat(cy, perCell));
                float zy = Mathf.Min(cy, px - cy);
                for (int xx = 0; xx < px; xx++)
                {
                    float cx = xx + 0.5f;
                    float dx = Mathf.Min(Mathf.Repeat(cx, perCell), perCell - Mathf.Repeat(cx, perCell));
                    float zx = Mathf.Min(cx, px - cx);
                    // coverage of a 1-texel pixel by a line of half width h at distance d: clamp(h - d + 0.5, 0, 1)
                    float cell = Mathf.Clamp01(half - Mathf.Min(dx, dy) + 0.5f) * op;
                    float zoneA = Mathf.Clamp01(zoneHalf - Mathf.Min(zx, zy) + 0.5f) * Mathf.Min(1f, op * em);
                    float a = Mathf.Max(cell, zoneA);
                    pix[yy * px + xx] = new Color32(r, g, b, (byte)(a * 255f));
                }
            }
            s_tex.SetPixels32(pix, 0);
            // At eye height a far line vanished: the mip chain is built with a MAX filter, not Unity's average,
            // so a line keeps full opacity at about 1 texel in every mip = about 1 screen pixel at any distance; it thins, never disappears
            var prev = pix; int size2 = px;
            for (int mip = 1; mip < s_tex.mipmapCount; mip++)
            {
                int ns = Mathf.Max(1, size2 / 2);
                // once a cell is under ~3 texels in this mip the lines would merge into a solid wash: fade them (far away only)
                float cellTex = perCell / (1 << mip), fade = Mathf.Clamp01((cellTex - 1f) / 2f);
                var next = new Color32[ns * ns];
                for (int y2 = 0; y2 < ns; y2++)
                    for (int x2 = 0; x2 < ns; x2++)
                    {
                        int sx = Mathf.Min(x2 * 2, size2 - 1), sy = Mathf.Min(y2 * 2, size2 - 1), sx1 = Mathf.Min(sx + 1, size2 - 1), sy1 = Mathf.Min(sy + 1, size2 - 1);
                        byte am = (byte)Mathf.Max(Mathf.Max(prev[sy * size2 + sx].a, prev[sy * size2 + sx1].a), Mathf.Max(prev[sy1 * size2 + sx].a, prev[sy1 * size2 + sx1].a));
                        next[y2 * ns + x2] = new Color32(r, g, b, (byte)(am * fade));
                    }
                s_tex.SetPixels32(next, mip);
                prev = next; size2 = ns;
            }
            s_tex.Apply(false);
            s_mat.mainTexture = s_tex;
            FiresLogger.LogInfo($"[FAT] super flat grid: texture {px} px ({texPerM:0.#} texels/m, {s_tex.mipmapCount} mips max-filtered, on material {s_mat.name} shader {s_mat.shader.name}) for {size:0.##} m cells, line {w:0.###} m, opacity {op:0.##}");
        }

        // Fire 10-24: "basically anything we plant says needs more room to grow" on super flat. Vanilla Plant.HaveGrowSpace overlaps
        // the grow radius against Default/static_solid/Default_small/piece/piece_nonsolid, and the snap slab is a solid collider on
        // piece_nonsolid (it has to be: Piece.GetSnapPoints only finds snap points on piece/piece_nonsolid colliders, and a trigger
        // would be counted by both queries alike). So for the one grow check the slab's collider is switched off and back on (a
        // disabled collider leaves physics queries at once). One Debug line the first time it stands aside.
        private static bool s_growNoted;

        [HarmonyPatch(typeof(Plant), "HaveGrowSpace")]
        private static class Plant_HaveGrowSpace_IgnoreSnapSlab
        {
            [HarmonyPrefix]
            private static void Prefix(out BoxCollider __state)
            {
                __state = null;
                if (s_anchor == null || !s_anchor.activeInHierarchy) return;
                var col = s_anchor.GetComponent<BoxCollider>();
                if (col == null || !col.enabled) return;
                col.enabled = false;
                __state = col;
                if (!s_growNoted) { s_growNoted = true; FiresLogger.LogDebug("[FAT] super flat grid: the build-snap slab stands aside for plant grow-space checks (it is not a plant's neighbour)"); }
            }

            [HarmonyFinalizer]
            private static System.Exception Finalizer(System.Exception __exception, BoxCollider __state)
            {
                if (__state != null) __state.enabled = true;
                return __exception;
            }
        }

        private static void EnsureAnchor()
        {
            if (s_anchor != null) return;
            s_anchor = new GameObject("FAT_SuperFlatGridSnap");
            s_anchor.SetActive(false);
            Object.DontDestroyOnLoad(s_anchor);
            int layer = LayerMask.NameToLayer("piece_nonsolid");
            s_anchor.layer = layer >= 0 ? layer : 0;
            var col = s_anchor.AddComponent<BoxCollider>();
            col.isTrigger = false;
            var piece = s_anchor.AddComponent<Piece>();
            piece.m_name = "Build grid";
            piece.m_canBeRemoved = false;
            FiresLogger.LogInfo("[FAT] super flat grid: snap anchor built (client only, never networked)");
        }

        private static void RebuildSnap(Vector3 aim, float size, Vector2Int zone, int area)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            s_snapSize = size;
            s_snapCell = new Vector2Int(Mathf.FloorToInt((aim.x - ZoneEdgeOffset) / size), Mathf.FloorToInt((aim.z - ZoneEdgeOffset) / size));
            int side = (int)Mathf.Sqrt(MaxSnapPoints);   // 32 x 32
            float y = FiresWorldProfile.SuperFlatHeight;
            float minX = (zone.x - area) * ZoneSize - ZoneEdgeOffset, maxX = minX + (area * 2 + 1) * ZoneSize;
            float minZ = (zone.y - area) * ZoneSize - ZoneEdgeOffset, maxZ = minZ + (area * 2 + 1) * ZoneSize;
            float cx = Snap(aim.x, size), cz = Snap(aim.z, size);
            s_anchor.transform.position = new Vector3(cx, y - 0.25f, cz);   // first: the points are its children
            int used = 0;
            for (int j = 0; j < side; j++)
                for (int i = 0; i < side; i++)
                {
                    float x = cx + (i - side / 2) * size, z = cz + (j - side / 2) * size;
                    if (x < minX || x > maxX || z < minZ || z > maxZ) continue;
                    Transform t;
                    if (used < s_points.Count) t = s_points[used];
                    else { var go = new GameObject("_snappoint") { tag = "snappoint" }; go.transform.SetParent(s_anchor.transform, false); go.SetActive(false); t = go.transform; s_points.Add(t); }
                    t.position = new Vector3(x, y, z);
                    if (!t.gameObject.activeSelf) t.gameObject.SetActive(true);
                    used++;
                }
            for (int k = used; k < s_points.Count; k++) if (s_points[k].gameObject.activeSelf) s_points[k].gameObject.SetActive(false);
            SnapPointCount = used;
            // the overlap collider: a thin slab just UNDER the flat ground across the window, so the place ray hits the ground first
            float span = side * size;
            var col = s_anchor.GetComponent<BoxCollider>();
            col.size = new Vector3(span, 0.1f, span);
            col.center = Vector3.zero;
            LastRebuildMs = (float)sw.Elapsed.TotalMilliseconds;
            FiresLogger.LogVerbose($"[FAT] super flat grid: {used} snap point(s) around ({cx:0.##},{cz:0.##}) at {size:0.##} m in {LastRebuildMs:0.00} ms");
        }
    }
}
