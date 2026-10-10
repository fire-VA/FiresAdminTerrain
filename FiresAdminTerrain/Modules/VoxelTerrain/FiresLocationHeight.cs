using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // ═══ A LOCATION SITS ON OUR SURFACE. FULL STOP. ══════════════════════════
    // Vanilla decides a location's final height in ZoneSystem.PlaceLocations by
    // throwing away the sited y and re-snapping through GetGroundData, which
    // answers from a Heightmap, a collider raycast, or a voxel probe — three
    // things that are all either absent or half-built while the world is being
    // generated. Every one of them can come back below the real surface, and
    // because a location then registers a LEVELING PAD at that height, the
    // error does not merely misplace the building: it excavates a disc of the
    // pad's radius down to the wrong height and leaves a crater. That is the
    // start temple 60m down in a bowl.
    //
    // The generator does not have that problem. FiresWorldGenBase.SampleFinalMeters
    // is the field the terrain is built from — it needs no Heightmap, no
    // collider, no chunk, and it is identical on the server and every client.
    // So on a Fires world the height comes from there, authoritatively, before
    // the location spawns and before the pad is registered.
    //
    // Deliberately NOT routed through PredictedSurface: that subtracts a channel
    // depth wherever the reconciled water field reports water, which is another
    // way to end up under the ground — a location belongs on the surface, and
    // the ones that belong in water say so with m_snapToWater.
    // The Priority.First prefix of FAT's one ZoneSystem.SpawnLocation hook
    // (FatVanillaHooks): it runs before the pad-registering postfix
    // (LocationSpawnPads), so the pad levels to the corrected height rather
    // than the bad one.
    internal static class FiresLocationHeight
    {
        internal static void Correct(ZoneSystem.ZoneLocation location, ref Vector3 pos)
        {
            if (!FiresWorldGenBase.Active) return;
            if (location != null && location.m_snapToWater) return;   // the sea owns these

            // A FAITHFUL WORLD'S SAVED PLACEMENT IS THE TRUTH, NOT A BUG TO FIX.
            // This patch exists because vanilla re-snaps a freshly SITED location
            // through GetGroundData and can drop it 200 m into the ground. On an
            // imported world none of that applies: the instance came out of the
            // save with the height it was actually placed at, the terrain under it
            // is the same terrain it was placed on, and "correcting" it to the
            // generated surface is how a runestone deliberately buried below
            // ground gets yanked into the open and a hand-adjusted start temple
            // snaps back to its original height. Both were reported, both were
            // this line, and the log said so plainly the whole time:
            //   location 'Runestone_Greydwarfs' at (1233,555): incoming y=31.3,
            //   generated surface=37.1, delta=-5.8m — corrected.
            // 0.2.799: a NEW placement may go into the cave under its spot (FatUndergroundLocations), and a placement already
            // underground (a saved one, a proxy's respawn) keeps its depth: neither is ever snapped to the surface.
            if (FatUndergroundLocations.TryPlace(location, ref pos)) { FatUndergroundLocations.BeforeSpawn(location, pos); return; }
            if (FiresWorldGenBase.LoadedFaithful) return;

            float y = FiresWorldGenBase.SampleFinalMeters(pos.x, pos.z);
            if (float.IsNaN(y) || float.IsInfinity(y)) return;
            float delta = pos.y - y;
            // INSTRUMENTED ON PURPOSE. "Locations are below the terrain" and "the
            // prefix reports nothing" cannot both be true, so this says what it
            // actually sees rather than only shouting past a threshold: every
            // start temple, and anything more than a metre out.
            bool loud = System.Math.Abs(delta) > 1f
                     || (location != null && location.m_prefabName == "StartTemple");
            if (loud)
                FiresLogger.LogInfo($"[FiresVoxel] location '{location?.m_prefabName ?? "?"}' at ({pos.x:0},{pos.z:0}): " +
                          $"incoming y={pos.y:0.0}, generated surface={y:0.0}, delta={delta:+0.0;-0.0}m" +
                          $"{(System.Math.Abs(delta) > 0.05f ? " — corrected" : " — already correct")}.");
            if (System.Math.Abs(delta) > 0.05f) pos.y = y;
        }
    }
}
