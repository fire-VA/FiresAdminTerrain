using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // ═══ THE PREDICATES PLACEMENT RULES ARE WRITTEN IN ═══════════════════════
    // Expand World's data files describe where things belong almost entirely in
    // terrain terms — in the shipped set: 913 minAltitude, 871 maxAltitude, 580
    // maxTerrainDelta, 527 maxTilt, plus ocean depth. Vanilla answers altitude
    // from WorldGenerator.GetHeight (ours already, via GetBiomeHeight) but
    // answers DELTA by firing ten physics raycasts through ZoneSystem
    // .GetGroundHeight. On a voxel world each of those becomes a vertical probe
    // through chunk data, so a single placement attempt costs ten probes — and
    // PlaceVegetation makes an attempt per candidate, not per placed item.
    //
    // Our height field can answer the same question analytically. Same ten
    // samples, same radius, same random disc — just read from the field the
    // terrain is built from instead of raycast against colliders that often are
    // not there yet. Cheaper, and correct during world generation rather than
    // only after the chunks stream in.
    [HarmonyPatch]
    internal static class FiresTerrainPredicates
    {
        private static System.Reflection.MethodBase TargetMethod()
            => AccessTools.Method(typeof(ZoneSystem), "GetTerrainDelta");

        private static bool Prefix(Vector3 center, float radius, out float delta, out Vector3 slopeDirection)
        {
            delta = 0f; slopeDirection = Vector3.up;
            if (!FiresWorldGenBase.Active) return true;

            float lo = float.MaxValue, hi = float.MinValue;
            Vector3 loP = center, hiP = center;
            // Vanilla's own sample count and distribution, so a rule tuned
            // against vanilla behaves the same way here.
            for (int i = 0; i < 10; i++)
            {
                Vector2 o = Random.insideUnitCircle * radius;
                float x = center.x + o.x, z = center.z + o.y;
                float y = FiresWorldGenBase.SampleFinalMeters(x, z);
                if (y < lo) { lo = y; loP = new Vector3(x, y, z); }
                if (y > hi) { hi = y; hiP = new Vector3(x, y, z); }
            }
            delta = hi - lo;
            slopeDirection = (loP - hiP).normalized;   // downhill, as vanilla returns it
            return false;
        }
    }
}
