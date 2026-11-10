using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Terrain.Voxel;

namespace VerdantsAscent.Water.Tools
{
    // The water V-tools on the voxel stroke framework (VoxelBrushDrag.Strokes.cs). VoxelBrushDrag owns the
    // input (the click gate, the crosshair, the radius on scroll, one offset per tool on PgUp/PgDn, the stroke pacing); these
    // drivers say what each water tool shows and does:
    //   Vpour  drag a water ribbon at the ground under the crosshair + the offset (it rides that offset along the carve, so
    //          rivers are easy to fill in); over an edge, a fall
    //   Vriver drag a river bed, the offset its depth under the ground as it was before the stroke; or click a start,
    //          Ctrl+click spine points, click the end (0.2.1052 click mode, see River)
    //   Vpool  click: level, carve and fill a pool at the hit + the offset, or adopt the water clicked; LeftAlt fills the dip
    //          under the click; with a pool or pond clicked before, the routed river between them ("pond connector")
    //   Vpond  click: fill the dip under a flat circle at the ground + the offset (PondTool), edges become falls
    // Previews are flat: a water disc for the tools that lay water (water, not terrain, as the preview),
    // a ring for Vriver; drawn by PondTool from DrawExtras (the framework's ghost has no water shader).
    internal static class WaterToolDriver
    {
        private static bool s_registered;

        internal static void Register()
        {
            if (s_registered) return;
            s_registered = true;
            VToolDrivers.Register("Vpour", new Pour());
            VToolDrivers.Register("Vriver", new River());
            VToolDrivers.Register("Vpool", new Pool());
            VToolDrivers.Register("Vpond", new Pond());
            FiresLogger.LogDebug("[FiresWater] water V-tools on the stroke framework: Vpour, Vriver, Vpool, Vpond.");
        }

        // 0.2.1087: Vriver click-mode carves run as a budgeted job; VoxelBrushDrag.Update pumps it every frame.
        internal static void PumpRiver() => River.PumpRiver();
        internal static string StudioRiver(IList<Vector3> path, float radius, float depth) => River.StudioCarve(path, radius, depth);   // 0.2.1246
        internal static bool StudioRiverCarving => River.StudioCarving;
        internal static void ForgetCarveJob() => River.ForgetJob();   // 0.2.1354 (review: statics surviving logout)

        private static float WaterSpeed => FiresVoxelConfig.WaterDrawSpeed != null ? FiresVoxelConfig.WaterDrawSpeed.Value : 4f;

        private static Material Lines => VoxelBrushDrag.SharedLineMat();

        private static Ghost NoGhost => new Ghost { Shape = GhostShape.None };

        // A refusal says why in the middle of the screen as well as in the log.
        // The Lake tool's steps are shown to the player (top left) as well as logged: the chain is a sequence they follow.
        private static void Inform(string line)
        {
            FiresLogger.LogInfo(line);
            if (Player.m_localPlayer != null) Player.m_localPlayer.Message(MessageHud.MessageType.TopLeft, line.Replace("[FiresWater] ", ""), 0, null, false);
        }

        private static void Say(string line)
        {
            FiresLogger.LogDebug(line);
            int at = line.IndexOf(" refused", System.StringComparison.Ordinal);
            if (at >= 0 && Player.m_localPlayer != null)
                Player.m_localPlayer.Message(MessageHud.MessageType.Center, line.Substring(at + 1), 0, null, false);
        }

        // WATER (piece Vpour; Fire 10-24: "just lays the water in at the height selected and not effect terrain at all"):
        // click or drag. The level is set when the stroke starts (the ground under the crosshair + the height offset) and every
        // dab of the stroke lays water at that one level, so a drag over uneven ground makes one flat sheet, not steps. Ground
        // at or above the level stays dry. Nothing here carves, levels, paints or places falls.
        private sealed class Pour : IVToolDriver
        {
            private float _level;
            private bool _stroke;

            public float DrawSpeed => WaterSpeed;
            public bool ClickOnly => false;
            public float DefaultOffset => 0.5f;
            public float MinOffset => -8f;
            public string OffsetLabel => "water height";
            public Ghost Preview(in StrokeInput s) => NoGhost;
            public void DrawExtras(in StrokeInput s)
                => PondTool.PreviewDisc(null, Lines, s.HitGround, _stroke ? _level : s.HitGround.y + s.Offset, s.Radius, true);
            public void Begin(in StrokeInput s)
            {
                _level = s.HitGround.y + s.Offset;
                _stroke = true;
                // 0.2.1024 (Fire 10-24: "smooth them together if we are click dragging or laying ones next to eachother at slightly
                // different heights"): a stroke started beside water already laid, at a level within JoinRange of it, takes that
                // level, so the two join into one seamless sheet. A bigger difference is a step the player meant.
                string joined = "";
                if (TryNeighbourLevel(s.HitGround, s.Radius, _level, out float near))
                {
                    joined = $"; joined the water beside it at {near:F2} (was {_level:F2})";
                    _level = near;
                }
                FiresLogger.LogDebug($"[FiresWater] Water stroke at level {_level:F2} (ground {s.HitGround.y:F2} + {s.Offset:F2}){joined}; terrain untouched");
                Dab(s);
            }

            private const float JoinRange = 0.6f, JoinReach = 1.6f;

            private static bool TryNeighbourLevel(Vector3 at, float radius, float level, out float joinLevel)
            {
                joinLevel = level;
                float best = float.MaxValue;
                float reach = radius * JoinReach;
                for (int i = 0; i <= 8; i++)
                {
                    float x = at.x, z = at.z;
                    if (i > 0) { float a = (i - 1) * Mathf.PI / 4f; x += Mathf.Cos(a) * reach; z += Mathf.Sin(a) * reach; }
                    if (!VerdantsAscent.Water.Core.FatWaterChecks.TryWaterAt(x, z, 0.05f, false, out float surface, out _)) continue;
                    float diff = Mathf.Abs(surface - level);
                    if (diff <= JoinRange && diff < best) { best = diff; joinLevel = surface; }
                }
                return best < float.MaxValue;
            }
            public void Dab(in StrokeInput s) => WaterShapingTools.WaterLayerDab(s.HitGround, s.Radius, _level);
            public void End(in StrokeInput s) => _stroke = false;
        }

        // RIVER (Fire 10-24: "set the height offset and click and drag and it carves the riverbed and fills it over nice and
        // smooth"): the bed keeps its depth from the ground as it was before the stroke (not from the trench being dug), and the
        // water is laid RiverFreeboard under that original bank. Along one stroke the water level never rises (it follows the
        // lowest bank so far), so there is no jagged step. Shift+wheel / PgUp/PgDn = depth, wheel = width.
        //
        // 0.2.1052 CLICK MODE (Fire 10-26: "Trying to use the river tool to place a starting point, the spines, and the end point
        // doesn't seem to work very well"; the chain lived on the Lake tool behind LeftAlt, dug a lake at both ends and routed
        // each leg over the low ground). A press that ends without a drag is a CLICK:
        //   click            no chain: the start point; with a chain: the end point, and the river is carved
        //   Shift+click      a spine point (0.2.1053, Fire: "shift click spines"; it was Ctrl in 0.2.1052; never Alt)
        //   click on the last point (within the radius): finish there
        //   Numpad 7 / Backspace  drop the last point (the start too: the chain is gone); Numpad 9 puts it back (0.2.1053)
        //   Esc              cancel the chain
        // The chain is drawn until it is carved or cancelled: a line through the points to the crosshair, a post on each point.
        // The carve is straight segments through the points (RiverDabFill every half radius), the water stepping down along the
        // ground as found before the carve, never rising, exactly as a drag. Lakes at both ends only with [Water Tools] River chain lakes
        // (0.2.1053, off by default). On ground that is not voxel the cuts are vanilla heightmap cuts (WaterShapingTools.Cut). The whole carve is one undo step (it runs
        // in the click's End, inside the stroke framework's undo scope). A drag still carves as before.
        private sealed class River : IVToolDriver
        {
            private const float BaseDepth = 4.0f;   // 0.2.1073 (Fire: water ~1 m lower): about 3 m of water under a 1.0 m freeboard (bed 4 m under the original ground)
            private float _level = float.MaxValue;
            private StrokeInput _press;
            private bool _pressPending, _dragged;
            private readonly List<Vector3> _chain = new List<Vector3>();
            private readonly List<Vector3> _dropped = new List<Vector3>();   // points numpad 7 took back, for numpad 9
            // 0.2.1064 (Fire: "set the ruler radius at each spine point to guide the width of the river"): each point keeps the
            // wheel radius it was placed with; the carve eases the width between points.
            private readonly List<float> _radii = new List<float>(), _droppedRadii = new List<float>();
            private int _drawnFrame;
            private static River s_active;   // the River driver drawing this frame (its chain is what numpad 7 / 9 step)

            // 0.2.1053 (Fire 10-26: "Undo should be numpad 7. Redo numpad 9. Just like all of our other undo redo"): while a chain
            // is being placed, the one undo history's numpad 7 takes back the last point and numpad 9 puts it back; with no
            // chain they undo / redo the carves through the history as every other edit (Core's UndoHistory prefixes below).
            internal static bool UndoPoint()
            {
                var r = s_active;
                if (r == null || r._chain.Count == 0 || Time.frameCount - r._drawnFrame > 2) return false;
                r._dropped.Add(r._chain[r._chain.Count - 1]);
                r._droppedRadii.Add(r._radii[r._radii.Count - 1]); r._radii.RemoveAt(r._radii.Count - 1);
                r._chain.RemoveAt(r._chain.Count - 1);
                Tell(r._chain.Count == 0 ? "River chain cleared (numpad 9 puts the start back)" : $"River point removed ({r._chain.Count} left, numpad 9 puts it back)");
                FiresLogger.LogDebug($"[FiresWater] river chain: numpad 7 took back a point ({r._chain.Count} left)");
                return true;
            }

            internal static bool RedoPoint()
            {
                var r = s_active;
                if (r == null || r._dropped.Count == 0 || Time.frameCount - r._drawnFrame > 2) return false;
                r._chain.Add(r._dropped[r._dropped.Count - 1]);
                r._radii.Add(r._droppedRadii[r._droppedRadii.Count - 1]); r._droppedRadii.RemoveAt(r._droppedRadii.Count - 1);
                r._dropped.RemoveAt(r._dropped.Count - 1);
                Tell($"River point restored ({r._chain.Count} points)");
                FiresLogger.LogDebug($"[FiresWater] river chain: numpad 9 put a point back ({r._chain.Count} points)");
                return true;
            }

            public float DrawSpeed => WaterSpeed;
            public bool ClickOnly => false;
            public float DefaultOffset => BaseDepth;
            public float MinOffset => 0.5f;
            public string OffsetLabel => "depth";
            public Ghost Preview(in StrokeInput s) => NoGhost;
            public void DrawExtras(in StrokeInput s)
            {
                PondTool.PreviewDisc(null, Lines, s.HitGround, s.HitGround.y, s.Radius, false);
                s_active = this; _drawnFrame = Time.frameCount;
                ChainKeys();
                RiverChainView.Draw(Lines, _chain, _radii, s.HitGround, s.Radius);
            }

            // The press waits: a release with no drag is a chain click (End), a drag carves from the press point on (Dab).
            public void Begin(in StrokeInput s) { _press = s; _pressPending = true; _dragged = false; }
            public void Dab(in StrokeInput s)
            {
                if (s_job != null) { _pressPending = false; return; }   // 0.2.1087: a click-mode carve is still running
                if (_pressPending)
                {
                    _pressPending = false;
                    _dragged = true;
                    s_riverSurface.Clear(); _level = float.MaxValue;
                    _run = new WaterShapingTools.RiverRun(); _runLength = 0f; _runWidth = 0f;
                    WaterShapingTools.RiverFinishBegin(false);   // 0.2.1348
                    DragDab(_press);
                }
                DragDab(s);
            }
            public void End(in StrokeInput s)
            {
                bool click = _pressPending && !_dragged;
                _pressPending = false;
                s_riverSurface.Clear();
                if (_dragged && _run != null && _run.Dabs > 0)
                    FiresLogger.LogDebug(_run.Summary(_runLength, _runWidth) + " (drag)");
                if (_dragged) WaterShapingTools.RiverFinishEnd();   // 0.2.1348
                _run = null;
                if (click) ChainClick(_press);
            }

            // 0.2.1069 (river on slopes): a drag keeps the bed on one profile as it goes: each dab's bed is its ORIGINAL ground
            // - depth, never above the previous dab's, and the dab cuts and fills to it (WaterShapingTools.RiverStep).
            private WaterShapingTools.RiverRun _run;
            private float _runLength, _runWidth;

            private void DragDab(in StrokeInput s)
            {
                Vector3 at = s.HitGround;
                at.y = StrokeSurface(at, s.Radius);
                if (_run == null) _run = new WaterShapingTools.RiverRun();
                float d = WaterShapingTools.ClampRiverDepth(s.Offset);
                float bed = at.y - d;
                if (_run.Has)
                {
                    _runLength += new Vector2(at.x - _run.Prev.x, at.z - _run.Prev.z).magnitude;
                    bed = Mathf.Min(bed, _run.Prev.y);
                }
                _runWidth = s.Radius;
                WaterShapingTools.RiverStep(_run, at, s.Radius, bed, d);
            }

            private static bool ShiftHeld => VerdantsAscent.Terrain.FatInput.ShiftHeld;

            private void ChainClick(in StrokeInput s)
            {
                if (!s.HasGround) { Tell("River point refused: no ground under the crosshair"); return; }
                Vector3 p = s.HitGround;
                _dropped.Clear(); _droppedRadii.Clear();   // a new point ends numpad 9's way back
                if (_chain.Count == 0)
                {
                    _chain.Add(p);
                    _radii.Add(s.Radius);
                    Tell($"River start set. Shift+click = spine point, click = end point (carves), Numpad 7 / Backspace = undo point, Esc = cancel");
                    return;
                }
                Vector3 last = _chain[_chain.Count - 1];
                bool onLast = new Vector2(p.x - last.x, p.z - last.z).sqrMagnitude <= s.Radius * s.Radius;
                if (ShiftHeld && !onLast)
                {
                    _chain.Add(p);
                    _radii.Add(s.Radius);
                    Tell($"River spine point {_chain.Count - 1} set ({_chain.Count} points). Click = end point, Numpad 7 / Backspace = undo point");
                    return;
                }
                if (!onLast) { _chain.Add(p); _radii.Add(s.Radius); }
                if (_chain.Count < 2) { Tell("River needs a start and an end: click somewhere else for the end point"); return; }
                Carve(s.Offset);
            }

            private void ChainKeys()
            {
                if (_chain.Count == 0) return;
                if ((Chat.instance != null && Chat.instance.HasFocus()) || global::Console.IsVisible() || Menu.IsVisible() || TextInput.IsVisible()) return;
                if (Input.GetKeyDown(KeyCode.Backspace))
                {
                    _chain.RemoveAt(_chain.Count - 1);
                    _radii.RemoveAt(_radii.Count - 1);
                    Tell(_chain.Count == 0 ? "River chain cleared" : $"River point removed ({_chain.Count} left)");
                }
                else if (Input.GetKeyDown(KeyCode.Escape))
                {
                    _chain.Clear();
                    _radii.Clear();
                    Tell("River chain cancelled");
                }
            }

            // 0.2.1087 (FDT: "MAIN-THREAD STALL 1443 ms", 1,290 ms in VoxelBrushDrag.Update): a click-mode carve did the whole
            // river in the click's frame: pass 1 read the ground over the whole footprint (VoxelWorld.TryFindSurfaceY per cell)
            // and pass 2 ran RiverStep (a voxel cut/fill + a water dab) for every sample: 514 samples on a 267 m river, ~2.5 ms
            // each. The same two passes now run as a job pumped from VoxelBrushDrag.Update (PumpRiver) under a per-frame time
            // budget, in the same order and with the same inputs, so the river is identical. Pass 1 still finishes before the
            // first cut (the ground is read as it was), and every slice runs inside a player-edit scope (one undo step: the
            // stroke stays open while the job dabs each frame). While a carve runs, the River tool refuses a new carve or drag.
            private void Carve(float depth)
            {
                if (s_job != null) { Tell("River still carving: wait a moment"); return; }
                // The sample list (each sample carries the width eased between its two points' radii, spaced a quarter of it).
                s_riverSurface.Clear();
                var job = new CarveJob { Depth = depth, Chain = new List<Vector3>(_chain), Radii = new List<float>(_radii), RMin = float.MaxValue };
                for (int i = 1; i < _chain.Count; i++)
                {
                    Vector3 a = _chain[i - 1], b = _chain[i];
                    float ra = _radii[i - 1], rb = _radii[i];
                    float len = new Vector2(b.x - a.x, b.z - a.z).magnitude;
                    job.Length += len;
                    float step = Mathf.Max(Mathf.Min(ra, rb) * 0.25f, 0.5f);
                    int n = Mathf.Max(1, Mathf.CeilToInt(len / step));
                    for (int k = i == 1 ? 0 : 1; k <= n; k++)
                    {
                        float t = k / (float)n;
                        job.Samples.Add(Vector3.Lerp(a, b, t));
                        job.Widths.Add(Mathf.Lerp(ra, rb, t * t * (3f - 2f * t)));
                    }
                }
                job.Started = Time.realtimeSinceStartup;
                s_job = job;
                ClutterResetWatch.StrokeBegin();   // 0.2.1346
                WaterShapingTools.RiverFinishBegin(false);   // 0.2.1348
                Tell($"River carving: {_chain.Count} points, {job.Length:F0} m");
                _chain.Clear();
                _radii.Clear();
                _dropped.Clear();
                _droppedRadii.Clear();
                PumpRiver();   // the first slice this frame
            }

            // 0.2.1246 (World Studio water.place river): the click-mode carve for a given path, every point at the same radius
            // (half the Studio's width): the same samples, bed profile and budgeted job as Carve, pumped by VoxelBrushDrag.Update.
            internal static string StudioCarve(IList<Vector3> path, float radius, float depth)
            {
                if (s_job != null) return null;
                s_riverSurface.Clear();
                var job = new CarveJob { Depth = depth, Chain = new List<Vector3>(path), Radii = new List<float>(), RMin = float.MaxValue, Studio = true };
                for (int i = 0; i < path.Count; i++) job.Radii.Add(radius);
                for (int i = 1; i < path.Count; i++)
                {
                    Vector3 a = path[i - 1], b = path[i];
                    float len = new Vector2(b.x - a.x, b.z - a.z).magnitude;
                    job.Length += len;
                    float step = Mathf.Max(radius * 0.25f, 0.5f);
                    int n = Mathf.Max(1, Mathf.CeilToInt(len / step));
                    for (int k = i == 1 ? 0 : 1; k <= n; k++) { job.Samples.Add(Vector3.Lerp(a, b, k / (float)n)); job.Widths.Add(radius); }
                }
                job.Started = Time.realtimeSinceStartup;
                s_job = job;
                ClutterResetWatch.StrokeBegin();   // 0.2.1346
                WaterShapingTools.RiverFinishBegin(true);   // 0.2.1348
                PumpRiver();
                return $"river carving: {path.Count} points, {job.Length:F0} m, {job.Samples.Count} dabs";
            }
            internal static bool StudioCarving => s_job != null;
            // 0.2.1354 (review: statics surviving logout): a running carve would go on digging at the old coordinates in the next login.
            internal static void ForgetJob() { s_job = null; s_riverSurface.Clear(); }

            private sealed class CarveJob
            {
                internal float Depth, Length, RMin, RMax, D, Started;
                internal bool Studio;   // 0.2.1256: a Studio river carves the heightmap where the ground is not voxel yet
                internal List<Vector3> Chain, Samples = new List<Vector3>();
                internal List<float> Radii, Widths = new List<float>();
                internal float[] Beds;
                internal WaterShapingTools.RiverRun Run;
                internal readonly HashSet<Vector2s> Zones = new HashSet<Vector2s>();
                internal int Read, Stepped, Frames;
                internal int PoolHead, PoolTail;   // 0.2.1343: samples before PoolHead / from PoolTail on are the widened start / end pools
                internal double Ms;
            }

            private static CarveJob s_job;
            private const double CarveBudgetMs = 6.0;
            private const float PoolWiden = 1.25f;   // 0.2.1343

            // From VoxelBrushDrag.Update every frame: one slice of the running carve, inside the player-edit scope.
            internal static void PumpRiver()
            {
                var j = s_job;
                if (j == null) return;
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                long budget = (long)(CarveBudgetMs * System.Diagnostics.Stopwatch.Frequency / 1000.0);
                j.Frames++;
                VerdantsAscent.Terrain.FatUndo.BeginPlayerEdit("a Vriver stroke"); VoxelWorld.VToolScope++;
                WaterShapingTools.StudioHeightmap = j.Studio;
                try
                {
                    // Pass 1: every sample's ground as it is now, before any dab of this carve edits it.
                    while (j.Read < j.Samples.Count)
                    {
                        int i = j.Read++;
                        Vector3 at = j.Samples[i]; at.y = StrokeSurface(at, j.Widths[i]); j.Samples[i] = at;
                        j.RMin = Mathf.Min(j.RMin, j.Widths[i]); j.RMax = Mathf.Max(j.RMax, j.Widths[i]);
                        if (System.Diagnostics.Stopwatch.GetTimestamp() - t0 >= budget) return;
                    }
                    // Pass 2 (0.2.1069): ONE bed profile for the whole path from the ground as found in pass 1, never rising, eased;
                    // each sample cuts AND fills to it, waterfalls at cliffs, the water at the profile's level (RiverStep).
                    if (j.Beds == null)
                    {
                        j.D = WaterShapingTools.ClampRiverDepth(j.Depth);
                        j.Beds = WaterShapingTools.RiverBedProfile(j.Samples, j.D);
                        j.Run = new WaterShapingTools.RiverRun();
                        // 0.2.1343: a pool is an end of the chain wider than the channel (the chain's narrowest): at least the [Pond] end-pool
                        // width and PoolWiden x the channel. A uniform-width river (a Studio river, a wide chain) has none; a drag has no chain.
                        float poolAt = Mathf.Max(6f, j.RMin * PoolWiden);
                        while (j.PoolHead < j.Widths.Count && j.Widths[j.PoolHead] >= poolAt) j.PoolHead++;
                        j.PoolTail = j.Widths.Count;
                        while (j.PoolTail > j.PoolHead && j.Widths[j.PoolTail - 1] >= poolAt) j.PoolTail--;
                    }
                    while (j.Stepped < j.Samples.Count)
                    {
                        int i = j.Stepped++;
                        Vector3 at = j.Samples[i];
                        j.Run.NextIsPool = i < j.PoolHead || i >= j.PoolTail;   // 0.2.1343
                        WaterShapingTools.RiverStep(j.Run, at, j.Widths[i], j.Beds[i], j.D);
                        if (i % 24 == 0) WaterShapingTools.ScatterShore(at, j.Widths[i], j.Beds[i] + j.D - WaterShapingTools.RiverFreeboard);   // 0.2.1067: rocks / stones / stumps along the banks
                        j.Zones.Add(ZoneSystem.GetZone(at));
                        ClutterResetWatch.StrokeZone(ZoneSystem.GetZone(at));   // 0.2.1346
                        if (System.Diagnostics.Stopwatch.GetTimestamp() - t0 >= budget) return;
                    }
                    Finish(j);
                }
                catch (System.Exception ex)
                {
                    FiresLogger.LogWarning($"[FiresWater] river carve stopped at sample {j.Stepped}/{j.Samples.Count}: {ex.Message}");
                    s_job = null;
                    s_riverSurface.Clear();
                    WaterShapingTools.RiverFinishEnd();   // 0.2.1348: what landed still finishes
                }
                finally
                {
                    j.Ms += (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                    VoxelWorld.VToolScope--; VerdantsAscent.Terrain.FatUndo.EndPlayerEdit();
                    WaterShapingTools.StudioHeightmap = false;
                }
            }

            private static void Finish(CarveJob j)
            {
                s_job = null;
                var run = j.Run;
                float d = j.D;
                float first = run.FirstBed + d - WaterShapingTools.RiverFreeboard, level = run.LastBed + d - WaterShapingTools.RiverFreeboard;
                FiresLogger.LogDebug(run.Summary(j.Length, j.RMax));
                // 0.2.1053 (Fire 10-26: "Lakes on a toggle"): [Water Tools] River chain lakes digs a lake at both ends.
                bool lakes = VerdantsAscent.Water.Core.FiresLiquidConfig.RiverChainLakes != null && VerdantsAscent.Water.Core.FiresLiquidConfig.RiverChainLakes.Value;
                if (lakes)
                {
                    WaterShapingTools.LakeClick(j.Chain[0], j.Radii[0] * 2f, j.Depth);
                    WaterShapingTools.LakeClick(j.Chain[j.Chain.Count - 1], j.Radii[j.Radii.Count - 1] * 2f, j.Depth);
                }
                s_riverSurface.Clear();
                FiresLogger.LogDebug($"[FiresWater] river chain carved: {j.Chain.Count} points, {j.Length:F0} m, radius {j.RMin:F1}..{j.RMax:F1} (per point {string.Join("/", j.Radii.ConvertAll(r => r.ToString("F1")).ToArray())}), "
                    + $"level {first:F2}->{level:F2}, {run.Water}/{j.Samples.Count} water dabs, {j.Zones.Count} zones, depth {d:F1} m, path {(WaterShapingTools.OnVoxel(j.Chain[0]) ? "voxel" : "vanilla heightmap")}, lakes {(lakes ? "on" : "off")}; "
                    + $"{j.Ms:F0} ms of work over {j.Frames} frame(s) ({CarveBudgetMs:F0} ms budget), {Time.realtimeSinceStartup - j.Started:F1} s wall");
                WaterShapingTools.RiverFinishEnd();   // 0.2.1348: after the lakes, so their water counts too
                Tell($"River carved: {j.Chain.Count} points, {j.Length:F0} m");
            }

            private static void Tell(string line)
            {
                if (Player.m_localPlayer != null) Player.m_localPlayer.Message(MessageHud.MessageType.Center, line, 0, null, false);
            }
        }

        private sealed class Pool : IVToolDriver
        {
            public float DrawSpeed => WaterSpeed;
            public bool ClickOnly => true;
            public float DefaultOffset => 0f;
            public float MinOffset => -8f;
            public string OffsetLabel => "water level";
            public Ghost Preview(in StrokeInput s) => NoGhost;
            public void DrawExtras(in StrokeInput s) => PondTool.PreviewDisc(null, Lines, s.HitAny, s.HitAny.y + s.Offset, s.Radius, true);
            public void Begin(in StrokeInput s)
            {
                WaterShapingTools.LevelOffset = s.Offset;
                if (s.Alt) Say(WaterShapingTools.FillBowl(s.HitAny, s.Radius));   // LeftAlt: fill the dip under the click to its rim
                else WaterShapingTools.PoolClick(s.HitAny, s.Radius);             // HitAny: a click on a lake adopts the lake
            }
            public void Dab(in StrokeInput s) { }
            public void End(in StrokeInput s) { }
        }

        // LAKE (piece Vpond; Fire 10-24: "a lake/pond tool that auto carves out the hole for the lake and lays the mesh in at the
        // radius selected"): click once. Wheel = radius, Shift+wheel / PgUp/PgDn = depth. Carves a sloped basin and fills it.
        private sealed class Pond : IVToolDriver
        {
            public float DrawSpeed => WaterSpeed;
            public bool ClickOnly => true;
            public float DefaultOffset => 2.5f;
            public float MinOffset => 0.5f;
            public string OffsetLabel => "depth";
            public Ghost Preview(in StrokeInput s) => NoGhost;
            public void DrawExtras(in StrokeInput s)
                => PondTool.PreviewDisc(null, Lines, s.HitGround, s.HitGround.y - WaterShapingTools.LakeRimFreeboard, s.Radius, true);
            // LeftAlt+click = a river spine point; a click = a lake (the start of a chain, or its end when spine points were added).
            public void Begin(in StrokeInput s) => Inform(s.Alt ? WaterShapingTools.LakeChainSpine(s.HitGround) : WaterShapingTools.LakeChainClick(s.HitGround, s.Radius, s.Offset));
            public void Dab(in StrokeInput s) { }
            public void End(in StrokeInput s) { }
        }

        // ── Vriver's surface as the stroke found it: each 1 m cell is read once, before a dab edits it ───────────────────────
        // Numpad 7 / 9 while a River chain is being placed step its points, not the history (see River.UndoPoint).
        [HarmonyPatch(typeof(FiresCore.Undo.UndoHistory), nameof(FiresCore.Undo.UndoHistory.Undo))]
        private static class UndoHistory_Undo_RiverPoint
        {
            private static bool Prefix() => !River.UndoPoint();
        }

        [HarmonyPatch(typeof(FiresCore.Undo.UndoHistory), nameof(FiresCore.Undo.UndoHistory.Redo))]
        private static class UndoHistory_Redo_RiverPoint
        {
            private static bool Prefix() => !River.RedoPoint();
        }

        private static readonly Dictionary<long, float> s_riverSurface = new Dictionary<long, float>();
        private const float SurfaceReach = 12f;

        // 0.2.1064: the cells are read over the WHOLE cut footprint (the sloped banks reach radius * BankSpread) before a dab cuts
        // them. Reading only radius left the bank ring carved but unread, so the next dab centred there read the already-cut bed
        // as its "surface" and cut a full depth below it again: each dab one depth lower, a canyon along the stroke.
        private static float StrokeSurface(Vector3 at, float radius)
        {
            int r = Mathf.CeilToInt(radius * WaterShapingTools.BankSpread + 1f);
            int cx = Mathf.FloorToInt(at.x), cz = Mathf.FloorToInt(at.z);
            for (int dz = -r; dz <= r; dz++)
                for (int dx = -r; dx <= r; dx++)
                {
                    if (dx * dx + dz * dz > r * r) continue;
                    long key = ((long)(cx + dx) << 32) ^ (uint)(cz + dz);
                    if (s_riverSurface.ContainsKey(key)) continue;
                    s_riverSurface[key] = VoxelWorld.TryFindSurfaceY(cx + dx + 0.5f, cz + dz + 0.5f, at.y, SurfaceReach, out float y) ? y : at.y;
                }
            return s_riverSurface.TryGetValue(((long)cx << 32) ^ (uint)cz, out float centre) ? centre : at.y;
        }
    }

    // Vriver's chain on screen (0.2.1052): a line through the points to the crosshair and a post on each point, in the Vriver
    // ring's dig colour (PondTool's preview style). It hides itself the frame nobody draws it (the tool was put away).
    internal sealed class RiverChainView : MonoBehaviour
    {
        private const float PostHeight = 2.5f, LineWidth = 0.12f;
        private static readonly Color Dig = new Color(0.85f, 0.6f, 0.3f, 1f), Start = new Color(0.35f, 0.75f, 1f, 1f);
        private static RiverChainView s_view;
        private LineRenderer _path;
        private readonly List<LineRenderer> _posts = new List<LineRenderer>();
        private readonly List<LineRenderer> _rings = new List<LineRenderer>();   // 0.2.1064: each point's own width
        private const int RingSegments = 40;
        private int _drawnFrame;

        internal static void Draw(Material mat, List<Vector3> chain, List<float> radii, Vector3 cursor, float cursorRadius)
        {
            if (chain.Count == 0) { if (s_view != null && s_view.gameObject.activeSelf) s_view.gameObject.SetActive(false); return; }
            if (s_view == null)
            {
                var go = new GameObject("FAT_RiverChainView");
                DontDestroyOnLoad(go);
                s_view = go.AddComponent<RiverChainView>();
                s_view._path = NewLine(go.transform, mat);
            }
            if (!s_view.gameObject.activeSelf) s_view.gameObject.SetActive(true);
            s_view._drawnFrame = Time.frameCount;
            var path = s_view._path;
            path.positionCount = chain.Count + 1;
            for (int i = 0; i < chain.Count; i++) path.SetPosition(i, chain[i] + Vector3.up * 0.3f);
            path.SetPosition(chain.Count, cursor + Vector3.up * 0.3f);
            path.startColor = Dig; path.endColor = Dig;
            while (s_view._posts.Count < chain.Count) s_view._posts.Add(NewLine(s_view.transform, mat));
            for (int i = 0; i < s_view._posts.Count; i++)
            {
                var post = s_view._posts[i];
                bool on = i < chain.Count;
                if (post.gameObject.activeSelf != on) post.gameObject.SetActive(on);
                if (!on) continue;
                post.positionCount = 2;
                post.SetPosition(0, chain[i]);
                post.SetPosition(1, chain[i] + Vector3.up * PostHeight);
                Color c = i == 0 ? Start : Dig;
                post.startColor = c; post.endColor = c;
            }
            // A ring of each placed point's width (the radius the wheel had when it was placed).
            while (s_view._rings.Count < chain.Count) s_view._rings.Add(NewLine(s_view.transform, mat));
            for (int i = 0; i < s_view._rings.Count; i++)
            {
                var ring = s_view._rings[i];
                bool on = i < chain.Count && radii != null && i < radii.Count;
                if (ring.gameObject.activeSelf != on) ring.gameObject.SetActive(on);
                if (!on) continue;
                ring.loop = true;
                ring.positionCount = RingSegments;
                for (int k = 0; k < RingSegments; k++)
                {
                    float a = k * Mathf.PI * 2f / RingSegments;
                    ring.SetPosition(k, chain[i] + new Vector3(Mathf.Cos(a) * radii[i], 0.3f, Mathf.Sin(a) * radii[i]));
                }
                Color c = i == 0 ? Start : Dig;
                ring.startColor = c; ring.endColor = c;
            }
        }

        private static LineRenderer NewLine(Transform parent, Material mat)
        {
            var go = new GameObject("line");
            go.transform.SetParent(parent, false);
            var lr = go.AddComponent<LineRenderer>();
            lr.sharedMaterial = mat;
            lr.useWorldSpace = true;
            lr.startWidth = LineWidth; lr.endWidth = LineWidth;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            return lr;
        }

        private void LateUpdate()
        {
            if (Time.frameCount > _drawnFrame + 1) gameObject.SetActive(false);
        }
    }
}
