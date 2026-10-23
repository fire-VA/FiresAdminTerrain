using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Rendering;
using VerdantsAscent.Terrain.Voxel;

namespace VerdantsAscent.Water.Core
{
    // Clear alpine lake look (the probe is experimental and off by default). The inland
    // shader (Custom/FiresInlandWater) reflects unity_SpecCube0, which is normally just the sky. One realtime ReflectionProbe is kept at the
    // nearest still inland body (lake or pond, by FAT's own water classes) within Range, box-projected to that body, so the water mirrors
    // the real trees, shore and mountains around it. It re-renders every RefreshSeconds (time-sliced face by face), at Resolution px.
    // Client only; nothing on a headless server. Away from any lake it switches itself off. "Mirror" (a planar reflection camera) is
    // opt-in and not built yet: choosing it uses the probe for now.
    internal sealed class InlandLakeProbe : MonoBehaviour
    {
        internal static ConfigEntry<string> Mode;
        internal static ConfigEntry<float> Range;
        internal static ConfigEntry<int> Resolution;
        internal static ConfigEntry<float> RefreshSeconds;
        internal static ConfigEntry<bool> Experimental;

        private const string Section = "Ocean - Inland look";
        private const float SearchStep = 16f, ExtentStep = 8f, MaxHalfExtent = 120f, ProbeLift = 1.5f, ProbeHeight = 60f;

        private ReflectionProbe _probe;
        private float _nextSearch, _nextRender;
        private Vector2 _lakeCentre = new Vector2(float.NaN, float.NaN);

        internal static void Bind(ConfigFile cfg)
        {
            Mode = cfg.Bind(Section, "Lake reflection", "Sky",   // Sky until the probe is fixed (white chunk blocks under rain/fog)
                new ConfigDescription("What lakes and ponds reflect. Sky = only the sky (as before). Probe = the trees, shore and mountains around the "
                    + "nearest lake (a soft mirror, cheap). Mirror = an exact mirror (costly; not built yet - uses Probe).",
                    new AcceptableValueList<string>("Sky", "Probe", "Mirror")));
            Range = cfg.Bind(Section, "Lake reflection range (m)", 150f,
                new ConfigDescription("How near a lake must be for its reflection to be rendered.", new AcceptableValueRange<float>(30f, 400f)));
            Resolution = cfg.Bind(Section, "Lake reflection resolution", 128,
                new ConfigDescription("Reflection texture size per face. Higher = sharper and slower.", new AcceptableValueList<int>(64, 128, 256, 512)));
            RefreshSeconds = cfg.Bind(Section, "Lake reflection refresh (s)", 3f,
                new ConfigDescription("How often the lake's reflection is re-rendered (time of day, moving trees).", new AcceptableValueRange<float>(0.5f, 30f)));
            // The probe made the water look white, so it stays off unless this NEW key is set, so a
            // config saved with "Probe" falls back to the sky without a migration. Lake reflection = Probe/Mirror needs this too.
            Experimental = cfg.Bind(Section, "Lake reflection probe (experimental)", false,
                "Allow the lake reflection probe (Lake reflection = Probe). Off: lakes reflect the sky, whatever Lake reflection says.");
        }

        internal static void EnsureAttached()
        {
            if (Application.isBatchMode || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return;
            var root = global::VerdantsAscent.FiresAdminTerrain.RootObject;
            if (root == null || root.GetComponent<InlandLakeProbe>() != null) return;
            root.AddComponent<InlandLakeProbe>();
        }

        private void Update()
        {
            bool on = Mode != null && Mode.Value != "Sky" && Experimental != null && Experimental.Value && FiresWorldGenBase.Active;
            var player = Player.m_localPlayer;
            if (!on || player == null) { SetActive(false); return; }
            if (Time.time >= _nextSearch)
            {
                _nextSearch = Time.time + 2f;
                Search(player.transform.position);
            }
            if (_probe != null && _probe.enabled && Time.time >= _nextRender)
            {
                _nextRender = Time.time + (RefreshSeconds != null ? RefreshSeconds.Value : 3f);
                _probe.RenderProbe();
            }
        }

        private void Search(Vector3 p)
        {
            float range = Range != null ? Range.Value : 150f;
            float bestD = float.MaxValue;
            Vector2 best = Vector2.zero;
            for (float dz = -range; dz <= range; dz += SearchStep)
                for (float dx = -range; dx <= range; dx += SearchStep)
                {
                    float d = dx * dx + dz * dz;
                    if (d > range * range || d >= bestD) continue;
                    byte c = FiresWorldGenBase.WaterClassAt(p.x + dx, p.z + dz);
                    if (c != FiresWorldGenBase.WC_LAKE && c != FiresWorldGenBase.WC_POND) continue;
                    bestD = d; best = new Vector2(p.x + dx, p.z + dz);
                }
            if (bestD == float.MaxValue) { SetActive(false); return; }
            if (!FiresWorldGenBase.TryGetWaterSurfaceGen(best.x, best.y, out float level, out _)) { SetActive(false); return; }
            // the body's rough box: walk out from the found cell while it stays still water of the same kind
            byte cls = FiresWorldGenBase.WaterClassAt(best.x, best.y);
            float minX = Reach(best, cls, -1, 0), maxX = Reach(best, cls, 1, 0), minZ = Reach(best, cls, 0, -1), maxZ = Reach(best, cls, 0, 1);
            var centre = new Vector2((minX + maxX) * 0.5f, (minZ + maxZ) * 0.5f);
            var size = new Vector3(Mathf.Max(20f, maxX - minX), ProbeHeight, Mathf.Max(20f, maxZ - minZ));
            EnsureProbe();
            _probe.transform.position = new Vector3(centre.x, level + ProbeLift, centre.y);
            _probe.size = size;
            _probe.center = new Vector3(0f, ProbeHeight * 0.5f - ProbeLift - 2f, 0f);
            _probe.resolution = Resolution != null ? Resolution.Value : 128;
            bool moved = float.IsNaN(_lakeCentre.x) || (_lakeCentre - centre).sqrMagnitude > 4f;
            _lakeCentre = centre;
            if (!_probe.enabled || moved)
            {
                _probe.enabled = true;
                _nextRender = 0f;
                FiresLogger.LogDebug($"[InlandWater] lake reflection on the {(cls == FiresWorldGenBase.WC_LAKE ? "lake" : "pond")} at ({centre.x:0},{centre.y:0}) "
                    + $"level {level:0.0} m, box {size.x:0}x{size.z:0} m, {_probe.resolution} px");
            }
        }

        private static float Reach(Vector2 from, byte cls, int sx, int sz)
        {
            float t = 0f;
            while (t < MaxHalfExtent && FiresWorldGenBase.WaterClassAt(from.x + sx * (t + ExtentStep), from.y + sz * (t + ExtentStep)) == cls) t += ExtentStep;
            return sx != 0 ? from.x + sx * t : from.y + sz * t;
        }

        private void EnsureProbe()
        {
            if (_probe != null) return;
            var go = new GameObject("FiresInlandLakeProbe");
            go.transform.SetParent(transform, false);
            _probe = go.AddComponent<ReflectionProbe>();
            _probe.mode = ReflectionProbeMode.Realtime;
            _probe.refreshMode = ReflectionProbeRefreshMode.ViaScripting;
            _probe.timeSlicingMode = ReflectionProbeTimeSlicingMode.IndividualFaces;
            _probe.boxProjection = true;
            _probe.importance = 10;
            _probe.blendDistance = 20f;   // wide blend, no hard chunk edges
            _probe.intensity = 0.6f;     // capped (a fogged capture is near white)
            _probe.hdr = true;
            _probe.clearFlags = ReflectionProbeClearFlags.Skybox;
            _probe.nearClipPlane = 0.5f;
            _probe.farClipPlane = 1500f;
            _probe.enabled = false;
        }

        private void SetActive(bool on)
        {
            if (_probe != null && _probe.enabled != on)
            {
                _probe.enabled = on;
                if (!on) { _lakeCentre = new Vector2(float.NaN, float.NaN); FiresLogger.LogDebug("[InlandWater] lake reflection off (no lake or pond in range)"); }
            }
        }

        private void OnDestroy()
        {
            if (_probe != null) Destroy(_probe.gameObject);
        }
    }
}
