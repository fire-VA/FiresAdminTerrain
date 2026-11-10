using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace VerdantsAscent.Terrain.Voxel
{
    // 0.2.1089 (lead, Fire: "no more guessing"): fat_render_dump. For the renderer under the crosshair: its material and shader,
    // then every property the shader declares that our code (or Core / FTS) sets, read from BOTH the material and the renderer's
    // MaterialPropertyBlock (a value in the MPB wins at draw time), plus the snow globals, plus the Harmony owners of the methods
    // that decide the ground's look (patch order = who can override whom). One Info line block in the log.
    internal static class FatRenderDump
    {
        private static readonly string[] Watch = { "Cultivated", "_FiresSnow", "_ClearedMaskTex", "_FiresLocalWater", "_DiffuseArrayTex", "_NormalArrayTex", "_FiresHeat", "_SnowNormal" };
        private static readonly string[] Globals = { "_FiresSnowCover", "_FiresSnowDepthScale", "_FiresSnowMaxDepth", "_FiresHeatCount", "_FiresSnowFieldOn" };

        [HarmonyPatch(typeof(Terminal), "InitTerminal")]
        private static class Command
        {
            private static bool s_registered;
            private static void Postfix()
            {
                if (s_registered) return;
                s_registered = true;
                new Terminal.ConsoleCommand("fat_render_dump",
                    "[FiresAdminTerrain] material, shader, material + property-block values (cultivated, snow, paint) of the ground under the crosshair, and the Harmony owners of the ground's look",
                    args => args.Context?.AddString(Dump()), isCheat: true);
            }
        }

        private static string Dump()
        {
            var sb = new System.Text.StringBuilder("[RenderDump] ");
            var cam = GameCamera.instance != null ? GameCamera.instance.transform : null;
            Renderer rend = null;
            if (cam != null && Physics.Raycast(cam.position, cam.forward, out RaycastHit hit, 80f, LayerMask.GetMask("terrain", "Default", "static_solid"), QueryTriggerInteraction.Ignore))
            {
                rend = hit.collider.GetComponent<Renderer>() ?? hit.collider.GetComponentInParent<Renderer>() ?? hit.collider.GetComponentInChildren<Renderer>();
                sb.Append($"hit '{hit.collider.name}' at ({hit.point.x:0.0},{hit.point.y:0.0},{hit.point.z:0.0}) zone {ZoneSystem.GetZone(hit.point)}");
            }
            else sb.Append("nothing under the crosshair");
            if (rend == null) sb.Append("; no renderer on the hit object");
            else
            {
                var mat = rend.sharedMaterial;
                var mpb = new MaterialPropertyBlock();
                rend.GetPropertyBlock(mpb);
                var view = rend.GetComponent<VoxelChunkView>() ?? rend.GetComponentInParent<VoxelChunkView>();
                sb.Append($"\n  renderer '{rend.name}' ({rend.GetType().Name}), voxel chunk {(view != null ? "yes, snow bound " + view.SnowBound : "no")}, MPB {(mpb.isEmpty ? "EMPTY" : "set")}");
                if (mat == null) sb.Append("\n  material: none");
                else
                {
                    var sh = mat.shader;
                    sb.Append($"\n  material '{mat.name}' shader '{sh?.name}' keywords [{string.Join(",", mat.shaderKeywords)}]");
                    if (sh != null)
                        for (int i = 0; i < sh.GetPropertyCount(); i++)
                        {
                            string name = sh.GetPropertyName(i);
                            int id = sh.GetPropertyNameId(i);
                            bool watched = Watch.Any(w => name.IndexOf(w, System.StringComparison.Ordinal) >= 0);
                            var type = sh.GetPropertyType(i);
                            string m = "", b = "";
                            switch (type)
                            {
                                case ShaderPropertyType.Texture:
                                    m = TexName(mat.GetTexture(id));
                                    b = mpb.HasTexture(id) ? TexName(mpb.GetTexture(id)) : null;
                                    break;
                                case ShaderPropertyType.Float:
                                case ShaderPropertyType.Range:
                                    m = mat.GetFloat(id).ToString("0.###");
                                    b = mpb.HasFloat(id) ? mpb.GetFloat(id).ToString("0.###") : null;
                                    break;
                                case ShaderPropertyType.Color:
                                    m = mat.GetColor(id).ToString();
                                    b = mpb.HasColor(id) ? mpb.GetColor(id).ToString() : null;
                                    break;
                                case ShaderPropertyType.Vector:
                                    m = mat.GetVector(id).ToString();
                                    b = mpb.HasVector(id) ? mpb.GetVector(id).ToString() : null;
                                    break;
                            }
                            if (!watched && b == null) continue;
                            sb.Append($"\n    {name} ({type}): material {m}; MPB {(b ?? "not set")}");
                        }
                    // MPB-only properties the shader does not declare (a name mismatch = silently unbound).
                    foreach (var p in new[] { "_FiresSnowZoneTex", "_FiresSnowZoneRect", "_FiresSnowZoneOn", "_ClearedMaskTex", "_FiresLocalWaterY" })
                    {
                        int id = Shader.PropertyToID(p);
                        bool declared = sh != null && sh.FindPropertyIndex(p) >= 0;
                        bool inMpb = mpb.HasTexture(id) || mpb.HasFloat(id) || mpb.HasVector(id);
                        if (inMpb && !declared) sb.Append($"\n    {p}: in the MPB but NOT declared as a property by '{sh?.name}' (read only if the shader has it as a uniform)");
                    }
                }
            }
            sb.Append("\n  globals:");
            foreach (var g in Globals) sb.Append($" {g} {Shader.GetGlobalFloat(g):0.###};");
            sb.Append("\n  Harmony owners (order):");
            Owners(sb, typeof(Heightmap), "RebuildRenderMesh");
            Owners(sb, typeof(TerrainComp), "DoOperation");
            Owners(sb, typeof(TerrainComp), "ApplyOperation");
            Owners(sb, typeof(TerrainComp), "RPC_ApplyOperation");
            Owners(sb, typeof(ZoneSystem), "Update");
            Owners(sb, typeof(ZoneSystem), "Awake");
            Owners(sb, typeof(ClutterSystem), "ResetGrass");
            string s = sb.ToString();
            FiresLogger.LogInfo(s);
            return s;
        }

        private static string TexName(Texture t) => t == null ? "NULL" : $"'{t.name}' {t.width}x{t.height}";

        private static void Owners(System.Text.StringBuilder sb, System.Type t, string method)
        {
            var mi = AccessTools.Method(t, method);
            if (mi == null) { sb.Append($"\n    {t.Name}.{method}: not found"); return; }
            var info = Harmony.GetPatchInfo(mi);
            if (info == null) { sb.Append($"\n    {t.Name}.{method}: unpatched"); return; }
            string L(System.Collections.ObjectModel.ReadOnlyCollection<Patch> ps) => string.Join(", ", ps.OrderByDescending(p => p.priority).Select(p => $"{p.owner}:{p.PatchMethod.DeclaringType?.Name} (prio {p.priority})"));
            sb.Append($"\n    {t.Name}.{method}: prefixes [{L(info.Prefixes)}] postfixes [{L(info.Postfixes)}] transpilers [{L(info.Transpilers)}] finalizers [{L(info.Finalizers)}]");
        }
    }
}
