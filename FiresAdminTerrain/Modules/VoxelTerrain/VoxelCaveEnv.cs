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
        private string _forcedEnv;

        // 0.2.817 (Fire 10-07: the light flapped Clear <-> Crypt every 3-4 s at the dome edge, restarting the fade each time): out at
        // 20% depth (was 40%) and at least DomeHold s between switches (was 1 s), so one crossing is one fade.
        private const float DomeBand = 6f, DomeIn = 0.6f, DomeOut = 0.2f, DomeHold = 6f, DomeBlend = 3.5f;
        private const float DomeStayBand = 14f;
        private const float EnvEnterHold = 0.5f, EnvLeaveHold = 1.5f, EnvDwell = 3f;
        private bool _caveHeld;
        private float _caveRawSince, _caveSwitched = -100f;
        private bool _inDome;
        private float _domeSince, _domeDepth;
        private static bool s_envChecked;

        private bool UpdateDome(out string env)
        {
            env = null;
            var player = Player.m_localPlayer;
            if (player == null) return _inDome = false;
            if (!s_envChecked && EnvMan.instance != null) { s_envChecked = true; FatUndergroundLocations.CheckEnvironments(); }
            // 0.2.825 (Fire 10-07: "the grotto env should hold a further bounds than it does so the cave env doesnt trigger just
            // because were under an overhang in the grotto"): once in, the dome is measured with the wider DomeStayBand, so it
            // holds until ~DomeStayBand * (1 - 2 * DomeOut) m past the dome's wall / floor / top (8.4 m), and its env beats the
            // cave env under any overhang inside or around the grotto. Entering keeps the 6 m band.
            bool near = FatUndergroundLocations.TryDomeAt(player.transform.position + Vector3.up, _inDome ? DomeStayBand : DomeBand, out var dome, out _domeDepth);
            bool want = near && (_inDome ? _domeDepth > DomeOut : _domeDepth >= DomeIn);
            if (want != _inDome && Time.time - _domeSince >= DomeHold)
            {
                _inDome = want;
                _domeSince = Time.time;
            }
            if (_inDome && near) env = dome.Environment;
            return _inDome && near;
        }

        private static string CaveEnvHere()
        {
            var player = Player.m_localPlayer;
            if (player == null || WorldGenerator.instance == null) return FiresVoxelConfig.CaveEnvironment.Value;
            Vector3 p = player.transform.position + Vector3.up * 0.5f;
            if (!FiresCaveBiome.TryCaveBiome(p, out var biome))
                biome = FiresCaveBiome.RegionBiome(p.x, p.z, WorldGenerator.instance.GetBiome(p.x, p.z));
            return FiresCaveBiome.EnvironmentFor(biome);
        }
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

            // 0.2.824 (Fire 10-07, out-river tunnel: "(open air) -> Crypt" then released 2-5 s later, 4x in 30 s): the raw test
            // only proposes; in after EnvEnterHold s, out after EnvLeaveHold s, switches at least EnvDwell s apart (the clutter's
            // rule, ClutterGroundProbe). Liquid releases at once: the underwater env owns submerged.
            bool liquid = Player.m_localPlayer != null && Player.m_localPlayer.InLiquid();
            // 0.2.844 (Fire 10-07, 121/122.webp: the env "triggering and cancelling over and over" at a tunnel mouth): the tunnel
            // environment follows CaveFactor's one decision (continuous cover x depth, in at 0.7, out at 0.3), the same one the
            // clutter, the ocean cull and the player's cover read. The up-ray test above only gates it (a sky island overhead is
            // not a cave). Liquid releases at once: the underwater env owns submerged.
            _caveHeld = !liquid && CaveFactor.InCave && (wantCave || _caveHeld);
            wantCave = _caveHeld;

            ApplyCameraClip(wantCave);

            // 0.2.797: the environment of THIS cave's biome (FiresCaveBiome: painted, else its region's; the 'Cave environments'
            // map, else the voxel 'Cave environment'), re-forced when the player walks into a cave region of another biome.
            string env = wantCave ? CaveEnvHere() : null;
            // 0.2.799 (Fire 10-06: open chambers lighter than the passages, and "a nice smooth transition"): inside an
            // underground location's dome its own environment wins. Hysteresis on the dome's boundary band (in at 60% depth,
            // out at 40%, held at least DomeHold s), and every switch goes through vanilla's forced-environment cross-fade,
            // slowed to DomeBlend s, so passage -> dome never pops.
            if (Player.m_localPlayer != null) FatGrottos.Tick(Player.m_localPlayer.transform.position);   // 0.2.800: grotto glow + domes
            if (Player.m_localPlayer != null) FatSinkFalls.Tick(Player.m_localPlayer.transform.position);   // 0.2.802: waterfall sinkhole curtains + domes
            FatShaftUnderwater.Tick();   // 0.2.821: the river seen from below while under a big sinkhole's bed
            if (Player.m_localPlayer != null) FatCaveRivers.Tick(Player.m_localPlayer.transform.position, VerdantsAscent.Performance.ClutterGroundProbe.CameraInCave);   // 0.2.801: shaft lights + ocean cull
            bool inDome = UpdateDome(out string domeEnv);
            if (inDome) { wantCave = true; env = domeEnv; }
            if (wantCave && (!_forcedByUs || env != _forcedEnv))
            {
                if (EnvMan.instance.m_transitionDuration < DomeBlend) EnvMan.instance.m_transitionDuration = DomeBlend;
                FiresLogger.LogDebug($"[CaveEnv] {(_forcedByUs ? _forcedEnv : "(open air)")} -> {env} over {EnvMan.instance.m_transitionDuration:0.0} s"
                    + (inDome ? $" (dome, band depth {_domeDepth:0.00})" : ""));
                EnvMan.instance.SetForceEnvironment(env);
                _forcedByUs = true;
                _forcedEnv = env;
            }
            else if (!wantCave && _forcedByUs)
            {
                // Only release if the forced env is still OURS (don't stomp the
                // underwater env or admin-forced weather).
                _forcedByUs = false;
                if (EnvMan.instance != null)
                {
                    // 0.2.825 (Fire 10-07: "make sure env blending over time is properly handled"): leaving fades too, over the
                    // same DomeBlend floor (vanilla re-queues the weather env from the current blended state on release).
                    if (EnvMan.instance.m_transitionDuration < DomeBlend) EnvMan.instance.m_transitionDuration = DomeBlend;
                    FiresLogger.LogDebug($"[CaveEnv] {_forcedEnv} -> (open air) over {EnvMan.instance.m_transitionDuration:0.0} s");
                    EnvMan.instance.SetForceEnvironment(string.Empty);
                }
            }
        }

        private void OnDestroy()
        {
            ShareRoofQuery(false);
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

        // ── Core's swimming: is this point in a cave under the sea? (0.2.727; Fire 10-05: "when a cave passes under a water plane
        // (like the ocean) the water shouldnt be registered within the cave") ──────────────────────────────────────────────────
        // FiresCore.Movement.PlayerSwim.RoofAbove (Core 0.2.298) asks it for every character the sea reaches, and drops that sea
        // where it answers true; water placed with FAT's tools reaches characters through other components and is never asked
        // about. A cave is the cave environment's own test: below the generated surface (vanilla's height on a stock-generated voxel
        // world), with a voxel ceiling straight above that is not a sky island. A sea cliff's overhang (over the sea floor, not under
        // the ground) and open water are never caves. Registered by reflection, so FAT runs on an older Core that has no such hook.
        private const float RoofBodyLift = 0.9f, RoofBelowGround = 1f, RoofReachMargin = 2f;

        // Core asks for every character the sea touches, each time vanilla sets a liquid level ([generator] 10-05: keep it O(1)).
        // Answers are kept per 1 m cell for a quarter second in a fixed table: no allocation, at most one ground sample and one
        // short ray per cell and moment.
        private const int RoofCacheSlots = 64;
        private const float RoofCacheSeconds = 0.25f;
        private static readonly int[] s_roofKey = new int[RoofCacheSlots];
        private static readonly float[] s_roofUntil = new float[RoofCacheSlots];
        private static readonly bool[] s_roofAnswer = new bool[RoofCacheSlots];

        internal static bool CaveRoofAbove(Vector3 pos, float seaSurface)
        {
            if (VoxelWorld.ChunkCount <= 0) return false;
            int key;
            unchecked { key = (Mathf.FloorToInt(pos.x) * 73856093) ^ (Mathf.FloorToInt(pos.y) * 19349663) ^ (Mathf.FloorToInt(pos.z) * 83492791); }
            int slot = (key & 0x7fffffff) % RoofCacheSlots;
            float now = Time.time;
            if (s_roofKey[slot] == key && s_roofUntil[slot] > now) return s_roofAnswer[slot];
            bool answer = CaveRoofAboveNow(pos, seaSurface);
            s_roofKey[slot] = key;
            s_roofUntil[slot] = now + RoofCacheSeconds;
            s_roofAnswer[slot] = answer;
            return answer;
        }

        private static bool CaveRoofAboveNow(Vector3 pos, float seaSurface)
        {
            Vector3 origin = pos + Vector3.up * RoofBodyLift;   // the body's middle: a roof just over the head still counts
            float ground;
            if (FiresWorldGenBase.Active && FiresWorldGenBase.TryGetGenGround(origin.x, origin.z, out float gen)) ground = gen;
            else if (WorldGenerator.instance != null) ground = WorldGenerator.instance.GetHeight(origin.x, origin.z);
            else return false;
            if (origin.y >= ground - RoofBelowGround) return false;
            if (s_mask < 0) s_mask = LayerMask.GetMask("terrain");
            float reach = Mathf.Max(seaSurface, ground) - origin.y + RoofReachMargin;
            return Physics.Raycast(origin, Vector3.up, out var hit, reach, s_mask, QueryTriggerInteraction.Ignore)
                   && hit.collider.GetComponent<VoxelChunkView>() != null
                   && !FiresSkyIslands.IsOnIsland(hit.point);
        }

        private static System.Reflection.FieldInfo s_coreRoofField;
        private static bool s_coreRoofLookedUp;
        private static readonly System.Func<Vector3, float, bool> s_roofQuery = CaveRoofAbove;

        private static void ShareRoofQuery(bool share)
        {
            if (!s_coreRoofLookedUp)
            {
                s_coreRoofLookedUp = true;
                var swim = System.Type.GetType("FiresCore.Movement.PlayerSwim, FiresUnifiedCore");
                s_coreRoofField = swim?.GetField("RoofAbove", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                FiresLogger.LogDebug(s_coreRoofField != null
                    ? "[FiresVoxel] Core's swimming asks FAT whether a point is in a cave under the sea (PlayerSwim.RoofAbove)."
                    : "[FiresVoxel] this Core has no PlayerSwim.RoofAbove (older than 0.2.298): the sea in caves stays FAT's own deep-water gate.");
            }
            if (s_coreRoofField == null) return;
            try
            {
                if (share) s_coreRoofField.SetValue(null, s_roofQuery);
                else if (ReferenceEquals(s_coreRoofField.GetValue(null), s_roofQuery)) s_coreRoofField.SetValue(null, null);
            }
            catch (System.Exception ex)
            {
                FiresLogger.LogWarning($"[FiresVoxel] could not {(share ? "share" : "withdraw")} the cave query with Core: {ex.Message}");
            }
        }

        private void Awake() => ShareRoofQuery(true);
    }
}
