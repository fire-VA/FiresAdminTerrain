using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace VerdantsAscent.Terrain
{
    /// <summary>
    /// 0.2.1207 (lead 10-28, ice crack at 288,862 still showing a thin wedge on FAT 1206): `fires_mesh_at x z [r]` - READ-ONLY.
    /// Lists every rendered mesh whose bounds cover the column at (x, z): object path, components, layer, shader, enabled, and the
    /// mesh's own vertices within r m of the column (world space: nearest few + min/max height). A crack between two meshes shows
    /// as two owners whose edge heights disagree, or one owner with a hole. Writes the full list to FAT's cache folder.
    /// </summary>
    internal static class FatMeshAtCommand
    {
        private const string Command = "fires_mesh_at";
        private static bool s_registered;
        private const string FatVersion = global::VerdantsAscent.FiresAdminTerrain.PluginVersion;

        internal static void Register()
        {
            if (s_registered) return;
            s_registered = true;
            new Terminal.ConsoleCommand(Command,
                "Read-only: every mesh drawn at a column, with its vertex heights near it. Usage: " + Command + " x z [radius m, default 2]",
                args =>
                {
                    var inv = System.Globalization.CultureInfo.InvariantCulture;
                    if (args.Length < 3 || !float.TryParse(args[1], System.Globalization.NumberStyles.Float, inv, out float x)
                        || !float.TryParse(args[2], System.Globalization.NumberStyles.Float, inv, out float z))
                    { args.Context?.AddString("Usage: " + Command + " x z [radius]"); return; }
                    float r = 2f;
                    if (args.Length > 3) float.TryParse(args[3], System.Globalization.NumberStyles.Float, inv, out r);
                    string path = Run(x, z, Mathf.Clamp(r, 0.25f, 16f), out int owners);
                    args.Context?.AddString($"{Command}: {owners} mesh(es) at {x:0.##} {z:0.##}; list: {path}");
                },
                isCheat: true);

            // 0.2.1208 (lead: "exact beats my estimate, we'll need it for every visual bug"): a pixel of a shot -> the world spot.
            // px/py are shot pixels from the top-left; with sw/sh given (default 3440 1440) they are scaled to this screen. Logs the
            // camera pose and every collider the ray passes (first 8), then runs the same listing at the first hit's x z.
            new Terminal.ConsoleCommand(ScreenCommand,
                "Read-only: " + Command + " at the world spot under a screen pixel. Usage: " + ScreenCommand + " px py [radius m, default 2] [shotWidth shotHeight, default 3440 1440]",
                args =>
                {
                    var inv = System.Globalization.CultureInfo.InvariantCulture;
                    var ns = System.Globalization.NumberStyles.Float;
                    if (args.Length < 3 || !float.TryParse(args[1], ns, inv, out float px) || !float.TryParse(args[2], ns, inv, out float py))
                    { args.Context?.AddString("Usage: " + ScreenCommand + " px py [radius] [shotWidth shotHeight]"); return; }
                    float r = 2f, sw = 3440f, sh = 1440f;
                    if (args.Length > 3) float.TryParse(args[3], ns, inv, out r);
                    if (args.Length > 5) { float.TryParse(args[4], ns, inv, out sw); float.TryParse(args[5], ns, inv, out sh); }
                    args.Context?.AddString(ScreenRun(px, py, Mathf.Clamp(r, 0.25f, 16f), sw, sh));
                },
                isCheat: true);
        }

        private const string ScreenCommand = "fires_mesh_at_screen";

        private static string ScreenRun(float px, float py, float r, float sw, float sh)
        {
            Camera cam = Utils.GetMainCamera();
            if (cam == null) return ScreenCommand + ": no main camera.";
            float sx = px * Screen.width / Mathf.Max(1f, sw), sy = Screen.height - py * Screen.height / Mathf.Max(1f, sh);
            Ray ray = cam.ScreenPointToRay(new Vector3(sx, sy, 0f));
            Vector3 cp = cam.transform.position, ce = cam.transform.eulerAngles;
            var hits = Physics.RaycastAll(ray, 500f, ~0, QueryTriggerInteraction.Ignore);
            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            var sb = new StringBuilder();
            sb.Append($"[FiresAdminTerrain] {ScreenCommand} {px:0} {py:0} (of {sw:0}x{sh:0} -> screen {sx:0},{sy:0} of {Screen.width}x{Screen.height}): camera ({cp.x:0.00},{cp.y:0.00},{cp.z:0.00}) yaw {ce.y:0.0} pitch {ce.x:0.0} vFOV {cam.fieldOfView:0.0}; ray dir ({ray.direction.x:0.000},{ray.direction.y:0.000},{ray.direction.z:0.000}); {hits.Length} collider hit(s):");
            for (int i = 0; i < Mathf.Min(8, hits.Length); i++)
                sb.Append($" [{hits[i].distance:0.00} m {PathOf(hits[i].collider.transform)} layer {LayerMask.LayerToName(hits[i].collider.gameObject.layer)} at ({hits[i].point.x:0.00},{hits[i].point.y:0.00},{hits[i].point.z:0.00})]");
            FiresLogger.LogInfo(sb.ToString());
            if (hits.Length == 0) return ScreenCommand + ": the ray hit no collider (camera pose in the log).";
            Vector3 p = hits[0].point;
            string path = Run(p.x, p.z, r, out int owners);
            return $"{ScreenCommand}: first hit {PathOf(hits[0].collider.transform)} at ({p.x:0.00},{p.y:0.00},{p.z:0.00}); {owners} mesh(es) there; list: {path}";
        }

        private static string Run(float x, float z, float r, out int owners)
        {
            owners = 0;
            var sb = new StringBuilder();
            sb.AppendLine($"# {Command} {x:0.###} {z:0.###} r {r:0.##}  {DateTime.Now:yyyy-MM-dd HH:mm:ss} FAT {FatVersion}");
            sb.AppendLine($"# 32 m chunk at x {Mathf.FloorToInt(x / 32f)}, z {Mathf.FloorToInt(z / 32f)}; distance to chunk borders x {Dist32(x):0.###} z {Dist32(z):0.###}");
            var near = new List<Vector3>();
            foreach (var mf in UnityEngine.Object.FindObjectsOfType<MeshFilter>())
            {
                var mr = mf.GetComponent<MeshRenderer>();
                Mesh mesh = mf.sharedMesh;
                if (mr == null || mesh == null) continue;
                Bounds b = mr.bounds;
                if (x < b.min.x - r || x > b.max.x + r || z < b.min.z - r || z > b.max.z + r) continue;
                owners++;
                string shader = mr.sharedMaterial != null && mr.sharedMaterial.shader != null ? mr.sharedMaterial.shader.name : "-";
                var comps = new StringBuilder();
                foreach (var c in mf.GetComponents<Component>()) if (c != null && !(c is Transform)) comps.Append(c.GetType().Name).Append(' ');
                sb.AppendLine($"== {PathOf(mf.transform)}  layer {LayerMask.LayerToName(mf.gameObject.layer)}  active {mf.gameObject.activeInHierarchy} rendered {mr.enabled}  shader {shader}  bounds y {b.min.y:0.###}..{b.max.y:0.###}  verts {(mesh.isReadable ? mesh.vertexCount.ToString() : "unreadable")}  [{comps.ToString().Trim()}]");
                if (!mesh.isReadable) continue;
                near.Clear();
                var verts = mesh.vertices;
                Matrix4x4 m = mf.transform.localToWorldMatrix;
                for (int i = 0; i < verts.Length; i++)
                {
                    Vector3 w = m.MultiplyPoint3x4(verts[i]);
                    float dx = w.x - x, dz = w.z - z;
                    if (dx * dx + dz * dz <= r * r) near.Add(w);
                }
                if (near.Count == 0) { sb.AppendLine("   no vertex within r (bounds cover it: a face spans the column)"); continue; }
                float lo = float.MaxValue, hi = float.MinValue;
                foreach (var w in near) { lo = Mathf.Min(lo, w.y); hi = Mathf.Max(hi, w.y); }
                near.Sort((a, c) => ((a.x - x) * (a.x - x) + (a.z - z) * (a.z - z)).CompareTo((c.x - x) * (c.x - x) + (c.z - z) * (c.z - z)));
                sb.Append($"   {near.Count} vertex(es) within r, y {lo:0.###}..{hi:0.###}; nearest:");
                for (int i = 0; i < Mathf.Min(12, near.Count); i++) sb.Append($" ({near[i].x:0.###},{near[i].y:0.###},{near[i].z:0.###})");
                sb.AppendLine();
            }
            string path;
            try
            {
                string dir = global::VerdantsAscent.FiresCachePaths.For("FiresAdminTerrain", "Census");
                System.IO.Directory.CreateDirectory(dir);
                path = System.IO.Path.Combine(dir, $"mesh_at_{x:0}_{z:0}_{DateTime.Now:HHmmss}.txt");
                System.IO.File.WriteAllText(path, sb.ToString());
            }
            catch (Exception ex) { path = "(not written: " + ex.Message + ")"; }
            FiresLogger.LogInfo($"[FiresAdminTerrain] {Command} {x:0.##} {z:0.##}: {owners} mesh(es) cover the column; list: {path}");
            FiresLogger.LogDebug($"[FiresAdminTerrain] {Command} proof:\n{sb}");
            return path;
        }

        private static float Dist32(float v) { float m = Mathf.Repeat(v, 32f); return Mathf.Min(m, 32f - m); }

        private static string PathOf(Transform t)
        {
            string s = t.name;
            for (var p = t.parent; p != null; p = p.parent) s = p.name + "/" + s;
            return s;
        }
    }
}
