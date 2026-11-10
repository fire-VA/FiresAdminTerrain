using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // 0.2.1405 (Fire 11-07 via [seasons]/[lead]: "remove the duplicate snow shaders from fts ... voxel snow ... into FAT"; one writer
    // per global set): FAT writes the heat globals its voxel shader reads (FiresHeatBands, the fire bullseye): _FiresHeat[16] =
    // (x, y, z, w), _FiresHeatCount, _FiresHeatCampfireR. Ported from FTS FiresPieceSnowDriver.LateUpdate (0.3.203-0.3.287, same
    // formulas): vanilla's Heat EffectAreas within 80 m (w = the area's collider shape x scale, min 0.5) and burning smelters / kilns /
    // furnaces (ring outer 5 / 6 / 7 m), each growing from 0 to full over 60 s of burning (eased); a fire already burning at load or
    // first seen past 40 m is full at once. Nearest 16, once a second. FTS's writer stands down while FAT is present (FatApi
    // HeatApiVersion) and its FiresPiece reads these same globals; Vedr writes its own _VedrHeat*. Not on a dedicated server.
    internal static class FatHeatSources
    {
        internal const int MaxHeat = 16;
        private const float Range = 80f, GrowSec = 60f, StationScanSec = 5f;
        private static readonly int s_heatId = Shader.PropertyToID("_FiresHeat"), s_heatCountId = Shader.PropertyToID("_FiresHeatCount");
        private static readonly int s_campfireRId = Shader.PropertyToID("_FiresHeatCampfireR");
        private static readonly Vector4[] s_heat = new Vector4[MaxHeat];
        private static readonly List<Vector4> s_all = new List<Vector4>();
        private static readonly FieldInfo s_allAreas = AccessTools.Field(typeof(EffectArea), "s_allAreas");
        private static readonly Dictionary<Vector2Int, float> s_grow = new Dictionary<Vector2Int, float>();
        private static readonly HashSet<Vector2Int> s_seen = new HashSet<Vector2Int>();
        private static readonly HashSet<Smelter> s_known = new HashSet<Smelter>();
        private static readonly List<Smelter> s_stations = new List<Smelter>();
        private static readonly Dictionary<Vector2Int, bool> s_stationSaid = new Dictionary<Vector2Int, bool>();
        private static float s_next, s_nextStationScan, s_growLast = -1f, s_growDt;
        private static bool s_firstTick, s_said;
        private static int s_lastCount = -1;

        internal static Vector4 Nearest => s_heat[0];
        internal static int Count { get; private set; }

        [HarmonyPatch(typeof(Smelter), "Awake")]
        private static class Patch_SmelterAwake
        {
            private static void Postfix(Smelter __instance) { if (__instance != null) s_known.Add(__instance); }
        }

        private static Vector2Int Key(Vector3 p) => new Vector2Int(Mathf.RoundToInt(p.x * 2f), Mathf.RoundToInt(p.z * 2f));

        // FTS's (and FatFireBullseye's) definition: the Heat area's collider shape x scale, min 0.5.
        internal static float RadiusOf(EffectArea a)
        {
            var col = a != null ? a.GetComponent<Collider>() : null;
            if (col == null) return 3f;
            Vector3 s = a.transform.lossyScale; float r;
            if (col is SphereCollider sc) r = sc.radius * Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.z));
            else if (col is BoxCollider bc) r = Mathf.Max(bc.size.x * Mathf.Abs(s.x), bc.size.z * Mathf.Abs(s.z)) * 0.5f;
            else if (col is CapsuleCollider cc) r = cc.radius * Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.z));
            else r = Mathf.Max(col.bounds.extents.x, col.bounds.extents.z);
            return Mathf.Max(0.5f, r);
        }

        internal static float StationOuterM(string prefab)
        {
            string n = prefab.ToLowerInvariant();
            if (n.Contains("blastfurnace") || n.Contains("blast_furnace")) return 7f;
            if (n.Contains("kiln")) return 5f;
            if (n.Contains("smelter") || n.Contains("furnace")) return 6f;
            return -1f;
        }

        private static float Grow(Vector3 p, float r, Vector3 cam)
        {
            var k = Key(p); s_seen.Add(k);
            bool had = s_grow.TryGetValue(k, out float g);
            if (!had && (s_firstTick || (p - cam).sqrMagnitude > 40f * 40f)) g = 1f;
            else if (!had) { g = 0f; FiresLogger.LogDebug($"[FireMelt] heat: {p.x:0},{p.z:0} (r {r:0.0} m) lit, ring grows over {GrowSec:0} s (0.2.1405)"); }
            float before = g; g = Mathf.Min(1f, g + s_growDt / GrowSec); s_grow[k] = g;
            if (before < 1f && g >= 1f) FiresLogger.LogDebug($"[FireMelt] heat: {p.x:0},{p.z:0} ring full (0.2.1405)");
            return g * g * (3f - 2f * g);
        }

        // Called every frame (FatSnowGlobals); works once a second.
        internal static void Tick()
        {
            if (Time.unscaledTime < s_next) return;
            s_next = Time.unscaledTime + 1f;
            var cam = Utils.GetMainCamera();
            if (cam == null) return;
            if (!s_said)
            {
                s_said = true;
                FiresLogger.LogInfo($"[FireMelt] FAT writes the heat globals (_FiresHeat[{MaxHeat}], _FiresHeatCount, _FiresHeatCampfireR = {FatFireBullseye.CampfireR:0.00} m) for the voxel snow; FTS's piece shader reads them while FAT is loaded (0.2.1405)");
            }
            Vector3 cp = cam.transform.position;
            float now = Time.time;
            s_firstTick = s_growLast < 0f; s_growDt = s_growLast < 0f ? 0f : Mathf.Clamp(now - s_growLast, 0f, 5f); s_growLast = now;
            s_all.Clear();
            if (s_allAreas?.GetValue(null) is System.Collections.IList areas)
                foreach (var o in areas)
                {
                    var a = o as EffectArea;
                    if (a == null || !a.isActiveAndEnabled || (a.m_type & EffectArea.Type.Heat) == 0) continue;
                    float r = RadiusOf(a);
                    var p = a.transform.position;
                    var fk = Key(p); s_seen.Add(fk);
                    if ((p - cp).sqrMagnitude > Range * Range) { if (!s_grow.ContainsKey(fk)) s_grow[fk] = 1f; continue; }
                    s_all.Add(new Vector4(p.x, p.y, p.z, r * Grow(p, r, cp)));
                }
            AddStations(cp);
            var gone = new List<Vector2Int>();
            foreach (var k in s_grow.Keys) if (!s_seen.Contains(k)) gone.Add(k);
            foreach (var k in gone) s_grow.Remove(k);
            s_seen.Clear();
            s_all.Sort((x, y) => ((Vector3)x - cp).sqrMagnitude.CompareTo(((Vector3)y - cp).sqrMagnitude));
            int n = Mathf.Min(MaxHeat, s_all.Count);
            for (int i = 0; i < MaxHeat; i++) s_heat[i] = i < n ? s_all[i] : Vector4.zero;
            Shader.SetGlobalVectorArray(s_heatId, s_heat);
            Shader.SetGlobalFloat(s_heatCountId, n);
            Shader.SetGlobalFloat(s_campfireRId, FatFireBullseye.CampfireR);
            Count = n;
            if (n != s_lastCount) { s_lastCount = n; FiresLogger.LogDebug($"[FireMelt] heat: {n} source(s) near the camera{(n > 0 ? $" (nearest w {s_heat[0].w:0.0} at {(Vector3)s_heat[0]:F0})" : "")} (0.2.1405)"); }
        }

        private static void AddStations(Vector3 cp)
        {
            if (Time.unscaledTime >= s_nextStationScan)
            {
                s_nextStationScan = Time.unscaledTime + StationScanSec;
                s_stations.Clear();
                s_known.RemoveWhere(x => x == null);
                foreach (var s in s_known)
                    if (s != null && StationOuterM(s.name) > 0f && (s.transform.position - cp).sqrMagnitude < Range * Range) s_stations.Add(s);
            }
            float cr = FatFireBullseye.CampfireR > 0f ? FatFireBullseye.CampfireR : 3f;
            foreach (var s in s_stations)
            {
                if (s == null) continue;
                var p = s.transform.position; var k = Key(p);
                bool burning = s.m_enabledObject != null && s.m_enabledObject.activeInHierarchy;
                string prefab = Utils.GetPrefabName(s.gameObject);
                if (!s_stationSaid.TryGetValue(k, out bool was) || was != burning)
                {
                    s_stationSaid[k] = burning;
                    FiresLogger.LogDebug($"[FireMelt] station heat: {prefab} at {p.x:0},{p.z:0} {(burning ? $"burning: ring outer {StationOuterM(prefab):0} m" : "not burning: no ring")} (0.2.1405)");
                }
                if (!burning) continue;
                float w = StationOuterM(prefab) * cr / 4f;   // outer = 4 x w / campfire R
                s_seen.Add(k);
                s_all.Add(new Vector4(p.x, p.y, p.z, w * Grow(p, w, cp)));
            }
        }
    }

    // One client-side driver for FAT's snow globals (heat, look) and the skyland snow scan, on every world type (FTS's piece and
    // heightmap shaders read the same globals while FAT is loaded). Created once at ZNetScene.Awake; never on a dedicated server.
    internal sealed class FatSnowGlobals : MonoBehaviour
    {
        private static FatSnowGlobals s_instance;

        [HarmonyPatch(typeof(ZNetScene), "Awake")]
        private static class Patch_SceneAwake
        {
            private static void Postfix()
            {
                try
                {
                    if (s_instance != null || FiresCore.Lifecycle.FiresMod.IsDedicatedServer) return;
                    var go = new GameObject("FAT_SnowGlobals");
                    DontDestroyOnLoad(go);
                    s_instance = go.AddComponent<FatSnowGlobals>();
                }
                catch (System.Exception ex) { FiresLogger.LogDebug($"[FireMelt] snow globals driver not started: {ex.Message}"); }
            }
        }

        private void Update()
        {
            try { FatHeatSources.Tick(); } catch (System.Exception ex) { FiresLogger.LogDebug($"[FireMelt] heat tick threw: {ex.Message}"); }
            try { FatSnowLookDefaults.Tick(); } catch (System.Exception ex) { FiresLogger.LogDebug($"[SnowLook] tick threw: {ex.Message}"); }
            try { FatSkylandSnow.Tick(); } catch (System.Exception ex) { FiresLogger.LogDebug($"[SkylandSnow] tick threw: {ex.Message}"); }
        }
    }
}
