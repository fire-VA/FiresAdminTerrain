using System;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // A WEC / INFINITY HAMMER TERRAIN OP EDITS ONLY THE LAYER IT IS AIMED AT (Fire, 10-06: "all the tools should only affect the
    // layer they are raycast on"; "infinity hammer level should only affect the cave level, nothing above the roof").
    //
    // World Edit Commands' `terrain` command (Infinity Hammer's level/raise/slope/reset run it with the ghost's Y) writes the
    // ground zone's TerrainComp: one height per XZ column, the column's TOP surface. A level at a cave floor's y 58 under a hill
    // at y 104 cut the hill down; a level on the hill over a cave filled down into it. The layer an op is aimed at comes from
    // FiresTerrainLayers (the one resolver every tool uses): Island, Cave, or Ground with its band (Floor..Ceiling). WecTerrainBridge
    // sends here every Cave op and every Ground op with a finite Floor (a cave under it); islands keep FiresSkyIslandTerrainEdit,
    // open ground keeps the TerrainComp.
    //
    // Here: a grid of small cylinders over the op's circle. Each column is resolved on its own (TryResolveColumn: the surface
    // nearest the op's layer surface there) and edited only when it is the SAME layer (a cave column under an open mouth is left
    // alone). Its surface moves to the op's target, the cut or fill span clamped into the column's band (ClampSpan): no other
    // limit (Fire: "there really shouldnt be much of a limit"). The plan is read before any stamp, then queued (Segment-tall
    // cylinders, SegmentsPerFrame a frame) inside a FatUndo scope, so one op is one undo step and syncs through the chunk anchors
    // like every V-tool stroke. The TerrainComp is not touched. Debug lines:
    //   [FiresLayers] terrain op on <layer.Describe()> at (x,y,z) via <route>: <op> r=R -> this layer only: ...
    //   [FiresLayers] terrain op on ... : done, N segment(s) stamped
    internal static class FiresLayerTerrainEdit
    {
        private const float StampRadius = 2f, StampSpacing = 2f;
        private const float MinStep = 0.05f;
        private const float BedOverlap = 1f;
        private const float MaxRadius = 40f;
        private const float ResetHalfHeight = 12f;
        private const string Tag = "[FiresLayers]";

        // The layers this route edits: a cave, or ground with a cave under it.
        internal static bool Takes(in TerrainLayer layer)
            => layer.Kind == TerrainLayerKind.Cave || (layer.Kind == TerrainLayerKind.Ground && layer.Floor > FiresTerrainLayers.WorldBottom);

        // target(dx, dz, surface) = the height that column's surface ends at (NaN: leave it). Returns the queued line.
        internal static string Reshape(string op, string route, in TerrainLayer layer, Vector3 center, float radius,
            Func<float, float, float, float> target)
        {
            float r = Mathf.Clamp(radius, StampRadius, MaxRadius);
            int filled = 0, cut = 0, already = 0, other = 0, clamped = 0, queued = 0;
            float half = StampSpacing * 0.5f;
            for (float dz = -r + half; dz < r; dz += StampSpacing)
                for (float dx = -r + half; dx < r; dx += StampSpacing)
                {
                    if (dx * dx + dz * dz > r * r) continue;
                    float x = center.x + dx, z = center.z + dz;
                    if (!FiresTerrainLayers.TryResolveColumn(x, z, in layer, out TerrainLayer col)) { other++; continue; }
                    float s = col.Surface;
                    float t = target(dx, dz, s);
                    if (float.IsNaN(t) || Mathf.Abs(t - s) < MinStep) { already++; continue; }
                    bool fill = t > s;
                    float lo = fill ? s - BedOverlap : t, hi = fill ? t : s + BedOverlap;
                    float wantLo = lo, wantHi = hi;
                    if (!col.ClampSpan(ref lo, ref hi) || hi - lo < MinStep) { clamped++; continue; }
                    if (lo > wantLo + 0.01f || hi < wantHi - 0.01f) clamped++;
                    queued += Queue(new Vector3(x, 0f, z), lo, hi, fill ? VoxelBrushMode.Fill : VoxelBrushMode.Carve);
                    if (fill) filled++;
                    else cut++;
                }
            string line = $"{Tag} terrain op on {layer.Describe()} at ({center.x:0.0},{center.y:0.0},{center.z:0.0}) via {route}: "
                + $"{op} r={r:0.#}{(radius > MaxRadius ? $" (asked {radius:0.#}, capped)" : "")} -> this layer only: {filled} column(s) to "
                + $"fill, {cut} to cut ({queued} segment(s) queued, {SegmentsPerFrame} a frame), {already} already there, {clamped} held "
                + $"inside the layer's band, {other} another layer or no surface (left alone)";
            FiresLogger.LogDebug(line);
            if (queued > 0) StartPump(line, $"a {KindName(layer)} {op}");
            return line;
        }

        // `reset`: the brush edits in a cylinder ResetHalfHeight up and down from the op, clamped into the layer's band.
        internal static string Restore(string route, in TerrainLayer layer, Vector3 center, float radius)
        {
            float r = Mathf.Clamp(radius, StampRadius, MaxRadius);
            float lo = center.y - ResetHalfHeight, hi = center.y + ResetHalfHeight;
            string done = "nothing (no room inside the layer's band)";
            if (layer.ClampSpan(ref lo, ref hi) && hi - lo >= MinStep)
            {
                FatUndo.BeginPlayerEdit($"a {KindName(layer)} reset");
                try { done = VoxelWorld.ApplyBrush(new Vector3(center.x, (lo + hi) * 0.5f, center.z), r, hi - lo, VoxelBrushShape.Cylinder, VoxelBrushMode.Restore); }
                finally { FatUndo.EndPlayerEdit(); }
            }
            string line = $"{Tag} terrain op on {layer.Describe()} at ({center.x:0.0},{center.y:0.0},{center.z:0.0}) via {route}: "
                + $"reset r={r:0.#} -> this layer only, restored {lo:0.#}-{hi:0.#} ({done})";
            FiresLogger.LogDebug(line);
            return line;
        }

        private static string KindName(in TerrainLayer layer) => layer.Kind == TerrainLayerKind.Cave ? "cave" : "ground-over-cave";

        // ── The queue (one op at a time; a second op's segments join the same queue) ─────────────────────────────────────────
        private const float Segment = 24f;
        private const int SegmentsPerFrame = 8;

        private struct Job
        {
            internal Vector3 Centre;
            internal float Height;
            internal VoxelBrushMode Mode;
        }

        private static readonly System.Collections.Generic.Queue<Job> s_jobs = new System.Collections.Generic.Queue<Job>();
        private static bool s_pumping;
        private static string s_undoWhat;
        private static int s_done, s_refused, s_threw;
        private static string s_firstThrow;

        private static int Queue(Vector3 at, float bottom, float top, VoxelBrushMode mode)
        {
            int n = 0;
            for (float y0 = bottom; y0 < top - 0.01f; y0 += Segment)
            {
                float y1 = Mathf.Min(top, y0 + Segment + 0.5f);   // segments overlap 0.5 m: no seam between them
                s_jobs.Enqueue(new Job { Centre = new Vector3(at.x, (y0 + y1) * 0.5f, at.z), Height = y1 - y0, Mode = mode });
                n++;
            }
            return n;
        }

        private static void StartPump(string head, string undoWhat)
        {
            if (s_pumping) return;
            s_undoWhat = undoWhat;
            if (ZNet.instance == null) { StampBatch(int.MaxValue); Finish(head); return; }
            s_pumping = true;
            ZNet.instance.StartCoroutine(Pump(head));
        }

        private static System.Collections.IEnumerator Pump(string head)
        {
            try
            {
                while (s_jobs.Count > 0)
                {
                    StampBatch(SegmentsPerFrame);
                    if (s_jobs.Count > 0) yield return null;
                }
            }
            finally { s_pumping = false; }
            Finish(head);
        }

        // Each batch inside a FatUndo player-edit scope: VoxelWorld.ApplyBrush keeps the touched chunks' edits only inside one,
        // and the batches of one op follow each other well inside FatUndo's stroke idle time, so the op is one undo step.
        private static void StampBatch(int max)
        {
            FatUndo.BeginPlayerEdit(s_undoWhat ?? "a layer terrain op");
            try { for (int i = 0; i < max && s_jobs.Count > 0; i++) Stamp(s_jobs.Dequeue()); }
            finally { FatUndo.EndPlayerEdit(); }
        }

        private static void Stamp(Job job)
        {
            try
            {
                string done = VoxelWorld.ApplyBrush(job.Centre, StampRadius, job.Height, VoxelBrushShape.Cylinder, job.Mode);
                if (done != null && done.StartsWith("Refused", StringComparison.Ordinal)) s_refused++;
                else s_done++;
            }
            catch (Exception ex)
            {
                s_threw++;
                if (s_firstThrow == null)
                {
                    s_firstThrow = $"{ex.GetType().Name}: {ex.Message}";
                    FiresLogger.LogError($"{Tag} layer terrain stamp threw at ({job.Centre.x:0.0},{job.Centre.z:0.0}): {ex}");
                }
            }
        }

        private static void Finish(string head)
        {
            int cut = head.IndexOf(" -> ", StringComparison.Ordinal);
            FiresLogger.LogDebug($"{(cut > 0 ? head.Substring(0, cut) : head)}: done, {s_done} segment(s) stamped"
                + (s_refused > 0 ? $", {s_refused} refused (closed zone)" : "")
                + (s_threw > 0 ? $", {s_threw} THREW (first: {s_firstThrow})" : ""));
            s_done = s_refused = s_threw = 0;
            s_firstThrow = null;
        }
    }
}
