using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    /// <summary>
    /// The "Super flat" admin build / test / blueprint world, linked through Dimensions so build yards and
    /// big ZDO counts stay off the main server. Chosen on the New World screen (FiresNewWorldOptions) and kept in the world's profile
    /// (FiresWorldProfile.SuperFlat, sent to every client in the handshake), so every gate here reads the WORLD, never a config:
    ///   - ground: flat Meadows at y = 50 (water is at 30) inside the world's radius (2500 m = 25% of vanilla); vanilla-depth ocean past it
    ///   - no locations except the start temple, no vegetation, pickables or rocks, no grass / clutter
    ///   - no spawning of any kind: spawn systems, creature spawners, spawn areas, random events (raids)
    /// Everything is a postfix or a "skip" prefix that runs only on a super flat world; every other world is untouched.
    /// </summary>
    internal static class FatSuperFlat
    {
        private const float ShoreWidth = 24f;     // the ground slopes from y 50 down to the sea floor over this many metres past the radius
        private const float SeaFloorY = -10f;     // about vanilla deep-ocean depth under the 30 m water line

        internal static bool On => FiresWorldProfile.CurrentIsSuperFlat;

        // Draw biomes with territories for testing:
        // CONTENT AREAS. A mod registers a test (FatApi.AddSuperFlatContentArea, first user RPGM's ForceBiome territories): a point it
        // answers true for is a normal world again on super flat for everything except the flat ground: the biome / biome sector answer
        // earlier patches gave (the territory's forced biome) stands, the height mask is not blacked out, vegetation is placed, auto
        // clutter grows and spawners / spawn systems run. Everywhere else super flat stays empty. Copy-on-write array: generator threads
        // call it; a test must be cheap, thread-safe and never call back into GetBiome. Each blocked/bypassed switch logs once.
        private static System.Func<float, float, bool>[] s_content = new System.Func<float, float, bool>[0];
        private static readonly object s_contentLock = new object();
        [System.ThreadStatic] private static int t_inContent;
        private static int s_contentFaults;
        internal static int ContentAreaCount => s_content.Length;

        internal static void AddContentArea(System.Func<float, float, bool> test)
        {
            lock (s_contentLock)
            {
                var a = new System.Func<float, float, bool>[s_content.Length + 1];
                System.Array.Copy(s_content, a, s_content.Length);
                a[a.Length - 1] = test;
                s_content = a;
            }
        }

        internal static bool RemoveContentArea(System.Func<float, float, bool> test)
        {
            lock (s_contentLock)
            {
                int i = System.Array.IndexOf(s_content, test);
                if (i < 0) return false;
                var a = new System.Func<float, float, bool>[s_content.Length - 1];
                System.Array.Copy(s_content, 0, a, 0, i);
                System.Array.Copy(s_content, i + 1, a, i, a.Length - i);
                s_content = a;
                return true;
            }
        }

        internal static bool ContentAt(float wx, float wz)
        {
            var tests = s_content;
            if (tests.Length == 0 || t_inContent > 0) return false;
            t_inContent++;
            try
            {
                foreach (var t in tests)
                {
                    try { if (t(wx, wz)) return true; }
                    catch (System.Exception ex)
                    {
                        if (System.Threading.Interlocked.Increment(ref s_contentFaults) == 1)
                            FiresLogger.LogWarning($"[FAT] super flat: a content-area test threw, skipped (logged once): {ex.Message}");
                    }
                }
                return false;
            }
            finally { t_inContent--; }
        }

        private static readonly System.Collections.Generic.HashSet<string> s_bypassLogged = new System.Collections.Generic.HashSet<string>();
        private static void LogBypass(string what, float x, float z)
        {
            lock (s_bypassLogged) { if (!s_bypassLogged.Add(what)) return; }
            FiresLogger.LogInfo($"[FAT] super flat: {what} ALLOWED in a content area (first at {x:0},{z:0}); outside content areas it stays blocked.");
        }

        private static bool s_logged;

        private static float Radius => FiresWorldProfile.Current?.WorldRadius ?? FiresWorldProfile.SuperFlatRadius;

        internal static float HeightAt(float wx, float wy)
        {
            float d = Mathf.Sqrt(wx * wx + wy * wy) - Radius;
            if (d <= 0f) return FiresWorldProfile.SuperFlatHeight;
            return Mathf.Lerp(FiresWorldProfile.SuperFlatHeight, SeaFloorY, Mathf.Clamp01(d / ShoreWidth));
        }

        private static bool Land(float wx, float wy) => wx * wx + wy * wy <= Radius * Radius;
        internal static bool IsLand(float wx, float wz) => Land(wx, wz);   // ONE land rule for the flat field, the biome postfix and the corner blend

        private static void LogOnce(string where)
        {
            if (s_logged) return;
            s_logged = true;
            FiresLogger.LogInfo($"[FAT] super flat: this world is the admin build world - flat Meadows at y {FiresWorldProfile.SuperFlatHeight:0} "
                + $"inside radius {Radius:0}, ocean past it; no locations, vegetation, clutter or spawning (first seen in {where}).");
        }

        // ── Ground ──────────────────────────────────────────────────────────
        // Postfixes, Last: whatever any other generator answered (vanilla, Fires, a paint, a BetterContinents map), a super flat world's
        // answer is this one. The mask is black (no forest / clutter weighting).
        [HarmonyPatch(typeof(WorldGenerator), nameof(WorldGenerator.GetBiomeHeight))]
        private static class Height
        {
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(float wx, float wy, ref Color mask, ref float __result)
            {
                if (!On) return;
                __result = HeightAt(wx, wy);
                if (ContentAt(wx, wy)) return;   // A content area keeps its biome's mask (vegetation density / forest)
                mask = Color.black;
            }
        }

        [HarmonyPatch(typeof(WorldGenerator), nameof(WorldGenerator.GetBiome), typeof(float), typeof(float), typeof(float), typeof(bool))]
        private static class BiomeAt
        {
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(float wx, float wy, ref Heightmap.Biome __result)
            {
                if (!On) return;
                if (Land(wx, wy) && ContentAt(wx, wy)) return;   // The earlier answer (a forced biome) stands
                __result = Land(wx, wy) ? Heightmap.Biome.Meadows : Heightmap.Biome.Ocean;
            }
        }

        [HarmonyPatch(typeof(WorldGenerator), nameof(WorldGenerator.GetBiome), typeof(Vector3))]
        private static class BiomeAtPoint
        {
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(Vector3 __0, ref Heightmap.Biome __result)   // positional: vanilla's parameter name is not ours to rely on
            {
                if (!On) return;
                if (Land(__0.x, __0.z) && ContentAt(__0.x, __0.z)) return;
                __result = Land(__0.x, __0.z) ? Heightmap.Biome.Meadows : Heightmap.Biome.Ocean;
            }
        }

        // ── Biome sectors ─────────────────────────────────────────
        // The ground once drew as SAND with grass only on the temple patch. Heightmap corners, the environment and the
        // player's biome read the biome-sector grid (GetBiomeSector), not GetBiome, so the client mesh kept vanilla's biomes. On a super
        // flat world every sector inside the land is Meadows (the same kind of BiomeSector BiomePaint answers with); Ocean past the edge.
        private static BiomeSector s_meadows, s_ocean;
        private static BiomeSector Sector(float wx, float wz)
            => Land(wx, wz) ? (s_meadows ?? (s_meadows = new BiomeSector(null, Heightmap.Biome.Meadows))) : (s_ocean ?? (s_ocean = new BiomeSector(null, Heightmap.Biome.Ocean)));

        [HarmonyPatch(typeof(WorldGenerator), nameof(WorldGenerator.GetBiomeSector), typeof(float), typeof(float), typeof(bool))]
        private static class SectorAt
        {
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(float __0, float __1, ref BiomeSector __result) { if (On && !(Land(__0, __1) && ContentAt(__0, __1))) __result = Sector(__0, __1); }
        }

        [HarmonyPatch(typeof(WorldGenerator), nameof(WorldGenerator.GetBiomeSector), typeof(Vector3), typeof(bool))]
        private static class SectorAtPoint
        {
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(Vector3 __0, ref BiomeSector __result) { if (On && !(Land(__0.x, __0.z) && ContentAt(__0.x, __0.z))) __result = Sector(__0.x, __0.z); }
        }

        // ── Locations: none, the start temple included ──────────────────────
        // Every location's quota already counts as met, so none is ever planned; PlaceLocations is a second wall. Without a start temple
        // a new character spawns at the world's centre on the flat ground.
        [HarmonyPatch(typeof(ZoneSystem), "CountNrOfLocation")]
        private static class NoLocationPlans
        {
            [HarmonyPriority(Priority.First)]
            private static bool Prefix(ZoneSystem.ZoneLocation location, ref int __result)
            {
                if (!On || location == null) return true;
                // The start temple and nothing else: without it a new character never spawned (the client waited for
                // a StartTemple that never came). Vanilla plans it centre-first, so it lands at the middle on the flat ground.
                if (location.m_prefabName == StartTempleName || IsAllowedExtra(location.m_prefabName)) return true;   // The Mausoleum auto-places too
                LogOnce("location planning");
                __result = location.m_quantity;
                return false;
            }
        }

        // Only the start temple is ever planned on a super flat world, so PlaceLocations runs (it places the temple and nothing
        // else exists to place). Its postfix logs where the temple went, once.
        internal const string StartTempleName = "StartTemple";
        // Nothing on it except the start temple and the Mausoleum (which auto-places): FiresMausoleum's one
        // near-spawn location (FiresMausoleumCrypt, quantity 1, centre-first) is planned like vanilla plans it.
        internal const string MausoleumLocationName = "FiresMausoleumCrypt";
        private static bool IsAllowedExtra(string prefab) => prefab == MausoleumLocationName;
        private static bool s_templeLogged;

        [HarmonyPatch(typeof(ZoneSystem), "PlaceLocations")]
        private static class TempleOnly
        {
            private static void Postfix(ZoneSystem __instance)
            {
                if (!On || s_templeLogged || __instance == null) return;
                try
                {
                    var instances = Traverse.Create(__instance).Field("m_locationInstances").GetValue() as System.Collections.IDictionary;
                    if (instances == null) return;
                    foreach (var li in instances.Values)
                    {
                        var t = Traverse.Create(li);
                        var loc = t.Field("m_location").GetValue();
                        string name = loc != null ? Traverse.Create(loc).Field("m_prefabName").GetValue<string>() : null;
                        if (name != StartTempleName || !t.Field("m_placed").GetValue<bool>()) continue;
                        Vector3 at = t.Field("m_position").GetValue<Vector3>();
                        s_templeLogged = true;
                        FiresLogger.LogInfo($"[FAT] super flat: start temple placed at {at.x:0},{at.z:0} (y {at.y:0.0}); no other location on this world.");
                        return;
                    }
                }
                catch (System.Exception e) { FiresLogger.LogWarning("[FAT] super flat: could not read the start temple's spot: " + e.Message); s_templeLogged = true; }
            }
        }

        // ── Vegetation, pickables, rocks, grass: none ───────────────────────
        [HarmonyPatch(typeof(ZoneSystem), "PlaceVegetation")]
        private static class NoVegetation
        {
            [HarmonyPriority(Priority.First)]
            private static bool Prefix(Vector2s __0)   // positional: the zone
            {
                if (!On) return true;
                var c = ZoneSystem.GetZonePos(__0);
                if (ContentAt(c.x, c.z)) { LogBypass("vegetation", c.x, c.z); return true; }
                LogOnce("vegetation");
                return false;
            }
        }

        // The auto clutter gate itself sits FIRST in FatVanillaHooks' one GenerateVegPatch prefix (a separate prefix here was
        // overridden by ClutterSpread's own patch). Patches built before the profile said super flat are cleared once, after the frame.
        private static bool s_clearPending = true;
        internal static void NoteAutoClutterBlocked() => LogOnce("auto clutter (vanilla grass; FAP-painted clutter is unaffected)");

        [HarmonyPatch(typeof(ClutterSystem), "LateUpdate")]
        private static class ClearAutoClutterOnce
        {
            private static void Postfix(ClutterSystem __instance)
            {
                if (!On) { s_clearPending = true; return; }
                if (!s_clearPending) return;
                s_clearPending = false;
                __instance.ClearAll();
                Debug.Log("[FiresAdminTerrain] [FAT] super flat: auto clutter patches cleared once (built before the profile was known)");
            }
        }

        // ── Spawning: none of any kind ──────────────────────────────────────
        [HarmonyPatch(typeof(SpawnSystem), "UpdateSpawning")]
        private static class NoSpawnSystem
        {
            [HarmonyPriority(Priority.First)]
            private static bool Prefix(SpawnSystem __instance) => !On || Allowed("the spawn system", __instance);
        }

        [HarmonyPatch(typeof(CreatureSpawner), "UpdateSpawner")]
        private static class NoCreatureSpawners
        {
            [HarmonyPriority(Priority.First)]
            private static bool Prefix(CreatureSpawner __instance) => !On || Allowed("creature spawners", __instance);
        }

        [HarmonyPatch(typeof(SpawnArea), "UpdateSpawn")]
        private static class NoSpawnAreas
        {
            [HarmonyPriority(Priority.First)]
            private static bool Prefix(SpawnArea __instance) => !On || Allowed("spawn areas", __instance);
        }

        // Spawning runs inside a content area (random events stay off everywhere).
        private static bool Allowed(string what, Component c)
        {
            if (c == null) return false;
            var p = c.transform.position;
            if (!ContentAt(p.x, p.z)) return false;
            LogBypass(what, p.x, p.z);
            return true;
        }

        // Auto clutter grows in a content area (FatVanillaHooks' GenerateVegPatch gate asks this).
        internal static bool ClutterAllowed(Vector2Int patchID, float size)
        {
            float x = patchID.x * size, z = patchID.y * size;   // vanilla's patch centre
            if (!ContentAt(x, z)) return false;
            LogBypass("auto clutter", x, z);
            return true;
        }

        [HarmonyPatch(typeof(RandEventSystem), "FixedUpdate")]
        private static class NoRandomEvents
        {
            [HarmonyPriority(Priority.First)]
            private static bool Prefix() => !On;
        }

        // ── Zone water surfaces on the land: hidden ─────────────────────────
        // Ocean and water showed right near spawn: 8 zone 'WaterSurface' renderers (Custom/FiresWater) were
        // drawn with their top at y 50 (the ground height) where the probe found NO liquid.
        // Every zone carries vanilla's sea WaterVolume at y 30; on a super flat world a zone wholly
        // inside the land radius has no sea, so its surface is not drawn. Zones that reach the shore or the ocean keep theirs.
        // Built-piece water (a hot tub: a WaterVolume under a ZNetView) is never touched.
        private const float ZoneHalfDiagonal = 46f;   // 64 m zone: half its diagonal, rounded up
        private static int s_hiddenSurfaces;

        [HarmonyPatch(typeof(WaterVolume), "OnEnable")]
        private static class LandHasNoSea
        {
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(WaterVolume __instance)
            {
                if (!On || __instance == null || __instance.m_waterSurface == null) return;
                if (__instance.GetComponentInParent<ZNetView>() != null) return;
                Vector3 c = __instance.transform.position;
                if (Mathf.Sqrt(c.x * c.x + c.z * c.z) + ZoneHalfDiagonal >= Radius) return;
                // Hidden here ("zone sea surface hidden" logged) yet 8 were drawn again
                // later: something re-sets renderer.enabled). forceRenderingOff is a separate switch that code toggling .enabled never
                // touches, so the surface stays undrawn for the zone's life.
                if (__instance.m_waterSurface.forceRenderingOff) return;
                __instance.m_waterSurface.forceRenderingOff = true;
                if (++s_hiddenSurfaces == 1 || s_hiddenSurfaces % 50 == 0)
                    FiresLogger.LogInfo($"[FAT] super flat: zone sea surface hidden on the land ({s_hiddenSurfaces} so far; first at {c.x:0},{c.z:0}) - no sea inside the {Radius:0} m land radius.");
            }
        }

        // ── Every water renderer of ours over the land: off ─────────────────
        // Super flat draws ONLY flat terrain, no generated water of ours: one client sweep every 2 s, on a
        // super flat world only. Any renderer drawing a Fires water shader (Custom/FiresWater*, FAT inland water / combiners,
        // TossinShade's surfaces), a vanilla zone 'WaterSurface', or TossinShade's player-centred ocean (FiresClipmapOcean*,
        // FiresWaterRing*) is switched off with forceRenderingOff while it sits over the land - the player-centred oceans while the
        // player is inside the land. Past the edge they draw again: the vanilla-style ocean beyond 2500 m stays.
        private const float SweepSeconds = 2f, OceanInsideMargin = 300f;
        private static float s_nextSweep;
        private static readonly System.Collections.Generic.HashSet<Renderer> s_off = new System.Collections.Generic.HashSet<Renderer>();
        private static int s_sweepLogged = -1;

        private static bool IsOurWater(Renderer r)
        {
            string n = r.name;
            if (n == "WaterSurface" || n.StartsWith("FiresClipmapOcean", System.StringComparison.Ordinal) || n.StartsWith("FiresWaterRing", System.StringComparison.Ordinal)) return true;
            var m = r.sharedMaterial;
            string sh = m != null && m.shader != null ? m.shader.name : null;
            // Vanilla's own water shaders too (the distant sea plane), never falls / mist
            return sh != null && sh.IndexOf("Water", System.StringComparison.OrdinalIgnoreCase) >= 0 && sh.IndexOf("Fall", System.StringComparison.OrdinalIgnoreCase) < 0
                && sh.IndexOf("Mist", System.StringComparison.OrdinalIgnoreCase) < 0;
        }

        // A sea plane still showed further out: any mesh far bigger than a zone (a distant / LOD sea plane
        // drawn around the player) counts as player-centred too.
        private static bool PlayerCentred(Renderer r) => r.name.StartsWith("FiresClipmapOcean", System.StringComparison.Ordinal) || r.name.StartsWith("FiresWaterRing", System.StringComparison.Ordinal)
            || r.bounds.extents.x > 500f || r.bounds.extents.z > 500f;

        [HarmonyPatch(typeof(GameCamera), "LateUpdate")]
        private static class NoWaterOverLand
        {
            private static void Postfix()
            {
                if (!On) { if (s_off.Count > 0) { foreach (var r in s_off) if (r != null) r.forceRenderingOff = false; s_off.Clear(); } return; }
                if (Time.unscaledTime < s_nextSweep) return;
                s_nextSweep = Time.unscaledTime + SweepSeconds;
                var cam = GameCamera.instance != null ? GameCamera.instance.transform.position : Vector3.zero;
                bool playerInside = Mathf.Sqrt(cam.x * cam.x + cam.z * cam.z) < Radius - OceanInsideMargin;
                int hidden = 0;
                foreach (var r in UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
                {
                    if (r == null || !IsOurWater(r) || r.GetComponentInParent<ZNetView>() != null) continue;   // built-piece water (hot tubs) stays
                    Vector3 c = r.bounds.center;
                    bool overLand = PlayerCentred(r) ? playerInside : Mathf.Sqrt(c.x * c.x + c.z * c.z) < Radius - ShoreWidth;
                    if (overLand) { if (!r.forceRenderingOff) { r.forceRenderingOff = true; s_off.Add(r); } hidden++; }
                    else if (s_off.Remove(r)) r.forceRenderingOff = false;
                }
                s_off.RemoveWhere(r => r == null);
                if (hidden != s_sweepLogged)
                {
                    s_sweepLogged = hidden;
                    FiresLogger.LogInfo($"[FAT] super flat: {hidden} water renderer(s) of ours over the land kept undrawn (player {(playerInside ? "inside" : "at the edge")}).");
                }
            }
        }

        // A new world resets the one-time log line (a Dimensions hop can go from a super flat world to a normal one and back).
        [HarmonyPatch(typeof(ZNet), "OnDestroy")]
        private static class ResetLog
        {
            private static void Postfix() { s_logged = false; s_templeLogged = false; s_hiddenSurfaces = 0; s_sweepLogged = -1; s_off.Clear(); }
        }
    }
}
