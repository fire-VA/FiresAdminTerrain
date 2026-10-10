using System;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Zones
{
    // ── NOTHING ENTERS A CLOSED ZONE ────────────────────────────────────────
    // A closed zone must stay empty so that opening it later shows the world exactly as generated:
    //   * building: allowed over a closed zone since 0.2.718 (tagged, taken down when it opens: ZoneModeOverClosed), except
    //     terrain tools; with 'Build over closed zones' off the ghost goes red and placing is refused, as before;
    //   * terrain: any terrain op, hoe stroke or dig whose footprint touches a closed zone is refused
    //     whole — a half-applied edit at the edge would split the shared border of two heightmaps;
    //   * teleports into closed sea are refused, and a saved logout or bed point there is cleared;
    //   * creature spawns (including raids) never pick a closed-zone point.
    // Positions above SurfaceCeiling are dungeon interiors (5000 m up) and are never refused.
    internal static class ZoneModeRefusals
    {
        private const float SurfaceCeiling = 1000f;
        private const float MessageCooldownSeconds = 1.5f;
        private const float NearbyOpMessageDistance = 12f;
        private const string ClosedSquareMessage = "This square is still sea. It has to be opened first.";
        private const string NearEdgeMessage = "Too close to the edge of the open land.";
        private const string TeleportMessage = "That destination lies in unopened sea.";

        private static readonly AccessTools.FieldRef<Player, GameObject> PlacementGhostRef =
            AccessTools.FieldRefAccess<Player, GameObject>("m_placementGhost");
        private static readonly AccessTools.FieldRef<Player, Player.PlacementStatus> PlacementStatusRef =
            AccessTools.FieldRefAccess<Player, Player.PlacementStatus>("m_placementStatus");
        private static readonly Action<Player, bool> SetPlacementGhostValid =
            AccessTools.MethodDelegate<Action<Player, bool>>(AccessTools.Method(typeof(Player), "SetPlacementGhostValid"));
        private static readonly AccessTools.FieldRef<Game, PlayerProfile> PlayerProfileRef =
            AccessTools.FieldRefAccess<Game, PlayerProfile>("m_playerProfile");

        private static float s_lastMessageTime = -100f;
        // (placement over a closed zone: see GhostRefused and ZoneModeOverClosed)
        private static TerrainOp s_lastCancelledOp;

        internal static int BuildsRefused;
        internal static int TerrainEditsRefused;
        internal static int TeleportsRefused;
        internal static int SpawnPointsCleared;
        internal static int CreatureSpawnsRefused;

        // A skyland over closed sea is land: building, digging, teleports and logout points on it are fine.
        internal static bool IsClosedAt(Vector3 point)
            => IsClosedGround(point) && !VerdantsAscent.Terrain.Voxel.FiresSkyIslands.IsOnIsland(point);

        // The closed sea itself, islands or not — creature spawns go by this.
        private static bool IsClosedGround(Vector3 point)
            => point.y < SurfaceCeiling && ZoneModeGate.IsClosedForTerrain(point.x, point.z);

        internal static bool TerrainEditTouchesClosed(Vector3 center, float radius)
        {
            var view = ZoneModeGate.Current;
            if (view == null || center.y >= SurfaceCeiling) return false;
            if (VerdantsAscent.Terrain.Voxel.FiresSkyIslands.IsOnIsland(center)) return false;
            return !view.Open.IsRealAt(center.x, center.z) || view.Open.DiscTouchesClosed(center.x, center.z, radius);
        }

        internal static bool WasCancelled(TerrainOp op) => op != null && ReferenceEquals(op, s_lastCancelledOp);

        private static float TerrainRadiusOf(GameObject pieceOrGhost)
        {
            var op = pieceOrGhost.GetComponentInChildren<TerrainOp>();
            if (op != null) return op.GetRadius();
            var modifier = pieceOrGhost.GetComponentInChildren<TerrainModifier>();
            return modifier != null ? modifier.GetRadius() : 0f;
        }

        // FAT 0.2.718 (Fire 10-05: "we were meant to be able to build over the edge of unopened chunks ... platforms down to
        // the water"): a piece over a closed zone is allowed anywhere (Fire's answer), and ZoneModeOverClosed tags it so it is
        // taken down, materials to a chest, when that zone opens. A terrain tool there stays refused: a dig never touches
        // closed ground. 'Build over closed zones' off brings back the old rule (everything over a closed zone refused).
        private static bool GhostRefused(GameObject ghost, out string message)
        {
            Vector3 position = ghost.transform.position;
            float radius = TerrainRadiusOf(ghost);
            if (IsClosedAt(position) && (radius > 0f || ZoneModeConfig.BuildOverClosedZones == null || !ZoneModeConfig.BuildOverClosedZones.Value))
            {
                message = ClosedSquareMessage;
                return true;
            }
            if (radius > 0f && TerrainEditTouchesClosed(position, radius))
            {
                message = NearEdgeMessage;
                return true;
            }
            message = null;
            return false;
        }

        internal static void TellLocalPlayer(string message)
        {
            var player = Player.m_localPlayer;
            if (player == null || Time.time - s_lastMessageTime < MessageCooldownSeconds) return;
            s_lastMessageTime = Time.time;
            player.Message(MessageHud.MessageType.Center, message);
        }

        // From FAT's one Player.UpdatePlacementGhost hook (FatVanillaHooks).
        internal static void RedGhostOverClosedZone(Player player)
        {
            if (!ZoneModeGate.Active || player != Player.m_localPlayer) return;
            var ghost = PlacementGhostRef(player);
            if (ghost == null || !ghost.activeSelf || !GhostRefused(ghost, out _)) return;
            RefuseGhost(player, Player.PlacementStatus.NoBuildZone);
        }

        internal static GameObject PlacementGhost(Player player) => PlacementGhostRef(player);

        internal static Player.PlacementStatus PlacementStatus(Player player) => PlacementStatusRef(player);

        internal static void RefuseGhost(Player player, Player.PlacementStatus status)
        {
            PlacementStatusRef(player) = status;
            SetPlacementGhostValid(player, false);
        }

        // Called from FAT's one Player.TryPlacePiece hook (VoxelToolPieces).
        internal static bool RefusesPlacement(Player player)
        {
            if (!ZoneModeGate.Active || player == null) return false;
            var ghost = PlacementGhostRef(player);
            if (ghost == null || !GhostRefused(ghost, out string message)) return false;
            TellLocalPlayer(message);
            BuildsRefused++;
            // The proof line (lead 10-05: "prove it with a line first"): which rule refused which placement where.
            Vector3 at = ghost.transform.position;
            var zone = ZoneSystem.GetZone(at);
            FiresLogger.LogDebug($"[FiresZones] build refused at ({at.x:0.0},{at.y:0.0},{at.z:0.0}) in "
                + $"{(ZoneModeGate.IsClosedForTerrain(at.x, at.z) ? "closed" : "open")} zone ({zone.x},{zone.y}): "
                + $"{Utils.GetPrefabName(ghost)} - \"{message}\" (terrain tool radius {TerrainRadiusOf(ghost):0.0} m, "
                + $"build over closed zones {(ZoneModeConfig.BuildOverClosedZones?.Value ?? false ? "on" : "off")})");
            return true;
        }

        // From the Priority.First prefix of FAT's one TerrainOp.Awake hook (FatVanillaHooks): false refuses and
        // destroys an op whose footprint touches a closed zone, and marks it for WasCancelled.
        internal static bool AllowTerrainOp(TerrainOp op)
        {
            if (!ZoneModeGate.Active || TerrainOp.m_forceDisableTerrainOps || op == null) return true;
            Vector3 position = op.transform.position;
            if (!TerrainEditTouchesClosed(position, op.GetRadius())) return true;

            s_lastCancelledOp = op;
            TerrainEditsRefused++;
            var player = Player.m_localPlayer;
            if (player != null && Vector3.Distance(player.transform.position, position) < NearbyOpMessageDistance)
                TellLocalPlayer(NearEdgeMessage);
            UnityEngine.Object.Destroy(op.gameObject);
            return false;
        }

        // The prefix of FAT's one Player.TeleportTo hook (FatVanillaHooks, 0.2.697): false refuses the teleport.
        internal static bool AllowTeleport(Player __instance, Vector3 pos, ref bool __result)
        {
            if (!ZoneModeGate.Active || !IsClosedAt(pos)) return true;
            TeleportsRefused++;
            FiresLogger.LogInfo($"[FiresZones] refused a teleport to ({pos.x:0},{pos.z:0}) — zone "
                + $"({ZoneSystem.GetZone(pos).x},{ZoneSystem.GetZone(pos).y}) is closed.");
            if (__instance == Player.m_localPlayer) TellLocalPlayer(TeleportMessage);
            __result = false;
            return false;
        }

        [HarmonyPatch(typeof(Game), "FindSpawnPoint")]
        private static class ClearSpawnPointsInClosedZones
        {
            private static void Prefix(Game __instance)
            {
                if (!ZoneModeGate.Active) return;
                var profile = PlayerProfileRef(__instance);
                if (profile == null) return;
                if (profile.HaveLogoutPoint() && IsClosedAt(profile.GetLogoutPoint()))
                {
                    Vector3 point = profile.GetLogoutPoint();
                    profile.ClearLoguoutPoint();
                    SpawnPointsCleared++;
                    FiresLogger.LogWarning($"[FiresZones] cleared a logout point at ({point.x:0},{point.z:0}) — it lies in a "
                        + "closed zone. Spawning at the bed or the start location instead.");
                }
                if (profile.HaveCustomSpawnPoint() && IsClosedAt(profile.GetCustomSpawnPoint()))
                {
                    Vector3 point = profile.GetCustomSpawnPoint();
                    profile.ClearCustomSpawnPoint();
                    SpawnPointsCleared++;
                    FiresLogger.LogWarning($"[FiresZones] cleared a bed spawn point at ({point.x:0},{point.z:0}) — it lies in "
                        + "a closed zone.");
                }
            }
        }

        [HarmonyPatch(typeof(SpawnSystem), "IsSpawnPointGood")]
        private static class NoCreatureSpawnsInClosedZones
        {
            private static void Postfix(ref Vector3 spawnPoint, ref bool __result)
            {
                if (!__result || !ZoneModeGate.Active || !IsClosedGround(spawnPoint)) return;
                __result = false;
                CreatureSpawnsRefused++;
            }
        }
    }
}
