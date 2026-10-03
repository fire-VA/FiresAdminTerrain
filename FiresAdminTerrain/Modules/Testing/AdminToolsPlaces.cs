using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using BepInEx;
using UnityEngine;
using VerdantsAscent.Terrain.Voxel;

namespace VerdantsAscent.Terrain.Testing
{
    internal enum PlaceKind
    {
        Surface,
        Cave,
        Island,
    }

    // admintools_test's three places, fixed per world so runs compare: `find` picks them once and writes them to
    // BepInEx\FiresTests\admintools_spots_<world>.txt, and every later run reads them back. Each place lays its cases out
    // on its own grid of cells, one case per cell.
    internal static class AdminToolsPlaces
    {
        private const string ResultsFolder = "FiresTests";
        private const string SpotsFilePattern = "admintools_spots_{0}.txt";
        // SURFACE: flat dry ground, away from the start point's buildings, big enough for the whole grid.
        private const float SurfaceMinDistance = 300f;
        private const float SurfaceMaxDistance = 1500f;
        private const float SurfaceSearchStep = 16f;
        private const float SurfaceFlatness = 3.0f;       // the largest bump off the grid's fitted plane (R70: best 1.8 m at 4.6 deg; R75: 2.1 m; R77: 2.6 m)
        private const float SurfaceMaxSlopeDegrees = 6f;  // each cell takes its own ground, so an even slope is fine
        private const float DryAboveSea = 4f;
        private const int FlatnessSamples = 5;
        internal const int GridColumns = 6;
        internal const float SurfaceCellSpacing = 12f;
        internal const float IslandCellSpacing = 7f;
        // Arrival: open sky above the surface, a roof above a cave floor, open air far below an island's top.
        private const float SkyCheckMetres = 60f;
        private const float CaveRoofMaxMetres = 25f;
        private const float IslandDropMinMetres = 30f;
        private const float PieceClearMetres = 40f;
        private const float HeadHeight = 1.8f;

        internal static string SpotsPath(string world)
            => Path.Combine(Paths.BepInExRootPath, ResultsFolder, string.Format(CultureInfo.InvariantCulture, SpotsFilePattern, world));

        internal static Dictionary<PlaceKind, Vector3> Load(string world)
        {
            var spots = new Dictionary<PlaceKind, Vector3>();
            string path = SpotsPath(world);
            if (!File.Exists(path)) return spots;
            foreach (string line in File.ReadAllLines(path))
            {
                var parts = line.Split(' ');
                if (parts.Length < 4 || !Enum.TryParse(parts[0], true, out PlaceKind kind)) continue;
                if (float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                    && float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float y)
                    && float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
                    spots[kind] = new Vector3(x, y, z);
            }
            return spots;
        }

        internal static void Save(string world, Dictionary<PlaceKind, Vector3> spots)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SpotsPath(world)));
            var lines = new List<string>();
            foreach (var spot in spots)
                lines.Add(string.Format(CultureInfo.InvariantCulture, "{0} {1:0.0} {2:0.0} {3:0.0}", spot.Key, spot.Value.x, spot.Value.y, spot.Value.z));
            File.WriteAllLines(SpotsPath(world), lines.ToArray());
        }

        // Rings outward from `near` for the first spot whose whole grid stands on gently sloping dry ground. Each cell takes
        // its own ground (CellGround), so an even slope is fine; what fails is a steep plane or bumps off it. The first rule
        // (the grid's whole height range within SurfaceFlatness, i.e. under 1 degree over the ~100 m grid) found nothing
        // on hilly ATomTest (R69, 2026-09-29). `best` says how close the search came when nothing passes.
        // Spread over frames like FindCave: out to SurfaceMaxDistance a failing search samples ~650k heights. done(spot, why):
        // spot null = nothing passed, and `why` says how close the search came.
        internal static IEnumerator FindSurface(Vector3 near, int cases, Action<Vector3?, string> done)
        {
            var generator = WorldGenerator.instance;
            if (generator == null) { done(null, "no world generator"); yield break; }
            float span = Mathf.Max(GridColumns, Mathf.CeilToInt(cases / (float)GridColumns)) * SurfaceCellSpacing;
            float bestScore = float.MaxValue;
            string best = "no ground sampled";
            var clock = System.Diagnostics.Stopwatch.StartNew();
            // Flat candidates are then checked by the SERVER, which holds every ZDO and every location even where this
            // client has loaded nothing: no build piece within the arrival rule's radius, no placed location close by
            // (R72/R73: the find picked a spot under a tree, then one 26 pieces deep beside the mausoleum).
            var batch = new List<Vector3>();
            int rejectedPieces = 0, rejectedLocation = 0;
            bool serverChecked = false, unanswered = false;
            for (float ring = SurfaceMinDistance; ring <= SurfaceMaxDistance + SurfaceSearchStep; ring += SurfaceSearchStep)
            {
                bool lastRing = ring > SurfaceMaxDistance;
                int around = lastRing ? 0 : Mathf.Max(8, Mathf.CeilToInt(2f * Mathf.PI * ring / SurfaceSearchStep));
                for (int i = 0; i < around; i++)
                {
                    float angle = i * 2f * Mathf.PI / around;
                    var centre = new Vector3(near.x + Mathf.Sin(angle) * ring, 0f, near.z + Mathf.Cos(angle) * ring);
                    if (FlatAndDry(generator, centre, span, out float height, out float slope, out float bumps, out bool dry))
                    {
                        centre.y = height;
                        if (!batch.Exists(c => (c - centre).sqrMagnitude < CandidateSpacing * CandidateSpacing)) batch.Add(centre);
                        if (batch.Count < CandidateBatch) continue;
                    }
                    float score = (dry ? 0f : 1000f) + slope / SurfaceMaxSlopeDegrees + bumps / SurfaceFlatness;
                    if (score < bestScore)
                    {
                        bestScore = score;
                        best = string.Format(CultureInfo.InvariantCulture, "best at ({0:0}, {1:0}): slope {2:0.0} deg (max {3:0}), bumps {4:0.0} m (max {5:0.0}){6}",
                            centre.x, centre.z, slope, SurfaceMaxSlopeDegrees, bumps, SurfaceFlatness, dry ? "" : ", under the sea margin");
                    }
                    if (batch.Count < CandidateBatch)
                    {
                        if (clock.ElapsedMilliseconds < FrameBudgetMs) continue;
                        yield return null;
                        clock.Restart();
                        continue;
                    }
                    // A full batch: ask the server about all of them in one round trip.
                    Vector3? pick = null;
                    yield return CheckWithServer(batch, (found, pieces, location, asked) =>
                    {
                        pick = found; rejectedPieces += pieces; rejectedLocation += location; serverChecked |= asked; unanswered = !asked;
                    });
                    batch.Clear();
                    if (unanswered) { done(null, Unanswered); yield break; }
                    if (pick.HasValue) { done(pick, null); yield break; }
                    clock.Restart();
                }
                if (lastRing && batch.Count > 0)
                {
                    Vector3? pick = null;
                    yield return CheckWithServer(batch, (found, pieces, location, asked) =>
                    {
                        pick = found; rejectedPieces += pieces; rejectedLocation += location; serverChecked |= asked; unanswered = !asked;
                    });
                    batch.Clear();
                    if (unanswered) { done(null, Unanswered); yield break; }
                    if (pick.HasValue) { done(pick, null); yield break; }
                }
            }
            string rejected = rejectedPieces + rejectedLocation > 0
                ? string.Format(CultureInfo.InvariantCulture, "; {0} flat place(s) had build pieces within {1:0} m and {2} a location within {3:0} m (server-checked)",
                    rejectedPieces, AdminToolsProbe.PieceNearMetres, rejectedLocation, LocationClearMetres)
                : serverChecked ? "" : "; the server did not answer the piece/location check";
            done(null, string.Format(CultureInfo.InvariantCulture, "{0:0}x{0:0} m grid, rings {1:0}-{2:0} m; {3}{4}", span, SurfaceMinDistance, SurfaceMaxDistance, best, rejected));
        }

        private static readonly string Unanswered = string.Format(CultureInfo.InvariantCulture,
            "server probe unanswered: the server did not answer the build-piece/location check within {0:0} s, so no place was picked",
            AdminToolsProbe.AnswerSeconds);
        private const int CandidateBatch = 16;
        private const float CandidateSpacing = 60f;
        private const float LocationClearMetres = 100f;

        // done(the first candidate with no build piece near and no location close, pieces-rejected, location-rejected, the
        // server answered). A peer that holds no world data answers NaN, and only the server's reading counts. Read-only on
        // the server (ZDO and location lookups), CandidateBatch points per round trip, AnswerSeconds to answer. Without a
        // server answer the find fails ("server probe unanswered"), never picking an unchecked place.
        private static IEnumerator CheckWithServer(List<Vector3> candidates, Action<Vector3?, int, int, bool> done)
        {
            var points = new List<ProbePoint>(candidates.Count * 2);
            foreach (var c in candidates)
            {
                points.Add(new ProbePoint(ProbeKind.PiecesNear, c));
                points.Add(new ProbePoint(ProbeKind.LocationNear, c));
            }
            float[] values = null;
            if (ZNet.instance != null && ZNet.instance.IsServer()) values = AdminToolsProbe.ReadLocal(points);
            else
            {
                List<PeerProbe> peers = null;
                yield return AdminToolsProbe.AskAll(points, answers => peers = answers);
                var server = peers?.Find(p => p.Role == "server" && p.Values != null && p.Values.Length == points.Count);
                values = server?.Values;
            }
            // No server answer: never fall back to an unchecked place (the lead's rule); the find stops and says so.
            if (values == null) { done(null, 0, 0, false); yield break; }
            int pieces = 0, location = 0;
            for (int i = 0; i < candidates.Count; i++)
            {
                float p = values[i * 2], l = values[i * 2 + 1];
                if (float.IsNaN(p) || float.IsNaN(l)) continue;
                if (p > 0f) { pieces++; continue; }
                if (l < LocationClearMetres) { location++; continue; }
                done(candidates[i], pieces, location, true);
                yield break;
            }
            done(null, pieces, location, true);
        }

        // A least-squares plane through the grid's samples: its tilt is the slope, the largest distance of a sample from it
        // the bumps.
        private static bool FlatAndDry(WorldGenerator generator, Vector3 centre, float span, out float mean, out float slope,
                                       out float bumps, out bool dry)
        {
            int n = FlatnessSamples * FlatnessSamples;
            var xs = new float[n];
            var zs = new float[n];
            var hs = new float[n];
            float min = float.MaxValue, sum = 0f;
            for (int ix = 0, k = 0; ix < FlatnessSamples; ix++)
                for (int iz = 0; iz < FlatnessSamples; iz++, k++)
                {
                    xs[k] = (ix / (float)(FlatnessSamples - 1) - 0.5f) * span;
                    zs[k] = (iz / (float)(FlatnessSamples - 1) - 0.5f) * span;
                    hs[k] = generator.GetHeight(centre.x + xs[k], centre.z + zs[k]);
                    min = Mathf.Min(min, hs[k]);
                    sum += hs[k];
                }
            mean = sum / n;
            // The sample offsets are symmetric about the centre, so the fit separates: gx = sum(x*h)/sum(x*x), same for z.
            float sxh = 0f, sxx = 0f, szh = 0f, szz = 0f;
            for (int k = 0; k < n; k++)
            {
                sxh += xs[k] * (hs[k] - mean);
                sxx += xs[k] * xs[k];
                szh += zs[k] * (hs[k] - mean);
                szz += zs[k] * zs[k];
            }
            float gx = sxx > 0f ? sxh / sxx : 0f, gz = szz > 0f ? szh / szz : 0f;
            slope = Mathf.Atan(Mathf.Sqrt(gx * gx + gz * gz)) * Mathf.Rad2Deg;
            bumps = 0f;
            for (int k = 0; k < n; k++) bumps = Mathf.Max(bumps, Mathf.Abs(hs[k] - (mean + gx * xs[k] + gz * zs[k])));
            dry = min >= ZoneSystem.c_WaterLevel + DryAboveSea;
            return dry && slope <= SurfaceMaxSlopeDegrees && bumps <= SurfaceFlatness;
        }

        internal static bool TryFindIsland(Vector3 near, out Vector3 spot) => FiresSkyIslands.TryNearestTop(near, out spot, out _);

        // CAVE: the generator's own tunnel field (VoxelCaveNoise.CarveSdf, > 0 inside a tunnel, the same call chunk
        // generation makes), searched column by column in rings from `near` for a floor with headroom under enough rock.
        // Slow enough to spread over frames.
        internal const float CaveCellSpacing = 5f;
        private const float CaveHeadroom = 6f;
        private const float CaveMinCover = 4f;
        private const float CaveSearchMin = 60f;
        private const float CaveSearchMax = 1500f;
        private const float CaveSearchStep = 8f;
        private const float CaveScanStep = 0.5f;
        private const float CaveCellSearchRadius = 30f;
        private const float CaveCellReach = 8f;
        private const long FrameBudgetMs = 8;

        // A cave floor only counts when the run's own cell rule (CaveFloorSpots) fits every case around it: R75 and R76's finds
        // passed spots with room for 2 and 3 cells, and the runs SKIPped 16 and 15 cases on "no room". Floors closer than
        // CaveTrySpacing to one already tried are the same tunnel and are skipped; past CaveTriesMax the roomiest one found
        // so far is not returned (a place the run can't use is a FAIL here, not 15 SKIPs later), only named in `why`.
        private const float CaveTrySpacing = 60f;
        private const int CaveTriesMax = 60;

        internal static IEnumerator FindCave(Vector3 near, int cases, Action<Vector3?, string> done)
        {
            var generator = WorldGenerator.instance;
            if (generator == null || !FiresVoxelConfig.GeneratesCaves) { done(null, "this world generates no caves"); yield break; }
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var tried = new List<Vector3>();
            Vector3 roomiest = Vector3.zero;
            int roomiestCells = 0;
            for (float ring = CaveSearchMin; ring <= CaveSearchMax && tried.Count < CaveTriesMax; ring += CaveSearchStep)
            {
                int around = Mathf.Max(8, Mathf.CeilToInt(2f * Mathf.PI * ring / CaveSearchStep));
                for (int i = 0; i < around && tried.Count < CaveTriesMax; i++)
                {
                    float angle = i * 2f * Mathf.PI / around;
                    float x = near.x + Mathf.Sin(angle) * ring, z = near.z + Mathf.Cos(angle) * ring;
                    float ground = generator.GetHeight(x, z);
                    if (ground >= ZoneSystem.c_WaterLevel + DryAboveSea
                        && tried.TrueForAll(t => (t.x - x) * (t.x - x) + (t.z - z) * (t.z - z) >= CaveTrySpacing * CaveTrySpacing)
                        && CaveFloorBelow(x, z, ground, ground - CaveMinCover, FiresVoxelConfig.CaveMinY.Value, out float floor))
                    {
                        var spot = new Vector3(x, floor, z);
                        tried.Add(spot);
                        int cells = CaveFloorSpots(spot, cases).Count;
                        if (cells >= cases) { done(spot, null); yield break; }
                        if (cells > roomiestCells) { roomiest = spot; roomiestCells = cells; }
                        yield return null;
                        clock.Restart();
                        continue;
                    }
                    if (clock.ElapsedMilliseconds < FrameBudgetMs) continue;
                    yield return null;
                    clock.Restart();
                }
            }
            done(null, tried.Count == 0 ? $"no cave floor within {CaveSearchMax:0} m"
                : string.Format(CultureInfo.InvariantCulture, "{0} cave floor(s) tried, none with room for {1} cells {2:0} m apart; roomiest {3} cell(s) at ({4:0}, {5:0})",
                    tried.Count, cases, CaveCellSpacing, roomiestCells, roomiest.x, roomiest.z));
        }

        // Scanning down from `top`, the first floor under a carved run at least CaveHeadroom tall.
        private static bool CaveFloorBelow(float x, float z, float ground, float top, float bottom, out float floor)
        {
            floor = float.NaN;
            float carvedRun = 0f;
            for (float y = top; y >= bottom; y -= CaveScanStep)
            {
                bool carved = VoxelCaveNoise.CarveSdf(x, y, z, ground - y) > 0f;
                if (carved) { carvedRun += CaveScanStep; continue; }
                if (carvedRun >= CaveHeadroom) { floor = y + CaveScanStep; return true; }
                carvedRun = 0f;
            }
            return false;
        }

        // Where a place's cases can go: a SURFACE grid; the ISLAND grid cells that stand on the island's top; cave floor
        // spots with headroom near the CAVE spot, CaveCellSpacing apart. Each keeps the tester's own ground reading as y.
        internal static List<Vector3> Candidates(PlaceKind kind, Vector3 centre, int count)
        {
            if (kind == PlaceKind.Cave) return CaveFloorSpots(centre, count);
            float spacing = kind == PlaceKind.Surface ? SurfaceCellSpacing : IslandCellSpacing;
            int rows = Mathf.CeilToInt(count / (float)GridColumns);
            var origin = centre - new Vector3((GridColumns - 1) * spacing * 0.5f, 0f, (rows - 1) * spacing * 0.5f);
            var cells = new List<Vector3>(count);
            for (int i = 0; i < count; i++) cells.Add(origin + new Vector3(i % GridColumns * spacing, 0f, i / GridColumns * spacing));
            return cells;
        }

        private static List<Vector3> CaveFloorSpots(Vector3 centre, int count)
        {
            var spots = new List<Vector3>();
            var generator = WorldGenerator.instance;
            if (generator == null) return spots;
            for (float r = 0f; r <= CaveCellSearchRadius && spots.Count < count; r += CaveCellSpacing)
            {
                int around = r <= 0f ? 1 : Mathf.CeilToInt(2f * Mathf.PI * r / CaveCellSpacing);
                for (int i = 0; i < around && spots.Count < count; i++)
                {
                    float angle = i * 2f * Mathf.PI / around;
                    float x = centre.x + Mathf.Sin(angle) * r, z = centre.z + Mathf.Cos(angle) * r;
                    float ground = generator.GetHeight(x, z);
                    if (!CaveFloorBelow(x, z, ground, centre.y + CaveHeadroom + CaveCellReach, centre.y - CaveCellReach, out float floor)) continue;
                    var spot = new Vector3(x, floor, z);
                    if (spots.TrueForAll(s => (s - spot).sqrMagnitude >= CaveCellSpacing * CaveCellSpacing)) spots.Add(spot);
                }
            }
            return spots;
        }

        // A candidate cell's ground as this peer reads it, or NaN when the cell can't hold a case: no voxel surface near
        // the place's height, or (ISLAND) not far above the ground below.
        internal static float CellGround(PlaceKind kind, Vector3 cell, float placeY)
        {
            float nearY = kind == PlaceKind.Cave ? cell.y : placeY;
            bool voxel = VoxelWorld.TryFindSurfaceY(cell.x, cell.z, nearY, VoxelReach(kind), out float surface);
            // No heightmap here yet = not ready (NaN), never the place's own y: that fallback let R74's surface cases start
            // on ground this client had not loaded.
            if (kind == PlaceKind.Surface) return voxel ? surface : Heightmap.GetHeight(cell, out float height) ? height : float.NaN;
            if (!voxel) return float.NaN;
            if (kind == PlaceKind.Island)
            {
                float below = WorldGenerator.instance != null ? WorldGenerator.instance.GetHeight(cell.x, cell.z) : surface;
                return surface - below >= IslandDropMinMetres ? surface : float.NaN;
            }
            return surface;
        }

        private static float VoxelReach(PlaceKind kind) => kind == PlaceKind.Cave ? CaveCellReach : kind == PlaceKind.Island ? CaveHeadroom : SurfaceFlatness * 8f;

        // Null when the tester stands where the place says; otherwise why not.
        internal static string CheckArrival(PlaceKind kind, Vector3 at)
        {
            var head = at + Vector3.up * HeadHeight;
            // Ground only (voxel chunks, and so cave roofs, overhangs and islands, sit on the terrain layer): a tree's canopy
            // is open sky for this test (R72: every surface case SKIPped on "something 9 m overhead" under a tree).
            bool roof = Physics.Raycast(head, Vector3.up, out RaycastHit above, SkyCheckMetres, LayerMask.GetMask("terrain"));
            switch (kind)
            {
                case PlaceKind.Surface:
                    if (roof) return $"something {above.distance:0} m overhead: not open sky";
                    var pieces = new List<Piece>();
                    Piece.GetAllPiecesInRadius(at, PieceClearMetres, pieces);
                    // Player-placed only, as the server's find probe counts them: some mod gives trees and rocks a Piece component
                    // on the client (R76: "95 build piece(s) … top: FirTree_small 27, Rock_4 18, FirTree 12").
                    pieces.RemoveAll(p => p == null || !p.IsPlacedByPlayer());
                    return pieces.Count > 0 ? $"{pieces.Count} build piece(s) within {PieceClearMetres:0} m (top: {TopPrefabs(pieces, 3)})" : null;
                case PlaceKind.Cave:
                    return roof && above.distance <= CaveRoofMaxMetres ? null : "no cave roof overhead";
                case PlaceKind.Island:
                    float below = WorldGenerator.instance != null ? WorldGenerator.instance.GetHeight(at.x, at.z) : at.y;
                    return at.y - below >= IslandDropMinMetres ? null : $"only {at.y - below:0} m above the ground below: not on an island";
            }
            return null;
        }

        // "name count, name count, …" for the most common prefabs among the pieces (R75 SKIPped 26 cases on a bare count).
        private static string TopPrefabs(List<Piece> pieces, int top)
        {
            var counts = new Dictionary<string, int>();
            foreach (var piece in pieces)
            {
                string name = piece != null ? Utils.GetPrefabName(piece.gameObject) : "?";
                counts[name] = counts.TryGetValue(name, out int n) ? n + 1 : 1;
            }
            var sorted = new List<KeyValuePair<string, int>>(counts);
            sorted.Sort((a, b) => b.Value.CompareTo(a.Value));
            var parts = new List<string>();
            for (int i = 0; i < sorted.Count && i < top; i++) parts.Add($"{sorted[i].Key} {sorted[i].Value}");
            if (sorted.Count > top) parts.Add($"+{sorted.Count - top} more kind(s)");
            return string.Join(", ", parts);
        }
    }
}
