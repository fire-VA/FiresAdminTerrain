using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // Round-93 — HONEST cave environment. The legacy trick (tile underside
    // meshes acting as "you're in a cave" detectors) is retired; real caves
    // are voxel geometry now. This drives the cave env from actual enclosure:
    // an upward raycast from the player's head that hits a VOXEL ceiling
    // (terrain layer) means we're underground → force the cave environment.
    //
    // Plays nice with the water stack: while the player is in liquid, the
    // FiresUnderwaterEnv system owns the environment — we stand down.
    [DisallowMultipleComponent]
    public sealed class VoxelCaveEnv : MonoBehaviour
    {
        private const float POLL = 0.5f;
        private const float RAY_UP = 120f;

        private float _nextPoll;
        private bool _forcedByUs;
        private static int s_mask = -1;

        // X-ray guard: vanilla shrinks the camera near plane to
        // m_nearClipPlaneMin (0.1) against blocking geometry — still wide
        // enough to poke through a grazing voxel wall and show sky. In caves
        // we clamp that min way down so the poke window practically vanishes.
        private const float CAVE_NEAR_MIN = 0.03f;
        private float _savedNearMin = float.NaN;

        private void ApplyCameraClip(bool inCave)
        {
            var cam = GameCamera.instance;
            if (cam == null) return;
            if (inCave && float.IsNaN(_savedNearMin))
            {
                _savedNearMin = cam.m_nearClipPlaneMin;
                cam.m_nearClipPlaneMin = CAVE_NEAR_MIN;
            }
            else if (!inCave && !float.IsNaN(_savedNearMin))
            {
                cam.m_nearClipPlaneMin = _savedNearMin;
                _savedNearMin = float.NaN;
            }
        }

        private void Update()
        {
            if (Time.time < _nextPoll) return;
            _nextPoll = Time.time + POLL;

            bool wantCave = false;
            try
            {
                var player = Player.m_localPlayer;
                if (player != null && EnvMan.instance != null
                    && FiresVoxelConfig.CaveEnvEnabled != null && FiresVoxelConfig.CaveEnvEnabled.Value
                    && VoxelWorld.ChunkCount > 0
                    && !player.InLiquid())   // underwater env owns submerged
                {
                    var origin = player.transform.position + Vector3.up * 1.8f;
                    // A roof overhead is NOT enough — a sky island (or a
                    // hand-built voxel bridge) overhead is open air. A cave
                    // means BELOW the generated surface with a ceiling.
                    bool underSurface = true;
                    if (FiresWorldGenBase.Active
                        && FiresWorldGenBase.TryGetGenGround(origin.x, origin.z, out float cg))
                        underSurface = origin.y < cg - 1.5f;
                    if (underSurface)
                    {
                        if (s_mask < 0) s_mask = LayerMask.GetMask("terrain");
                        if (Physics.Raycast(origin, Vector3.up, out var hit, RAY_UP, s_mask, QueryTriggerInteraction.Ignore)
                            && hit.collider.GetComponent<VoxelChunkView>() != null
                            && !FiresSkyIslands.IsOnIsland(hit.point))
                        {
                            wantCave = true;
                        }
                    }
                }
            }
            catch { }

            ApplyCameraClip(wantCave);

            if (wantCave && !_forcedByUs)
            {
                EnvMan.instance.SetForceEnvironment(FiresVoxelConfig.CaveEnvironment.Value);
                _forcedByUs = true;
            }
            else if (!wantCave && _forcedByUs)
            {
                // Only release if the forced env is still OURS (don't stomp the
                // underwater env or admin-forced weather).
                _forcedByUs = false;
                if (EnvMan.instance != null)
                    EnvMan.instance.SetForceEnvironment(string.Empty);
            }
        }

        private void OnDestroy()
        {
            ApplyCameraClip(false);
            // 0.2.701 ([ghost] for [worldgen], the lead 10-02): at quit the root goes with the plugin while the scene's EnvMan and its
            // lights are being torn down, and SetForceEnvironment runs EnvMan.FixedUpdate -> SetEnv on them ("NullReferenceException
            // at EnvMan.SetEnv", every quit with the cave env on); the env goes with the scene then. A plugin reload mid-game still
            // releases it.
            if (_forcedByUs && EnvMan.instance != null && !FiresAdminTerrain.ApplicationQuitting)
                EnvMan.instance.SetForceEnvironment(string.Empty);
            _forcedByUs = false;
        }

        public static void EnsureAttached()
        {
            var root = FiresAdminTerrain.RootObject;
            if (root == null || root.GetComponent<VoxelCaveEnv>() != null) return;
            root.AddComponent<VoxelCaveEnv>();
            FiresLogger.LogInfo("[FiresVoxel] VoxelCaveEnv attached (up-raycast cave environment).");
        }
    }
}
