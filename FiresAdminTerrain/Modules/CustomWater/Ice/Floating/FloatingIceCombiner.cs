using System.Collections.Generic;
using UnityEngine;
using VerdantsAscent.Water.Ice;

namespace VerdantsAscent.Water.Core
{
    /// <summary>
    /// Manages combining overlapping <see cref="FloatingIceController"/> placements
    /// into unified floating ice surfaces by adding footprints to existing controllers.
    /// </summary>
    public static class FloatingIceCombiner
    {
        /// <summary>
        /// After placing a new floating ice disk, find or create a controller for it.
        /// If an existing controller's footprints overlap the new disk, add the
        /// footprint to it. Otherwise, leave the new controller standalone.
        /// Returns the controller that owns the new footprint.
        /// </summary>
        public static FloatingIceController TryCombineNearby(FloatingIceController newController)
        {
            if (newController == null) return null;

            var newFootprint = newController.Footprints[0];

            var overlapping = new List<FloatingIceController>();
            var allDisks = FloatingIceController.AllFloatingIce;

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

                // Transfer holes
                var sourceHoles = source.Holes;
                if (sourceHoles != null)
                {
                    for (int h = 0; h < sourceHoles.Count; h++)
                        target.BreakIceAt(sourceHoles[h].Center, sourceHoles[h].Radius);
                }

                source.Remove();
            }

            newController.Remove();

            Debug.Log($"[FloatingIceCombiner] Combined footprint into existing controller ({target.Footprints.Count} total footprints)");
            return target;
        }
    }
}
