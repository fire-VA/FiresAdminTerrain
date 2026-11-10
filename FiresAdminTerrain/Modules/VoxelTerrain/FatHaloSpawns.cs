using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // HALO RING creature spawning (PLAN_halo_up_retarget A5 + HALO_UP_CREATURES #8/#9; written by [fgn] as files for [worldgen], 10-24).
    //
    // Vanilla places every spawn with a world-down question: SpawnSystem.FindBaseSpawnPoint picks a point around a player by a yaw
    // around world Y, IsSpawnPointGood drops it onto ZoneSystem.GetGroundData (a heightmap height for that x,z), UpdateSpawnList adds
    // the ground offset along Vector3.up, Spawn instantiates with Quaternion.identity, and CreatureSpawner.Spawn uses FindFloor + a
    // world yaw. On the ring "down" is away from the axis, so creatures appear on the flat world floor, in the air or tipped over.
    //
    // A float "ground height" can't describe a point on a vertical ring wall, so the fix is at the spawn CALLERS, not in
    // GetGroundHeight. Inside the ring's field (FatHaloArc.TryGetUp) only:
    // - FindBaseSpawnPoint: the candidate is the player's position plus a random direction in the ring's local floor plane.
    // - IsSpawnPointGood: the point is dropped onto the ring along -up with Core's Surface.Point (THE one ring ground query,
    //   Core 0.2.816+, live 0.2.817), and the biome comes from FatHaloBiome (the ring's own patches). Checks that only mean
    //   something on the flat world (altitude above sea, heightmap tilt, distance from the world centre, forest, lava, ocean
    //   depth, IsBlocked's world-down ray) are skipped on the ring. The player-range and player-base checks stay (3D).
    // - UpdateSpawnList's "+ Vector3.up * groundOffset": Spawn's prefix moves that offset onto the ring's up.
    // - Spawn: rotation = Frame(up) * a random yaw instead of Quaternion.identity.
    // - CreatureSpawner.Spawn: the creature is put on the ring floor under the spawner, upright on the ring.
    // - Character.InInterior(Vector3) (y > 3000 = a dungeon): never inside the ring's field (the band stays below 3,000 today;
    //   this keeps a taller band from turning the ring into "interior" speed/scale).
    // UnderWorldCheck is already skipped in the field for every Character (FatVanillaHooks.Character_UnderWorldCheck, 0.2.1029),
    // which covers creatures too (#9).
    // Off the ring every method runs vanilla, unchanged. One Debug line per kind, then one every 10 s.
    internal static class FatHaloSpawns
    {
        private static Vector3 s_lastPoint, s_lastUp = Vector3.up;
        private static bool s_lastOnRing;
        private static float s_nextLog;

        private static bool InRing(Vector3 p, out Vector3 up)
        {
            up = Vector3.up;
            return FatHaloArc.Active && FatHaloArc.TryGetUp(p, out up);
        }

        private static void Say(string msg)
        {
            if (Time.unscaledTime < s_nextLog) return;
            s_nextLog = Time.unscaledTime + 10f;
            FiresLogger.LogDebug("[FatHalo] spawn: " + msg);
        }

        /// <summary>A random direction in the ring's local floor plane at up.</summary>
        private static Vector3 FloorDir(Vector3 up)
        {
            Vector3 refv = Mathf.Abs(Vector3.Dot(up, Vector3.up)) < 0.9f ? Vector3.up : Vector3.forward;
            Vector3 t = Vector3.Cross(up, refv).normalized;
            return Quaternion.AngleAxis(UnityEngine.Random.Range(0f, 360f), up) * t;
        }

        /// <summary>The ring version of IsSpawnPointGood: false = not a good point; p moves onto the ring floor.</summary>
        private static bool RingGood(SpawnSystem.SpawnData spawn, ref Vector3 p)
        {
            if (!FiresCore.World.Surface.Point(p, out Vector3 floor, out Vector3 up)) return false;
            if (!FatHaloBiome.TryBiomeAt(floor, out Heightmap.Biome biome)) return false;
            if ((spawn.m_biome & biome) == Heightmap.Biome.None) return false;
            if ((spawn.m_biomeArea & Heightmap.BiomeArea.Median) == 0) return false;
            float range = spawn.m_spawnRadiusMin > 0f ? spawn.m_spawnRadiusMin : 40f;
            if (!spawn.m_canSpawnCloseToPlayer && Player.IsPlayerInRange(floor, range)) return false;
            if (!spawn.m_insidePlayerBase && EffectArea.IsPointInsideArea(floor, EffectArea.Type.PlayerBase)) return false;
            p = floor;
            s_lastPoint = floor; s_lastUp = up; s_lastOnRing = true;
            return true;
        }

        [HarmonyPatch(typeof(SpawnSystem), "IsSpawnPointGood")]
        private static class SpawnSystem_IsSpawnPointGood
        {
            // Last: FAT's RavenWaterPatch IsSpawnPointGood prefixes (water/fish) keep their own say off the ring.
            [HarmonyPriority(Priority.Last)]
            private static bool Prefix(SpawnSystem.SpawnData spawn, ref Vector3 spawnPoint, ref bool __result)
            {
                s_lastOnRing = false;
                if (spawn == null || !InRing(spawnPoint, out _)) return true;
                __result = RingGood(spawn, ref spawnPoint);
                return false;
            }
        }

        [HarmonyPatch(typeof(SpawnSystem), "FindBaseSpawnPoint")]
        private static class SpawnSystem_FindBaseSpawnPoint
        {
            private static MethodInfo s_isGood;

            private static bool Prefix(SpawnSystem __instance, SpawnSystem.SpawnData spawn, List<Player> allPlayers,
                ref Vector3 spawnCenter, ref Player targetPlayer, ref bool __result)
            {
                if (!FatHaloArc.Active || allPlayers == null || allPlayers.Count == 0) return true;
                bool anyOnRing = false;
                foreach (var pl in allPlayers) if (pl != null && InRing(pl.transform.position, out _)) { anyOnRing = true; break; }
                if (!anyOnRing) return true;   // nobody on the ring: vanilla, unchanged
                if (s_isGood == null) s_isGood = AccessTools.Method(typeof(SpawnSystem), "IsSpawnPointGood");
                float min = spawn.m_spawnRadiusMin > 0f ? spawn.m_spawnRadiusMin : 40f;
                float max = spawn.m_spawnRadiusMax > 0f ? spawn.m_spawnRadiusMax : 80f;
                var args = new object[2];
                for (int i = 0; i < 20; i++)
                {
                    Player pl = allPlayers[UnityEngine.Random.Range(0, allPlayers.Count)];
                    if (pl == null) continue;
                    Vector3 at = pl.transform.position;
                    Vector3 dir = InRing(at, out Vector3 up)
                        ? FloorDir(up)
                        : Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f) * Vector3.forward;   // vanilla's own draw
                    Vector3 p = at + dir * UnityEngine.Random.Range(min, max);
                    args[0] = spawn; args[1] = p;
                    if ((bool)s_isGood.Invoke(__instance, args))   // runs the patched IsSpawnPointGood (ring or vanilla)
                    {
                        spawnCenter = (Vector3)args[1];
                        targetPlayer = pl;
                        __result = true;
                        return false;
                    }
                }
                spawnCenter = Vector3.zero;
                targetPlayer = null;
                __result = false;
                return false;
            }
        }

        [HarmonyPatch(typeof(SpawnSystem), "Spawn")]
        private static class SpawnSystem_Spawn
        {
            // UpdateSpawnList called Spawn(point + Vector3.up * groundOffset) right after a good ring point: move that offset onto the
            // ring's up. Only when the point is that same ring point (the offset is along world Y, so x and z still match).
            private static void Prefix(SpawnSystem.SpawnData critter, ref Vector3 spawnPoint)
            {
                if (!s_lastOnRing) return;
                float dx = spawnPoint.x - s_lastPoint.x, dz = spawnPoint.z - s_lastPoint.z;
                if (dx * dx + dz * dz > 0.0001f) { s_lastOnRing = false; return; }
                float offset = spawnPoint.y - s_lastPoint.y;
                spawnPoint = s_lastPoint + s_lastUp * offset;
                Say($"{(critter?.m_prefab != null ? critter.m_prefab.name : "?")} on the ring at ({spawnPoint.x:0.0}, {spawnPoint.y:0.0}, {spawnPoint.z:0.0}), up ({s_lastUp.x:0.00}, {s_lastUp.y:0.00}, {s_lastUp.z:0.00}), offset {offset:0.0} m along up");
            }

            // Instantiate(prefab, point, Quaternion.identity): upright on the ring with a random yaw when the point is a ring point.
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
            {
                var identity = AccessTools.PropertyGetter(typeof(Quaternion), nameof(Quaternion.identity));
                var mine = AccessTools.Method(typeof(FatHaloSpawns), nameof(SpawnRotation));
                int n = 0;
                foreach (var ci in code)
                {
                    if (n == 0 && ci.Calls(identity)) { n++; yield return new CodeInstruction(OpCodes.Call, mine).MoveLabelsFrom(ci); continue; }
                    yield return ci;
                }
                if (n != 1) FiresLogger.LogWarning("[FatHalo] spawn: SpawnSystem.Spawn has no Quaternion.identity to replace; ring spawns keep the world rotation");
            }

            private static void Postfix() => s_lastOnRing = false;
        }

        internal static Quaternion SpawnRotation()
            => s_lastOnRing ? GravityFrame(s_lastUp) * Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f) : Quaternion.identity;

        private static Quaternion GravityFrame(Vector3 up) => Quaternion.FromToRotation(Vector3.up, up);

        [HarmonyPatch(typeof(CreatureSpawner), "Spawn")]
        private static class CreatureSpawner_Spawn
        {
            // Vanilla: FindFloor (a world-down ray) + a world yaw. On the ring: the floor under the spawner along -up, upright on it.
            private static void Postfix(CreatureSpawner __instance, ZNetView __result)
            {
                if (__result == null || __instance == null) return;
                Vector3 at = __instance.transform.position;
                if (!InRing(at, out _)) return;
                if (!FiresCore.World.Surface.Point(at, out Vector3 floor, out Vector3 up)) return;
                Quaternion rot = GravityFrame(up) * Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f);
                Transform t = __result.transform;
                t.SetPositionAndRotation(floor, rot);
                var body = __result.GetComponent<Rigidbody>();
                if (body != null) { body.position = floor; body.rotation = rot; }
                var zdo = __result.GetZDO();
                if (zdo != null) { zdo.SetPosition(floor); zdo.SetRotation(rot); }
                Say($"CreatureSpawner {__result.name} on the ring floor at ({floor.x:0.0}, {floor.y:0.0}, {floor.z:0.0}), up ({up.x:0.00}, {up.y:0.00}, {up.z:0.00})");
            }
        }

        [HarmonyPatch(typeof(Character), nameof(Character.InInterior), typeof(Vector3))]
        private static class Character_InInterior
        {
            private static bool Prefix(Vector3 position, ref bool __result)
            {
                if (position.y <= 3000f || !InRing(position, out _)) return true;
                __result = false;
                return false;
            }
        }
    }
}
