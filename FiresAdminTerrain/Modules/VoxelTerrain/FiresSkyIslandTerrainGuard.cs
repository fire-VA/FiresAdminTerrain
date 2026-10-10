using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // A VANILLA TERRAIN OP ON A SKY ISLAND NEVER REACHES THE GROUND BELOW.
    //
    // A height-changing terrain op on an island would reach the GROUND zone's TerrainComp,
    // which writes a level delta of (op Y - generated ground height) - so the ground far
    // below rises by the island's altitude. Since 0.2.710 a voxel island takes the op as
    // voxel brushes in its own columns (FiresSkyIslandTerrainEdit); it is refused only where
    // that can't run. Paint on an island or a cave floor goes on that layer alone (0.2.758,
    // VoxelLayerPaint), never on the ground's TerrainComp.
    // Plan: Docs/PLAN_WecInfinityHammerTerrainBridge.md
    internal static class FiresSkyIslandTerrainGuard
    {
        private const float MessageCooldownSeconds = 1.5f;
        private const float NearbyOpMessageDistance = 12f;
        private const string RefusedMessage = "Sky islands can't be reshaped - it would raise the ground far below.";

        private static float s_lastMessageTime = -100f;
        private static bool s_reported;

        // From the Priority.Low prefix of FAT's one TerrainOp.Awake hook (FatVanillaHooks),
        // after the closed-zone refusal and the FAT tile route have both passed on the op.
        internal static bool AllowTerrainOp(TerrainOp op)
        {
            if (op == null || TerrainOp.m_forceDisableTerrainOps) return true;
            if (Zones.ZoneModeRefusals.WasCancelled(op) || Persistence.TileTerrainOpBlocker.WasRoutedToTile(op)) return false;

            var settings = op.m_settings;
            if (settings == null) return true;
            // 0.2.758 (Fire 10-06: paint keeps to the layer it is aimed at): on a cave floor or a skyland the op's paint goes on
            // that layer (VoxelLayerPaint) and never reaches the ground's TerrainComp; what is left of the op carries on.
            if (settings.m_paintCleared && Player.m_localPlayer != null && VoxelLayerPaint.TryPaint(op.transform.position, settings.m_paintRadius, true,
                    VoxelLayerPaint.MaskOf(settings.m_paintType), false, 0, "a terrain paint"))
            {
                settings.m_paintCleared = false;   // this instance's copy, not the prefab's
                if (!(settings.m_level || settings.m_raise || settings.m_smooth))
                {
                    UnityEngine.Object.Destroy(op.gameObject);
                    return false;
                }
            }
            if (!(settings.m_level || settings.m_raise || settings.m_smooth)) return true;

            Vector3 position = op.transform.position;
            // 0.2.760 (Fire 10-06: every tool keeps to the layer it is aimed at): a hoe / cultivator level or raise in a cave, or on
            // ground over a cave, reshapes that layer's voxels through FiresLayerTerrainEdit (the route WEC / IH ops take, one undo
            // step, synced by the chunk anchors), never the ground zone's TerrainComp: a cave-floor level no longer cuts the hill
            // above, and ground over a cave no longer fills into it. Ground over a cave keeps its paint on the TerrainComp.
            if (Player.m_localPlayer != null && VoxelZoneStreamer.IsVoxelWorldActive)
            {
                var layer = FiresTerrainLayers.Resolve(position);
                if (layer.Kind != TerrainLayerKind.Island && FiresLayerTerrainEdit.Takes(in layer))
                {
                    LayerFromTerrainOp(settings, in layer, position);
                    if (layer.Kind == TerrainLayerKind.Ground && settings.m_paintCleared)
                    {
                        settings.m_level = settings.m_raise = settings.m_smooth = false;   // this instance's copy
                        return true;
                    }
                    UnityEngine.Object.Destroy(op.gameObject);
                    return false;
                }
            }
            // 0.2.737: beside an island at its height counts too (FiresSkyIslandTerrainEdit.Targets), not only over its shape.
            if (!FiresSkyIslandTerrainEdit.Targets(position) && !FiresSkyIslands.IsOnIsland(position)) return true;

            // 0.2.710 (Fire, 10-05): on a voxel island the op reshapes the island itself (FiresSkyIslandTerrainEdit). Its paint
            // was taken onto the island above (0.2.758); nothing of an island's op reaches the ground's TerrainComp.
            if (FiresSkyIslandTerrainEdit.Targets(position))
            {
                FiresSkyIslandTerrainEdit.FromTerrainOp(settings, position);
                UnityEngine.Object.Destroy(op.gameObject);
                return false;
            }

            Report(position);
            TellLocalPlayer(position);
            UnityEngine.Object.Destroy(op.gameObject);
            return false;
        }

        // The op's height parts as layer reshapes: the same targets FiresSkyIslandTerrainEdit.FromTerrainOp gives an island's.
        private const string LayerRoute = "vanilla TerrainOp";

        private static void LayerFromTerrainOp(TerrainOp.Settings s, in TerrainLayer layer, Vector3 position)
        {
            if (s.m_level)
            {
                float level = position.y + s.m_levelOffset;
                FiresLayerTerrainEdit.Reshape("level", LayerRoute, in layer, position, s.m_levelRadius, (dx, dz, surface) => level);
            }
            if (s.m_raise)
            {
                float r = Mathf.Max(s.m_raiseRadius, 2f), delta = s.m_raiseDelta, power = Mathf.Max(s.m_raisePower, 0.01f);
                float cap = position.y + delta;
                FiresLayerTerrainEdit.Reshape(delta >= 0f ? "raise" : "lower", LayerRoute, in layer, position, r, (dx, dz, surface) =>
                {
                    float w = Mathf.Pow(1f - Mathf.Clamp01(Mathf.Sqrt(dx * dx + dz * dz) / r), power);
                    float t = surface + delta * w;
                    return delta >= 0f ? Mathf.Min(t, Mathf.Max(cap, surface)) : Mathf.Max(t, Mathf.Min(cap, surface));
                });
            }
            if (s.m_smooth)
                FiresLogger.LogDebug($"[FiresLayers] terrain op on {layer.Describe()} at ({position.x:0.0},{position.y:0.0},{position.z:0.0}) via "
                    + $"{LayerRoute}: smooth r={s.m_smoothRadius:0.#} skipped (no voxel smooth); the ground zone's TerrainComp untouched");
        }

        // Loud once, then quiet: a held brush would otherwise log every stroke.
        private static void Report(Vector3 position)
        {
            string line = $"[FiresSkyIslands] refused a terrain op at ({position.x:0},{position.y:0},{position.z:0}) - it sits "
                + "on a sky island, and levelling, raising or smoothing there would have moved the ground below instead. "
                + "Paint and cultivate still work on islands.";
            if (s_reported)
            {
                FiresLogger.LogVerbose(line);
                return;
            }
            s_reported = true;
            FiresLogger.LogWarning(line);
        }

        private static void TellLocalPlayer(Vector3 position)
        {
            var player = Player.m_localPlayer;
            if (player == null || Vector3.Distance(player.transform.position, position) >= NearbyOpMessageDistance) return;
            if (Time.time - s_lastMessageTime < MessageCooldownSeconds) return;
            s_lastMessageTime = Time.time;
            player.Message(MessageHud.MessageType.Center, RefusedMessage);
        }
    }
}
