using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace VerdantsAscent.Terrain.Voxel
{
    // Mist at every waterfall, drawn with Custom/FiresWaterfallMist from the "fireswaterfall" bundle: spray peeling off
    // the lip, a dense churn where the water lands on rock or water with a faint veil rising from it (the only layer that
    // shows the rainbow), and the cloud a skyland fall turns into where it thins out in mid-air. How high the churn and
    // veil rise grows with the drop, so a small step only steams at its foot. The recipes mirror the bundle builder's
    // preview (FiresWaterfallBuilder.Mist). A falls object's emitters sit under its RootName child and run only while
    // the camera is within the configured range.
    internal static class FiresWaterfallMist
    {
        internal const string RootName = "Mist";

        internal struct Site
        {
            internal Vector3 Lip;
            internal Vector3 Foot;
            internal Vector3 Outward;
            internal float Width;
            internal float Drop;
            internal float FadeLength;
            internal bool Dissolves;
            // A short step inside a river (Fire 10-05: "the rapids should be mist like we have at the bottom of our
            // waterfalls"): no curtain is hung there, only the Rapids spray rising off the step at Lip.
            internal bool Rapids;
        }

        private sealed class Recipe
        {
            internal string Name;
            internal float MinDrop;
            internal bool Rainbow;
            internal float LifeMin, LifeMax;
            internal float SpeedMin, SpeedMax;
            internal bool SpeedScales;
            internal float SizeMin, SizeMax;
            internal float Gravity;
            internal float RatePerMetre, RatePerMetrePerDrop, RatePerDrop, RateMin, RateMax;
            internal float BoxExtraWidth, BoxHeight, BoxHeightPerFade, BoxDepth;
            internal float RandomDirection;
            internal float DownMin, DownMax, RiseMin, RiseMax;
            internal float DriftMin, DriftMax;
            internal float PeakAlphaSmallDrop, PeakAlpha;
            internal float GrowStart, GrowMid, GrowEnd;
        }

        private static readonly Recipe Lip = new Recipe
        {
            // 2 m (was 6): every curtain that is drawn at all (FiresWaterfalls.MinFallLength) sprays at its lip, so the top of a
            // fall reads as white water the way its foot does (Fire 10-05).
            Name = "MistLip", MinDrop = 2f, LifeMin = 2f, LifeMax = 3.5f, SpeedMin = 0.6f, SpeedMax = 1.4f,
            SizeMin = 2.5f, SizeMax = 4.5f, Gravity = 0.35f, RatePerMetre = 1.5f, RateMin = 0f, RateMax = 18f,
            BoxExtraWidth = 1f, BoxHeight = 0.4f, BoxDepth = 0.4f, RandomDirection = 0.3f, DownMin = -2f, DownMax = -0.8f,
            DriftMin = 0f, DriftMax = 0.3f, PeakAlphaSmallDrop = 0.28f, PeakAlpha = 0.28f, GrowStart = 0.6f, GrowMid = 1.36f, GrowEnd = 2.5f,
        };

        private static readonly Recipe Churn = new Recipe
        {
            Name = "MistChurn", MinDrop = 2f, LifeMin = 2.5f, LifeMax = 4.5f, SpeedMin = 1f, SpeedMax = 3f, SpeedScales = true,
            SizeMin = 2.5f, SizeMax = 5f, RatePerMetre = 2f, RatePerMetrePerDrop = 0.1f, RateMin = 3f, RateMax = 40f,
            BoxExtraWidth = 1.5f, BoxHeight = 1f, BoxDepth = 2.5f, RandomDirection = 0.6f, RiseMin = 2f, RiseMax = 4f,
            DriftMin = 0.2f, DriftMax = 0.8f, PeakAlphaSmallDrop = 0.45f, PeakAlpha = 0.8f, GrowStart = 0.6f, GrowMid = 1f, GrowEnd = 1.8f,
        };

        private static readonly Recipe Veil = new Recipe
        {
            Name = "MistVeil", MinDrop = 8f, Rainbow = true, LifeMin = 6f, LifeMax = 9f, SpeedMin = 0.5f, SpeedMax = 1.2f, SpeedScales = true,
            SizeMin = 5f, SizeMax = 10f, RatePerMetre = 0.6f, RatePerDrop = 0.08f, RateMin = 1.5f, RateMax = 10f,
            BoxExtraWidth = 3f, BoxHeight = 2f, BoxDepth = 4f, RandomDirection = 0.5f, RiseMin = 1.25f, RiseMax = 2f,
            DriftMin = 0.4f, DriftMax = 1.2f, PeakAlphaSmallDrop = 0.3f, PeakAlpha = 0.3f, GrowStart = 0.7f, GrowMid = 1.3f, GrowEnd = 2.2f,
        };

        // Spray off a rapid: low, churning puffs over the step, smaller and thinner than a fall's churn.
        private static readonly Recipe RapidsSpray = new Recipe
        {
            Name = "MistRapids", MinDrop = 0f, LifeMin = 1.5f, LifeMax = 3f, SpeedMin = 0.4f, SpeedMax = 1.2f,
            SizeMin = 1.5f, SizeMax = 3f, Gravity = 0.05f, RatePerMetre = 1.2f, RateMin = 2f, RateMax = 14f,
            BoxExtraWidth = 0.5f, BoxHeight = 0.3f, BoxDepth = 1.2f, RandomDirection = 0.6f, RiseMin = 4f, RiseMax = 12f,
            DriftMin = 0.1f, DriftMax = 0.5f, PeakAlphaSmallDrop = 0.35f, PeakAlpha = 0.45f, GrowStart = 0.5f, GrowMid = 1f, GrowEnd = 1.6f,
        };

        private static readonly Recipe Dissolve = new Recipe
        {
            Name = "MistDissolve", MinDrop = 0f, LifeMin = 4f, LifeMax = 7f, SpeedMin = 0.2f, SpeedMax = 0.6f,
            SizeMin = 2.5f, SizeMax = 5f, Gravity = 0.08f, RatePerMetre = 3f, RatePerDrop = 0.1f, RateMin = 3f, RateMax = 30f,
            BoxExtraWidth = 1f, BoxHeightPerFade = 0.7f, BoxDepth = 1.5f, RandomDirection = 0.5f, DownMin = -2f, DownMax = -0.8f,
            DriftMin = 0.1f, DriftMax = 0.4f, PeakAlphaSmallDrop = 0.55f, PeakAlpha = 0.55f, GrowStart = 0.7f, GrowMid = 1.3f, GrowEnd = 2.2f,
        };

        private const float ScaleLowDrop = 3f;
        private const float ScaleHighDrop = 45f;
        private const float ScaleLow = 0.6f;
        private const float ScaleHigh = 1.6f;
        private const float FullAlphaDrop = 20f;
        private const float RisePerDrop = 0.02f;
        private const float RiseMinSpeed = 0.05f;
        private const float RiseMaxSpeed = 0.8f;
        private const float DissolveCentre = 0.45f;
        private const float LimitSpeed = 1.2f;
        private const float LimitDampen = 0.1f;
        private const float SpinRadians = 0.26f;
        private const float GrowMidTime = 0.4f;
        private const float AlphaRiseTime = 0.12f;
        private const float AlphaHoldTime = 0.6f;
        private const float AlphaHoldShare = 0.6f;
        private const float MaxScreenSize = 1f;
        private const int MaxParticles = 160;
        private const int PuffTiles = 2;
        private const float LastPuffFrame = 0.999f;
        private const float SignatureGrid = 10f;
        private const float TickSeconds = 0.5f;
        private const float ReportSeconds = 10f;
        private const float RangeHysteresis = 20f;

        private sealed class Group
        {
            internal GameObject Root;
            internal Vector3 Centre;
            internal int Signature;
            internal readonly List<ParticleSystem> Systems = new List<ParticleSystem>(4);
        }

        private sealed class Driver : MonoBehaviour
        {
            private void Update() => Tick();
        }

        private static readonly List<Group> s_groups = new List<Group>();
        private static readonly List<ParticleSystemVertexStream> s_streams = new List<ParticleSystemVertexStream>
        {
            ParticleSystemVertexStream.Position, ParticleSystemVertexStream.Normal, ParticleSystemVertexStream.Color,
            ParticleSystemVertexStream.UV, ParticleSystemVertexStream.Tangent,
        };
        private static GameObject s_driver;
        private static bool s_headless, s_headlessKnown;
        private static float s_nextTick, s_nextReport;
        private static int s_lastRunning = -1;

        private static bool Headless
        {
            get
            {
                if (!s_headlessKnown)
                {
                    s_headless = SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;
                    s_headlessKnown = true;
                }
                return s_headless;
            }
        }

        // Replaces the emitters under `falls` with the ones these sites raise; the same sites as last time leave them be.
        internal static void SetEmitters(Transform falls, List<Site> sites)
        {
            if (falls == null || Headless) return;
            var mist = FiresWaterfalls.MistMaterial;
            var veil = FiresWaterfalls.VeilMaterial;
            if (mist == null || veil == null) return;

            var rootTransform = falls.Find(RootName);
            var group = rootTransform != null ? Find(rootTransform.gameObject) : null;
            int signature = Signature(sites);
            if (group != null && group.Signature == signature) return;
            if (rootTransform == null)
            {
                var root = new GameObject(RootName);
                root.SetActive(false);
                root.transform.SetParent(falls, false);
                rootTransform = root.transform;
            }
            if (group == null)
            {
                group = new Group { Root = rootTransform.gameObject };
                s_groups.Add(group);
                EnsureDriver();
            }

            group.Root.SetActive(false);
            for (int i = rootTransform.childCount - 1; i >= 0; i--)
            {
                var old = rootTransform.GetChild(i).gameObject;
                old.SetActive(false);
                Object.Destroy(old);
            }
            group.Systems.Clear();
            group.Signature = signature;
            Vector3 centre = Vector3.zero;
            int rapids = 0;
            foreach (var site in sites)
            {
                if (site.Rapids)
                {
                    Raise(group, RapidsSpray, mist, site, site.Lip);
                    centre += site.Lip;
                    rapids++;
                    continue;
                }
                Raise(group, Lip, mist, site, site.Lip);
                if (site.Dissolves)
                {
                    var cloud = site.Foot;
                    cloud.y = site.Lip.y - (site.Drop - site.FadeLength * DissolveCentre);
                    Raise(group, Dissolve, mist, site, cloud);
                }
                else
                {
                    Raise(group, Churn, mist, site, site.Foot);
                    Raise(group, Veil, veil, site, site.Foot);
                }
                centre += site.Foot;
            }
            group.Centre = sites.Count > 0 ? centre / sites.Count : falls.position;
            FiresLogger.LogDebug($"[FiresWaterfall] mist raised {group.Systems.Count} emitter(s) at {sites.Count - rapids} fall(s) + {rapids} rapid(s) under '{falls.name}' at {falls.position}.");
            UpdateGroup(group, Utils.GetMainCamera());
        }

        private static void Raise(Group group, Recipe recipe, Material material, in Site site, Vector3 position)
        {
            if (site.Drop < recipe.MinDrop) return;
            float scale = Mathf.Lerp(ScaleLow, ScaleHigh, Mathf.InverseLerp(ScaleLowDrop, ScaleHighDrop, site.Drop));
            float rise = Mathf.Clamp(site.Drop * RisePerDrop, RiseMinSpeed, RiseMaxSpeed);
            float speedScale = recipe.SpeedScales ? scale : 1f;

            var go = new GameObject(recipe.Name);
            go.transform.SetParent(group.Root.transform, false);
            go.transform.SetPositionAndRotation(position, Quaternion.LookRotation(site.Outward, Vector3.up));
            var system = go.AddComponent<ParticleSystem>();

            var main = system.main;
            main.playOnAwake = true;
            main.loop = true;
            main.prewarm = true;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.cullingMode = ParticleSystemCullingMode.Pause;
            main.maxParticles = MaxParticles;
            main.startLifetime = new ParticleSystem.MinMaxCurve(recipe.LifeMin, recipe.LifeMax);
            main.startSpeed = new ParticleSystem.MinMaxCurve(recipe.SpeedMin * speedScale, recipe.SpeedMax * speedScale);
            main.startSize = new ParticleSystem.MinMaxCurve(recipe.SizeMin * scale, recipe.SizeMax * scale);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.startColor = Color.white;
            main.gravityModifier = recipe.Gravity;

            var emission = system.emission;
            emission.rateOverTime = Mathf.Clamp(site.Width * (recipe.RatePerMetre + site.Drop * recipe.RatePerMetrePerDrop)
                + site.Drop * recipe.RatePerDrop, recipe.RateMin, recipe.RateMax);

            var shape = system.shape;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(site.Width + recipe.BoxExtraWidth, recipe.BoxHeight + site.FadeLength * recipe.BoxHeightPerFade,
                recipe.BoxDepth);
            shape.randomDirectionAmount = recipe.RandomDirection;

            var velocity = system.velocityOverLifetime;
            velocity.enabled = true;
            velocity.space = ParticleSystemSimulationSpace.Local;
            velocity.x = new ParticleSystem.MinMaxCurve(0f, 0f);
            velocity.y = new ParticleSystem.MinMaxCurve(recipe.DownMin + rise * recipe.RiseMin, recipe.DownMax + rise * recipe.RiseMax);
            velocity.z = new ParticleSystem.MinMaxCurve(recipe.DriftMin, recipe.DriftMax);

            var limit = system.limitVelocityOverLifetime;
            limit.enabled = true;
            limit.limit = LimitSpeed * scale;
            limit.dampen = LimitDampen;

            var size = system.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0f, recipe.GrowStart),
                new Keyframe(GrowMidTime, recipe.GrowMid), new Keyframe(1f, recipe.GrowEnd)));

            float peak = Mathf.Lerp(recipe.PeakAlphaSmallDrop, recipe.PeakAlpha, Mathf.InverseLerp(ScaleLowDrop, FullAlphaDrop, site.Drop));
            var gradient = new Gradient();
            gradient.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(peak, AlphaRiseTime),
                    new GradientAlphaKey(peak * AlphaHoldShare, AlphaHoldTime), new GradientAlphaKey(0f, 1f) });
            var color = system.colorOverLifetime;
            color.enabled = true;
            color.color = new ParticleSystem.MinMaxGradient(gradient);

            var rotation = system.rotationOverLifetime;
            rotation.enabled = true;
            rotation.z = new ParticleSystem.MinMaxCurve(-SpinRadians, SpinRadians);

            var sheet = system.textureSheetAnimation;
            sheet.enabled = true;
            sheet.mode = ParticleSystemAnimationMode.Grid;
            sheet.numTilesX = PuffTiles;
            sheet.numTilesY = PuffTiles;
            sheet.animation = ParticleSystemAnimationType.WholeSheet;
            sheet.frameOverTime = new ParticleSystem.MinMaxCurve(0f, LastPuffFrame);
            sheet.cycleCount = 1;

            var renderer = go.GetComponent<ParticleSystemRenderer>();
            renderer.sharedMaterial = material;
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.normalDirection = 1f;
            renderer.sortMode = ParticleSystemSortMode.Distance;
            renderer.minParticleSize = 0f;
            renderer.maxParticleSize = MaxScreenSize;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.SetActiveVertexStreams(s_streams);
            group.Systems.Add(system);
        }

        private static void Tick()
        {
            if (Time.time < s_nextTick) return;
            s_nextTick = Time.time + TickSeconds;
            var cam = Utils.GetMainCamera();
            int running = 0, emitters = 0, particles = 0;
            for (int i = s_groups.Count - 1; i >= 0; i--)
            {
                var group = s_groups[i];
                if (group.Root == null)
                {
                    s_groups.RemoveAt(i);
                    continue;
                }
                if (!UpdateGroup(group, cam)) continue;
                running++;
                foreach (var system in group.Systems)
                {
                    if (system == null) continue;
                    emitters++;
                    particles += system.particleCount;
                }
            }
            if (Time.time < s_nextReport || (running == 0 && s_lastRunning == 0)) return;
            s_nextReport = Time.time + ReportSeconds;
            s_lastRunning = running;
            FiresLogger.LogInfo($"[FiresWaterfall] mist: {running} of {s_groups.Count} fall group(s) running within {FiresVoxelConfig.WaterfallMistRangeMetres:0} m, "
                + $"{emitters} emitter(s), {particles} particle(s).");
        }

        // Runs the group's emitters while mist is on and the camera is within range; returns whether they run.
        private static bool UpdateGroup(Group group, Camera cam)
        {
            bool running = group.Root.activeSelf;
            float range = FiresVoxelConfig.WaterfallMistRangeMetres + (running ? RangeHysteresis : 0f);
            bool want = FiresVoxelConfig.WaterfallMistOn && cam != null && group.Systems.Count > 0
                && (cam.transform.position - group.Centre).sqrMagnitude < range * range;
            if (want != running) group.Root.SetActive(want);
            return want && group.Root.activeInHierarchy;
        }

        private static Group Find(GameObject root)
        {
            foreach (var group in s_groups) if (group.Root == root) return group;
            return null;
        }

        private static int Signature(List<Site> sites)
        {
            unchecked
            {
                int hash = sites.Count;
                foreach (var site in sites)
                {
                    hash = hash * 31 + Mathf.RoundToInt(site.Foot.x * SignatureGrid);
                    hash = hash * 31 + Mathf.RoundToInt(site.Foot.y * SignatureGrid);
                    hash = hash * 31 + Mathf.RoundToInt(site.Foot.z * SignatureGrid);
                    hash = hash * 31 + Mathf.RoundToInt(site.Lip.y * SignatureGrid);
                    hash = hash * 31 + Mathf.RoundToInt(site.Width * SignatureGrid);
                    hash = hash * 31 + (site.Dissolves ? 1 : 0) + (site.Rapids ? 2 : 0);
                }
                return hash;
            }
        }

        private static void EnsureDriver()
        {
            if (s_driver != null) return;
            s_driver = new GameObject("FiresWaterfallMist");
            Object.DontDestroyOnLoad(s_driver);
            s_driver.AddComponent<Driver>();
        }
    }
}
