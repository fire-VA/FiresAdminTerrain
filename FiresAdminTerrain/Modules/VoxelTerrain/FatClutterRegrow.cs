using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // ── Grass follows the ground after a voxel edit (vanilla's own path) ────────────────
    // Edited terrain must refresh its grass to the edited level, just like all other terrain
    // edits in Valheim. Vanilla: a terrain edit rebuilds the heightmap and its collider, then calls
    // ClutterSystem.ResetGrass(centre, radius); the patches in that radius are re-rolled on the following frames through
    // GetGroundInfo onto the new surface. FAT does the same, at the same moment: when a voxel chunk's collider is
    // (re)assigned (VoxelChunkView.AssignCollider - every live edit, local or another player's, and every chunk build),
    // ResetGrass over that collider's own bounds plus 1 m. No queue, no batching (a queue of 32 m columns
    // flushed every 0.5 s was tried and dropped).
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
        // 0.2.1082: a chunk's FIRST collider (stream-in as the player approaches) no longer re-rolls grass: the grass there was
        // rolled for that same ground, and re-rolling every streamed chunk made clutter flash on approach (Fire's 1080 log:
        // FatClutterRegrow.Collider x46 / 5 s). Only a rebuild of a chunk that already had a collider (a real edit) re-rolls.
        // The zone swap from vanilla to voxel still re-rolls once (Zone).
        private static readonly System.Collections.Generic.HashSet<int> s_hadCollider = new System.Collections.Generic.HashSet<int>();

        internal static void Collider(Bounds b) => Collider(0, b);

        // 0.2.1347 (1345 log: 3,110 calls, 62.5% of all resets): AssignCollider runs for EVERY remesh, and the 1082 "already had a collider" test passes on all of them
        // after the first: paint, snow, refine and border mirror remeshes re-rolled grass over unchanged ground (same log: 8,345 of 13,094 remeshes had the same density).
        // Now the same density hash + vertex count decide. A chunk's first collider still skips and the zone swap still re-rolls.
        internal static bool GroundChangeOn => FiresVoxelConfig.ClutterColliderOnGroundChange != null && FiresVoxelConfig.ClutterColliderOnGroundChange.Value;
        internal static int SameGroundSkipped, GroundFired;
        private static bool s_groundProof;

        internal static void Collider(int chunkId, Bounds b, bool groundMoved = true)
        {
            var cs = ClutterSystem.instance;
            if (!Draws || cs == null) return;
            if (chunkId != 0 && s_hadCollider.Add(chunkId)) return;   // first collider: stream-in, nothing changed
            if (!groundMoved && GroundChangeOn)   // 0.2.1347
            {
                SameGroundSkipped++;
                if (!s_groundProof) { s_groundProof = true; FiresLogger.LogInfo("[ClutterGate] a new collider re-rolls grass only when the chunk's ground changed (0.2.1347)"); }
                return;
            }
            GroundFired++;
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
