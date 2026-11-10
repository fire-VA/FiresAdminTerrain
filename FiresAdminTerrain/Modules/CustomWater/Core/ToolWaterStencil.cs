using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    // 0.2.1068 (Fire 10-26: under a carved river, "the water mesh from underneith seems to be culled when we angle the camera
    // just right"). Suspected, NOT proven: Custom/FiresWater's stencil de-dup (_FiresStencilComp 3 = Equal, Pass Invert,
    // WriteMask 16) between overlapping tool-water surfaces; the first surface to draw a pixel claims it and later ones are
    // rejected there, and transparent draw order follows each renderer's bounds centre, so the winner flips with the angle.
    // Proof tool: `fat_water_stencil off` gives every tool-water material the pour's exemption (FiresLiquidWaterPrefab:
    // Comp 8 = Always, WriteMask 0); `dedup` puts each material's own values back; `status` lists what is drawn near you.
    // Session-only, client-local, nothing saved. Default = dedup, so nothing looks different until it is flipped.
    // Tool water is drawn by PlacedWaterSheets (one renderer per voxel chunk, the shared sheet material); the combiners'
    // own grid mesh is hidden while those draw, but non-water liquids still draw on the combiners, so both are covered.
    // Also: every tool-water renderer gets a fixed sortingOrder from a stable key, so the draw order between overlapping
    // surfaces can't flip as the camera turns, and allowOcclusionWhenDynamic off.
    internal static class ToolWaterStencil
    {
        private static readonly int CompId = Shader.PropertyToID("_FiresStencilComp");
        private static readonly int MaskId = Shader.PropertyToID("_FiresStencilWriteMask");
        private const float AlwaysComp = 8f, NoWrite = 0f;

        internal static bool Off { get; private set; }

        // Each material's own values, taken the first time it is seen, so `dedup` restores exactly what it had.
        private static readonly Dictionary<Material, (float comp, float mask)> s_original = new Dictionary<Material, (float, float)>();
        private static readonly HashSet<Renderer> s_ordered = new HashSet<Renderer>();
        private static bool s_orderLogged;

        private static bool Apply(Material m)
        {
            if (m == null || !m.HasProperty(CompId)) return false;
            if (!s_original.ContainsKey(m)) s_original[m] = (m.GetFloat(CompId), m.HasProperty(MaskId) ? m.GetFloat(MaskId) : 16f);
            if (Off)
            {
                m.SetFloat(CompId, AlwaysComp);
                if (m.HasProperty(MaskId)) m.SetFloat(MaskId, NoWrite);
            }
            else
            {
                var o = s_original[m];
                m.SetFloat(CompId, o.comp);
                if (m.HasProperty(MaskId)) m.SetFloat(MaskId, o.mask);
            }
            return true;
        }

        private static void Order(Renderer r, int stableKey)
        {
            if (r == null) return;
            r.sortingOrder = stableKey;
            if (r is MeshRenderer mr) mr.allowOcclusionWhenDynamic = false;
            if (s_ordered.Add(r) && !s_orderLogged && s_ordered.Count >= 8)
            {
                s_orderLogged = true;
                FiresLogger.LogDebug($"[FiresWater] combiner draw order fixed: {s_ordered.Count} renderer(s) (stable sortingOrder per surface, no dynamic occlusion)");
            }
        }

        // A stable sortingOrder from a position key. 0.2.1100 (Fire, shots 568/569: one chunk-shaped block of water fogged solid white,
        // the next clear, a hard straight line): sortingOrder sorts transparent renderers BEFORE distance, and this key ran
        // -15000..15000, so mist/fog particles (sortingOrder 0, same Transparent queue) drew OVER every chunk with a negative key and
        // UNDER every chunk with a positive one. All water now sorts below 0: placed sheets share one order (SheetOrder; they never
        // overlap since 0.2.1091), combiners keep a stable key in -15000..-2. Particles and fog always draw after the water.
        private const int SheetOrder = -1;
        private static int KeyOf(int x, int y, int z) => -2 - (int)((uint)(x * 73856093 ^ y * 19349663 ^ z * 83492791) % 15000u);

        // PlacedWaterSheets.MeshFor, each time a chunk's sheet renderer is made or its material set.
        internal static void OnSheet(Renderer r, Vector3Int key)
        {
            if (r == null) return;
            Order(r, SheetOrder);
            Apply(r.sharedMaterial);
            ReportFog();
        }

        // 0.2.1100: "[WaterFog] sheets n, materials n, queue q, sortingOrder s, fog ..." on change, at most every 30 s.
        private static string s_fogSaid;
        private static float s_fogAt;
        private static void ReportFog()
        {
            if (Time.unscaledTime < s_fogAt) return;
            s_fogAt = Time.unscaledTime + 30f;
            int sheets = 0, minQ = int.MaxValue, maxQ = int.MinValue, minS = int.MaxValue, maxS = int.MinValue;
            var mats = new HashSet<Material>();
            foreach (var r in PlacedWaterSheets.Renderers())
            {
                if (r == null) continue;
                sheets++;
                var m = r.sharedMaterial;
                mats.Add(m);
                int q = m != null ? m.renderQueue : -1;
                if (q < minQ) minQ = q; if (q > maxQ) maxQ = q;
                if (r.sortingOrder < minS) minS = r.sortingOrder; if (r.sortingOrder > maxS) maxS = r.sortingOrder;
            }
            if (sheets == 0) return;
            string line = $"[WaterFog] sheets {sheets}, materials {mats.Count}, queue {minQ}{(maxQ != minQ ? ".." + maxQ : "")}, sortingOrder {minS}{(maxS != minS ? ".." + maxS : "")} (fog/mist particles at 0 draw after), fog {(RenderSettings.fog ? RenderSettings.fogMode.ToString() : "off")}";
            if (line == s_fogSaid) return;
            s_fogSaid = line;
            FiresLogger.LogDebug(line);
        }

        // WaterMeshCombiner, after its material is set.
        internal static void OnCombiner(Renderer r, Vector3 position)
        {
            if (r == null) return;
            Order(r, KeyOf(Mathf.RoundToInt(position.x), Mathf.RoundToInt(position.y), Mathf.RoundToInt(position.z)));
            Apply(r.sharedMaterial);
        }

        private static IEnumerable<Renderer> AllRenderers()
        {
            foreach (var r in PlacedWaterSheets.Renderers()) if (r != null) yield return r;
            var all = WaterMeshCombiner.AllCombiners;
            if (all == null) yield break;
            foreach (var c in all)
            {
                var r = c != null ? c.CombinerRenderer : null;
                if (r != null) yield return r;
            }
        }

        private static int ApplyAll()
        {
            var seen = new HashSet<Material>();
            foreach (var r in AllRenderers())
                if (seen.Add(r.sharedMaterial)) Apply(r.sharedMaterial);
            int n = 0;
            foreach (var m in seen) if (m != null && m.HasProperty(CompId)) n++;
            return n;
        }

        private static void Status(Terminal term)
        {
            var cam = Camera.main;
            Vector3 eye = cam != null ? cam.transform.position : Vector3.zero;
            var inv = CultureInfo.InvariantCulture;
            int renderers = 0, visible = 0;
            var mats = new HashSet<Material>();
            var near = new List<(float d, string line)>();
            foreach (var r in AllRenderers())
            {
                renderers++;
                if (r.sharedMaterial != null) mats.Add(r.sharedMaterial);
                if (!r.enabled || !r.gameObject.activeInHierarchy) continue;
                if (r.isVisible) visible++;
                Bounds b = r.bounds;
                float d = Vector3.Distance(eye, b.center);
                if (d > 200f) continue;
                Material m = r.sharedMaterial;
                string st = m != null && m.HasProperty(CompId)
                    ? string.Format(inv, "comp {0:0} mask {1:0}", m.GetFloat(CompId), m.HasProperty(MaskId) ? m.GetFloat(MaskId) : -1f)
                    : "no stencil";
                near.Add((d, string.Format(inv, "{0} centre ({1:0.0}, {2:0.0}, {3:0.0}) size ({4:0.0}, {5:0.0}, {6:0.0}) {7:0} m, visible {8}, order {9}, shader {10}, {11}",
                    r.gameObject.name, b.center.x, b.center.y, b.center.z, b.size.x, b.size.y, b.size.z, d, r.isVisible, r.sortingOrder,
                    m != null && m.shader != null ? m.shader.name : "?", st)));
            }
            near.Sort((a, c) => a.d.CompareTo(c.d));
            string head = $"[FiresWater] tool-water stencil status: mode {(Off ? "off" : "dedup")}, {renderers} renderer(s) ({visible} visible), {mats.Count} material(s), {near.Count} within 200 m";
            FiresLogger.LogInfo(head);
            term?.AddString(head);
            for (int i = 0; i < near.Count && i < 20; i++)
            {
                FiresLogger.LogInfo("[FiresWater]   " + near[i].line);
                term?.AddString("  " + near[i].line);
            }
        }

        internal static void RegisterCommand()
        {
            new Terminal.ConsoleCommand("fat_water_stencil",
                "<dedup|off|status> - tool water's stencil de-dup: off = every tool-water surface draws every pixel (proof for the underwater holes), dedup = back to normal, status = what is drawn near you. Session only.",
                args =>
                {
                    string mode = args.Length > 1 ? args[1].ToLowerInvariant() : "status";
                    if (mode == "status") { Status(args.Context); return; }
                    if (mode != "off" && mode != "dedup") { args.Context?.AddString("fat_water_stencil <dedup|off|status>"); return; }
                    Off = mode == "off";
                    int n = ApplyAll();
                    string line = $"[FiresWater] tool-water stencil: {(Off ? "off" : "dedup")} on {n} combiner material(s)";
                    FiresLogger.LogInfo(line);
                    args.Context?.AddString(line);
                },
                isCheat: true);
        }
    }
}
