using System.Collections.Generic;
using UnityEngine;
using VerdantsAscent.Terrain.Voxel;
using VerdantsAscent.Water.Core;

namespace VerdantsAscent.Water.Tools
{
    // Vpond (0.2.745; Fire 10-05: "fill/pond was meant to be a radius flat circle we could adjust the fill height for with page
    // up and down like you nudge infinity hammer up and down and draw a water mesh in a depression, not a sphere"). A flat circle
    // of the brush radius stands at the water height: the ground under the crosshair plus an offset PgUp/PgDn nudge (Shift: fine
    // steps). A click fills the depression under the circle with water flat at that height: one water disk, synced and saved like
    // every drawn water (CustomWaterManager -> WaterOperationRPC -> the server). The ground is read from the voxel data
    // (VoxelWorld.TrySampleSurfaceY: the highest floor UNDER the height), the same on a client and on the server, so caves (the
    // roof is above, not a floor), skylands and plain voxel ground all work.
    // Where the water would leave the circle:
    //   * over a LOW RIM (ground a little under the height): the preview is red with a post there and the click is refused;
    //   * over a CLIFF or a skyland's edge (0.2.746; Fire 10-05: "placing a pond/river disk over the edge of a cliff or skyland drop
    //     should cause it to turn the overhanging portion into a waterfall"): the preview marks it with a white post, the pond is
    //     laid up to the edge and a Vfall (ToolFalls) hangs from the lip there.
    // 0.2.746 also: the flat water-disc preview for Vpool / Vpour / Vriver (Fire: "v pool should be a flat disk, and it should be
    // water not terrain as the preview" - they drew the terrain-shaded sphere), the ground-only cursor the water tools aim with
    // (the shared crosshair ray hits water too, so a Vpour drag over its own water climbed by the offset every dab), and a Vpour
    // drag that runs over an edge hanging a fall there.
    internal static class PondTool
    {
        internal const float OffsetStep = 0.5f, OffsetFineStep = 0.1f, OffsetLimit = 16f;
        private const float DeepestReach = 32f;        // how far under the height a floor is looked for
        private const float MinDepth = 0.3f;           // shallower than this everywhere = no depression under the circle
        private const float RimSlack = 0.02f;          // ground this close under the height still holds the water
        private const float CliffDrop = 2f;            // ground this far under the height at the rim (or none) = an edge: a fall
        private const int RimSamples = 64;
        private const float FillStep = 0.5f;           // the click's floor survey grid
        private const int PreviewGridAcross = 12;      // the preview's coarse floor survey (per diameter)
        private const float PreviewSeconds = 0.2f, PreviewMove = 0.1f;
        private const float MinPondRadius = 1f, EdgeInset = 0.25f, FallWidthMin = 1f, FallWidthMax = 4f;
        private const float StrokeGapSeconds = 0.6f;   // no pour dab for this long = the next dab starts a new stroke

        internal static float Offset = 1f;             // the water height over the ground under the crosshair

        internal struct Survey
        {
            internal float Level, Radius, Floor;       // Floor: the lowest ground under the circle (NaN: none)
            internal int LowRims, Cliffs, NoGround;
            internal float LowestRim, LowestRimBearing;
            internal float LowestLowRim;               // the lowest low-rim ground (MaxValue: none): where the water spills
            internal bool[] CliffAt;                   // per rim sample
            internal bool Ok => LowRims == 0 && Cliffs < RimSamples && !float.IsNaN(Floor) && Level - Floor >= MinDepth;
        }

        // The circle at (centre.x, level, centre.z): its lowest floor inside, every rim point the water would run out at (low rims
        // in `lowRims`, cliff edges in `cliffs`, both may be null).
        internal static Survey Look(Vector3 centre, float level, float radius, float gridStep, List<Vector3> lowRims, List<Vector3> cliffs)
        {
            var s = new Survey { Level = level, Radius = radius, Floor = float.NaN, LowestRim = float.MaxValue, LowestLowRim = float.MaxValue, CliffAt = new bool[RimSamples] };
            lowRims?.Clear();
            cliffs?.Clear();
            for (int i = 0; i < RimSamples; i++)
            {
                float a = i * 2f * Mathf.PI / RimSamples;
                float x = centre.x + Mathf.Sin(a) * radius, z = centre.z + Mathf.Cos(a) * radius;
                bool found = VoxelWorld.TrySampleSurfaceY(x, z, level + 0.05f, level - DeepestReach, out float g, out bool wall);
                if (wall) continue;                                        // ground stands over the height here: it holds
                if (found && g < s.LowestRim) { s.LowestRim = g; s.LowestRimBearing = a * Mathf.Rad2Deg; }
                if (found && g >= level - RimSlack) continue;
                if (!found || g < level - CliffDrop)
                {
                    s.Cliffs++;
                    s.CliffAt[i] = true;
                    if (!found) s.NoGround++;
                    cliffs?.Add(new Vector3(x, found ? g : level - CliffDrop, z));
                }
                else
                {
                    s.LowRims++;
                    if (g < s.LowestLowRim) s.LowestLowRim = g;
                    lowRims?.Add(new Vector3(x, g, z));
                }
            }
            for (float dx = -radius; dx <= radius; dx += gridStep)
                for (float dz = -radius; dz <= radius; dz += gridStep)
                {
                    if (dx * dx + dz * dz > radius * radius) continue;
                    if (!VoxelWorld.TrySampleSurfaceY(centre.x + dx, centre.z + dz, level + 0.05f, level - DeepestReach, out float g, out bool wall) || wall) continue;
                    if (g < level - CliffDrop - DeepestReach * 0.5f) continue;   // a floor far down past an edge is not this pond's bed
                    if (float.IsNaN(s.Floor) || g < s.Floor) s.Floor = g;
                }
            return s;
        }

        // 0.2.771 (Fire's final session: "v pond just doesn't work at all" - every click on a slope was refused over a low rim):
        // the height the circle holds. Over a low rim the water drops to that rim, where it would spill, instead of refusing; a
        // cliff edge is not a rim and still becomes a fall.
        internal static float SpillLevel(Vector3 centre, float level, float radius)
        {
            var s = Look(centre, level, radius, radius * 2f + 1f, null, null);
            return s.LowRims > 0 && s.LowestLowRim < level ? s.LowestLowRim : level;
        }

        // The click. Returns the Debug line ("[FiresWater] pond refused ..." when nothing was laid).
        internal static string Fill(Vector3 centre, float level, float radius)
        {
            if (float.IsNaN(level) || float.IsInfinity(level) || radius < MinPondRadius) return F("[FiresWater] pond refused: a bad height or radius ({0:0.00}, r {1:0.0})", level, radius);
            float asked = level;
            level = SpillLevel(centre, level, radius);
            var lowRims = new List<Vector3>();
            var s = Look(centre, level, radius, FillStep, lowRims, null);
            if (float.IsNaN(s.Floor))
                return F("[FiresWater] pond refused at ({0:0.0},{1:0.0}): no voxel ground under the circle within {2:0} m of the height {3:0.00}", centre.x, centre.z, DeepestReach, level);
            if (s.LowRims > 0)
                return F("[FiresWater] pond refused at ({0:0.0},{1:0.0}): water at {2:0.00} would spill over a low rim at {3} of {4} point(s), the lowest {5:0.00} (bearing {6:0}); lower the height or shrink the circle",
                    centre.x, centre.z, level, s.LowRims, RimSamples, s.LowestRim, s.LowestRimBearing);
            if (s.Cliffs >= RimSamples)
                return F("[FiresWater] pond refused at ({0:0.0},{1:0.0}): the circle at {2:0.00} has an edge all round - nothing holds the water", centre.x, centre.z, level);
            if (level - s.Floor < MinDepth)
                return F("[FiresWater] pond refused at ({0:0.0},{1:0.0}): the ground under the circle is at most {2:0.00} m under the height {3:0.00} - no depression to fill",
                    centre.x, centre.z, level - s.Floor, level);

            // Edges: each run of cliff rim points is one fall; the pond stops just inside the nearest lip.
            var arcs = CliffArcs(s.CliffAt);
            float pondRadius = radius;
            var lips = new List<(Vector3 dir, float lip, float width)>();
            foreach (var arc in arcs)
            {
                float mid = (arc.start + arc.length * 0.5f) * 2f * Mathf.PI / RimSamples;
                var dir = new Vector3(Mathf.Sin(mid), 0f, Mathf.Cos(mid));
                float lip = LipDistance(centre, dir, level, radius);
                float width = Mathf.Clamp(arc.length * 2f * Mathf.PI * radius / RimSamples * 0.5f, FallWidthMin, FallWidthMax);
                lips.Add((dir, lip, width));
                pondRadius = Mathf.Min(pondRadius, lip + EdgeInset);
            }
            if (pondRadius < MinPondRadius)
                return F("[FiresWater] pond refused at ({0:0.0},{1:0.0}): an edge is only {2:0.0} m from the centre - move the circle in", centre.x, centre.z, pondRadius - EdgeInset);

            var at = new Vector3(centre.x, level, centre.z);
            float depth = level - s.Floor + 0.5f;
            WaterDisk disk = null;
            try { disk = CustomWaterManager.CreateDiskOfType(at, pondRadius, depth, CustomLiquidType.Water); }
            catch (System.Exception ex) { return F("[FiresWater] pond refused: the water disk threw ({0})", ex.Message); }
            if (disk == null) return F("[FiresWater] pond refused: no water disk was made at ({0:0.0},{1:0.00},{2:0.0})", at.x, at.y, at.z);

            var falls = new List<string>();
            foreach (var l in lips)
            {
                Vector3 inside = centre + l.dir * Mathf.Max(0f, l.lip - 1f);
                inside.y = VoxelWorld.TrySampleSurfaceY(inside.x, inside.z, level + 0.05f, level - DeepestReach, out float g, out _) ? g : level;
                falls.Add(ToolFalls.Place(inside, l.dir, l.width * 0.5f));
            }
            // 0.2.749: a pond is a link in the two-click river too (Fire: "place two ponds and link them and a river carve and fill
            // auto connects them"): with a pond or pool clicked before, the routed channel is carved and filled from it to here.
            WaterShapingTools.Chain(at, pondRadius, F("pond filled at ({0:0.0},{1:0.00},{2:0.0}) r={3:0.0}", at.x, at.y, at.z, pondRadius));
            return F("[FiresWater] pond filled at ({0:0.0},{1:0.0}): water at {2:0.00}" + (level < asked - 0.005f ? F(" (lowered from {0:0.00} to the rim)", asked) : "") + ", r {3:0.0} m{4}, deepest {5:0.00} m (floor {6:0.00}){7}",
                centre.x, centre.z, level, pondRadius, pondRadius < radius ? F(" (trimmed from {0:0.0} to the edge)", radius) : "", level - s.Floor, s.Floor,
                falls.Count == 0 ? ", the rim holds all round" : F("; {0} edge(s) -> {1}", falls.Count, string.Join(" | ", falls.ToArray())));
        }

        // 0.2.749: the falls for a circle that is already dug and filled by another tool (a Vpool pool): each run of edge points
        // on its rim (ground CliffDrop+ under the height, or none) hangs a Vfall from its lip. Null when the rim has no edge.
        internal static string EdgeFalls(Vector3 centre, float level, float radius)
        {
            var s = Look(centre, level, radius, radius * 2f + 1f, null, null);
            if (s.Cliffs == 0 || s.Cliffs >= RimSamples) return null;
            var falls = new List<string>();
            foreach (var arc in CliffArcs(s.CliffAt))
            {
                float mid = (arc.start + arc.length * 0.5f) * 2f * Mathf.PI / RimSamples;
                var dir = new Vector3(Mathf.Sin(mid), 0f, Mathf.Cos(mid));
                float lip = LipDistance(centre, dir, level, radius);
                float width = Mathf.Clamp(arc.length * 2f * Mathf.PI * radius / RimSamples * 0.5f, FallWidthMin, FallWidthMax);
                Vector3 inside = centre + dir * Mathf.Max(0f, lip - 1f);
                inside.y = VoxelWorld.TrySampleSurfaceY(inside.x, inside.z, level + 0.05f, level - DeepestReach, out float g, out _) ? g : level;
                falls.Add(ToolFalls.Place(inside, dir, width * 0.5f));
            }
            return F("{0} edge(s) -> {1}", falls.Count, string.Join(" | ", falls.ToArray()));
        }

        // Runs of consecutive cliff rim points (the run across index 0 joined).
        private static List<(int start, int length)> CliffArcs(bool[] cliff)
        {
            var arcs = new List<(int, int)>();
            int n = cliff.Length, first = -1;
            for (int i = 0; i < n; i++) if (!cliff[i]) { first = i; break; }
            if (first < 0) return arcs;
            int run = 0, start = 0;
            for (int k = 1; k <= n; k++)
            {
                int i = (first + k) % n;
                if (cliff[i]) { if (run == 0) start = i; run++; }
                else if (run > 0) { arcs.Add((start, run)); run = 0; }
            }
            if (run > 0) arcs.Add((start, run));
            return arcs;
        }

        // From the centre out along `dir`: the last distance whose ground still stands within CliffDrop of the height.
        private static float LipDistance(Vector3 centre, Vector3 dir, float level, float radius)
        {
            float lip = 0f;
            for (float d = FillStep; d <= radius; d += FillStep)
            {
                bool found = VoxelWorld.TrySampleSurfaceY(centre.x + dir.x * d, centre.z + dir.z * d, level + 0.05f, level - DeepestReach, out float g, out bool wall);
                if (!wall && (!found || g < level - CliffDrop)) break;
                lip = d;
            }
            return lip;
        }

        // ── the cursor the water tools aim with: the ground, never water ────────────────────────────────────────────────────
        private static int s_groundMask;

        internal static bool GroundCursor(out Vector3 hit)
        {
            hit = Vector3.zero;
            var player = Player.m_localPlayer;
            if (player == null) return false;
            var cam = GameCamera.instance;
            Vector3 origin = cam != null ? cam.transform.position : player.transform.position + Vector3.up * 1.7f;
            Vector3 dir = cam != null ? cam.transform.forward : player.transform.forward;
            if (s_groundMask == 0) s_groundMask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "terrain");
            if (!Physics.Raycast(origin, dir, out RaycastHit rh, 600f, s_groundMask, QueryTriggerInteraction.Ignore)) return false;
            hit = rh.point;
            return true;
        }

        // ── Vpour strokes: water at the ground + the offset; over an edge, one fall instead of water hanging in the air ─────────
        private static float s_lastPourDab = -100f;
        private static bool s_havePrev;
        private static Vector3 s_prevGround;
        private static float s_prevSurface;

        // 0.2.749: the stroke framework's Begin / End say where a stroke starts (the time gap stays as a fallback).
        internal static void PourStrokeBegin() => s_havePrev = false;

        internal static void PourStrokeDab(Vector3 ground, float radius)
        {
            if (Time.time - s_lastPourDab > StrokeGapSeconds) s_havePrev = false;   // a new stroke
            s_lastPourDab = Time.time;
            // The framework spaces dabs between crosshair hits with a straight-line height, so over an edge a dab's height is
            // somewhere in the air. Each dab after the first reads the real floor under it, straight down from just over the
            // last water surface: nothing within reach, or a floor CliffDrop+ under that surface, is an edge.
            if (s_havePrev)
            {
                if (s_groundMask == 0) s_groundMask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "terrain");
                var from = new Vector3(ground.x, s_prevSurface + 1f, ground.z);
                ground.y = Physics.Raycast(from, Vector3.down, out RaycastHit hit, 200f, s_groundMask, QueryTriggerInteraction.Ignore)
                    ? hit.point.y : s_prevSurface - 200f;
            }
            float surface = ground.y + WaterShapingTools.LevelOffset;
            if (s_havePrev && ground.y < s_prevSurface - CliffDrop)
            {
                var dir = new Vector3(ground.x - s_prevGround.x, 0f, ground.z - s_prevGround.z);
                if (dir.sqrMagnitude > 0.01f)
                    FiresLogger.LogDebug("[FiresWater] Vpour stroke ran over an edge: " + ToolFalls.Place(s_prevGround, dir.normalized, radius));
                s_prevGround = ground;
                s_prevSurface = surface;
                return;   // the dab over the edge itself lays no water; the stroke goes on from the foot
            }
            s_havePrev = true;
            s_prevGround = ground;
            s_prevSurface = surface;
            WaterShapingTools.PourDab(ground, radius);
        }

        // ── the previews: a flat ring and a thin water disc at the height ───────────────────────────────────────────────────
        private static GameObject s_root, s_disc;
        private static LineRenderer s_ring;
        private static readonly List<LineRenderer> s_posts = new List<LineRenderer>();
        private static readonly List<Vector3> s_previewLow = new List<Vector3>(), s_previewCliff = new List<Vector3>();
        private static Vector3 s_lastCentre = new Vector3(float.NaN, 0f, 0f);
        private static float s_lastLevel, s_lastRadius, s_nextLook, s_lastHeld;
        private static Survey s_last;
        private static string s_lastState;
        private static readonly Color Good = new Color(0.35f, 0.75f, 1f, 1f), Bad = new Color(1f, 0.25f, 0.2f, 1f);
        private static readonly Color Edge = new Color(0.95f, 0.95f, 1f, 1f), Dig = new Color(0.85f, 0.6f, 0.3f, 1f);

        // 0.2.749: the stroke framework hides every frame before a driver draws, so the readout is not reset here (it would
        // repeat every frame); it shows again on the next change.
        internal static void Hide()
        {
            if (s_root != null && s_root.activeSelf) s_root.SetActive(false);
        }

        // Vpool / Vpour (water at the height: blue disc) and Vriver (the bed it digs: a ring only, no water).
        internal static void PreviewDisc(Transform parent, Material lineMat, Vector3 centre, float level, float radius, bool water)
        {
            Draw(parent, lineMat, centre, level, radius, water ? Good : Dig, water);
            ShowPosts(lineMat, null, null);
            ShowHint(lineMat, false);
            s_lastState = null;
        }

        // Vpond's A -> B hint (0.2.749): the line from the pond or pool clicked last to the circle, so the run is seen before the
        // click (Vpool keeps VoxelBrushDrag's own hint ray).
        private static LineRenderer s_hint;

        private static void ShowHint(Material lineMat, bool want)
        {
            want &= WaterShapingTools.TryGetAnchor(out Vector3 from, out _);
            if (s_hint == null) { if (!want) return; s_hint = NewLine(s_root.transform, lineMat, 2, 0.08f); }
            if (s_hint.gameObject.activeSelf != want) s_hint.gameObject.SetActive(want);
            if (!want) return;
            s_hint.SetPosition(0, from - s_root.transform.position);
            s_hint.SetPosition(1, Vector3.zero);
            s_hint.startColor = Good; s_hint.endColor = Good;
        }

        // Vpond: the disc at the height, red posts at low rims, white posts where an edge will take a fall.
        internal static void Preview(Transform parent, Material lineMat, Vector3 centre, float level, float radius, Player player)
        {
            bool moved = float.IsNaN(s_lastCentre.x) || (centre - s_lastCentre).sqrMagnitude > PreviewMove * PreviewMove
                || Mathf.Abs(level - s_lastLevel) > 0.01f || Mathf.Abs(radius - s_lastRadius) > 0.01f;
            if (moved || Time.time >= s_nextLook)
            {
                s_lastHeld = SpillLevel(centre, level, radius);
                s_last = Look(centre, s_lastHeld, radius, Mathf.Max(FillStep, radius * 2f / PreviewGridAcross), s_previewLow, s_previewCliff);
                s_lastCentre = centre; s_lastLevel = level; s_lastRadius = radius;
                s_nextLook = Time.time + PreviewSeconds;
            }
            level = s_lastHeld;   // 0.2.771: the disc stands at the height the click will lay (dropped to a low rim)
            Draw(parent, lineMat, centre, level, radius, s_last.Ok ? Good : Bad, true);
            ShowPosts(lineMat, s_previewLow, s_previewCliff);
            ShowHint(lineMat, true);

            string state = !s_last.Ok
                ? (float.IsNaN(s_last.Floor) ? F("Pond: no ground under the circle ({0:+0.0;-0.0;0} m)", Offset)
                   : s_last.LowRims > 0 ? F("Pond: spills over a low rim at {0} point(s) - lower it or shrink it ({1:+0.0;-0.0;0} m, radius {2:0.0} m)", s_last.LowRims, Offset, radius)
                   : s_last.Cliffs >= RimSamples ? "Pond: an edge all round - nothing holds the water"
                   : F("Pond: nothing to fill under the circle - raise it ({0:+0.0;-0.0;0} m)", Offset))
                : F("Pond: water at {0:0.0} ({1:+0.0;-0.0;0} m), radius {2:0.0} m, deepest {3:0.0} m{4}", level, Offset, radius, level - s_last.Floor,
                    s_last.Cliffs > 0 ? F(", {0} edge(s) become falls", CliffArcs(s_last.CliffAt).Count) : "");
            if (state != s_lastState && player != null)
            {
                s_lastState = state;
                player.Message(MessageHud.MessageType.TopLeft, state, 0, null, false);
            }
        }

        private static void Draw(Transform parent, Material lineMat, Vector3 centre, float level, float radius, Color ring, bool water)
        {
            if (s_root == null) Build(parent, lineMat);
            if (!s_root.activeSelf) s_root.SetActive(true);
            s_root.transform.position = new Vector3(centre.x, level, centre.z);
            for (int i = 0; i < s_ring.positionCount; i++)
            {
                float a = i * 2f * Mathf.PI / (s_ring.positionCount - 1);
                s_ring.SetPosition(i, new Vector3(Mathf.Sin(a) * radius, 0.02f, Mathf.Cos(a) * radius));
            }
            s_ring.startColor = ring; s_ring.endColor = ring;
            if (s_disc.activeSelf != water) s_disc.SetActive(water);
            if (!water) return;
            s_disc.transform.localScale = new Vector3(radius * 2f, 0.01f, radius * 2f);
            var mr = s_disc.GetComponent<MeshRenderer>();
            var mat = FiresLiquidWaterPrefab.GetSharedPourMaterial();
            if (mr != null && mat != null && mr.sharedMaterial != mat) mr.sharedMaterial = mat;
        }

        private static void ShowPosts(Material lineMat, List<Vector3> low, List<Vector3> cliff)
        {
            int nLow = low?.Count ?? 0, nCliff = cliff?.Count ?? 0, n = nLow + nCliff;
            while (s_posts.Count < n) s_posts.Add(NewLine(s_root.transform, lineMat, 2, 0.12f));
            for (int i = 0; i < s_posts.Count; i++)
            {
                bool on = i < n;
                if (s_posts[i].gameObject.activeSelf != on) s_posts[i].gameObject.SetActive(on);
                if (!on) continue;
                Vector3 p = (i < nLow ? low[i] : cliff[i - nLow]) - s_root.transform.position;
                s_posts[i].SetPosition(0, p);
                s_posts[i].SetPosition(1, new Vector3(p.x, 1.5f, p.z));
                Color c = i < nLow ? Bad : Edge;
                s_posts[i].startColor = c; s_posts[i].endColor = c;
            }
        }

        private static void Build(Transform parent, Material lineMat)
        {
            s_root = new GameObject("FiresPondPreview");
            if (parent != null) s_root.transform.SetParent(parent, false);
            else Object.DontDestroyOnLoad(s_root);   // drawn from a stroke driver, which has no transform
            s_ring = NewLine(s_root.transform, lineMat, 65, 0.1f);
            var prim = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            var mesh = Object.Instantiate(prim.GetComponent<MeshFilter>().sharedMesh);
            Object.Destroy(prim);
            s_disc = new GameObject("disc");
            s_disc.transform.SetParent(s_root.transform, false);
            s_disc.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = s_disc.AddComponent<MeshRenderer>();
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
        }

        private static LineRenderer NewLine(Transform parent, Material mat, int positions, float width)
        {
            var go = new GameObject("line");
            go.transform.SetParent(parent, false);
            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = false;
            lr.loop = false;
            lr.widthMultiplier = width;
            lr.material = mat;
            lr.textureMode = LineTextureMode.Tile;
            lr.positionCount = positions;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return lr;
        }

        private static string F(string format, params object[] args)
            => string.Format(System.Globalization.CultureInfo.InvariantCulture, format, args);
    }
}
