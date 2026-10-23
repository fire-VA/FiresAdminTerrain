using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEngine;
using VerdantsAscent.Terrain.Voxel;

namespace VerdantsAscent.Terrain.Utilities
{
    // For finding what draws an unexplained overlay when no slider changes it (TossinShade's
    // master toggle off included). `fat_what_renders [radius]`: read-only. Takes the point the camera looks at (where the
    // view ray first drops under a water surface, else the first collider hit) and lists every enabled renderer whose bounds cover it:
    // object path, layer, render queue, material and shader names, and which mod's components sit on it (by assembly). Then every
    // script on the main camera (image effects can paint over the whole screen). Instanced draws (Graphics.Draw*) and command buffers
    // have no renderer and are listed only as a count of camera command buffers. Written to the console and the log.
    internal static class WhatRendersProbe
    {
        internal static void Register()
        {
            new Terminal.ConsoleCommand("fat_what_renders", "[radius m, default 2] - list every renderer, material, shader and owning mod at the point you look at (read-only)",
                args =>
                {
                    float radius = 2f;
                    if (args.Args.Length > 1) float.TryParse(args.Args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out radius);
                    foreach (var line in Run(Mathf.Clamp(radius, 0.1f, 50f)))
                    {
                        FiresLogger.LogInfo(line);
                        args.Context?.AddString(line);
                    }
                });
        }

        // `fat_hide <name part> [on|off]` - render-only, this session: hides (or shows again) every renderer whose object path
        // contains the text, so suspects can be switched off one by one (e.g. "Terrain_lod", "genwater", "FarWater"). Bare: list what is hidden.
        private static readonly List<Renderer> s_hidden = new List<Renderer>();
        internal static void RegisterHide()
        {
            new Terminal.ConsoleCommand("fat_hide", "<name part> [off] - hide every renderer whose path contains the text (render only, this session); off shows them again",
                args =>
                {
                    void Say(string l) { FiresLogger.LogInfo(l); args.Context?.AddString(l); }
                    if (args.Args.Length < 2) { Say($"[fat_hide] {s_hidden.Count} renderer(s) hidden; usage: fat_hide <name part> [off]"); return; }
                    string part = args.Args[1]; bool show = args.Args.Length > 2 && args.Args[2].Equals("off", System.StringComparison.OrdinalIgnoreCase);
                    int n = 0;
                    if (show)
                    {
                        for (int i = s_hidden.Count - 1; i >= 0; i--)
                        {
                            var r = s_hidden[i];
                            if (r == null) { s_hidden.RemoveAt(i); continue; }
                            if (Path(r.transform).IndexOf(part, System.StringComparison.OrdinalIgnoreCase) < 0) continue;
                            r.enabled = true; s_hidden.RemoveAt(i); n++;
                        }
                        Say($"[fat_hide] shown again: {n} renderer(s) matching '{part}'");
                        return;
                    }
                    var names = new HashSet<string>();
                    foreach (var r in Object.FindObjectsOfType<Renderer>())
                    {
                        if (r == null || !r.enabled) continue;
                        string p = Path(r.transform);
                        if (p.IndexOf(part, System.StringComparison.OrdinalIgnoreCase) < 0) continue;
                        r.enabled = false; s_hidden.Add(r); n++;
                        if (names.Count < 8) names.Add(p);
                    }
                    Say($"[fat_hide] hidden: {n} renderer(s) matching '{part}' (e.g. {string.Join("; ", names)}). Undo: fat_hide {part} off");
                });
        }

        private static IEnumerable<string> Run(float radius)
        {
            var cam = global::Utils.GetMainCamera();
            if (cam == null) { yield return "[fat_what_renders] no main camera"; yield break; }
            var o = cam.transform.position; var d = cam.transform.forward;
            Vector3 at = Vector3.zero; string how = null;
            for (float s = 0.5f; s <= 400f; s += 0.5f)
            {
                var p = o + d * s;
                if (FiresWorldGenBase.Active && FiresWorldGenBase.TryGetWaterSurfaceGen(p.x, p.z, out float wl, out float wd) && wd > 0f && p.y <= wl)
                { at = new Vector3(p.x, wl, p.z); how = $"water surface {s:0} m ahead"; break; }
            }
            if (how == null && Physics.Raycast(o, d, out var hit, 1000f)) { at = hit.point; how = $"collider {hit.collider.name} {hit.distance:0} m ahead"; }
            if (how == null) { yield return "[fat_what_renders] the view hits nothing within 400 m"; yield break; }
            yield return string.Format(CultureInfo.InvariantCulture, "[fat_what_renders] point ({0:0.0},{1:0.00},{2:0.0}) = {3}; radius {4:0.0} m; water class {5}, biome code {6}",
                at.x, at.y, at.z, how, radius, FiresWorldGenBase.Active ? FiresWorldGenBase.WaterClassAt(at.x, at.z) : -1, FiresWorldGenBase.Active ? FiresWorldGenBase.BiomeCodeAt(at.x, at.z) : -1);

            var box = new Bounds(at, new Vector3(radius * 2f, radius * 2f, radius * 2f));
            int n = 0;
            foreach (var r in Object.FindObjectsOfType<Renderer>())
            {
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy || !r.bounds.Intersects(box)) continue;
                if (r is ParticleSystemRenderer) continue;
                n++;
                var sb = new StringBuilder();
                sb.Append("  ").Append(Path(r.transform)).Append(" [").Append(r.GetType().Name).Append(", layer ").Append(LayerMask.LayerToName(r.gameObject.layer)).Append(']');
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null) { sb.Append(" | (null material)"); continue; }
                    sb.Append(" | mat '").Append(m.name).Append("' shader '").Append(m.shader != null ? m.shader.name : "?").Append("' queue ").Append(m.renderQueue);
                }
                sb.Append(" | mods: ").Append(Owners(r.gameObject));
                yield return sb.ToString();
            }
            yield return $"[fat_what_renders] {n} renderer(s) cover that point";

            var cs = new StringBuilder("[fat_what_renders] main camera scripts: ");
            foreach (var c in cam.GetComponents<Component>())
                if (c != null && !(c is Transform)) cs.Append(c.GetType().FullName).Append(" (").Append(c.GetType().Assembly.GetName().Name).Append(c is Behaviour b && !b.enabled ? ", off" : "").Append("); ");
            yield return cs.ToString();
            yield return $"[fat_what_renders] camera command buffers: {cam.commandBufferCount} (instanced and command-buffer draws have no renderer to list)";
        }

        private static string Path(Transform t)
        {
            var parts = new List<string>();
            for (int i = 0; t != null && i < 6; i++, t = t.parent) parts.Add(t.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        // The assemblies of the non-Unity components on the object and its parents: which mod built it.
        private static string Owners(GameObject go)
        {
            var set = new HashSet<string>();
            for (var t = go.transform; t != null; t = t.parent)
                foreach (var c in t.GetComponents<Component>())
                {
                    if (c == null) continue;
                    string a = c.GetType().Assembly.GetName().Name;
                    if (a.StartsWith("UnityEngine")) continue;
                    set.Add(a);
                }
            return set.Count == 0 ? "(Unity only)" : string.Join(", ", set.OrderBy(x => x));
        }
    }
}
