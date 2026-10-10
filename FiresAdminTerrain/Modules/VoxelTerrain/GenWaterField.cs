using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // ONE place every generated-water consumer asks its questions, whatever
    // built the water: the Fires generator on Fires* worlds, or the hydrology
    // solve + channel network on a vanilla world (Tools/FAT_RIVER_CARVE_SPEC.md).
    //
    // Consumers — the pour placement (VoxelGenLiquid), the wet-shore paint
    // (VoxelZoneStreamer.PaintWet01) and the chunk water store — must not
    // branch on the source themselves; that is how the vanilla side ended up
    // with carved channels, no pours and no shore sand.
    internal static class GenWaterField
    {
        // Our field never claims the ocean plane: at or below this the vanilla
        // sea owns the column (VoxelRiverNetwork keeps the bed above it too).
        private const float SeaSheetGate = 30.3f;

        private static readonly Dictionary<long, byte> s_bioMemo = new Dictionary<long, byte>(4096);

        public static bool FromGenerator => FiresWorldGenBase.Active;

        public static bool Ready => FiresWorldGenBase.Active
            || (FiresVoxelConfig.CarvesRiverBeds && VoxelHydrology.Ready);

        // The field is QUERYABLE — stricter than Ready on a Fires world, where
        // the reconciled surface arrays load after the generator goes active.
        // The paint stamp must never memoize a heightmap before this is true or
        // that map keeps a permanent unpainted 64m rectangle.
        public static bool SurfaceReady => FiresWorldGenBase.Active
            ? FiresWorldGenBase.WaterSurfaceReady
            : (FiresVoxelConfig.CarvesRiverBeds && VoxelHydrology.Ready);

        public static void Clear()
        {
            lock (s_bioMemo) s_bioMemo.Clear();
        }

        // Which zones must become voxel for generated water to exist at all: a
        // zone a channel runs through, or one a pond bowl reaches. Both answer
        // here so the streamer never has to know there are two sources.
        public static bool TouchesZone(Vector2s zone)
        {
            var shift = Zones.ZoneModeRegistry.ShiftOf(zone);
            if (shift.Lift > 0f) return true;
            if (!Zones.ZoneModeRegistry.TryGetSource(zone, out var source)) return SourceTouches(zone);
            if (!shift.Slides) return SourceTouches(source);
            // A location seed's source square is slid to centre its location, so it spans up to four source zones.
            var centre = ZoneSystem.GetZonePos(source) + new Vector3(shift.Dx, 0f, shift.Dz);
            float reach = ZoneSystem.c_ZoneSizeHalf - SourceCornerInset;
            for (int corner = 0; corner < SourceCorners; corner++)
            {
                var at = centre + new Vector3((corner & 1) == 0 ? -reach : reach, 0f, (corner & 2) == 0 ? -reach : reach);
                if (SourceTouches(ZoneSystem.GetZone(at))) return true;
            }
            return false;
        }

        private const int SourceCorners = 4;
        private const float SourceCornerInset = 0.1f;

        private static bool SourceTouches(Vector2s zone) => VoxelRiverNetwork.TouchesZone(zone) || VoxelLakeBasins.TouchesZone(zone);

        // Server-side placement asks this before choosing ground: true once this machine knows the world's generated
        // water. A Fires world knows it with its generator and a world with none planned knows it at once; otherwise the
        // hydrology solve must be ready, and asking starts it on a server that would not run it for itself (a headless
        // server holding no voxel data).
        public static bool EnsureWaterKnown()
        {
            if (FiresWorldGenBase.Active) return true;
            if (!FiresVoxelConfig.CarvesRiverBeds && !FiresVoxelConfig.PlacesHydrologyWater) return true;
            if (VoxelHydrology.Ready) return true;
            VoxelHydrology.EnsureBuilding();
            VoxelHydrology.FlushReport();
            return false;
        }

        public static bool IsWetAt(float wx, float wz)
        {
            if (FiresWorldGenBase.Active) return FiresWorldGenBase.IsWetAt(wx, wz);
            return TryWaterSurfaceSmooth(wx, wz, out _, out float depth) && depth >= 0.05f;
        }

        public static bool TryWaterSurfaceSmooth(float wx, float wz, out float level, out float depth)
        {
            if (FiresWorldGenBase.Active) return FiresWorldGenBase.TryGetWaterSurfaceSmooth(wx, wz, out level, out depth);
            // A chunk raised with other land (ZoneModeLift) carries its source's water up with it: channels and bowls
            // rise by the lift, and the ground its source had under the sea holds the lifted sea.
            bool sea = Zones.ZoneModeGate.TryLiftedSea(wx, wz, out float seaLevel, out float seaFloor);
            Zones.ZoneModeGate.TranslateToSource(ref wx, ref wz, out float lift);
            if (TryHydrologySurface(wx, wz, out level, out depth))
            {
                level += lift;
                if (!sea || level >= seaLevel) return true;
            }
            if (!sea)
            {
                level = float.MinValue;
                depth = 0f;
                return false;
            }
            level = seaLevel;
            depth = seaLevel - seaFloor;
            return depth > 0f;
        }

        // The channels and still water of the hydrology solve at a point already moved to its source.
        private static bool TryHydrologySurface(float wx, float wz, out float level, out float depth)
        {
            level = float.MinValue;
            depth = 0f;
            if (!VoxelHydrology.Ready) return false;

            // Channel and still water both report here; the higher surface owns
            // the column, which is also how it composes with the sea plane.
            //
            // DEPTH IS MEASURED TO THE FLOOR THAT ACTUALLY HOLDS THE WATER: a
            // channel's floor is its carved bed, not the original ground. Using
            // the uncarved ground made every channel report a NEGATIVE depth
            // (the surface sits a freeboard below the old ground), so the pour
            // placement saw "not wet" and placed almost nothing.
            float floor = float.MaxValue;
            if (VoxelRiverNetwork.TryWetInfo(wx, wz, 0.5f, out float chan, out _, out _, out float bed))
            {
                level = chan;
                floor = bed;
            }
            // Ponds and lakes report their CARVED bowl: the level the basin was
            // cut for, and the depth that bowl actually provides here. Reporting
            // the raw 20m fill level against untouched ground is what floated
            // sheets over the grass.
            if (VoxelLakeBasins.TryWaterAt(wx, wz, out float lakeLvl, out float lakeDepth) && lakeLvl > level)
            {
                level = lakeLvl;
                floor = lakeLvl - lakeDepth;
            }
            if (level <= SeaSheetGate || floor == float.MaxValue) { level = float.MinValue; return false; }

            depth = level - floor;
            return depth > 0f;
        }

        public static bool TryGround(float wx, float wz, out float g)
        {
            if (FiresWorldGenBase.Active) return FiresWorldGenBase.TryGetGenGround(wx, wz, out g);
            Zones.ZoneModeGate.TranslateToSource(ref wx, ref wz, out float lift);
            if (!VoxelHydrology.TryGroundAt(wx, wz, out g)) return false;
            g += lift;
            return true;
        }

        public static byte BiomeCodeAt(float wx, float wz)
        {
            if (FiresWorldGenBase.Active) return FiresWorldGenBase.BiomeCodeAt(wx, wz);
            if (WorldGenerator.instance == null) return 0;

            // GetBiome is noise work; the paint asks per texel, so memo it per
            // 8m cell — biomes do not change inside one.
            long key = ((long)Mathf.FloorToInt(wx / 8f) << 32) ^ (uint)Mathf.FloorToInt(wz / 8f);
            lock (s_bioMemo)
            {
                if (s_bioMemo.TryGetValue(key, out byte cached)) return cached;
                byte code = CodeFor(WorldGenerator.instance.GetBiome(wx, wz));
                if (s_bioMemo.Count > 32768) s_bioMemo.Clear();
                s_bioMemo[key] = code;
                return code;
            }
        }

        // A lifted chunk's sea (ZoneModeLift) is still water: swamp water on swamp land, a lake elsewhere.
        public static byte WaterClassAt(float wx, float wz)
        {
            if (FiresWorldGenBase.Active) return FiresWorldGenBase.WaterClassAt(wx, wz);
            if (TryNearestWet(wx, wz, 0.5f, out byte cls, out _, out _)) return cls;
            if (!Zones.ZoneModeGate.IsLiftedSeaAt(wx, wz)) return FiresWorldGenBase.WC_NONE;
            return WorldGenerator.instance != null && WorldGenerator.instance.GetBiome(wx, wz) == Heightmap.Biome.Swamp
                ? FiresWorldGenBase.WC_SWAMP
                : FiresWorldGenBase.WC_LAKE;
        }

        private static byte CodeFor(Heightmap.Biome b)
        {
            switch (b)
            {
                case Heightmap.Biome.Meadows: return 0;
                case Heightmap.Biome.BlackForest: return 1;
                case Heightmap.Biome.Swamp: return 2;
                case Heightmap.Biome.Mountain: return 3;
                case Heightmap.Biome.Plains: return 4;
                case Heightmap.Biome.Mistlands: return 5;
                case Heightmap.Biome.AshLands: return 6;
                case Heightmap.Biome.DeepNorth: return 7;
                case Heightmap.Biome.Ocean: return 8;
                default: return 0;
            }
        }

        // The nearest wet source to a point, for the paint's shore integration:
        // its class, its water level and the distance to its wet edge. The
        // generator answers from its 2m field; the vanilla side answers from the
        // channel geometry (distance to the wet edge is what the carve query
        // already measures) and the still-water grid. A swapped zone's channels and bowls are its source's, raised or
        // lowered with its land (ZoneModeLift).
        public static bool TryNearestWet(float wx, float wz, float reach,
            out byte cls, out float level, out float dist)
        {
            cls = FiresWorldGenBase.WC_NONE;
            level = float.MinValue;
            dist = float.MaxValue;
            if (!VoxelHydrology.Ready) return false;
            Zones.ZoneModeGate.TranslateToSource(ref wx, ref wz, out float lift);

            if (VoxelRiverNetwork.TryWetInfo(wx, wz, reach, out float chanLvl, out float chanDist, out float halfWidth, out _))
            {
                cls = halfWidth >= 4f ? FiresWorldGenBase.WC_RIVER : FiresWorldGenBase.WC_STREAM;
                level = chanLvl;
                dist = chanDist;
            }

            // Basin water: 0 inside the bowl, real metres outside it, so the
            // shore band is drawn AROUND a lake and not only under it.
            if (VoxelLakeBasins.TryNearestWet(wx, wz, reach, out float still, out float stillDist)
                && (still > level || stillDist < dist))
            {
                cls = FiresWorldGenBase.WC_LAKE;
                level = still;
                dist = stillDist;
            }

            if (level <= float.MinValue + 1f || dist > reach) return false;
            level += lift;
            return true;
        }
    }
}
