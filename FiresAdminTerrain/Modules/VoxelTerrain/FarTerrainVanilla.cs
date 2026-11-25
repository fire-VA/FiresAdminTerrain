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
            // 0.2.1509 ([lead] 11-21, Fire: far terrain / trees / rocks / buildings in sync; Tools\DESIGN_far_zone_set_20261121.md): while EBM
            // owns the far ground (EbmApi.FarGroundOwned, read by name, no compile reference) this ring is not built and a live one is destroyed;
            // when it goes false the next tick builds it again (ExtendedReach still on). No key: it follows EBM's state.
            if (want && EbmFarGround.OwnedNow)
            {
                if (s_go != null) Destroy("EBM owns the far ground (EbmApi.FarGroundOwned)");
                Why("EBM owns the far ground (EbmApi.FarGroundOwned); FAT still supplies far data through FatApi");
                return;
            }
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
            foreach (var hm in s_go.GetComponentsInChildren<Heightmap>(false)) ApplyOne(hm);
        }

        // 0.2.1485 ([lead] VADN 11-18, Fire's sheer zone / diamond-shaped walls at the wrong height while moving): vanilla's TerrainLod.RebuildHeightmap
        // puts each region at y = 0 and Heightmap.Regenerate -> ApplySettings writes vanilla's near hide distance (NearSimulationDistance x the zone
        // diagonal, ~181 m; installed Heightmap.ApplySettings / GetLodHideDistance). 1418-1484 restored the 8 m sink and our hide distance only on
        // FarQuilt's 0.5 s tick, so after every rebuild this distant-LOD terrain drew from ~181 m in, at y = 0, for up to half a second, showing
        // through wherever the near ground is lower or still loading. Now it is restored the moment vanilla resets it: FAT's Heightmap.Regenerate
        // postfix and a Heightmap.ApplySettings postfix call this for our own regions only.
        internal static void OnHeightmapReset(Heightmap hm)
        {
            if (s_go == null || hm == null || !hm.transform.IsChildOf(s_go.transform)) return;
            ApplyOne(hm);
        }

        private static void ApplyOne(Heightmap hm)
        {
            if (hm == null) return;
            if (s_mpb == null) s_mpb = new MaterialPropertyBlock();
            var t = hm.transform;
            if (t.position.y != -Sink) t.position = new Vector3(t.position.x, -Sink, t.position.z);
            var mr = hm.GetComponent<MeshRenderer>();
            if (mr == null) return;
            mr.GetPropertyBlock(s_mpb);
            if (Mathf.Abs(s_mpb.GetFloat("_LodHideDistance") - s_hide) > 0.5f) { s_mpb.SetFloat("_LodHideDistance", s_hide); mr.SetPropertyBlock(s_mpb); }
        }

        private static void Destroy(string why)
        {
            if (s_go != null) Object.Destroy(s_go);
            s_go = null; s_lod = null;
            FiresLogger.LogInfo($"[FarTerrain] vanilla-terrain reach off ({why}).");
        }
    }

    // 0.2.1509: soft bind to EBM 1.2.483+ (consumer owns the code; [2ic] review 11-21 MUSTs 1/2 + SHOULD). FiresEasyBakeMeshes.EbmApi is
    // looked up ONCE, at FarQuilt's first tick (all plugins are loaded by then); EBM absent or without FarGroundOwned = false for the session
    // (1508 behaviour, zero cost). FarGroundOwnedChanged only marks the value stale (its thread is UNCONFIRMED, so no Unity call there); the
    // read happens on FarQuilt's 0.5 s main-thread tick when stale, plus a 10 s safety re-read for a missed event (0.5 s if EBM has no event).
    // Build once: owned -> FAT stands down at once; not-owned must hold ReleaseHold seconds before FAT's far rings come back.
    internal static class EbmFarGround
    {
        private const float SafetyRead = 10f, NoEventRead = 0.5f, ReleaseHold = 5f;
        private static bool s_bound, s_stale = true, s_raw, s_owned, s_hasEvent;
        private static float s_nextRead, s_falseSince = -1f;
        private static System.Reflection.PropertyInfo s_prop;
        private static System.Reflection.FieldInfo s_field;

        // The settled value FAT acts on (FarTerrainVanilla.Tick, FarQuilt.ExtOn).
        internal static bool OwnedNow => s_owned;

        // Once per FarQuilt tick, before anything reads OwnedNow.
        internal static void Refresh()
        {
            if (!s_bound) Bind();
            if (s_prop == null && s_field == null) return;
            float now = Time.time;
            if (s_stale || now >= s_nextRead)
            {
                s_stale = false;
                s_nextRead = now + (s_hasEvent ? SafetyRead : NoEventRead);
                try { s_raw = (bool)(s_prop != null ? s_prop.GetValue(null, null) : s_field.GetValue(null)); }
                catch { s_raw = false; }
            }
            if (s_raw) { s_falseSince = -1f; Set(true); return; }
            if (!s_owned) return;
            if (s_falseSince < 0f) { s_falseSince = now; s_stale = true; }   // keep re-reading through the hold
            if (now - s_falseSince >= ReleaseHold) Set(false);
            else s_stale = true;
        }

        private static void Set(bool owned)
        {
            if (owned == s_owned) return;
            s_owned = owned;
            FiresLogger.LogInfo(owned
                ? "[FarTerrain] EBM's far ground covers the view: FAT's far terrain rings (ExtendedReach second TerrainLod, quilt rings 2/3) stand down; FatApi far data still served."
                : $"[FarTerrain] EBM's far ground released (not owned for {ReleaseHold:0} s): FAT's far terrain rings follow their own keys again.");
        }

        private static void Bind()
        {
            s_bound = true;   // once, found or not
            if (!BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue("com.Fire.FiresEasyBakeMeshes", out var info) && !AnyEbm(out info))
                return;
            var t = info.Instance != null ? info.Instance.GetType().Assembly.GetType("FiresEasyBakeMeshes.EbmApi", false) : null;
            if (t == null) { FiresLogger.LogInfo("[FarTerrain] EBM far-ground bind: EbmApi not found in this EBM (rings follow FAT's own keys)."); return; }
            const System.Reflection.BindingFlags F = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static;
            s_prop = t.GetProperty("FarGroundOwned", F);
            if (s_prop == null) s_field = t.GetField("FarGroundOwned", F);
            var ev = t.GetEvent("FarGroundOwnedChanged", F);
            if ((s_prop != null || s_field != null) && ev != null && ev.EventHandlerType == typeof(System.Action))
                try { ev.AddEventHandler(null, new System.Action(() => s_stale = true)); s_hasEvent = true; } catch { }
            FiresLogger.LogInfo($"[FarTerrain] EBM far-ground bind: FarGroundOwned {(s_prop != null || s_field != null ? "found" : "not in this EBM (rings follow FAT's own keys)")}, " +
                                $"event {(s_hasEvent ? "subscribed" : "absent")}.");
        }

        // EBM GUID "com.Fire.FiresEasyBakeMeshes" (_ebmV_visual_1483 Plugin.cs:16); assembly-name fallback in case the GUID ever changes.
        private static bool AnyEbm(out BepInEx.PluginInfo info)
        {
            foreach (var kv in BepInEx.Bootstrap.Chainloader.PluginInfos)
                if (kv.Value?.Instance != null && kv.Value.Instance.GetType().Assembly.GetName().Name == "FiresEasyBakeMeshes") { info = kv.Value; return true; }
            info = null; return false;
        }
    }
}
