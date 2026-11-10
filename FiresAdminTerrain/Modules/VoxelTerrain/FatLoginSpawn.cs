using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // 0.2.1138 (players via Fire 10-27: "i'll save my character underground but when i log back in my character will be back on
    // the main level terrain" / "when you log out on a sky island and log back in you fall to the ground"):
    //  * CAVES (proven from code): vanilla Game.FindSpawnPoint (Game.cs:366-395) lifts the logout point to GetGroundHeight when it
    //    is below it ("if (point.y < height) point.y = height"), and FAT's GetGroundHeight (VoxelGroundTruth.Sample) answers the
    //    TOP surface of the column, so a cave floor logout lands on the surface.
    //  * ISLANDS (to be proven by the [SpawnProof] lines): the saved Y is kept, but the island set reaches a client late and the
    //    island chunk under the point may not exist when vanilla spawns, so the player falls to the ground below.
    // The fix (client side; the client spawns itself): for a logout point in a voxel cave or over a sky island, FindSpawnPoint
    // keeps the SAVED floor (the walkable voxel floor nearest the saved Y within +-3 m, or the island top) and does not return
    // until a collider stands under it (and, for islands, the server's island set has arrived). The player does not exist while
    // FindSpawnPoint waits, so nothing can fall or take damage; after the spawn a short HOLD (no gravity, no damage) covers the
    // first second while the body settles. A 20 s timeout releases onto the nearest valid floor, always restoring the body.
    internal static class FatLoginSpawn
    {
        private const float FloorWindow = 3f, ColliderReach = 3f, Timeout = 20f, HoldSeconds = 1.5f, ProbeSeconds = 10f;
        private const float RaisedMetres = 4f;   // 0.2.1430: a terrain logout point higher than this above the ground needs a collider under it before release

        private static int s_mask;
        private static int Mask => s_mask != 0 ? s_mask : (s_mask = LayerMask.GetMask("terrain", "piece", "static_solid", "Default"));

        // the wait for one logout point
        private static float s_waitSince = -1f;
        private static Vector3 s_waitFor;
        private static string s_kind;

        // the proof probe + the hold, armed by a restored (or vanilla) logout spawn
        private static Vector3 s_logout, s_restored;
        private static bool s_proofArmed, s_restoredByUs;
        private static float s_rawGround = float.NaN; private static string s_rawBranch;
        private static float s_spawnedAt = -1f, s_holdUntil = -1f;
        private static Vector3 s_lastPos; private static float s_maxDrop;
        private static bool s_holding, s_gravityBefore, s_saidNoGround;
        internal static bool Holding => s_holding;

        // Where the logout point really is: in a cave (a voxel floor near the saved Y well below the column's top surface), over a
        // sky island (or possibly over one while the island set has not arrived), or on plain terrain (vanilla handles it).
        private static string Classify(Vector3 p, float vanillaGround, out int island)
        {
            island = -1;
            if (FiresSkyIslands.Enabled && FiresSkyIslands.TryTopAt(p.x, p.z, out float top) && p.y >= top - FloorWindow - 1f)
            {
                if (FiresSkyIslands.TryNearestInfo(p, out int idx, out _, out _, out _, out _, out _, out _)) island = idx;
                return "island";
            }
            if (!FiresSkyIslands.ZoneSkylandsReady && !float.IsNaN(vanillaGround) && p.y > vanillaGround + 4f) return "island?";
            if (!float.IsNaN(vanillaGround) && p.y < vanillaGround - 1.5f) return "cave";
            return "terrain";
        }

        // The walkable floor nearest the saved Y: a voxel solid->air crossing within +-FloorWindow (TrySampleSurfaceY answers the
        // highest crossing in the window, which for a cave logout point standing on its floor is that floor).
        private static bool TryFloorNear(Vector3 p, out float floor)
        {
            floor = float.NaN;
            if (!VoxelWorld.TrySampleSurfaceY(p.x, p.z, p.y + FloorWindow, p.y - FloorWindow, out float y, out bool solidTop)) return false;
            if (solidTop) return false;
            floor = y;
            return true;
        }

        private static bool ColliderUnder(Vector3 p, out RaycastHit hit)
            => Physics.Raycast(p + Vector3.up * 0.5f, Vector3.down, out hit, ColliderReach + 0.5f, Mask, QueryTriggerInteraction.Ignore);

        [HarmonyLib.HarmonyPatch(typeof(Game), "FindSpawnPoint")]
        private static class Game_FindSpawnPoint_Login
        {
            [HarmonyLib.HarmonyPriority(HarmonyLib.Priority.High)]
            private static bool Prefix(ref Vector3 point, ref bool usedLogoutPoint, ref bool __result, float dt,
                bool ___m_respawnAfterDeath, PlayerProfile ___m_playerProfile, ref float ___m_respawnWait, float ___m_respawnLoadDuration)
            {
                if (___m_respawnAfterDeath || ___m_playerProfile == null || !___m_playerProfile.HaveLogoutPoint()) return true;
                if (ZNet.instance == null || ZNetScene.instance == null || ZoneSystem.instance == null) return true;
                Vector3 logout = ___m_playerProfile.GetLogoutPoint();
                if (!s_proofArmed || (s_logout - logout).sqrMagnitude > 0.01f)
                {
                    s_proofArmed = true; s_restoredByUs = false; s_logout = logout;
                    s_rawGround = float.NaN;
                }
                // 0.2.1430 ([worldgen], Fire's hand test 11-10: "[Spawn] restored at (2950.7,97.6,-85.1) on terrain; vanilla path (no hold); ground NaN", then a 26 m fall
                // to death at y 71): the ground was sampled ONCE, at the first call, before the heightmap existed, and the stale NaN classified the point as plain terrain.
                // It is sampled again on every call until it answers.
                if (float.IsNaN(s_rawGround))
                {
                    s_rawGround = ZoneSystem.instance.GetGroundHeight(logout, out float g) ? g : float.NaN;
                    s_rawBranch = VoxelGroundTruth.LastBranch ?? "vanilla";
                }
                string kind = Classify(logout, s_rawGround, out int island);
                // 0.2.1430: never release onto ground nobody has checked.
                //  * "unknown": the ground does not answer yet: wait (the player does not exist yet, nothing falls). At Timeout vanilla's own rule decides, and vanilla never drops the
                //    player there: with no ground it clears the logout point and spawns at the bed / start ("Invalid spawn point, no ground", Game.cs:613-620).
                //  * "raised": plain terrain, but the saved point is more than RaisedMetres above the ground (a building, a platform, or a logout while flying). Vanilla keeps the saved Y
                //    (Game.cs:624-626) whether or not anything stands under it yet (EBM's RespawnNet even forces "area ready" after 30 s). Wait for a collider under the saved point;
                //    at Timeout place the player on the known ground instead.
                if (kind == "terrain")
                {
                    if (float.IsNaN(s_rawGround)) kind = "unknown";
                    else if (logout.y > s_rawGround + RaisedMetres) kind = "raised";
                    else return true;   // vanilla's own path at ground level; the postfix still logs the proof line
                }

                // our path: vanilla's own bookkeeping first (the original is skipped)
                ___m_respawnWait += dt;
                usedLogoutPoint = false; point = Vector3.zero; __result = false;
                ZNet.instance.SetReferencePosition(logout);
                if (s_waitSince < 0f || (s_waitFor - logout).sqrMagnitude > 0.01f)
                {
                    s_waitSince = Time.unscaledTime; s_waitFor = logout; s_kind = kind;
                    VoxelZoneStreamer.MarkAreaDirty(logout, 96f);
                    FiresLogger.LogDebug($"[SpawnProof] login at a {kind} logout point ({logout.x:0.0},{logout.y:0.0},{logout.z:0.0}): vanilla ground {s_rawGround:0.0} ({s_rawBranch}) "
                        + $"would {(logout.y < s_rawGround ? $"LIFT it by {s_rawGround - logout.y:0.0} m" : "keep the Y")}; FAT holds the spawn for the saved floor"
                        + (FiresSkyIslands.ZoneSkylandsReady ? "" : " (the island set has not arrived yet)"));
                }
                if (___m_respawnWait <= ___m_respawnLoadDuration || !ZNetScene.instance.IsAreaReady(logout)) return false;
                float waited = Time.unscaledTime - s_waitSince;
                bool timedOut = waited > Timeout;

                if (kind == "unknown")
                {
                    if (!timedOut) return false;   // keep waiting for the heightmap under the point; the next call samples it again
                    s_waitSince = -1f;
                    FiresLogger.LogInfo($"[Spawn] the ground under the logout point ({logout.x:0.0},{logout.y:0.0},{logout.z:0.0}) never answered in {Timeout:0} s: vanilla's own spawn rule decides (with no ground it uses the bed / start point, it never drops the player)");
                    ___m_respawnWait = 0f;
                    return true;
                }
                if (kind == "raised")
                {
                    Vector3 saved = logout;
                    bool support = ColliderUnder(saved, out RaycastHit sup);
                    if (!support && !timedOut) return false;   // the piece / platform under the saved point is not there yet
                    ___m_playerProfile.ClearLoguoutPoint();
                    point = support ? new Vector3(saved.x, sup.point.y + 0.25f, saved.z) : new Vector3(saved.x, s_rawGround + 0.25f, saved.z);
                    usedLogoutPoint = true; __result = true;
                    s_restored = point; s_restoredByUs = true; s_waitSince = -1f;
                    FiresLogger.LogInfo(support
                        ? $"[Spawn] restored at ({point.x:0.0},{point.y:0.0},{point.z:0.0}) on a structure {logout.y - s_rawGround:0.0} m above the ground; held {waited:0.0} s for the collider under it"
                        : $"[Spawn] restored at ({point.x:0.0},{point.y:0.0},{point.z:0.0}) ON THE GROUND: the saved point was {logout.y - s_rawGround:0.0} m up with nothing under it after {Timeout:0} s (a building not loaded, or a logout while flying); not dropped");
                    return false;
                }

                // the floor: the island top, or the voxel floor nearest the saved Y
                kind = Classify(logout, s_rawGround, out island);
                float floor = float.NaN;
                if (kind == "island" && FiresSkyIslands.TryTopAt(logout.x, logout.z, out float top) && Mathf.Abs(top - logout.y) <= FloorWindow + 1f) floor = top;
                if (float.IsNaN(floor) && TryFloorNear(logout, out float f)) floor = f;
                bool islandsReady = FiresSkyIslands.ZoneSkylandsReady;
                Vector3 at = new Vector3(logout.x, float.IsNaN(floor) ? logout.y : floor, logout.z);
                bool collider = ColliderUnder(at, out RaycastHit hit);
                bool ready = !float.IsNaN(floor) && collider && (kind != "island?" && islandsReady || kind == "cave");
                if (!ready && !timedOut) return false;

                string why;
                if (ready) { at.y = hit.point.y; why = $"held {waited:0.0} s for chunk"; }
                else
                {
                    // the timeout fallback: the nearest valid floor (the voxel floor near the saved Y, else vanilla's ground)
                    if (!float.IsNaN(floor) && collider) at.y = hit.point.y;
                    else if (!float.IsNaN(floor)) at.y = floor;
                    else at.y = float.IsNaN(s_rawGround) ? logout.y : s_rawGround;
                    why = $"held {waited:0.0} s, TIMED OUT (floor {(float.IsNaN(floor) ? "none" : floor.ToString("0.0"))}, collider {(collider ? "yes" : "no")}, island set {(islandsReady ? "in" : "not in")}): fallback";
                }
                ___m_playerProfile.ClearLoguoutPoint();
                point = at + Vector3.up * 0.25f;
                usedLogoutPoint = true;
                __result = true;
                s_restored = point; s_restoredByUs = true;
                s_waitSince = -1f;
                string where = kind == "cave" ? "in cave" : kind.StartsWith("island") ? $"on island {(island >= 0 ? island.ToString() : "?")}" : "on terrain";
                FiresLogger.LogInfo($"[Spawn] restored at ({point.x:0.0},{point.y:0.0},{point.z:0.0}) {where}; {why}");
                return false;
            }

            private static void Postfix(bool __result, Vector3 point, bool usedLogoutPoint)
            {
                if (!__result || !s_proofArmed) return;
                s_proofArmed = false;
                if (!usedLogoutPoint) return;
                if (!s_restoredByUs) { s_restored = point; FiresLogger.LogInfo($"[Spawn] restored at ({point.x:0.0},{point.y:0.0},{point.z:0.0}) on terrain; vanilla path (no hold)"); }
                FiresLogger.LogDebug($"[SpawnProof] logout ({s_logout.x:0.0},{s_logout.y:0.0},{s_logout.z:0.0}), vanilla ground {s_rawGround:0.0} ({s_rawBranch}), spawn y {point.y:0.0} "
                    + $"({(s_restoredByUs ? "FAT restored the saved floor" : "vanilla")}); watching the player for {ProbeSeconds:0} s");
                s_spawnedAt = -2f;   // armed: the next local player spawn starts the probe + hold
            }
        }

        // the probe + the hold start when the local player spawns
        [HarmonyLib.HarmonyPatch(typeof(Player), nameof(Player.OnSpawned))]
        private static class Player_OnSpawned_Login
        {
            private static void Postfix(Player __instance)
            {
                if (__instance != Player.m_localPlayer || s_spawnedAt != -2f) return;
                s_spawnedAt = Time.unscaledTime;
                s_lastPos = __instance.transform.position; s_maxDrop = 0f;
                if (s_restoredByUs) StartHold(__instance);
            }
        }

        private static void StartHold(Player p)
        {
            var body = p.GetComponent<Rigidbody>();
            if (body == null) return;
            s_gravityBefore = body.useGravity;
            body.useGravity = false;
            body.linearVelocity = Vector3.zero;
            s_holding = true; s_saidNoGround = false;
            s_holdUntil = Time.unscaledTime + HoldSeconds;
        }

        private static void EndHold(Player p, string why)
        {
            if (!s_holding) return;
            s_holding = false;
            var body = p != null ? p.GetComponent<Rigidbody>() : null;
            if (body != null) body.useGravity = s_gravityBefore;
            FiresLogger.LogDebug($"[Spawn] hold released ({why})");
        }

        // the watchdog + the 10 s proof probe: every jump > 1 m in a frame and the total fall below the spawn are logged with a cause
        [HarmonyLib.HarmonyPatch(typeof(Player), "LateUpdate")]
        private static class Player_LateUpdate_Login
        {
            private static void Postfix(Player __instance)
            {
                if (__instance != Player.m_localPlayer || s_spawnedAt < 0f) return;
                float t = Time.unscaledTime - s_spawnedAt;
                Vector3 pos = __instance.transform.position;
                if (s_holding)
                {
                    // 0.2.1430: the hold ends at its time only once a collider stands under the player (never released onto nothing); the probe end (10 s) is the hard limit
                    bool groundUnder = ColliderUnder(pos, out _);
                    if (Time.unscaledTime >= s_holdUntil && !groundUnder && !s_saidNoGround) { s_saidNoGround = true; FiresLogger.LogDebug($"[Spawn] hold kept past {HoldSeconds:0.0} s: no collider under the player yet ({pos.x:0.0},{pos.y:0.0},{pos.z:0.0})"); }
                    if ((Time.unscaledTime >= s_holdUntil && groundUnder) || __instance.IsTeleporting() || __instance.IsDead()) EndHold(__instance, __instance.IsTeleporting() || __instance.IsDead() ? "teleport/death" : "time, ground under");
                    else
                    {
                        var body = __instance.GetComponent<Rigidbody>();
                        if (body != null) body.linearVelocity = Vector3.zero;
                        if ((pos - s_restored).sqrMagnitude > 0.25f)
                        {
                            FiresLogger.LogDebug($"[SpawnProof] t {t:0.00} s: held player moved externally to ({pos.x:0.0},{pos.y:0.0},{pos.z:0.0}); put back at the restored point");
                            __instance.transform.position = s_restored;
                            if (body != null) body.position = s_restored;
                            pos = s_restored;
                        }
                    }
                }
                Vector3 d = pos - s_lastPos;
                if (d.magnitude > 1f)
                {
                    var body = __instance.GetComponent<Rigidbody>();
                    float vy = body != null ? body.linearVelocity.y : 0f;
                    string cause = __instance.IsTeleporting() ? "teleport" : d.y < -1f && vy < -2f ? "fall (gravity)" : "moved by code (SetPosition / rescue / sync)";
                    FiresLogger.LogDebug($"[SpawnProof] t {t:0.00} s: jump {d.magnitude:0.0} m to ({pos.x:0.0},{pos.y:0.0},{pos.z:0.0}) - {cause}");
                }
                float drop = s_restored.y - pos.y;
                if (drop > 2f && drop > s_maxDrop + 2f)
                {
                    s_maxDrop = drop;
                    FiresLogger.LogDebug($"[SpawnProof] t {t:0.00} s: {drop:0.0} m BELOW the spawn point ({pos.x:0.0},{pos.y:0.0},{pos.z:0.0})");
                }
                s_lastPos = pos;
                if (t > ProbeSeconds)
                {
                    EndHold(__instance, "probe end");
                    FiresLogger.LogDebug($"[SpawnProof] after {ProbeSeconds:0} s the player is at ({pos.x:0.0},{pos.y:0.0},{pos.z:0.0}), {Vector3.Distance(pos, s_restored):0.0} m from the spawn point");
                    s_spawnedAt = -1f;
                }
            }
        }

        // no damage while held (a fall or a hit during the first second after a restored spawn)
        [HarmonyLib.HarmonyPatch(typeof(Character), nameof(Character.Damage))]
        private static class Character_Damage_Login
        {
            private static bool Prefix(Character __instance) => !(s_holding && __instance == Player.m_localPlayer);
        }

        // ---- the test hook: the same decision FindSpawnPoint makes, for a saved point, without a relog
        internal static bool TryRestore(Vector3 logout, out Vector3 at, out string kind, out string why)
        {
            at = logout; why = "";
            float ground = ZoneSystem.instance != null && ZoneSystem.instance.GetGroundHeight(logout, out float g) ? g : float.NaN;
            kind = Classify(logout, ground, out int island);
            if (kind == "terrain") { why = $"terrain (vanilla ground {ground:0.0})"; return false; }
            float floor = float.NaN;
            if (kind == "island" && FiresSkyIslands.TryTopAt(logout.x, logout.z, out float top) && Mathf.Abs(top - logout.y) <= FloorWindow + 1f) floor = top;
            if (float.IsNaN(floor) && TryFloorNear(logout, out float f)) floor = f;
            if (float.IsNaN(floor)) { why = "no floor near the saved Y"; return false; }
            at = new Vector3(logout.x, floor, logout.z);
            if (ColliderUnder(at, out RaycastHit hit)) at.y = hit.point.y;
            at += Vector3.up * 0.25f;
            why = $"{kind}{(island >= 0 ? " #" + island : "")}: floor {floor:0.0}, vanilla ground {ground:0.0} (vanilla would {(logout.y < ground ? "lift" : "keep")})";
            return true;
        }
    }
}
