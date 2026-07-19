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
            && FiresVoxelConfig.WorldMode != null
            && FiresVoxelConfig.WorldMode.Value == VoxelWorldMode.VoxelOnly
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
            if (swapped == 0)
                Debug.LogWarning("[FiresVoxel] Attack.DoMeleeAttack terrain-lookup transpiler matched NO call sites — pickaxe-on-voxel stays inert (game update changed the method?).");
            else
                Debug.Log($"[FiresVoxel] Attack.DoMeleeAttack terrain lookup shimmed ({swapped} site(s)) — pickaxe/hoe hits on voxel chunks resolve the zone heightmap.");
        }
    }

    // Pickaxe on VOXEL ground = a true 3D dig, not vanilla's column drop.
    // Vanilla's TerrainOp lowers the heightfield from the hit point to the
    // surface — you can never tunnel INTO a wall. On voxel ground the swing
    // becomes a 2m-circle voxel carve at the exact hit point (the same op as
    // the Vcarve tool), so pickaxes dig sideways, downward, overhangs — real
    // tunnels. Ground without voxel data (tiles/vanilla) keeps the vanilla op.
    [HarmonyPatch(typeof(Attack), nameof(Attack.SpawnOnHitTerrain))]
    internal static class Patch_Attack_SpawnOnHitTerrain_VoxelDig
    {
        private static bool Prefix(Vector3 hitPoint, ItemDrop.ItemData weapon, ref GameObject __result)
        {
            if (FiresVoxelConfig.Enabled == null || !FiresVoxelConfig.Enabled.Value) return true;
            // Only claim the swing when the hit point sits on/in voxel ground.
            if (!VoxelWorld.TrySampleSurfaceY(hitPoint.x, hitPoint.z, hitPoint.y + 3f, hitPoint.y - 3f, out _, out bool solid)
                && !solid)
                return true;
            VoxelWorld.ApplyBrush(hitPoint, 1.0f, 0f, VoxelBrushShape.Sphere, VoxelBrushMode.Carve);
            __result = null;
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
            if (swapped == 0)
                Debug.LogWarning("[FiresVoxel] Player.PieceRayTest terrain-lookup transpiler matched NO call sites — hoe/cultivator stay dead on voxel ground (game update changed the method?).");
            else
                Debug.Log($"[FiresVoxel] Player.PieceRayTest terrain lookup shimmed ({swapped} site(s)) — hoe/cultivator ground pieces resolve the zone heightmap on voxel ground.");
        }
    }

    [HarmonyPatch(typeof(Heightmap), "RebuildRenderMesh")]
    internal static class Patch_Heightmap_RebuildRenderMesh_VoxelOnly
    {
        private static bool Prefix(Heightmap __instance)
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
    [HarmonyPatch(typeof(Heightmap), "GetLava", typeof(Vector3))]
    internal static class Patch_Heightmap_GetLava_VoxelOnly
    {
        private static bool s_logged;

        [HarmonyPriority(Priority.First)]
        private static bool Prefix(ref float __result)
        {
            if (!VoxelOnlyMode.Active) return true;
            if (!s_logged)
            {
                s_logged = true;
                Debug.Log("[FiresVoxel] Heightmap.GetLava shielded on voxel world (no vanilla lava; EWD BiomeHeat NRE class suppressed).");
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
    [HarmonyPatch(typeof(Character), "UnderWorldCheck")]
    internal static class Patch_Character_UnderWorldCheck_VoxelAware
    {
        private static readonly AccessTools.FieldRef<Character, float> TimerRef =
            AccessTools.FieldRefAccess<Character, float>("m_underWorldCheckTimer");

        // THE ONE under-world authority (audit: three prefixes fought over
        // this choke point with different truth sources; the raycast twin is
        // deleted — a terrain-layer ray can hit a 2D tile or an unbaked
        // collider and lie, voxel DENSITY cannot). Guards merged from the
        // retired patch: dead-skip, NPC cadence mirror, timer contract.
        private static bool Prefix(Character __instance, float dt)
        {
            if (FiresVoxelConfig.Enabled == null || !FiresVoxelConfig.Enabled.Value) return true;
            if (__instance == null || __instance.IsDead()) return true;
            if (TimerRef(__instance) + dt <= 5f && !__instance.IsPlayer()) return true;

            var zs = ZoneSystem.instance;
            if (zs == null) return true;
            var p = __instance.transform.position;
            if (p.y >= zs.GetGroundHeight(p) - 1f) return false;   // vanilla wouldn't push — save it the work
            if (VoxelWorld.TrySampleSurfaceY(p.x, p.z, p.y + 2f, p.y - 300f, out _, out _))
            {
                TimerRef(__instance) = 0f;   // keep the NPC cadence contract
                return false;                // solid voxel floor below — inside carved terrain, no rescue
            }
            return true;                     // true void — vanilla safety net
        }
    }

    // Spawn/teleport gate: vanilla considers an area "ready" when its zones
    // and objects exist — in VoxelOnly that no longer implies GROUND exists
    // (heightmap colliders never build). Hold readiness until the target
    // zone + neighbours have live voxel colliders. FAIL-OPEN after 45s so a
    // stuck wrap can never become an infinite loading screen (the streamer
    // wraps around ZNet's reference position pre-spawn, which vanilla sets
    // to the pending spawn point).
    [HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.IsAreaReady))]
    internal static class Patch_ZNetScene_IsAreaReady_VoxelOnly
    {
        private static float s_gateSince = -1f;

        private static void Postfix(Vector3 point, ref bool __result)
        {
            if (!__result || !VoxelOnlyMode.Active) return;
            if (VoxelZoneStreamer.AreaSolid(point)) { s_gateSince = -1f; return; }

            if (s_gateSince < 0f) s_gateSince = Time.realtimeSinceStartup;
            if (Time.realtimeSinceStartup - s_gateSince > 45f)
            {
                Debug.LogWarning("[FiresVoxel] VoxelOnly spawn gate FAIL-OPEN after 45s — voxel colliders not ready, releasing anyway.");
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
                Debug.LogWarning($"[FiresVoxel] EffectArea WITHOUT a Collider on '{__instance.transform.root.name}/{__instance.name}' — Awake skipped (was NRE-ing every load). Fix the prefab.");
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
    [HarmonyPatch(typeof(WaterVolume), "OnEnable")]
    internal static class Patch_WaterVolume_OnEnable_MaterialLog
    {
        private static bool s_logged;

        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(WaterVolume __instance)
        {
            if (s_logged || __instance.m_waterSurface == null) return;
            s_logged = true;
            var mat = __instance.m_waterSurface.sharedMaterial;
            Debug.Log($"[FiresVoxel] zone water surface: material='{(mat != null ? mat.name : "null")}' " +
                      $"shader='{(mat != null && mat.shader != null ? mat.shader.name : "null")}'");
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
    // Vanilla clutter's ground probe raycasts (our voxel colliders answer —
    // height and normal are already carved truth) but then dereferences
    // GetComponent<Heightmap>() on the hit collider with NO null check. Voxel
    // chunk colliders carry no Heightmap → NRE → grass never places on voxel
    // ground. Resolve the ZONE heightmap through the same resolver the
    // pickaxe/hoe shims use; biome, vegetation mask and the cleared paint
    // (river beds) all read from it downstream.
    [HarmonyPatch(typeof(ClutterSystem), nameof(ClutterSystem.GetGroundInfo))]
    internal static class Patch_ClutterSystem_GetGroundInfo_VoxelOnly
    {
        // int, NOT LayerMask — a LayerMask FieldRef throws on the value-type
        // mismatch EVERY call and killed clutter everywhere (menu included).
        private static readonly AccessTools.FieldRef<ClutterSystem, int> s_rayMask =
            AccessTools.FieldRefAccess<ClutterSystem, int>("m_placeRayMask");

        private static bool Prefix(ClutterSystem __instance, Vector3 p, out Vector3 point,
            out Vector3 normal, out Heightmap hmap, out Heightmap.Biome biome, ref bool __result)
        {
            point = p; normal = Vector3.up; hmap = null; biome = Heightmap.Biome.Meadows;
            if (!VoxelOnlyMode.Active || ZNetScene.instance == null) return true;
            int mask = s_rayMask(__instance);
            if (Physics.Raycast(p + Vector3.up * 500f, Vector3.down, out var hit, 1000f, mask))
            {
                point = hit.point;
                normal = hit.normal;
                hmap = hit.collider.GetComponent<Heightmap>();
                if (hmap == null) hmap = VoxelHeightmapResolver.FromChunkGO(hit.collider.gameObject);
                if (hmap != null)
                {
                    biome = FiresWorldGenBase.IsWetAt(point.x, point.z)
                        ? Heightmap.Biome.Ocean
                        : hmap.GetBiome(point);
                    __result = true;
                    return false;
                }
            }
            __result = false;
            return false;
        }
    }

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
            if (!FiresWorldGenBase.TryGetGenWaterSmooth(wx, wz, out float lvl, out float depth)) return heightmapY;
            if (depth < 0.25f || lvl <= 30.25f) return heightmapY;
            float wantBed = lvl - channelDepth;
            return heightmapY > wantBed ? wantBed : heightmapY;
        }

        private static bool Prefix(ref Vector3 p, ref Vector3 normal, ref Heightmap.Biome biome,
            ref Heightmap.BiomeArea biomeArea, ref Heightmap hmap)
        {
            if (!VoxelOnlyMode.Active) return true;
            var hm = Heightmap.FindHeightmap(p);
            if (hm == null) return true;

            if (!Heightmap.GetHeight(p, out float h)) return true;

            // CARVED TRUTH FIRST (audit §3): spawns/vegetation/locations land
            // via this call — answering from heightmap DATA put them on the
            // UNCARVED surface (floating over riverbeds, buried over fills).
            // The voxel top surface is the ground where voxel data exists;
            // heightmap data stays the fallback for unwrapped zones.
            bool voxel = VoxelWorld.TrySampleSurfaceY(p.x, p.z, h + 64f, h - 200f, out float vs, out _);
            // Voxel data not in for this column yet (the zone's heightmap is
            // created BEFORE the streamer wraps it, and vegetation places right
            // then). Handing back the raw heightmap here puts flora on the
            // UN-carved surface, and it visibly jumps the moment the real voxel
            // terrain arrives underneath — the bouncing veg. The voxel surface
            // is the heightmap minus the gen-water bed carve, so predict that
            // and place on the height the terrain is ABOUT to have.
            p.y = voxel ? vs : PredictedSurface(p.x, p.z, h);

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
            if (FiresWorldGenBase.IsWetAt(p.x, p.z))
                biome = Heightmap.Biome.Ocean;
            biomeArea = hm.GetBiomeArea();
            hmap = hm;
            return false;
        }
    }
}
