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

        internal static bool EyeUnderground()
        {
            var player = Player.m_localPlayer;
            if (player == null || !FiresWorldGenBase.Active) return false;
            Vector3 eye = player.m_eye != null ? player.m_eye.position : player.transform.position;
            return FiresWorldGenBase.TryGetGenGround(eye.x, eye.z, out float g)
                   && eye.y < g - 1.5f;
        }
    }

    [HarmonyPatch(typeof(GameCamera), "GetCameraPosition")]
    internal static class Patch_GameCamera_CaveLiquidScope
    {
        [HarmonyPrefix]
        private static void Prefix()
        {
            FiresCaveCamera.SuppressLiquid = FiresCaveCamera.EyeUnderground();
        }

        // LOW-HEADROOM PIN. In tight caves the vanilla spherecast can't fit a
        // camera behind the player, and the solve leaves the camera OUTSIDE
        // the terrain looking at the mesh's dark underside (user screenshot)
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
            if (!FiresWorldGenBase.TryGetGenGround(pos.x, pos.z, out float gc)
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
