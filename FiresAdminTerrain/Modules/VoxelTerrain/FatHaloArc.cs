using System;
using System.Globalization;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // HALO RING (0.2.1027 spike arc, 0.2.1029 HaloFlat; Fire 10-24: a TRUE 3D ring where "up" is retargeted, tested at full size
    // on a superflat world; [visual] owns the gravity side through FatApi.HaloTryGetUp/HaloAxis).
    //
    // One ring shell in voxels around an axis line (origin O, unit direction D). The shell is solid between radius R (the INSIDE
    // face players walk on; gravity pulls outward, so "up" points at the axis) and R + T; along the axis it spans |t| <= W from O,
    // with a rim lip RimHeight tall rising inward at both band edges; around the axis it covers ±A degrees of a reference
    // direction (A >= 180 = the whole ring). It unions into chunk density at generation time like a sky island (VoxelChunk), and
    // the streamer extends each column's chunk band over it.
    //
    // HaloFlat (Fire approved, vertical hub line agreed with [visual] + [lead]): D = world Y through the origin, R 4,775 m
    // (30 km around), the band y 200..3,200 (O.y 1,700, W 1,500), T 4 m, on a SuperFlat world (its floor stays at y 50, far
    // below the band). A vertical axis gives every point of the ring its own (x, z) column: zones, ZDO sectors, activation and
    // the minimap all key on (x, z), so nothing stacks.
    //
    // Stored as ONE ZoneSystem global key, "fat_halo_arc ox,oy,oz,dx,dy,dz,r,w,t,a" (global keys persist with the world and
    // reach every client, so every peer builds the same ring). The 0.2.1027 seven-value form (cx,cy,cz,r,w,t,a) still parses as
    // an X axis. Partial arcs measure their angle from -Y projected off the axis (from +X when the axis is vertical).
    // Only X- or Y-aligned axes stream (TryGetBand); any other direction is refused when parsed.
    internal static class FatHaloArc
    {
        private const string Key = "fat_halo_arc";
        internal const float RimHeight = 40f, RimThickness = 4f;
        internal struct Arc
        {
            public Vector3 O, D;
            public float R, W, T, A;
            public bool Vertical => Mathf.Abs(D.y) > 0.999f;
        }

        private static bool s_has;
        private static Arc s_arc;
        private static Vector3 s_ref, s_ref2;   // the angle frame: s_ref is angle 0, s_ref2 = D x s_ref
        private static string s_lastRaw;

        internal static bool Active => s_has;

        // MAIN THREAD ONLY (VoxelZoneStreamer.Update + the commands): global keys are read here and nowhere else, so the
        // generation workers only ever read the parsed s_arc. A change (a command here, or a client receiving the key) re-arms
        // the old and new ring areas, so their loaded zones regenerate in place.
        internal static void Tick()
        {
            EnsureHaloFlatKey();
            FatHaloSectors.Tick();   // ring object sectors on HaloFlat ([fgn] zone-key root fix)
            Arc old = s_arc; bool hadOld = s_has;
            if (Refresh())
            {
                if (hadOld) Rearm(old);
                if (s_has) Rearm(s_arc);
            }
            if (!s_has && !hadOld) return;   // 0.2.1052 (Fire 10-26, off-ring audit): no ring, none was there: nothing below runs
            UpdateFocus();
            LogStandingSurface();
            PushShaderGlobals();
            FatHaloClutter.Tick();   // 0.2.1041 step 3: vanilla clutter on the ring, oriented to its up
            FatHaloVegetation.Tick();   // 0.2.1042 step 4: vanilla vegetation on the ring (server)
            FatHaloSky.Tick(s_has, s_lastRaw, s_arc.O, s_arc.D, s_arc.R, s_arc.W, s_arc.T);   // 0.2.1031 step C
            FatHaloWorldTree.Tick(s_has, s_lastRaw, s_arc.O, s_arc.D);   // 0.2.1050 Fire: vanilla tree along the axle
        }

        // ── HaloFlat, zero setup (0.2.1030; [lead]): a world NAMED HaloFlat is created super flat + full voxel at
        // HaloFlatWorldRadius (FiresWorldProfile), its server writes the ring key on first start, and a character with no logout
        // point and no bed spawns on the ring (Game.FindSpawnPoint below). fat_halo_ring / fat_halo_go stay for other worlds.
        internal const string HaloFlatWorldName = "HaloFlat";
        internal const float HaloFlatWorldRadius = 5400f, HaloFlatWorldEdge = 5600f;
        // 0.2.1039: band y -50..2,950, wholly under vanilla's 3,000 m "interior" line (Character.InInterior: above it the overworld
        // render group switches off, as in a dungeon); it was y 200..3,200, whose top 200 m crossed that line.
        internal const string HaloFlatKeyValue = "0,1450,0,0,1,0,4775,1500,4,180";   // R 4,775 m (30 km), band y -50..2,950
        private static bool s_haloFlatChecked;

        internal static bool IsHaloFlatWorld(string worldName) => string.Equals(worldName, HaloFlatWorldName, StringComparison.OrdinalIgnoreCase);

        // ── RING ONLY (0.2.1036; Fire 10-24: "the entire world was meant to be nothing but this ring"; "i like the idea of being
        // able to have both, but this specific world was supposed to be the halo ring"). On the HaloFlat world the ring is the
        // whole world: the flat field's ground drops to RingOnlyGround (FatSuperFlat.HeightAt - never built, the streamer builds
        // ring chunks only), no grid, no ocean (zone sea surfaces + volumes off, our water sweep on), and a body that leaves the
        // ring's field goes back onto the ring (RescueOffRing). fat_halo_arc / fat_halo_ring over a normal world are unchanged.
        // Decided from the world's NAME + super flat profile, which both peers know when the generator initialises (before the
        // ring itself arrives by RPC), so the flat field bakes the right ground.
        internal const float RingOnlyGround = -2000f;
        // 0.2.1052 ([wishbone]'s off-ring audit): cached per world name + profile, so a hot read is two compares, no string compare.
        private static string s_ringOnlyFor; private static bool s_ringOnlyFlat, s_ringOnly;
        internal static bool RingOnly
        {
            get
            {
                string w = FiresWorldProfile.WorldName; bool flat = FiresWorldProfile.CurrentIsSuperFlat;
                if (!ReferenceEquals(w, s_ringOnlyFor) || flat != s_ringOnlyFlat)
                {
                    s_ringOnlyFor = w; s_ringOnlyFlat = flat;
                    s_ringOnly = flat && IsHaloFlatWorld(w);
                }
                return s_ringOnly;
            }
        }

        private static float s_lastRingRescueLog = -100f;

        // 0.2.1040: the voxel shader's ring frame (FiresVoxelHeightmap: _FiresHaloOn / _FiresHaloAxis / _FiresHaloDir) and the
        // biome field's frame + seed (FatHaloBiome), pushed when the ring changes.
        private static readonly int HaloOnId = Shader.PropertyToID("_FiresHaloOn");
        private static readonly int HaloAxisId = Shader.PropertyToID("_FiresHaloAxis");
        private static readonly int HaloDirId = Shader.PropertyToID("_FiresHaloDir");
        private static string s_pushedFor = "\u0001";

        private static void PushShaderGlobals()
        {
            string key = s_has ? s_lastRaw : null;
            int seed = ZNet.World != null ? ZNet.World.m_seed : 0;
            if (key == s_pushedFor && (key == null || seed == s_pushedSeed)) return;
            s_pushedFor = key; s_pushedSeed = seed;
            if (!s_has)
            {
                Shader.SetGlobalFloat(HaloOnId, 0f);
                FatHaloBiome.Set(false, default, default, 0f, 0f, default, default, 0);
                return;
            }
            Shader.SetGlobalFloat(HaloOnId, 1f);
            Shader.SetGlobalVector(HaloAxisId, new Vector4(s_arc.O.x, s_arc.O.y, s_arc.O.z, s_arc.R));
            Shader.SetGlobalVector(HaloDirId, new Vector4(s_arc.D.x, s_arc.D.y, s_arc.D.z, 0f));
            FatHaloBiome.Set(true, s_arc.O, s_arc.D, s_arc.R, s_arc.W, s_ref, s_ref2, seed);
            FiresLogger.LogInfo($"[FatHalo] ring look on: shader ring frame (axis {s_arc.O}, R {s_arc.R:F0}) + biome patches (seed {seed})");
        }

        private static int s_pushedSeed;
        private static float s_nextSurfaceLog;

        // 0.2.1039 PROOF, no probe needed (Fire 10-24: "i feel like im just floating in the middle of it" - standing on the
        // wall, on ground True, but nothing drawn): every 10 s while the local player is in the ring's field, what is under
        // him along the ring's down - the collider hit, the voxel chunk view that owns that point and every input of its
        // renderer's visibility. Debug only.
        private static void LogStandingSurface()
        {
            var pl = Player.m_localPlayer;
            if (pl == null || !s_has || !FiresLogger.DebugEnabled || Time.unscaledTime < s_nextSurfaceLog) return;
            s_nextSurfaceLog = Time.unscaledTime + 10f;
            Vector3 p = pl.transform.position + Vector3.zero;
            if (!TryGetUp(p, out Vector3 up)) return;
            Vector3 from = p + up * 1f;
            string hitText = "NO collider within 6 m along the ring's down";
            Vector3 probe = p - up * 1f;
            if (Physics.Raycast(from, -up, out RaycastHit hit, 6f, LayerMask.GetMask("terrain", "piece", "static_solid", "Default"), QueryTriggerInteraction.Ignore))
            {
                hitText = $"collider '{hit.collider.name}' (layer {LayerMask.LayerToName(hit.collider.gameObject.layer)}) {hit.distance - 1f:F2} m under the feet";
                probe = hit.point - up * 0.5f;
            }
            var key = new Vector3Int(Mathf.FloorToInt(probe.x / 32f), Mathf.FloorToInt(probe.y / 32f), Mathf.FloorToInt(probe.z / 32f));
            string view = VoxelWorld.TryGetView(key, out var v) && v != null ? v.RenderState() : "NO view";
            var cam = Camera.main;
            string camText = cam != null ? $"camera at ({cam.transform.position.x:F0},{cam.transform.position.y:F0},{cam.transform.position.z:F0}) far {cam.farClipPlane:F0} cullingMask has terrain {(cam.cullingMask & (1 << LayerMask.NameToLayer("terrain"))) != 0}" : "no camera";
            FiresLogger.LogDebug($"[FatHalo] standing surface: player ({p.x:F1},{p.y:F1},{p.z:F1}) up ({up.x:F2},{up.y:F2},{up.z:F2}); {hitText}; chunk {key}: {view}; {camText}; fog {(RenderSettings.fog ? RenderSettings.fogMode + " density " + RenderSettings.fogDensity.ToString("F4") : "off")}");
        }

        // The ring's footprint for the map (FatHaloMap): the axis point and the solid's radial reach (rim lips to outer face).
        // Before the ring arrives by RPC (the map can build first), HaloFlat's own default ring.
        // The strip map's frame (FatHaloMap): the axis point, the inside radius and the half-length along the axis.
        internal static void MapStrip(out Vector3 origin, out float radius, out float halfLength)
        {
            if (s_has && s_arc.Vertical) { origin = s_arc.O; radius = s_arc.R; halfLength = s_arc.W; return; }
            TryParse(HaloFlatKeyValue, out var a);
            origin = a.O; radius = a.R; halfLength = a.W;
        }

        internal static void MapRing(out Vector3 origin, out float inner, out float outer)
        {
            if (s_has && s_arc.Vertical) { origin = s_arc.O; inner = s_arc.R - RimHeight; outer = s_arc.R + s_arc.T; return; }
            TryParse(HaloFlatKeyValue, out var a);
            origin = a.O; inner = a.R - RimHeight; outer = a.R + a.T;
        }

        // A body off the ring's field (through the shell, off a band edge, far inside): onto the inside face at its own angle,
        // its axis position clamped into the band, 1.5 m in from the face. Only when it is really off the field.
        internal static void RescueOffRing(Character c, Vector3 p)
        {
            if (c == null || !s_has || TryGetUp(p, out _)) return;
            var a = s_arc;
            Frame(p, out float t, out float r, out _, out Vector3 radial);
            Vector3 outward = r > 0.01f ? radial / r : s_ref;
            float tc = Mathf.Clamp(t, -a.W + RimThickness + 2f, a.W - RimThickness - 2f);
            Vector3 to = a.O + a.D * tc + outward * (a.R - 1.5f);
            c.transform.position = to;
            var body = c.GetComponent<Rigidbody>();
            if (body != null) { body.position = to; body.linearVelocity = Vector3.zero; }
            if (Time.unscaledTime - s_lastRingRescueLog > 5f)
            {
                s_lastRingRescueLog = Time.unscaledTime;
                FiresLogger.LogInfo($"[FatHalo] ring rescue: {c.m_name} was off the ring at ({p.x:0},{p.y:0},{p.z:0}) r {r:0}; put back on the inside face at ({to.x:0},{to.y:0},{to.z:0})");
            }
        }

        private static void EnsureHaloFlatKey() => ServerLoad();

        [HarmonyLib.HarmonyPatch(typeof(Game), "FindSpawnPoint")]
        private static class Game_FindSpawnPoint_Halo
        {

            // Runs vanilla for a logout point (not after death) and for a bed; otherwise, on a world with a full ring, the ring
            // spawn instead of the start temple at the origin (which on HaloFlat is 4.8 km from the ring, on the flat floor).
            private static bool Prefix(ref Vector3 point, ref bool usedLogoutPoint, ref bool __result, bool ___m_respawnAfterDeath, PlayerProfile ___m_playerProfile)
            {
                if (!s_has || s_arc.A < 180f || ___m_playerProfile == null) return true;
                if (ZNet.instance == null || !IsHaloFlatWorld(ZNet.instance.GetWorldName())) return true;
                if (!___m_respawnAfterDeath && ___m_playerProfile.HaveLogoutPoint()) return true;
                if (___m_playerProfile.HaveCustomSpawnPoint()) return true;
                if (!TryGetSpawn(out Vector3 at, out Vector3 up)) return true;
                usedLogoutPoint = false;
                point = at + up * 1.5f;
                ZNet.instance.SetReferencePosition(point);
                // 0.2.1032 (Fire 10-24 HaloFlat: spawned at 04:29, then fell 1,650 m to the floor - "void rescue ... (5001, 49, 0)"):
                // the Y-band gate only armed once the player existed, so the wall chunk under the spawn was not built yet. Now the
                // spawn point is a gate focus from the first ask, and the spawn waits until a collider stands behind it along the
                // ring's down (SpawnWallReach), with a SpawnTimeout after which it spawns anyway and says so.
                if (s_spawnWaitSince < 0f)
                {
                    s_spawnWaitSince = Time.unscaledTime;
                    s_spawnFocus = point; s_spawnFocusOn = true;
                    VoxelZoneStreamer.MarkAreaDirty(point, 160f);
                    FiresLogger.LogDebug($"[FatHalo] spawn waits for the wall at ({point.x:F0},{point.y:F0},{point.z:F0}): the Y-band gate is armed there");
                }
                bool area = ZNetScene.instance != null && ZNetScene.instance.IsAreaReady(point);
                if (s_spawnMask == 0) s_spawnMask = LayerMask.GetMask("terrain", "piece", "static_solid", "Default");
                bool wall = Physics.Raycast(point, -up, out RaycastHit hit, SpawnWallReach, s_spawnMask, QueryTriggerInteraction.Ignore);
                float waited = Time.unscaledTime - s_spawnWaitSince;
                bool timedOut = waited > SpawnTimeout;
                __result = area && (wall || timedOut);
                if (__result)
                {
                    if (wall) point = hit.point + up * 0.25f;
                    FiresLogger.LogDebug(wall
                        ? $"[FatHalo] HaloFlat spawn on the ring at ({point.x:F0},{point.y:F0},{point.z:F0}): wall found after {waited:F1} s"
                        : $"[FatHalo] HaloFlat spawn at ({point.x:F0},{point.y:F0},{point.z:F0}) WITHOUT a wall under it: timed out after {waited:F0} s (no collider within {SpawnWallReach:F0} m behind the spawn)");
                    s_spawnWaitSince = -1f;
                    s_spawnFocusOn = false;
                }
                return false;
            }

            private const float SpawnWallReach = 6f, SpawnTimeout = 60f;
            private static float s_spawnWaitSince = -1f;
            private static int s_spawnMask;
        }

        // ── Step B: the Y-BAND GATE (0.2.1030). A vertical ring's wall column is 3 km tall (~94 chunks); only the chunks within
        // GateReach metres (along the axis) of a player standing near that column are built Full, the rest stay UniformAir
        // placeholders. Focus points = the local player, and on a server every peer's reference position. When a focus moves
        // GateRearm metres along the axis, its area re-arms so the band follows. Captured on the main thread; the column builder
        // reads the snapshot.
        internal const float GateReach = 256f, GateRearm = 96f, GateNear = 400f;
        private static volatile Vector3[] s_focus = new Vector3[0];
        private static Vector3 s_spawnFocus;
        private static bool s_spawnFocusOn;
        private static Vector3[] s_armedAt = new Vector3[0];

        private static void UpdateFocus()
        {
            if (!s_has || !s_arc.Vertical) { if (s_focus.Length > 0) s_focus = new Vector3[0]; return; }
            var list = new System.Collections.Generic.List<Vector3>();
            if (Player.m_localPlayer != null) list.Add(Player.m_localPlayer.transform.position);
            if (s_spawnFocusOn) list.Add(s_spawnFocus);   // 0.2.1032: the spawn point builds before the player exists
            if (ZNet.instance != null && ZNet.instance.IsServer())
                foreach (var peer in ZNet.instance.GetPeers()) if (peer != null) list.Add(peer.m_refPos);
            var now = list.ToArray();
            for (int i = 0; i < now.Length; i++)
            {
                bool near = false;
                foreach (var a in s_armedAt)
                    if (Mathf.Abs(a.y - now[i].y) < GateRearm && new Vector2(a.x - now[i].x, a.z - now[i].z).sqrMagnitude < GateRearm * GateRearm) { near = true; break; }
                if (near || !TryGetUp(now[i], out _)) continue;
                VoxelZoneStreamer.MarkAreaDirty(now[i], 160f);
                FiresLogger.LogDebug($"[FatHalo] Y-band gate follows a player to ({now[i].x:F0},{now[i].y:F0},{now[i].z:F0})");
            }
            s_armedAt = now;
            s_focus = now;
        }

        // Should this ring chunk (origin o) be built now? Always true off a vertical ring or with no focus (nothing to gate on).
        internal static bool GateAllows(Vector3 o)
        {
            if (!s_arc.Vertical) return true;
            var focus = s_focus;
            if (focus.Length == 0) return true;
            float cy = o.y + 16f, cx = o.x + 16f, cz = o.z + 16f;
            foreach (var f in focus)
                if (Mathf.Abs(f.y - cy) <= GateReach + 16f && new Vector2(f.x - cx, f.z - cz).sqrMagnitude <= GateNear * GateNear) return true;
            return false;
        }

        private static bool Refresh()
        {
            string raw = null;
            try
            {
                raw = s_stateRaw;
            }
            catch { raw = null; }
            if (raw == s_lastRaw) return false;
            s_lastRaw = raw;
            s_has = raw != null && TryParse(raw, out s_arc);
            if (s_has)
            {
                s_ref = s_arc.Vertical ? Vector3.right : Vector3.ProjectOnPlane(Vector3.down, s_arc.D).normalized;
                s_ref2 = Vector3.Cross(s_arc.D, s_ref);
            }
            FiresLogger.LogInfo(s_has
                ? $"[FatHalo] HaloActive true (key from {s_stateSource}) - ring ON: axis ({s_arc.O.x:F0},{s_arc.O.y:F0},{s_arc.O.z:F0}) dir ({s_arc.D.x:F0},{s_arc.D.y:F0},{s_arc.D.z:F0}), R {s_arc.R:F0} m, half-length {s_arc.W:F0} m, shell {s_arc.T:F0} m, {(s_arc.A >= 180f ? "full ring" : "±" + s_arc.A.ToString("F0", CultureInfo.InvariantCulture) + "°")}"
                : raw != null ? $"[FatHalo] ring key '{raw}' did not parse - ring OFF" : "[FatHalo] ring OFF");
            return true;
        }

        private static bool TryParse(string raw, out Arc a)
        {
            a = default;
            var p = raw.Split(',');
            if (p.Length != 7 && p.Length != 10) return false;
            var f = new float[p.Length];
            for (int i = 0; i < p.Length; i++) if (!float.TryParse(p[i], NumberStyles.Float, CultureInfo.InvariantCulture, out f[i])) return false;
            if (p.Length == 7)   // 0.2.1027: cx,cy,cz,r,w,t,a with the axis along X
                a = new Arc { O = new Vector3(f[0], f[1], f[2]), D = Vector3.right, R = f[3], W = f[4], T = f[5], A = f[6] };
            else
                a = new Arc { O = new Vector3(f[0], f[1], f[2]), D = new Vector3(f[3], f[4], f[5]).normalized, R = f[6], W = f[7], T = f[8], A = f[9] };
            a.A = Mathf.Clamp(a.A, 1f, 180f);
            bool aligned = Mathf.Abs(a.D.y) > 0.999f || Mathf.Abs(a.D.x) > 0.999f;
            return aligned && a.R > 4f && a.W > 1f && a.T > 0.5f;
        }

        // The point in the ring's frame: t along the axis, r from the axis, the angle (degrees) from the reference direction.
        private static void Frame(Vector3 p, out float t, out float r, out float ang, out Vector3 radial)
        {
            Vector3 v = p - s_arc.O;
            t = Vector3.Dot(v, s_arc.D);
            radial = v - t * s_arc.D;
            r = radial.magnitude;
            ang = s_arc.A >= 180f ? 0f : Mathf.Atan2(Vector3.Dot(radial, s_ref2), Vector3.Dot(radial, s_ref)) * Mathf.Rad2Deg;
        }

        // Signed density in metres (positive = solid), or < -100 far outside.
        internal static float Sdf(float wx, float wy, float wz)
        {
            if (!s_has) return -1000f;
            var a = s_arc;
            Frame(new Vector3(wx, wy, wz), out float t, out float r, out float ang, out _);
            float around = a.A >= 180f ? 1000f : (a.A - Mathf.Abs(ang)) * Mathf.Deg2Rad * Mathf.Max(r, 1f);
            float shell = a.T * 0.5f - Mathf.Abs(r - (a.R + a.T * 0.5f));
            float body = Mathf.Min(shell, a.W - Mathf.Abs(t));
            // The rim lips: RimThickness wide at both band edges, from the outer face inward RimHeight + T.
            float lip = Mathf.Min(Mathf.Min(r - (a.R - RimHeight), a.R + a.T - r), Mathf.Min(a.W - Mathf.Abs(t), Mathf.Abs(t) - (a.W - RimThickness)));
            return Mathf.Min(Mathf.Max(body, lip), around);
        }

        // Radial reach of the solid (lips included), for the band and overlap tests.
        private static float Inner => s_arc.R - RimHeight - 1f;
        private static float Outer => s_arc.R + s_arc.T + 1f;

        // The ring's vertical band over a column rect (for the streamer's chunk range).
        internal static bool TryGetBand(float minX, float minZ, float maxX, float maxZ, out float bot, out float top)
        {
            bot = top = 0f;
            if (!s_has) return false;
            var a = s_arc;
            if (a.Vertical)
            {
                RectDistances(minX, minZ, maxX, maxZ, a.O.x, a.O.z, out float near, out float far);
                if (near > Outer || far < Inner) return false;
                if (a.A < 180f && !RectInArc(minX, minZ, maxX, maxZ)) return false;
                bot = a.O.y - a.W - 1f;
                top = a.O.y + a.W + 1f;
                return true;
            }
            // X axis: the rect's x range against the band, its z range against the ring's reach; the y range from the arc.
            if (maxX < a.O.x - a.W - 1f || minX > a.O.x + a.W + 1f) return false;
            float zReach = a.A >= 90f ? Outer : Outer * Mathf.Sin(a.A * Mathf.Deg2Rad) + 1f;
            if (maxZ < a.O.z - zReach || minZ > a.O.z + zReach) return false;
            bot = a.O.y - Outer;
            top = a.A >= 180f ? a.O.y + Outer : a.O.y - Inner * Mathf.Cos(Mathf.Min(a.A, 180f) * Mathf.Deg2Rad) + a.T + 1f;
            if (a.A > 90f) top = Mathf.Max(top, a.O.y + Outer * -Mathf.Cos(a.A * Mathf.Deg2Rad));
            return true;
        }

        // Does a 32 m chunk's box cut the shell (its distance-from-axis range overlaps the solid's radial reach, its axis range
        // the band)?
        internal static bool ChunkOverlaps(Vector3 o)
        {
            if (!TryGetBand(o.x, o.z, o.x + 32f, o.z + 32f, out float bot, out float top)) return false;
            if (o.y + 33f < bot || o.y - 1f > top) return false;
            var a = s_arc;
            float near, far;
            if (a.Vertical) RectDistances(o.x - 1f, o.z - 1f, o.x + 33f, o.z + 33f, a.O.x, a.O.z, out near, out far);
            else RectDistances(o.y - 1f, o.z - 1f, o.y + 33f, o.z + 33f, a.O.y, a.O.z, out near, out far);
            return near <= Outer && far >= Inner;
        }

        // The nearest and farthest distance from (cx, cy) to an axis-aligned rect.
        private static void RectDistances(float minA, float minB, float maxA, float maxB, float ca, float cb, out float near, out float far)
        {
            float na = Mathf.Clamp(ca, minA, maxA) - ca, nb = Mathf.Clamp(cb, minB, maxB) - cb;
            near = Mathf.Sqrt(na * na + nb * nb);
            float fa = Mathf.Max(Mathf.Abs(minA - ca), Mathf.Abs(maxA - ca)), fb = Mathf.Max(Mathf.Abs(minB - cb), Mathf.Abs(maxB - cb));
            far = Mathf.Sqrt(fa * fa + fb * fb);
        }

        // A vertical partial arc: any rect corner or the centre within the arc (+ a margin). Conservative.
        private static bool RectInArc(float minX, float minZ, float maxX, float maxZ)
        {
            float margin = s_arc.A + 2f;
            for (int i = 0; i < 5; i++)
            {
                float x = i == 4 ? (minX + maxX) * 0.5f : (i & 1) == 0 ? minX : maxX;
                float z = i == 4 ? (minZ + maxZ) * 0.5f : (i & 2) == 0 ? minZ : maxZ;
                Frame(new Vector3(x, s_arc.O.y, z), out _, out _, out float ang, out _);
                if (Mathf.Abs(ang) <= margin) return true;
            }
            return false;
        }

        internal static uint Signature() => s_has ? unchecked((uint)s_lastRaw.GetHashCode() * 2654435761u) : 0u;

        // [visual]'s provider contract (FatApi.HaloTryGetUp): up ONLY inside the ring's gravity field, never world-wide. The field:
        // within the half-length along the axis (+2 m), within the arc (+2°), and radially from R - 30 m (standing room inside the
        // shell) to R + T + 1 m. Up = unit vector from the point toward the closest axis point.
        private const float FieldInside = 30f, FieldOutside = 1f, FieldAlong = 2f, FieldDegrees = 2f;

        internal static bool TryGetUp(Vector3 p, out Vector3 up)
        {
            up = Vector3.up;
            if (!s_has) return false;
            var a = s_arc;
            Frame(p, out float t, out float r, out float ang, out Vector3 radial);
            if (Mathf.Abs(t) > a.W + FieldAlong) return false;
            // 0.2.1040 (Fire: the ring's up should hold "all the way up to the rings center"): the whole interior, r 0.5 m to the shell.
            if (r > a.R + a.T + FieldOutside || r < 0.5f) return false;
            if (a.A < 180f && Mathf.Abs(ang) > a.A + FieldDegrees) return false;
            up = -radial / r;
            return true;
        }

        internal static bool Axis(out Vector3 origin, out Vector3 dir, out float radius)
        {
            origin = Vector3.zero; dir = Vector3.up; radius = 0f;
            if (!s_has) return false;
            origin = s_arc.O; dir = s_arc.D; radius = s_arc.R;
            return true;
        }

        internal static bool Band(out float halfWidth, out float halfArcDeg, out float thickness)
        {
            halfWidth = halfArcDeg = thickness = 0f;
            if (!s_has) return false;
            halfWidth = s_arc.W; halfArcDeg = s_arc.A; thickness = s_arc.T;
            return true;
        }

        internal static Vector3 UpAt(Vector3 p) => TryGetUp(p, out var up) ? up : Vector3.up;

        // The spawn on the ring: the inside face at the band middle on the reference side (+X for HaloFlat), 1 m in.
        internal static bool TryGetSpawn(out Vector3 at, out Vector3 up)
        {
            at = Vector3.zero; up = Vector3.up;
            if (!s_has) return false;
            at = s_arc.O + s_ref * (s_arc.R - 1f);
            up = -s_ref;
            return true;
        }

        private static void SetKey(string raw) => RequestSet(raw);

        // ── THE RING'S STATE (0.2.1030). Was a vanilla global key; Fire's live test (10-24) proved another mod on the rig
        // intercepts SetGlobalKey ("[Progression] Blocking global key 'fat_halo_arc ...' - redirecting to private keys"), so the
        // key never reached the world and HaloActive stayed false. The ring is now FAT's own server-owned state:
        //   server: FiresWorldGen\firesgen_<world>.halo (one line, the ring value; HaloFlat writes its default on first start),
        //           broadcast to every peer on change and sent to each client that asks;
        //   client: asks on connect (and every 5 s until answered); an admin command sends the new value to the server, which
        //           checks the sender is an admin, caps the size, parses it, saves and broadcasts. A host sets it directly.
        private const string RpcSet = "FatHalo_Set", RpcState = "FatHalo_State", RpcAsk = "FatHalo_Ask";
        private const int MaxRaw = 200;
        private static string s_stateRaw, s_stateSource = "none";
        private static ZRoutedRpc s_rpcFor;
        private static bool s_serverLoaded, s_answered;
        private static float s_nextAsk;

        private static void EnsureRpc()
        {
            var rpc = ZRoutedRpc.instance;
            if (rpc == null || ReferenceEquals(rpc, s_rpcFor)) return;
            s_rpcFor = rpc;   // one registration per ZRoutedRpc instance (a new connection makes a new one)
            s_serverLoaded = false; s_answered = false; s_nextAsk = 0f; s_stateRaw = null; s_stateSource = "none";
            if (ZNet.instance != null && ZNet.instance.IsServer())
            {
                rpc.Register<string>(RpcSet, RPC_Set);
                rpc.Register(RpcAsk, RPC_Ask);
            }
            else rpc.Register<string>(RpcState, RPC_State);
        }

        private static string HaloPath(string world) => FiresWorldRecords.PathFor(world, ".halo");

        private static void ServerLoad()
        {
            EnsureRpc();
            if (s_serverLoaded || ZNet.instance == null || !ZNet.instance.IsServer()) { AskIfClient(); return; }
            string world = ZNet.instance.GetWorldName();
            if (string.IsNullOrEmpty(world)) return;
            s_serverLoaded = true;
            string raw = null;
            try { string path = HaloPath(world); if (System.IO.File.Exists(path)) raw = System.IO.File.ReadAllText(path).Trim(); }
            catch (Exception ex) { FiresLogger.LogWarning($"[FatHalo] reading the ring file threw: {ex.Message}"); }
            if (string.IsNullOrEmpty(raw) && IsHaloFlatWorld(world))
            {
                raw = HaloFlatKeyValue;
                Save(world, raw);
                FiresLogger.LogInfo($"[FatHalo] HaloFlat world: first start - ring written ({raw})");
            }
            s_stateRaw = string.IsNullOrEmpty(raw) ? null : raw;
            s_stateSource = "server file";
        }

        private static void Save(string world, string raw)
        {
            try
            {
                string path = HaloPath(world);
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                if (string.IsNullOrEmpty(raw)) { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); }
                else System.IO.File.WriteAllText(path, raw);
            }
            catch (Exception ex) { FiresLogger.LogWarning($"[FatHalo] saving the ring file threw: {ex.Message}"); }
        }

        private static void AskIfClient()
        {
            if (s_answered || ZNet.instance == null || ZNet.instance.IsServer() || ZRoutedRpc.instance == null) return;
            if (ZNet.instance.GetServerPeer() == null || Time.unscaledTime < s_nextAsk) return;
            s_nextAsk = Time.unscaledTime + 5f;
            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), RpcAsk);
        }

        // Admin command path. A host/server applies directly; a client asks the server.
        private static void RequestSet(string raw)
        {
            EnsureRpc();
            if (ZNet.instance == null || ZRoutedRpc.instance == null) return;
            if (ZNet.instance.IsServer()) { ApplyOnServer(raw, "local admin"); return; }
            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), RpcSet, raw ?? "");
        }

        private static void ApplyOnServer(string raw, string who)
        {
            raw = (raw ?? "").Trim();
            if (raw.Length > MaxRaw) { FiresLogger.LogWarning($"[FatHalo] ring value from {who} refused: {raw.Length} chars (cap {MaxRaw})"); return; }
            if (raw.Length > 0 && !TryParse(raw, out _)) { FiresLogger.LogWarning($"[FatHalo] ring value from {who} refused: '{raw}' does not parse"); return; }
            s_stateRaw = raw.Length == 0 ? null : raw;
            s_stateSource = who;
            string world = ZNet.instance.GetWorldName();
            if (!string.IsNullOrEmpty(world)) Save(world, s_stateRaw);
            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, RpcState, s_stateRaw ?? "");
            FiresLogger.LogInfo($"[FatHalo] ring set by {who}: {(s_stateRaw ?? "removed")}; sent to every peer");
            Tick();
        }

        // SERVER: only an admin may change the ring (sender checked, size capped, value parsed).
        private static void RPC_Set(long sender, string raw)
        {
            if (!VerdantsAscent.Terrain.Lifecycle.AdminCheck.IsAdmin(sender)) { FiresLogger.LogWarning($"[FatHalo] ring change from non-admin peer {sender} refused"); return; }
            ApplyOnServer(raw, $"admin peer {sender}");
        }

        // SERVER: a client asks for the current ring.
        private static void RPC_Ask(long sender)
            => ZRoutedRpc.instance.InvokeRoutedRPC(sender, RpcState, s_stateRaw ?? "");

        // CLIENT: the server's ring (only the server may send it).
        private static void RPC_State(long sender, string raw)
        {
            if (ZRoutedRpc.instance == null || sender != ZRoutedRpc.instance.GetServerPeerID()) return;
            if (raw != null && raw.Length > MaxRaw) return;
            s_answered = true;
            s_stateRaw = string.IsNullOrEmpty(raw) ? null : raw;
            s_stateSource = "server";
            FiresLogger.LogDebug($"[FatHalo] ring from server: {(s_stateRaw ?? "none")}");
            Tick();
        }

        private static float Arg(Terminal.ConsoleEventArgs args, int i, float def, float min, float max)
            => args.Length > i && float.TryParse(args[i], NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? Mathf.Clamp(v, min, max) : def;

        private static bool Ready(Terminal.ConsoleEventArgs args)
        {
            if (ZoneSystem.instance == null || Player.m_localPlayer == null) { args.Context?.AddString("[FatHalo] no world"); return false; }
            if (!VerdantsAscent.FatApi.IsVoxelWorld()) { args.Context?.AddString("[FatHalo] needs a FAT voxel world"); return false; }
            return true;
        }

        internal static void RegisterCommand()
        {
            new Terminal.ConsoleCommand(Key, "[radius] [halfwidth] [degrees] | off - (admin) halo spike: a voxel ring arc above you, axis along X",
                args =>
                {
                    if (!Ready(args)) return;
                    if (args.Length > 1 && args[1].Equals("off", StringComparison.OrdinalIgnoreCase))
                    {
                        RequestSet("");
                        args.Context?.AddString("[FatHalo] ring removed");
                        return;
                    }
                    float r = Arg(args, 1, 300f, 20f, 600f), w = Arg(args, 2, 50f, 4f, 200f), deg = Arg(args, 3, 30f, 2f, 90f);
                    Vector3 p = Player.m_localPlayer.transform.position;
                    SetKey(string.Format(CultureInfo.InvariantCulture, "{0},{1},{2},1,0,0,{3},{4},4,{5}", Mathf.Round(p.x), Mathf.Round(p.y + 3f + r), Mathf.Round(p.z), r, w, deg));
                    args.Context?.AddString($"[FatHalo] arc set: R {r:F0} m, half-width {w:F0} m, ±{deg:F0}°, floor 3 m above you; loaded zones regenerate in place");
                }, isCheat: true);

            // HaloFlat: the full-size ring, once per world (the key persists with the world). Fire 10-24: 30 km around, 3 km wide.
            new Terminal.ConsoleCommand("fat_halo_ring", "[radius=4775] [width=3000] [bottomY=-50] - (admin) HaloFlat: a full vertical-axis ring world shell around the origin",
                args =>
                {
                    if (!Ready(args)) return;
                    float r = Arg(args, 1, 4775f, 100f, 9000f), width = Arg(args, 2, 3000f, 20f, 6000f), bottom = Arg(args, 3, -50f, -1000f, 4000f);
                    SetKey(string.Format(CultureInfo.InvariantCulture, "0,{0},0,0,1,0,{1},{2},4,180", bottom + width * 0.5f, r, width * 0.5f));
                    args.Context?.AddString($"[FatHalo] HaloFlat ring set: R {r:F0} m ({2f * Mathf.PI * r / 1000f:F1} km around), band y {bottom:F0}..{bottom + width:F0}. fat_halo_go takes you to the spawn on the wall.");
                }, isCheat: true);

            // 0.2.1035 (HaloFlat: the spawn timed out with no wall collider though the band's chunks were classified Full):
            // read-only. For points inside the shell beside you (or the spawn when no ring point is near), what each layer
            // says: the ring SDF, the chunk (exists / kind / generating / version), its density there, its view (mesh
            // vertices, collider ready, collider enabled) and a raycast along the ring's down.
            new Terminal.ConsoleCommand("fat_halo_probe", "- (read-only) why the ring wall here is or isn't solid: SDF, chunk, density, mesh, collider, ray",
                args =>
                {
                    if (!Ready(args)) return;
                    var lines = new System.Collections.Generic.List<string>();
                    if (!s_has) { args.Context?.AddString("[FatHalo] probe: no ring"); FiresLogger.LogInfo("[FatHalo] probe: no ring"); return; }
                    Vector3 me = Player.m_localPlayer.transform.position;
                    Frame(me, out float tMe, out float rMe, out _, out Vector3 radialMe);
                    Vector3 outward = rMe > 0.01f ? radialMe / rMe : s_ref;
                    Vector3 basePoint = (rMe > s_arc.R - 200f && rMe < s_arc.R + 200f) ? me : (s_arc.O + s_ref * s_arc.R);
                    Frame(basePoint, out float tB, out _, out _, out Vector3 radialB);
                    Vector3 outB = radialB.sqrMagnitude > 0.01f ? radialB.normalized : s_ref;
                    Vector3 axisPt = s_arc.O + s_arc.D * tB;
                    lines.Add($"[FatHalo] probe: you at ({me.x:F1},{me.y:F1},{me.z:F1}) r {rMe:F1} (inside face {s_arc.R:F0}, outer {s_arc.R + s_arc.T:F0}); probing the shell at band height {axisPt.y:F0}");
                    foreach (float rr in new[] { s_arc.R + 0.5f, s_arc.R + s_arc.T * 0.5f, s_arc.R + s_arc.T - 0.5f })
                    {
                        Vector3 p = axisPt + outB * rr;
                        var key = new Vector3Int(Mathf.FloorToInt(p.x / 32f), Mathf.FloorToInt(p.y / 32f), Mathf.FloorToInt(p.z / 32f));
                        string chunk = VoxelWorld.TryGetChunk(key, out var c) && c != null
                            ? $"chunk {key} kind {c.Kind}, generating {c.PendingGenerate}, overlaps {ChunkOverlaps(c.WorldOrigin)}"
                            : $"chunk {key} NOT REGISTERED";
                        string dens = VoxelWorld.TrySampleDensitySmooth(p, out float d) ? d.ToString("F2", CultureInfo.InvariantCulture) : "no sample";
                        string view = VoxelWorld.TryGetView(key, out var v) && v != null
                            ? $"view: {v.VertexCount} verts, collider ready {v.ColliderReady}, collider {(v.GetComponent<MeshCollider>() is MeshCollider mc ? (mc.enabled ? "enabled" : "DISABLED") + (mc.sharedMesh != null ? " with mesh" : " NO MESH") : "MISSING")}, layer {LayerMask.LayerToName(v.gameObject.layer)}"
                            : "view: NONE";
                        lines.Add($"  r {rr:F1} at ({p.x:F1},{p.y:F1},{p.z:F1}): SDF {Sdf(p.x, p.y, p.z):F2}; {chunk}; density {dens}; {view}");
                    }
                    Vector3 from = axisPt + outB * (s_arc.R - 3f);
                    bool hit = Physics.Raycast(from, outB, out RaycastHit h, 12f, LayerMask.GetMask("terrain", "piece", "static_solid", "Default"), QueryTriggerInteraction.Ignore);
                    lines.Add(hit ? $"  ray outward from r {s_arc.R - 3f:F0}: HIT '{h.collider.name}' (layer {LayerMask.LayerToName(h.collider.gameObject.layer)}) at r {Vector3.Distance(new Vector3(h.point.x, axisPt.y, h.point.z), axisPt):F2}"
                                  : $"  ray outward from r {s_arc.R - 3f:F0}: NO HIT within 12 m");
                    foreach (var l in lines) { FiresLogger.LogInfo(l); args.Context?.AddString(l); }
                });

            new Terminal.ConsoleCommand("fat_halo_go", "- (admin) teleport to the ring's spawn: the inside face at the band middle",
                args =>
                {
                    if (!Ready(args)) return;
                    if (!TryGetSpawn(out Vector3 at, out Vector3 up)) { args.Context?.AddString("[FatHalo] no ring in this world (fat_halo_ring)"); return; }
                    var target = at + up * 1.5f;
                    Player.m_localPlayer.TeleportTo(target, Quaternion.LookRotation(Vector3.ProjectOnPlane(Vector3.forward, up), up), true);
                    FiresLogger.LogDebug($"[FatHalo] teleport to the ring spawn ({target.x:F0},{target.y:F0},{target.z:F0}), up ({up.x:F2},{up.y:F2},{up.z:F2})");
                    args.Context?.AddString($"[FatHalo] to the ring at ({target.x:F0},{target.y:F0},{target.z:F0}); without the gravity provider you will fall along world down");
                }, isCheat: true);
        }

        private static void Rearm(Arc a)
        {
            if (a.Vertical)
            {
                // A whole vertical ring is an annulus; re-arm only the loaded zones near the local player (the rest build fresh).
                var pl = Player.m_localPlayer;
                if (pl != null) VoxelZoneStreamer.MarkAreaDirty(pl.transform.position, 256f);
                return;
            }
            float reach = Mathf.Max(a.W, a.A >= 90f ? a.R + a.T : (a.R + a.T) * Mathf.Sin(a.A * Mathf.Deg2Rad)) + 64f;
            VoxelZoneStreamer.MarkAreaDirty(new Vector3(a.O.x, 0f, a.O.z), reach);
        }
    }
}
