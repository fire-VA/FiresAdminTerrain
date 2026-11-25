using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // ── va_voxel_seamdump (0.2.1455, [ghost]; lead 11-17 Voxheim diff row P12, agreed with [worldgen]) ───────────────────────────
    // READ-ONLY. Writes the voxel chunk meshes around the player to one OBJ file (world space, one object per chunk, named by its
    // key), so a seam or a hole can be looked at in Blender instead of guessed from a shot. Next to it, one line per chunk: vertices,
    // triangles and OPEN EDGES inside the chunk (an edge only one triangle uses, away from the chunk's own outer faces): a crack or a hole
    // shows as open edges where there should be none. [worldgen]'s va_voxel_seamaudit gives the voxel-vs-vanilla numbers; this gives
    // the geometry. Console (devcommands): va_voxel_seamdump [radius m, default 48].
    internal static class VoxelSeamDump
    {
        private const string Command = "va_voxel_seamdump";
        private const float FaceTolerance = 0.05f;   // metres from a chunk's outer face that still counts as "on the face"
        private static bool s_registered;

        internal static void Register()
        {
            if (s_registered) return;
            s_registered = true;
            new Terminal.ConsoleCommand(Command,
                "Read-only: write the voxel chunk meshes around you to an OBJ file (for Blender), with open-edge counts per chunk. Usage: "
                + Command + " [radius m, default 48]",
                args =>
                {
                    float r = 48f;
                    if (args.Length > 1) float.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out r);
                    var player = Player.m_localPlayer;
                    if (player == null) { args.Context?.AddString(Command + ": no local player"); return; }
                    string line = Run(player.transform.position, Mathf.Clamp(r, 4f, 256f));
                    args.Context?.AddString(line);
                    FiresLogger.LogInfo("[FiresVoxel] " + line);
                },
                isCheat: true);
        }

        private static string Run(Vector3 at, float radius)
        {
            var inv = CultureInfo.InvariantCulture;
            var obj = new StringBuilder(1 << 20);
            var report = new StringBuilder();
            obj.AppendLine($"# {Command} at ({at.x:0.0},{at.y:0.0},{at.z:0.0}) r {radius:0} m, FAT {(global::VerdantsAscent.FiresAdminTerrain.PluginVersion)}, {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            int chunks = 0, totalOpen = 0, unreadable = 0, vBase = 1;
            var edges = new Dictionary<long, int>();
            foreach (var view in UnityEngine.Object.FindObjectsOfType<VoxelChunkView>())
            {
                if (view == null) continue;
                var mf = view.GetComponent<MeshFilter>();
                var mesh = mf != null ? mf.sharedMesh : null;
                if (mesh == null || mesh.vertexCount == 0) continue;
                Vector3 origin = new Vector3(view.Key.x * 32f, view.Key.y * 32f, view.Key.z * 32f);
                Vector3 centre = origin + new Vector3(16f, 16f, 16f);
                if ((centre - at).sqrMagnitude > (radius + 28f) * (radius + 28f)) continue;   // 28 = half a chunk's diagonal
                if (!mesh.isReadable) { unreadable++; continue; }
                var verts = mesh.vertices;
                var tris = mesh.triangles;
                var tf = view.transform;
                obj.AppendLine($"o chunk_{view.Key.x}_{view.Key.y}_{view.Key.z}");
                foreach (var v in verts)
                {
                    var w = tf.TransformPoint(v);
                    obj.Append("v ").Append(w.x.ToString("0.###", inv)).Append(' ').Append(w.y.ToString("0.###", inv)).Append(' ').Append(w.z.ToString("0.###", inv)).Append('\n');
                }
                for (int t = 0; t + 2 < tris.Length; t += 3)
                    obj.Append("f ").Append(tris[t] + vBase).Append(' ').Append(tris[t + 1] + vBase).Append(' ').Append(tris[t + 2] + vBase).Append('\n');
                vBase += verts.Length;

                // open edges away from the chunk's outer faces (local space, the mesh's own coordinates)
                edges.Clear();
                for (int t = 0; t + 2 < tris.Length; t += 3)
                {
                    Count(edges, tris[t], tris[t + 1]);
                    Count(edges, tris[t + 1], tris[t + 2]);
                    Count(edges, tris[t + 2], tris[t]);
                }
                int open = 0;
                foreach (var kv in edges)
                {
                    if (kv.Value != 1) continue;
                    int a = (int)(kv.Key >> 32), b = (int)(kv.Key & 0xFFFFFFFF);
                    if (OnOuterFace(tf, verts[a], verts[b], origin)) continue;
                    open++;
                }
                totalOpen += open;
                chunks++;
                report.AppendLine($"chunk ({view.Key.x},{view.Key.y},{view.Key.z}): {verts.Length} vertices, {tris.Length / 3} triangles, {open} open edge(s) inside{(open > 0 ? "  <-- crack or hole" : "")}");
            }
            string dir = Path.Combine(BepInEx.Paths.CachePath, "FiresAdminTerrain");
            Directory.CreateDirectory(dir);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", inv);
            string objPath = Path.Combine(dir, $"seamdump_{stamp}.obj");
            string txtPath = Path.Combine(dir, $"seamdump_{stamp}.txt");
            File.WriteAllText(objPath, obj.ToString());
            File.WriteAllText(txtPath, report.ToString());
            return $"{Command}: {chunks} chunk mesh(es) within {radius:0} m written to {objPath} ({totalOpen} open edge(s) inside chunks{(unreadable > 0 ? $", {unreadable} unreadable mesh(es) skipped" : "")}); per-chunk list: {txtPath}";
        }

        private static void Count(Dictionary<long, int> edges, int a, int b)
        {
            long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
            edges.TryGetValue(key, out int n);
            edges[key] = n + 1;
        }

        // an edge lying on one of the chunk's six outer faces (where the neighbour chunk's mesh takes over) is not a hole
        private static bool OnOuterFace(Transform tf, Vector3 a, Vector3 b, Vector3 origin)
        {
            Vector3 wa = tf.TransformPoint(a) - origin, wb = tf.TransformPoint(b) - origin;
            return SameFace(wa.x, wb.x) || SameFace(wa.y, wb.y) || SameFace(wa.z, wb.z);
        }

        private static bool SameFace(float a, float b)
            => (Mathf.Abs(a) < FaceTolerance && Mathf.Abs(b) < FaceTolerance) || (Mathf.Abs(a - 32f) < FaceTolerance && Mathf.Abs(b - 32f) < FaceTolerance);
    }
}
