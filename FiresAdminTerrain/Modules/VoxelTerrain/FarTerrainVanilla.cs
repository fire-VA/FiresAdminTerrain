using System.Collections;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // 0.2.1418 FarLands #3 for worlds WITHOUT FAT's far quilt (Fire's world: VoxelWrap on vanilla heights, so FarQuilt never runs and
    // 0.2.1414's ExtendedReach rings changed nothing there; shot 596's bare far islands are vanilla's own 2400 m TerrainLod edge).
    // Same key, [Far Terrain] ExtendedReach + ReachMeters, vanilla's own method: a SECOND TerrainLod (vanilla's component on our own
    // GameObject, vanilla's material, vanilla's HeightmapBuilder and rebuild rules), sized 2 x ReachMeters in 5 x 5 regions (~32 m
    // vertices). It draws only beyond vanilla's square: each of its renderers gets _LodHideDistance (the distant-LOD shader's own
    // near clip, which vanilla sets per renderer) just inside vanilla's half-size, its centre region (wholly inside) is not kept, and
    // it sits 8 m lower so the thin overlap band never z-fights. Off = destroyed. Stands down with a WHY while FarQuilt draws (its
    // rings do this job there). Main-thread cost: vanilla's own Regenerate of 24 heightmaps per 512 m of travel.
    internal static class FarTerrainVanilla
    {
        private const int Regions = 5;
        private const float Sink = 8f, HideMargin = 0.96f, StepDistance = 512f;
        private static GameObject s_go;
        private static TerrainLod s_lod;
        private static float s_hide, s_builtReach;
        private static string s_lastWhy;

        private static readonly AccessTools.FieldRef<TerrainLod, float> TerrainSize = AccessTools.FieldRefAccess<TerrainLod, float>("m_terrainSize");
        private static readonly AccessTools.FieldRef<TerrainLod, int> RegionsPerAxis = AccessTools.FieldRefAccess<TerrainLod, int>("m_regionsPerAxis");
        private static readonly AccessTools.FieldRef<TerrainLod, float> VertexDistance = AccessTools.FieldRefAccess<TerrainLod, float>("m_vertexDistance");
        private static readonly AccessTools.FieldRef<TerrainLod, float> UpdateStep = AccessTools.FieldRefAccess<TerrainLod, float>("m_updateStepDistance");
        private static readonly AccessTools.FieldRef<TerrainLod, Material> LodMaterial = AccessTools.FieldRefAccess<TerrainLod, Material>("m_material");
        private static readonly System.Reflection.FieldInfo HeightmapsField = AccessTools.Field(typeof(TerrainLod), "m_heightmaps");

        // From FarQuilt's driver tick (0.5 s), on a client with graphics.
        internal static void Tick(bool farQuiltDraws)
        {
            bool want = FarQuilt.ExtendedReach != null && FarQuilt.ExtendedReach.Value;
            if (!want || farQuiltDraws)
            {
                if (s_go != null) Destroy(farQuiltDraws ? "FAT's far quilt draws this world (its rings extend it)" : "ExtendedReach off");
                if (want && farQuiltDraws) Why("FAT's far quilt draws this world; its 512 m / 1024 m rings extend it instead");
                return;
            }
            float reach = FarQuilt.ReachMeters.Value;
            if (s_go != null && Mathf.Abs(reach - s_builtReach) > 1f) Destroy("ReachMeters changed");
            if (s_go == null) { Create(reach); return; }
            Maintain();
        }

        private static void Why(string why)
        {
            if (why == s_lastWhy) return;
            s_lastWhy = why;
            FiresLogger.LogInfo($"[FarTerrain] vanilla-terrain reach stands down: {why}.");
        }

        private static void Create(float reach)
        {
            var vanilla = Object.FindObjectOfType<TerrainLod>();
            if (vanilla == null || vanilla.gameObject == s_go) { Why("no vanilla TerrainLod in this scene yet"); return; }
            var mat = LodMaterial(vanilla);
            if (mat == null) { Why("vanilla's TerrainLod has no material"); return; }
            float vanillaHalf = TerrainSize(vanilla) * 0.5f;
            if (reach <= vanillaHalf + 64f) { Why($"ReachMeters {reach:0} m is not beyond vanilla's {vanillaHalf:0} m"); return; }
            // 0.2.1430 (Fire 11-10 2:58 AM, DN: "far terrain ends up with tears in it at some seams"): vanilla's TerrainLod.CreateMeshes spaces its
            // regions terrainSize / regions apart but gives each mesh RoundToInt(regionSize / vertexDistance) cells, centred (Heightmap.cs:623). With
            // size = 2 x reach (8192 / 5 = 1638.4 m regions, 51.2 -> 51 cells x 32 m = 1632 m meshes) every inner region border had a 6.4 m strip with
            // no terrain: straight lines of sky across the far land. The size is now a whole number of cells per region, so the meshes meet exactly
            // (shared edge vertices sample the same heights). It can be up to one cell per region larger than 2 x reach, never smaller.
            float vd = Mathf.Max(16f, Mathf.Round(reach * 2f / Regions / 52f));
            int cells = Mathf.CeilToInt(reach * 2f / Regions / vd - 0.001f);
            float size = cells * vd * Regions;

            var holder = new GameObject("FAT_FarTerrainVanillaHolder");
            holder.SetActive(false);                                   // so TerrainLod.OnEnable waits until its fields are set
            var go = new GameObject("FAT_FarTerrainVanilla");
            go.transform.SetParent(holder.transform, false);
            var lod = go.AddComponent<TerrainLod>();
            LodMaterial(lod) = mat;
            TerrainSize(lod) = size;
            RegionsPerAxis(lod) = Regions;
            VertexDistance(lod) = vd;
            UpdateStep(lod) = StepDistance;
            go.transform.SetParent(null, false);
            Object.Destroy(holder);
            s_go = go; s_lod = lod; s_builtReach = reach; s_lastWhy = null;
            s_hide = vanillaHalf * HideMargin;
            DropCentreRegion();   // OnEnable ran when the object left the inactive holder

            var cam = Camera.main;
            float clip = cam != null ? cam.farClipPlane : -1f;
            FiresLogger.LogInfo($"[FarTerrain] vanilla-terrain reach on: a second TerrainLod (vanilla's material and builder) to {reach:0} m " +
                                $"({Regions}x{Regions} regions of {size / Regions:0} m = {cells} x {vd:0} m cells, edges meet exactly, rebuilt every {StepDistance:0} m), drawn beyond vanilla's {vanillaHalf:0} m " +
                                $"(hide {s_hide:0} m, {Sink:0} m lower); camera far clip {(clip > 0 ? clip.ToString("0") + " m" : "unknown")}" +
                                (clip > 0 && clip < reach ? $" (WHY less shows: terrain past {clip:0} m is clipped by the camera)" : "") + ".");
        }

        // The middle region lies wholly inside vanilla's square: never built, never drawn.
        private static void DropCentreRegion()
        {
            if (HeightmapsField == null || s_lod == null) return;
            if (!(HeightmapsField.GetValue(s_lod) is IList list)) return;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var item = list[i];
                var off = (Vector3)AccessTools.Field(item.GetType(), "m_offset").GetValue(item);
                if (off.sqrMagnitude > 1f) continue;
                var hm = (Heightmap)AccessTools.Field(item.GetType(), "m_heightmap").GetValue(item);
                list.RemoveAt(i);
                if (hm != null) Object.Destroy(hm.gameObject);
            }
        }

        private static MaterialPropertyBlock s_mpb;

        // Every tick: vanilla's rebuild puts each region back at y = 0 and its ApplySettings writes vanilla's near hide distance.
        private static void Maintain()
        {
            if (s_mpb == null) s_mpb = new MaterialPropertyBlock();
            foreach (var hm in s_go.GetComponentsInChildren<Heightmap>(false))
            {
                var t = hm.transform;
                if (t.position.y != -Sink) t.position = new Vector3(t.position.x, -Sink, t.position.z);
                var mr = hm.GetComponent<MeshRenderer>();
                if (mr == null) continue;
                mr.GetPropertyBlock(s_mpb);
                if (Mathf.Abs(s_mpb.GetFloat("_LodHideDistance") - s_hide) > 0.5f) { s_mpb.SetFloat("_LodHideDistance", s_hide); mr.SetPropertyBlock(s_mpb); }
            }
        }

        private static void Destroy(string why)
        {
            if (s_go != null) Object.Destroy(s_go);
            s_go = null; s_lod = null;
            FiresLogger.LogInfo($"[FarTerrain] vanilla-terrain reach off ({why}).");
        }
    }
}
