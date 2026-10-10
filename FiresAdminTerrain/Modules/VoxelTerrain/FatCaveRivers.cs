using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // Underground rivers, shaft lights and the cave ocean cull (FAT 0.2.801; Fire 10-06 via the lead).
    //
    // RIVERS ("rivers = carve + mesh", no springs): one roll per block of BlockZones x BlockZones zones ('Cave river chance').
    // A river runs from the first generated tunnel under a seeded point of the block, along a seeded heading, for 64-96 m,
    // its bed descending at most MaxSlope; along it the server carves a walkable tube (WalkRadius, WalkHeight over the bed) and
    // a channel (ChannelRadius) cut under the bed, then lays calm sheets down the channel at stepping levels
    // (WaterSheets.LayChannel: the same synced sheet mesh as cave pools, never a pour). Every sample keeps the cover rule
    // ('Underground cover' between the tube's top and the generated surface) and stays above Cave Min Y, else no river. It is
    // built once, in the cave content pass of the zone holding its start (seeded, so every peer agrees where it is).
    //
    // SHAFT LIGHTS: every light shaft of an underground location or a grotto gets a soft spot light down the shaft, coloured
    // and scaled by the sun (EnvMan's directional light; off while the sun is down), smoothstep-faded with distance.
    //
    // OCEAN CULL (render only): while the camera is in a cave, vanilla's ocean surfaces within CullRange are not drawn, so the
    // sea plane at y30 never shows through a gap under a cave roof. Swimming, buoyancy and water bodies are untouched (their
    // rules are elsewhere: Core's PlayerSwim roof check, FiresCaveCamera, WaterMeshCombiner).
    internal static class FatCaveRivers
    {
        private const string Section = "09 - World generation";
        internal static ConfigEntry<float> Chance;

        private const int BlockZones = 4;
        private const float ZoneSize = 64f, MinLength = 64f, LengthSpan = 32f, Step = 4f, MaxSlope = 0.06f;
        private const float WalkRadius = 3f, WalkHeight = 5f, ChannelRadius = 1.6f, ChannelDepth = 1.5f, SheetDrop = 0.25f;

        internal static void Bind(ConfigFile cfg)
        {
            Chance = cfg.Bind(Section, "Cave river chance", 0.15f,
                new ConfigDescription("Chance per block of 4 x 4 zones of an underground river: a carved channel with flowing-level sheets "
                    + "along a walkable tunnel, 64-96 m long. Needs the same rock cover as underground locations.", new AcceptableValueRange<float>(0f, 1f)));
        }

        internal struct Plan
        {
            public Vector2s StartZone;
            public List<Vector3> Bed;   // channel floor samples, every Step m
        }

        private static Vector2s BlockOf(Vector2s zone) => new Vector2s(Mathf.FloorToInt(zone.x / (float)BlockZones), Mathf.FloorToInt(zone.y / (float)BlockZones));

        internal static bool TryPlan(Vector2s block, out Plan plan)
        {
            plan = default;
            if (Chance == null || Chance.Value <= 0f || !FiresWorldGenBase.Active || !FiresVoxelConfig.GeneratesCaves || WorldGenerator.instance == null) return false;
            if (Hash01(block, 0x7A1) >= Chance.Value) return false;
            float span = BlockZones * ZoneSize;
            float ax = block.x * span + Hash01(block, 0x7A2) * span, az = block.y * span + Hash01(block, 0x7A3) * span;
            float surfaceA = FiresWorldGenBase.SampleFinalMeters(ax, az);
            if (!FatUndergroundLocations.TryTunnelFloor(ax, az, surfaceA, out float floorA)) return false;
            float heading = Hash01(block, 0x7A4) * Mathf.PI * 2f, length = MinLength + Hash01(block, 0x7A5) * LengthSpan;
            if (!TryPlanFrom(ax, az, floorA, heading, length, out plan)) return false;
            return true;
        }

        // 0.2.802: a river from a given start (a waterfall sinkhole's pond), heading and length; the same rules.
        // 0.2.856 (Fire 10-06 20:15 design: a big sinkhole swallows the river, falls into its cave pond and continues as a cave
        // river; Fire 10-07: big sinkholes may cut deeper than Cave Min Y): a sinkhole's out-river passes its own channel floor
        // limit (minFloor), down to just over the sea water table; every other cave river keeps Cave Min Y.
        internal static bool TryPlanFrom(float ax, float az, float floorA, float heading, float length, out Plan plan, float minFloor = float.NaN)
        {
            plan = default;
            var dir = new Vector2(Mathf.Cos(heading), Mathf.Sin(heading));
            float bx = ax + dir.x * length, bz = az + dir.y * length;
            float floorB = FatUndergroundLocations.TryTunnelFloor(bx, bz, FiresWorldGenBase.SampleFinalMeters(bx, bz), out float fb) ? fb : floorA;
            floorB = Mathf.Clamp(floorB, floorA - MaxSlope * length, floorA);   // flows downhill, gently, or level
            float cover = FatUndergroundLocations.Cover != null ? FatUndergroundLocations.Cover.Value : 8f;
            float minY = float.IsNaN(minFloor) ? FiresVoxelConfig.CaveMinY.Value + 1f : minFloor;
            if (!float.IsNaN(minFloor)) floorB = Mathf.Max(floorB, minY + ChannelRadius);   // a sinkhole river levels out at its floor limit
            var bed = new List<Vector3>();
            int n = Mathf.CeilToInt(length / Step);
            for (int i = 0; i <= n; i++)
            {
                float t = i / (float)n, x = Mathf.Lerp(ax, bx, t), z = Mathf.Lerp(az, bz, t), y = Mathf.Lerp(floorA, floorB, t);
                if (y - ChannelRadius < minY) return false;   // 0.2.816: the swept channel is a ChannelRadius-deep U
                if (FiresWorldGenBase.SampleFinalMeters(x, z) - (y + TubeLift + WalkRadius) < cover) return false;   // 0.2.816: the swept tube's top
                if (FiresWorldGenBase.IsWetAt(x, z) && y < 40f) return false;   // never under open water near the sea level
                bed.Add(new Vector3(x, y, z));
            }
            plan = new Plan { StartZone = ZoneSystem.GetZone(new Vector3(ax, 0f, az)), Bed = bed };
            return true;
        }

        // Server, from VoxelCaveContent's dressing of `zone`: build the river that starts in it, if its block has one.
        internal static bool Build(Vector2s zone)
        {
            if (!TryPlan(BlockOf(zone), out var plan) || plan.StartZone.x != zone.x || plan.StartZone.y != zone.y) return false;
            return BuildPlan(plan);
        }

        // 0.2.816 (Fire 10-06: "cave rivers are carving a bunch of round depressions strung together, not a continued channel
        // for the water to run through"): the tube and the channel were cylinders stamped every Step (4 m), so the bed and the
        // walls were a chain of round bowls and the water a chain of disks over them. Now the bed is resampled finely along
        // the plan and SWEPT: the channel is a sphere every ChannelSweep m centred on the bed line (a round-bottomed U, the
        // scallops ~1 cm deep), the walk tube two sphere rows either side of the axis (a flat floor beside the channel), and
        // the water is laid as densely overlapping sheets whose level is interpolated along the length (one smooth surface:
        // the placed-water renderer blends the overlaps), so it reads as one continuous ribbon.
        private const float ChannelSweep = 0.4f, TubeSweep = 0.75f, TubeSide = 1.2f, TubeLift = 3f, WaterSpacing = 0.3f;

        internal static bool BuildPlan(Plan plan)
        {
            var zone = plan.StartZone;
            var line = Resample(plan.Bed, ChannelSweep);
            string first = null;
            float travelled = 0f, nextTube = 0f;
            for (int i = 0; i < line.Count; i++)
            {
                var p = line[i];
                if (i > 0) travelled += Vector3.Distance(new Vector3(line[i - 1].x, 0f, line[i - 1].z), new Vector3(p.x, 0f, p.z));
                if (travelled >= nextTube)
                {
                    nextTube += TubeSweep;
                    var dir = i + 1 < line.Count ? line[i + 1] - p : p - line[i - 1];
                    var side = new Vector3(-dir.z, 0f, dir.x).normalized * TubeSide;
                    string walk = VoxelWorld.ApplyBrush(new Vector3(p.x, p.y + TubeLift, p.z) + side, WalkRadius, WalkRadius * 2f, VoxelBrushShape.Sphere, VoxelBrushMode.Carve);
                    VoxelWorld.ApplyBrush(new Vector3(p.x, p.y + TubeLift, p.z) - side, WalkRadius, WalkRadius * 2f, VoxelBrushShape.Sphere, VoxelBrushMode.Carve);
                    first ??= walk;
                }
                VoxelWorld.ApplyBrush(p, ChannelRadius, ChannelRadius * 2f, VoxelBrushShape.Sphere, VoxelBrushMode.Carve);
            }
            if (first != null && first.StartsWith("Refused")) { FiresLogger.LogDebug($"[CaveRiver] ({zone.x},{zone.y}) skipped: {first}"); return false; }
            var spine = new List<Vector3>(line.Count);
            var levels = new List<float>(line.Count);
            foreach (var p in line) { spine.Add(new Vector3(p.x, p.y - ChannelRadius, p.z)); levels.Add(p.y - SheetDrop); }
            int sheets = VerdantsAscent.Water.Tools.WaterSheets.LayChannel(spine, levels, ChannelRadius, "cave river", WaterSpacing);
            Vector3 a = plan.Bed[0], b = plan.Bed[plan.Bed.Count - 1];
            FiresLogger.LogDebug(string.Format(CultureInfo.InvariantCulture,
                "[CaveRiver] from ({0:0},{1:0},{2:0}) to ({3:0},{4:0},{5:0}): {6:0} m, bed drop {7:0.0} m, swept over {8} point(s), {9} sheet(s) laid",
                a.x, a.y, a.z, b.x, b.y, b.z, Vector3.Distance(new Vector3(a.x, 0, a.z), new Vector3(b.x, 0, b.z)), a.y - b.y, line.Count, sheets));
            return true;
        }

        // The plan's bed (a point every Step m) resampled every `step` m, linear in x, y and z.
        private static List<Vector3> Resample(List<Vector3> bed, float step)
        {
            var line = new List<Vector3>();
            if (bed == null || bed.Count == 0) return line;
            line.Add(bed[0]);
            for (int i = 1; i < bed.Count; i++)
            {
                Vector3 a = bed[i - 1], b = bed[i];
                int parts = Mathf.Max(1, Mathf.CeilToInt(new Vector2(b.x - a.x, b.z - a.z).magnitude / step));
                for (int k = 1; k <= parts; k++) line.Add(Vector3.Lerp(a, b, k / (float)parts));
            }
            return line;
        }

        // ── client: shaft lights + ocean cull (from VoxelCaveEnv's poll) ───────────────────────────────────────────
        private const float ShaftLightNear = 20f, ShaftLightFar = 60f, ShaftLightIntensity = 1.4f, ShaftSpot = 60f, CullRange = 160f, OceanBand = 6f;
        private static readonly Dictionary<Vector2Int, Light> s_shaftLights = new Dictionary<Vector2Int, Light>();
        private static readonly List<MeshRenderer> s_culled = new List<MeshRenderer>();

        internal static void Tick(Vector3 player, bool cameraInCave)
        {
            ShaftLights(player);
            OceanCull(player, cameraInCave);
        }

        private static void ShaftLights(Vector3 player)
        {
            var sun = EnvMan.instance != null ? EnvMan.instance.m_dirLight : null;
            float sunScale = sun != null && sun.enabled && EnvMan.IsDaylight() ? Mathf.Clamp01(sun.intensity) : 0f;
            foreach (var dome in FatUndergroundLocations.Domes)
            {
                if (!dome.Shaft) continue;
                var key = new Vector2Int(Mathf.RoundToInt(dome.Centre.x), Mathf.RoundToInt(dome.Centre.z));
                if (!s_shaftLights.TryGetValue(key, out var light) || light == null)
                {
                    var go = new GameObject($"FatShaftLight_{key.x}_{key.y}");
                    go.transform.position = new Vector3(dome.Centre.x, dome.Top - 0.5f, dome.Centre.z);
                    go.transform.rotation = Quaternion.LookRotation(Vector3.down);
                    light = go.AddComponent<Light>();
                    // 0.2.814 (Fire 10-06: a hard vertical pink/blue split inside the sinkhole chamber): a POINT light, which fades
                    // with distance; the spot cone's edge (built-in pipeline) drew that split across the chamber walls.
                    light.type = LightType.Point;
                    light.range = dome.Top - dome.Centre.y + 6f;
                    light.shadows = LightShadows.None;
                    s_shaftLights[key] = light;
                }
                float d = Vector3.Distance(player, light.transform.position);
                float t = Mathf.Clamp01((ShaftLightFar - d) / (ShaftLightFar - ShaftLightNear));
                light.color = sun != null ? sun.color : new Color(1f, 0.95f, 0.85f);
                light.intensity = ShaftLightIntensity * sunScale * t * t * (3f - 2f * t);
                light.enabled = light.intensity > 0.01f;
            }
        }

        private static void OceanCull(Vector3 player, bool cameraInCave)
        {
            if (!cameraInCave)
            {
                if (s_culled.Count == 0) return;
                foreach (var r in s_culled) if (r != null) r.enabled = true;
                FiresLogger.LogDebug($"[CaveRiver] ocean surface(s) drawn again: {s_culled.Count} (camera left the caves)");
                s_culled.Clear();
                return;
            }
            foreach (var volume in WaterVolume.Instances)
            {
                var r = volume != null ? volume.m_waterSurface : null;
                if (r == null || !r.enabled || s_culled.Contains(r)) continue;
                // 0.2.810 (Fire 10-06, SinkTestGen: two stacked water surfaces on a river at y 70): only the SEA. FAT's own inland
                // water (WaterMeshCombiner volumes, whose grid renderers FAT keeps hidden under its sheets) and any surface away
                // from sea level were culled too, and re-enabled on leaving the cave, which drew a second river surface.
                if (volume.GetComponentInParent<VerdantsAscent.Water.Core.WaterMeshCombiner>() != null) continue;
                float seaY = ZoneSystem.instance != null ? ZoneSystem.instance.m_waterLevel : 30f;
                if (Mathf.Abs(r.transform.position.y - seaY) > OceanBand) continue;
                var at = volume.transform.position;
                if (new Vector2(at.x - player.x, at.z - player.z).magnitude > CullRange) continue;
                r.enabled = false;
                s_culled.Add(r);
            }
        }

        private static float Hash01(Vector2s block, int salt)
        {
            unchecked
            {
                int seed = WorldGenerator.instance != null ? WorldGenerator.instance.GetSeed() : 0;
                uint h = (uint)(seed * 73856093 ^ block.x * 19349663 ^ block.y * 83492791 ^ salt * 2654435761);
                h ^= h >> 13; h *= 0x5bd1e995; h ^= h >> 15;
                return (h & 0xFFFFFF) / 16777216f;
            }
        }
    }
}
