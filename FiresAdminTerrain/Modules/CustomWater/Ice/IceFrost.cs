using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    // 0.2.1179, polished ice + frost layer (Fire 10-28: "a thin layer just under the ice that gets cleared as we walk on it ... making
    // walked on ice "polish" so we can see through to the bottom"; answers: frost forms ONLY while snowing, polished paths refill LIKE
    // SNOW TRAILS, thick unwalked ice is a frosty haze, the bed under clear ice is the real ground).
    //   - FROST 0..1 per 1 m ice column = the world frost base (server-owned: rises at 1x, ~30 min full, only while the shown weather
    //     snows) capped per polished cell by that cell's regrowth.
    //   - POLISH: an owned character standing on solid ice (IceBreaks.Tick, every 0.5 s, player + creatures) clears frost along its
    //     walked segment (0.6 m either side). Applied at once on the walker's peer, then asked of the server (FatIcePolish); the server
    //     owns the list (cell -> polish time, regrowth), sends it to everyone (FatIceFrost) and saves it in _ice.txt ("p x z t r" lines,
    //     the base as "fr b").
    //   - REGROW like snow trails: untouched for 10 min (GraceSec), then 1x (FullSec to full) while it snows; a cell fully regrown is
    //     dropped. No regrowth in clear weather.
    //   - The look: WaterStateField writes it into _FiresWaterStateTex A (see the plan doc's shader contract): A = 0 legacy/no ice,
    //     else 0.5 + 0.5 x frost. No terrain remesh: only the state textures refill (budgeted, RefreshStale).
    internal static class IceFrost
    {
        internal const float GraceSec = 600f, FullSec = 1800f, StripHalf = 0.6f;
        private const int MaxCells = 4096;
        private const string RpcPolish = "FatIcePolish", RpcList = "FatIceFrost";

        private struct Cell { public double T; public float R; }
        private static volatile Dictionary<long, Cell> s_cells = new Dictionary<long, Cell>();
        private static float s_base, s_sentBase = -1f, s_lastStepBase;
        internal static bool Dirty;
        internal static float Base => s_base;
        internal static int Polished => s_cells.Count;
        private static int s_polishN, s_regrowN;
        private static bool s_snowing;

        private static long Key(int x, int z) => ((long)x << 32) ^ (uint)z;

        // 0.2.1183 (Fire: "ice has a hard cut in it ... more white in the top section"): polished cells are 1 m squares, so the clear
        // walked strip met the frosty haze in a hard straight step. The state texture now takes a 3x3 tent (1-2-1, 1 m) of the
        // world-space frost, so a polished edge ramps over ~2 m on every chunk alike. No polished cells = the plain base (no lookups).
        internal static float FrostSmoothAt(float x, float z)
        {
            float b = s_base;
            if (b <= 0f) return 0f;
            var c = s_cells;   // 0.2.1187: one snapshot for all 9 reads (copy-on-write store: writers build a new map and swap the volatile ref)
            if (c.Count == 0) return b;
            float sum = 0f;
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                    sum += FrostIn(c, b, x + dx, z + dz) * (dx == 0 ? 2f : 1f) * (dz == 0 ? 2f : 1f);
            return sum / 16f;
        }

        // 0.2.1187 (the lead: TryIceAt / FrostAt may be read off the main thread by Vedr): the cell store is copy-on-write - every
        // writer (main thread only) builds a new Dictionary and swaps the volatile s_cells reference, never mutating a published map -
        // so a reader takes one snapshot and needs no lock.
        private static float FrostIn(Dictionary<long, Cell> c, float b, float x, float z)
            => c.Count > 0 && c.TryGetValue(Key(Mathf.FloorToInt(x), Mathf.FloorToInt(z)), out var cell) ? Mathf.Min(b, cell.R) : b;

        internal static float FrostAt(float x, float z)
        {
            float b = s_base;
            if (b <= 0f) return 0f;
            var c = s_cells;
            if (c.Count > 0 && c.TryGetValue(Key(Mathf.FloorToInt(x), Mathf.FloorToInt(z)), out var cell)) return Mathf.Min(b, cell.R);
            return b;
        }

        // ---- RPC ----
        internal static void RegisterRpc(ZRoutedRpc r)
        {
            r.Register<Vector3, Vector3, string>(RpcPolish, OnPolishRequest);
            r.Register<ZPackage>(RpcList, OnList);
        }

        internal static void SendTo(long peer)
        {
            if (ZRoutedRpc.instance != null) ZRoutedRpc.instance.InvokeRoutedRPC(peer, RpcList, Pack());
        }

        private static ZPackage Pack()
        {
            var p = new ZPackage();
            p.Write(s_base);
            var c = s_cells;
            p.Write(c.Count);
            foreach (var kv in c) { p.Write((int)(kv.Key >> 32)); p.Write((int)(uint)kv.Key); p.Write(kv.Value.R); }
            return p;
        }

        private static void OnList(long sender, ZPackage p)
        {
            if (ZNet.instance == null || ZNet.instance.IsServer()) return;
            if (ZRoutedRpc.instance == null || sender != ZRoutedRpc.instance.GetServerPeerID())   // only the server's list
            {
                FiresLogger.LogDebug($"[WaterIce] frost list REJECTED: sender uid {sender} is not the server");
                return;
            }
            try
            {
                float b = p.ReadSingle();
                int n = p.ReadInt();
                if (float.IsNaN(b) || n < 0 || n > MaxCells) return;
                var next = new Dictionary<long, Cell>(n);
                for (int i = 0; i < n; i++) { int x = p.ReadInt(), z = p.ReadInt(); float r = p.ReadSingle(); if (!float.IsNaN(r)) next[Key(x, z)] = new Cell { T = 0, R = Mathf.Clamp01(r) }; }
                s_base = Mathf.Clamp01(b);
                s_cells = next;
                WaterStateField.FrostChanged();
            }
            catch { }
        }

        // ---- polish (any peer, owner side of the walker) ----
        private sealed class Last { public Vector3 P; public bool Has; }
        private static readonly ConditionalWeakTable<Character, Last> s_last = new ConditionalWeakTable<Character, Last>();
        private static float s_polishLogAt;

        // From IceBreaks.Tick: the owned character c stands on solid ice at p.
        internal static void Walked(Character c, Vector3 p)
        {
            var l = s_last.GetOrCreateValue(c);
            Vector3 from = l.Has && (l.P - p).sqrMagnitude < 36f ? l.P : p;
            l.P = p; l.Has = true;
            if (s_base <= 0.02f) return;   // no frost to clear
            if (FrostAt(p.x, p.z) <= 0.02f && FrostAt(from.x, from.z) <= 0.02f) return;
            string who = Utils.GetPrefabName(c.gameObject);
            var zn = ZNet.instance; var zr = ZRoutedRpc.instance;
            if (zn == null || zr == null) return;
            if (zn.IsServer()) { Add(from, p, who); return; }
            Stamp(from, p, local: true);   // the walker sees it at once
            zr.InvokeRoutedRPC(zr.GetServerPeerID(), RpcPolish, from, p, who);
        }

        private static void OnPolishRequest(long sender, Vector3 from, Vector3 to, string who)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (float.IsNaN(from.x) || float.IsNaN(to.x) || float.IsNaN(from.z) || float.IsNaN(to.z)) return;
            // SECURITY: a short segment only, on FAT water with ice
            if ((from - to).sqrMagnitude > 64f) from = to;
            if (!IceField.TryIceAt(to, out float cover, out _) || cover <= 0f) return;
            Add(from, to, string.IsNullOrEmpty(who) ? "peer" : (who.Length > 40 ? who.Substring(0, 40) : who));
        }

        private static void Add(Vector3 from, Vector3 to, string who)
        {
            int n = Stamp(from, to, local: false);
            if (n == 0) return;
            Dirty = true; s_polishN += n;
            if (Time.time >= s_polishLogAt)
            {
                s_polishLogAt = Time.time + 2f;
                FiresLogger.LogDebug($"[WaterIce] polished {n} cell(s) at ({to.x:0.0},{to.z:0.0}) by {who}: frost cleared on the walked strip, refrosts after {GraceSec / 60f:0} min untouched while it snows (0.2.1179)");
            }
            if (ZRoutedRpc.instance != null) ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, RpcList, Pack());
        }

        private static int Stamp(Vector3 a, Vector3 b, bool local)
        {
            var next = new Dictionary<long, Cell>(s_cells);
            double now = ZNet.instance != null ? ZNet.instance.GetTimeSeconds() : 0;
            Vector2 A = new Vector2(a.x, a.z), B = new Vector2(b.x, b.z), AB = B - A;
            float len2 = AB.sqrMagnitude;
            int x0 = Mathf.FloorToInt(Mathf.Min(a.x, b.x) - StripHalf), x1 = Mathf.FloorToInt(Mathf.Max(a.x, b.x) + StripHalf);
            int z0 = Mathf.FloorToInt(Mathf.Min(a.z, b.z) - StripHalf), z1 = Mathf.FloorToInt(Mathf.Max(a.z, b.z) + StripHalf);
            int n = 0;
            for (int z = z0; z <= z1; z++)
                for (int x = x0; x <= x1; x++)
                {
                    Vector2 q = new Vector2(x + 0.5f, z + 0.5f);
                    float t = len2 > 1e-4f ? Mathf.Clamp01(Vector2.Dot(q - A, AB) / len2) : 0f;
                    if ((A + AB * t - q).sqrMagnitude > (StripHalf + 0.5f) * (StripHalf + 0.5f)) continue;
                    if (next.Count >= MaxCells) break;
                    next[Key(x, z)] = new Cell { T = now, R = 0f };
                    n++;
                }
            if (n == 0) return 0;
            s_cells = next;
            WaterStateField.FrostChanged();
            return n;
        }

        // ---- server tick (from IceField.Tick's 2 s gate): the base grows while snowing, polished cells regrow after the grace ----
        private static float s_sendAt, s_logAt;
        internal static void ServerTick(float dtSec)
        {
            s_snowing = VerdantsAscent.Terrain.Voxel.FatSnowTrailRefill.IsSnowing(out _);
            if (!s_snowing || dtSec <= 0f) { MaybeSend(); return; }
            float d = dtSec / FullSec;
            s_base = Mathf.Min(1f, s_base + d);
            var c = s_cells;
            if (c.Count > 0)
            {
                double now = ZNet.instance != null ? ZNet.instance.GetTimeSeconds() : 0;
                Dictionary<long, Cell> next = null;
                foreach (var kv in c)
                {
                    if (now - kv.Value.T < GraceSec) continue;
                    next ??= new Dictionary<long, Cell>(c);
                    float r = kv.Value.R + d;
                    if (r >= 1f) next.Remove(kv.Key); else next[kv.Key] = new Cell { T = kv.Value.T, R = r };
                    s_regrowN++;
                }
                if (next != null) { s_cells = next; Dirty = true; }
            }
            if (Mathf.Abs(s_base - s_lastStepBase) >= 1f / 32f || s_regrowN > 0) { s_lastStepBase = s_base; WaterStateField.FrostChanged(); }
            MaybeSend();
        }

        private static void MaybeSend()
        {
            if (Time.time >= s_sendAt && (Mathf.Abs(s_base - s_sentBase) >= 1f / 64f || Dirty || Time.time >= s_sendAt + 20f))
            {
                s_sendAt = Time.time + 10f; s_sentBase = s_base;
                if (ZRoutedRpc.instance != null) ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, RpcList, Pack());
            }
        }

        // 30 s Debug line (any peer), from IceField's log tick
        internal static string Summary()
        {
            string s = $"frost: base {s_base:0.00}, polished cells {s_cells.Count} (polished {s_polishN}, regrowing {s_regrowN} in the last 30 s) (snowing {(s_snowing ? "y" : "n")}) (0.2.1179)";
            s_polishN = 0; s_regrowN = 0;
            return s;
        }

        // ---- persistence (the server; lines in _ice.txt: "fr b" and "p x z r") ----
        internal static void LoadLines(string[] lines)
        {
            var next = new Dictionary<long, Cell>();
            for (int i = 1; i < lines.Length; i++)
            {
                var p = lines[i].Trim().Split(' ');
                if (p.Length == 2 && p[0] == "fr" && float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float b) && !float.IsNaN(b)) s_base = Mathf.Clamp01(b);
                else if (p.Length == 4 && p[0] == "p" && int.TryParse(p[1], out int x) && int.TryParse(p[2], out int z)
                         && float.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float r) && !float.IsNaN(r) && next.Count < MaxCells)
                    next[Key(x, z)] = new Cell { T = -GraceSec, R = Mathf.Clamp01(r) };   // loaded cells are past their grace
            }
            s_cells = next; s_lastStepBase = s_base;
        }

        internal static string SaveLines()
        {
            var sb = new StringBuilder();
            sb.Append('\n').Append("fr ").Append(s_base.ToString("0.0000", CultureInfo.InvariantCulture));
            foreach (var kv in s_cells) sb.Append('\n').Append("p ").Append((int)(kv.Key >> 32)).Append(' ').Append((int)(uint)kv.Key).Append(' ').Append(kv.Value.R.ToString("0.000", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        // test hooks (fat_water_ice_test)
        internal static void TestSetBase(float b) { s_base = Mathf.Clamp01(b); WaterStateField.FrostChanged(); }
        internal static void TestClear() { s_cells = new Dictionary<long, Cell>(); Dirty = true; WaterStateField.FrostChanged(); }

        // ---- 0.2.1179 (Fire: under clear ice the REAL GROUND, no snow on the bed): our snow ops skip underwater columns ----
        private static bool s_underSaid;
        // 0.2.1315 (Fire 10-31: "snow should not draw underwater and we should not get snow trails there"): the test is the COLUMN, not
        // the walker's feet - a player walking ON the ice stands at the surface, so the 1179 feet test passed and his trail cut the snow
        // on the bed below. Underwater = the column's own water (generated: GenWaterField depth; placed: PlacedWaterField surface - bed)
        // is deeper than 0.3 m. The feet test stays for columns whose bed is unknown.
        internal static bool Underwater(Vector3 p)
        {
            if (ColumnUnderwater(p.x, p.z, p.y, out float s, out float depth))
            {
                if (!s_underSaid) { s_underSaid = true; FiresLogger.LogDebug($"[WaterIce] underwater snow op skipped at ({p.x:0.0},{p.z:0.0}): water column {depth:0.00} m under the surface {s:0.0}, walker y {p.y:0.0} (no trail / shovel snow on a water column, on the ice or below it; once per session) (0.2.1315)"); }
                return true;
            }
            if (!IceField.TryWater(p, out s) || p.y >= s - 0.3f) return false;
            if (!s_underSaid) { s_underSaid = true; FiresLogger.LogDebug($"[WaterIce] underwater snow op skipped at ({p.x:0.0},{p.z:0.0}): ground y {p.y:0.0} under the water surface {s:0.0} (no trail / shovel snow on the bed; once per session) (0.2.1179)"); }
            return true;
        }

        // The water column at (x, z) near height y (NaN = any height): true when its depth (surface - bed) is > 0.3 m.
        internal static bool ColumnUnderwater(float x, float z, float y, out float surface, out float depth, float minDepth = 0.3f)
        {
            surface = float.NaN; depth = 0f;
            try
            {
                if (VerdantsAscent.Terrain.Voxel.GenWaterField.Ready
                    && VerdantsAscent.Terrain.Voxel.GenWaterField.TryWaterSurfaceSmooth(x, z, out float lvl, out float d)
                    && (float.IsNaN(y) || Mathf.Abs(lvl - y) < 6f) && d > minDepth) { surface = lvl; depth = d; return true; }
                float lo = float.IsNaN(y) ? -1000f : y - 6f, hi = float.IsNaN(y) ? 5000f : y + 6f;
                if (PlacedWaterField.TrySurface(x, z, lo, hi, PlacedWaterField.Consumer.Sheet, out float s, out float bed)
                    && !float.IsNaN(bed) && s - bed > minDepth) { surface = s; depth = s - bed; return true; }
            }
            catch { }
            return false;
        }
    }
}
