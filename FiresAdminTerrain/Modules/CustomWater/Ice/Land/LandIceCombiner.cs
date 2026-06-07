using System.Collections.Generic;
using UnityEngine;
using VerdantsAscent.Water.Ice;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// Manages combining overlapping <see cref="LandIceController"/> placements
    /// into unified ice surfaces by adding footprints to existing controllers.
    /// </summary>
    public static class LandIceCombiner
    {
        /// <summary>
        /// After placing a new land ice disk, find or create a controller for it.
        /// If an existing controller's footprints overlap the new disk, add the
        /// footprint to it. Otherwise, leave the new controller standalone.
        /// Returns the controller that owns the new footprint.
        /// </summary>
        public static LandIceController TryCombineNearby(LandIceController newController)
        {
            if (newController == null) return null;

            var newFootprint = newController.Footprints[0];

            var overlapping = new List<LandIceController>();
            var allDisks = LandIceController.AllLandIce;

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

            Debug.Log($"[LandIceCombiner] Combined footprint into existing controller ({target.Footprints.Count} total footprints)");
            return target;
        }
    }
}
