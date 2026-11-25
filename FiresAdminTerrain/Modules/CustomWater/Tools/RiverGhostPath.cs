using System.Collections.Generic;
using TMPro;
using UnityEngine;
using VerdantsAscent.Terrain.Voxel;

namespace VerdantsAscent.Water.Tools
{
    // ── Vriver ghost path (0.2.14xx, [ghost]; lead 11-17 rivers diff row R1, agreed with [worldgen]) ─────────────────────────────
    // While a Vriver chain is being placed (click a start, Shift+click spine points, click the end = carve), the river it WOULD carve
    // is drawn from the placed points to the crosshair, before anything is dug: the water line (where the water will stand), both
    // banks, and plain labels on the spot: "waterfall here: 3.2 m" at each cliff, and at the crosshair end "joins the sea",
    // "flows into water", "cuts N m into the ground" (uphill, the bed never rises) or "blocked: no-build area" / "blocked: ward".
    // Our own code and our aim-and-click concept, no new mode: it only shows while a chain exists, and the drag mode keeps its ring.
    //
    // A PURE DRY RUN of the carve's own planner, the same inputs in the same order (WaterToolDriver.River.Carve + PumpRiver pass 1):
    //   samples every max(min(ra, rb) / 4, 0.5) m between points, the width eased between the points' radii; each sample's ground read
    //   at its 1 m cell centre with the same hint and reach as the carve's StrokeSurface (VoxelWorld.TryFindSurfaceY, the point's own
    //   height when there is no voxel ground); then WaterShapingTools.RiverBedProfile (bed = ground - depth, never rising, eased). The
    //   water stands at bed + depth - RiverFreeboard (RiverStep), and a fall goes where one sample's bed is FallDrop or more below
    //   the last at FallSlope or steeper (RiverStep's rule). Nothing here edits terrain, water or ZDOs; RiverStep is never called.
    // Recomputed only when the crosshair moves 0.5 m, the width or depth changes, or the chain changes (at most 10 times a second);
    // drawn every frame from the cached result. Hidden the frame the River tool stops drawing it.
    internal sealed class RiverGhostPath : MonoBehaviour
    {
        private const float SurfaceReach = 12f;       // = WaterToolDriver.SurfaceReach
        private const float RecomputeMove = 0.5f, RecomputeEvery = 0.1f;
        private const float DeepCutWarn = 3f;         // a bed this much deeper than the set depth (uphill) is called out
        private const float LineWidth = 0.18f, BankWidth = 0.08f;
        private static readonly Color WaterColor = new Color(0.30f, 0.65f, 1f, 0.95f), BankColor = new Color(0.85f, 0.6f, 0.3f, 0.8f);
        private static readonly Color FallColor = new Color(1f, 1f, 1f, 1f), WarnColor = new Color(1f, 0.35f, 0.25f, 1f);

        private static RiverGhostPath s_view;
        private LineRenderer _water, _bankL, _bankR;
        // R2 (true-result preview): the water surface the carve will lay, as a see-through strip at its level (RiverStep lays water
        // where level - bed > WaterMinDepth, the disk clamped to the width); orange where RiverStep must BUILD the banks up first
        // (its own rule: the ground under the sample is below the bank height). Vertex colours, Sprites/Default (no dash texture).
        private Mesh _strip;
        private static Material s_stripMat;
        private static readonly Color StripWater = new Color(0.30f, 0.62f, 1f, 0.35f), StripFill = new Color(1f, 0.6f, 0.2f, 0.5f);
        private static readonly List<Vector3> s_sv = new List<Vector3>();
        private static readonly List<Color> s_sc = new List<Color>();
        private static readonly List<int> s_st = new List<int>();
        private readonly List<TextMeshPro> _labels = new List<TextMeshPro>();
        private int _drawnFrame, _labelsUsed;

        // the cached dry run
        private static readonly List<Vector3> s_samples = new List<Vector3>();
        private static readonly List<float> s_widths = new List<float>();
        private static float[] s_beds;
        private static Vector3 s_lastCursor = new Vector3(float.NaN, 0f, 0f);
        private static float s_lastRadius, s_lastDepth, s_lastAt;
        private static int s_lastChainHash;

        /// <summary>From the River driver's DrawExtras every frame (chain = the placed points, radii = their widths).</summary>
        internal static void Draw(Material mat, List<Vector3> chain, List<float> radii, Vector3 cursor, float cursorRadius, float depthSetting)
        {
            if (chain == null || chain.Count == 0) { Hide(); return; }
            if (s_view == null)
            {
                var go = new GameObject("FAT_RiverGhostPath");
                DontDestroyOnLoad(go);
                s_view = go.AddComponent<RiverGhostPath>();
                s_view._water = NewLine(go.transform, mat, WaterColor, LineWidth);
                s_view._bankL = NewLine(go.transform, mat, BankColor, BankWidth);
                s_view._bankR = NewLine(go.transform, mat, BankColor, BankWidth);
                var sgo = new GameObject("water");
                sgo.transform.SetParent(go.transform, false);
                s_view._strip = new Mesh { name = "FAT_RiverGhostWater" };
                s_view._strip.MarkDynamic();
                sgo.AddComponent<MeshFilter>().sharedMesh = s_view._strip;
                var mr = sgo.AddComponent<MeshRenderer>();
                if (s_stripMat == null) s_stripMat = new Material(Shader.Find("Sprites/Default")) { name = "FAT_RiverGhostWater" };
                mr.sharedMaterial = s_stripMat;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
            }
            if (!s_view.gameObject.activeSelf) s_view.gameObject.SetActive(true);
            s_view._drawnFrame = Time.frameCount;

            float depth = WaterShapingTools.ClampRiverDepth(depthSetting);
            int chainHash = ChainHash(chain, radii);
            bool moved = float.IsNaN(s_lastCursor.x) || (cursor - s_lastCursor).sqrMagnitude > RecomputeMove * RecomputeMove;
            bool changed = chainHash != s_lastChainHash || !Mathf.Approximately(cursorRadius, s_lastRadius) || !Mathf.Approximately(depth, s_lastDepth);
            if ((moved || changed) && (changed || Time.unscaledTime - s_lastAt >= RecomputeEvery))
            {
                s_lastCursor = cursor; s_lastRadius = cursorRadius; s_lastDepth = depth; s_lastChainHash = chainHash; s_lastAt = Time.unscaledTime;
                Plan(chain, radii, cursor, cursorRadius, depth);
                s_view.Render(depth, cursor);
            }
            s_view.FaceLabels();
        }

        internal static void Hide()
        {
            if (s_view != null && s_view.gameObject.activeSelf) s_view.gameObject.SetActive(false);
            s_lastCursor.x = float.NaN;
        }

        // ── the dry run (the carve's planner, read only) ─────────────────────────────────────────────────────────────────────────
        private static void Plan(List<Vector3> chain, List<float> radii, Vector3 cursor, float cursorRadius, float depth)
        {
            s_samples.Clear(); s_widths.Clear();
            int n = chain.Count + 1;
            for (int i = 1; i < n; i++)
            {
                Vector3 a = chain[i - 1], b = i < chain.Count ? chain[i] : cursor;
                float ra = radii[i - 1], rb = i < chain.Count ? radii[i] : cursorRadius;
                float len = new Vector2(b.x - a.x, b.z - a.z).magnitude;
                float step = Mathf.Max(Mathf.Min(ra, rb) * 0.25f, 0.5f);
                int m = Mathf.Max(1, Mathf.CeilToInt(len / step));
                for (int k = i == 1 ? 0 : 1; k <= m; k++)
                {
                    float t = k / (float)m;
                    Vector3 at = Vector3.Lerp(a, b, t);
                    int cx = Mathf.FloorToInt(at.x), cz = Mathf.FloorToInt(at.z);
                    at.y = VoxelWorld.TryFindSurfaceY(cx + 0.5f, cz + 0.5f, at.y, SurfaceReach, out float y) ? y : at.y;   // as StrokeSurface
                    s_samples.Add(at);
                    s_widths.Add(Mathf.Lerp(ra, rb, t * t * (3f - 2f * t)));
                }
            }
            s_beds = WaterShapingTools.RiverBedProfile(s_samples, depth);
        }

        private void Render(float depth, Vector3 cursor)
        {
            int n = s_samples.Count;
            if (n < 2 || s_beds == null || s_beds.Length != n) { _water.positionCount = 0; _bankL.positionCount = 0; _bankR.positionCount = 0; _strip.Clear(); HideLabels(0); return; }
            _water.positionCount = n; _bankL.positionCount = n; _bankR.positionCount = n;
            _labelsUsed = 0;
            float deepest = 0f; int deepAt = -1;
            float mostFill = 0f; int fillAt = -1;   // R2: the most bank RiverStep has to build up
            float dug = 0f, filled = 0f;            // R3: rough m³ the carve digs and fills
            bool blockedNoBuild = false, blockedWard = false;
            s_sv.Clear(); s_sc.Clear(); s_st.Clear();
            int prevPair = -1;
            for (int i = 0; i < n; i++)
            {
                Vector3 s = s_samples[i];
                float bank = s_beds[i] + depth, level = bank - WaterShapingTools.RiverFreeboard;
                Vector3 dir = i < n - 1 ? s_samples[i + 1] - s : s - s_samples[i - 1];
                dir.y = 0f;
                Vector3 side = dir.sqrMagnitude > 1e-4f ? Vector3.Cross(Vector3.up, dir.normalized) * s_widths[i] : Vector3.right * s_widths[i];
                _water.SetPosition(i, new Vector3(s.x, level + 0.05f, s.z));
                _bankL.SetPosition(i, new Vector3(s.x, bank, s.z) - side);
                _bankR.SetPosition(i, new Vector3(s.x, bank, s.z) + side);
                // R2: RiverStep builds the banks up where the ground is under the bank (top < bank - 0.05), and lays water where
                // level - bed > WaterMinDepth
                float fill = bank - s.y;
                bool builds = fill > 0.05f;
                if (builds && fill > mostFill) { mostFill = fill; fillAt = i; }
                // R3: rough earth moved along this sample's stretch, from RiverStep's own shapes: SmoothBowl digs from the ground (or the
                // built-up bank) to the bed, full depth out to `floor` and sloping to the outer bank, so a cross-section of about
                // depth x (floor + outer); FillTo raises a cylinder of radius `outer` from the ground to the bank (cross-section ~ 2 x outer x fill)
                if (i > 0)
                {
                    float len = new Vector2(s.x - s_samples[i - 1].x, s.z - s_samples[i - 1].z).magnitude;
                    float w = s_widths[i], outer = w * WaterShapingTools.BankSpread, floor = Mathf.Max(w * 0.35f, w - 2.5f);   // 2.5 = RiverStep's EndPoolEdge
                    float top = builds ? bank : s.y;
                    if (top > s_beds[i]) dug += len * (top - s_beds[i]) * (floor + outer);
                    if (builds) filled += len * fill * 2f * outer;
                }
                if (level - s_beds[i] > WaterShapingTools.WaterMinDepth)
                {
                    Vector3 c = new Vector3(s.x, level + 0.03f, s.z);
                    Color col = builds ? StripFill : StripWater;
                    int at = s_sv.Count;
                    s_sv.Add(c - side); s_sv.Add(c + side); s_sc.Add(col); s_sc.Add(col);
                    if (prevPair >= 0) { s_st.Add(prevPair); s_st.Add(at); s_st.Add(prevPair + 1); s_st.Add(prevPair + 1); s_st.Add(at); s_st.Add(at + 1); }
                    prevPair = at;
                }
                else prevPair = -1;
                float cut = s.y - s_beds[i] - depth;   // how much deeper than the set depth the bed sits under this ground
                if (cut > deepest) { deepest = cut; deepAt = i; }
                if (i % 4 == 0)
                {
                    if (!blockedNoBuild && Location.IsInsideNoBuildLocation(s)) blockedNoBuild = true;
                    if (!blockedWard && !PrivateArea.CheckAccess(s, s_widths[i], false, false)) blockedWard = true;
                }
                if (i > 0)
                {
                    // RiverStep's fall rule on the dry-run beds
                    float run = new Vector2(s.x - s_samples[i - 1].x, s.z - s_samples[i - 1].z).magnitude;
                    float drop = s_beds[i - 1] - s_beds[i];
                    if (drop >= WaterShapingTools.FallDrop && (run > 0.01f ? drop / run : 0f) >= WaterShapingTools.FallSlope)
                        Label(new Vector3(s.x, level + 2f, s.z), $"waterfall here: {drop:0.0} m", FallColor);
                }
            }
            // the crosshair end: where this river goes
            Vector3 end = s_samples[n - 1];
            float endLevel = s_beds[n - 1] + depth - WaterShapingTools.RiverFreeboard;
            string endText; Color endColor = WaterColor;
            if (blockedNoBuild) { endText = "blocked: no-build area on the way"; endColor = WarnColor; }
            else if (blockedWard) { endText = "blocked: a ward on the way"; endColor = WarnColor; }
            else if (FatWaterAt(end, out string body, out float bodyY)) endText = $"flows into {body} (water at y {bodyY:0.0})";
            else if (ZoneSystem.instance != null && endLevel <= ZoneSystem.instance.m_waterLevel + 0.5f) endText = "joins the sea";
            else if (WaterBelow(end, endLevel)) endText = "flows into water";
            else endText = $"water at y {endLevel:0.0}";
            if (deepAt >= 0 && deepest >= DeepCutWarn)
            {
                Vector3 d = s_samples[deepAt];
                Label(new Vector3(d.x, d.y + 2.5f, d.z), $"cuts {deepest + depth:0} m into the ground here (a river never runs uphill)", WarnColor);
            }
            // R2: where the carve builds the banks up most (orange water on the ghost)
            if (fillAt >= 0 && mostFill >= 1f)
            {
                Vector3 f = s_samples[fillAt];
                Label(new Vector3(f.x, f.y + 2f, f.z), $"builds the banks up {mostFill:0.0} m here (orange)", StripFill);
            }
            // R4: the end higher than the start, in the words of FAT's own two-click warning (WaterShapingTools)
            Vector3 start = s_samples[0];
            if (end.y > start.y + 1f && !blockedNoBuild && !blockedWard)
                Label(new Vector3(start.x, start.y + 3f, start.z), $"the end is {end.y - start.y:0.0} m higher than this start: its water will not flow uphill. Start at the higher end.", WarnColor);
            Label(new Vector3(end.x, end.y + 3f, end.z), endText, endColor);
            // R3: the earth this carve moves, rounded (an estimate from RiverStep's shapes, not a measurement), and the world's own
            // vegetation the carve clears (the same test as WaterShapingTools.ClearCarvedVegetation, read only)
            int veg = CountCarvedVegetation();
            Label(new Vector3(end.x, end.y + 2.2f, end.z), $"digs about {Round(dug)} m3, fills about {Round(filled)} m3"
                + (veg > 0 ? $", clears {veg} plant(s) / tree(s)" : ""), BankColor);
            HideLabels(_labelsUsed);
            _strip.Clear();
            if (s_st.Count > 0) { _strip.SetVertices(s_sv); _strip.SetColors(s_sc); _strip.SetTriangles(s_st, 0); _strip.RecalculateBounds(); }
            Color line = blockedNoBuild || blockedWard ? WarnColor : WaterColor;
            _water.startColor = line; _water.endColor = line;
        }

        // FAT's own water at the end ([worldgen]'s 1455 query, WaterLookHost.TryWaterBodyAt: tool water first, else generated water):
        // a FAT body is a pond under 32 m across, else a lake; generated water says what it is (river / stream / lake / pond)
        private static bool FatWaterAt(Vector3 at, out string body, out float surfaceY)
        {
            body = null;
            if (!VerdantsAscent.Water.Core.WaterLookHost.TryWaterBodyAt(at, out surfaceY, out float size, out int kind) || kind < 0) return false;
            body = kind == 1 ? (size < 32f ? "a pond" : "a lake") : GeneratedKind(at);
            return true;
        }

        private static string GeneratedKind(Vector3 at)
        {
            try
            {
                int c = VerdantsAscent.Terrain.Voxel.GenWaterField.WaterClassAt(at.x, at.z);
                if (c == VerdantsAscent.Terrain.Voxel.FiresWorldGenBase.WC_RIVER) return "a river";
                if (c == VerdantsAscent.Terrain.Voxel.FiresWorldGenBase.WC_STREAM) return "a stream";
                if (c == VerdantsAscent.Terrain.Voxel.FiresWorldGenBase.WC_LAKE) return "a lake";
                if (c == VerdantsAscent.Terrain.Voxel.FiresWorldGenBase.WC_POND) return "a pond";
            }
            catch { }
            return "water";
        }

        // open water at the end already (sea, a lake, a drawn river): vanilla's liquid level there, above the
        // ground and close to the planned level
        private static bool WaterBelow(Vector3 at, float level)
        {
            float liquid = Floating.GetLiquidLevel(new Vector3(at.x, level + 2f, at.z), 1f, LiquidType.Water);
            return liquid > at.y + 0.2f && liquid > level - 3f;
        }

        // ── drawing helpers ───────────────────────────────────────────────────────────────────────────────────────────────────────
        private static LineRenderer NewLine(Transform parent, Material mat, Color c, float width)
        {
            var go = new GameObject("line");
            go.transform.SetParent(parent, false);
            var lr = go.AddComponent<LineRenderer>();
            lr.sharedMaterial = mat;
            lr.useWorldSpace = true;
            lr.positionCount = 0;
            lr.startWidth = width; lr.endWidth = width;
            lr.startColor = c; lr.endColor = c;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            return lr;
        }

        private static TMP_FontAsset s_font;

        private void Label(Vector3 at, string text, Color color)
        {
            if (_labelsUsed >= 24) return;
            TextMeshPro t;
            if (_labelsUsed < _labels.Count) t = _labels[_labelsUsed];
            else
            {
                if (s_font == null) s_font = Hud.instance != null ? Hud.instance.GetComponentInChildren<TMP_Text>(true)?.font : null;
                var go = new GameObject("label");
                go.transform.SetParent(transform, false);
                t = go.AddComponent<TextMeshPro>();
                if (s_font != null) t.font = s_font;
                t.fontSize = 3f;
                t.alignment = TextAlignmentOptions.Center;
                t.enableWordWrapping = false;
                _labels.Add(t);
            }
            _labelsUsed++;
            if (!t.gameObject.activeSelf) t.gameObject.SetActive(true);
            t.transform.position = at;
            if (t.text != text) t.text = text;
            t.color = color;
        }

        private void HideLabels(int from)
        {
            for (int i = from; i < _labels.Count; i++) if (_labels[i] != null && _labels[i].gameObject.activeSelf) _labels[i].gameObject.SetActive(false);
        }

        private void FaceLabels()
        {
            var cam = Utils.GetMainCamera();
            if (cam == null) return;
            for (int i = 0; i < _labelsUsed && i < _labels.Count; i++)
                if (_labels[i] != null) _labels[i].transform.rotation = Quaternion.LookRotation(_labels[i].transform.position - cam.transform.position);
        }

        // RiverStep → RefreshClutter(surface, outer, carved) → ClearCarvedVegetation(surface, outer): within outer (width x BankSpread) of a
        // sample in XZ and outer + 6 m in height, IsRefusedInstance (components / names only, no water needed, so the count is exact for
        // what is loaded now), and not player-made (ZDO creator 0). Counted once each; destroys nothing. Pre-filtered by the path's box.
        // (RiverStep clears at every carved sample; a sample the dry run says is not dug still clears in RiverStep, as its bowl always cuts.)
        private static int CountCarvedVegetation()
        {
            if (ZNetScene.instance == null || ZNetScene.instance.m_instances == null || s_samples.Count == 0) return 0;
            float maxOuter = 0f;
            Vector3 lo = s_samples[0], hi = s_samples[0];
            for (int i = 0; i < s_samples.Count; i++)
            {
                maxOuter = Mathf.Max(maxOuter, s_widths[i] * WaterShapingTools.BankSpread);
                lo = Vector3.Min(lo, s_samples[i]); hi = Vector3.Max(hi, s_samples[i]);
            }
            lo -= new Vector3(maxOuter, maxOuter + 6f, maxOuter); hi += new Vector3(maxOuter, maxOuter + 6f, maxOuter);
            int count = 0;
            foreach (var kv in ZNetScene.instance.m_instances)
            {
                var view = kv.Value;
                if (view == null) continue;
                var go = view.gameObject;
                var p = go.transform.position;
                if (p.x < lo.x || p.x > hi.x || p.z < lo.z || p.z > hi.z || p.y < lo.y || p.y > hi.y) continue;
                bool inside = false;
                for (int i = 0; i < s_samples.Count && !inside; i++)
                {
                    var s = s_samples[i];
                    float outer = s_widths[i] * WaterShapingTools.BankSpread;
                    float dx = p.x - s.x, dz = p.z - s.z;
                    inside = dx * dx + dz * dz <= outer * outer && Mathf.Abs(p.y - s.y) <= outer + 6f;
                }
                if (!inside || !VerdantsAscent.Terrain.Voxel.FatUnderWaterPlacement.IsRefusedInstance(go)) continue;
                var zdo = view.GetZDO();
                if (zdo != null && zdo.GetLong(ZDOVars.s_creator) != 0L) continue;
                count++;
            }
            return count;
        }

        // two significant figures: an estimate should not look like a measurement
        private static string Round(float v)
        {
            if (v < 10f) return Mathf.RoundToInt(v).ToString();
            float mag = Mathf.Pow(10f, Mathf.Floor(Mathf.Log10(v)) - 1f);
            return (Mathf.Round(v / mag) * mag).ToString("0");
        }

        private static int ChainHash(List<Vector3> chain, List<float> radii)
        {
            unchecked
            {
                int h = chain.Count;
                for (int i = 0; i < chain.Count; i++) h = h * 31 + chain[i].GetHashCode() + (i < radii.Count ? radii[i].GetHashCode() : 0);
                return h;
            }
        }

        private void LateUpdate()
        {
            if (Time.frameCount > _drawnFrame + 1) { gameObject.SetActive(false); s_lastCursor.x = float.NaN; }
        }
    }
}
