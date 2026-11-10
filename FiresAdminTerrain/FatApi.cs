namespace VerdantsAscent
{
    // FAT's public API for other mods. A CONTRACT: add-only, never rename or
    // change a signature; consumers reflect it by name (type "VerdantsAscent.FatApi").
    public static class FatApi
    {
        // ── 0.2.1424 (FiresFixes compat, [fgn] 11-08; lead's ruling: FF's soft FAT compat goes through FatApi only, never FAT internals). ADD-ONLY.
        // FF binds these by name and stays quiet when FAT or a member is absent. Feature detection: FiresFixesCompatApiVersion.
        public const int FiresFixesCompatApiVersion = 1;

        // FAT's world edge radius in metres (FF FishWorldEdge keeps fish inside it).
        public static float WorldEdgeRadius
        {
            get { try { return global::VerdantsAscent.Terrain.Voxel.FatWorldSize.EdgeRadius; } catch (System.Exception) { return 10500f; } }
        }

        // true while FAT spreads clutter (grass) rebuilds over frames (FF GrassRebuildSpread stands aside then). Answers false once
        // FAT's copy moves to FF (Fire 11-08: perf goes in EBM / FF).
        public static bool ClutterSpreadEnabled
        {
            get { try { return global::VerdantsAscent.Performance.ClutterSpread.Enabled; } catch (System.Exception) { return false; } }
        }

        // true when FAT refused this TerrainOp (zone-mode refusal) or routed it to its tile layer (FF TerrainPaintSeams skips it).
        // Main thread, in the same frame as the op's placement (FAT keeps only the last refused / routed op).
        public static bool TerrainOpRefused(TerrainOp op)
        {
            try { return global::VerdantsAscent.Terrain.Zones.ZoneModeRefusals.WasCancelled(op) || global::VerdantsAscent.Terrain.Persistence.TileTerrainOpBlocker.WasRoutedToTile(op); }
            catch (System.Exception) { return false; }
        }

        // 0.2.1323 (Core 1078 contract: Core's voxel SnowRefill eases ONLY trail-cut texels, so gardens survive): the per-TerrainComp
        // trail-cut mask, one bit per TerrainComp.m_paintMask texel (bit i = byte i >> 3, bit i & 7), ZDO key "fat_trailcut".
        // Set by FAT's snow trail ops (vanilla snow_walk, FAT_ShovelPatch_*, FAT_CreatureTrail_*) on the comp's owner; cleared by any
        // player paint op (cultivator / hoe / FAT brushes) on the texels it touches. Feature detection: TrailCutApiVersion.
        public const int TrailCutApiVersion = 1;
        // The live mask (null = none, or a stored length != ceil(paintMask.Length / 8)). Do not keep it across frames.
        public static byte[] TrailCutMask(TerrainComp tc) => global::VerdantsAscent.Terrain.Voxel.FatTrailCut.Mask(tc);
        public static bool IsTrailCut(TerrainComp tc, int index) => global::VerdantsAscent.Terrain.Voxel.FatTrailCut.IsSet(tc, index);
        // Owner only (no-op elsewhere): Core clears a bit when it eases the texel back to base; written to the ZDO at the comp's next Save.
        public static void ClearTrailCut(TerrainComp tc, int index) => global::VerdantsAscent.Terrain.Voxel.FatTrailCut.ClearFromCore(tc, index);

        // 0.2.1325 ([saddles] launch 2: FAP dungeons refused on super flat): a deliberate location spawn (an admin tool / command, not
        // world generation) passes FAT's super-flat surface rule. Vanilla's own `cheated` spawns pass already; a mod that spawns a
        // location without it wraps the call: BeginDeliberateSpawn(); ...SpawnLocation...; EndDeliberateSpawn(). Main thread; nests.
        public const int DeliberateSpawnApiVersion = 1;
        [System.ThreadStatic] private static int s_deliberate;
        public static void BeginDeliberateSpawn() => s_deliberate++;
        public static void EndDeliberateSpawn() { if (s_deliberate > 0) s_deliberate--; }
        internal static bool DeliberateSpawn => s_deliberate > 0;
        // 0.2.1340 (Fire): a location spawned inside this scope sits exactly at the given pos: no height correction, no level site, no pad. An underground entrance still gets its room.
        // BeginNoLevelSpawn(); ...SpawnLocation...; EndNoLevelSpawn(). Main thread; nests. A client's redraw has no scope: a proxy with the ZDO bool HasFieldsTerrainModifier is treated the same.
        public const int NoLevelSpawnApiVersion = 1;
        [System.ThreadStatic] private static int s_noLevel;
        public static void BeginNoLevelSpawn() => s_noLevel++;
        public static void EndNoLevelSpawn() { if (s_noLevel > 0) s_noLevel--; }
        internal static bool NoLevelSpawn => s_noLevel > 0;
        private static int s_deliberateLogged;
        internal static void NoteDeliberateSpawn(string prefab, UnityEngine.Vector3 pos, bool cheated)
        {
            if (++s_deliberateLogged == 1) FiresLogger.LogInfo($"[FAT] super flat: deliberate location spawns pass the surface rule (vanilla cheated / FatApi.BeginDeliberateSpawn) (0.2.1325)");
            if (s_deliberateLogged <= 20) FiresLogger.LogDebug($"[FAT] super flat: location {prefab} at ({pos.x:0},{pos.y:0},{pos.z:0}) let through: deliberate spawn ({(cheated ? "vanilla cheated" : "FatApi.BeginDeliberateSpawn")}), not world generation (0.2.1325)");
        }

        // 0.2.1175 (Fire: "work with vedr for tsunamis"; names locked with [seasons]): a timed wave multiplier from a named owner.
        // Overrides combine by max(1, ...) and expire after `seconds`; the SERVER's value is the one every peer uses, so call it there.
        public static void SetWaveOverride(string owner, float multiplier, float seconds)
        {
            try { global::VerdantsAscent.Water.Core.FiresWaves.SetOverride(owner, multiplier, seconds); } catch (System.Exception) { }
        }

        public static void ClearWaveOverride(string owner)
        {
            try { global::VerdantsAscent.Water.Core.FiresWaves.ClearOverride(owner); } catch (System.Exception) { }
        }

        // The final sea wave multiplier in use on this peer (slider x weather x overrides; the server's value on clients).
        public static float WaveMultiplier
        {
            get { try { return global::VerdantsAscent.Water.Core.FiresWaves.Final; } catch (System.Exception) { return 1f; } }
        }

        // 0.2.1155: the sky-island top surface at a world x/z. true = (x, z) is inside an island's footprint, y = its top there
        // (Core's TerrainGuard measures snow / trail / shovel ops against it). false = no island there (y = 0).
        public static bool TryIslandSurface(float x, float z, out float y)
        {
            y = 0f;
            try { bool ok = global::VerdantsAscent.Terrain.Voxel.FiresSkyIslands.TryTopAt(x, z, out y); if (!ok) y = 0f; return ok; }
            catch (System.Exception) { y = 0f; return false; }
        }

        // 0.2.1152: stamp a packed snow-trail disc (width = 2 x radius, 0.9-6 m, snapped to the creature-trail ops) on voxel
        // snow ground. false = not voxel ground / no snow-trail biome / snow <= 0.05 m (0.2.1154) / rate cap. Call on the owner only.
        public static bool SnowTrailDisc(UnityEngine.Vector3 pos, float radiusM)
        {
            try { return global::VerdantsAscent.Terrain.Voxel.FatCreatureTrail.Disc(pos, radiusM); }
            catch (System.Exception) { return false; }
        }

        // ── 0.2.1405 voxel snow from FTS (Fire 11-07; names settled with [seasons] / [ghost]). ADD-ONLY. ──────────────────────────
        // FAT is the ONE writer of the heat globals its voxel shader reads (_FiresHeat[16] = x, y, z, w; _FiresHeatCount;
        // _FiresHeatCampfireR) and of the snow look globals (_FiresSnowTint/Bright/Smooth/SmoothAmt/Sparkle/PackedTint/PackedSmooth/PackedAmt),
        // on every client. Another mod's writer of the same globals stands down when this is >= 1 (FTS keys on it). Vedr has its own set.
        public const int HeatApiVersion = 1;
        public static bool WritesHeatGlobals => true;

        // The biome of the sky island at or near pos (the point, a ring 8 m round and 6 m above / below, then rim padding out to 20 m and
        // 25 m down), for piece snow on islands. true = an island is there: biome = the island's, snowy = Mountain / Deep North or Core
        // winter cover. false = no island near (biome None). Main thread; cheap cell lookups.
        public static bool TryIslandBiomeNear(UnityEngine.Vector3 pos, out Heightmap.Biome biome, out bool snowy)
        {
            biome = Heightmap.Biome.None; snowy = false;
            try { return global::VerdantsAscent.Terrain.Voxel.FatSkylandSnow.OnSkylandNear(pos, out snowy, out biome); }
            catch (System.Exception) { biome = Heightmap.Biome.None; snowy = false; return false; }
        }

        // One snow-shovel swing's GROUND patch at pos for any character (a companion), exactly the player's: a 1 m patch cut to the
        // trench floor (snow only, the ground is not carved), on voxel ground and on heightmap ground with snow (key "Snow shovel patches
        // on heightmap"). Refused (false) with no snow there, under water, or when a snowy build piece is hit (Vedr's / FTS's
        // ShovelHitsPiece, or a piece in the 0.5 m sphere): that swing is the piece's. true = the patch op was sent. Callable on any
        // peer (the companion owner's): the op travels to the TerrainComp's owner and syncs like a player's swing; the caller does nothing more.
        public static bool ShovelGroundAt(Character who, UnityEngine.Vector3 pos)
        {
            try
            {
                bool did = global::VerdantsAscent.Terrain.Voxel.FatShovelPatch.GroundPatchAt(who, pos, out string why);
                global::VerdantsAscent.Terrain.Voxel.FatShovelPatch.ApiWhy(who, did, why);
                return did;
            }
            catch (System.Exception ex) { FiresLogger.LogDebug($"[Shovel] FatApi.ShovelGroundAt threw: {ex.Message}"); return false; }
        }

        // ── 0.2.1406 (Vedr/Heightmap border rule, [seasons] + [lead] 11-07: one snow drawer per tile). ADD-ONLY. ──────────────
        // true while FAT hides this exact Heightmap chunk under voxel ground (its zone is swapped to voxel): Vedr's heightmap
        // snow skips it, FAT's voxel snow draws there. false for every other chunk (vanilla ground, data-only zones, distant LOD
        // meshes FAT does not hide, null). Main thread. VoxelSwapRevision bumps on every hide / show: re-check on change, not per frame.
        public static bool IsHeightmapHidden(Heightmap hm)
        {
            try { return global::VerdantsAscent.Terrain.Voxel.VoxelZoneStreamer.IsHeightmapHidden(hm); }
            catch (System.Exception) { return false; }
        }

        public static int VoxelSwapRevision => global::VerdantsAscent.Terrain.Voxel.VoxelZoneStreamer.SwapRevision;

        // 0.2.1168 (Fire's freezing water; LOCKED with [seasons] for the creature / snowman ice cubes). Cell lookups, owner-side callers.
        // true when pos is in FAT water (placed / river / lake): coverage01 = ice cover there (0 open .. 1 iced over), thicknessM = ice
        // thickness (0 while it forms; walkable from 0.10 m). false off FAT water.
        public static bool TryIceAt(UnityEngine.Vector3 pos, out float coverage01, out float thicknessM)
        {
            coverage01 = 0f; thicknessM = 0f;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();   // 0.2.1203: call timing (IceApiTiming)
            try { return global::VerdantsAscent.Water.Core.IceField.TryIceAt(pos, out coverage01, out thicknessM); }
            catch (System.Exception) { coverage01 = 0f; thicknessM = 0f; return false; }
            finally { IceApiTiming.Add(0, t0); }
        }

        // true when pos is in FAT water whose biome freezes now (Deep North, or Vedr's winter) and no fire keeps it open: a creature
        // that falls in here should freeze. false (never throws) off FAT / voxel water.
        public static bool IsFreezingWater(UnityEngine.Vector3 pos)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();   // 0.2.1203
            try { return global::VerdantsAscent.Water.Core.IceField.IsFreezingWater(pos); }
            catch (System.Exception) { return false; }
            finally { IceApiTiming.Add(1, t0); }
        }

        // Bumped whenever the ice forms or breaks (each step of the timed ice, 0.05 day of exposure). No event: re-query on change.
        public static int IceRevision => global::VerdantsAscent.Water.Core.IceField.Revision;

        // FAT's generated river/lake water at a world x/z, loaded or not (the field the water sheet is drawn from).
        // true = generated water there: level = its surface Y, depth = surface minus ground. false = none (level = float.MinValue).
        public static bool TryWaterAt(float x, float z, out float level, out float depth)
        {
            level = float.MinValue; depth = 0f;
            try { return global::VerdantsAscent.Terrain.Voxel.GenWaterField.TryWaterSurfaceSmooth(x, z, out level, out depth); }
            catch (System.Exception) { level = float.MinValue; depth = 0f; return false; }
        }

        // The generated-water level the swim/float path reads at a point, both ways, and which one it uses now
        // ([Voxel Terrain] "Swim level follows the drawn water"). stored = the chunk's stored columns, sheet = the drawn sheet's
        // smoothed heights; -10000 = no generated water there. Read-only.
        public static void SwimLevelsAt(float x, float y, float z, out float stored, out float sheet, out bool usesSheet)
        {
            stored = sheet = -10000f; usesSheet = false;
            try
            {
                var p = new UnityEngine.Vector3(x, y, z);
                stored = global::VerdantsAscent.Terrain.Voxel.VoxelWaterSurface.SurfaceAtBase(p, false);
                sheet = global::VerdantsAscent.Terrain.Voxel.VoxelWaterSurface.SurfaceAtBase(p, true);
                var key = global::VerdantsAscent.Terrain.Voxel.FiresVoxelConfig.SwimFollowsDrawnWater;
                usesSheet = key != null && key.Value;
            }
            catch (System.Exception) { }
        }

        // Chunk blueprints.
        // A FAT voxel-terrain world is loaded (the only kind a chunk blueprint captures or stamps).
        public static bool IsVoxelWorld()
        {
            try { return global::VerdantsAscent.Terrain.Voxel.VoxelZoneStreamer.IsVoxelWorldActive; }
            catch (System.Exception) { return false; }
        }

        // 0.2.1026 ([ghost]'s SnowField): FAT owns this zone's terrain (voxel territory, wrapping or wrapped), so vanilla's
        // Heightmap paint mask is not what draws it. A zone can turn voxel after its Heightmap built: ask again on change.
        public static bool IsFatZone(Vector2s zone)
        {
            try { return global::VerdantsAscent.Terrain.Voxel.VoxelZoneStreamer.IsZoneKnown(zone); }
            catch (System.Exception) { return false; }
        }

        // 0.2.1028 (halo ring spike, [visual]'s FDT FatHalo provider): the fat_halo_arc test arc's gravity field.
        // HaloActive: an arc exists in this world. HaloTryGetUp: true only on or near the shell (within its half-width along the
        // axis, its degrees of arc, radially R - 30 m .. R + 5 m), up = unit vector toward the closest point on the axis; false
        // elsewhere with up = Vector3.up. HaloAxis: the axis line (origin = the band middle on the axis, unit direction from the key: world Y for HaloFlat) and the inside radius R.
        public static bool HaloActive
        {
            get { try { return global::VerdantsAscent.Terrain.Voxel.FatHaloArc.Active; } catch (System.Exception) { return false; } }
        }

        public static bool HaloTryGetUp(UnityEngine.Vector3 pos, out UnityEngine.Vector3 up)
        {
            up = UnityEngine.Vector3.up;
            try { return global::VerdantsAscent.Terrain.Voxel.FatHaloArc.TryGetUp(pos, out up); }
            catch (System.Exception) { up = UnityEngine.Vector3.up; return false; }
        }

        // 0.2.1031 ([wishbone]'s full-ring nav via [visual]): the ring's half-length along the axis (W; HaloFlat 1,500 m), half the arc
        // in degrees (A; 180 = the whole ring) and the shell thickness (T, 4 m). False when no ring.
        public static bool HaloBand(out float halfWidth, out float halfArcDeg, out float thickness)
        {
            halfWidth = halfArcDeg = thickness = 0f;
            try { return global::VerdantsAscent.Terrain.Voxel.FatHaloArc.Band(out halfWidth, out halfArcDeg, out thickness); }
            catch (System.Exception) { return false; }
        }

        public static bool HaloAxis(out UnityEngine.Vector3 origin, out UnityEngine.Vector3 dir, out float radius)
        {
            origin = UnityEngine.Vector3.zero; dir = UnityEngine.Vector3.right; radius = 0f;
            try { return global::VerdantsAscent.Terrain.Voxel.FatHaloArc.Axis(out origin, out dir, out radius); }
            catch (System.Exception) { return false; }
        }

        // A voxel world AND the local player may save one (admin, or the host). The server checks admin again on every request.
        public static bool CanCaptureChunkBlueprint()
        {
            try
            {
                var net = ZNet.instance;
                if (net == null || !IsVoxelWorld()) return false;
                return net.IsServer() || net.LocalPlayerIsAdminOrHost();
            }
            catch (System.Exception) { return false; }
        }

        // Save zone (zoneX, zoneZ) - its land, paint, biome and everything on it - as the chunk blueprint `name`
        // (FiresWorldGen\chunks\<name>.chunkbp, beside the world's records; the same save as the Chaos Hammer's Copy tool).
        // ASYNC on a client: the request goes to the server, and done(ok, report) runs on the main thread when it answers
        // (report = the hammer's own text: where it saved, or why not, e.g. the name exists and overwrite is false).
        // On the server/host it runs at once and done is called before this returns. false = not sent (no world, no name,
        // not allowed); done is then never called. A server older than FAT 0.2.949 never answers: callers keep their own timeout.
        public static bool RequestChunkBlueprint(int zoneX, int zoneZ, string name, bool overwrite, System.Action<bool, string> done)
        {
            try
            {
                if (!CanCaptureChunkBlueprint()) return false;
                bool sent = global::VerdantsAscent.Terrain.Zones.ZoneModeChunkTools.ApiRequestSave(
                    new Vector2s(zoneX, zoneZ), name, overwrite, done, out string problem);
                if (!sent) FiresLogger.LogInfo($"[FiresZones] (FatApi) chunk blueprint not sent: {problem}");
                return sent;
            }
            catch (System.Exception ex) { FiresLogger.LogWarning($"[FiresZones] (FatApi) RequestChunkBlueprint failed: {ex.Message}"); return false; }
        }

        // Biome paint pick-up (e.g. a minimap biome line click copies that spot's biome paint onto the hammer).
        // The local player is admin/host and carries the Chaos Hammer.
        public static bool CanQueueBiomePaint()
        {
            try { return global::VerdantsAscent.Terrain.Zones.ZoneModeChunkTools.ApiCanQueuePaint(out _); }
            catch (System.Exception) { return false; }
        }

        // Equips the Chaos Hammer (if only carried) and selects the Paint brush matching the ground biome and blend at (x, z)
        // (no blend = "Plain <biome>"). Paints nothing. Client-side, synchronous. false + report when it can't (no hammer,
        // not admin, ocean, no brush for that blend).
        public static bool QueueBiomePaintAt(float x, float z, out string report)
        {
            try { return global::VerdantsAscent.Terrain.Zones.ZoneModeChunkTools.ApiQueuePaintAt(x, z, out report); }
            catch (System.Exception ex) { report = "FAT error: " + ex.Message; FiresLogger.LogWarning($"[BiomePaint] (FatApi) QueueBiomePaintAt failed: {ex.Message}"); return false; }
        }

        // 0.2.1373, the map tool ([ghost]): every biome brush, in the hammer's order: its key (opaque), its display name, whether it is a custom
        // (Expand World yaml) biome. false until the world is up. Client side.
        public static bool TryGetBiomeBrushes(out string[] keys, out string[] names, out bool[] isCustom)
        {
            keys = new string[0]; names = new string[0]; isCustom = new bool[0];
            try
            {
                if (ZNet.instance == null || ZNet.World == null) return false;
                var k = new System.Collections.Generic.List<string>(); var n = new System.Collections.Generic.List<string>(); var c = new System.Collections.Generic.List<bool>();
                global::VerdantsAscent.Terrain.Zones.BiomePaint.ListBrushes(k, n, c);
                keys = k.ToArray(); names = n.ToArray(); isCustom = c.ToArray();
                return true;
            }
            catch (System.Exception ex) { FiresLogger.LogWarning($"[BiomePaint] (FatApi) TryGetBiomeBrushes failed: {ex.Message}"); return false; }
        }

        // 0.2.1373, the map tool: one paint circle, the same request as the Chaos Hammer's; the server checks the sender is an admin and answers with
        // the usual message. Several calls with no gap over 1.5 s are one stroke; radius clamps to 2-96 m. wipeBuilds: the first request only counts and asks; a repeat within 30 s wipes.
        public static bool RequestBiomePaint(string key, float x, float z, float radius, bool wipeBuilds, out string report)
        {
            report = string.Empty;
            try
            {
                var net = ZNet.instance;
                if (net == null || ZNet.World == null) { report = "No world."; return false; }
                if (!(net.IsServer() || net.LocalPlayerIsAdminOrHost())) { report = "Biome paint is an admin's."; return false; }
                if (!global::VerdantsAscent.Terrain.Zones.BiomePaint.TryParseKey(key, out _, out _, out _)) { report = "That paint is not one this world knows."; return false; }
                global::VerdantsAscent.Terrain.Zones.ZoneModeChunkTools.ApiAskPaint(key, new UnityEngine.Vector3(x, 0f, z), UnityEngine.Mathf.RoundToInt(radius), wipeBuilds);
                return true;
            }
            catch (System.Exception ex) { report = "FAT error: " + ex.Message; FiresLogger.LogWarning($"[BiomePaint] (FatApi) RequestBiomePaint failed: {ex.Message}"); return false; }
        }

        // 0.2.1375, the map tool: the same with a blend width in metres (0-96, below 0 = the server's default). Only custom biomes change height.
        public static bool RequestBiomePaint(string key, float x, float z, float radius, float blendWidth, bool wipeBuilds, out string report)
        {
            report = string.Empty;
            try
            {
                if (!CanRequestPaint(key, out report)) return false;
                global::VerdantsAscent.Terrain.Zones.ZoneModeChunkTools.ApiAskPaint(key, new UnityEngine.Vector3(x, 0f, z), UnityEngine.Mathf.RoundToInt(radius), wipeBuilds, blendWidth);
                return true;
            }
            catch (System.Exception ex) { report = "FAT error: " + ex.Message; FiresLogger.LogWarning($"[BiomePaint] (FatApi) RequestBiomePaint failed: {ex.Message}"); return false; }
        }

        // 0.2.1375, the map tool: a drawn closed outline, x0,z0,x1,z1,... in world metres, the first point not repeated: 3-2000 points, a simple polygon that
        // fits in a circle of 512 m radius. One stroke, one undo. Checked here and again on the server.
        public static bool RequestBiomePaintArea(string key, float[] outlineXZ, float blendWidth, bool wipeBuilds, out string report)
        {
            report = string.Empty;
            try
            {
                if (!CanRequestPaint(key, out report)) return false;
                if (!global::VerdantsAscent.Terrain.Zones.PaintOutline.TryCreate(outlineXZ, out _, out float cx, out float cz, out float bound, out report)) return false;
                global::VerdantsAscent.Terrain.Zones.ZoneModeChunkTools.ApiAskPaint(key, new UnityEngine.Vector3(cx, 0f, cz), UnityEngine.Mathf.RoundToInt(bound), wipeBuilds, blendWidth, outlineXZ);
                return true;
            }
            catch (System.Exception ex) { report = "FAT error: " + ex.Message; FiresLogger.LogWarning($"[BiomePaint] (FatApi) RequestBiomePaintArea failed: {ex.Message}"); return false; }
        }

        private static bool CanRequestPaint(string key, out string report)
        {
            report = string.Empty;
            var net = ZNet.instance;
            if (net == null || ZNet.World == null) { report = "No world."; return false; }
            if (!(net.IsServer() || net.LocalPlayerIsAdminOrHost())) { report = "Biome paint is an admin's."; return false; }
            if (!global::VerdantsAscent.Terrain.Zones.BiomePaint.TryParseKey(key, out _, out _, out _)) { report = "That paint is not one this world knows."; return false; }
            return true;
        }

        // Height modifiers on FAT GENERATOR worlds. modifier(wx, wz, h)
        // returns the new height; h is FAT's final generator height (with every earlier modifier applied: a chain, in add order).
        // Applied inside FAT's one height sampler, so terrain, the water bake, shore field, wet paint and water queries all agree.
        // The modifier MUST be cheap (called millions of times while a world bakes), thread-safe (bake threads call it) and
        // deterministic. A call back into the height path from inside it sees the unmodified chain. Throw / NaN = skipped for that
        // sample. Grids already baked keep the old heights: register before the world loads; changes reach them on reload.
        // Vanilla and BetterContinents worlds never call it (patch WorldGenerator there yourself).
        public static void AddHeightModifier(System.Func<float, float, float, float> modifier)
        {
            if (modifier == null) return;
            global::VerdantsAscent.Terrain.Voxel.FiresWorldGenBase.AddHeightMod(modifier);
            FiresLogger.LogInfo($"[FiresWorldGen] (FatApi) height modifier added ({modifier.Method.DeclaringType?.FullName}.{modifier.Method.Name}); {HeightModifierCount} active");
        }

        public static bool RemoveHeightModifier(System.Func<float, float, float, float> modifier)
            => modifier != null && global::VerdantsAscent.Terrain.Voxel.FiresWorldGenBase.RemoveHeightMod(modifier);

        public static int HeightModifierCount => global::VerdantsAscent.Terrain.Voxel.FiresWorldGenBase.HeightModCount;

        // SERVER ONLY. Zones (vanilla zone coordinates, ZoneSystem.GetZone) grow their natural vegetation again as the world
        // would grow it now (GetBiome's answer there today): the same path as the Chaos Hammer's paint regrow. Removed: natural
        // vegetation only. Never removed: player pieces / anything with a creator, locations and everything in their footprint,
        // portals, FAT terrain edits (TileAnchors, voxel anchors). keepPlayerPieces: pieces are always kept (reserved; false changes
        // nothing). dryRun = count only, change nothing. Returns two ints per zone, in input order with duplicates dropped:
        // [generated objects removed (or that would be), player pieces kept]; null = not the server / not available (report says why).
        // The growth happens ~1 s later; the report line is logged with the prefix [BiomePaint].
        public static int[] RegenerateZones(System.Collections.Generic.IList<Vector2i> zones, bool keepPlayerPieces, bool dryRun, out string report)
        {
            report = string.Empty;
            try
            {
                if (zones == null || zones.Count == 0) { report = "no zones"; return null; }
                var list = new System.Collections.Generic.List<Vector2s>(zones.Count);
                foreach (var z in zones) list.Add(new Vector2s(z.x, z.y));
                return global::VerdantsAscent.Terrain.Zones.BiomePaint.RegrowZones(list, dryRun, out report);
            }
            catch (System.Exception ex) { report = "FAT error: " + ex.Message; FiresLogger.LogWarning($"[BiomePaint] (FatApi) RegenerateZones failed: {ex.Message}"); return null; }
        }

        // SERVER ONLY. Each zone's location follows the biome GetBiome answers there now, by the Chaos Hammer paint swap's
        // rules: one that still belongs stays (also protected / one-per-world places, and at sea); otherwise a vanilla location for
        // that ground that passes vanilla's placement test there replaces it, or the zone loses it when none fits. A built zone's old
        // location's objects go (never a build, tombstone or tamed creature; chest items drop at its centre) and the new one is placed
        // a moment later; an unbuilt zone only changes its plan. A built zone not loaded on the server (or someone in its dungeon) is
        // skipped and named in the report: call again. Zones without a location are left alone. dryRun = report only.
        // Returns two ints per zone, in input order with duplicates dropped: [swapped (or would be) 0/1, kept 0/1]; null = not
        // available (report says why). The report is also logged with the prefix [BiomePaint].
        public static int[] SwapZoneLocations(System.Collections.Generic.IList<Vector2i> zones, bool dryRun, out string report)
        {
            report = string.Empty;
            try
            {
                if (zones == null || zones.Count == 0) { report = "no zones"; return null; }
                var list = new System.Collections.Generic.List<Vector2s>(zones.Count);
                foreach (var z in zones) list.Add(new Vector2s(z.x, z.y));
                return global::VerdantsAscent.Terrain.Zones.BiomePaint.SwapZoneLocations(list, dryRun, out report);
            }
            catch (System.Exception ex) { report = "FAT error: " + ex.Message; FiresLogger.LogWarning($"[BiomePaint] (FatApi) SwapZoneLocations failed: {ex.Message}"); return null; }
        }

        // SERVER ONLY. The FORCED biome's locations replace the old ones. forcedBiome = a
        // Heightmap.Biome value (int). In those zones every location except protected / one-per-world places (start temple, boss
        // altars, traders; kept and listed) is removed, whatever biome it belongs to, and the forced biome's locations are placed
        // by vanilla's placement rules: how many = the forced biome's location density in this world x the zone count; which and
        // where = vanilla's placement test (altitude, slope, forest, distance and spacing rules), weighted by quantity, one per zone.
        // Placed now in zones loaded on the server; queued (saved, survives restarts) and placed when the zone next loads otherwise;
        // planned only in zones nobody has generated yet. Builds, tombstones and tamed creatures are never removed. dryRun = report
        // only. Returns three ints per zone, input order, duplicates dropped: [old removed 0/1, new placed or planned 0/1, kept 0/1];
        // null = not available (report says why). Call it BEFORE RegenerateZones so the regrow keeps out of the new footprints.
        public static int[] ReplaceZoneLocations(System.Collections.Generic.IList<Vector2i> zones, int forcedBiome, bool dryRun, out string report)
        {
            report = string.Empty;
            try
            {
                if (zones == null || zones.Count == 0) { report = "no zones"; return null; }
                var list = new System.Collections.Generic.List<Vector2s>(zones.Count);
                foreach (var z in zones) list.Add(new Vector2s(z.x, z.y));
                return global::VerdantsAscent.Terrain.Zones.BiomePaint.ReplaceZoneLocations(list, (Heightmap.Biome)forcedBiome, dryRun, out report);
            }
            catch (System.Exception ex) { report = "FAT error: " + ex.Message; FiresLogger.LogWarning($"[BiomePaint] (FatApi) ReplaceZoneLocations failed: {ex.Message}"); return null; }
        }

        // The same work as ReplaceZoneLocations (a large synchronous run can stall the server long enough to drop clients),
        // TIME-SLICED inside FAT (about 4 ms of the server's main thread per frame, a progress line in the log every 3 s). SERVER
        // ONLY. Plans over the whole zone set (density, spacing) without touching the world, then applies one zone per step.
        // done(counts, report) runs on the main thread when it ends: counts exactly as ReplaceZoneLocations (3 ints per zone),
        // or null with the reason in report (also when the world closes mid-run). false = not started (not the server, or another
        // FAT zone job is running; nothing is called back then). One job at a time.
        public static bool StartReplaceZoneLocations(System.Collections.Generic.IList<Vector2i> zones, int forcedBiome, bool dryRun, System.Action<int[], string> done)
        {
            try
            {
                if (zones == null || zones.Count == 0) return false;
                var list = new System.Collections.Generic.List<Vector2s>(zones.Count);
                foreach (var z in zones) list.Add(new Vector2s(z.x, z.y));
                var res = new global::VerdantsAscent.Terrain.Zones.BiomePaint.ZoneJobResult();
                var job = global::VerdantsAscent.Terrain.Zones.BiomePaint.ReplaceZoneLocationsJob(list, (Heightmap.Biome)forcedBiome, dryRun, res);
                bool ok = global::VerdantsAscent.Terrain.Zones.BiomePaint.StartJob($"FatApi.ReplaceZoneLocations ({list.Count} zone(s){(dryRun ? ", dry run" : "")})", job, res, done, out string why);
                if (!ok) FiresLogger.LogInfo($"[BiomePaint] (FatApi) StartReplaceZoneLocations not started: {why}");
                return ok;
            }
            catch (System.Exception ex) { FiresLogger.LogWarning($"[BiomePaint] (FatApi) StartReplaceZoneLocations failed: {ex.Message}"); return false; }
        }

        // For testing biomes on a SUPER FLAT world: on such a world, a point a registered test
        // answers true for gets a normal world's content: the biome / biome sector your own patches answer there (super flat stops
        // forcing Meadows), vegetation, auto clutter and spawners / spawn systems (random events stay off). The ground stays flat.
        // Register on EVERY peer (clients draw the biome and clutter), before the world loads. The test must be cheap, thread-safe
        // (terrain threads call it) and must not call GetBiome / GetBiomeSector. No effect on other worlds.
        public static void AddSuperFlatContentArea(System.Func<float, float, bool> insideAt)
        {
            if (insideAt == null) return;
            global::VerdantsAscent.Terrain.Voxel.FatSuperFlat.AddContentArea(insideAt);
            FiresLogger.LogInfo($"[FAT] super flat: content area test added ({insideAt.Method.DeclaringType?.FullName}.{insideAt.Method.Name}); {(global::VerdantsAscent.Terrain.Voxel.FatSuperFlat.ContentAreaCount)} active");
        }

        public static bool RemoveSuperFlatContentArea(System.Func<float, float, bool> insideAt)
            => insideAt != null && global::VerdantsAscent.Terrain.Voxel.FatSuperFlat.RemoveContentArea(insideAt);

        // 0.2.1355 (Fire 11-03, for Vedr's flowers: "the spring/summer flowers should not show up in areas that do not have clutter drawn"): FAT's own clutter gate
        // at this point, on the vanilla grass patch holding it. Super flat refuses outside content areas, water tools and sky islands; every other world answers true,
        // since FAT refuses nothing there (vanilla's biome, altitude and tilt tests are the caller's). Main thread only.
        public static bool ClutterAllowedAt(float x, float z)
        {
            if (!global::VerdantsAscent.Terrain.Voxel.FatSuperFlat.On || ClutterSystem.instance == null) return true;
            float size = ClutterSystem.instance.m_grassPatchSize;
            var patch = new UnityEngine.Vector2Int(UnityEngine.Mathf.FloorToInt(x / size + 0.5f), UnityEngine.Mathf.FloorToInt(z / size + 0.5f));
            return global::VerdantsAscent.Terrain.Voxel.FatSuperFlat.ClutterAllowed(patch, size);
        }

        // The last verdict line of FAT's console test <name> ("paint_stroke",
        // "territory_content") on this peer, exactly "[FatTest] <name> PASS: ..." or "[FatTest] <name> FAIL: ...", or null while the test
        // has not ended (each run of the command clears it first). Poll this instead of reading the log.
        public static string LastTestVerdict(string name)
        {
#if FIRES_TESTS
            try { return global::VerdantsAscent.Terrain.Zones.FatContentTests.LastVerdict(name); }
            catch (System.Exception) { return null; }
#else
            return null;   // a build without the test tools
#endif
        }

        // The one biome override authority: register a test that answers a Heightmap.Biome value (int) for a point it overrides,
        // or 0 where it does not (e.g. RPGM's ForceBiome territories). Paint circles from the Chaos Hammer answer first, then the
        // providers in add order, else the world. WorldGenerator.GetBiome / GetBiomeSector and the heightmap biome lookups all
        // answer through it, so the environment, weather, freezing, music, spawns, vegetation, locations, clutter and piece snow
        // follow. Register on every peer, before the world loads. The test must be cheap, thread-safe and must not call GetBiome.
        public static void AddBiomeOverride(System.Func<float, float, int> biomeAt)
        {
            if (biomeAt == null) return;
            global::VerdantsAscent.Terrain.Zones.BiomeOverride.AddProvider(biomeAt);
            FiresLogger.LogInfo($"[BiomeOverride] provider added ({biomeAt.Method.DeclaringType?.FullName}.{biomeAt.Method.Name}); {(global::VerdantsAscent.Terrain.Zones.BiomeOverride.ProviderCount)} active");
        }

        public static bool RemoveBiomeOverride(System.Func<float, float, int> biomeAt)
            => biomeAt != null && global::VerdantsAscent.Terrain.Zones.BiomeOverride.RemoveProvider(biomeAt);

        // The same authority for overrides that carry an alt biome: return the BiomeSector to use at (x, z) (its Biome and its
        // AltBiomes, e.g. "Plains - Heath"), or null where it does not override. Asked before the biome-only providers; the
        // sector's Biome is what the biome lookups answer. Same rules as AddBiomeOverride (cheap, thread-safe, no GetBiome calls,
        // one shared sector object per forced biome/alt).
        public static void AddBiomeSectorOverride(System.Func<float, float, BiomeSector> sectorAt)
        {
            if (sectorAt == null) return;
            global::VerdantsAscent.Terrain.Zones.BiomeOverride.AddSectorProvider(sectorAt);
            FiresLogger.LogInfo($"[BiomeOverride] sector provider added ({sectorAt.Method.DeclaringType?.FullName}.{sectorAt.Method.Name}); {(global::VerdantsAscent.Terrain.Zones.BiomeOverride.ProviderCount)} active");
        }

        public static bool RemoveBiomeSectorOverride(System.Func<float, float, BiomeSector> sectorAt)
            => sectorAt != null && global::VerdantsAscent.Terrain.Zones.BiomeOverride.RemoveSectorProvider(sectorAt);

        // 0.2.1062: call after a territory / snow base that an override provider or Core's DeepGroundAt answers for changes at
        // runtime; loaded vanilla heightmaps rebuild so their vertex colours (Deep North snow look) follow. Add-only.
        public static void RefreshBiomeOverrides() => global::VerdantsAscent.Terrain.Zones.BiomePaint.RefreshForced();

        // The overriding biome at (x, z) (paint first, then the registered providers), or 0 when nothing overrides it there.
        public static int BiomeOverrideAt(float x, float z)
            => global::VerdantsAscent.Terrain.Zones.BiomeOverride.TryAt(x, z, out var biome) ? (int)biome : 0;

        // A FAT zone job (StartReplaceZoneLocations) is running on this server.
        public static bool ZoneJobRunning => global::VerdantsAscent.Terrain.Zones.BiomePaint.JobRunning;

        // Zones waiting on the saved regrow queue to grow again / locations waiting to be placed (server; 0 elsewhere).
        public static int RegenerateZonesQueued => global::VerdantsAscent.Terrain.Zones.BiomePaint.RegenQueued;
        public static int ReplaceZoneLocationsQueued => global::VerdantsAscent.Terrain.Zones.BiomePaint.LocationsQueued;

        // ── Paint version (0.2.1058, [seasons]' snow diag; ADD-ONLY). Ticks on every biome-paint change (a new circle set or the
        // world's paint cleared). A per-zone cache keyed on painted ground (Core's snow base) drops its entries when it moves.
        public static int PaintVersion
        {
            get { try { return global::VerdantsAscent.Terrain.Zones.BiomePaintVersion.Current; } catch (System.Exception) { return 0; } }
        }

        // ── Halo ring keys (zone-key root fix, [fgn] for [worldgen] 10-24; Tools\PLAN_halo_zone_keys_20261024.md). ADD-ONLY.
        // On the HaloFlat ring-only world, OBJECT sectors (ZDO sectors: what loads and syncs) are keyed on the unrolled ring;
        // TERRAIN zones (ZoneSystem.GetZone / GetZonePos) stay vanilla x,z. Off HaloFlat every member answers like vanilla.

        // Ring object sectors are active in this world.
        public static bool HaloZonesOn
        {
            get { try { return global::VerdantsAscent.Terrain.Voxel.FatHaloSectors.On; } catch (System.Exception) { return false; } }
        }

        // The object sector to query ZDOMan with around p (FindSectorObjects / FindObjects): the ring sector on the ring,
        // ZoneSystem.GetZone(p) elsewhere. Use it instead of GetZone(p) for OBJECT queries.
        public static Vector2s HaloZone(UnityEngine.Vector3 p)
        {
            try { return global::VerdantsAscent.Terrain.Voxel.FatHaloSectors.SectorOf(p); }
            catch (System.Exception) { return ZoneSystem.GetZone(p); }
        }

        // An object sector's anchor: on the ring the point on the inside face at the sector's centre; GetZonePos elsewhere.
        public static UnityEngine.Vector3 HaloZonePos(Vector2s id)
        {
            try { return global::VerdantsAscent.Terrain.Voxel.FatHaloSectors.SectorPos(id); }
            catch (System.Exception) { return ZoneSystem.GetZonePos(id); }
        }

        // b - a in sector ids, the around-the-ring index wrapped at the seam (plain b - a off the ring).
        public static Vector2s HaloZoneDelta(Vector2s a, Vector2s b)
        {
            try { return global::VerdantsAscent.Terrain.Voxel.FatHaloSectors.Delta(a, b); }
            catch (System.Exception) { return new Vector2s(b.x - a.x, b.y - a.y); }
        }

        // pos -> u = metres around the ring (0..2πR, the ring's own angle frame), v = metres along the axis from the band middle.
        // False when there is no ring or pos is off its field.
        public static bool HaloUnroll(UnityEngine.Vector3 pos, out float u, out float v)
        {
            u = v = 0f;
            try { return global::VerdantsAscent.Terrain.Voxel.FatHaloSectors.HaloUnroll(pos, out u, out v); }
            catch (System.Exception) { return false; }
        }

        // The inverse of HaloUnroll: the point on the ring's inside face (radius R), u wrapped. False when there is no ring.
        public static bool HaloRoll(float u, float v, out UnityEngine.Vector3 pos)
        {
            pos = UnityEngine.Vector3.zero;
            try { return global::VerdantsAscent.Terrain.Voxel.FatHaloSectors.HaloRoll(u, v, out pos); }
            catch (System.Exception) { return false; }
        }

        // Distance along the ring surface (unrolled, wrapped at the seam) when both points are on the ring; 3D distance otherwise.
        public static float HaloSurfaceDistance(UnityEngine.Vector3 a, UnityEngine.Vector3 b)
        {
            try { return global::VerdantsAscent.Terrain.Voxel.FatHaloSectors.SurfaceDistance(a, b); }
            catch (System.Exception) { return UnityEngine.Vector3.Distance(a, b); }
        }

        // ── World Studio (0.2.1242, Fire 10-29 "Studio FAT first"; signatures settled with [visual], plan §0d.4a/§0d.4b) ──────
        // Server/host only, main thread only. false + reason when refused ("not host", "not loaded", "tooLarge: split it",
        // "stale", "unsupported: ...", "bad args: ..."). Feature detection: StudioApiVersion.
        public const int StudioApiVersion = 5;   // 1 = terrain (1242), 2 = + locations (1245), 3 = + water + vegetation (1246), 4 = + PieceHits / ConfirmPieces / pieces in undo (1261), 5 = + FatLocationArgs.Creator (1262)
        private static class P   // 0.2.1261 piece warn / restore, FatStudioPieces
        {
            internal static global::VerdantsAscent.Terrain.Studio.FatStudioPieces.Reach Of(UnityEngine.Vector3[] p, float r) => global::VerdantsAscent.Terrain.Studio.FatStudioPieces.Of(p, r);
            internal static string Gate(global::VerdantsAscent.Terrain.Studio.FatStudioPieces.Reach a, bool confirm, out int n) => global::VerdantsAscent.Terrain.Studio.FatStudioPieces.Gate(a, confirm, out n);
            internal static void Append(FatSnapshot s, global::VerdantsAscent.Terrain.Studio.FatStudioPieces.Reach a) => global::VerdantsAscent.Terrain.Studio.FatStudioPieces.Append(s, a);
        }

        // One terrain edit: Points = a stroke (one dab = one point), ONE snapshot of everything it touched taken before the edit.
        // Up to 9 zones per call. Level uses Height as the target y; Paint is TerrainModifier.PaintType as int.
        public static bool TerrainEdit(FatTerrainEditArgs args, out FatSnapshot before, out string reason)
        {
            try
            {
                before = null;
                var reach = P.Of(args?.Points, args != null ? args.Radius : 0f);
                if (args != null && args.Points != null && (reason = P.Gate(reach, args.ConfirmPieces, out _)) != null) return false;
                if (!global::VerdantsAscent.Terrain.Studio.FatStudioTerrain.Edit(args, out before, out reason)) return false;
                P.Append(before, reach);
                return true;
            }
            catch (System.Exception ex) { before = null; reason = $"failed: {ex.GetType().Name}: {ex.Message}"; return false; }
        }


        /// <summary>0.2.1293 ([lead], Studio walk mode: hoe / cultivator undo): run <paramref name="edit"/> (e.g. vanilla Player.PlacePiece
        /// of a TerrainOp entry) between TerrainEdit's own snapshot halves - the before bytes, the comps owned so the op applies here at once,
        /// then Save + the after hash + footprint. <paramref name="before"/> restores it with Restore, like a TerrainEdit. Main thread;
        /// the same piece gate as TerrainEdit (pieces in reach refuse unless <paramref name="confirmPieces"/>).</summary>
        public static bool TerrainUse(UnityEngine.Vector3 center, float radius, System.Action edit, bool confirmPieces, out FatSnapshot before, out string reason)
        {
            try
            {
                before = null;
                var reach = P.Of(new[] { center }, radius);
                if ((reason = P.Gate(reach, confirmPieces, out _)) != null) return false;
                if (!global::VerdantsAscent.Terrain.Studio.FatStudioTerrain.Use(center, radius, edit, out before, out reason)) return false;
                P.Append(before, reach);
                return true;
            }
            catch (System.Exception ex) { before = null; reason = $"failed: {ex.GetType().Name}: {ex.Message}"; return false; }
        }

        /// <summary>0.2.1287 (Studio walk mode, [lead] OK): use one entry of a FAT tool's build menu (the Chaos Hammer's chunk tools, saved
        /// chunk / area / skyland entries, biome paint) exactly as the in-game swing does - the same OnPlace code the TryPlacePiece hook
        /// runs, aimed at <paramref name="pos"/>, with the Studio's own confirm instead of the in-game yes/no and <paramref name="name"/>
        /// for the save entries instead of the name dialog. Main thread, on the admin's OWN client (refused otherwise). false + report for
        /// a non-admin, an unknown tool / entry, a V-tool brush (use TerrainEdit) or a real piece (place it as a piece). The report is the
        /// message the player would see; the server's final answer also shows in game and in the log. rot is accepted for later entries
        /// and unused now (no current entry reads a rotation).</summary>
        public static bool UseToolEntry(string tool, string entryPrefab, UnityEngine.Vector3 pos, UnityEngine.Quaternion rot, string name, out string report)
            => global::VerdantsAscent.Terrain.Zones.ZoneModeChunkTools.UseEntry(tool, entryPrefab, pos, name, out report);

        /// <summary>0.2.1291 (Studio God client with no game client): the same swing ON THE SERVER, with no local player - the entry's
        /// server work runs at once for <paramref name="adminPeer"/> (an admin's peer id; 0 = the server itself, e.g. the server-owned God
        /// clone). Same refusals as UseToolEntry, plus: server only. A save goes without its photo (no camera on a dedi); undo is
        /// recorded under that peer. Layer (cave / skyland) biome paint and V-tools stay client-side (V-tools: use TerrainEdit).</summary>
        public static bool UseToolEntryAt(string tool, string entryPrefab, UnityEngine.Vector3 pos, UnityEngine.Quaternion rot, string name, long adminPeer, out string report)
            => global::VerdantsAscent.Terrain.Zones.ZoneModeChunkTools.UseEntryAt(tool, entryPrefab, pos, name, adminPeer, out report);

        /// <summary>0.2.1287: true when that build-menu entry is a FAT tool (use UseToolEntry / TerrainEdit), false for a real piece.</summary>
        public static bool IsToolEntry(string entryPrefab) => global::VerdantsAscent.Terrain.Zones.ZoneModeChunkTools.IsToolEntry(entryPrefab);
        // Undo any Studio snapshot (Kind "terrain" in v1). Byte-identical restore; "stale" when the area changed since.
        public static bool Restore(FatSnapshot snapshot, out string reason)
        {
            try
            {
                if (!Body(snapshot, out var body, out var trailer, out reason)) return false;
                if (trailer != null)
                {
                    bool ok = Restore(body, out reason);
                    if (ok) global::VerdantsAscent.Terrain.Studio.FatStudioPieces.Restore(trailer, snapshot.Id);
                    return ok;
                }
                if (snapshot != null && snapshot.Kind == global::VerdantsAscent.Terrain.Studio.FatStudioTerrain.Kind)
                    return global::VerdantsAscent.Terrain.Studio.FatStudioTerrain.Restore(snapshot, out reason);
                if (snapshot != null && snapshot.Kind == global::VerdantsAscent.Terrain.Studio.FatStudioLocations.Kind)
                    return global::VerdantsAscent.Terrain.Studio.FatStudioLocations.Restore(snapshot, out reason);
                if (snapshot != null && (snapshot.Kind == global::VerdantsAscent.Terrain.Studio.FatStudioWater.WaterKind || snapshot.Kind == global::VerdantsAscent.Terrain.Studio.FatStudioWater.VegKind))
                    return global::VerdantsAscent.Terrain.Studio.FatStudioWater.Restore(snapshot, out reason);
                reason = $"unsupported: snapshot kind {snapshot?.Kind ?? "null"}";
                return false;
            }
            catch (System.Exception ex) { reason = $"failed: {ex.GetType().Name}: {ex.Message}"; return false; }
        }

        // ── Studio locations (0.2.1245, StudioApiVersion 2) ──
        // Place a location (vanilla, FAT or EWD by its real name) at args.Position in a generated zone that has none (one per zone,
        // as vanilla); RotY NaN = vanilla's rotation rule, Seed 0 = vanilla's zone seed. "loading: ..." = its prefab is loading,
        // call again next frame. The snapshot undoes it through Restore.
        public static bool LocationPlace(FatLocationArgs args, out FatLocationHandle handle, out FatSnapshot snapshot, out string reason)
        {
            try { return global::VerdantsAscent.Terrain.Studio.FatStudioLocations.Place(args, out handle, out snapshot, out reason); }
            catch (System.Exception ex) { handle = null; snapshot = null; reason = $"failed: {ex.GetType().Name}: {ex.Message}"; return false; }
        }

        // Remove a placed or generated location; the snapshot holds every removed object's data, and Restore puts them back.
        public static bool LocationRemove(FatLocationHandle handle, out FatSnapshot snapshot, out string reason)
        {
            try { return global::VerdantsAscent.Terrain.Studio.FatStudioLocations.Remove(handle, out snapshot, out reason); }
            catch (System.Exception ex) { snapshot = null; reason = $"failed: {ex.GetType().Name}: {ex.Message}"; return false; }
        }

        // Placed locations within radius of centre (radius <= 0: the one in centre's zone).
        public static FatLocationHandle[] ListLocations(UnityEngine.Vector3 centre, float radius)
        {
            try { return global::VerdantsAscent.Terrain.Studio.FatStudioLocations.List(centre, radius); }
            catch (System.Exception) { return new FatLocationHandle[0]; }
        }

        // Every enabled location type's name in this world (vanilla, FAT, EWD), sorted: the Studio's browse list.
        public static string[] LocationNames()
        {
            try { return global::VerdantsAscent.Terrain.Studio.FatStudioLocations.Names(); }
            catch (System.Exception) { return new string[0]; }
        }

        // Changes whenever the location's objects change (0 = no such location here).
        public static uint LocationRevision(FatLocationHandle handle)
        {
            try { return global::VerdantsAscent.Terrain.Studio.FatStudioLocations.Revision(handle); }
            catch (System.Exception) { return 0u; }
        }

        // ── Studio water + vegetation (0.2.1246, StudioApiVersion 3; Fire 10-29 "Placed water first") ──
        // Water is ASYNC: the carve is immediate, the surface lays ~1-2 s later (a river carves over several frames first) and the
        // combiner anchors save after a debounce. Every snapshot is a REGION snapshot taken before the edit, so its Restore also
        // takes back whatever the async part laid since. Generated (hydrology) water is listed as Kind "generated" but its
        // remove / replace is refused "unsupported" for now.
        public static bool WaterPlacePond(FatPondArgs args, out FatWaterHandle handle, out FatSnapshot snapshot, out string reason)
        {
            try
            {
                handle = null; snapshot = null;
                var reach = P.Of(args != null ? new[] { args.Center } : null, args != null ? args.Radius + 2f : 0f);   // + the shore
                if (args != null && (reason = P.Gate(reach, args.ConfirmPieces, out _)) != null) return false;
                if (!global::VerdantsAscent.Terrain.Studio.FatStudioWater.PlacePond(args, out handle, out snapshot, out reason)) return false;
                P.Append(snapshot, reach);
                return true;
            }
            catch (System.Exception ex) { handle = null; snapshot = null; reason = $"failed: {ex.GetType().Name}: {ex.Message}"; return false; }
        }

        public static bool WaterPlaceRiver(FatRiverArgs args, out FatWaterHandle handle, out FatSnapshot snapshot, out string reason)
        {
            try
            {
                handle = null; snapshot = null;
                var reach = P.Of(args?.Path, args != null ? args.Width * 0.5f + args.Depth : 0f);
                if (args != null && args.Path != null && (reason = P.Gate(reach, args.ConfirmPieces, out _)) != null) return false;
                if (!global::VerdantsAscent.Terrain.Studio.FatStudioWater.PlaceRiver(args, out handle, out snapshot, out reason)) return false;
                P.Append(snapshot, reach);
                return true;
            }
            catch (System.Exception ex) { handle = null; snapshot = null; reason = $"failed: {ex.GetType().Name}: {ex.Message}"; return false; }
        }

        public static bool WaterRemove(FatWaterHandle handle, out FatSnapshot snapshot, out string reason)
        {
            try { return global::VerdantsAscent.Terrain.Studio.FatStudioWater.Remove(handle, out snapshot, out reason); }
            catch (System.Exception ex) { snapshot = null; reason = $"failed: {ex.GetType().Name}: {ex.Message}"; return false; }
        }

        // Exactly one of pond / river. One snapshot = one undo for the remove and the new shape together.
        public static bool WaterReplace(FatWaterHandle handle, FatPondArgs pond, FatRiverArgs river, out FatWaterHandle newHandle, out FatSnapshot snapshot, out string reason)
        {
            try
            {
                newHandle = null; snapshot = null;
                var reach = pond != null ? P.Of(new[] { pond.Center }, pond.Radius + 2f) : P.Of(river?.Path, river != null ? river.Width * 0.5f + river.Depth : 0f);
                bool confirm = pond != null ? pond.ConfirmPieces : river != null && river.ConfirmPieces;
                if ((pond != null || river?.Path != null) && (reason = P.Gate(reach, confirm, out _)) != null) return false;
                if (!global::VerdantsAscent.Terrain.Studio.FatStudioWater.Replace(handle, pond, river, out newHandle, out snapshot, out reason)) return false;
                P.Append(snapshot, reach);
                return true;
            }
            catch (System.Exception ex) { newHandle = null; snapshot = null; reason = $"failed: {ex.GetType().Name}: {ex.Message}"; return false; }
        }

        public static FatWaterHandle[] ListWaterFeatures(UnityEngine.Vector3 centre, float radius)
        {
            try { return global::VerdantsAscent.Terrain.Studio.FatStudioWater.List(centre, radius); }
            catch (System.Exception) { return new FatWaterHandle[0]; }
        }

        public static uint WaterRevision(FatWaterHandle handle)
        {
            try { return global::VerdantsAscent.Terrain.Studio.FatStudioWater.Revision(handle); }
            catch (System.Exception) { return 0u; }
        }

        // Remove / Thin take only creator-0 vegetation from the world's vegetation list (never pieces); Paint plants the listed
        // vegetation prefabs where they suit (biome, altitude), Density per 100 m².
        public static bool VegetationEdit(FatVegArgs args, out FatSnapshot snapshot, out string reason)
        {
            try { return global::VerdantsAscent.Terrain.Studio.FatStudioWater.Vegetation(args, out snapshot, out reason); }
            catch (System.Exception ex) { snapshot = null; reason = $"failed: {ex.GetType().Name}: {ex.Message}"; return false; }
        }

        // 0.2.1248: undo WITHOUT the stale check (the Studio asks "the area changed since: undo anyway?" first). Terrain only for now:
        // a v1 snapshot is written back whole, a v2 one over its footprint. Other kinds answer "unsupported".
        public static bool RestoreForce(FatSnapshot snapshot, out string reason)
        {
            try
            {
                if (!Body(snapshot, out var body, out var trailer, out reason)) return false;
                if (body != null && body.Kind == global::VerdantsAscent.Terrain.Studio.FatStudioTerrain.Kind)
                {
                    bool ok = global::VerdantsAscent.Terrain.Studio.FatStudioTerrain.Restore(body, true, out reason);
                    if (ok && trailer != null) global::VerdantsAscent.Terrain.Studio.FatStudioPieces.Restore(trailer, snapshot.Id);
                    return ok;
                }
                reason = $"unsupported: a forced restore of kind {snapshot?.Kind ?? "null"} (terrain only)";
                return false;
            }
            catch (System.Exception ex) { reason = $"failed: {ex.GetType().Name}: {ex.Message}"; return false; }
        }

        // 0.2.1261 (StudioApiVersion 4, Fire 10-29 "Warn + undo restores"; contract with [visual] / [classes-ui]): every player-built
        // piece (creator != 0) within radius + 4 m of the points (one point, or a polyline: a stroke / a river path), nearest first.
        // Why "inside" = within radius, "undercut" = in the 4 m margin. A terrain / pond / river edit with any is refused "pieces: N ..."
        // unless its args say ConfirmPieces; a confirmed edit's snapshot keeps those pieces, and its undo makes missing ones again and
        // removes the item drops the break left. op is informational ("terrain", "pond", "river"); a pond's reach is radius + 2 (shore),
        // a river's width / 2 + depth.
        public static FatPieceHit[] PieceHits(UnityEngine.Vector3[] points, float radius, string op)
        {
            try { return global::VerdantsAscent.Terrain.Studio.FatStudioPieces.Hits(P.Of(points, radius)); }
            catch (System.Exception) { return new FatPieceHit[0]; }
        }

        public static FatPieceHit[] PieceHits(UnityEngine.Vector3 center, float radius, string op) => PieceHits(new[] { center }, radius, op);

        // The snapshot without its 1261 pieces trailer (hash checked over the whole); trailer null = an older snapshot, unchanged.
        private static bool Body(FatSnapshot s, out FatSnapshot body, out byte[] trailer, out string reason)
        {
            body = s; trailer = null; reason = null;
            if (s == null || s.Bytes == null) return true;
            byte[] head = global::VerdantsAscent.Terrain.Studio.FatStudioPieces.Split(s.Bytes, out trailer);
            if (trailer == null) return true;
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                byte[] h = sha.ComputeHash(s.Bytes);
                if (s.Hash != null && !System.Linq.Enumerable.SequenceEqual(h, s.Hash)) { reason = "bad snapshot: hash mismatch"; return false; }
                body = new FatSnapshot { Id = s.Id, Kind = s.Kind, Version = s.Version, Bytes = head, Hash = sha.ComputeHash(head) };
            }
            return true;
        }

        // The zone's TerrainComp ZDO data revision (0 = no terrain edits in that zone / not loaded).
        public static uint TerrainRevision(Vector2s zone)
        {
            try { return global::VerdantsAscent.Terrain.Studio.FatStudioTerrain.Revision(zone); }
            catch (System.Exception) { return 0u; }
        }
    }

    // World Studio contract types (add-only: new fields only, never rename).
    public enum FatTerrainOp { Raise = 0, Dig = 1, Smooth = 2, Level = 3, Paint = 4 }

    public class FatTerrainEditArgs
    {
        public FatTerrainOp Op;
        public UnityEngine.Vector3[] Points;
        public float Radius = 4f;
        public float Strength = 1f;
        public float Height;
        public int Paint;
        public bool ConfirmPieces;   // 0.2.1261: go ahead although player pieces are in reach (FatApi.PieceHits); undo restores them
    }

    public class FatLocationArgs
    {
        public string Name;
        public UnityEngine.Vector3 Position;
        public float RotY = float.NaN;
        public int Seed;
        public long Creator;   // 0.2.1262 (StudioApiVersion 5, Fire: World / Me per placement): 0 = world-owned; else stamped on every Piece the location spawns (late parts too)
    }

    // A placed location: its real name (m_prefabName for EWD blueprint locations) and position. Studio key: "<nameHash>@x,y,z".
    public class FatLocationHandle
    {
        public string Name;
        public int NameHash;
        public UnityEngine.Vector3 Position;

        public override string ToString()
            => string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0}@{1:0.00},{2:0.00},{3:0.00}", NameHash, Position.x, Position.y, Position.z);

        public static bool TryParse(string key, out FatLocationHandle handle)
        {
            handle = null;
            if (string.IsNullOrEmpty(key)) return false;
            int at = key.IndexOf('@');
            if (at <= 0) return false;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var xyz = key.Substring(at + 1).Split(',');
            if (xyz.Length != 3 || !int.TryParse(key.Substring(0, at), System.Globalization.NumberStyles.Integer, inv, out int hash)) return false;
            if (!float.TryParse(xyz[0], System.Globalization.NumberStyles.Float, inv, out float x)
                || !float.TryParse(xyz[1], System.Globalization.NumberStyles.Float, inv, out float y)
                || !float.TryParse(xyz[2], System.Globalization.NumberStyles.Float, inv, out float z)) return false;
            handle = new FatLocationHandle { NameHash = hash, Position = new UnityEngine.Vector3(x, y, z) };
            return true;
        }
    }

    public class FatPondArgs
    {
        public UnityEngine.Vector3 Center;
        public float Radius = 8f;
        public float Depth = 2f;
        public float Level = float.NaN;   // NaN = rim - 0.25 m (the Lake tool's)
        public bool Shore = true;
        public bool ConfirmPieces;   // 0.2.1261 (as FatTerrainEditArgs.ConfirmPieces)
    }

    public class FatRiverArgs
    {
        public UnityEngine.Vector3[] Path;
        public float Width = 4f;
        public float Depth = 1.5f;
        public float StartLevel = float.NaN;   // NaN = the bed follows the ground (v3 accepts NaN only)
        public float EndLevel = float.NaN;
        public bool ConfirmPieces;   // 0.2.1261 (as FatTerrainEditArgs.ConfirmPieces)
    }

    // A water feature: "pond" | "river" | "generated", its centre and reach. Studio key: "<kindHash>@x,y,z".
    public class FatWaterHandle
    {
        public string Kind;
        public UnityEngine.Vector3 Position;
        public float Radius;

        public override string ToString()
            => string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0}@{1:0.00},{2:0.00},{3:0.00}", (Kind ?? "").GetStableHashCode(), Position.x, Position.y, Position.z);

        public static bool TryParse(string key, out FatWaterHandle handle)
        {
            handle = null;
            if (!FatLocationHandle.TryParse(key, out var h)) return false;
            string kind = null;
            foreach (var k in new[] { "pond", "river", "generated" }) if (k.GetStableHashCode() == h.NameHash) kind = k;
            if (kind == null) return false;
            handle = new FatWaterHandle { Kind = kind, Position = h.Position, Radius = 0f };
            return true;
        }
    }

    public enum FatVegMode { Remove = 0, Thin = 1, Paint = 2 }

    public class FatVegArgs
    {
        public UnityEngine.Vector3[] Points;
        public float Radius = 4f;
        public FatVegMode Mode;
        public string[] Prefabs;     // Paint: what to plant; Remove / Thin: only these (null = all vegetation)
        public float Density = 10f;  // per 100 m² (Paint target, Thin keeps this many)
        public int Seed;             // 0 = from the stroke
        public bool ConfirmPieces;   // 0.2.1261: accepted so every op has the same shape; vegetation edits never touch pieces, so not needed
    }

    // 0.2.1261: a player-built piece in an edit's reach. Key = the Studio zone-row key "<prefabHash>@x.xx,y.yy,z.zz".
    public class FatPieceHit
    {
        public string Key;
        public string Name;
        public long CreatorId;
        public UnityEngine.Vector3 Pos;
        public string Why;   // "inside" | "undercut"
    }

    public class FatSnapshot
    {
        public string Id;
        public string Kind;
        public int Version;
        public byte[] Hash;    // SHA-256 of Bytes
        public byte[] Bytes;
    }

    // 0.2.1203 (lead, dense58: Vedr IceCubes "Slow frame ~91 ms" x74; Vedr 0.8.75 calls TryIceAt / IsFreezingWater): what the FAT side
    // of those calls costs. Every 10 s at Debug: calls, total ms, the worst single call and the worst FRAME total per entry point, so a
    // Vedr slow frame splits into FAT time and Vedr's own loop (its 5 s FindObjectsByType scans, Chill).
    internal static class IceApiTiming
    {
        private static readonly string[] Names = { "TryIceAt", "IsFreezingWater" };
        private static readonly long[] s_calls = new long[2], s_ticks = new long[2], s_worst = new long[2], s_frameTicks = new long[2], s_worstFrame = new long[2];
        private static readonly int[] s_frame = new int[2];
        private static long s_logAt;
        private static readonly object s_gate = new object();

        internal static void Add(int which, long t0)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp(), d = now - t0;
            int frame = -1;
            try { if (System.Threading.Thread.CurrentThread.ManagedThreadId == global::VerdantsAscent.Water.Core.WaterStateField.MainThread) frame = UnityEngine.Time.frameCount; } catch { }
            lock (s_gate)
            {
                s_calls[which]++; s_ticks[which] += d; if (d > s_worst[which]) s_worst[which] = d;
                if (frame != s_frame[which]) { s_frame[which] = frame; s_frameTicks[which] = 0; }
                s_frameTicks[which] += d; if (s_frameTicks[which] > s_worstFrame[which]) s_worstFrame[which] = s_frameTicks[which];
                if (s_logAt == 0) s_logAt = now + 10 * System.Diagnostics.Stopwatch.Frequency;
                if (now < s_logAt || !FiresLogger.DebugEnabled) return;
                s_logAt = now + 10 * System.Diagnostics.Stopwatch.Frequency;
                double ms = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                var sb = new System.Text.StringBuilder("[WaterIce] ice API last 10 s (callers: Vedr ice cubes): ");
                for (int i = 0; i < 2; i++)
                {
                    sb.Append($"{Names[i]} {s_calls[i]} call(s) {s_ticks[i] * ms:0.0} ms (worst call {s_worst[i] * ms:0.00} ms, worst frame {s_worstFrame[i] * ms:0.0} ms){(i == 0 ? "; " : "")}");
                    s_calls[i] = 0; s_ticks[i] = 0; s_worst[i] = 0; s_worstFrame[i] = 0;
                }
                FiresLogger.LogDebug(sb.Append(" (0.2.1203)").ToString());
            }
        }
    }
}
