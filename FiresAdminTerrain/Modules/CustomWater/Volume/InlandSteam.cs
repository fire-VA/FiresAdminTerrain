using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Rendering;
using VerdantsAscent.Terrain.Voxel;

namespace VerdantsAscent.Water.Core
{
    // A light steam over still inland water (lake, pond, swamp,
    // hot spring) in warm biomes: Plains and Ashlands by default. Client only; nothing on a headless server. Budget-capped like the
    // waterfall mist: at most MaxEmitters emitters, only within Range of the player, re-placed every few seconds. Strongest at dawn
    // and dusk (cool air over warm water), faint at midday. Uses the waterfall mist material (rainbow off).
    internal sealed class InlandSteam : MonoBehaviour
    {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> Range;
        internal static ConfigEntry<int> MaxEmitters;
        private static readonly ConfigEntry<bool>[] s_biomeOn = new ConfigEntry<bool>[9];
        private static readonly ConfigEntry<float>[] s_biomeDensity = new ConfigEntry<float>[9];

        private const string Section = "Ocean - Inland steam";
        private const float SearchStep = 12f, BaseRate = 6f, PuffSize = 3.5f;
        private static readonly string[] BiomeNames = { "Meadows", "Black Forest", "Swamp", "Mountain", "Plains", "Mistlands", "Ashlands", "Deep North", "Ocean" };
        // Defaults: Plains steam (light), Ashlands steam (strong). Mistlands is tropical now, no mist.
        private static readonly float[] DefaultDensity = { 0f, 0f, 0f, 0f, 0.4f, 0f, 0.8f, 0f, 0f };

        private readonly List<ParticleSystem> _emitters = new List<ParticleSystem>();
        private readonly List<float> _density = new List<float>();
        private float _nextSearch, _nextTune;
        private int _placedLogged = -1;

        internal static void Bind(ConfigFile cfg)
        {
            Enabled = cfg.Bind(Section, "Steam over warm water", true,
                "A light steam rising off still lakes and ponds in warm biomes (Plains, Ashlands by default). Strongest at dawn and dusk.");
            Range = cfg.Bind(Section, "Steam range (m)", 80f,
                new ConfigDescription("Steam shows only on water this near the player.", new AcceptableValueRange<float>(20f, 200f)));
            MaxEmitters = cfg.Bind(Section, "Steam emitters (max)", 6,
                new ConfigDescription("Most steam patches at once (the frame-time budget).", new AcceptableValueRange<int>(0, 16)));
            for (int b = 0; b < 8; b++)
            {
                s_biomeOn[b] = cfg.Bind(Section, BiomeNames[b] + " steam", DefaultDensity[b] > 0f, $"Steam over still water in {BiomeNames[b]}.");
                s_biomeDensity[b] = cfg.Bind(Section, BiomeNames[b] + " steam density", DefaultDensity[b] > 0f ? DefaultDensity[b] : 0.4f,
                    new ConfigDescription("How thick the steam is (0-1).", new AcceptableValueRange<float>(0f, 1f)));
            }
        }

        internal static void EnsureAttached()
        {
            if (Application.isBatchMode || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return;
            var root = global::VerdantsAscent.FiresAdminTerrain.RootObject;
            if (root == null || root.GetComponent<InlandSteam>() != null) return;
            root.AddComponent<InlandSteam>();
        }

        private void Update()
        {
            var player = Player.m_localPlayer;
            bool on = Enabled != null && Enabled.Value && FiresWorldGenBase.Active && player != null && !VerdantsAscent.Terrain.Voxel.FatSuperFlat.On;   // no steam over real lakes on super flat
            if (!on) { Place(0, null); return; }
            if (Time.time >= _nextSearch)
            {
                _nextSearch = Time.time + 3f;
                Search(player.transform.position);
            }
            if (Time.time >= _nextTune)
            {
                _nextTune = Time.time + 2f;
                float t = Strength();
                for (int i = 0; i < _emitters.Count; i++)
                {
                    if (_emitters[i] == null || !_emitters[i].gameObject.activeSelf) continue;
                    var em = _emitters[i].emission;
                    em.rateOverTime = BaseRate * _density[i] * t;
                }
            }
        }

        // Dawn/dusk peak: 1 at day fractions 0.25 and 0.75, 0.3 at midday and midnight.
        private static float Strength()
        {
            var env = EnvMan.instance;
            if (env == null) return 0.6f;
            float f = env.GetDayFraction();
            float d = Mathf.Min(Mathf.Abs(f - 0.25f), Mathf.Abs(f - 0.75f));
            return Mathf.Lerp(1f, 0.3f, Mathf.Clamp01(d / 0.2f));
        }

        private readonly List<Vector4> _found = new List<Vector4>();

        private void Search(Vector3 p)
        {
            _found.Clear();
            float range = Range != null ? Range.Value : 80f;
            int cap = MaxEmitters != null ? MaxEmitters.Value : 6;
            for (float dz = -range; dz <= range; dz += SearchStep)
                for (float dx = -range; dx <= range; dx += SearchStep)
                {
                    if (dx * dx + dz * dz > range * range) continue;
                    float x = p.x + dx, z = p.z + dz;
                    byte c = FiresWorldGenBase.WaterClassAt(x, z);
                    if (c != FiresWorldGenBase.WC_LAKE && c != FiresWorldGenBase.WC_POND && c != FiresWorldGenBase.WC_SWAMP && c != FiresWorldGenBase.WC_HOTSPRING) continue;
                    int b = FiresWorldGenBase.BiomeCodeAt(x, z);
                    if (b < 0 || b >= 8 || s_biomeOn[b] == null || !s_biomeOn[b].Value || s_biomeDensity[b].Value <= 0f) continue;
                    if (!FiresWorldGenBase.TryGetWaterSurfaceGen(x, z, out float level, out _)) continue;
                    _found.Add(new Vector4(x, level, z, s_biomeDensity[b].Value + (dx * dx + dz * dz) * 1e-7f));
                }
            // nearest first, at most cap
            _found.Sort((a, b) => ((a.x - p.x) * (a.x - p.x) + (a.z - p.z) * (a.z - p.z)).CompareTo((b.x - p.x) * (b.x - p.x) + (b.z - p.z) * (b.z - p.z)));
            Place(Mathf.Min(cap, _found.Count), _found);
        }

        private void Place(int n, List<Vector4> spots)
        {
            for (int i = 0; i < _emitters.Count; i++)
                if (_emitters[i] != null && i >= n && _emitters[i].gameObject.activeSelf) _emitters[i].gameObject.SetActive(false);
            if (n == 0) { if (_placedLogged != 0 && _placedLogged != -1) FiresLogger.LogDebug("[InlandWater] steam off (no warm still water in range)"); if (_placedLogged != -1) _placedLogged = 0; return; }
            var mat = FiresWaterfalls.MistMaterial;
            if (mat == null) return;
            float t = Strength();
            for (int i = 0; i < n; i++)
            {
                if (i >= _emitters.Count) { _emitters.Add(Make(mat)); _density.Add(0f); }
                var ps = _emitters[i];
                var s = spots[i];
                ps.transform.position = new Vector3(s.x, s.y + 0.2f, s.z);
                _density[i] = Mathf.Clamp01(s.w);
                var em = ps.emission;
                em.rateOverTime = BaseRate * _density[i] * t;
                if (!ps.gameObject.activeSelf) ps.gameObject.SetActive(true);
            }
            if (n != _placedLogged)
            {
                _placedLogged = n;
                FiresLogger.LogDebug($"[InlandWater] steam: {n} emitters over warm still water, strength {t:0.00}");
            }
        }

        private ParticleSystem Make(Material mat)
        {
            var go = new GameObject("FiresInlandSteam");
            go.transform.SetParent(transform, false);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.loop = true;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.cullingMode = ParticleSystemCullingMode.Pause;
            main.startLifetime = new ParticleSystem.MinMaxCurve(4f, 7f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.05f, 0.2f);
            main.startSize = new ParticleSystem.MinMaxCurve(PuffSize * 0.7f, PuffSize * 1.3f);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.startColor = new Color(1f, 1f, 1f, 0.12f);
            main.maxParticles = 60;
            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(SearchStep, 0.1f, SearchStep);
            var vel = ps.velocityOverLifetime;
            vel.enabled = true;
            vel.space = ParticleSystemSimulationSpace.World;
            vel.x = new ParticleSystem.MinMaxCurve(-0.1f, 0.1f);
            vel.y = new ParticleSystem.MinMaxCurve(0.15f, 0.4f);
            vel.z = new ParticleSystem.MinMaxCurve(-0.1f, 0.1f);
            var col = ps.colorOverLifetime;
            col.enabled = true;
            var g = new Gradient();
            g.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.3f), new GradientAlphaKey(0f, 1f) });
            col.color = new ParticleSystem.MinMaxGradient(g);
            var sheet = ps.textureSheetAnimation;
            sheet.enabled = true;
            sheet.mode = ParticleSystemAnimationMode.Grid;
            sheet.numTilesX = 2; sheet.numTilesY = 2;
            sheet.animation = ParticleSystemAnimationType.WholeSheet;
            sheet.frameOverTime = new ParticleSystem.MinMaxCurve(0f, 0.999f);
            sheet.cycleCount = 1;
            var r = go.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = mat;
            r.renderMode = ParticleSystemRenderMode.Billboard;
            r.sortMode = ParticleSystemSortMode.Distance;
            r.maxParticleSize = 1f;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.SetActiveVertexStreams(new List<ParticleSystemVertexStream> { ParticleSystemVertexStream.Position, ParticleSystemVertexStream.Normal,
                ParticleSystemVertexStream.Color, ParticleSystemVertexStream.UV, ParticleSystemVertexStream.Tangent });   // as the waterfall mist
            ps.Play();
            return ps;
        }

        private void OnDestroy()
        {
            foreach (var ps in _emitters) if (ps != null) Destroy(ps.gameObject);
        }
    }
}
