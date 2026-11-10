using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // CAVE CAMERA FIX — proven from vanilla source: GameCamera.GetCameraPosition
    // runs its geometry collision FIRST and the water clamp LAST with no
    // re-check ("end.y = liquidLevel + m_minWaterDistance"). Our cave pools
    // and nearby generated water report through Floating.GetLiquidLevel, so
    // any camera position inside a cave at/below a water level was teleported
    // straight up THROUGH the cave roof every frame — the constant camera
    // fight, the pull-to-surface, and the camera pinned outside the mesh.
    //
    // Fix: while the camera solve runs FOR AN UNDERGROUND EYE (below the
    // generated surface — a real cave), the liquid query reports no water,
    // so the clamp never fires and the geometry collision keeps the last
    // word. Scoped by a flag set only around GetCameraPosition: floating
    // physics, swimming, ships and every other GetLiquidLevel caller are
    // untouched.
    internal static class FiresCaveCamera
    {
        internal static bool SuppressLiquid;

        // The GENERATED ground over a point (no brush edits): the Fires field on a Fires world; on any other voxel world (super
        // flat, vanilla-generated voxel) the world generator's own height. Fire 10-23 (0.2.1013 hand-over): deep in a carved
        // tunnel on SUPER FLAT the camera left the player and sat at the surface: this test required a Fires world, so on
        // super flat the eye never counted as underground, and under the sea line (y 30) vanilla's water clamp lifted the camera
        // to 30.3, through the rock.
        internal static bool TryGenGround(float x, float z, out float g)
        {
            g = 0f;
            if (FiresWorldGenBase.Active) return FiresWorldGenBase.TryGetGenGround(x, z, out g);
            if (WorldGenerator.instance == null) return false;
            if (!VoxelOnlyMode.Active && !VoxelZoneStreamer.IsZoneSolid(ZoneSystem.GetZone(new Vector3(x, 0f, z)))) return false;
            g = WorldGenerator.instance.GetHeight(x, z);
            return true;
        }

        internal static bool EyeUnderground()
        {
            var player = Player.m_localPlayer;
            if (player == null) return false;
            Vector3 eye = player.m_eye != null ? player.m_eye.position : player.transform.position;
            if (FatHaloArc.Active && FatHaloArc.TryGetUp(eye, out _)) return false;   // 0.2.1051 halo ring: "underground" = under the flat world's ground means nothing on the ring wall (the cave pin would hold the camera at 0.55 m on the band's low side)
            if (!TryGenGround(eye.x, eye.z, out float g) || eye.y >= g - 1.5f) return false;
            // 0.2.1151 (the lead, round 35 deep-snow first-person check): "below the generated height" alone isn't a cave - lowered or
            // dug ground in the open would trip the low-headroom pin (= forced first person). Require a real roof: the terrain (voxel)
            // above the eye within 12 m.
            if (s_terrainMask == 0) s_terrainMask = LayerMask.GetMask("terrain");
            return Physics.Raycast(eye, Vector3.up, 12f, s_terrainMask, QueryTriggerInteraction.Ignore);
        }

        private static int s_terrainMask;
    }

    [HarmonyPatch(typeof(GameCamera), "GetCameraPosition")]
    internal static class Patch_GameCamera_CaveLiquidScope
    {
        [HarmonyPrefix]
        private static void Prefix()
        {
            FiresCaveCamera.SuppressLiquid = FiresCaveCamera.EyeUnderground();
            if (FiresCaveCamera.SuppressLiquid && FiresLogger.DebugEnabled && Time.time >= s_nextNote)
            {
                s_nextNote = Time.time + 30f;
                var p = Player.m_localPlayer;
                Vector3 eye = p.m_eye != null ? p.m_eye.position : p.transform.position;
                FiresCaveCamera.TryGenGround(eye.x, eye.z, out float g);
                FiresLogger.LogDebug($"[FiresVoxel] cave camera: eye y {eye.y:0.0} under the generated ground {g:0.0} at ({eye.x:0},{eye.z:0}): "
                    + "the water clamp is off for the camera (vanilla would lift it to the sea line), geometry collision decides (0.2.1014).");
            }
        }

        private static float s_nextNote;

        // LOW-HEADROOM PIN. In tight caves the vanilla spherecast can't fit a
        // camera behind the player, and the solve leaves the camera OUTSIDE
        // the terrain looking at the mesh's dark underside
        // instead of collapsing toward first person. If the eye is in a cave
        // and the solved position sits at/above the generated surface, the
        // camera doesn't belong there — pin it just behind the eye.
        [HarmonyPostfix]
        private static void Postfix(ref Vector3 pos)
        {
            if (!FiresCaveCamera.SuppressLiquid) return;   // set only for an underground eye
            var player = Player.m_localPlayer;
            if (player == null) return;
            Vector3 eye = player.m_eye != null ? player.m_eye.position : player.transform.position;
            if (!FiresCaveCamera.TryGenGround(pos.x, pos.z, out float gc)
                || pos.y <= gc - 0.4f) return;             // camera still inside the cave — fine
            Vector3 d = pos - eye;
            float dist = d.magnitude;
            if (dist < 0.01f) return;
            pos = eye + d / dist * Mathf.Min(dist, 0.55f);
        }

        [HarmonyFinalizer]
        private static void Finalizer()
        {
            FiresCaveCamera.SuppressLiquid = false;
        }
    }

    [HarmonyPatch(typeof(Floating), nameof(Floating.GetLiquidLevel))]
    internal static class Patch_Floating_CaveCameraLiquid
    {
        [HarmonyPostfix]
        private static void Postfix(ref float __result)
        {
            if (FiresCaveCamera.SuppressLiquid) __result = -999999f;
        }
    }
}
