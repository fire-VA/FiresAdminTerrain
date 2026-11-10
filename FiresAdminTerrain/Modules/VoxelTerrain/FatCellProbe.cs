using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // 0.2.1080 (lead: "the one-cell proof"): `fat_cell_probe` reads everything that decides how ONE ground cell under the
    // crosshair looks, so a carve's paint, clutter and water can be checked in game without guessing:
    //   * the vanilla heightmap's paint mask there (r dirt / g cultivated / b paved / a wet-or-lava), which the voxel ground
    //     copies per column;
    //   * the wet value FAT's wet-mask stamp computes there (PaintWet01: 1 under placed water, 0.6 / 0.3 in the shore bands);
    //   * the ground normal's slope (the voxel shader turns steep ground to cliff rock);
    //   * the super-flat clutter gate for its grass patch;
    //   * placed water: its surface and the floor under it (the bed), and the voxel floor probe the water sheets use.
    // Client only, read-only, a cheat command. One line to the log and a short version on screen.
    [HarmonyPatch(typeof(Terminal), "InitTerminal")]
    internal static class FatCellProbe
    {
        private static bool s_registered;
        private static readonly AccessTools.FieldRef<Heightmap, Texture2D> s_paint = AccessTools.FieldRefAccess<Heightmap, Texture2D>("m_paintMask");

        [HarmonyPostfix]
        private static void Postfix()
        {
            if (s_registered) return;
            s_registered = true;
            new Terminal.ConsoleCommand("fat_cell_probe",
                "[FiresAdminTerrain] what decides the look of the ground cell under the crosshair: paint mask, wet value, slope, clutter gate, placed water + bed",
                args => args.Context.AddString(Probe()), isCheat: true);
        }

        private static string Probe()
        {
            var cam = GameCamera.instance != null ? GameCamera.instance.transform : (Camera.main != null ? Camera.main.transform : null);
            if (cam == null) return "fat_cell_probe: no camera";
            int mask = LayerMask.GetMask("terrain");
            if (!Physics.Raycast(cam.position, cam.forward, out RaycastHit hit, 200f, mask)) return "fat_cell_probe: no terrain under the crosshair (200 m)";
            Vector3 p = hit.point;
            string paint = "no heightmap";
            var hm = Heightmap.FindHeightmap(p);
            if (hm != null)
            {
                var tex = s_paint(hm);
                if (tex != null)
                {
                    Vector3 local = p - hm.transform.position;
                    float half = (tex.width - 1) * 0.5f;
                    int ix = Mathf.Clamp(Mathf.RoundToInt(local.x + half), 0, tex.width - 1), iz = Mathf.Clamp(Mathf.RoundToInt(local.z + half), 0, tex.height - 1);
                    Color c = tex.GetPixel(ix, iz);
                    paint = $"r {c.r:F2} g {c.g:F2} b {c.b:F2} a {c.a:F2} (heightmap {hm.name}, texel {ix},{iz}{(hm.enabled ? "" : ", hidden: voxel copies it")})";
                }
                else paint = "heightmap has no paint mask yet";
            }
            float wet = VoxelZoneStreamer.PaintWet01Uncached(p.x, p.z);
            float slope = Vector3.Angle(hit.normal, Vector3.up);
            string clutter = "n/a (not super flat)";
            if (FatSuperFlat.On && ClutterSystem.instance != null)
            {
                float size = ClutterSystem.instance.m_grassPatchSize;
                var patch = new Vector2Int(Mathf.FloorToInt(p.x / size + 0.5f), Mathf.FloorToInt(p.z / size + 0.5f));
                clutter = (FatSuperFlat.ClutterAllowed(patch, size) ? "allow" : "refuse") + $" (patch {patch.x},{patch.y} size {size:F0})";
            }
            string water = VerdantsAscent.Water.Core.FatWaterChecks.TryWaterAt(p.x, p.z, 0.05f, false, out float surface, out float bed)
                ? $"placed water surface {surface:F2}, bed {bed:F2} (depth {surface - bed:F2})"
                : "no placed water here";
            string floor = VoxelWorld.TrySampleWaterFloorY(p.x, p.z, p.y + 5f, p.y - 20f, out float fy, out bool wall)
                ? $"voxel floor {fy:F2}{(wall ? " (wall)" : "")}" : "voxel floor none";
            string line = $"[FatCellProbe] cell ({p.x:F1},{p.y:F2},{p.z:F1}) on '{hit.collider.name}': paint {paint}; wet (stamp value) {wet:F2}; slope {slope:F0} deg"
                + $" (voxel shader turns ~>50 deg to cliff rock); clutter gate {clutter}; {water}; {floor}; {WaterLife(p)}";
            FiresLogger.LogInfo(line);
            return line;
        }

        // 0.2.1086 (lead: "no more log says placed"): the water life within 5 m of the cell. Water clutter = every vanilla InstanceRenderer
        // instance of a water band (lilies, vass, reeds ...): its prefab, position, height against our water's surface and bed, and
        // whether this frame draws it (index under the renderer's LOD count). Fish = every Fish within 5 m, in our water or not,
        // plus the nearest loaded fish.
        private static readonly AccessTools.FieldRef<InstanceRenderer, Matrix4x4[]> s_inst = AccessTools.FieldRefAccess<InstanceRenderer, Matrix4x4[]>("m_instances");
        private static readonly AccessTools.FieldRef<InstanceRenderer, int> s_instN = AccessTools.FieldRefAccess<InstanceRenderer, int>("m_instanceCount");
        private static readonly AccessTools.FieldRef<InstanceRenderer, float> s_lodN = AccessTools.FieldRefAccess<InstanceRenderer, float>("m_lodCount");
        private const float LifeReach = 5f;

        private static string WaterLife(Vector3 p)
        {
            var sb = new System.Text.StringBuilder("water life within 5 m: clutter [");
            int shown = 0, total = 0;
            foreach (var r in Object.FindObjectsOfType<InstanceRenderer>())
            {
                string n = r.name.ToLowerInvariant();
                if (!(n.Contains("lil") || n.Contains("vass") || n.Contains("reed") || n.Contains("water"))) continue;
                var m = s_inst(r);
                int cnt = s_instN(r);
                float lod = s_lodN(r);
                for (int i = 0; m != null && i < cnt && i < m.Length; i++)
                {
                    Vector3 q = m[i].GetColumn(3);
                    if (Mathf.Abs(q.x - p.x) > LifeReach || Mathf.Abs(q.z - p.z) > LifeReach) continue;
                    total++;
                    if (shown++ >= 6) continue;
                    string rel = VerdantsAscent.Water.Core.FatWaterChecks.TryWaterAt(q.x, q.z, 0.05f, false, out float s, out float b)
                        ? $"vs surface {q.y - s:+0.00;-0.00}, vs bed {q.y - b:+0.00;-0.00}" : "no water there";
                    bool drawn = r.isActiveAndEnabled && i < lod && r.m_material != null && r.m_mesh != null;
                    sb.Append($"{r.name.Replace("(Clone)", "")} ({q.x:F1},{q.y:F2},{q.z:F1}) {rel} {(drawn ? "drawn" : "NOT drawn (lod/disabled)")}; ");
                }
            }
            sb.Append($"] {total} instance(s); fish [");
            int fish = 0;
            float nearest = float.MaxValue;
            Vector3 np = Vector3.zero;
            foreach (var f in Object.FindObjectsOfType<Fish>())
            {
                Vector3 q = f.transform.position;
                float d = Utils.DistanceXZ(q, p);
                if (d < nearest) { nearest = d; np = q; }
                if (d > LifeReach) continue;
                fish++;
                bool wet = VerdantsAscent.Water.Core.FatWaterChecks.TryWaterAt(q.x, q.z, 0.05f, false, out float s, out float b) && q.y < s && q.y > b - 0.5f;
                sb.Append($"{f.name.Replace("(Clone)", "")} ({q.x:F1},{q.y:F2},{q.z:F1}) {(wet ? "in water" : "NOT in water")}; ");
            }
            sb.Append($"] {fish} fish");
            sb.Append(nearest < float.MaxValue ? $" (nearest loaded fish {nearest:F0} m at ({np.x:F0},{np.y:F1},{np.z:F0}))" : " (no fish loaded)");
            return sb.ToString();
        }
    }
}
