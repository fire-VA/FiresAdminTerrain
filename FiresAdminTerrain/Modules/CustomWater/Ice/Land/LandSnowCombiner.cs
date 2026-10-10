using System.Collections.Generic;
using UnityEngine;
using VerdantsAscent.Water.Ice;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// Manages combining overlapping <see cref="LandSnowController"/> placements
    /// into unified snow surfaces by adding footprints to existing controllers.
    ///
    /// Mirrors <see cref="LandIceCombiner"/> 1:1 - snow is visual-only but uses
    /// the same disk-footprint + mesh-combine architecture as land ice, so when
    /// disks overlap they should merge into a single smoothed mesh rather than
    /// each rendering their own skirt at the overlap boundary (which is what
    /// produces the funny "two disks fighting" look the user reported).
    /// </summary>
    public static class LandSnowCombiner
    {
        /// <summary>
        /// After placing a new land snow disk, find or create a controller for it.
        /// If an existing controller's footprints overlap the new disk, add the
        /// footprint to it. Otherwise, leave the new controller standalone.
        /// Returns the controller that owns the new footprint.
        /// </summary>
        public static LandSnowController TryCombineNearby(LandSnowController newController)
        {
            if (newController == null) return null;

            var newFootprint = newController.Footprints[0];

            var overlapping = new List<LandSnowController>();
            var allDisks = LandSnowController.AllLandSnow;

            for (int i = 0; i < allDisks.Count; i++)
            {
                var existing = allDisks[i];
                if (existing == null || existing == newController) continue;
                if (existing.OverlapsWithDisk(newFootprint.Center, newFootprint.Radius))
                    overlapping.Add(existing);
            }

            if (overlapping.Count == 0)
                return newController;

            var target = overlapping[0];
            target.AddFootprint(newFootprint.Center, newFootprint.Radius);

            // Merge all overlapping controllers into target
            for (int i = 1; i < overlapping.Count; i++)
            {
                var source = overlapping[i];
                if (source == null || source == target) continue;

                var sourceFootprints = source.Footprints;
                for (int f = 0; f < sourceFootprints.Count; f++)
                    target.AddFootprint(sourceFootprints[f].Center, sourceFootprints[f].Radius);

                source.Remove();
            }

            newController.Remove();

            FiresLogger.LogInfo($"[LandSnowCombiner] Combined footprint into existing controller ({target.Footprints.Count} total footprints)");
            return target;
        }
    }
}
