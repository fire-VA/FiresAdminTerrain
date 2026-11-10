using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // BODY READINESS HOLD (0.2.1054; Fire 10-26 picked it from the Voxheim comparison, Tools\RESEARCH_voxheim_vs_fat_20261026.md;
    // [fgn]'s cart-drop suspect). Since 0.2.1054 a REMESH keeps the chunk's old collider live until the new one is baked
    // (VoxelChunkView._staging), so the one moment a resting body can lose its ground is a chunk that has a surface but no
    // collider at all yet (first build, or a chunk that was empty) in a zone whose vanilla ground is already off (swapped,
    // or voxel-only). Bodies resting over such a chunk are held until its collider is live:
    // - owner-simulated rigidbodies that are not characters (carts, item drops, ships, anything else): constraints and
    //   velocities saved, FreezeAll + zero velocity, restored exactly on release;
    // - vanilla's own fall / rescue paths skip a held object: ItemDrop.TerrainCheck, Floating.TerrainCheck, StaticPhysics.SUpdate.
    // Characters keep their own logic (FiresSkylandHold, the arrival gate). Released the frame the collider is live; after 30 s
    // one Warning per body, never a forced release. Nothing pending = one count check per frame.
    internal static class BodyHold
    {
        private const float WarnSeconds = 30f, SummarySeconds = 30f;

        private sealed class Held
        {
            public Rigidbody Body;
            public GameObject Go;
            public VoxelChunkView View;
            public RigidbodyConstraints Constraints;
            public Vector3 Velocity, Angular;
            public float Since;
            public bool Warned;
            public string Name;
            public bool Island;   // 0.2.1079: held over a sky island whose chunk is not built yet (no View); released when terrain is under it
        }

        private static readonly HashSet<VoxelChunkView> s_pending = new HashSet<VoxelChunkView>();
        private static readonly Dictionary<int, Held> s_held = new Dictionary<int, Held>();   // by GameObject instance id
        private static readonly Collider[] s_hits = new Collider[256];
        private static readonly List<int> s_release = new List<int>();
        private static int s_heldTotal, s_releasedTotal, s_maxHeldNow;
        private static float s_nextSummary;

        internal static bool IsHeld(GameObject go) => go != null && s_held.Count > 0 && s_held.ContainsKey(go.GetInstanceID());

        // Main thread (VoxelChunkView.ApplyBuffers / AssignCollider).
        internal static void ChunkChanged(VoxelChunkView view)
        {
            if (view == null) return;
            if (view.AwaitingFirstCollider) s_pending.Add(view);
            else s_pending.Remove(view);
        }

        internal static void ChunkGone(VoxelChunkView view) { if (view != null) s_pending.Remove(view); }

        // 0.2.1078: is p inside a chunk that has a surface but no collider yet? (VoxelSupportHold asks this per support pass.)
        internal static bool PendingAt(Vector3 p)
        {
            if (s_pending.Count == 0) return false;
            foreach (var view in s_pending)
            {
                if (view == null || !view.AwaitingFirstCollider) continue;
                Bounds b = view.SurfaceBounds;
                b.Expand(new Vector3(1f, 6f, 1f));
                if (b.Contains(p)) return true;
            }
            return false;
        }

        // Main thread, once per frame (VoxelMeshPipeline).
        internal static void Tick()
        {
            FlushIslandQueue();
            if (s_pending.Count == 0 && s_held.Count == 0) return;
            if (s_pending.Count > 0) HoldOver();
            if (s_held.Count > 0) ReleaseReady();
            Summary();
        }

        private static void HoldOver()
        {
            s_pending.RemoveWhere(v => v == null || !v.AwaitingFirstCollider);
            foreach (var view in s_pending)
            {
                Bounds b = view.SurfaceBounds;
                Vector2s zone = ZoneSystem.GetZone(b.center);
                if (!VoxelOnlyMode.Active && !VoxelZoneStreamer.IsZoneSolid(zone)) continue;   // vanilla ground is still under it
                Vector3 half = b.extents + new Vector3(0.5f, 3f, 0.5f);
                int n = Physics.OverlapBoxNonAlloc(b.center, half, s_hits, Quaternion.identity, ~0, QueryTriggerInteraction.Ignore);
                for (int i = 0; i < n; i++)
                {
                    var col = s_hits[i];
                    if (col == null) continue;
                    Rigidbody rb = col.attachedRigidbody;
                    GameObject go = rb != null ? rb.gameObject : col.GetComponentInParent<StaticPhysics>()?.gameObject;
                    if (go == null || s_held.ContainsKey(go.GetInstanceID())) continue;
                    if (go.GetComponentInParent<Character>() != null) continue;   // characters keep their own logic
                    var nview = go.GetComponentInParent<ZNetView>();
                    if (nview != null && (!nview.IsValid() || !nview.IsOwner())) continue;   // the owner simulates it
                    if (rb != null && rb.isKinematic) continue;
                    Hold(go, rb, view, zone);
                }
            }
        }

        // 0.2.1079 (Fire: "a TON of floating and falling pieces" under fresh skylands): a skyland's dressing (logs, rocks,
        // pickables, roots) is spawned as world objects at once, but its voxel chunks are built over the next frames, and on a
        // client often later still. A body or StaticPhysics object created over an island column with no terrain collider
        // under it yet fell to the flat ground (no chunk = no AwaitingFirstCollider view, so the hold above never saw it).
        // Objects created over island space are queued here and held until terrain is under them.
        private static readonly List<GameObject> s_islandQueue = new List<GameObject>();
        private static int s_islandHeld;
        private static int s_terrainMask = -1;
        private const float IslandMargin = 8f, TerrainReach = 14f;

        internal static void QueueIfOverIsland(GameObject go)
        {
            if (go == null || !FiresSkyIslands.Enabled) return;
            s_islandQueue.Add(go);
        }

        private static bool TerrainUnder(Vector3 p)
        {
            if (s_terrainMask == -1) s_terrainMask = LayerMask.GetMask("terrain");
            return Physics.Raycast(p + Vector3.up * 1.5f, Vector3.down, TerrainReach, s_terrainMask, QueryTriggerInteraction.Ignore);
        }

        private static void FlushIslandQueue()
        {
            if (s_islandQueue.Count == 0) return;
            foreach (var go in s_islandQueue)
            {
                if (go == null || s_held.ContainsKey(go.GetInstanceID())) continue;
                Vector3 p = go.transform.position;
                if (!FiresSkyIslands.TryIslandAt(p, IslandMargin, out _, out _, out _)) continue;
                if (TerrainUnder(p)) continue;
                var nview = go.GetComponent<ZNetView>();
                if (nview != null && (!nview.IsValid() || !nview.IsOwner())) continue;
                if (go.GetComponent<Character>() != null) continue;
                Rigidbody rb = go.GetComponent<Rigidbody>();
                if (rb != null && rb.isKinematic) rb = null;
                if (rb == null && go.GetComponent<StaticPhysics>() == null && go.GetComponent<ItemDrop>() == null) continue;
                var h = new Held { Body = rb, Go = go, View = null, Island = true, Since = Time.time, Name = Utils.GetPrefabName(go) };
                if (rb != null)
                {
                    h.Constraints = rb.constraints;
                    h.Velocity = rb.linearVelocity;
                    h.Angular = rb.angularVelocity;
                    rb.linearVelocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                    rb.constraints = RigidbodyConstraints.FreezeAll;
                }
                s_held[go.GetInstanceID()] = h;
                s_heldTotal++;
                s_islandHeld++;
                if (s_held.Count > s_maxHeldNow) s_maxHeldNow = s_held.Count;
            }
            s_islandQueue.Clear();
        }

        [HarmonyPatch(typeof(ZNetView), "Awake")]
        private static class Patch_ZNetView_Awake_IslandHold
        {
            private static void Postfix(ZNetView __instance)
            {
                if (__instance != null && FiresSkyIslands.Enabled) QueueIfOverIsland(__instance.gameObject);
            }
        }

        private static void Hold(GameObject go, Rigidbody rb, VoxelChunkView view, Vector2s zone)
        {
            var h = new Held { Body = rb, Go = go, View = view, Since = Time.time, Name = Utils.GetPrefabName(go) };
            if (rb != null)
            {
                h.Constraints = rb.constraints;
                h.Velocity = rb.linearVelocity;
                h.Angular = rb.angularVelocity;
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                rb.constraints = RigidbodyConstraints.FreezeAll;
            }
            s_held[go.GetInstanceID()] = h;
            s_heldTotal++;
            if (s_held.Count > s_maxHeldNow) s_maxHeldNow = s_held.Count;
            Vector3 p = go.transform.position;
            FiresLogger.LogDebug($"[FiresVoxel] body hold: held {h.Name} zone ({zone.x},{zone.y}) chunk {view.Key} at ({p.x:0.0},{p.y:0.0},{p.z:0.0})");
        }

        private static int s_offRimRemoved;

        private static bool RemoveIfOffRim(Held h)
        {
            Vector3 p = h.Go.transform.position;
            if (FiresSkyIslands.TryIslandAt(p, 3f, out _, out _, out _)) return false;   // over the island's own solid: keep waiting
            var nview = h.Go.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid() || !nview.IsOwner()) return false;
            var zdo = nview.GetZDO();
            if (zdo == null || zdo.GetLong(ZDOVars.s_creator) != 0L) return false;   // player-made: never
            if (h.Go.GetComponent<ItemDrop>() != null || h.Go.GetComponent<Character>() != null || h.Go.GetComponent<TombStone>() != null) return false;
            string name = h.Name;
            Vector2s z = ZoneSystem.GetZone(p);
            nview.Destroy();
            h.Go = null;   // the release loop skips it (nothing to restore)
            s_offRimRemoved++;
            FiresLogger.LogDebug($"[FiresVoxel] body hold: removed {name} at ({p.x:0.0},{p.y:0.0},{p.z:0.0}) zone ({z.x},{z.y}): off the sky island's rim (no island solid under it), {s_offRimRemoved} this session");
            return true;
        }

        private static void ReleaseReady()
        {
            s_release.Clear();
            foreach (var kv in s_held)
            {
                var h = kv.Value;
                if (h.Go == null) { s_release.Add(kv.Key); continue; }   // destroyed while held: nothing to restore
                if (h.Island)
                {
                    if (TerrainUnder(h.Go.transform.position)) s_release.Add(kv.Key);
                    // 0.2.1086 (Fire's log: Birch1/Beech1/Oak1 held 30 s+ over island air, floating for good): past WarnSeconds an
                    // object with no island solid under its pivot (the skyland's own column, not the bounding disc) is off the rim:
                    // a world-generated one (no creator) is removed by its owner instead of hanging there or dropping to the ground.
                    else if (Time.time - h.Since > WarnSeconds && RemoveIfOffRim(h)) { s_release.Add(kv.Key); continue; }
                }
                else if (h.View == null || !h.View.AwaitingFirstCollider) { s_release.Add(kv.Key); continue; }
                if (!h.Warned && Time.time - h.Since > WarnSeconds)
                {
                    h.Warned = true;
                    Vector3 p = h.Go.transform.position;
                    Vector2s z = ZoneSystem.GetZone(p);
                    FiresLogger.LogWarning($"[FiresVoxel] body hold: {h.Name} zone ({z.x},{z.y}) chunk {(h.View != null ? h.View.Key.ToString() : "island")} still held after {WarnSeconds:0} s "
                        + (h.Island ? "(the sky island under it has no collider yet)" : "(its chunk has a surface but no collider yet)") + "; it stays held until the collider is live");
                }
            }
            foreach (int id in s_release)
            {
                var h = s_held[id];
                s_held.Remove(id);
                if (h.Go == null) continue;
                if (h.Body != null)
                {
                    h.Body.constraints = h.Constraints;
                    h.Body.linearVelocity = h.Velocity;
                    h.Body.angularVelocity = h.Angular;
                }
                s_releasedTotal++;
                Vector2s z = ZoneSystem.GetZone(h.Go.transform.position);
                FiresLogger.LogDebug($"[FiresVoxel] body hold: released {h.Name} zone ({z.x},{z.y}) after {Time.time - h.Since:0.00} s (collider ready)");
            }
        }

        private static void Summary()
        {
            if (!FiresLogger.DebugEnabled || Time.time < s_nextSummary) return;
            s_nextSummary = Time.time + SummarySeconds;
            FiresLogger.LogDebug($"[FiresVoxel] body hold: {s_held.Count} held now (peak {s_maxHeldNow}), {s_heldTotal} held / {s_releasedTotal} released this session, "
                + $"{s_pending.Count} chunk(s) waiting on a first collider; {s_islandHeld} held over a sky island not built yet (0.2.1079)");
            s_maxHeldNow = s_held.Count;
        }

        internal static void Clear()
        {
            s_pending.Clear();
            s_held.Clear();
        }

        // Vanilla's fall / rescue paths leave a held object alone.
        [HarmonyPatch(typeof(ItemDrop), "TerrainCheck")]
        private static class Patch_ItemDrop_TerrainCheck
        {
            private static bool Prefix(ItemDrop __instance) => !IsHeld(__instance != null ? __instance.gameObject : null);
        }

        [HarmonyPatch(typeof(Floating), "TerrainCheck")]
        private static class Patch_Floating_TerrainCheck
        {
            private static bool Prefix(Floating __instance) => !IsHeld(__instance != null ? __instance.gameObject : null);
        }

        [HarmonyPatch(typeof(StaticPhysics), nameof(StaticPhysics.SUpdate))]
        private static class Patch_StaticPhysics_SUpdate
        {
            private static bool Prefix(StaticPhysics __instance) => !IsHeld(__instance != null ? __instance.gameObject : null);
        }

        [HarmonyPatch(typeof(ZNet), "OnDestroy")]
        private static class Patch_ZNet_OnDestroy_ClearHolds
        {
            private static void Postfix() => Clear();
        }
    }
}
