using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // 0.2.828 (Fire 10-07, cave clutter by light: "draw the clutter from the cave's chosen biome, maybe at half the scale and
    // quarter density because of the low light, and more dense at the mouth openings and areas where there's an actual sun shaft
    // or sinkhole nearby. The darker areas, or longer tunnels deeper and further from the light, get the mushroom placements").
    // How lit a cave point is, 0 (dark) .. 1 (at a light source), client side, main thread, cached per 8 m cell:
    //   * big sinkhole shafts (FatSinkFallCuts) under their bed, and underground-location domes with a light shaft: 1 inside the
    //     shaft, fading to 0 at 'Light reach' past it;
    //   * cave mouths: a ray straight up that reaches the sky = 1; else 8 rays slanted 45 deg up (MouthRay m): one that gets out
    //     gives 1 - 0.8 * (distance to where it leaves the ground) / MouthRay.
    // Read by the cave grass (ClutterSpread: density and scale).
    internal static class CaveLight
    {
        private const string Section = "VoxelTerrain Cave Clutter";
        private const float Cell = 8f, CacheSeconds = 60f, MouthRay = 40f, MouthStep = 4f, RayUp = 120f, EyeLift = 1.5f, BedSlack = 2f;
        private const int MouthRays = 8;

        private static ConfigEntry<float> s_darkDensity, s_darkScale, s_reach, s_cavernBase, s_cavernMinRadius;
        private static readonly Dictionary<Vector3Int, (float light, float at)> s_cache = new Dictionary<Vector3Int, (float, float)>();
        private static int s_mask = -1;

        internal static float DarkDensity => s_darkDensity?.Value ?? 0.25f;
        internal static float DarkScale => s_darkScale?.Value ?? 0.5f;

        internal static void Bind(ConfigFile cfg)
        {
            s_darkDensity = cfg.Bind(Section, "Dark density", 0.25f, new ConfigDescription(
                "Share of the cave biome's grass and plants kept where no light reaches (1 = as dense as the surface). It rises to 1 "
                + "toward cave mouths, sun shafts and sinkholes.", new AcceptableValueRange<float>(0f, 1f)));
            s_darkScale = cfg.Bind(Section, "Dark scale", 0.5f, new ConfigDescription(
                "Size of the cave biome's grass and plants where no light reaches (1 = surface size); rises to 1 toward the light.",
                new AcceptableValueRange<float>(0.1f, 1f)));
            s_reach = cfg.Bind(Section, "Light reach", 30f, new ConfigDescription(
                "How far (m) a sun shaft's or sinkhole's light reaches into the cave around it.", new AcceptableValueRange<float>(5f, 100f)));
            s_cavernBase = cfg.Bind(Section, "Cavern base light", 0.6f, new ConfigDescription(
                "Clutter light (0 dark .. 1 lit) every big chamber has whatever its openings (underground-location domes, grottos, "
                + "sinkhole chambers): its grass grows at that share of the way from 'Dark density' / 'Dark scale' to the surface's. "
                + "Clutter only; the chamber's lighting and environment are not changed.", new AcceptableValueRange<float>(0f, 1f)));
            s_cavernMinRadius = cfg.Bind(Section, "Cavern min radius", 10f, new ConfigDescription(
                "Least dome radius (m) that counts as a big chamber for 'Cavern base light'.", new AcceptableValueRange<float>(4f, 60f)));
            s_reach.SettingChanged += (_, __) => s_cache.Clear();
            s_cavernBase.SettingChanged += (_, __) => s_cache.Clear();
            s_cavernMinRadius.SettingChanged += (_, __) => s_cache.Clear();
        }

        internal static void Clear() => s_cache.Clear();

        internal static float At(Vector3 p)
        {
            var key = new Vector3Int(Mathf.FloorToInt(p.x / Cell), Mathf.FloorToInt(p.y / Cell), Mathf.FloorToInt(p.z / Cell));
            float now = Time.time;
            if (s_cache.TryGetValue(key, out var hit) && now - hit.at < CacheSeconds) return hit.light;
            var centre = new Vector3((key.x + 0.5f) * Cell, p.y, (key.z + 0.5f) * Cell);
            float light = Compute(centre);
            s_cache[key] = (light, now);
            return light;
        }

        // 0.2.829: the same light without colliders (a dedicated server has no voxel colliders), for the server's cave dressing:
        // the sources as in Compute, and the mouth rays marched through the generated cave field (VoxelCaveNoise.CarveSdf): a ray
        // that leaves the ground before it meets rock is an opening. Deterministic: every peer computes the same value.
        internal static float Static(Vector3 p)
        {
            float light = Sources(p);
            if (light >= 1f) return 1f;
            var eye = p + Vector3.up * EyeLift;
            for (int k = 0; k <= MouthRays; k++)
            {
                Vector3 dir;
                if (k == MouthRays) dir = Vector3.up;
                else { float a = k * Mathf.PI * 2f / MouthRays; dir = new Vector3(Mathf.Cos(a), 1f, Mathf.Sin(a)).normalized; }
                for (float s = MouthStep * 0.5f; s <= MouthRay; s += MouthStep * 0.5f)
                {
                    var q = eye + dir * s;
                    float ground = FiresWorldGenBase.SampleFinalMeters(q.x, q.z);
                    if (q.y > ground) { light = Mathf.Max(light, 1f - 0.8f * s / MouthRay); break; }
                    if (VoxelCaveNoise.CarveSdf(q.x, q.y, q.z, ground - q.y) <= 0f) break;   // rock
                }
                if (light >= 1f) break;
            }
            return light;
        }

        private static float Compute(Vector3 p)
        {
            float light = Sources(p);
            if (light >= 1f) return 1f;
            return Rays(p, light);
        }

        private static float Sources(Vector3 p)
        {
            float reach = s_reach?.Value ?? 30f, light = 0f;
            for (int i = 0; FatSinkFallCuts.TryShaft(i, out Vector2 c, out float r, out float bed); i++)
            {
                if (float.IsNaN(bed) || p.y > bed + BedSlack) continue;
                float d = new Vector2(p.x - c.x, p.z - c.y).magnitude;
                light = Mathf.Max(light, 1f - Mathf.Clamp01((d - r) / reach));
            }
            float cavern = s_cavernBase?.Value ?? 0.6f, cavernMin = s_cavernMinRadius?.Value ?? 10f;
            foreach (var dome in FatUndergroundLocations.Domes)
            {
                if (p.y > dome.Top + BedSlack) continue;
                float d = new Vector2(p.x - dome.Centre.x, p.z - dome.Centre.z).magnitude;
                if (dome.Shaft) light = Mathf.Max(light, 1f - Mathf.Clamp01(d / reach));
                // 0.2.838 (Fire 10-07, Dolmen01's cavern: "the light level is good in here, but it needs grasses"): a big chamber
                // counts as lit FOR CLUTTER (grass density / size, mushroom odds), never for the light or the environment.
                if (dome.Radius >= cavernMin && d <= dome.Radius && p.y >= dome.Centre.y - BedSlack) light = Mathf.Max(light, cavern);
            }
            return light;
        }

        private static float Rays(Vector3 p, float light)
        {
            if (s_mask < 0) s_mask = LayerMask.GetMask("terrain");
            var eye = p + Vector3.up * EyeLift;
            if (!Physics.Raycast(eye, Vector3.up, RayUp, s_mask, QueryTriggerInteraction.Ignore)) return 1f;
            for (int k = 0; k < MouthRays; k++)
            {
                float a = k * Mathf.PI * 2f / MouthRays;
                var dir = new Vector3(Mathf.Cos(a), 1f, Mathf.Sin(a)).normalized;
                if (Physics.Raycast(eye, dir, MouthRay, s_mask, QueryTriggerInteraction.Ignore)) continue;
                for (float s = MouthStep; s <= MouthRay; s += MouthStep)
                {
                    var q = eye + dir * s;
                    if (q.y <= FiresWorldGenBase.SampleFinalMeters(q.x, q.z)) continue;
                    light = Mathf.Max(light, 1f - 0.8f * s / MouthRay);
                    break;
                }
            }
            return light;
        }
    }
}
