using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // 0.2.1405 (Fire 11-07: voxel snow moves from FTS to FAT). Ported from FTS FiresSkylandSnow (0.3.213-0.3.229, [seasons], Fire
    // 10-26: "skylands ... do not draw the snow texture just due to height ... rather just based of season/biome"): vanilla's
    // Custom/Vegetation and Custom/StaticRock whiten by world height (their _AddSnow toggle), so a Meadows sky island 100+ m up drew
    // trees and rocks snow-white. On an island the ISLAND's biome decides: objects on an island that is not Mountain / Deep North
    // (and no winter cover from Core's SnowField) get _AddSnow = 0 through vanilla's own per-object owner MaterialMan; snowy islands,
    // ground-level objects and real mountains are untouched. Islands are FAT's own (FiresSkyIslands.TryIslandBiomeAt), so no
    // reflection; FatApi.TryIslandBiomeNear publishes the same near-test for piece snow (Vedr). Scan: loaded networked objects that
    // draw, within 220 m of the camera, each once, 200 per frame.
    internal static class FatSkylandSnow
    {
        private static readonly int s_addSnow = Shader.PropertyToID("_AddSnow");
        private const float ScanRange = 220f;
        private const int ScanPerFrame = 200;
        private const float RingM = 8f, RingUp = 6f;
        private static readonly float[] PadRings = { 14f, 20f };
        private static readonly float[] PadUp = { 0f, RingUp, 12f, -RingUp, -12f, -25f };
        private static readonly FieldInfo s_instances = AccessTools.Field(typeof(ZNetScene), "m_instances");
        private static readonly HashSet<int> s_done = new HashSet<int>();
        private static readonly List<ZNetView> s_queue = new List<ZNetView>();
        private static readonly List<Renderer> s_rs = new List<Renderer>();
        private static int s_at, s_off, s_kept, s_pad;
        private static float s_nextFill, s_logAt;
        private static ZNetScene s_scene;
        private static bool s_said;

        internal static bool OnSkyland(Vector3 p, out bool snowy, out Heightmap.Biome biome)
        {
            snowy = false; biome = Heightmap.Biome.None;
            try { if (!FiresSkyIslands.TryIslandBiomeAt(p, out biome)) return false; } catch { return false; }
            snowy = biome == Heightmap.Biome.Mountain || biome == Heightmap.Biome.DeepNorth || WinterCover() > 0.2f;
            return true;
        }

        // On an island at the point, else from a ring around it / above / below (rim, cliff and belly props), else the padding rings.
        internal static bool OnSkylandNear(Vector3 p, out bool snowy, out Heightmap.Biome biome)
        {
            if (OnSkyland(p, out snowy, out biome)) return true;
            if (OnSkyland(p + Vector3.up * RingUp, out snowy, out biome) || OnSkyland(p - Vector3.up * RingUp, out snowy, out biome)) return true;
            for (int k = 0; k < 8; k++)
            {
                float a = k * Mathf.PI / 4f;
                var q = p + new Vector3(Mathf.Cos(a) * RingM, 0f, Mathf.Sin(a) * RingM);
                if (OnSkyland(q, out snowy, out biome) || OnSkyland(q + Vector3.up * RingUp, out snowy, out biome)) return true;
            }
            foreach (float dy in PadUp)
                if (dy != 0f && OnSkyland(p + Vector3.up * dy, out snowy, out biome)) { s_pad++; return true; }
            foreach (float r in PadRings)
                for (int k = 0; k < 12; k++)
                {
                    float a = k * Mathf.PI / 6f;
                    var q = p + new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
                    foreach (float dy in PadUp)
                        if (OnSkyland(q + Vector3.up * dy, out snowy, out biome)) { s_pad++; return true; }
                }
            return false;
        }

        private static PropertyInfo s_winter; private static bool s_winterLooked;
        private static float WinterCover()
        {
            if (!s_winterLooked) { s_winterLooked = true; s_winter = Type.GetType("FiresCore.Snow.SnowField, FiresUnifiedCore", false)?.GetProperty("WinterCover", BindingFlags.Public | BindingFlags.Static); }
            try { return s_winter != null ? (float)s_winter.GetValue(null) : 0f; } catch { return 0f; }
        }

        // Every frame from FatSnowGlobals (client only).
        internal static void Tick()
        {
            var scene = ZNetScene.instance;
            if (scene == null || MaterialMan.instance == null) return;
            if (scene != s_scene) { s_scene = scene; s_done.Clear(); s_queue.Clear(); s_at = 0; }
            if (!FiresSkyIslands.Enabled) return;   // no islands in this world: nothing to scan
            var cam = Utils.GetMainCamera();
            if (cam == null) return;
            if (!s_said) { s_said = true; FiresLogger.LogInfo("[SkylandSnow] FAT owns sky-island snow: objects on a non-snow island lose vanilla's height snow, the island's biome decides (was FTS FiresSkylandSnow) (0.2.1405)"); }
            if (s_at >= s_queue.Count && Time.unscaledTime >= s_nextFill)
            {
                s_nextFill = Time.unscaledTime + 1f;
                s_queue.Clear(); s_at = 0;
                if (s_instances?.GetValue(scene) is Dictionary<ZDO, ZNetView> all)
                {
                    Vector3 cp = cam.transform.position;
                    foreach (var nv in all.Values)
                        if (nv != null && !s_done.Contains(nv.GetInstanceID()) && (nv.transform.position - cp).sqrMagnitude < ScanRange * ScanRange) s_queue.Add(nv);
                }
            }
            int n = 0;
            while (s_at < s_queue.Count && n++ < ScanPerFrame)
            {
                var nv = s_queue[s_at++];
                if (nv == null) continue;
                s_done.Add(nv.GetInstanceID());
                nv.GetComponentsInChildren(true, s_rs);
                bool draws = false;
                foreach (var r in s_rs) if (r is MeshRenderer || r is SkinnedMeshRenderer) { draws = true; break; }
                if (!draws) continue;
                Vector3 p = nv.transform.position + Vector3.up * 0.5f;
                if (!OnSkylandNear(p, out bool snowy, out var biome)) continue;
                if (snowy) { s_kept++; continue; }
                try { MaterialMan.instance.SetValue(nv.gameObject, s_addSnow, 0f, true); s_off++; } catch { }
                if (Time.unscaledTime >= s_logAt)
                {
                    s_logAt = Time.unscaledTime + 10f;
                    FiresLogger.LogDebug($"[SkylandSnow] height snow off on {s_off} object(s) on non-snow islands so far, {s_kept} kept on snowy ones, {s_pad} caught by the rim padding (latest {Utils.GetPrefabName(nv.gameObject)} on a {biome} island at {p.x:0},{p.y:0},{p.z:0}) (0.2.1405)");
                }
            }
            if (s_done.Count > 50000) s_done.Clear();
        }
    }
}
