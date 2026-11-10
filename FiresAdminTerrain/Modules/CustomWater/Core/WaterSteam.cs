using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    // 0.2.1097 (Fire: lava meeting water): steam over the cooled-crust border where lava meets water (WaterStateField). The sheet
    // builders note the border columns (Note); a small pool of local particle emitters sits on the nearest ones to the camera:
    // at most PerTile per 32 m tile and Pool in all, only within Reach. The particle material is vanilla's own steam (fx_FoodSteam,
    // the 1.0 rip's Feasts steam) when ZNetScene has it, else the first smoke material found; never a networked object - the
    // prefab is only read for its material. Graphics peers only.
    internal static class WaterSteam
    {
        private const int Pool = 24, PerTile = 3, MaxNotes = 4096;
        private const float Reach = 70f, Every = 1f;
        private static readonly HashSet<Vector3Int> s_notes = new HashSet<Vector3Int>();
        private static readonly List<ParticleSystem> s_pool = new List<ParticleSystem>();
        private static readonly List<Vector3Int> s_pick = new List<Vector3Int>();
        private static readonly Dictionary<long, int> s_perTile = new Dictionary<long, int>();
        private static int s_version = -1;
        private static float s_next;
        private static Material s_mat;
        private static bool s_matTried, s_said;

        internal static bool Active => SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null;

        // A crust column on the water side of the band (from the sheet builders).
        internal static void Note(float x, float y, float z)
        {
            if (!Active || s_notes.Count >= MaxNotes) return;
            s_notes.Add(new Vector3Int(Mathf.FloorToInt(x), Mathf.RoundToInt(y * 4f), Mathf.FloorToInt(z)));
        }

        internal static void Tick()
        {
            if (!Active || Time.time < s_next) return;
            s_next = Time.time + Every;
            if (s_version != WaterStateField.Version) { s_version = WaterStateField.Version; s_notes.Clear(); }
            var cam = Utils.GetMainCamera();
            if (cam == null) return;
            Vector3 c = cam.transform.position;
            s_pick.Clear(); s_perTile.Clear();
            foreach (var n in s_notes)
            {
                float dx = n.x + 0.5f - c.x, dz = n.z + 0.5f - c.z;
                if (dx * dx + dz * dz > Reach * Reach) continue;
                s_pick.Add(n);
            }
            s_pick.Sort((a, b) => ((a.x - c.x) * (a.x - c.x) + (a.z - c.z) * (a.z - c.z)).CompareTo((b.x - c.x) * (b.x - c.x) + (b.z - c.z) * (b.z - c.z)));
            int used = 0;
            foreach (var n in s_pick)
            {
                if (used >= Pool) break;
                long tile = ((long)Mathf.FloorToInt(n.x / 32f) << 32) ^ (uint)Mathf.FloorToInt(n.z / 32f);
                s_perTile.TryGetValue(tile, out int k);
                if (k >= PerTile) continue;
                s_perTile[tile] = k + 1;
                var ps = Emitter(used);
                if (ps == null) return;
                ps.transform.position = new Vector3(n.x + 0.5f, n.y / 4f + 0.05f, n.z + 0.5f);
                if (!ps.gameObject.activeSelf) ps.gameObject.SetActive(true);
                used++;
            }
            for (int i = used; i < s_pool.Count; i++) if (s_pool[i] != null && s_pool[i].gameObject.activeSelf) s_pool[i].gameObject.SetActive(false);
            if (used > 0 && !s_said)
            {
                s_said = true;
                FiresLogger.LogInfo($"[WaterState] steam: {used} emitter(s) on the lava/water border near the camera ({s_notes.Count} border column(s) noted; material '{(s_mat != null ? s_mat.name : "none")}')");
            }
        }

        private static ParticleSystem Emitter(int i)
        {
            while (s_pool.Count <= i) s_pool.Add(null);
            if (s_pool[i] != null) return s_pool[i];
            var mat = Material();
            if (mat == null) return null;
            var go = new GameObject("fires_water_steam");
            Object.DontDestroyOnLoad(go);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.loop = true; main.startLifetime = 3.5f; main.startSpeed = 0.6f; main.startSize = new ParticleSystem.MinMaxCurve(1.2f, 2.4f);
            main.startColor = new Color(1f, 1f, 1f, 0.22f); main.maxParticles = 30; main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.gravityModifier = -0.04f;
            var em = ps.emission; em.rateOverTime = 4f;
            var sh = ps.shape; sh.shapeType = ParticleSystemShapeType.Circle; sh.radius = 0.8f; sh.rotation = new Vector3(-90f, 0f, 0f);
            var col = ps.colorOverLifetime; col.enabled = true;
            var g = new Gradient();
            g.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                      new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(0.25f, 0.25f), new GradientAlphaKey(0f, 1f) });
            col.color = g;
            var r = go.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            ps.Play();
            s_pool[i] = ps;
            return ps;
        }

        private static Material Material()
        {
            if (s_matTried) return s_mat;
            if (ZNetScene.instance == null) return null;
            s_matTried = true;
            foreach (var name in new[] { "fx_FoodSteam", "fx_FoodSteam_Small", "vfx_smelter_add", "vfx_Smoke" })
            {
                var p = ZNetScene.instance.GetPrefab(name);
                var pr = p != null ? p.GetComponentInChildren<ParticleSystemRenderer>(true) : null;
                if (pr != null && pr.sharedMaterial != null) { s_mat = pr.sharedMaterial; break; }
            }
            if (s_mat == null)
            {
                var sh = Shader.Find("Legacy Shaders/Particles/Alpha Blended") ?? Shader.Find("Sprites/Default");
                if (sh != null) s_mat = new Material(sh) { name = "fires_water_steam_fallback" };
            }
            FiresLogger.LogInfo($"[WaterState] steam material: {(s_mat != null ? s_mat.name : "none found - no steam")}");
            return s_mat;
        }
    }
}
