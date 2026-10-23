namespace VerdantsAscent
{
    // FAT's public API for other mods. A CONTRACT: add-only, never rename or
    // change a signature; consumers reflect it by name (type "VerdantsAscent.FatApi").
    public static class FatApi
    {
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

        // A FAT zone job (StartReplaceZoneLocations) is running on this server.
        public static bool ZoneJobRunning => global::VerdantsAscent.Terrain.Zones.BiomePaint.JobRunning;

        // Zones waiting on the saved regrow queue to grow again / locations waiting to be placed (server; 0 elsewhere).
        public static int RegenerateZonesQueued => global::VerdantsAscent.Terrain.Zones.BiomePaint.RegenQueued;
        public static int ReplaceZoneLocationsQueued => global::VerdantsAscent.Terrain.Zones.BiomePaint.LocationsQueued;
    }
}
