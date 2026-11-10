using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // HALO RING OBJECT SECTORS keyed on unrolled ring coordinates. Root fix for the ~14.5k-object column
    // (Tools\PLAN_halo_zone_keys_20261024.md; written by [fgn] as files for [worldgen], 10-24).
    //
    // Vanilla files every ZDO in a 64 m x,z sector (ZoneSystem.GetSectorIndex). On HaloFlat (axis = world Y) one x,z cell on the
    // wall is a ~3 km column, so every object query around a player (what instantiates, what syncs, what spawns count) returns
    // the whole column. Fix, OBJECT side only:
    // - GetSectorIndex(p) inside the ring's field = sector (zu, zv): zu = index around the ring (N = round(2πR/64) cells of C/N m,
    //   wrapped to ±N/2), zv = index along the axis + VOffset (200). These ids never collide with the x,z ids of anything off the
    //   ring (|z| <= 88 on HaloFlat) and fit vanilla's 512 x 512 sector grid (±256).
    // - SectorToIndex(x, y) wraps x for ring rows: the centre ± area loops in ZDOMan.FindSectorObjects / FindDistantObjects
    //   cross the seam (angle 0) correctly.
    // - The five vanilla object queries that turn a POSITION into a sector (ZDOMan.ReleaseNearbyZDOS, ZDOMan.CreateSyncList,
    //   ZNetScene.IsAreaReady, ZNetScene.CreateDestroyObjects, SpawnSystem.UpdateSpawnList) call ZoneSystem.GetZone(pos); that
    //   one call in each is redirected to SectorOf(pos).
    // TERRAIN zones are NOT touched: ZoneSystem.GetZone / GetZonePos / m_zones / IsZoneLoaded stay vanilla x,z, because FAT's
    // voxel streamer, far quilt, paint and vegetation use zone ids as x,z geometry (~105 sites).
    //
    // Every peer must file ZDOs the same way from the first one, and the ring itself reaches clients by RPC only after connect,
    // so the key uses HaloFlat's FIXED ring (FatHaloArc.HaloFlatKeyValue) and is ON only on the HaloFlat ring-only world
    // (FatHaloArc.RingOnly: world name + super flat, known to both peers at load). When it switches on with ZDOs already filed
    // the vanilla way (the server's world load, a client's first packets), every ZDO is re-filed once.
    // Saves: ZDOs store positions, never sectors, so nothing in the save format changes; old HaloFlat saves load as they are.
    internal static class FatHaloSectors
    {
        internal const int VOffset = 200;
        private const float Cell = 64f;

        private sealed class Map
        {
            public Vector3 O, D, Ref0, Ref1;
            public float R, W, T, C, CellU;
            public int N, Half, RowLo, RowHi;
        }

        private static volatile Map s_map;
        private static bool s_wanted;

        internal static bool On => s_map != null;

        private static readonly FieldInfo Instances = AccessTools.Field(typeof(ZNetScene), "m_instances");
        private static float s_nextCount;

        // Proof line for Fire's hand-test: how many objects are instantiated around this peer (expect hundreds, not ~14.5k).
        private static void CountLine()
        {
            if (Time.unscaledTime < s_nextCount || ZNetScene.instance == null || ZNet.instance == null) return;
            s_nextCount = Time.unscaledTime + 30f;
            int n = Instances?.GetValue(ZNetScene.instance) is ICollection c ? c.Count : -1;
            Vector3 at = ZNet.instance.GetReferencePosition();
            Vector2s s = SectorOf(at);
            FiresLogger.LogDebug($"[FatHaloSectors] around you: {n} objects instantiated; you are in ring sector ({s.x}, {s.y}) at ({at.x:0}, {at.y:0}, {at.z:0})");
        }

        // MAIN THREAD (FatHaloArc.Tick).
        internal static void Tick()
        {
            if (s_map != null) CountLine();
            bool want = FatHaloArc.RingOnly;
            if (want == s_wanted) return;
            s_wanted = want;
            if (want)
            {
                var m = Build();
                if (m == null) return;
                Patches(true);
                s_map = m;
                int moved = Refile();
                FiresLogger.LogInfo($"[FatHaloSectors] ring object sectors ON: N {m.N} around (cell {m.CellU:0.00} m), rows {m.RowLo}..{m.RowHi} (V offset {VOffset}), ids zu ±{m.Half} inside the ±256 grid; {moved} existing objects re-filed");
            }
            else if (s_map != null)
            {
                int moved = Refile(off: true);
                s_map = null;
                Patches(false);
                FiresLogger.LogInfo($"[FatHaloSectors] ring object sectors OFF; {moved} objects re-filed by x,z");
            }
        }

        // 0.2.1052 (Fire 10-26: "none of our ringworld logic ... ever runs on any world that is not a ring world"; [wishbone]'s
        // off-ring audit): the sector patches are applied only while ring sectors are on, and removed when they turn off or the
        // world shuts down. On every other world vanilla's GetSectorIndex / SectorToIndex / the five queries run unpatched.
        private static bool s_patched;

        private static void Patches(bool on)
        {
            if (on == s_patched) return;
            var h = global::VerdantsAscent.FiresAdminTerrain.harmony;
            var targets = new (MethodBase target, HarmonyMethod prefix, HarmonyMethod transpiler)[]
            {
                (AccessTools.Method(typeof(ZoneSystem), nameof(ZoneSystem.GetSectorIndex), new[] { typeof(Vector3) }),
                    new HarmonyMethod(typeof(ZoneSystem_GetSectorIndex), "Prefix"), null),
                (AccessTools.Method(typeof(ZoneSystem), nameof(ZoneSystem.SectorToIndex), new[] { typeof(int), typeof(int) }),
                    new HarmonyMethod(typeof(ZoneSystem_SectorToIndex), "Prefix"), null),
                (AccessTools.Method(typeof(ZDOMan), "ReleaseNearbyZDOS"), null, new HarmonyMethod(typeof(FatHaloSectors), nameof(Redirect))),
                (AccessTools.Method(typeof(ZDOMan), "CreateSyncList"), null, new HarmonyMethod(typeof(FatHaloSectors), nameof(Redirect))),
                (AccessTools.Method(typeof(ZNetScene), nameof(ZNetScene.IsAreaReady)), null, new HarmonyMethod(typeof(FatHaloSectors), nameof(Redirect))),
                (AccessTools.Method(typeof(ZNetScene), "CreateDestroyObjects"), null, new HarmonyMethod(typeof(FatHaloSectors), nameof(Redirect))),
                (AccessTools.Method(typeof(SpawnSystem), "UpdateSpawnList"), null, new HarmonyMethod(typeof(FatHaloSectors), nameof(Redirect))),
            };
            int n = 0;
            foreach (var (target, prefix, transpiler) in targets)
            {
                if (target == null) { FiresLogger.LogWarning("[FatHaloSectors] a sector patch target is missing on this game version"); continue; }
                try
                {
                    if (on) h.Patch(target, prefix: prefix, transpiler: transpiler);
                    else h.Unpatch(target, HarmonyPatchType.All, h.Id);
                    n++;
                }
                catch (Exception e) { FiresLogger.LogWarning($"[FatHaloSectors] {(on ? "patch" : "unpatch")} {target.DeclaringType?.Name}.{target.Name}: {e.Message}"); }
            }
            s_patched = on;
            FiresLogger.LogDebug($"[FatHaloSectors] sector patches {(on ? "applied" : "removed")} ({n} methods)");
        }

        // World shutdown: the map and the patches go with the world, so the next world never files a ZDO into ring sectors.
        [HarmonyPatch(typeof(ZNet), "OnDestroy")]
        private static class ZNet_OnDestroy_ClearSectors
        {
            private static void Postfix()
            {
                s_map = null;
                s_wanted = false;
                s_helper = null; s_helperFor = 0u;
                Patches(false);
            }
        }

        private static Map Build()
        {
            var p = FatHaloArc.HaloFlatKeyValue.Split(',');
            var f = new float[p.Length];
            for (int i = 0; i < p.Length; i++) f[i] = float.Parse(p[i], CultureInfo.InvariantCulture);
            return Build(new Vector3(f[0], f[1], f[2]), new Vector3(f[3], f[4], f[5]), f[6], f[7], f[8], true);
        }

        private static Map Build(Vector3 o, Vector3 d, float r, float w, float t, bool sectors)
        {
            var m = new Map { O = o, D = d.normalized, R = r, W = w, T = t };
            m.Ref0 = Mathf.Abs(m.D.y) > 0.999f ? Vector3.right : Vector3.ProjectOnPlane(Vector3.down, m.D).normalized;   // = FatHaloArc's angle frame
            m.Ref1 = Vector3.Cross(m.D, m.Ref0);
            m.C = 2f * Mathf.PI * m.R;
            m.N = Mathf.Max(8, Mathf.RoundToInt(m.C / Cell));
            m.CellU = m.C / m.N;
            m.Half = m.N / 2;
            m.RowLo = Mathf.FloorToInt((-m.W - Cell + 32f) / Cell) + VOffset;
            m.RowHi = Mathf.FloorToInt((m.W + Cell + 32f) / Cell) + VOffset;
            int xzEdge = Mathf.CeilToInt((FatHaloArc.HaloFlatWorldEdge + 32f) / Cell);
            if (sectors && (m.Half >= 255 || m.RowHi >= 255 || m.RowLo <= xzEdge))
            {
                FiresLogger.LogError($"[FatHaloSectors] ring object sectors REFUSED: N {m.N}, rows {m.RowLo}..{m.RowHi} don't fit the ±256 sector grid clear of x,z ids (|z| <= {xzEdge}). Objects keep x,z sectors.");
                return null;
            }
            return m;
        }

        // 0.2.1052 ([wishbone]'s audit): the FatApi unroll helpers use THIS world's ring (FatHaloArc's own axis + band), built
        // once per ring and cached by its signature; no ring = no map (no string parse, no allocation per call).
        private static Map s_helper;
        private static uint s_helperFor;

        private static Map Helper()
        {
            if (s_map != null) return s_map;
            if (!FatHaloArc.Active) return null;
            uint sig = FatHaloArc.Signature();
            if (s_helper != null && sig == s_helperFor) return s_helper;
            if (!FatHaloArc.Axis(out Vector3 o, out Vector3 d, out float r) || !FatHaloArc.Band(out float w, out _, out float t)) return null;
            s_helper = Build(o, d, r, w, t, false);
            s_helperFor = sig;
            return s_helper;
        }

        /// <summary>Unrolled metres in the ring's frame: u 0..C around, v along the axis from the band middle. False off the field.</summary>
        private static bool Unroll(Map m, Vector3 p, out float u, out float v)
        {
            Vector3 rel = p - m.O;
            v = Vector3.Dot(rel, m.D);
            u = 0f;
            if (v > m.W + Cell || v < -m.W - Cell) return false;
            Vector3 radial = rel - v * m.D;
            float r2 = radial.sqrMagnitude;
            float outer = m.R + m.T + Cell;
            if (r2 < 0.25f || r2 > outer * outer) return false;
            float ang = Mathf.Atan2(Vector3.Dot(radial, m.Ref1), Vector3.Dot(radial, m.Ref0));
            if (ang < 0f) ang += 2f * Mathf.PI;
            u = ang * m.R;
            return true;
        }

        private static int Wrap(Map m, int zu) { int w = ((zu + m.Half) % m.N + m.N) % m.N - m.Half; return w; }

        /// <summary>The ring object sector of p, or false off the ring (then vanilla's x,z sector applies).</summary>
        private static bool TryRingSector(Map m, Vector3 p, out int zu, out int zv)
        {
            zu = zv = 0;
            if (!Unroll(m, p, out float u, out float v)) return false;
            zu = Wrap(m, Mathf.FloorToInt(u / m.CellU));
            zv = Mathf.FloorToInt((v + 32f) / Cell) + VOffset;
            return true;
        }

        private static bool IsRingRow(Map m, int y) => y >= m.RowLo && y <= m.RowHi;

        /// <summary>The sector to query objects around p: the ring object sector on the ring, vanilla's GetZone elsewhere.</summary>
        public static Vector2s SectorOf(Vector3 p)
        {
            var m = s_map;
            if (m != null && TryRingSector(m, p, out int zu, out int zv)) return new Vector2s(zu, zv);
            return ZoneSystem.GetZone(p);
        }

        private static ZoneSystem.SectorIndex VanillaIndex(Vector3 p)
            => ZoneSystem.SectorToIndex(Utils.FloorToInt((p.x + 32f) / 64f), Utils.FloorToInt((p.z + 32f) / 64f));

        // ── vanilla patches (each returns to vanilla when the key is off) ───────────────────────────────────────────────

        private static class ZoneSystem_GetSectorIndex
        {
            private static bool Prefix(Vector3 point, ref ZoneSystem.SectorIndex __result)
            {
                var m = s_map;
                if (m == null || !TryRingSector(m, point, out int zu, out int zv)) return true;
                __result = ZoneSystem.SectorToIndex(zu, zv);
                return false;
            }
        }

        private static class ZoneSystem_SectorToIndex
        {
            // The centre ± area loops hand x past ±N/2 at the seam: wrap it on ring rows only.
            private static void Prefix(ref int sectorX, int sectorY)
            {
                var m = s_map;
                if (m == null || !IsRingRow(m, sectorY)) return;
                if (sectorX < -m.Half || sectorX >= m.N - m.Half) sectorX = Wrap(m, sectorX);
            }
        }

        // The one ZoneSystem.GetZone(pos) in each of the five object queries -> SectorOf(pos).
        // 0.2.1397: applied by hand in the sector switch above (ring worlds only); the five unused one-line wrapper classes are deleted.
        private static IEnumerable<CodeInstruction> Redirect(IEnumerable<CodeInstruction> code, MethodBase original)
        {
            var getZone = AccessTools.Method(typeof(ZoneSystem), nameof(ZoneSystem.GetZone), new[] { typeof(Vector3) });
            var mine = AccessTools.Method(typeof(FatHaloSectors), nameof(SectorOf));
            int n = 0;
            foreach (var ci in code)
            {
                if (ci.Calls(getZone)) { n++; yield return new CodeInstruction(OpCodes.Call, mine).MoveLabelsFrom(ci); continue; }
                yield return ci;
            }
            if (n != 1) FiresLogger.LogWarning($"[FatHaloSectors] {original?.DeclaringType?.Name}.{original?.Name}: expected one ZoneSystem.GetZone, found {n} (redirected {n}); ring object queries there may use x,z columns");
        }

        // ── re-file every ZDO when the key turns on (or off) with ZDOs already filed ────────────────────────────────────

        private static readonly FieldInfo ById = AccessTools.Field(typeof(ZDOMan), "m_objectsByID");
        private static readonly FieldInfo Portals = AccessTools.Field(typeof(ZDOMan), "m_portalObjects");

        private static int Refile(bool off = false)
        {
            var man = ZDOMan.instance;
            if (man == null || ById == null) return 0;
            var all = new List<ZDO>();
            if (ById.GetValue(man) is IDictionary dict) foreach (DictionaryEntry e in dict) if (e.Value is ZDO z) all.Add(z);
            int moved = 0;
            var m = s_map;
            foreach (var z in all)
            {
                Vector3 p = z.GetPosition();
                ZoneSystem.SectorIndex vanilla = VanillaIndex(p);
                ZoneSystem.SectorIndex ring = m != null && TryRingSector(m, p, out int zu, out int zv) ? ZoneSystem.SectorToIndex(zu, zv) : vanilla;
                ZoneSystem.SectorIndex from = off ? ring : vanilla, to = off ? vanilla : ring;
                if (from.Sector == to.Sector) continue;
                man.RemoveFromSector(z, from);
                man.AddToSector(z, to);
                moved++;
            }
            // Portals are listed by sector too: rebuild that index under the new keys.
            if (Portals?.GetValue(man) is Dictionary<ZoneSystem.SectorIndex, List<ZDO>> portals && portals.Count > 0)
            {
                var list = new List<ZDO>();
                foreach (var kv in portals) list.AddRange(kv.Value);
                portals.Clear();
                foreach (var z in list)
                {
                    var key = off ? VanillaIndex(z.GetPosition()) : z.GetSectorIndex();
                    if (!portals.TryGetValue(key, out var l)) portals[key] = l = new List<ZDO>();
                    l.Add(z);
                }
            }
            return moved;
        }

        // ── FatApi helpers (add-only; Core / FGN / FDT / the ring map reflect them) ────────────────────────────────────

        internal static bool HaloUnroll(Vector3 p, out float u, out float v)
        {
            u = v = 0f;
            var m = Helper();
            return m != null && Unroll(m, p, out u, out v);
        }

        internal static bool HaloRoll(float u, float v, out Vector3 pos)
        {
            pos = Vector3.zero;
            var m = Helper();
            if (m == null) return false;
            u = Mathf.Repeat(u, m.C);
            float ang = u / m.R;
            Vector3 dir = m.Ref0 * Mathf.Cos(ang) + m.Ref1 * Mathf.Sin(ang);
            pos = m.O + m.D * v + dir * m.R;
            return true;
        }

        internal static Vector3 SectorPos(Vector2s id)
        {
            var m = s_map;
            if (m == null || !IsRingRow(m, id.y)) return ZoneSystem.GetZonePos(id);
            HaloRoll((Wrap(m, id.x) + 0.5f) * m.CellU, (id.y - VOffset) * Cell, out Vector3 pos);
            return pos;
        }

        internal static Vector2s Delta(Vector2s a, Vector2s b)
        {
            int dx = b.x - a.x, dy = b.y - a.y;
            var m = s_map;
            if (m != null && IsRingRow(m, a.y) && IsRingRow(m, b.y)) dx = Wrap(m, dx);
            return new Vector2s(dx, dy);
        }

        internal static float SurfaceDistance(Vector3 a, Vector3 b)
        {
            var m = s_map;
            if (m == null || !Unroll(m, a, out float ua, out float va) || !Unroll(m, b, out float ub, out float vb)) return Vector3.Distance(a, b);
            float du = Mathf.Abs(ua - ub); if (du > m.C * 0.5f) du = m.C - du;
            float dv = va - vb;
            return Mathf.Sqrt(du * du + dv * dv);
        }
    }
}
