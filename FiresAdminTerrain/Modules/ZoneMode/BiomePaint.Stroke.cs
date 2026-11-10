using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using VerdantsAscent.Terrain.Voxel;

namespace VerdantsAscent.Terrain.Zones
{
    // The Chaos Hammer's biome paint swaps biome, clutter and vegetation within its painted area, is non-destructive to
    // existing player builds, and only ever adds locations.
    //  - A STROKE is the run of paint circles laid with no gap longer than StrokeGapSeconds. Each circle still clears its natural
    //    vegetation and grows the new ground's at once (BiomePaint.Regrow, one circle = a few zones); the old location swap is gone.
    //  - When the stroke ends, a time-sliced job ADDS the painted ground's locations where one fits wholly inside the painted area
    //    (vanilla's placement test at the spot, its footprint inside one of the stroke's circles, no player piece within it, a zone
    //    that has no location yet). How many: the ground's density (this world's, else its vanilla table) x the painted area. It never
    //    removes a location, a player piece, a tombstone or a tame. One line per stroke.
    //  - Super flat: every non-Restore paint circle is a content area (FatSuperFlat), so painted ground grows and spawns there.
    //  - The paint clear logs why objects inside a circle were NOT cleared (not vegetation, with the top prefab names; a creator;
    //    a location footprint; protected), so a "0 growth cleared" names its cause.
    internal static partial class BiomePaint
    {
        private const float StrokeGapSeconds = 1.5f;
        private static readonly List<Circle> s_stroke = new List<Circle>();
        private static float s_strokeLastAt;
        private static int s_strokeCleared;
        private static bool s_contentRegistered, s_swapRetiredLogged;

        // ── clear diagnostics ───────────────────────────────────────────────
        private static int s_dSeen, s_dCreator, s_dKeepOut, s_dProtected, s_dNotVeg;
        private static readonly Dictionary<int, int> s_dNotVegByPrefab = new Dictionary<int, int>();

        private static void ResetClearDiag() { s_dSeen = s_dCreator = s_dKeepOut = s_dProtected = s_dNotVeg = 0; s_dNotVegByPrefab.Clear(); }

        private static void NoteNotVeg(int prefab)
        {
            s_dNotVeg++;
            s_dNotVegByPrefab[prefab] = s_dNotVegByPrefab.TryGetValue(prefab, out int n) ? n + 1 : 1;
        }

        private static string ClearDiag()
        {
            var top = new List<KeyValuePair<int, int>>(s_dNotVegByPrefab);
            top.Sort((a, b) => b.Value.CompareTo(a.Value));
            var names = new List<string>();
            for (int i = 0; i < top.Count && i < 6; i++)
            {
                var go = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(top[i].Key) : null;
                names.Add($"{(go != null ? go.name : top[i].Key.ToString())} x{top[i].Value}");
            }
            return $"{s_dSeen} object(s) inside; kept: {s_dNotVeg} not on the world's vegetation list{(names.Count > 0 ? " (" + string.Join(", ", names) + ")" : "")}, "
                + $"{s_dCreator} with a creator (built / placed), {s_dKeepOut} in a location footprint, {s_dProtected} protected / skyland; "
                + $"vegetation list {s_vegetation.Count} prefab(s).";
        }

        // ── strokes ─────────────────────────────────────────────────────────
        private static void NoteStroke(in Circle circle)
        {
            EnsureContentArea();
            if (circle.Brush == Brush.Restore) return;
            if (s_stroke.Count > 0 && (s_stroke[0].Ground != circle.Ground || s_stroke[0].Brush != circle.Brush || s_stroke[0].Blend != circle.Blend)) FlushStroke(true);
            s_stroke.Add(circle);
            s_strokeLastAt = Time.time;
        }

        // From Tick, on the server.
        private static void FlushStroke(bool force)
        {
            if (s_stroke.Count == 0 || (!force && Time.time - s_strokeLastAt < StrokeGapSeconds)) return;
            if (JobRunning && !force) return;   // the next tick, once the running zone job ends
            var circles = new List<Circle>(s_stroke);
            s_stroke.Clear();
            var res = new ZoneJobResult();
            if (!StartJob($"paint stroke ({circles.Count} circle(s))", StrokeLocationsJob(circles, res), res, null, out string why))
                FiresLogger.LogInfo($"[BiomePaint] stroke {circles[0].Ground}: locations not added ({why}).");
        }

        private static IEnumerator StrokeLocationsJob(List<Circle> circles, ZoneJobResult res)
        {
            var zs = ZoneSystem.instance;
            var generator = WorldGenerator.instance;
            var ground = circles[0].Ground;
            string label = $"stroke {ground}{(circles[0].Brush == Brush.Blend ? " / " + circles[0].Blend : "")} {circles.Count} circle(s)";
            if (zs == null || generator == null || ZDOMan.instance == null || !zs.LocationsGenerated || !ResolveLocations() || !Resolve())
            { res.Report = $"[BiomePaint] {label}: locations not added (the world is not ready)"; FiresLogger.LogInfo(res.Report); yield break; }
            // Painted area and the zones it touches.
            float area = 0f;
            var zones = new List<Vector2s>();
            var seen = new HashSet<Vector2s>();
            foreach (var c in circles)
            {
                area += c.Outline != null ? c.Outline.Area : Mathf.PI * c.R * c.R;   // overlaps counted twice: an upper bound, the fit test decides
                foreach (var z in ZonesOf(c)) if (seen.Add(z)) zones.Add(z);
            }
            if (circles[0].Brush == Brush.Custom)
            {
                int swappedOut = SwapOutLocations(zs, circles, zones);
                if (swappedOut > 0) FiresLogger.LogInfo($"[BiomePaint] {label}: {swappedOut} old location(s) taken away; the new ground's follow (0.2.1374).");
            }
            var measured = new LocationDensity();
            yield return MeasureLocationDensity(zs, generator, ground, res, measured);
            float density = measured.PerZone;
            float exact = density * area / (ZoneSystem.c_ZoneSize * ZoneSystem.c_ZoneSize);
            uint seed = (uint)generator.GetSeed();
            uint roll = Mix(Mix(Mix(HashBasis, seed), (uint)Mathf.RoundToInt(circles[0].X)), (uint)Mathf.RoundToInt(circles[0].Z));
            int want = Mathf.FloorToInt(exact) + ((roll % 1000u) / 1000f < exact - Mathf.Floor(exact) ? 1 : 0);
            var extra = new List<KeyValuePair<Vector2s, ZoneSystem.LocationInstance>>();
            var tries = new Dictionary<string, int>();
            var added = new Dictionary<string, int>();
            int placedNow = 0, queued = 0, planned = 0, noFit = 0, hasLocation = 0, nearBuild = 0;
            res.Phase = "pick"; res.Steps = zones.Count;
            var order = new List<Vector2s>(zones);
            order.Sort((a, b) => ZoneOrder(seed, a).CompareTo(ZoneOrder(seed, b)));
            for (int zi = 0; zi < order.Count && extra.Count < want; zi++)
            {
                res.Step = zi;
                yield return null;
                var zone = order[zi];
                if (zs.m_locationInstances.TryGetValue(zone, out var cur) && cur.m_location != null) { hasLocation++; continue; }
                // Spots: each stroke circle's centre that lies in this zone.
                bool ok = false;
                Vector3 at = default;
                foreach (var c in circles)
                {
                    if (ok) break;
                    if (!SameZone(ZoneSystem.GetZone(new Vector3(c.X, 0f, c.Z)), zone)) continue;
                    s_planExtra = extra; s_planTries = tries;
                    try { ok = TryPickZone(zs, zone, new Vector3(c.X, 0f, c.Z), ground, out at); }
                    finally { ClearPlan(); }
                    if (!ok) continue;
                    float r = Mathf.Max(s_pickZone.m_exteriorRadius, s_pickZone.m_interiorRadius);
                    if (!InsideStroke(circles, at, r)) { ok = false; noFit++; continue; }
                    if (BuildNear(zone, at, r + LocationMargin)) { ok = false; nearBuild++; continue; }
                }
                if (!ok) continue;
                var inst = new ZoneSystem.LocationInstance { m_location = s_pickZone, m_position = at, m_placed = false };
                extra.Add(new KeyValuePair<Vector2s, ZoneSystem.LocationInstance>(zone, inst));
                string nm = LocationName(s_pickZone);
                added[nm] = added.TryGetValue(nm, out int k) ? k + 1 : 1;
                yield return null;
                // Apply now, one zone per step (add only: the zone had no location).
                if (zs.m_locationInstances.TryGetValue(zone, out var again) && again.m_location != null) { extra.RemoveAt(extra.Count - 1); continue; }
                zs.m_locationInstances[zone] = inst;
                if (!GeneratedZones(zs).Contains(zone)) planned++;
                else if (HeightmapOf(zs, zone) != null) { ClearFootprint(zs, zone, inst.m_location, inst.m_position); QueuePlacement(zs, zone); placedNow++; }
                else { QueueLocation(zone); queued++; }
                if (HasIcon(inst.m_location)) SendIcons(zs);
            }
            if (queued > 0) SaveRegenQueue();
            res.Report = $"[BiomePaint] {label}: veg cleared {s_strokeCleared} (grows again in a moment), locations added {extra.Count} of {want} wanted"
                + $" ({exact:0.00} expected over {area:0} m2 at {density:0.000} per zone)"
                + (added.Count > 0 ? ": " + string.Join(", ", Linq(added)) : "")
                + (extra.Count > 0 ? $" ({placedNow} now, {queued} when their zone next loads, {planned} planned)" : "")
                + $"; skipped: {hasLocation} zone(s) already with a location, {noFit} pick(s) not fitting inside the paint, {nearBuild} near a player piece; "
                + "no location removed, player pieces untouched.";
            s_strokeCleared = 0;
            FiresLogger.LogInfo(res.Report);
        }

        private static bool SameZone(Vector2s a, Vector2s b) => a.x == b.x && a.y == b.y;

        private static bool InsideStroke(List<Circle> circles, Vector3 at, float r)
        {
            foreach (var c in circles)
            {
                if (c.Rim(at.x, at.z, r) >= r) return true;   // 0.2.1375: a drawn outline's rim as well
            }
            return false;
        }

        private static bool BuildNear(Vector2s zone, Vector3 at, float r)
        {
            var found = new List<ZDO>();
            ZDOMan.instance.FindSectorObjects(zone, new SimulationDistance(1, 0), found);
            foreach (var zdo in found)
                if (zdo != null && ZoneModeBuilds.IsBuilt(zdo) && ZoneModeStartSpots.FlatDistanceSquared(zdo.GetPosition(), at) <= r * r) return true;
            return false;
        }

        // ── super flat ──────────────────────────────────────────────────────
        private static void EnsureContentArea()
        {
            if (s_contentRegistered) return;
            s_contentRegistered = true;
            FatSuperFlat.AddContentArea(PaintedAt);
        }

        // Thread-safe: reads the published snapshot only.
        private static bool PaintedAt(float wx, float wz) => TryNewest(wx, wz, out var circle, out _) && circle.Brush != Brush.Restore;

        internal static void NoteStrokeCleared(int n) => s_strokeCleared += n;

        private static void LogSwapRetired()
        {
            if (s_swapRetiredLogged) return;
            s_swapRetiredLogged = true;
            FiresLogger.LogInfo("[BiomePaint] location swaps are retired: paint only ADDS locations at the end of a stroke; no location is ever removed or swapped back.");
        }
    }
}
