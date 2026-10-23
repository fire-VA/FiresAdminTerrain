using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.Rendering;
using FiresCore.Materials;

namespace VerdantsAscent.Terrain.Voxel
{
    // Pad seams pulled apart at around 20-30 m and stitched together at about 5-10 m, with 0 holes and a max step of
    // 0.026 m, so the geometry is closed: a live A/B switchboard for the RENDER-side suspects, so one hand pass at 25 m pins it.
    // `fat_seam_suspect <name> [on|off]` flips one suspect for this session only (nothing saved), `fat_seam_suspect list` prints
    // their states, `fat_seam_suspect reset` restores everything. Suspects:
    //   quilt     - the far-terrain quilt tiles (render-only, generator heights) hidden
    //   cast      - voxel ground casts no shadows
    //   receive   - voxel ground receives no shadows
    //   cascades  - QualitySettings.shadowCascades 1 (no cascade switch at ~10-20 m)
    //   shadowdist- QualitySettings.shadowDistance 15 m (everything past it unshadowed: a line that moves with it = shadows)
    //   clutter   - vanilla grass/clutter off (ClutterSystem)
    //   postfx    - the camera's post-processing layer off (SSAO and friends)
    //   farblend  - the ground material's distance blend pushed out (shader property guessing, see 'list': only properties
    //               the terrain material actually has are touched)
    internal static class FatSeamSuspects
    {
        private const string Tag = "[FatSeamSuspect]";
        private static readonly Dictionary<string, bool> s_on = new Dictionary<string, bool>();
        private static int s_origCascades = -1;
        private static float s_origShadowDist = -1f;
        private static readonly Dictionary<string, float> s_origFloats = new Dictionary<string, float>();
        // terrain-shader distance/blend properties worth trying (only those the material has are used)
        private static readonly List<Behaviour> s_postfx = new List<Behaviour>();
        // The shader's log showed "0 distance properties" on Custom/FiresVoxelHeightmap, so farblend was never tested: the shader's OWN
        // float/range properties whose names say distance or fade, plus the old guessed names if present.
        private static IEnumerable<string> DistanceProps(Material mat)
        {
            var seen = new HashSet<string>();
            foreach (var p in FarProps) if (mat.HasProperty(p) && seen.Add(p)) yield return p;
            var sh = mat.shader;
            if (sh == null) yield break;
            for (int i = 0; i < sh.GetPropertyCount(); i++)
            {
                var ty = sh.GetPropertyType(i);
                if (ty != ShaderPropertyType.Float && ty != ShaderPropertyType.Range) continue;
                string n = sh.GetPropertyName(i);
                if ((n.IndexOf("Dist", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Fade", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Far", StringComparison.OrdinalIgnoreCase) >= 0) && seen.Add(n)) yield return n;
            }
        }
        private static readonly string[] FarProps = { "_DistantTexStart", "_DistantTexEnd", "_FarFadeStart", "_FarFadeEnd", "_DistanceFade", "_TriplanarFade", "_DetailFadeStart", "_DetailFadeEnd", "_NormalFadeStart", "_NormalFadeEnd" };

        internal static void Register()
        {
            new Terminal.ConsoleCommand("fat_seam_suspect",
                "list | reset | <quilt|cast|receive|cascades|shadowdist|clutter|postfx|farblend> [on|off] - live A/B of render suspects for terrain seams (session only, nothing saved)",
                args =>
                {
                    string a = args.Length > 1 ? args[1].ToLowerInvariant() : "list";
                    if (a == "list") { foreach (var l in List()) Say(args, l); return; }
                    if (a == "reset") { foreach (var k in new List<string>(s_on.Keys)) Set(k, false); Say(args, Tag + " all suspects restored"); return; }
                    bool on = args.Length > 2 ? args[2].ToLowerInvariant() != "off" : !(s_on.TryGetValue(a, out bool cur) && cur);
                    Say(args, Set(a, on));
                }, isCheat: true);
        }

        // Use a command, never force: `fat_rewrap_area <x> <z> <r>` rebuilds every loaded zone within
        // r through the SAME path a reload or a terrain edit takes: each heightmap there is Poked (Heightmap.Regenerate), which runs
        // FAT's RewrapZone (zone + neighbours re-derived from heightmap data + TerrainComp, voxel brush bounds re-applied).
        // camp_edge_test's after_reload step uses it to prove a levelled camp survives a rebuild.
        internal static void RegisterRewrap()
        {
            new Terminal.ConsoleCommand("fat_rewrap_area",
                "<x> <z> <r> - rebuild the voxel terrain of every loaded zone within r m (the reload/edit path: heightmap regenerate -> FAT rewrap)",
                args =>
                {
                    var inv = CultureInfo.InvariantCulture;
                    if (args.Length < 4 || !float.TryParse(args[1], NumberStyles.Float, inv, out float x) || !float.TryParse(args[2], NumberStyles.Float, inv, out float z)
                        || !float.TryParse(args[3], NumberStyles.Float, inv, out float r))
                    { args.Context?.AddString("fat_rewrap_area <x> <z> <r>"); return; }
                    r = Mathf.Clamp(r, 1f, 256f);
                    var list = new List<Heightmap>();
                    Heightmap.FindHeightmap(new Vector3(x, 0f, z), r, list);
                    int n = 0;
                    foreach (var hm in list) { if (hm == null) continue; hm.Poke(0); n++; }
                    string line = string.Format(inv, "[FiresVoxel] fat_rewrap_area: {0} zone(s) rewrapped at ({1:0.0},{2:0.0}) r {3:0} (heightmap regenerate -> FAT rewrap, the reload path)", n, x, z, r);
                    FiresLogger.LogInfo(line);
                    args.Context?.AddString(line);
                }, isCheat: true);
        }

        private static void Say(Terminal.ConsoleEventArgs args, string line) { FiresLogger.LogInfo(line); args.Context?.AddString(line); }

        private static IEnumerable<string> List()
        {
            yield return string.Format(CultureInfo.InvariantCulture, "{0} quilt {1}, cast {2}, receive {3}, cascades {4} (now {5}), shadowdist {6} (now {7:0} m), clutter {8}, postfx {9}, farblend {10}",
                Tag, St("quilt"), St("cast"), St("receive"), St("cascades"), QualitySettings.shadowCascades, St("shadowdist"), QualitySettings.shadowDistance, St("clutter"), St("postfx"), St("farblend"));
            var mat = TerrainMaterialCache.GetCurrentMaterial();
            if (mat != null)
            {
                var have = new List<string>();
                foreach (var p in DistanceProps(mat)) have.Add(p + "=" + mat.GetFloat(p).ToString("0.##", CultureInfo.InvariantCulture));
                yield return $"{Tag} ground material '{mat.name}' shader '{mat.shader?.name}': distance props {(have.Count > 0 ? string.Join(", ", have) : "none of the known names")}; keywords [{string.Join(" ", mat.shaderKeywords)}]";
            }
        }

        private static string St(string k) => s_on.TryGetValue(k, out bool v) && v ? "OFF(suspect killed)" : "normal";

        private static string Set(string name, bool kill)
        {
            switch (name)
            {
                case "quilt":
                    FarQuilt.DebugHidden = kill; if (kill) FarQuilt.ApplyDebugHidden(); break;
                case "cast":
                case "receive":
                {
                    var views = new List<VoxelChunkView>(); VoxelWorld.CollectViews(views);
                    int n = 0;
                    foreach (var v in views)
                    {
                        var r = v != null ? v.GroundRenderer : null;
                        if (r == null) continue;
                        if (name == "cast") r.shadowCastingMode = kill ? ShadowCastingMode.Off : ShadowCastingMode.On; else r.receiveShadows = !kill;
                        n++;
                    }
                    s_on[name] = kill;
                    return $"{Tag} {name} {(kill ? "OFF" : "restored")} on {n} loaded ground renderer(s) (chunks built later keep the normal setting)";
                }
                case "cascades":
                    if (s_origCascades < 0) s_origCascades = QualitySettings.shadowCascades;
                    QualitySettings.shadowCascades = kill ? 1 : s_origCascades; break;
                case "shadowdist":
                    if (s_origShadowDist < 0f) s_origShadowDist = QualitySettings.shadowDistance;
                    QualitySettings.shadowDistance = kill ? 15f : s_origShadowDist; break;
                case "clutter":
                    if (ClutterSystem.instance != null) ClutterSystem.instance.gameObject.SetActive(!kill); break;
                case "postfx":
                {
                    // The log showed "no PostProcessLayer on the main camera", so postfx was never tested: the assembly-qualified
                    // lookup missed; take every post-processing Behaviour on the main camera by type name instead, and name them.
                    var cam = Utils.GetMainCamera();
                    if (cam == null) return $"{Tag} postfx: no main camera";
                    if (kill) { s_postfx.Clear(); foreach (var b in cam.GetComponents<Behaviour>()) if (b != null && b.enabled && b.GetType().Name.IndexOf("PostProcess", StringComparison.OrdinalIgnoreCase) >= 0) s_postfx.Add(b); }
                    var names = new List<string>();
                    foreach (var b in s_postfx) if (b != null) { b.enabled = !kill; names.Add(b.GetType().FullName); }
                    s_on[name] = kill;
                    return $"{Tag} postfx {(kill ? "OFF" : "restored")} on {names.Count} component(s) of camera '{cam.name}': [{string.Join(", ", names)}]{(names.Count == 0 ? " (0 = nothing named *PostProcess* on the main camera: not tested)" : "")}";
                }
                case "farblend":
                {
                    var mat = TerrainMaterialCache.GetCurrentMaterial();
                    if (mat == null) return $"{Tag} farblend: no ground material";
                    int n = 0;
                    foreach (var p in DistanceProps(mat))
                    {
                        if (!s_origFloats.ContainsKey(p)) s_origFloats[p] = mat.GetFloat(p);
                        mat.SetFloat(p, kill ? 100000f : s_origFloats[p]);
                        n++;
                    }
                    s_on[name] = kill;
                    return $"{Tag} farblend {(kill ? "pushed to 100 km" : "restored")} on {n} distance propert(ies) of '{mat.name}' [{string.Join(", ", new List<string>(DistanceProps(mat)))}] (0 = the shader has no distance/fade float: farblend is not a suspect on this shader)";
                }
                default: return $"{Tag} unknown suspect '{name}'";
            }
            s_on[name] = kill;
            return $"{Tag} {name} {(kill ? "OFF (suspect killed)" : "restored")}";
        }
    }
}
