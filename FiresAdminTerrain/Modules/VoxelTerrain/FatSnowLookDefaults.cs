using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // 0.2.1310 (Fire 10-30 "its black", capture 233400 frame 7922): FiresVoxelHeightmap multiplies the snow albedo by the GLOBALS
    // _FiresSnowTint * _FiresSnowBright (shader :1462). They have no default; unset they multiply voxel snow to black.
    // 0.2.1405 (Fire 11-07, voxel snow moves to FAT; one writer per global set): FAT is THE writer of the snow look globals,
    // on every world (FTS's heightmap shader reads them while FAT is loaded and FTS no longer pushes its own). Values = FTS's old
    // identity look (FiresHeightmapShaderLoader LookTint .. LookPackedAmt); the sparkle is FAT's key "Snow sparkle" (was FTS
    // [17 - FiresHeightmap] 08 SnowSparkle, same default 0.6). Re-pushed every 2 s; one Info line when FAT takes them, one per sparkle change.
    internal static class FatSnowLookDefaults
    {
        private static readonly int Tint = Shader.PropertyToID("_FiresSnowTint");
        private static readonly int Bright = Shader.PropertyToID("_FiresSnowBright");
        private static readonly int Smooth = Shader.PropertyToID("_FiresSnowSmooth");
        private static readonly int SmoothAmt = Shader.PropertyToID("_FiresSnowSmoothAmt");
        private static readonly int Sparkle = Shader.PropertyToID("_FiresSnowSparkle");
        private static readonly int PackedTint = Shader.PropertyToID("_FiresSnowPackedTint");
        private static readonly int PackedSmooth = Shader.PropertyToID("_FiresSnowPackedSmooth");
        private static readonly int PackedAmt = Shader.PropertyToID("_FiresSnowPackedAmt");
        internal const float DefaultSparkle = 0.6f;
        private static float s_nextCheck, s_sparkleSaid = -1f;
        private static bool s_said, s_fromVedrSaid;

        // Called every frame (VoxelSnowDepth.Tick and FatSnowGlobals); works every 2 s.
        internal static void Tick()
        {
            if (Time.unscaledTime < s_nextCheck) return;
            s_nextCheck = Time.unscaledTime + 2f;
            float sparkle = FiresVoxelConfig.SnowSparkle != null ? Mathf.Clamp01(FiresVoxelConfig.SnowSparkle.Value) : DefaultSparkle;
            // 0.2.1410 ([seasons] single owner via [lead] 11-08): while Vedr owns the ground snow (VedrApi.GroundSnowActive, live), the sparkle is
            // Vedr's key (VedrApi.SnowSparkle), so the player has one key; FAT's "Snow sparkle" applies again when Vedr's ground snow is off.
            bool fromVedr = VedrLooks.TryVedrSparkle(out float vedrSparkle);
            if (fromVedr) sparkle = Mathf.Clamp01(vedrSparkle);
            if (fromVedr != s_fromVedrSaid)
            {
                s_fromVedrSaid = fromVedr;
                FiresLogger.LogDebug(fromVedr ? $"[SnowLook] snow sparkle follows Vedr's key ({sparkle:0.##}, VedrApi.SnowSparkle) while Vedr draws the ground snow (0.2.1410)"
                                              : $"[SnowLook] snow sparkle back on FAT's 'Snow sparkle' key ({sparkle:0.##}): Vedr's ground snow is off or Vedr is absent (0.2.1410)");
            }
            Shader.SetGlobalColor(Tint, Color.white);
            Shader.SetGlobalFloat(Bright, 1f);
            Shader.SetGlobalFloat(Smooth, 0.35f);
            Shader.SetGlobalFloat(SmoothAmt, 0f);
            Shader.SetGlobalFloat(Sparkle, sparkle);
            Shader.SetGlobalColor(PackedTint, new Color(0.80f, 0.85f, 0.94f, 1f));
            Shader.SetGlobalFloat(PackedSmooth, 0.6f);
            Shader.SetGlobalFloat(PackedAmt, 0.7f);
            if (!s_said)
            {
                s_said = true;
                FiresLogger.LogInfo($"[SnowLook] FAT writes the snow look globals (tint white, bright 1, packed tint 0.80/0.85/0.94, sparkle {sparkle:0.##}) for voxel snow; FTS's heightmap shader reads them while FAT is loaded (0.2.1405)");
            }
            else if (Mathf.Abs(sparkle - s_sparkleSaid) > 0.0001f) FiresLogger.LogInfo($"[SnowLook] snow sparkle {s_sparkleSaid:0.##} -> {sparkle:0.##} (0.2.1405)");
            s_sparkleSaid = sparkle;
        }
    }
}
