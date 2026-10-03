using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // ── Grass follows the ground after a voxel edit (0.2.690; cut back to vanilla's own path in 0.2.693) ────────────────
    // Fire 10-01: "We edit the terrain and the grass needs to refresh to the edited level. Just like all other terrain
    // edits ever in Valheim." Vanilla: a terrain edit rebuilds the heightmap and its collider, then calls
    // ClutterSystem.ResetGrass(centre, radius); the patches in that radius are re-rolled on the following frames through
    // GetGroundInfo onto the new surface. FAT does the same, at the same moment: when a voxel chunk's collider is
    // (re)assigned (VoxelChunkView.AssignCollider - every live edit, local or another player's, and every chunk build),
    // ResetGrass over that collider's own bounds plus 1 m. No queue, no batching (0.2.690 queued 32 m columns and
    // flushed every 0.5 s).
    // The one addition vanilla has no counterpart for: a zone SWAP from vanilla's heightmap to voxel ground (world load,
    // stream-in). Grass rolled before the swap stood on vanilla's collider, at the original height; the swap re-rolls the
    // zone once, the same ResetGrass over the zone. Clients only (a dedicated server draws no grass).
    internal static class FatClutterRegrow
    {
        private const float Margin = 1f, ZoneHalf = 32f;

        // For clutterfloat_test live: how many resets were issued, and when the last one was.
        internal static int Queued, Flushes, Resets;
        internal static float LastFlushAt = -1f;

        private static bool Draws => ZNet.instance == null || !ZNet.instance.IsDedicated();

        // A chunk's collider was (re)assigned: re-roll the grass over it (VoxelChunkView.AssignCollider).
        internal static void Collider(Bounds b)
        {
            var cs = ClutterSystem.instance;
            if (!Draws || cs == null) return;
            cs.ResetGrass(new Vector3(b.center.x, 0f, b.center.z), Mathf.Max(b.extents.x, b.extents.z) + Margin);
            Count();
        }

        // A zone swapped to voxel ground (VoxelZoneStreamer): re-roll the grass rolled on vanilla's collider.
        internal static void Zone(Vector2s zone)
        {
            var cs = ClutterSystem.instance;
            if (!Draws || cs == null) return;
            cs.ResetGrass(ZoneSystem.GetZonePos(zone), ZoneHalf + Margin);
            Count();
        }

        private static void Count()
        {
            Queued++; Flushes++; Resets++;
            LastFlushAt = Time.time;
        }
    }
}
