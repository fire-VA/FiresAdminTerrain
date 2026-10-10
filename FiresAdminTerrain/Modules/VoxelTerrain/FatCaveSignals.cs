using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // 0.2.825 ([visual] for FTS's cave-aware fog, 10-07): a small public surface for peers that read FAT by reflection. Plain
    // primitives only; names and meanings are a contract (tell [visual] before any change).
    //   CameraInCave            - the camera is in a cave: the clutter's hysteresis'd decision (in 0.5 s, out 1.5 s, 3 s apart).
    //   ShaftCount / TryGetShaft - every big sinkhole shaft this world knows (sidecar): centre x/z, radius, river bed y and the
    //                              river surface y over it (bed + 1 when no generated river stands there).
    public static class FatCaveSignals
    {
        public static bool CameraInCave => VerdantsAscent.Performance.ClutterGroundProbe.CameraInCave;

        // 0.2.844 ([visual], FTS 0.3.157): the camera's continuous cave factor, 0 open sky .. 1 deep in a cave (roof cover x depth under
        // the ground, smoothed over 1.5 s; CaveFactor). No hysteresis: FTS lerps its fog by it. CameraInCave is the decision on it
        // (in at 0.7, out at 0.3).
        public static float CameraCaveFactor => CaveFactor.Factor;

        // 0.2.835 ([visual] for FTS's submerged flag, Fire's 106.webp: under a lake above sea level the camera read dry): the
        // surface of the inland water FAT draws over the point (x, y, z): the generated rivers / lakes / cave rivers from the
        // stored chunk columns the sheets are built from (the layer the point is in, so a cave river under a surface river answers
        // its own level; nothing under a big sinkhole's bed), and placed water (pools, ponds, pours) within reach above. False
        // when no inland water covers the column there. Cheap: array lookups, no raycast. The sea is not included.
        public static bool TryWaterSurfaceAt(float x, float y, float z, out float surfaceY)
        {
            surfaceY = float.NaN;
            float gen = VoxelWaterSurface.SurfaceAt(new Vector3(x, y, z));
            if (gen > -1000f) surfaceY = gen;
            float placed = VerdantsAscent.Water.Tools.WaterSheets.SurfaceAt(x, z, y - PlacedBelow, y + PlacedAbove);
            if (!float.IsNaN(placed) && (float.IsNaN(surfaceY) || placed > surfaceY)) surfaceY = placed;
            return !float.IsNaN(surfaceY);
        }
        private const float PlacedBelow = 1f, PlacedAbove = 40f;

        public static int ShaftCount
        {
            get
            {
                int n = 0;
                while (FatSinkFallCuts.TryShaft(n, out _, out _, out _)) n++;
                return n;
            }
        }

        public static bool TryGetShaft(int index, out float x, out float z, out float radius, out float bedY, out float surfaceY)
        {
            x = z = radius = bedY = surfaceY = float.NaN;
            if (!FatSinkFallCuts.TryShaft(index, out Vector2 centre, out radius, out bedY) || float.IsNaN(bedY)) return false;
            x = centre.x;
            z = centre.y;
            surfaceY = bedY + 1f;
            if (GenWaterField.TryWaterSurfaceSmooth(centre.x, centre.y, out float level, out _) && level > bedY) surfaceY = level;
            return true;
        }
    }
}
