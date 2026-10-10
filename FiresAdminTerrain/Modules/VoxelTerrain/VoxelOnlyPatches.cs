using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Terrain.Utilities;

namespace VerdantsAscent.Terrain.Voxel
{
    // W3-A1 — VANILLA TERRAIN NEVER EXISTS. In VoxelOnly mode on a Fires
    // world, near-zone Heightmaps keep building their DATA (biomes, vegetation
    // masks, minimap, paint — all fed by our generator anyway) but their
    // render mesh and collider are never built. Voxels are the only physical
    // and visual ground from the first frame: nothing to hide, nothing to
    // swap, no z-fight window, no frontier to tear. Distant-LOD heightmaps
    // stay alive until the A4 far-quilt replaces them (render-only, no
    // colliders, far away).
    internal static class VoxelOnlyMode
    {
        public static bool Active =>
            FiresVoxelConfig.Enabled != null && FiresVoxelConfig.Enabled.Value
            && FiresVoxelConfig.WorldKind == VoxelWorldMode.VoxelOnly
            && FiresWorldGenBase.Active;

        internal static readonly AccessTools.FieldRef<Heightmap, bool> IsDistantLod =
            AccessTools.FieldRefAccess<Heightmap, bool>("m_isDistantLod");
    }

    // Vanilla's pickaxe/terrain-hit path (Attack.DoMeleeAttack) requires the
    // HIT COLLIDER's own GameObject to carry a Heightmap component — voxel
    // chunk colliders have none, so every swing on voxel ground resolved as
    // "not terrain" and the dig op never spawned. Swap that one lookup for a
    // resolver that answers the ZONE's real heightmap when the hit is a
    // voxel chunk: the spawned dig/raise op then runs the normal data path
    // (TerrainComp → Regenerate → zone rewrap → voxel update).
    internal static class VoxelHeightmapResolver
    {
        // Chunk view GOs all sit at world (0,0,0) — mesh verts are world-space
        // — so transform.position resolves the MAP-CENTER zone (or null) for
        // every chunk. The collider/renderer world-space bounds center is the
        // chunk's real location; a 32m chunk column lies inside exactly one
        // 64m zone, so this always names the right heightmap.
        public static Heightmap FromChunkGO(GameObject go)
        {
            var col = go.GetComponent<Collider>();
            if (col != null) return Heightmap.FindHeightmap(col.bounds.center);
            var rend = go.GetComponent<Renderer>();
            return rend != null ? Heightmap.FindHeightmap(rend.bounds.center) : null;
        }
    }

    [HarmonyPatch(typeof(Attack), "DoMeleeAttack")]
    internal static class Patch_Attack_DoMeleeAttack_VoxelTerrain
    {
        public static Heightmap Resolve(GameObject go)
        {
            if (go == null) return null;
            var hm = go.GetComponent<Heightmap>();
            if (hm != null) return hm;
            if (go.GetComponent<VoxelChunkView>() == null) return null;
            return VoxelHeightmapResolver.FromChunkGO(go);
        }

        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var resolve = AccessTools.Method(typeof(Patch_Attack_DoMeleeAttack_VoxelTerrain), nameof(Resolve));
            int swapped = 0;
            foreach (var ins in instructions)
            {
                if ((ins.opcode == System.Reflection.Emit.OpCodes.Callvirt
                        || ins.opcode == System.Reflection.Emit.OpCodes.Call)
                    && ins.operand is System.Reflection.MethodInfo mi
                    && mi.Name == "GetComponent" && mi.IsGenericMethod
                    && mi.DeclaringType == typeof(GameObject)
                    && mi.GetGenericArguments().Length == 1
                    && mi.GetGenericArguments()[0] == typeof(Heightmap))
                {
                    yield return new CodeInstruction(System.Reflection.Emit.OpCodes.Call, resolve)
                    { labels = ins.labels, blocks = ins.blocks };
                    swapped++;
                    continue;
                }
                yield return ins;
            }
            // Harmony re-runs transpilers whenever another mod patches or
            // unpatches the method; report a result only when it changes.
            if (swapped == s_loggedSites) yield break;
            s_loggedSites = swapped;
            if (swapped == 0)
                FiresLogger.LogWarning("[FiresVoxel] Attack.DoMeleeAttack terrain-lookup transpiler matched NO call sites — pickaxe-on-voxel stays inert (game update changed the method?).");
            else
                FiresLogger.LogInfo($"[FiresVoxel] Attack.DoMeleeAttack terrain lookup shimmed ({swapped} site(s)) — pickaxe/hoe hits on voxel chunks resolve the zone heightmap.");
        }

        private static int s_loggedSites = -1;
    }

    // Pickaxe dig = a true 3D dig, not vanilla's column drop. Vanilla's
    // TerrainOp lowers the heightfield from the hit point to the surface —
    // you can never tunnel INTO a wall. The swing becomes a 2m-circle voxel
    // carve at the exact hit point (the same op as the Vcarve tool), so
    // pickaxes dig sideways, downward, overhangs — real tunnels. On vanilla
    // ground the first dig converts the zone (ZoneReplace/PlacedOnly: the
    // carve arms it exactly like a brush does); with Terrain Mode Off,
    // vanilla ground keeps the vanilla op. Wards and no-build locations gate
    // the dig exactly like vanilla's. Only terrain-op hits dig: a VFX-only
    // spawnOnHitTerrain (sledge slams) never carves.
    [HarmonyPatch(typeof(Attack), nameof(Attack.SpawnOnHitTerrain))]
    internal static class Patch_Attack_SpawnOnHitTerrain_VoxelDig
    {
        private const float DigRadius = 1.0f;

        private static bool Prefix(Vector3 hitPoint, GameObject prefab, ref GameObject __result)
        {
            if (FiresVoxelConfig.Enabled == null || !FiresVoxelConfig.Enabled.Value || prefab == null) return true;
            var modifier = prefab.GetComponentInChildren<TerrainModifier>();
            var op = prefab.GetComponentInChildren<TerrainOp>();
            if (modifier == null && op == null) return true;
            if (Zones.ZoneModeRefusals.TerrainEditTouchesClosed(hitPoint, DigRadius)) return true;

            bool onVoxel = VoxelWorld.TrySampleSurfaceY(hitPoint.x, hitPoint.z, hitPoint.y + 3f, hitPoint.y - 3f, out _, out bool solid)
                || solid;
            if (!onVoxel && !(FiresVoxelConfig.StreamerRuns && FiresVoxelConfig.WrapsBrushedZones)) return true;

            __result = null;
            if (modifier != null && (!PrivateArea.CheckAccess(hitPoint, modifier.GetRadius()) || Location.IsInsideNoBuildLocation(hitPoint)))
                return false;
            if (op != null && (!PrivateArea.CheckAccess(hitPoint, op.GetRadius()) || Location.IsInsideNoBuildLocation(hitPoint)))
                return false;
            VoxelWorld.ApplyBrush(hitPoint, DigRadius, 0f, VoxelBrushShape.Sphere, VoxelBrushMode.Carve);
            return false;
        }
    }

    // Hoe, cultivator and every m_groundPiece/m_groundOnly/m_cultivatedGroundOnly
    // piece die at the SAME trap one layer up: Player.PieceRayTest reads
    // hitInfo.collider.GetComponent<Heightmap>() — null on voxel chunks, so
    // the ghost goes Invalid and TryPlacePiece prints $msg_invalidplacement
    // before any TerrainOp can spawn. Same shim as the Attack one, except the
    // call site lives on Component (collider), not GameObject. The resolved
    // zone heightmap is load-bearing here: IsCultivated/GetBiome/
    // GetVegetationMask answer from its live DATA layer, so cultivate → plant
    // works with no further patches.
    [HarmonyPatch(typeof(Player), "PieceRayTest")]
    internal static class Patch_Player_PieceRayTest_VoxelTerrain
    {
        public static Heightmap Resolve(Component c)
        {
            if (c == null) return null;
            var hm = c.GetComponent<Heightmap>();
            if (hm != null) return hm;
            if (c.GetComponent<VoxelChunkView>() == null) return null;
            return VoxelHeightmapResolver.FromChunkGO(c.gameObject);
        }

        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var resolve = AccessTools.Method(typeof(Patch_Player_PieceRayTest_VoxelTerrain), nameof(Resolve));
            int swapped = 0;
            foreach (var ins in instructions)
            {
                if ((ins.opcode == System.Reflection.Emit.OpCodes.Callvirt
                        || ins.opcode == System.Reflection.Emit.OpCodes.Call)
                    && ins.operand is System.Reflection.MethodInfo mi
                    && mi.Name == "GetComponent" && mi.IsGenericMethod
                    && mi.DeclaringType == typeof(Component)
                    && mi.GetGenericArguments().Length == 1
                    && mi.GetGenericArguments()[0] == typeof(Heightmap))
                {
                    yield return new CodeInstruction(System.Reflection.Emit.OpCodes.Call, resolve)
                    { labels = ins.labels, blocks = ins.blocks };
                    swapped++;
                    continue;
                }
                yield return ins;
            }
            if (swapped == s_loggedSites) yield break;
            s_loggedSites = swapped;
            if (swapped == 0)
                FiresLogger.LogWarning("[FiresVoxel] Player.PieceRayTest terrain-lookup transpiler matched NO call sites — hoe/cultivator stay dead on voxel ground (game update changed the method?).");
            else
                FiresLogger.LogInfo($"[FiresVoxel] Player.PieceRayTest terrain lookup shimmed ({swapped} site(s)) — hoe/cultivator ground pieces resolve the zone heightmap on voxel ground.");
        }

        private static int s_loggedSites = -1;
    }

    // The Normal-priority prefix of FAT's one Heightmap.RebuildRenderMesh hook (FatVanillaHooks).
    internal static class VoxelOnlyRenderMesh
    {
        internal static bool Allow(Heightmap __instance)
        {
            if (!VoxelOnlyMode.Active) return true;
            if (!VoxelOnlyMode.IsDistantLod(__instance)) return false;   // near: never renders
            // Distant: OURS (user mandate — we own distance). Vanilla's
            // TerrainLod never builds while the Fires low-LOD is on; the
            // material-variant swamp (capture lottery, _ISDISTANTLOD_ON
            // keyword coverage in the shader port) made vanilla's horizon
            // either invisible or an uncarved sheet at the player's feet.
            // With the lod config OFF, vanilla distant builds as fallback.
            bool firesLod = FiresVoxelConfig.FarQuiltEnabled != null && FiresVoxelConfig.FarQuiltEnabled.Value;
            return !firesLod;
        }
    }

    [HarmonyPatch(typeof(Heightmap), "RebuildCollisionMesh")]
    internal static class Patch_Heightmap_RebuildCollisionMesh_VoxelOnly
    {
        private static bool Prefix(Heightmap __instance)
            => !(VoxelOnlyMode.Active && !VoxelOnlyMode.IsDistantLod(__instance));
    }

    // Lava lives in the vanilla render pipeline we suppress — ExpandWorldData
    // prefixes Heightmap.GetLava and NREs on the missing render state
    // (BiomeHeat.GetLava → per-FixedUpdate error spam via BalrondNature's
    // lava-damage poll). Our generator paints no vanilla lava, so the answer
    // on a voxel-owned world is simply 0. Priority.First + return false skips
    // EWD's prefix and the original in one move.
    // Called first from FAT's one Heightmap.GetLava hook (FatVanillaHooks), before FiresLavaQueries.
    internal static class VoxelOnlyLava
    {
        private static bool s_logged;

        internal static bool Shield(ref float __result)
        {
            if (!VoxelOnlyMode.Active) return true;
            if (!s_logged)
            {
                s_logged = true;
                FiresLogger.LogInfo("[FiresVoxel] Heightmap.GetLava shielded on voxel world (no vanilla lava; EWD BiomeHeat NRE class suppressed).");
            }
            __result = 0f;
            return false;
        }
    }

    // Vanilla's under-world rescue (Character.UnderWorldCheck) compares the
    // character's Y against the HEIGHTMAP ground — but voxel carves live
    // BELOW the heightmap surface by design, so standing in any cave or dug
    // shaft read as "fell through the world" and teleported the player back
    // to the surface. If a solid voxel floor exists beneath the character,
    // they are inside the voxel world: no rescue. No voxel data below at
    // all → vanilla's check still runs (real void safety net).
    internal static class VoxelUnderWorld
    {
        private static readonly AccessTools.FieldRef<Character, float> TimerRef =
            AccessTools.FieldRefAccess<Character, float>("m_underWorldCheckTimer");

        // THE ONE under-world authority (audit: three prefixes fought over
        // this choke point with different truth sources; the raycast twin is
        // deleted — a terrain-layer ray can hit a 2D tile or an unbaked
        // collider and lie, voxel DENSITY cannot). Guards merged from the
        // retired patch: dead-skip, NPC cadence mirror, timer contract.
        // Called from FAT's one Character.UnderWorldCheck hook (FatVanillaHooks).
        internal static bool Allow(Character __instance, float dt)
        {
            if (FiresVoxelConfig.Enabled == null || !FiresVoxelConfig.Enabled.Value) return true;
            if (__instance == null || __instance.IsDead()) return true;
            // Noclip flight (Server Devcommands) goes anywhere, under the ground too: no rescue of any kind, vanilla's included.
            if (FatNoClip.Skips(__instance, "the under-world rescue"))
            {
                TimerRef(__instance) = 0f;
                return false;
            }
            if (TimerRef(__instance) + dt <= 5f && !__instance.IsPlayer()) return true;

            var zs = ZoneSystem.instance;
            if (zs == null) return true;
            var p = __instance.transform.position;

            // 0.2.697 (Fire 10-01, after a sea-cliff climb kept snapping him 61 m up to the cliff top: "vanilla should not be
            // running "below the ground" rescue on our voxel terrain, or our caves, or our sky islands for that matter
            // because it would pull us up to them too"). Vanilla lifts any body below GetGroundHeight - 1 to that height,
            // and on voxel ground GetGroundHeight is the TOP of the column: a cliff or overhang above a swimmer, the roof
            // over a cave, a sky island over the ground. On voxel ground vanilla's rescue never runs; the only net left is
            // voxel-native (VoidRescue): a body far below the world with nothing under it goes onto the LOWEST floor of
            // its column, never onto a column top above it. Zones still on vanilla terrain keep vanilla's check.
            if (VoxelOnlyMode.Active || VoxelZoneStreamer.IsZoneSolid(ZoneSystem.GetZone(p)))
            {
                TimerRef(__instance) = 0f;   // vanilla's timer contract (NPCs re-check every 5 s)
                VoidRescue(__instance, p);
                return false;
            }

            if (p.y >= zs.GetGroundHeight(p) - 1f)
            {
                TimerRef(__instance) = 0f;   // vanilla resets here too; without it NPCs re-check every frame
                return false;
            }
            if (VoxelWorld.TrySampleSurfaceY(p.x, p.z, p.y + 2f, p.y - 300f, out _, out _))
            {
                TimerRef(__instance) = 0f;   // keep the NPC cadence contract
                return false;                // solid voxel floor below — inside carved terrain, no rescue
            }
            return true;                     // true void on vanilla terrain — vanilla safety net
        }

        // Fire 10-01: "we will replace it with our own safety that only pulls someone up to the lowest layer if they happen
        // to fall through the world" - "or, whatever layer they were trying to teleport to if they fall through on
        // teleport". It fires ONLY when nothing solid is under the body (a down ray on the solid layers, checked first, so
        // a normal frame costs one ray) AND the body is below the LOWEST voxel floor of its column (the first solid→air
        // crossing scanned up from ScanBottom). It lands on that lowest floor - never a cave ceiling, an overhang, a cliff
        // top or a sky island above - except within TeleportMemory s of a teleport that targeted this spot, where it
        // lands on the floor nearest the target's height (the layer the teleport meant: an island, a cave, a floor).
        private const float ScanBottom = -300f, ColumnTop = 1200f, ScanStep = 20f, RescueLift = 0.5f, BelowFloor = 1f;
        private const float TeleportMemory = 10f, TeleportReach = 16f, TeleportWindow = 40f;
        private static readonly int s_solidMask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "terrain", "vehicle");
        private static readonly AccessTools.FieldRef<Character, float> MaxAirAltitudeRef =
            AccessTools.FieldRefAccess<Character, float>("m_maxAirAltitude");
        private const float FallingSpeed = 5f;
        private static bool s_rescueLogged;
        private static readonly System.Collections.Generic.Dictionary<int, (Vector3 to, float at)> s_teleports =
            new System.Collections.Generic.Dictionary<int, (Vector3 to, float at)>();

        // From FAT's one Player.TeleportTo hook (FatVanillaHooks), after a teleport that went ahead.
        internal static void NoteTeleport(Character c, Vector3 to)
        {
            if (c == null) return;
            if (s_teleports.Count > 256) s_teleports.Clear();
            s_teleports[c.GetInstanceID()] = (to, Time.time);
        }

        // 0.2.784 ([lead] 10-06, Seekers from a dungeon interior landed on the overworld): a creature inside an interior
        // (vanilla's band, y > 3000) that drops with nothing under it within InteriorDrop goes back to the last interior
        // floor it stood on, never to the world floor 5000 m below. Creatures only: players leave interiors by door
        // teleports, which this must not undo.
        private const float InteriorDrop = 30f;
        private const int InteriorMemoryCap = 1024;
        private static readonly System.Collections.Generic.Dictionary<int, Vector3> s_interiorFloor =
            new System.Collections.Generic.Dictionary<int, Vector3>();
        private static bool s_interiorRescueLogged;

        private static bool InteriorRescue(Character c, Vector3 p)
        {
            if (c.IsPlayer() || c.IsTamed()) return false;
            int id = c.GetInstanceID();
            bool remembered = s_interiorFloor.TryGetValue(id, out var floor);
            if (Character.InInterior(p))
            {
                if (c.IsOnGround())
                {
                    if (!remembered && s_interiorFloor.Count >= InteriorMemoryCap) s_interiorFloor.Clear();
                    s_interiorFloor[id] = p;
                    return true;
                }
                if (!remembered || Physics.Raycast(p + Vector3.up * 0.5f, Vector3.down, InteriorDrop, s_solidMask, QueryTriggerInteraction.Ignore))
                    return true;
            }
            // Below the band: only a body still FALLING goes back. One that stands there arrived by a door teleport (a
            // companion following its player out) and stays; tamed creatures are never pulled back at all (0.2.785).
            else if (!remembered || c.IsOnGround() || (c.GetComponent<Rigidbody>()?.linearVelocity.y ?? 0f) > -FallingSpeed)
            {
                s_interiorFloor.Remove(id);
                return false;
            }
            s_interiorFloor.Remove(id);
            var to = floor + Vector3.up * RescueLift;
            c.transform.position = to;
            var body = c.GetComponent<Rigidbody>();
            if (body != null) { body.position = to; body.linearVelocity = Vector3.zero; }
            MaxAirAltitudeRef(c) = to.y;
            if (!s_interiorRescueLogged)
            {
                s_interiorRescueLogged = true;
                FiresLogger.LogInfo($"[FiresVoxel] interior rescue: {c.m_name} dropped out of an interior at ({p.x:0}, {p.y:0}, {p.z:0}); "
                    + $"put back on its last interior floor ({floor.x:0}, {floor.y:0}, {floor.z:0}). First of this session.");
            }
            return true;
        }

        private static void VoidRescue(Character c, Vector3 p)
        {
            if (InteriorRescue(c, p)) return;
            if (Physics.Raycast(p + Vector3.up * 0.5f, Vector3.down, 3000f, s_solidMask, QueryTriggerInteraction.Ignore)) return;
            float lowest = float.NaN;
            for (float lo = ScanBottom; lo < ColumnTop && float.IsNaN(lowest); lo += ScanStep)
                if (VoxelWorld.TrySampleSurfaceY(p.x, p.z, lo + ScanStep, lo, out float y, out _)) lowest = y;
            if (float.IsNaN(lowest) || p.y >= lowest - BelowFloor) return;   // not under the world's floor here: no rescue

            float floor = lowest;
            string why = "the lowest voxel floor";
            if (s_teleports.TryGetValue(c.GetInstanceID(), out var tp) && Time.time - tp.at <= TeleportMemory
                && Utils.DistanceXZ(tp.to, p) <= TeleportReach)
            {
                // The floor nearest the target's height: the highest crossing at or below target + 2 m within the window,
                // else the lowest one above it.
                if (VoxelWorld.TrySampleSurfaceY(p.x, p.z, tp.to.y + 2f, tp.to.y - TeleportWindow, out float near, out _))
                { floor = near; why = "the floor nearest its teleport target"; }
                else if (VoxelWorld.TrySampleSurfaceY(p.x, p.z, tp.to.y + TeleportWindow, tp.to.y + 2f, out float above, out _))
                { floor = above; why = "the floor nearest its teleport target"; }
            }
            var to = new Vector3(p.x, floor + RescueLift, p.z);
            c.transform.position = to;
            var body = c.GetComponent<Rigidbody>();
            if (body != null) { body.position = to; body.linearVelocity = Vector3.zero; }
            MaxAirAltitudeRef(c) = to.y;   // 0.2.785: the lift is not a fall
            if (!s_rescueLogged)
            {
                s_rescueLogged = true;
                FiresLogger.LogInfo($"[FiresVoxel] void rescue: {c.m_name} fell through the world at ({p.x:0}, {p.y:0}, {p.z:0}); "
                    + $"placed on {why}, y {floor:0.0} (lowest floor {lowest:0.0}). First of this session.");
            }
        }
    }

    // Spawn/teleport gate: vanilla considers an area "ready" when its zones
    // and objects exist — in VoxelOnly that no longer implies GROUND exists
    // (heightmap colliders never build). Hold readiness until the target
    // zone + neighbours have live voxel colliders. FAIL-OPEN after 45s so a
    // stuck wrap can never become an infinite loading screen (the streamer
    // wraps around ZNet's reference position pre-spawn, which vanilla sets
    // to the pending spawn point).
    // Vegetation/rock fallers must WAIT for the voxel ground. StaticPhysics
    // starts fall-checking the moment its zone spawns, but voxel colliders
    // arrive from the mesh pipeline AFTER — trees fell through the
    // not-yet-solid ground and pogoed when it landed ("veg wigs out", worst
    // on sky islands where the fall is 200m). Skip the whole update until
    // the zone's colliders are live: the object stands at its spawn height,
    // then ground-checks against REAL ground. Readiness-gated, not a timer.
    [HarmonyPatch(typeof(StaticPhysics), nameof(StaticPhysics.SUpdate))]
    internal static class Patch_StaticPhysics_WaitForVoxelGround
    {
        private static bool Prefix(StaticPhysics __instance)
        {
            if (VoxelCaveContent.IsCaveContent(__instance)) return false;
            // A prop dressed onto a skyland never falls or pushes up at all. Vanilla measures the ground it should sit on
            // with ZoneSystem.GetGroundHeight (GetSolidHeight only when m_checkSolids, which these rocks do not set), and
            // that is the heightmap 150 m below: every rock and stone dropped out of the sky once its area loaded. It
            // keeps the height the dressing placed it at, and so does anything under a skyland the server has announced
            // but whose island set has not reached this client yet.
            Vector3 position = __instance.transform.position;
            if (FiresSkyIslands.IsOnZoneSkyland(position) || Zones.ZoneModeSkylands.RisingAt(position)) return false;
            if (!FiresWorldGenBase.Active) return true;
            // Solid AND settled for 3s — bounce reports persisted with the
            // bare solid gate; the settle window outlasts collider bake races.
            return VoxelZoneStreamer.IsZoneSolidFor(ZoneSystem.GetZone(__instance.transform.position), 3f);
        }
    }

    [HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.IsAreaReady))]
    internal static class Patch_ZNetScene_IsAreaReady_VoxelOnly
    {
        private static float s_gateSince = -1f;
        private const float GateWarnAfter = 60f;   // 0.2.853: progress lines are Debug below this (the FAIL-OPEN at 45 s stays a Warning)
        private static float s_nextDiag;
        // The exact point a spawn/teleport is waiting on — served to the
        // streamer as its pre-spawn wrap center. Ground truth from the gate
        // itself: the old chain (peer refpositions, then own refposition)
        // could center the wrap somewhere else entirely, and the streamer
        // then wrapped NOTHING near the spawn while the gate waited on it —
        // the full 45s FAIL-OPEN burned on every login.
        private static Vector3 s_pendingPoint;
        private static float s_pendingAt = -1f;

        internal static Vector3 PendingSpawnArea(float maxAge)
            => s_pendingAt >= 0f && Time.realtimeSinceStartup - s_pendingAt <= maxAge
               ? s_pendingPoint : Vector3.zero;

        private static void Postfix(Vector3 point, ref bool __result)
        {
            // VoxelOnly and skylands as before; now also any world whose voxel terrain this CLIENT builds (VoxelWrap /
            // converted worlds): their login dropped the player at heightmap height under the voxel ground (Fire 10-03).
            bool voxelHere = VoxelOnlyMode.Active || FiresSkyIslands.IsOnZoneSkyland(point)
                || (VoxelZoneStreamer.IsVoxelWorldActive && ZNet.instance != null && !ZNet.instance.IsDedicated());
            if (!__result || !voxelHere) return;
            if (VoxelZoneStreamer.SpawnAreaReady(point))
            {
                if (s_gateSince >= 0f)
                    FiresLogger.LogDebug($"[FiresVoxel] spawn gate released — area solid after " +
                              $"{Time.realtimeSinceStartup - s_gateSince:F1}s.");
                s_gateSince = -1f;
                s_pendingAt = -1f;
                VoxelMeshPipeline.SpawnGateBoost = false;
                return;
            }

            s_pendingPoint = point;
            s_pendingAt = Time.realtimeSinceStartup;
            // Vanilla's spawn flow is supposed to point the reference position
            // at the pending spawn every frame (zones spawn around refpos —
            // without it there is nothing for the streamer to wrap). Assert it
            // ourselves while holding so no other mod's spawn flow can starve
            // the area of vanilla zones.
            if (ZNet.instance != null && Player.m_localPlayer == null)
                ZNet.instance.SetReferencePosition(point);

            if (s_gateSince < 0f)
            {
                s_gateSince = Time.realtimeSinceStartup;
                s_nextDiag = 0f;
                FiresLogger.LogInfo($"[FiresVoxel] spawn gate HOLDING at {point:F0} — full-throttle drain. " +
                          $"{VoxelZoneStreamer.DescribeArea(point)}");
            }
            // The loading screen has no gameplay to hitch: the spawn area was
            // draining at the gameplay throttle (2 applies + 2 collider assigns
            // per frame), which is why 3x3 zones could burn the whole 45s.
            // Open the pipeline wide until the gate releases.
            VoxelMeshPipeline.SpawnGateBoost = true;

            float held = Time.realtimeSinceStartup - s_gateSince;
            if (held > 5f && Time.realtimeSinceStartup >= s_nextDiag)
            {
                s_nextDiag = Time.realtimeSinceStartup + 10f;
                // 0.2.853 (fat_load_test red every round on these): progress at Debug (Fire's logging rule), a Warning only past
                // GateWarnAfter s, a real stall.
                string gateLine = $"[FiresVoxel] spawn gate holding {held:F0}s: {VoxelZoneStreamer.DescribeArea(point)}";
                if (held > GateWarnAfter) FiresLogger.LogWarning(gateLine); else FiresLogger.LogDebug(gateLine);
            }
            if (held > 45f)
            {
                FiresLogger.LogWarning($"[FiresVoxel] VoxelOnly spawn gate FAIL-OPEN after 45s — releasing anyway. " +
                                 $"Blockers: {VoxelZoneStreamer.DescribeArea(point)}");
                // Fresh timer for any LATER gating episode — the stale timer
                // made every subsequent check fail-open instantly (the second
                // FAIL-OPEN line every login).
                s_gateSince = -1f;
                VoxelMeshPipeline.SpawnGateBoost = false;
                return;   // leave __result true
            }
            __result = false;
        }
    }

    // Field-error guard: some modded prefab carries an EffectArea with no
    // sibling Collider — vanilla's Awake NREs on it (twice per load in the
    // prewarm window). Skip the registration and NAME the object once so the
    // real culprit can be fixed at the source.
    [HarmonyPatch(typeof(EffectArea), "Awake")]
    internal static class Patch_EffectArea_Awake_ColliderGuard
    {
        private static bool s_warned;

        private static bool Prefix(EffectArea __instance)
        {
            if (__instance.GetComponent<Collider>() != null) return true;
            if (!s_warned)
            {
                s_warned = true;
                FiresLogger.LogWarning($"[FiresVoxel] EffectArea WITHOUT a Collider on '{__instance.transform.root.name}/{__instance.name}' — Awake skipped (was NRE-ing every load). Fix the prefab.");
            }
            return false;
        }
    }

    // Water-shader field question, answered automatically: the first zone
    // water surface logs its material + shader once per session, so "is our
    // custom water applied" stops being a guess. Logged at OnEnable with
    // LAST priority — the FiresTossinShade swap postfix runs on the same
    // method at default priority, so this reports the POST-swap truth (the
    // old Awake-time log fired before any swap could land and cried vanilla
    // even when the swap was about to apply).
    // A built piece with water (a hot tub) enables its own WaterVolume and can come first ([perf] R41: "zone water surface:
    // material='water_hottub'"), so zone water and piece water are logged apart, each naming its object.
    [HarmonyPatch(typeof(WaterVolume), "OnEnable")]
    internal static class Patch_WaterVolume_OnEnable_MaterialLog
    {
        private static bool s_loggedZone;
        private static bool s_loggedPiece;

        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(WaterVolume __instance)
        {
            if (__instance.m_waterSurface == null) return;
            bool piece = __instance.GetComponentInParent<ZNetView>() != null;
            if (piece ? s_loggedPiece : s_loggedZone) return;
            if (piece) s_loggedPiece = true;
            else s_loggedZone = true;
            var mat = __instance.m_waterSurface.sharedMaterial;
            FiresLogger.LogInfo($"[FiresVoxel] {(piece ? "piece" : "zone")} water surface on '{__instance.transform.root.name}': "
                + $"material='{(mat != null ? mat.name : "null")}' shader='{(mat != null && mat.shader != null ? mat.shader.name : "null")}'");
        }
    }

    // W3-A4 — far terrain wears OUR material. Distant-LOD heightmaps already
    // render our heights (they build from the generator), but in vanilla's
    // distant material — a visible look-break at the voxel frontier. Swap
    // them onto the same captured terrain material the voxel chunks use, so
    // near and far are one continuous surface. (Full quilt replacement of the
    // distant component rides with the big-world radius work.)
    // (Distant-material swap DELETED — vanilla TerrainLod no longer renders
    // under the Fires low-LOD, and skinning it caused both the uncarved
    // sheets at the player's feet (near variant, no near-fade) and the
    // invisible horizon (fallback/keyword variants). One owner now.)

    // With no terrain collider, vanilla's top-down ground raycast would miss
    // (or hit a voxel chunk and find no Heightmap component). Answer from
    // heightmap DATA instead — the exact values the voxels are built from, so
    // vegetation, locations and spawns land on the voxel surface by
    // construction, and SpawnZone has zero physics dependency.
    [HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.GetGroundData))]
    internal static class Patch_ZoneSystem_GetGroundData_VoxelOnly
    {
        // What the voxel surface WILL be at (x,z) once the chunks generate,
        // derived from the same inputs the generator uses. Only the gen-water
        // bed re-carve moves the surface — caves carve strictly below it — so
        // predicting that is enough to place flora at its final height before
        // the voxel data exists. Must stay in step with the bed carve in
        // VoxelChunk.RunPendingGenerate; if they disagree, veg jumps again.
        internal static float PredictedSurface(float wx, float wz, float heightmapY)
        {
            if (!FiresWorldGenBase.Active) return heightmapY;
            float channelDepth = FiresVoxelConfig.WaterMinChannelDepth != null
                ? FiresVoxelConfig.WaterMinChannelDepth.Value : 0f;
            if (channelDepth <= 0f) return heightmapY;
            // RECONCILED surface — must stay in step with the bed carve in
            // VoxelChunk.RunPendingGenerate or veg jumps again.
            if (!FiresWorldGenBase.TryGetWaterSurfaceSmooth(wx, wz, out float lvl, out float depth)) return heightmapY;
            if (depth < 0.25f || lvl <= 30.25f) return heightmapY;
            float wantBed = lvl - channelDepth;
            return heightmapY > wantBed ? wantBed : heightmapY;
        }

        // Every voxel world, whatever its generator and terrain kind: vanilla's top-down ray that lands on voxel ground
        // hits a chunk collider with no Heightmap, so it answers no biome and no heightmap (and callers read no lava).
        // The zone's own heightmap answers them, the generator's biome where none is loaded; the height stays the ray's.
        // 0.2.674 → 0.2.675 (R46, CoopStream base pond, 10-01): the answer was gated on the ray landing within 0.5 m
        // of a sampled voxel surface. At a tool-carved pond in a zone converted by ZoneReplace that sample missed, the biome
        // stayed None, and every biome-gated spawn and clutter rule refused the pond ("biome None", 0 fish). Vanilla leaves
        // the biome None only when its ray missed or hit something without a Heightmap, and the zone's (or the generator's)
        // biome is the right answer in both cases, so it is now always given.
        private static void Postfix(ref Vector3 p, ref Heightmap.Biome biome, ref Heightmap.BiomeArea biomeArea, ref Heightmap hmap)
        {
            if (hmap != null || biome != Heightmap.Biome.None) return;
            var zoneMap = Heightmap.FindHeightmap(p);
            if (zoneMap != null)
            {
                hmap = zoneMap;
                biome = zoneMap.GetBiome(p);
                biomeArea = zoneMap.GetBiomeArea();
                return;
            }
            if (WorldGenerator.instance != null) biome = WorldGenerator.instance.GetBiome(p.x, p.z);
        }


        // Timed wrapper — see FiresVegProfiler. Zero cost outside PlaceVegetation:
        // one bool test, and the stopwatch only runs while that method is on the stack.
        private static bool Prefix(ref Vector3 p, ref Vector3 normal, ref Heightmap.Biome biome,
            ref Heightmap.BiomeArea biomeArea, ref Heightmap hmap)
        {
            if (!FiresVegProfiler.Inside) return PrefixBody(ref p, ref normal, ref biome, ref biomeArea, ref hmap);
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            FiresVegProfiler.GroundCalls++;
            bool r = PrefixBody(ref p, ref normal, ref biome, ref biomeArea, ref hmap);
            FiresVegProfiler.GroundTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            return r;
        }

        private static bool PrefixBody(ref Vector3 p, ref Vector3 normal, ref Heightmap.Biome biome,
            ref Heightmap.BiomeArea biomeArea, ref Heightmap hmap)
        {
            if (!VoxelOnlyMode.Active) return true;
            var hm = Heightmap.FindHeightmap(p);
            // THE GENERATOR CAN ALWAYS ANSWER; A HEIGHTMAP CANNOT.
            // Handing this back to vanilla when no Heightmap exists is what
            // buried locations: ZoneSystem.PlaceLocations discards the sited y
            // and re-snaps through GetGroundData, and during world generation
            // the zone's Heightmap has usually not been built, so vanilla
            // answered from a terrain that does not exist on a Fires world and
            // the start temple ended up ~60m under the surface, dragging its
            // clear-area hole down with it. Our height field needs no
            // Heightmap, no collider and no voxel chunk — it is the same
            // SampleFinalMeters the terrain is built from, so answering from it
            // puts locations exactly on the surface by construction.
            if (hm == null || !Heightmap.GetHeight(p, out float h))
            {
                if (!FiresWorldGenBase.Active) return true;
                // The GENERATED surface, not PredictedSurface: that subtracts a
                // channel depth wherever the water field reports water, which is
                // another route to a y below the ground.
                p.y = FiresWorldGenBase.SampleFinalMeters(p.x, p.z);
                // A REAL NORMAL. Returning Vector3.up here made every tilt-based
                // placement rule see dead-flat ground — 527 maxTilt rules in the
                // shipped Expand World set alone, all of which would pass on a
                // cliff. Central difference on the same height field, 2m arms to
                // match the grid the terrain is built on.
                float hxm = FiresWorldGenBase.SampleFinalMeters(p.x - 2f, p.z);
                float hxp = FiresWorldGenBase.SampleFinalMeters(p.x + 2f, p.z);
                float hzm = FiresWorldGenBase.SampleFinalMeters(p.x, p.z - 2f);
                float hzp = FiresWorldGenBase.SampleFinalMeters(p.x, p.z + 2f);
                normal = new Vector3(hxm - hxp, 4f, hzm - hzp).normalized;
                biome = FiresWorldGenBase.SampleBiome(p.x, p.z);
                biomeArea = Heightmap.BiomeArea.Median;
                hmap = null;
                return false;
            }

            // CARVED TRUTH FIRST (audit §3): spawns/vegetation/locations land
            // via this call — answering from heightmap DATA put them on the
            // UNCARVED surface (floating over riverbeds, buried over fills).
            // The voxel top surface is the ground where voxel data exists;
            // heightmap data stays the fallback for unwrapped zones.
            bool isle = FiresSkyIslands.IslandAt(p.x, p.z);
            float probeTop = isle ? h + 340f : h + 64f;
            // One-zone-at-a-time skylands carry their own dressing, so the ground under them keeps ALL of its
            // own: probe from below the island's underside and never land on the island.
            bool zoneWorld = Zones.ZoneModeRegistry.ModeActive;
            if (isle && zoneWorld)
                probeTop = FiresSkyIslands.TryBottomAt(p.x, p.z, out float islandBottom)
                    ? Mathf.Min(h + 64f, islandBottom - 1f) : h + 64f;
            if (FiresVegProfiler.Inside) FiresVegProfiler.VoxelProbes++;
            bool voxel = VoxelWorld.TrySampleSurfaceY(p.x, p.z, probeTop, h - 200f, out float vs, out _);
            // SKY ISLAND SPLIT — the top-down probe finds the ISLAND as "the
            // surface", which hoisted EVERY veg item off the ground below
            // (bare terrain under a fully-forested island). Ground keeps most
            // of its vegetation; a deterministic ~30% share stays on the
            // island so it isn't bare either.
            if (voxel && isle && !zoneWorld
                && FiresWorldGenBase.TryGetGenGround(p.x, p.z, out float baseG)
                && vs > baseG + 12f)
            {
                uint hsh;
                unchecked
                {
                    hsh = (uint)(Mathf.FloorToInt(p.x) * 374761393) ^ (uint)(Mathf.FloorToInt(p.z) * 668265263);
                    hsh ^= hsh >> 13; hsh *= 1274126177u; hsh ^= hsh >> 16;
                }
                if ((hsh & 1023) >= 307)   // ~70% of items return to the ground
                {
                    if (VoxelWorld.TrySampleSurfaceY(p.x, p.z, baseG + 6f, h - 200f, out float gs, out _)) vs = gs;
                    else voxel = false;    // ground column not in yet — predicted surface below
                }
            }
            // Voxel data not in for this column yet (the zone's heightmap is
            // created BEFORE the streamer wraps it, and vegetation places right
            // then). Handing back the raw heightmap here puts flora on the
            // UN-carved surface, and it visibly jumps the moment the real voxel
            // terrain arrives underneath — the bouncing veg. The voxel surface
            // is the heightmap minus the gen-water bed carve, so predict that
            // and place on the height the terrain is ABOUT to have.
            p.y = voxel ? vs : PredictedSurface(p.x, p.z, h);
            // A wrapped voxel column already carries any location pad (baked in
            // GatherColumns); when the column isn't in yet, add the pad to the
            // predicted surface so flora/spawns over it land on the pad, not the
            // rough ground it replaced.
            if (!voxel && FiresLocationPads.TrySample(p.x, p.z, p.y, out float lpy)) p.y = lpy;

            float hx0, hx1, hz0, hz1;
            if (voxel)
            {
                if (!VoxelWorld.TrySampleSurfaceY(p.x - 0.5f, p.z, p.y + 4f, p.y - 4f, out hx0, out _)) hx0 = p.y;
                if (!VoxelWorld.TrySampleSurfaceY(p.x + 0.5f, p.z, p.y + 4f, p.y - 4f, out hx1, out _)) hx1 = p.y;
                if (!VoxelWorld.TrySampleSurfaceY(p.x, p.z - 0.5f, p.y + 4f, p.y - 4f, out hz0, out _)) hz0 = p.y;
                if (!VoxelWorld.TrySampleSurfaceY(p.x, p.z + 0.5f, p.y + 4f, p.y - 4f, out hz1, out _)) hz1 = p.y;
            }
            else
            {
                hx0 = hx1 = hz0 = hz1 = h;
                Heightmap.GetHeight(p + new Vector3(-0.5f, 0f, 0f), out hx0);
                Heightmap.GetHeight(p + new Vector3(0.5f, 0f, 0f), out hx1);
                Heightmap.GetHeight(p + new Vector3(0f, 0f, -0.5f), out hz0);
                Heightmap.GetHeight(p + new Vector3(0f, 0f, 0.5f), out hz1);
            }
            var n = new Vector3(hx0 - hx1, 2f, hz0 - hz1);
            normal = n.sqrMagnitude > 1e-6f ? n.normalized : Vector3.up;

            biome = hm.GetBiome(p);
            // River wet channel = Ocean for placement purposes: vegetation,
            // clutter and spawns biome-gate on this call, and Ocean is the
            // one biome no land flora plants in — riverbeds stay clear.
            if (FiresWorldGenBase.IsGenWetAt(p.x, p.z))
                biome = Heightmap.Biome.Ocean;
            biomeArea = hm.GetBiomeArea();
            hmap = hm;
            return false;
        }
    }

    // ZoneSystem.GetGroundHeight is a physics ray from y=6000 on the terrain
    // mask. In the voxel world that is wrong three ways: it grabs a SKY
    // ISLAND's top instead of the ground beneath it, it returns the caller's
    // own p.y as a garbage fallback wherever no collider is streamed (unloaded
    // zones — which is where location suitability runs), and where it does hit
    // it reads whatever collider happens to exist instead of the 2m truth.
    // GetTerrainDelta — the location placement suitability check — is built on
    // this call, which is how locations approved on garbage deltas ended up
    // pulling terrain to extremes. Answer analytically from the SAME sources
    // the mesher builds from: voxel surface where data exists, 2m grid + bed
    // carve prediction everywhere else. The probe window anchors near the
    // ground so islands overhead can never hijack the sample.
    internal static class VoxelGroundTruth
    {
        internal static bool Sample(Vector3 p, out float y)
        {
            y = 0f;
            if (!Heightmap.GetHeight(p, out float h)
                && !FiresWorldGenBase.TryGetGenGround(p.x, p.z, out h))
                return false;
            if (VoxelWorld.TrySampleSurfaceY(p.x, p.z, h + 24f, h - 200f, out float vs, out _))
            { y = vs; return true; }   // wrapped column already carries the pad
            y = Patch_ZoneSystem_GetGroundData_VoxelOnly.PredictedSurface(p.x, p.z, h);
            if (FiresLocationPads.TrySample(p.x, p.z, y, out float lpy)) y = lpy;
            return true;
        }
    }

    // ExpandWorldData's BiomeHeat.GetLava prefix reads hm.m_renderMesh
    // .colors32 — NULL on voxel-swapped heightmaps → an NRE every
    // FixedUpdate for any character standing in Ashlands (log spam). Our
    // FIRST-priority prefix answers from the worldgen lava field (the same
    // data that paints the mask) and skips both the original and EWD's
    // prefix — and gameplay lava damage now tracks OUR pools and flows
    // exactly. Called second from FAT's one Heightmap.GetLava hook
    // (FatVanillaHooks), so its value stands over VoxelOnlyLava's 0.
    internal static class FiresLavaQueries
    {
        internal static bool GetLava(Vector3 worldPos, ref float __result)
        {
            if (!FiresWorldGenBase.Active) return true;
            __result = FiresWorldGenBase.SampleLavaMask(worldPos.x, worldPos.z).a;
            return false;
        }
    }

    [HarmonyPatch(typeof(Heightmap), nameof(Heightmap.IsLava))]
    [HarmonyPriority(Priority.First)]
    internal static class Patch_Heightmap_IsLava_FiresLava
    {
        private static bool Prefix(Vector3 worldPos, float lavaValue, ref bool __result)
        {
            if (!FiresWorldGenBase.Active) return true;
            __result = FiresWorldGenBase.SampleLavaMask(worldPos.x, worldPos.z).a > lavaValue;
            return false;
        }
    }

    // Called first from FAT's one hook on each GetGroundHeight overload (FatVanillaHooks), before the tile ruler lift.
    internal static class VoxelGroundHeight
    {
        internal static bool Single(Vector3 p, ref float __result)
        {
            if (!VoxelOnlyMode.Active) return true;
            if (!VoxelGroundTruth.Sample(p, out float y)) return true;
            __result = y;
            return false;
        }

        internal static bool WithOut(Vector3 p, ref float height, ref bool __result)
        {
            if (!VoxelOnlyMode.Active) return true;
            if (!VoxelGroundTruth.Sample(p, out float y)) return true;
            height = y;
            __result = true;
            return false;
        }
    }
}
