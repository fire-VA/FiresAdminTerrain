using UnityEngine;
using VerdantsAscent.Terrain.Persistence;

namespace VerdantsAscent.Terrain.Voxel
{
    // A LOCATION'S DEVGROUND MUST NOT REACH THE HEIGHTMAP ON VOXEL TERRAIN.
    //
    // Vanilla levels the ground under a location with TerrainOps carried by the location prefab
    // (the "devground" plate and its friends). TerrainComp.LevelTerrain writes, per vertex inside
    // the circle:
    //
    //     delta = (op.y + m_levelOffset) - currentHeight       clamped to +/-8 m
    //
    // On a heightmap world that is exactly right. On voxel terrain it is the same defect the sky
    // island guard already refuses: the delta is measured against the GENERATED GROUND height, so
    // placing a location on voxel terrain drags a solid sheet up from the original ground - through
    // caves, through anything hollow underneath. Fire: "it ruins caves and does weird things".
    //
    // The answer is NOT to drop the levelling. A location needs flat ground or it sits in a hillside.
    // So this keeps vanilla's decision and changes only where it lands: the SAME circle, the SAME
    // target level, done with voxel brushes (LevelWithVoxels: fill up to the level, cut down to it) that
    // edit the voxel field in place instead of raising a heightmap vertex. Until FAT 0.2.651 this went
    // to the 2D tile brush, which on a voxel world only drew a floating plate over the ground.
    //
    // ONLY location-owned ops. GetComponentInParent<Location>() is the test: a player's own hoe
    // stroke has no Location above it and is left completely alone, so nothing about hand-editing
    // changes here.
    internal static class FiresVoxelLocationLevelGuard
    {
        private const float MinOpRadius = 1.5f;

        internal static bool AllowTerrainOp(TerrainOp op)
        {
            if (op == null || TerrainOp.m_forceDisableTerrainOps) return true;

            // Something earlier in the chain already claimed it.
            if (Zones.ZoneModeRefusals.WasCancelled(op) || TileTerrainOpBlocker.WasRoutedToTile(op)) return false;

            // Only where the voxel field owns the ground. On a heightmap world vanilla is correct.
            if (!FiresVoxelConfig.WrapsAllTerrain) return true;

            var settings = op.m_settings;
            if (settings == null) return true;
            // 0.2.799: an underground location's dome floor is already level (FatUndergroundLocations); its level op would
            // be measured from the SURFACE and cut a hole down to it. Dropped.
            // 0.2.830 (Fire 10-07: "strip the vanilla terrain modifier and dev grounds"): EVERY op of an underground location
            // (raise and paint too), so nothing it carries touches the surface over it.
            bool isLocationOp = op.GetComponentInParent<Location>() != null;
            if ((isLocationOp || FatUndergroundLocations.t_spawningUnderground) && FatUndergroundLocations.IsUnderground(op.transform.position))
            {
                FatUndergroundLocations.Blocked("TerrainOp", op.name, op.transform.position);   // 0.2.832: one line per item
                Object.Destroy(op.gameObject);
                return false;
            }
            if (!settings.m_level && !settings.m_smooth) return true;   // raise/paint are not the sheet problem

            // A location's op, not a player's brush.
            if (!isLocationOp) return true;

            Vector3 position = op.transform.position;
            // Vanilla lifts level and smooth by m_levelOffset before using the point; matching that
            // is the whole reason this produces the same flat ground vanilla would have produced.
            Vector3 levelPosition = position + Vector3.up * settings.m_levelOffset;

            string routed = "nothing";
            if (settings.m_level)
            {
                float radius = Mathf.Max(settings.m_levelRadius, MinOpRadius);
                routed = LevelWithVoxels(levelPosition, radius);
            }
            // Smoothing has no voxel brush; the level alone gives the location its flat ground.
            if (settings.m_smooth) routed += $", smooth r={Mathf.Max(settings.m_smoothRadius, MinOpRadius):0.#} skipped (no voxel smooth)";

            Report(op, position, routed);
            Object.Destroy(op.gameObject);
            return false;
        }

        private static bool s_reported;

        // Vanilla's level with voxel brushes, inside the same circle: the generated ground under it is filled up to the target y
        // and cut down to it, each by at most vanilla's MaxLevelDelta. The fill starts at the LOWEST generated ground in the
        // circle, not below it, so a cave under the location is left alone (the reason this guard exists). FAT 0.2.651: this
        // used to go to the 2D tile layer, which drew as a floating plate (Fire, 2026-09-29: tools are voxel only).
        private const float MaxLevelDelta = 8f;
        private const float LevelSampleStep = 2f;
        private const float LevelMinBand = 0.05f;
        private const float CarveHeadroom = 1f;

        private static string LevelWithVoxels(Vector3 level, float radius)
        {
            var generator = WorldGenerator.instance;
            if (generator == null) return "level skipped: no world generator";
            float low = float.MaxValue, high = float.MinValue;
            for (float dz = -radius; dz <= radius; dz += LevelSampleStep)
                for (float dx = -radius; dx <= radius; dx += LevelSampleStep)
                {
                    if (dx * dx + dz * dz > radius * radius) continue;
                    float h = generator.GetHeight(level.x + dx, level.z + dz);
                    low = Mathf.Min(low, h);
                    high = Mathf.Max(high, h);
                }
            if (low == float.MaxValue) { low = high = generator.GetHeight(level.x, level.z); }
            string done = $"level r={radius:0.#} y={level.y:0.#} over ground {low:0.#}-{high:0.#}";
            if (low < level.y - LevelMinBand)
            {
                float bottom = Mathf.Max(low, level.y - MaxLevelDelta);
                string fill = VoxelWorld.ApplyBrush(new Vector3(level.x, (bottom + level.y) * 0.5f, level.z), radius, level.y - bottom,
                    VoxelBrushShape.Cylinder, VoxelBrushMode.Fill);
                done += $", filled {bottom:0.#}-{level.y:0.#} ({fill})";
            }
            if (high > level.y + LevelMinBand)
            {
                float top = Mathf.Min(high, level.y + MaxLevelDelta) + CarveHeadroom;
                string carve = VoxelWorld.ApplyBrush(new Vector3(level.x, (level.y + top) * 0.5f, level.z), radius, top - level.y,
                    VoxelBrushShape.Cylinder, VoxelBrushMode.Carve);
                done += $", cut {level.y:0.#}-{top:0.#} ({carve})";
            }
            return done;
        }

        // Loud once per session, then verbose: a location can carry dozens of these and a placed
        // chunk would otherwise write a page of identical lines.
        private static void Report(TerrainOp op, Vector3 position, string routed)
        {
            string line = $"[FiresVoxel] rerouted a location's terrain op at ({position.x:0},{position.y:0},{position.z:0}) "
                + $"to our brush ({routed}). On voxel terrain vanilla would have levelled against the GENERATED ground "
                + "height and pulled a solid sheet up from it, through any cave below.";
            if (s_reported) { FiresLogger.LogVerbose(line); return; }
            s_reported = true;
            FiresLogger.LogWarning(line);
        }
    }
}
