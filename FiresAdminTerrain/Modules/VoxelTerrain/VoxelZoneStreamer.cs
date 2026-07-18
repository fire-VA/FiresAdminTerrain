using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // W1 — the WHOLE-WORLD voxel wrap (Stage W, wrap-and-subtract strategy).
    //
    // For each vanilla zone near the player (VoxelWrap mode only):
    //   1. Gather ground + biome per column from the LIVE heightmap DATA
    //      (main thread, array reads — fast).
    //   2. Create the zone's chunk stack (2×2 XZ, surface band ± cave depth in
    //      Y) with MeshAll=true and a deferred generate payload; the WORKER
    //      fills density + carves the deterministic cave noise + meshes.
    //   3. When every chunk's collider is live, hide the vanilla heightmap's
    //      RENDERER + COLLIDER — the voxel mesh IS the ground now. The
    //      Heightmap component stays alive as the data API (heights, biomes,
    //      minimap, placement all keep reading it). Swap order matters: never
    //      hide vanilla before voxel colliders exist or players fall through.
    //   4. Leaving the radius (or toggling mode to Vanilla) reverses it:
    //      re-enable vanilla FIRST, then drop the chunks. Everything
    //      regenerates deterministically from the world seed on return.
    //
    // Terrain ops (hoe/pickaxe) still write heightmap data; Regenerate on a
    // wrapped zone re-hides vanilla and queues a full re-wrap so the voxel
    // surface follows the edit. (Brush deltas in that zone are regenerated
    // away — acceptable until P1 persistence lands.)
    public static class VoxelZoneStreamer
    {
        private sealed class ZoneWrap
        {
            public Vector2i Zone;
            public Heightmap Hm;
            public readonly List<Vector3Int> Keys = new List<Vector3Int>();
            public bool Swapped;
            public float StartedAt;      // wrap start (stuck-swap diagnostics)
            public float NextDiagAt;     // throttle for blocked-swap logging
            public float LastRewrapAt;   // debounce for terraform rewraps
            // Round-91 — true when any border/apron column had to fall back to
            // WorldGenerator math because a neighbour heightmap wasn't loaded
            // yet. Such borders DISAGREE with the neighbour once it loads (no
            // TerrainComp + different height path) → the persistent zone-edge
            // split. Healed by a targeted in-place regen when the neighbour
            // wraps (see StartWrap).
            public bool UsedFallback;
        }

        private static readonly Dictionary<Vector2i, ZoneWrap> s_zones = new Dictionary<Vector2i, ZoneWrap>();
        private static readonly List<Vector2i> s_scratchDrop = new List<Vector2i>();
        // Zones whose heightmap DATA changed (terrain ops) — rewrapped one per
        // tick. Includes neighbours: their border/apron samples read into the
        // regenerated zone, so skipping them would leave hairline border lips.
        private static readonly HashSet<Vector2i> s_dirty = new HashSet<Vector2i>();
        private static readonly List<(Vector2i zone, float due)> s_waterQueue = new List<(Vector2i, float)>();
        private static readonly HashSet<Vector2i> s_waterDone = new HashSet<Vector2i>();
        private static readonly List<Vector3> s_springScratch = new List<Vector3>();
        private static readonly List<Vector3> s_springSpots = new List<Vector3>();
        private static float s_nextSummary;
        private static GameObject s_driverGO;

        private static readonly AccessTools.FieldRef<Heightmap, bool> s_isDistantLod =
            AccessTools.FieldRefAccess<Heightmap, bool>("m_isDistantLod");
        private static readonly AccessTools.FieldRef<Heightmap, MeshCollider> s_hmCollider =
            AccessTools.FieldRefAccess<Heightmap, MeshCollider>("m_collider");

        public static int WrappedZoneCount => s_zones.Count;

        // W3-A1 spawn gate: a zone is SOLID once its voxel colliders are live
        // (Swapped flips exactly then). The area check mirrors vanilla's
        // IsAreaReady footprint so first spawns/teleports never land on
        // ground that hasn't meshed yet.
        public static bool IsZoneSolid(Vector2i zone)
            => s_zones.TryGetValue(zone, out var w) && w.Swapped;

        // Zone is voxel TERRITORY (wrapping OR wrapped). The quilt must hide
        // its near tile the moment wrapping starts: in VoxelOnly the voxel
        // meshes render immediately, and the not-yet-Swapped window had the
        // analytic tile (no carves, no collision, −0.8m) drawn OVER the real
        // ground — the phantom sheets covering carves and cave mouths.
        public static bool IsZoneKnown(Vector2i zone) => s_zones.ContainsKey(zone);

        public static bool AreaSolid(Vector3 point)
        {
            var zone = ZoneSystem.GetZone(point);
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                    if (!IsZoneSolid(new Vector2i(zone.x + dx, zone.y + dz)))
                        return false;
            return true;
        }

        private static bool ModeActive =>
            FiresVoxelConfig.Enabled != null && FiresVoxelConfig.Enabled.Value
            && FiresVoxelConfig.WorldMode != null
            && FiresVoxelConfig.WorldMode.Value != VoxelWorldMode.Vanilla;

        // ── Lifecycle ────────────────────────────────────────────────────────
        [HarmonyPatch(typeof(ZoneSystem), "Start")]
        private static class ZoneSystem_Start_VoxelStreamer
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                // W3-A3 — headless streams too, but ONLY in VoxelOnly (there
                // is no vanilla ground to stand on; the server needs voxel
                // colliders around its peers). VoxelWrap stays client-only:
                // vanilla terrain is authoritative there.
                if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null
                    && (FiresVoxelConfig.WorldMode == null
                        || FiresVoxelConfig.WorldMode.Value != VoxelWorldMode.VoxelOnly)) return;
                if (s_driverGO != null) return;
                s_driverGO = new GameObject("FiresVoxelZoneStreamer");
                s_driverGO.AddComponent<StreamerDriver>();
                Debug.Log($"[FiresVoxel] zone streamer up (mode={FiresVoxelConfig.WorldMode?.Value}).");
            }
        }

        private sealed class StreamerDriver : MonoBehaviour
        {
            private float _nextTick;

            private void FixedUpdate()
            {
                try { VoxelCavePopGuard.FixedTick(); }
                catch (System.Exception ex) { Debug.LogWarning($"[FiresVoxel] pop guard threw: {ex.Message}"); }
            }

            private void Update()
            {
                // Impostors draw per frame (DrawMeshInstanced is a one-frame
                // submission) — outside the 0.25s streamer throttle.
                try { FarVegImpostors.Tick(); }
                catch (System.Exception ex) { Debug.LogWarning($"[FiresVoxel] far impostors threw: {ex.Message}"); }

                if (Time.time < _nextTick) return;
                _nextTick = Time.time + 0.25f;
                try { Tick(); }
                catch (System.Exception ex) { Debug.LogWarning($"[FiresVoxel] streamer tick threw: {ex}"); }
            }

            private void OnDestroy()
            {
                s_zones.Clear();
                s_driverGO = null;
            }
        }

        // ── Main tick ────────────────────────────────────────────────────────
        private static void Tick()
        {
            var player = Player.m_localPlayer;

            // Mode off → unwind every wrapped zone.
            if (!ModeActive)
            {
                if (s_zones.Count > 0) UnwrapAll();
                return;
            }

            bool voxelOnly = FiresVoxelConfig.WorldMode.Value == VoxelWorldMode.VoxelOnly;
            bool headless = SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null;

            // VoxelOnly is only meaningful on a Fires world (the A1 patches
            // gate on the generator too) — on anything else, stand down.
            if (voxelOnly && !FiresWorldGenBase.Active)
            {
                if (s_zones.Count > 0) UnwrapAll();
                return;
            }

            Vector3 preSpawnCenter = Vector3.zero;
            if (headless)
            {
                // W3-A3 — the dedi streams COLLIDERS around its peers. Zone
                // discovery below is heightmap-driven, and vanilla only keeps
                // zones alive near peer refpositions, so WrapAllLoaded IS the
                // per-peer coverage. No peers → free everything.
                if (ZNet.instance == null || ZNet.instance.GetPeers().Count == 0)
                {
                    if (s_zones.Count > 0) UnwrapAll();
                    return;
                }
            }
            else
            {
                // W3-A1: in VoxelOnly there is no vanilla ground, so the spawn
                // area must wrap BEFORE the player exists — center on the
                // network reference position (vanilla points it at the pending
                // spawn during the respawn wait). Never unwind while waiting.
                if (player == null && !voxelOnly)
                {
                    if (s_zones.Count > 0) UnwrapAll();
                    s_paintDone.Clear();
                    s_paintPending.Clear();
                    return;
                }
                if (player == null)
                {
                    if (ZNet.instance != null) preSpawnCenter = ZNet.instance.GetReferencePosition();
                    if (preSpawnCenter == Vector3.zero) return;   // not waiting on a spawn yet
                }
            }

            if (WorldGenerator.instance != null)
                VoxelCaveNoise.SetSeed(WorldGenerator.instance.GetSeed());

            // W2 — hydrology must exist before any zone wraps so river beds
            // carve in from the very first chunk (build takes ~2-4s on a
            // worker at login; vanilla terrain shows until then). Headless
            // skips it: rivers live in the base on Fires worlds and water
            // placement is client-side.
            if (!headless
                && FiresVoxelConfig.HydrologyEnabled != null && FiresVoxelConfig.HydrologyEnabled.Value)
            {
                VoxelHydrology.EnsureBuilding();
                if (!VoxelHydrology.Ready) return;
            }

            // Drain due water placements (2s after a zone's swap, so the
            // zone's own pre-existing pours have loaded for the idempotency
            // check). Client-side world furniture — never on the dedi.
            for (int i = s_waterQueue.Count - 1; i >= 0; i--)
            {
                if (Time.time < s_waterQueue[i].due) continue;
                var zq = s_waterQueue[i].zone;
                s_waterQueue.RemoveAt(i);
                if (headless) continue;
                var swWater = System.Diagnostics.Stopwatch.StartNew();
                try { PlaceWaterForZone(zq); }
                catch (System.Exception ex) { Debug.LogWarning($"[FiresVoxel] water placement threw for ({zq.x},{zq.y}): {ex.Message}"); }
                if (swWater.Elapsed.TotalMilliseconds >= 25.0)
                    Debug.Log($"[FiresVoxel] zone ({zq.x},{zq.y}) water build took {swWater.Elapsed.TotalMilliseconds:0} ms.");
            }

            Vector3 pp = player != null ? player.transform.position : preSpawnCenter;
            float radius = FiresVoxelConfig.WrapRadius.Value;
            bool wrapAll = headless
                || (FiresVoxelConfig.WrapAllLoaded != null && FiresVoxelConfig.WrapAllLoaded.Value);

            // ROLLING AUTO-HEAL — the ZERO-seams guarantee. Canonical border
            // mirroring runs at every generate/edit/restore, and this sweep
            // re-verifies a small slice of live chunks each tick (full pass
            // ~30s at 444 chunks), re-mirroring + remeshing anything that
            // diverged for ANY reason. No divergence survives a minute.
            AutoHealTick();
            VoxelGenWaterMesh.Tick();
            PaintTick();
            GhostBurst(pp);

            // Unwrap: dead heightmaps (zone unloaded); in radius mode also
            // zones beyond radius+hysteresis.
            s_scratchDrop.Clear();
            foreach (var kv in s_zones)
            {
                var w = kv.Value;
                if (w.Hm == null) { s_scratchDrop.Add(kv.Key); continue; }
                if (wrapAll) continue;
                Vector3 c = w.Hm.transform.position;
                float dx = Mathf.Abs(pp.x - c.x), dz = Mathf.Abs(pp.z - c.z);
                if (Mathf.Max(dx, dz) > radius + 48f) s_scratchDrop.Add(kv.Key);
            }
            foreach (var z in s_scratchDrop) Unwrap(z);

            // Terraform-dirty zones: regenerate IN PLACE, one per tick, 2s
            // debounce per zone. Vanilla stays hidden and the old voxel
            // meshes/colliders stay live until each chunk's fresh mesh swaps
            // in — no window where both or neither terrain exists.
            foreach (var dz in s_dirty)
            {
                if (!s_zones.TryGetValue(dz, out var dwrap)) { s_dirty.Remove(dz); break; }
                if (Time.time - dwrap.LastRewrapAt < 2f) continue;   // debounce — retry next tick
                s_dirty.Remove(dz);
                dwrap.LastRewrapAt = Time.time;
                if (dwrap.Hm == null) { Unwrap(dz); break; }
                if (FiresVoxelConfig.LogTimings != null && FiresVoxelConfig.LogTimings.Value)
                    Debug.Log($"[FiresVoxel] zone ({dz.x},{dz.y}) terrain data changed → in-place regen ({dwrap.Keys.Count} chunks).");
                BuildZoneChunks(dwrap);
                // In-place rewraps left neighbour rings stale for a full sweep
                // pass (~30s+) — the priority heal only fired on FIRST-time
                // swaps. Queue it here too so terraform borders converge in
                // ticks (entries skip-and-requeue while chunks are mid-regen).
                QueuePriorityHeal(dwrap);
                break;
            }

            // W3.3 — paint-only ops need no remesh: drain a few mask-texture
            // refreshes per tick (1089-texel copy + rebind each).
            VoxelWorld.DrainPaintDirty(4);

            // Watchdog — something re-enabled a swapped zone's vanilla terrain
            // (Heightmap rebuild paths, other mods): re-hide and SAY SO, so the
            // log names the fight instead of us guessing.
            foreach (var kv in s_zones)
            {
                var w = kv.Value;
                if (!w.Swapped || w.Hm == null) continue;
                var rend = w.Hm.GetComponent<MeshRenderer>();
                var col = s_hmCollider(w.Hm);
                bool rendBack = rend != null && rend.enabled;
                bool colBack = col != null && col.enabled;
                if (rendBack || colBack)
                {
                    HideVanilla(w.Hm);
                    Debug.LogWarning($"[FiresVoxel] zone ({kv.Key.x},{kv.Key.y}) vanilla terrain RE-ENABLED externally " +
                        $"(renderer={rendBack}, collider={colBack}) — re-hidden. If this repeats, something is fighting the swap.");
                }
            }

            // Periodic streamer state summary (LogTimings) — the ground truth
            // for diagnosing wrap behaviour from logs alone.
            if (FiresVoxelConfig.LogTimings != null && FiresVoxelConfig.LogTimings.Value
                && Time.time >= s_nextSummary)
            {
                s_nextSummary = Time.time + 10f;
                int swapped = 0;
                foreach (var kv in s_zones) if (kv.Value.Swapped) swapped++;
                Debug.Log($"[FiresVoxel] streamer: zones={s_zones.Count} swapped={swapped} " +
                    $"pendingSwap={s_zones.Count - swapped} dirty={s_dirty.Count} chunks={VoxelWorld.ChunkCount}");
            }

            // Wrap candidates + completion checks. ≤1 NEW zone per tick keeps
            // the worker fed without bursts.
            bool startedOne = false;
            foreach (var hm in Heightmap.GetAllHeightmaps())
            {
                if (hm == null || s_isDistantLod(hm)) continue;
                Vector3 c = hm.transform.position;
                if (!wrapAll)
                {
                    float dx = Mathf.Abs(pp.x - c.x), dz = Mathf.Abs(pp.z - c.z);
                    if (Mathf.Max(dx, dz) > radius) continue;
                }

                var zone = ZoneSystem.GetZone(c);
                if (s_zones.TryGetValue(zone, out var wrap))
                {
                    if (!wrap.Swapped) TrySwap(wrap);
                    continue;
                }
                if (startedOne) continue;
                startedOne = StartWrap(zone, hm);
            }
        }

        // Ground + biome per column (34×34, apron included), from heightmap
        // DATA (never physics — round-87 lesson; +ε — round-89 border fix).
        private static void GatherColumns(int kx, int kz, out float[] ground, out Color32[] biome,
            out float minG, out float maxG, ref bool usedFallback)
        {
            var wg = WorldGenerator.instance;
            float ox = kx * 32f, oz = kz * 32f;
            ground = new float[VoxelChunk.SAMPLES * VoxelChunk.SAMPLES];
            biome = new Color32[VoxelChunk.SAMPLES * VoxelChunk.SAMPLES];
            minG = float.MaxValue; maxG = float.MinValue;
            for (int z = -1; z <= VoxelChunk.CELLS; z++)
            {
                for (int x = -1; x <= VoxelChunk.CELLS; x++)
                {
                    float wx = ox + x, wz = oz + z;
                    float g;
                    // ONE SOURCE. Heightmap DATA is the ground truth — it IS
                    // our generator (the height prefixes feed vanilla's grids)
                    // PLUS every edit (hoe/pickaxe/IH/WEC comp deltas). The
                    // old code flipped per-sample between heightmap data and
                    // the raw analytic generator depending on whether a
                    // TerrainComp existed — a time-varying source switch
                    // inside the exact function that decides border data
                    // (hash churn on every first terraform, interpolated-vs-
                    // analytic shelves at borders). The pure generator now
                    // answers ONLY when the heightmap isn't loaded, and that
                    // border stays flagged provisional (round-91 heal).
                    if (!Heightmap.GetHeight(new Vector3(wx + 0.013f, 0f, wz + 0.013f), out g))
                    {
                        g = wg != null ? wg.GetHeight(wx, wz) : 30f;
                        usedFallback = true;   // neighbour hm not loaded — border provisional
                    }
                    int col = (x + 1) + VoxelChunk.SAMPLES * (z + 1);
                    ground[col] = g;
                    // ONE biome authority: paint overlay > env box > worldgen
                    // (audit §2-H: painted biome COLOR never reached the voxel
                    // skin because this read raw worldgen). Paint ops trigger
                    // Regenerate → zone dirty → rewrap, and biome is in the
                    // change-gate hash, so repainted tint lands automatically.
                    // Corner-blended (vanilla parity) — one-hot per vertex
                    // drew hard pixel edges at every biome boundary.
                    biome[col] = wg != null
                        ? BiomeCornerBlend.Sample(wx, wz)
                        : new Color32(0, 0, 0, 0);
                    if (g < minG) minG = g;
                    if (g > maxG) maxG = g;
                }
            }
        }

        private static readonly List<VoxelChunk> s_healScratch = new List<VoxelChunk>();
        private static int s_healCursor;

        // Event-driven heal: chunk keys queued at zone swap (the zone + its
        // neighbour ring) get their borders re-mirrored ahead of the rolling
        // sweep, so a rewrap converges in ticks instead of ~30s.
        private static readonly Queue<Vector3Int> s_priorityHeal = new Queue<Vector3Int>();
        private static readonly HashSet<Vector3Int> s_priorityHealSet = new HashSet<Vector3Int>();

        private static void QueuePriorityHeal(ZoneWrap wrap)
        {
            foreach (var key in wrap.Keys)
            {
                for (int dz = -1; dz <= 1; dz++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        var k = new Vector3Int(key.x + dx, key.y, key.z + dz);
                        if (s_priorityHealSet.Add(k)) s_priorityHeal.Enqueue(k);
                    }
                }
            }
        }

        private static void AutoHealTick()
        {
            const int PER_TICK = 4;
            int budget = PER_TICK;

            // Bounded scan: entries for chunks that are mid-regen go BACK on
            // the queue (they used to be silently dropped — the one-shot heal
            // for a chunk that happened to be regenerating was lost forever).
            int scan = s_priorityHeal.Count;
            while (budget > 0 && scan-- > 0 && s_priorityHeal.Count > 0)
            {
                var key = s_priorityHeal.Dequeue();
                if (!VoxelWorld.TryGetChunk(key, out var pc) || pc == null)
                { s_priorityHealSet.Remove(key); continue; }
                if (pc.PendingGenerate) { s_priorityHeal.Enqueue(key); continue; }
                s_priorityHealSet.Remove(key);
                // Remesh when ANY heal class applies: ring data diff, a mesh
                // that never applied the current data version, or a frontier
                // mask change (neighbour arrived/left). Skipping converged
                // chunks halves the mesh+bake bill of every wrap heal.
                if (pc.MirrorBordersNow(true) > 0
                    || pc.Version != pc.AppliedVersion
                    || VoxelMeshPipeline.ComputeFrontierMask(pc) != pc.FrontierMask
                    || VoxelMeshPipeline.ComputeOrphanMask(pc) != pc.OrphanMask)
                    VoxelMeshPipeline.EnqueueRemesh(pc);
                budget--;
            }

            // The fallback sweep gets its OWN budget. Sharing one pot starved
            // it whenever the priority queue stayed fed (constant zone churn
            // while exploring) — a chunk that went stale outside a wrap event
            // never healed (audit: dataDiff=122 persisting across sweeps).
            s_healScratch.Clear();
            foreach (var c in VoxelWorld.AllChunks) s_healScratch.Add(c);
            if (s_healScratch.Count == 0) return;
            // Deterministic order — Dictionary enumeration reshuffles under
            // wrap/unwrap churn and let chunks slide past the modulo cursor
            // unvisited for minutes while the player moved.
            s_healScratch.Sort(s_chunkKeyOrder);
            for (int k = 0; k < PER_TICK; k++)
            {
                s_healCursor = (s_healCursor + 1) % s_healScratch.Count;
                var c = s_healScratch[s_healCursor];
                if (c == null || c.PendingGenerate) continue;
                // Data diff OR a mesh that never applied the current data
                // version — the second class was invisible to the sweep
                // (converged data, stale mesh) and persisted until the manual
                // console heal.
                if (c.MirrorBordersNow(true) > 0 || c.Version != c.AppliedVersion)
                    VoxelMeshPipeline.EnqueueRemesh(c);
            }
        }

        private static readonly System.Comparison<VoxelChunk> s_chunkKeyOrder = (a, b) =>
        {
            if (a == null || b == null) return (a == null ? 0 : 1) - (b == null ? 0 : 1);
            int c = a.Key.x.CompareTo(b.Key.x);
            if (c != 0) return c;
            c = a.Key.z.CompareTo(b.Key.z);
            return c != 0 ? c : a.Key.y.CompareTo(b.Key.y);
        };

        // FNV-1a over the generate inputs (ground bits, biome, caves flag,
        // hydrology readiness — rivers carve once Ready flips, so that first
        // regen must not be gated away).
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
        private struct FloatBits
        {
            [System.Runtime.InteropServices.FieldOffset(0)] public float F;
            [System.Runtime.InteropServices.FieldOffset(0)] public uint U;
        }

        private static uint HashColumns(float[] ground, Color32[] biome, bool caves)
        {
            unchecked
            {
                uint h = 2166136261u;
                var fb = default(FloatBits);
                for (int i = 0; i < ground.Length; i++)
                {
                    fb.F = ground[i];
                    uint b = fb.U;
                    h = (h ^ (b & 0xFF)) * 16777619u;
                    h = (h ^ ((b >> 8) & 0xFF)) * 16777619u;
                    h = (h ^ ((b >> 16) & 0xFF)) * 16777619u;
                    h = (h ^ (b >> 24)) * 16777619u;
                }
                for (int i = 0; i < biome.Length; i++)
                {
                    var c = biome[i];
                    h = (h ^ c.r) * 16777619u;
                    h = (h ^ c.g) * 16777619u;
                    h = (h ^ c.b) * 16777619u;
                }
                if (caves) h = (h ^ 0x5Au) * 16777619u;
                if (FiresVoxelConfig.HydrologyEnabled != null && FiresVoxelConfig.HydrologyEnabled.Value
                    && VoxelHydrology.Ready) h = (h ^ 0xA5u) * 16777619u;
                return h;
            }
        }

        // Generate (or REgenerate, in place) the zone's chunk stack. On a
        // swapped zone this never touches the vanilla heightmap — existing
        // chunks get a fresh generate payload and remesh seamlessly, so a
        // terraform rewrap has NO window where both/neither terrain exists.
        private static void BuildZoneChunks(ZoneWrap wrap)
        {
            var zone = wrap.Zone;
            int kx0 = FloorDiv(Mathf.RoundToInt(zone.x * 64f - 32f), 32);
            int kz0 = FloorDiv(Mathf.RoundToInt(zone.y * 64f - 32f), 32);
            bool caves = FiresVoxelConfig.GenerateCaves.Value;
            int caveDepth = Mathf.CeilToInt(FiresVoxelConfig.CaveMaxDepth.Value);

            var newKeys = new List<Vector3Int>(12);
            var armed = new List<VoxelChunk>(12);
            bool usedFallback = false;
            for (int cz = 0; cz < 2; cz++)
            {
                for (int cx = 0; cx < 2; cx++)
                {
                    int kx = kx0 + cx, kz = kz0 + cz;
                    GatherColumns(kx, kz, out var ground, out var biome, out float minG, out float maxG, ref usedFallback);
                    uint genHash = HashColumns(ground, biome, caves);

                    int kyLo = FloorDiv(Mathf.FloorToInt(minG) - (caves ? caveDepth : 4), 32);
                    int kyHi = FloorDiv(Mathf.CeilToInt(maxG) + 2, 32);
                    // CROSS-ZONE Y-UNION (audit §4-1): every in-plane quad at a
                    // face belongs to the +side chunk — if a lateral neighbour
                    // column has chunks at a ky we don't, the faces between us
                    // have NO owner and see-through strips appear exactly at
                    // zone borders ("always a Y difference"). Extend our range
                    // to cover every live lateral neighbour's; the neighbour
                    // zone does the same on its next build, so both sides
                    // converge to the union.
                    for (int d = 0; d < 4; d++)
                    {
                        var nc = d == 0 ? new Vector2Int(kx + 1, kz)
                               : d == 1 ? new Vector2Int(kx - 1, kz)
                               : d == 2 ? new Vector2Int(kx, kz + 1)
                                        : new Vector2Int(kx, kz - 1);
                        if (VoxelWorld.TryGetColumnKyRange(nc, out int nLo, out int nHi))
                        {
                            if (nLo < kyLo) kyLo = nLo;
                            if (nHi > kyHi) kyHi = nHi;
                        }
                    }
                    for (int ky = kyLo; ky <= kyHi; ky++)
                    {
                        var key = new Vector3Int(kx, ky, kz);
                        newKeys.Add(key);
                        if (VoxelWorld.TryGetChunk(key, out var existing))
                        {
                            // Change-gate: identical inputs → identical chunk.
                            // Skipping the no-op regen keeps the collider under
                            // the player untouched (rubber-band fix).
                            if (existing.LastGenHash == genHash && !existing.PendingGenerate)
                                continue;
                            // In-place: fresh generate payload onto the live
                            // chunk (brush deltas regenerate away — P1 caveat).
                            existing.MeshAll = true;
                            existing.LastGenHash = genHash;
                            existing.PrepareGenerate(ground, biome, caves);
                            armed.Add(existing);
                            continue;
                        }
                        var chunk = VoxelWorld.CreateStreamed(key);
                        chunk.MeshAll = true;
                        // Hidden until the swap flips voxel-on/vanilla-off in
                        // one frame (already-swapped zones render immediately).
                        chunk.SuppressRenderUntilSwap = !wrap.Swapped && !VoxelOnlyMode.Active;
                        chunk.LastGenHash = genHash;
                        chunk.PrepareGenerate(ground, biome, caves);
                        armed.Add(chunk);
                    }
                }
            }

            // Whole zone is armed (mutually pending) — NOW capture canonical
            // border values from every loaded generated neighbour (frozen
            // brush chunks included) so the worker generate ends with rings
            // that mirror their owners exactly. Same-batch borders need no
            // override: they generate from the same gather, deterministically.
            // Batch keys count as COVERING for the frontier mask — pending
            // siblings otherwise read "uncovered", every first-mesh grew
            // interior lips, and the swap heal re-meshed the whole zone to
            // shed them (double mesh+bake per wrap).
            s_batchKeys.Clear();
            foreach (var c in armed) s_batchKeys.Add(c.Key);
            foreach (var c in armed)
            {
                c.SetBorderOverrides(VoxelChunk.CaptureBorderOverrides(c.Key));
                VoxelMeshPipeline.EnqueueRemesh(c, s_batchKeys);
            }

            // Y-RANGE CONSISTENCY INVARIANT (the see-through fix): a chunk may
            // NOT be dropped while any lateral neighbour column still has a
            // chunk at its height — that neighbour's shared face quads are
            // OWNED by this chunk, and a dropped owner means nobody meshes
            // the plane: a permanent horizontal see-through band exactly at
            // the border ("always a Y difference, never XZ"). Terraform
            // range-shrinks (filling a pit raises minG) were dropping border
            // chunks their neighbours still referenced. Such chunks are KEPT
            // and re-armed with the fresh gather instead (mostly air/solid —
            // cheap); truly unneeded chunks (no lateral neighbours) drop.
            foreach (var old in wrap.Keys)
                if (!newKeys.Contains(old))
                {
                    bool neighboured =
                           VoxelWorld.TryGetChunk(new Vector3Int(old.x + 1, old.y, old.z), out var n1) && n1 != null
                        || VoxelWorld.TryGetChunk(new Vector3Int(old.x - 1, old.y, old.z), out var n2) && n2 != null
                        || VoxelWorld.TryGetChunk(new Vector3Int(old.x, old.y, old.z + 1), out var n3) && n3 != null
                        || VoxelWorld.TryGetChunk(new Vector3Int(old.x, old.y, old.z - 1), out var n4) && n4 != null;
                    if (neighboured && VoxelWorld.TryGetChunk(old, out var keep) && keep != null && !keep.HasBrushEdits)
                    {
                        // Re-arm on the column's fresh gather so its data
                        // stays canonical with the new terrain.
                        int lcx = old.x - kx0, lcz = old.z - kz0;
                        if (lcx >= 0 && lcx < 2 && lcz >= 0 && lcz < 2)
                        {
                            GatherColumns(old.x, old.z, out var kg, out var kb, out _, out _, ref usedFallback);
                            keep.MeshAll = true;
                            keep.LastGenHash = HashColumns(kg, kb, caves);
                            keep.PrepareGenerate(kg, kb, caves);
                            keep.SetBorderOverrides(VoxelChunk.CaptureBorderOverrides(old));
                            VoxelMeshPipeline.EnqueueRemesh(keep);
                        }
                        newKeys.Add(old);
                        continue;
                    }
                    VoxelWorld.DropChunkKey(old);
                    for (int dz = -1; dz <= 1; dz++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            var k = new Vector3Int(old.x + dx, old.y, old.z + dz);
                            if (s_priorityHealSet.Add(k)) s_priorityHeal.Enqueue(k);
                        }
                }
            wrap.Keys.Clear();
            wrap.Keys.AddRange(newKeys);
            wrap.UsedFallback = usedFallback;
        }

        private static readonly HashSet<Vector3Int> s_batchKeys = new HashSet<Vector3Int>();

        // ── Wrap: first-time build for a zone ────────────────────────────────
        private static bool StartWrap(Vector2i zone, Heightmap hm)
        {
            var wrap = new ZoneWrap { Zone = zone, Hm = hm, StartedAt = Time.time };
            BuildZoneChunks(wrap);
            s_zones[zone] = wrap;

            // Round-91 — HEAL PROVISIONAL BORDERS. Any already-wrapped
            // neighbour that seeded while THIS zone's heightmap was missing
            // used WorldGenerator fallback at the shared border → persistent
            // zone-edge split (real data ≠ fallback; brushing can't fix seed
            // data). Now that our heightmap exists, queue those neighbours for
            // an in-place regen — their border re-reads our real data and the
            // two sides converge exactly.
            int healed = 0;
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dz == 0) continue;
                    var n = new Vector2i(zone.x + dx, zone.y + dz);
                    if (s_zones.TryGetValue(n, out var nw) && nw.UsedFallback)
                    {
                        s_dirty.Add(n);
                        healed++;
                    }
                }
            if (FiresVoxelConfig.LogTimings != null && FiresVoxelConfig.LogTimings.Value)
                Debug.Log($"[FiresVoxel] wrapping zone ({zone.x},{zone.y}) → {wrap.Keys.Count} chunks queued" +
                    (wrap.UsedFallback ? " [provisional borders]" : "") +
                    (healed > 0 ? $" | queued {healed} neighbour border-heal(s)" : "") + ".");
            return true;
        }

        // ── Swap: hide vanilla once every voxel collider is live ────────────
        // The handoff is ATOMIC: voxel renderers un-suppress and vanilla
        // hides in the SAME frame — the two surfaces never render together.
        private static readonly AccessTools.FieldRef<TerrainComp, Color[]> s_tcPaint =
            AccessTools.FieldRefAccess<TerrainComp, Color[]>("m_paintMask");
        private static readonly AccessTools.FieldRef<TerrainComp, bool[]> s_tcModPaint =
            AccessTools.FieldRefAccess<TerrainComp, bool[]>("m_modifiedPaint");
        private static readonly AccessTools.FieldRef<TerrainComp, int> s_tcWidth =
            AccessTools.FieldRefAccess<TerrainComp, int>("m_width");

        // River beds wear DIRT + CLEARED, painted into the VANILLA paint data
        // once per zone at swap — the one writer everything follows: the voxel
        // skin (W3.3 mask copy), grass clutter (cleared), and the minimap.
        // Player paint always wins (m_modifiedPaint vertices are skipped).
        // GHOST-ZONE ACCELERATOR — vanilla CreateGhostZones generates ONE
        // zone per ZoneSystem tick (~4/s): entering fresh land holds the
        // near-creation gate (IsActiveAreaLoaded) CLOSED 50-60% of frames
        // for seconds while zone stubs trickle out, so veg/rocks/monsters
        // "take forever" (ZFLOW-proven; each Ghost spawn costs ~0.3ms on
        // Fires worlds). Generate every missing active-area zone under a
        // 4ms/frame budget; SpawnZone self-registers in m_generatedZones.
        private static AccessTools.FieldRef<ZoneSystem, HashSet<Vector2i>> s_generatedZonesRef;
        private static System.Reflection.MethodInfo s_spawnZoneMethod;
        private static readonly object[] s_spawnZoneArgs = new object[3];

        private static void GhostBurst(Vector3 center)
        {
            var zs = ZoneSystem.instance;
            if (zs == null || ZNet.instance == null) return;
            if (s_spawnZoneMethod == null)
            {
                s_generatedZonesRef = AccessTools.FieldRefAccess<ZoneSystem, HashSet<Vector2i>>("m_generatedZones");
                s_spawnZoneMethod = AccessTools.Method(typeof(ZoneSystem), "SpawnZone");
                if (s_spawnZoneMethod == null) return;
            }
            var gen = s_generatedZonesRef(zs);
            var zc = ZoneSystem.GetZone(center);
            int area = zs.m_activeArea + zs.m_activeDistantArea;
            System.Diagnostics.Stopwatch sw = null;
            for (int dz = -area; dz <= area; dz++)
            {
                for (int dx = -area; dx <= area; dx++)
                {
                    var z = new Vector2i(zc.x + dx, zc.y + dz);
                    if (gen.Contains(z)) continue;
                    // Budget BEFORE spawning: zone spawns measured 112-159ms
                    // on mountain worlds — checking after meant every burst
                    // frame ate one anyway ON TOP of the budget. One
                    // expensive spawn per tick max; cheap ones still batch.
                    if (sw != null && sw.Elapsed.TotalMilliseconds >= 4.0) return;
                    if (sw == null) sw = System.Diagnostics.Stopwatch.StartNew();
                    s_spawnZoneArgs[0] = z;
                    s_spawnZoneArgs[1] = ZoneSystem.SpawnMode.Ghost;
                    s_spawnZoneArgs[2] = null;
                    try { s_spawnZoneMethod.Invoke(zs, s_spawnZoneArgs); }
                    catch (System.Exception ex)
                    {
                        Debug.LogWarning($"[FiresVoxel] ghost burst threw for ({z.x},{z.y}): {ex.Message}");
                        return;
                    }
                }
            }
        }

        private static readonly HashSet<Vector2i> s_paintDone = new HashSet<Vector2i>();
        private static readonly HashSet<Vector2i> s_paintPending = new HashSet<Vector2i>();
        private static readonly List<Vector2i> s_paintScratch = new List<Vector2i>();
        private static readonly List<ZDO> s_paintZdoScratch = new List<ZDO>();
        private static int s_tcPrefabHash;
        private static float s_nextPaintRetry;

        private static void PaintRiverBed(Heightmap hm)
        {
            if (!FiresWorldGenBase.Active || hm == null) return;
            // ONE writer: the server. Client and dedi both painting spawned
            // duplicate terrain compilers, vanilla removed the loser, the
            // orphaned side re-painted on the data-change regen — an endless
            // cross-machine loop (log-proven: 7,213 "another terrain
            // compiler" removals + 1,475 in-place regens in one session).
            // Clients receive the paint through normal TerrainComp ZDO sync.
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            var zone = ZoneSystem.GetZone(hm.transform.position);
            if (s_paintDone.Contains(zone) || s_paintPending.Contains(zone)) return;
            if (TryPaintZone(hm)) s_paintDone.Add(zone);
            else s_paintPending.Add(zone);
        }

        // Deferred paints — the zone's stored compiler ZDO was still in the
        // spawn queue; retry until it's instantiated (or verifiably absent).
        private static void PaintTick()
        {
            if (s_paintPending.Count == 0 || Time.time < s_nextPaintRetry) return;
            s_nextPaintRetry = Time.time + 2f;
            s_paintScratch.Clear();
            s_paintScratch.AddRange(s_paintPending);
            foreach (var zone in s_paintScratch)
            {
                if (!s_zones.TryGetValue(zone, out var wrap) || wrap.Hm == null)
                { s_paintPending.Remove(zone); continue; }
                if (TryPaintZone(wrap.Hm)) { s_paintPending.Remove(zone); s_paintDone.Add(zone); }
            }
        }

        private static bool TryPaintZone(Heightmap hm)
        {
            var zc = hm.transform.position;
            bool any = false;
            for (int z = 0; z <= 64 && !any; z += 8)
                for (int x = 0; x <= 64 && !any; x += 8)
                    if (FiresWorldGenBase.IsWetAt(zc.x - 32f + x, zc.z - 32f + z)) any = true;
            if (!any) return true;

            // FIND FIRST — the vanilla contract. FindTerrainCompiler only
            // sees INSTANTIATED compilers; when the zone's stored compiler
            // ZDO is still in ZNetScene's spawn queue, creating here minted
            // a duplicate. If the sector holds a compiler ZDO that isn't
            // spawned yet, WAIT — never create over it.
            var tc = TerrainComp.FindTerrainCompiler(zc);
            if (tc == null)
            {
                if (s_tcPrefabHash == 0 && hm.m_terrainCompilerPrefab != null)
                    s_tcPrefabHash = hm.m_terrainCompilerPrefab.name.GetStableHashCode();
                if (ZDOMan.instance == null) return false;
                s_paintZdoScratch.Clear();
                ZDOMan.instance.FindSectorObjects(ZoneSystem.GetZone(zc), 0, 0, s_paintZdoScratch);
                for (int i = 0; i < s_paintZdoScratch.Count; i++)
                    if (s_paintZdoScratch[i] != null && s_paintZdoScratch[i].GetPrefab() == s_tcPrefabHash)
                        return false;
                tc = hm.GetAndCreateTerrainCompiler();
            }
            if (tc == null) return false;
            var paint = s_tcPaint(tc);
            var mod = s_tcModPaint(tc);
            if (paint == null || mod == null) return false;
            int w = s_tcWidth(tc) + 1;
            bool changed = false;
            for (int vz = 0; vz < w; vz++)
            {
                for (int vx = 0; vx < w; vx++)
                {
                    int i = vz * w + vx;
                    if (i >= paint.Length || mod[i]) continue;
                    if (!FiresWorldGenBase.IsWetAt(zc.x - 32f + vx, zc.z - 32f + vz)) continue;
                    paint[i] = new Color(1f, 0f, 0f, 1f);   // dirt + cleared vegetation
                    mod[i] = true;
                    changed = true;
                }
            }
            if (changed)
            {
                AccessTools.Method(typeof(TerrainComp), "Save")?.Invoke(tc, null);
                hm.Poke(false);
                // Grass already instanced over the bed rebuilds against the
                // fresh cleared paint (vanilla paint ops do the same).
                if (ClutterSystem.instance != null)
                    ClutterSystem.instance.ResetGrass(zc, 46f);
            }
            return true;
        }

        private static void TrySwap(ZoneWrap wrap)
        {
            if (wrap.Hm == null) return;

            int pendingGen = 0, queued = 0, noView = 0, noCollider = 0;
            foreach (var key in wrap.Keys)
            {
                if (!VoxelWorld.TryGetChunk(key, out var chunk)) continue;
                if (chunk.PendingGenerate) pendingGen++;
                else if (chunk.Queued) queued++;
                else if (!VoxelWorld.TryGetView(key, out var view) || view == null) noView++;
                else if (!view.ColliderReady) noCollider++;
            }
            if (pendingGen + queued + noView + noCollider > 0)
            {
                // Stuck-swap diagnostics — a wrap blocked >8s logs WHY.
                if (Time.time - wrap.StartedAt > 8f && Time.time >= wrap.NextDiagAt)
                {
                    wrap.NextDiagAt = Time.time + 8f;
                    Debug.LogWarning($"[FiresVoxel] zone ({wrap.Zone.x},{wrap.Zone.y}) swap BLOCKED " +
                        $"{Time.time - wrap.StartedAt:F0}s: pendingGen={pendingGen} queued={queued} " +
                        $"noView={noView} colliderNotReady={noCollider} of {wrap.Keys.Count} chunks.");
                }
                return;
            }

            foreach (var key in wrap.Keys)
            {
                if (VoxelWorld.TryGetChunk(key, out var chunk)) chunk.SuppressRenderUntilSwap = false;
                if (VoxelWorld.TryGetView(key, out var view) && view != null) view.SetRenderSuppressed(false);
            }
            HideVanilla(wrap.Hm);
            wrap.Swapped = true;
            if (FiresVoxelConfig.LogTimings != null && FiresVoxelConfig.LogTimings.Value)
                Debug.Log($"[FiresVoxel] zone ({wrap.Zone.x},{wrap.Zone.y}) SWAPPED to voxel terrain " +
                    $"({wrap.Keys.Count} chunks, {Time.time - wrap.StartedAt:F1}s after wrap start).");
            var swPaint = System.Diagnostics.Stopwatch.StartNew();
            try { PaintRiverBed(wrap.Hm); }
            catch (System.Exception ex) { Debug.LogWarning($"[FiresVoxel] river bed paint threw for ({wrap.Zone.x},{wrap.Zone.y}): {ex.Message}"); }
            if (swPaint.Elapsed.TotalMilliseconds >= 25.0)
                Debug.Log($"[FiresVoxel] zone ({wrap.Zone.x},{wrap.Zone.y}) bed paint took {swPaint.Elapsed.TotalMilliseconds:0} ms.");

            // W2 — schedule hydrology water for this zone (springs + ponds),
            // once per session per zone; the pour-proximity check makes it
            // idempotent across sessions.
            if (FiresVoxelConfig.HydrologyPlaceWater != null && FiresVoxelConfig.HydrologyPlaceWater.Value
                && s_waterDone.Add(wrap.Zone))
                s_waterQueue.Add((wrap.Zone, Time.time + 2f));

            // Weld: borders of the freshly swapped zone (and the ring of
            // neighbours whose aprons read into it) re-mirror NOW instead of
            // waiting for the rolling sweep to come around (~30s).
            QueuePriorityHeal(wrap);

            // W3.3 — paint that loaded from TerrainComp ZDOs before the zone
            // wrapped gets its masks on the freshly visible chunks.
            MarkZonePaintDirty(wrap.Zone);
        }

        // Zone (zx,zy) covers world [zx·64−32, zx·64+32) = chunk columns
        // 2zx−1..2zx; the extra west/south ring covers neighbouring columns
        // whose edge texel (i==32) probes into this zone via the +ε idiom.
        private static void MarkZonePaintDirty(Vector2i zone)
        {
            for (int cz = zone.y * 2 - 2; cz <= zone.y * 2; cz++)
                for (int cx = zone.x * 2 - 2; cx <= zone.x * 2; cx++)
                    VoxelWorld.MarkPaintDirty(new Vector2Int(cx, cz));
        }

        private static void HideVanilla(Heightmap hm)
        {
            if (hm == null) return;
            var rend = hm.GetComponent<MeshRenderer>();
            if (rend != null && rend.enabled) rend.enabled = false;
            var col = s_hmCollider(hm);
            if (col != null && col.enabled) col.enabled = false;
        }

        // ── Unwrap: vanilla back FIRST, then drop chunks ─────────────────────
        private static void Unwrap(Vector2i zone)
        {
            if (!s_zones.TryGetValue(zone, out var wrap)) return;
            if (wrap.Hm != null)
            {
                var rend = wrap.Hm.GetComponent<MeshRenderer>();
                if (rend != null) rend.enabled = true;
                var col = s_hmCollider(wrap.Hm);
                if (col != null) col.enabled = true;
                // Render/collision rebuilds are skipped while hidden — the
                // returning vanilla meshes are stale until this rebuild.
                wrap.Hm.Poke(false);
            }
            foreach (var key in wrap.Keys) VoxelWorld.DropChunkKey(key);
            // The receding frontier: surviving chunks in adjacent wrapped
            // zones still carry a "covered" frontier mask toward this zone —
            // no lip, no quads, and no heal ever fired (border mirroring
            // skips absent owners, so the sweep saw zero divergence). Queue
            // the zone's ring; dead keys self-discard, survivors remesh with
            // fresh masks and the lips return.
            QueuePriorityHeal(wrap);
            s_zones.Remove(zone);
            VoxelGenWaterMesh.DropZone(zone);
        }

        private static void UnwrapAll()
        {
            s_scratchDrop.Clear();
            foreach (var kv in s_zones) s_scratchDrop.Add(kv.Key);
            foreach (var z in s_scratchDrop) Unwrap(z);
        }

        // Terrain op (hoe/pickaxe/TerrainComp) regenerated a heightmap: mark
        // that zone AND its 8 neighbours dirty (their border/apron samples read
        // into this zone's data — round-89, kills border lips after terraform).
        // The tick drains one rewrap per 0.25s so hoe-drag op storms converge
        // instead of thrashing.
        [HarmonyPatch(typeof(Heightmap), nameof(Heightmap.Regenerate))]
        private static class Heightmap_Regenerate_RewrapZone
        {
            [HarmonyPostfix]
            private static void Postfix(Heightmap __instance)
            {
                try
                {
                    if (__instance == null || s_isDistantLod(__instance)) return;
                    var zone = ZoneSystem.GetZone(__instance.transform.position);
                    // W3.3 — every paint op routes through Regenerate too
                    // (DoOperation → Poke), so this is THE paint refresh hook,
                    // on every peer. BEFORE the zone-count gate: brush chunks
                    // exist in Vanilla world mode too, and MarkPaintDirty
                    // no-ops for columns without voxel chunks.
                    MarkZonePaintDirty(zone);
                    if (s_zones.Count == 0) return;
                    for (int dz = -1; dz <= 1; dz++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            var z = new Vector2i(zone.x + dx, zone.y + dz);
                            if (s_zones.ContainsKey(z)) s_dirty.Add(z);
                        }
                }
                catch (System.Exception ex)
                {
                    Debug.LogWarning($"[FiresVoxel] regenerate dirty-mark threw: {ex.Message}");
                }
            }
        }

        // W2 — auto-place water for a freshly swapped zone: bank-following
        // SPRING pours at hydrology headwaters (they fill the carved beds
        // downhill — the whole point), settling water volumes on ponds.
        // Idempotent: skips anywhere a pour already exists (persisted pours
        // from previous sessions load with the zone before this runs).
        // A generated source must pin the way a hammer spring does: at the
        // ruler ON the real ground. The grid level is a ~10m-cell estimate;
        // clamp it into a sane window above the ACTUAL local surface (voxel
        // truth first, heightmap data fallback) so a source never stands
        // proud of the real bed (dome) or under it (dry).
        private static Vector3 ClampSpringToGround(Vector3 spot)
        {
            float bed;
            if (!VoxelWorld.TrySampleSurfaceY(spot.x, spot.z, spot.y + 12f, spot.y - 32f, out bed, out _))
            {
                if (!Heightmap.GetHeight(new Vector3(spot.x + 0.013f, 0f, spot.z + 0.013f), out bed))
                    return spot;
            }
            if (spot.y < bed + 0.2f) spot.y = bed + 0.2f;
            else if (spot.y > bed + 5f) spot.y = bed + 5f;
            return spot;
        }

        private static void PlaceWaterForZone(Vector2i zone)
        {
            if (!VoxelHydrology.Ready) return;

            int placed = 0;

            // W4.2 v4 — generated worlds: sources feed the liquid engine the
            // way it is DESIGNED to be fed. Bed cells ARE the meandered
            // thalweg; spots walk upstream→downstream at ~26m so each next
            // source lands INSIDE the previous pour's 64m grid and MERGES
            // (round-85) into one continuous mesh carrying a descending chain
            // of pinned fill levels — the engine's bank-following spread does
            // the filling to the bank edges. Dedup asks the controllers'
            // SOURCE lists (merged sources included), not pour centers.
            // (The 0.2.129 direct-field mesh experiment is DEAD — the coarse
            // gen grid put sheets meters off the real surface. User verdict
            // stands: the settle engine is the water; place its sources
            // correctly instead.)
            // GENERATED WATER = STATIC GEOMETRY (v2 — real-ground clamped).
            // The spring relay is retired for gen worlds: vanilla settle
            // drains at each 64m pour border, so pinned disks make pools,
            // never 500m channels (log-proven). The mesh renders the
            // generator's settled field directly, wet-tested per vertex
            // against the REAL heightmap so it can never float. Springs stay
            // fully functional as hammer tools.
            if (FiresWorldGenBase.Active)
            {
                VoxelGenWaterMesh.BuildForZone(zone);
                return;
            }

            s_springScratch.Clear();
            VoxelHydrology.GetSpringsInZone(zone, s_springScratch);
            foreach (var s in s_springScratch)
            {
                if (Water.Core.FiresLiquidWaterPrefab.HasPourNear(s, 24f)) continue;
                var p = s;
                float bed = VoxelHydrology.SampleBedY(p.x, p.z);
                if (bed < float.MaxValue) p.y = bed + 0.6f;
                if (Water.Core.FiresLiquidWaterPrefab.SpawnSpring(p, 3f, 1f) != null) placed++;
            }

            // Ponds — coarse scan of the zone; one settling volume per ~pond
            // (the proximity check stops dense tiling). LAKES WIDER THAN A
            // POUR GRID ARE SKIPPED (user directive — a partial 64m volume in
            // a 200m lake reads worse than no water; big-lake water is a
            // future dedicated pass).
            float minX = zone.x * 64f - 32f, minZ = zone.y * 64f - 32f;
            for (float z = 8f; z < 64f; z += 16f)
            {
                for (float x = 8f; x < 64f; x += 16f)
                {
                    float wx = minX + x, wz = minZ + z;
                    float still = VoxelHydrology.SampleStillWaterY(wx, wz);
                    if (still <= float.MinValue + 1f) continue;

                    // Size probe: still water at the same level ≥40m out in
                    // most directions = a big lake → skip.
                    int bigHits = 0;
                    for (int k = 0; k < 8; k++)
                    {
                        float ang = k * Mathf.PI * 0.25f;
                        float px = wx + Mathf.Cos(ang) * 40f, pz = wz + Mathf.Sin(ang) * 40f;
                        float s2 = VoxelHydrology.SampleStillWaterY(px, pz);
                        if (s2 > float.MinValue + 1f && Mathf.Abs(s2 - still) < 2f) bigHits++;
                    }
                    if (bigHits >= 5) continue;   // wide lake — leave dry for now

                    var p = new Vector3(wx, still, wz);
                    if (Water.Core.FiresLiquidWaterPrefab.HasPourNear(p, 22f)) continue;
                    if (Water.Core.FiresLiquidWaterPrefab.Spawn(p, 8f) != null) placed++;
                }
            }

            if (placed > 0)
                Debug.Log($"[FiresVoxel] zone ({zone.x},{zone.y}) hydrology water: {placed} pour(s) placed.");
        }

        // va_voxel_wrapall — burst-wrap every loaded zone right now (skips the
        // one-per-tick pacing; swaps still gate on collider readiness).
        public static int WrapAllNow()
        {
            if (!ModeActive) return 0;
            int started = 0;
            foreach (var hm in Heightmap.GetAllHeightmaps())
            {
                if (hm == null || s_isDistantLod(hm)) continue;
                var zone = ZoneSystem.GetZone(hm.transform.position);
                if (s_zones.ContainsKey(zone)) continue;
                if (StartWrap(zone, hm)) started++;
            }
            Debug.Log($"[FiresVoxel] wrapall: started {started} zone wrap(s); {s_zones.Count} total tracked.");
            return started;
        }

        private static int FloorDiv(int a, int b) => a >= 0 ? a / b : ~(~a / b);
    }

    // VANILLA-PARITY biome color blending (Heightmap.GetBiomeColor(ix,iy) +
    // m_cornerBiomes): the biome is sampled at the containing ZONE's four
    // corners and every vertex gets the bilinear of the one-hot corner
    // colors — the same 64m gradients vanilla terrain wears. The per-vertex
    // one-hot we used before drew hard pixel edges at every biome boundary.
    // Corner order matches vanilla: 0=(x0,z0) 1=(x1,z0) 2=(x0,z1) 3=(x1,z1).
    // Main-thread only (BiomeManager + the single-slot zone memo).
    internal static class BiomeCornerBlend
    {
        private static int s_zx = int.MinValue, s_zz = int.MinValue;
        private static Color32 s_c0, s_c1, s_c2, s_c3;
        private static bool s_same;

        public static Color32 Sample(float wx, float wz)
        {
            int zx = Mathf.FloorToInt((wx + 32f) / 64f);
            int zz = Mathf.FloorToInt((wz + 32f) / 64f);
            if (zx != s_zx || zz != s_zz)
            {
                s_zx = zx; s_zz = zz;
                float x0 = zx * 64f - 32f, z0 = zz * 64f - 32f;
                var b0 = VerdantsAscent.Terrain.Biome.BiomeManager.GetBiomeAt(x0, z0);
                var b1 = VerdantsAscent.Terrain.Biome.BiomeManager.GetBiomeAt(x0 + 64f, z0);
                var b2 = VerdantsAscent.Terrain.Biome.BiomeManager.GetBiomeAt(x0, z0 + 64f);
                var b3 = VerdantsAscent.Terrain.Biome.BiomeManager.GetBiomeAt(x0 + 64f, z0 + 64f);
                s_same = b0 == b1 && b0 == b2 && b0 == b3;
                s_c0 = Heightmap.GetBiomeColor(b0);
                s_c1 = s_same ? s_c0 : Heightmap.GetBiomeColor(b1);
                s_c2 = s_same ? s_c0 : Heightmap.GetBiomeColor(b2);
                s_c3 = s_same ? s_c0 : Heightmap.GetBiomeColor(b3);
            }
            if (s_same) return s_c0;
            float ix = (wx - (zx * 64f - 32f)) * (1f / 64f);
            float iz = (wz - (zz * 64f - 32f)) * (1f / 64f);
            return Color32.Lerp(Color32.Lerp(s_c0, s_c1, ix), Color32.Lerp(s_c2, s_c3, ix), iz);
        }
    }
}
