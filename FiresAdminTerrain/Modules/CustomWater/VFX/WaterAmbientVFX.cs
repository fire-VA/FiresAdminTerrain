using System;
using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// Spawns ephemeral ambient VFX and audio on a WaterMeshCombiner.
    /// 
    /// ALL EFFECTS ARE NON-PERSISTENT:
    /// - Created as child GameObjects of a container parented to the combiner
    /// - No ZNetView, no ZDO, no ZNetScene registration
    /// - Destroyed automatically when combiner is destroyed (cascade)
    /// - Re-created on combiner restore/regeneration
    /// 
    /// FIVE EFFECT TYPES:
    /// 1. SHORELINE SPLASH - particle mist at water/terrain boundary edges
    /// 2. SHORELINE FOAM - subtle white particle line along banks
    /// 3. WATERFALL SPRAY - mist VFX at cliff cells (>2m drop)
    /// 4. SURFACE DEBRIS - floating leaves/foam bits drifting on the water surface
    /// 5. AMBIENT AUDIO - looping water sound (river rush or pond ambience)
    /// 
    /// PERFORMANCE BUDGET:
    /// - Max 8 shoreline splash emitters per combiner
    /// - Max 4 waterfall spray emitters per combiner
    /// - 1 surface debris emitter per combiner (covers full bounds)
    /// - 1 audio source per combiner
    /// - All use Unity ParticleSystem - zero CPU when off-screen (culled by renderer bounds)
    /// </summary>
    public class WaterAmbientVFX : MonoBehaviour
    {
        #region Constants

        private const int MAX_SHORELINE_EMITTERS = 8;
        private const int MAX_WATERFALL_EMITTERS = 4;
        private const float MIN_SPACING_BETWEEN_EMITTERS = 8f;
        private const float SHORELINE_SCAN_DEPTH_THRESHOLD = 0.15f;
        private const float WATERFALL_MIN_DROP = 2f;

        // Audio
        private const float RIVER_AUDIO_MAX_DISTANCE = 40f;
        private const float POND_AUDIO_MAX_DISTANCE = 25f;
        private const float RIVER_AUDIO_VOLUME = 0.3f;
        private const float POND_AUDIO_VOLUME = 0.15f;

        #endregion

        #region State

        private WaterMeshCombiner _combiner;
        private GameObject _vfxContainer;
        private List<ParticleSystem> _shorelineSplash = new List<ParticleSystem>();
        private List<ParticleSystem> _waterfallSpray = new List<ParticleSystem>();
        private ParticleSystem _surfaceDebris;
        private AudioSource _ambientAudio;
        private bool _initialized;

        // Cached Valheim audio clips (searched once)
        private static AudioClip s_riverAudioClip;
        private static AudioClip s_pondAudioClip;
        private static bool s_audioSearched;

        #endregion

        #region Public API

        /// <summary>
        /// Initializes the VFX system for a combiner.
        /// Call this after mesh regeneration completes.
        /// </summary>
        public void Initialize(WaterMeshCombiner combiner)
        {
            _combiner = combiner;

            // Clean up any existing VFX
            CleanupVFX();

            // Create container parented to combiner - cascade destroy
            _vfxContainer = new GameObject("WaterAmbientVFX");
            _vfxContainer.transform.SetParent(combiner.transform, false);
            _vfxContainer.transform.localPosition = Vector3.zero;

            RebuildEffects();
            _initialized = true;
        }

        /// <summary>
        /// Rebuilds all VFX after mesh regeneration.
        /// </summary>
        public void RebuildEffects()
        {
            if (_combiner == null || _vfxContainer == null) return;

            // Clear old child effects
            for (int i = _vfxContainer.transform.childCount - 1; i >= 0; i--)
                Destroy(_vfxContainer.transform.GetChild(i).gameObject);

            _shorelineSplash.Clear();
            _waterfallSpray.Clear();
            _surfaceDebris = null;
            _ambientAudio = null;

            // Detect features from combiner grid data
            var shorelinePoints = DetectShorelineEdges();
            var cliffData = _combiner.GetCliffCellData();

            // 1. Shoreline splash
            SpawnShorelineSplash(shorelinePoints);

            // 2. Waterfall spray
            if (cliffData != null)
                SpawnWaterfallSpray(cliffData);

            // 3. Surface debris
            SpawnSurfaceDebris();

            // 4. Ambient audio - disabled: auto-placed SFX conflicts with world audio
            // SpawnAmbientAudio();
        }

        /// <summary>
        /// Cleans up all VFX. Safe to call multiple times.
        /// </summary>
        public void CleanupVFX()
        {
            _shorelineSplash.Clear();
            _waterfallSpray.Clear();
            _surfaceDebris = null;
            _ambientAudio = null;

            if (_vfxContainer != null)
            {
                Destroy(_vfxContainer);
                _vfxContainer = null;
            }

            _initialized = false;
        }

        #endregion

        #region Lifecycle

        private void OnDestroy()
        {
            CleanupVFX();
        }

        #endregion

        #region Shoreline Edge Detection

        /// <summary>
        /// Data for a shoreline point where water meets terrain.
        /// </summary>
        private struct ShorelinePoint
        {
            public Vector3 WorldPos;
            public Vector3 Normal; // Points away from water (into terrain)
            public float WaterDepth;
            public float SurfaceY;
        }

        /// <summary>
        /// Scans the combiner's grid to find cells at the water/terrain boundary.
        /// A shoreline cell has water (depth > threshold) and at least one neighbor
        /// with no water or very shallow water.
        /// 
        /// Returns up to MAX_SHORELINE_EMITTERS points, spaced apart by MIN_SPACING.
        /// </summary>
        private List<ShorelinePoint> DetectShorelineEdges()
        {
            var candidates = new List<ShorelinePoint>();

            var surfaceHeights = _combiner.GetSurfaceHeights();
            var terrainHeights = _combiner.GetTerrainHeights();

            if (surfaceHeights == null || terrainHeights == null) return candidates;

            int gridW = _combiner.GridWidth;
            int gridH = _combiner.GridHeight;
            if (gridW < 3 || gridH < 3) return candidates;

            int[] dx = { -1, 1, 0, 0 };
            int[] dz = { 0, 0, -1, 1 };

            // Pass 1: find ALL shoreline cells
            for (int z = 1; z < gridH - 1; z++)
            {
                for (int x = 1; x < gridW - 1; x++)
                {
                    int idx = z * gridW + x;
                    float surfY = surfaceHeights[idx];
                    if (surfY < -50f) continue;

                    float terrY = terrainHeights[idx];
                    float depth = surfY - terrY;
                    if (depth < SHORELINE_SCAN_DEPTH_THRESHOLD) continue;

                    // Check if any neighbor is dry (no water or below threshold)
                    Vector3 normalAccum = Vector3.zero;
                    bool isEdge = false;

                    for (int d = 0; d < 4; d++)
                    {
                        int nx = x + dx[d];
                        int nz = z + dz[d];
                        int nidx = nz * gridW + nx;

                        float nSurfY = surfaceHeights[nidx];
                        bool neighborDry = nSurfY < -50f;

                        if (!neighborDry)
                        {
                            float nDepth = nSurfY - terrainHeights[nidx];
                            neighborDry = nDepth < SHORELINE_SCAN_DEPTH_THRESHOLD * 0.5f;
                        }

                        if (neighborDry)
                        {
                            isEdge = true;
                            // Normal points toward the dry neighbor (away from water)
                            normalAccum += new Vector3(dx[d], 0, dz[d]);
                        }
                    }

                    if (!isEdge) continue;

                    Vector3 worldPos = _combiner.GridToWorld(x, z);
                    worldPos.y = surfY;

                    if (normalAccum.sqrMagnitude > 0.001f)
                        normalAccum.Normalize();
                    else
                        normalAccum = Vector3.up;

                    candidates.Add(new ShorelinePoint
                    {
                        WorldPos = worldPos,
                        Normal = normalAccum,
                        WaterDepth = depth,
                        SurfaceY = surfY
                    });
                }
            }

            // Pass 2: downsample to MAX_SHORELINE_EMITTERS with spacing
            if (candidates.Count <= MAX_SHORELINE_EMITTERS)
                return candidates;

            var selected = new List<ShorelinePoint>();
            float spacingSq = MIN_SPACING_BETWEEN_EMITTERS * MIN_SPACING_BETWEEN_EMITTERS;

            // Stride through candidates evenly
            float step = (float)candidates.Count / MAX_SHORELINE_EMITTERS;
            for (float fi = 0; fi < candidates.Count && selected.Count < MAX_SHORELINE_EMITTERS; fi += step)
            {
                var candidate = candidates[(int)fi];

                // Enforce minimum spacing
                bool tooClose = false;
                for (int s = 0; s < selected.Count; s++)
                {
                    float ddx = candidate.WorldPos.x - selected[s].WorldPos.x;
                    float ddz = candidate.WorldPos.z - selected[s].WorldPos.z;
                    if (ddx * ddx + ddz * ddz < spacingSq)
                    {
                        tooClose = true;
                        break;
                    }
                }

                if (!tooClose)
                    selected.Add(candidate);
            }

            return selected;
        }

        #endregion

        #region VFX Spawning

        /// <summary>
        /// Creates shoreline splash particle emitters at detected edge points.
        /// Uses a simple procedural ParticleSystem (no prefab dependency).
        /// </summary>
        private void SpawnShorelineSplash(List<ShorelinePoint> points)
        {
            if (points == null || points.Count == 0) return;

            foreach (var pt in points)
            {
                var go = new GameObject("ShorelineSplash");
                go.transform.SetParent(_vfxContainer.transform, false);
                go.transform.position = pt.WorldPos;

                var ps = go.AddComponent<ParticleSystem>();
                var main = ps.main;
                main.loop = true;
                main.startLifetime = 1.5f;
                main.startSpeed = new ParticleSystem.MinMaxCurve(0.2f, 0.6f);
                main.startSize = new ParticleSystem.MinMaxCurve(0.1f, 0.3f);
                main.startColor = new Color(0.85f, 0.9f, 1f, 0.4f);
                main.maxParticles = 15;
                main.simulationSpace = ParticleSystemSimulationSpace.World;
                main.gravityModifier = 0.3f;

                var emission = ps.emission;
                emission.rateOverTime = 5f;

                var shape = ps.shape;
                shape.shapeType = ParticleSystemShapeType.Hemisphere;
                shape.radius = 0.5f;
                shape.rotation = new Vector3(-90f, 0f, 0f); // Emit upward

                var colorOverLifetime = ps.colorOverLifetime;
                colorOverLifetime.enabled = true;
                var gradient = new Gradient();
                gradient.SetKeys(
                    new GradientColorKey[] {
                        new GradientColorKey(new Color(0.85f, 0.9f, 1f), 0f),
                        new GradientColorKey(new Color(0.9f, 0.95f, 1f), 1f)
                    },
                    new GradientAlphaKey[] {
                        new GradientAlphaKey(0.4f, 0f),
                        new GradientAlphaKey(0.2f, 0.5f),
                        new GradientAlphaKey(0f, 1f)
                    }
                );
                colorOverLifetime.color = gradient;

                var sizeOverLifetime = ps.sizeOverLifetime;
                sizeOverLifetime.enabled = true;
                sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(
                    new Keyframe(0f, 0.5f),
                    new Keyframe(0.5f, 1f),
                    new Keyframe(1f, 0.2f)
                ));

                // Use default particle material
                var renderer = go.GetComponent<ParticleSystemRenderer>();
                renderer.material = GetParticleMaterial();
                renderer.renderMode = ParticleSystemRenderMode.Billboard;

                ps.Play();
                _shorelineSplash.Add(ps);
            }
        }

        /// <summary>
        /// Creates waterfall spray emitters at cliff cells with significant drops.
        /// </summary>
        private void SpawnWaterfallSpray(List<WaterMeshCombiner.CliffCellDataPublic> cliffData)
        {
            if (cliffData == null || cliffData.Count == 0) return;

            // Sort by drop height (largest first) and take the top N
            cliffData.Sort((a, b) => b.StretchDistance.CompareTo(a.StretchDistance));

            float spacingSq = MIN_SPACING_BETWEEN_EMITTERS * MIN_SPACING_BETWEEN_EMITTERS;
            var placed = new List<Vector3>();

            for (int i = 0; i < cliffData.Count && placed.Count < MAX_WATERFALL_EMITTERS; i++)
            {
                var cliff = cliffData[i];
                if (cliff.StretchDistance < WATERFALL_MIN_DROP) continue;

                // Spacing check
                bool tooClose = false;
                for (int p = 0; p < placed.Count; p++)
                {
                    float ddx = cliff.WorldPos.x - placed[p].x;
                    float ddz = cliff.WorldPos.z - placed[p].z;
                    if (ddx * ddx + ddz * ddz < spacingSq)
                    {
                        tooClose = true;
                        break;
                    }
                }
                if (tooClose) continue;

                var go = new GameObject("WaterfallSpray");
                go.transform.SetParent(_vfxContainer.transform, false);
                go.transform.position = cliff.WorldPos;

                var ps = go.AddComponent<ParticleSystem>();
                var main = ps.main;
                main.loop = true;
                main.startLifetime = new ParticleSystem.MinMaxCurve(1.0f, 2.5f);
                main.startSpeed = new ParticleSystem.MinMaxCurve(0.5f, 2.0f);
                main.startSize = new ParticleSystem.MinMaxCurve(0.3f, 1.0f);
                main.startColor = new Color(0.8f, 0.85f, 1f, 0.5f);
                main.maxParticles = 30;
                main.simulationSpace = ParticleSystemSimulationSpace.World;
                main.gravityModifier = 0.8f;

                var emission = ps.emission;
                // More particles for bigger drops
                float dropScale = Mathf.Clamp01(cliff.StretchDistance / 10f);
                emission.rateOverTime = Mathf.Lerp(5f, 20f, dropScale);

                var shape = ps.shape;
                shape.shapeType = ParticleSystemShapeType.Cone;
                shape.angle = 25f;
                shape.radius = 0.3f;
                // Point the cone in the cliff direction (downward and outward)
                Vector3 sprayDir = cliff.CliffDirection + Vector3.down * 0.5f;
                if (sprayDir.sqrMagnitude > 0.01f)
                    go.transform.rotation = Quaternion.LookRotation(sprayDir.normalized);

                var colorOverLifetime = ps.colorOverLifetime;
                colorOverLifetime.enabled = true;
                var gradient = new Gradient();
                gradient.SetKeys(
                    new GradientColorKey[] {
                        new GradientColorKey(new Color(0.8f, 0.85f, 1f), 0f),
                        new GradientColorKey(new Color(0.9f, 0.95f, 1f), 1f)
                    },
                    new GradientAlphaKey[] {
                        new GradientAlphaKey(0.5f, 0f),
                        new GradientAlphaKey(0.3f, 0.3f),
                        new GradientAlphaKey(0f, 1f)
                    }
                );
                colorOverLifetime.color = gradient;

                var renderer = go.GetComponent<ParticleSystemRenderer>();
                renderer.material = GetParticleMaterial();
                renderer.renderMode = ParticleSystemRenderMode.Billboard;

                ps.Play();
                _waterfallSpray.Add(ps);
                placed.Add(cliff.WorldPos);
            }
        }

        /// <summary>
        /// Creates a single particle system covering the water surface that emits
        /// floating debris (foam bits, small specks) drifting on the water.
        /// </summary>
        private void SpawnSurfaceDebris()
        {
            var bounds = _combiner.GetCombinedBounds();
            if (bounds.size.x < 2f || bounds.size.z < 2f) return;

            var go = new GameObject("SurfaceDebris");
            go.transform.SetParent(_vfxContainer.transform, false);
            go.transform.position = bounds.center;

            float surfaceY = _combiner.IsPondMode ? _combiner.SettledWaterLevel : bounds.center.y;
            go.transform.position = new Vector3(bounds.center.x, surfaceY + 0.05f, bounds.center.z);

            var ps = go.AddComponent<ParticleSystem>();
            var main = ps.main;
            main.loop = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(6f, 12f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.02f, 0.08f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.05f, 0.15f);
            main.startColor = new Color(0.95f, 0.97f, 1f, 0.35f);

            // Scale particle count with surface area, capped
            float area = bounds.size.x * bounds.size.z;
            int maxParticles = Mathf.Clamp((int)(area / 50f), 5, 40);
            main.maxParticles = maxParticles;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.gravityModifier = 0f;

            // 3D start rotation to randomize orientation of flat particles
            main.startRotation3D = true;
            main.startRotationX = new ParticleSystem.MinMaxCurve(80f * Mathf.Deg2Rad, 100f * Mathf.Deg2Rad);
            main.startRotationY = new ParticleSystem.MinMaxCurve(0f, 360f * Mathf.Deg2Rad);
            main.startRotationZ = 0f;

            var emission = ps.emission;
            emission.rateOverTime = Mathf.Clamp(area / 200f, 0.5f, 4f);

            // Emit from a box matching the water surface
            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(bounds.size.x * 0.8f, 0.01f, bounds.size.z * 0.8f);

            // Gentle drift via velocity over lifetime
            // NOTE: All three axes MUST use the same MinMaxCurve mode (TwoConstants)
            // or Unity logs "Particle Velocity curves must all be in the same mode"
            var velocityOverLifetime = ps.velocityOverLifetime;
            velocityOverLifetime.enabled = true;
            velocityOverLifetime.space = ParticleSystemSimulationSpace.World;
            velocityOverLifetime.x = new ParticleSystem.MinMaxCurve(-0.03f, 0.03f);
            velocityOverLifetime.y = new ParticleSystem.MinMaxCurve(0f, 0f);
            velocityOverLifetime.z = new ParticleSystem.MinMaxCurve(-0.03f, 0.03f);

            var colorOverLifetime = ps.colorOverLifetime;
            colorOverLifetime.enabled = true;
            var gradient = new Gradient();
            gradient.SetKeys(
                new GradientColorKey[] {
                    new GradientColorKey(new Color(0.95f, 0.97f, 1f), 0f),
                    new GradientColorKey(new Color(0.95f, 0.97f, 1f), 1f)
                },
                new GradientAlphaKey[] {
                    new GradientAlphaKey(0f, 0f),
                    new GradientAlphaKey(0.35f, 0.15f),
                    new GradientAlphaKey(0.35f, 0.7f),
                    new GradientAlphaKey(0f, 1f)
                }
            );
            colorOverLifetime.color = gradient;

            var renderer = go.GetComponent<ParticleSystemRenderer>();
            renderer.material = GetParticleMaterial();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;

            ps.Play();
            _surfaceDebris = ps;
        }




        #region Material Cache

        private static Material s_particleMaterial;

        /// <summary>
        /// Gets or creates a simple particle material.
        /// Uses Valheim's particle shader if available, falls back to Sprites/Default.
        /// </summary>
        private static Material GetParticleMaterial()
        {
            if (s_particleMaterial != null) return s_particleMaterial;

            // Try Valheim's particle shader
            var shader = Shader.Find("Particles/Standard Unlit");
            if (shader == null)
                shader = Shader.Find("Legacy Shaders/Particles/Alpha Blended");
            if (shader == null)
                shader = Shader.Find("Sprites/Default");

            if (shader != null)
            {
                s_particleMaterial = new Material(shader);
                s_particleMaterial.name = "WaterAmbientVFX_Particle";

                // Set rendering mode to additive/alpha blend
                if (s_particleMaterial.HasProperty("_Mode"))
                {
                    s_particleMaterial.SetFloat("_Mode", 2); // Fade
                }
                s_particleMaterial.SetColor("_Color", Color.white);
                s_particleMaterial.SetColor("_TintColor", new Color(0.5f, 0.5f, 0.5f, 0.5f));

                // Ensure proper rendering
                s_particleMaterial.renderQueue = 3100;
            }

            return s_particleMaterial;
        }

        #endregion

        #region Static Reset

        /// <summary>
        /// Resets cached static state. Call on logout/world change.
        /// </summary>
        public static void ResetStaticCache()
        {
            s_audioSearched = false;
            s_riverAudioClip = null;
            s_pondAudioClip = null;
            s_particleMaterial = null;
        }

        #endregion
    }
}
#endregion