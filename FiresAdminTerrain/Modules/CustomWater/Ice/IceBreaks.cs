using System.Collections.Generic;
using System.Globalization;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    // 0.2.1170, water freeze step 3 (Fire 10-28: "thin ice CAN break under you", "ships break thin ice, ships take hull damage on thick
    // ice", "no fishing under solid ice", and "if there is a log or something in the water when it freezes it freezes into the ice same
    // with ships etc"):
    //   - BROKEN HOLES: a 1 m column broken through keeps the exposure at its break (fb); its ice re-grows from zero as the exposure
    //     rises past fb (IceField.DeriveAt). The server owns the list: a peer asks (RPC FatIceBreak), the server adds the cells, sends the
    //     whole list to everyone (FatIceHoles) and saves it in the same _ice.txt. A hole expires when it has fully re-frozen (F - fb
    //     >= 2.5 day) or the winter is over (F < fb - 1).
    //   - THIN ICE UNDER CHARACTERS: an owned, weight-bearing character (not flying) standing on our ice collider where the ice is under
    //     BreakBelow (0.15 m) breaks through with a chance per check (0.5 s) rising as the ice thins: 0.10 m -> 60 %, 0.15 m -> 5 %.
    //     Items never break ice (only Characters).
    //   - SHIPS (owner side): a moving ship breaks ice thinner than ShipBreakBelow (0.30 m) at its bow and hull points; on thicker
    //     ice it takes blunt hull damage scaled by speed and is slowed. A ship whose centre column is solid ice is frozen in.
    //   - FREEZE-IN: anything with Floating or a Ship on solid ice is held (kinematic) where it is, released (physics restored) when the
    //     ice thaws or breaks. SKIPPED (agreed with [seasons], whose Vedr ice cubes cover them): anything with a Character, Fish or
    //     SnowRoller component, or a ZDO float "vedr_frozen" > 0 (0.2.1188: Vedr writes a float, not an int). Owner side only, checked at ~2 Hz per object.
    //   - FISHING: a solid-ice column has no liquid level (PlacedWaterField / VoxelWaterSurface) and fish live only in pure water
    //     (WaterStateField.PureWaterAt), so a float rests on the ice and nothing bites there; this file only logs it once.
    internal static class IceBreaks
    {
        internal const float BreakBelow = 0.15f, ShipBreakBelow = 0.30f, HoleRadius = 1.2f, ShipHoleRadius = 2.2f;
        private const int MaxHoles = 768;
        private const string RpcBreak = "FatIceBreak", RpcHoles = "FatIceHoles";

        private static volatile Dictionary<long, float> s_holes = new Dictionary<long, float>();   // snapshot, replaced on change (worker reads)
        internal static bool Dirty;
        internal static int HoleCount => s_holes.Count;
        internal static int Breaks, Refrozen, ShipHits, FrozenIn, Released;

        private static long Key(int x, int z) => ((long)x << 32) ^ (uint)z;

        internal static bool HoleExposure(float x, float z, out float fb)
        {
            var h = s_holes;
            if (h.Count == 0) { fb = 0f; return false; }
            return h.TryGetValue(Key(Mathf.FloorToInt(x), Mathf.FloorToInt(z)), out fb);
        }

        // ---- RPC ----
        internal static void RegisterRpc(ZRoutedRpc r)
        {
            r.Register<Vector3, float, string>(RpcBreak, OnBreakRequest);
            r.Register<ZPackage>(RpcHoles, OnHoles);
        }

        internal static void SendHolesTo(long peer)
        {
            if (ZRoutedRpc.instance == null) return;
            ZRoutedRpc.instance.InvokeRoutedRPC(peer, RpcHoles, Pack());
        }

        private static ZPackage Pack()
        {
            var p = new ZPackage();
            var h = s_holes;
            p.Write(h.Count);
            foreach (var kv in h) { p.Write((int)(kv.Key >> 32)); p.Write((int)(uint)kv.Key); p.Write(kv.Value); }
            return p;
        }

        // Any peer: ask the server to break the ice at pos (radius r). The asking peer applies it at once too, so the fall-through is
        // immediate; the server's list replaces it a moment later.
        internal static void BreakAt(Vector3 pos, float r, string why)
        {
            var zr = ZRoutedRpc.instance;
            if (zr == null || ZNet.instance == null) return;
            if (ZNet.instance.IsServer()) { AddHoles(pos, r, why); return; }
            ApplyLocal(pos, r);
            zr.InvokeRoutedRPC(zr.GetServerPeerID(), RpcBreak, pos, r, why ?? "");
        }

        private static void OnBreakRequest(long sender, Vector3 pos, float r, string why)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (float.IsNaN(pos.x) || float.IsNaN(pos.z) || float.IsNaN(r)) return;
            // SECURITY: bounded radius, and the point must be FAT water with ice (no remote hole-punching anywhere)
            r = Mathf.Clamp(r, 0.5f, ShipHoleRadius);
            if (!IceField.TryIceAt(pos, out float cover, out _) || cover <= 0f) return;
            AddHoles(pos, r, why);
        }

        private static void ApplyLocal(Vector3 pos, float r)
        {
            var next = new Dictionary<long, float>(s_holes);
            Stamp(next, pos, r);
            s_holes = next;
            IceField.BumpRevision();
            WaterStateField.HoleChanged(pos.x, pos.z, pos.y);
        }

        private static int Stamp(Dictionary<long, float> into, Vector3 pos, float r)
        {
            int n = 0, ri = Mathf.CeilToInt(r);
            float fb = IceField.AppliedExposure;
            for (int dz = -ri; dz <= ri; dz++)
                for (int dx = -ri; dx <= ri; dx++)
                {
                    if (dx * dx + dz * dz > r * r + 0.5f) continue;
                    if (into.Count >= MaxHoles) return n;
                    into[Key(Mathf.FloorToInt(pos.x) + dx, Mathf.FloorToInt(pos.z) + dz)] = fb;
                    n++;
                }
            return n;
        }

        // test hook (fat_water_ice_test finally): drop every hole the test made
        internal static void TestClearHoles()
        {
            var old = s_holes;
            if (old.Count == 0) return;
            s_holes = new Dictionary<long, float>(); Dirty = true;
            IceField.BumpRevision();
            Touch(old, s_holes);
            if (ZNet.instance != null && ZNet.instance.IsServer() && ZRoutedRpc.instance != null) ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, RpcHoles, Pack());
        }

        private static float s_breakLogAt;
        private static void AddHoles(Vector3 pos, float r, string why)
        {
            var next = new Dictionary<long, float>(s_holes);
            int n = Stamp(next, pos, r);
            if (n == 0) return;
            s_holes = next;
            Breaks++; Dirty = true;
            IceField.BumpRevision();
            WaterStateField.HoleChanged(pos.x, pos.z, pos.y);
            if (Time.time >= s_breakLogAt)
            {
                s_breakLogAt = Time.time + 2f;
                FiresLogger.LogDebug($"[WaterIce] broke through at ({pos.x:0.0},{pos.z:0.0}): {why}; {n} column(s) opened, {next.Count} hole column(s) now (0.2.1170)");
            }
            if (ZRoutedRpc.instance != null) ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, RpcHoles, Pack());
        }

        private static void OnHoles(long sender, ZPackage p)
        {
            if (ZNet.instance == null || ZNet.instance.IsServer()) return;
            if (ZRoutedRpc.instance == null || sender != ZRoutedRpc.instance.GetServerPeerID()) return;   // only the server's list
            try
            {
                int n = p.ReadInt();
                if (n < 0 || n > MaxHoles) return;
                var next = new Dictionary<long, float>(n);
                for (int i = 0; i < n; i++) { int x = p.ReadInt(), z = p.ReadInt(); float fb = p.ReadSingle(); if (!float.IsNaN(fb)) next[Key(x, z)] = fb; }
                var old = s_holes;
                s_holes = next;
                IceField.BumpRevision();
                Touch(old, next);
            }
            catch { }
        }

        // refresh the state / colliders where the hole set differs (bounded)
        private static void Touch(Dictionary<long, float> a, Dictionary<long, float> b)
        {
            var done = new HashSet<long>();
            int budget = 24;
            void At(long k)
            {
                int x = (int)(k >> 32), z = (int)(uint)k;
                long tile = ((long)(x >> 4) << 32) ^ (uint)(z >> 4);   // one refresh per 16 m
                if (!done.Add(tile) || budget-- <= 0) return;
                float y = Player.m_localPlayer != null ? Player.m_localPlayer.transform.position.y : 30f;
                if (IceField.TryWater(new Vector3(x + 0.5f, y, z + 0.5f), out float s)) y = s;
                WaterStateField.HoleChanged(x + 0.5f, z + 0.5f, y);
            }
            foreach (var k in b.Keys) if (!a.ContainsKey(k)) At(k);
            foreach (var k in a.Keys) if (!b.ContainsKey(k)) At(k);
        }

        // ---- persistence (the server; lines after the exposure in _ice.txt: "h x z fb") ----
        internal static void LoadLines(string[] lines)
        {
            var next = new Dictionary<long, float>();
            for (int i = 1; i < lines.Length; i++)
            {
                var p = lines[i].Trim().Split(' ');
                if (p.Length != 4 || p[0] != "h") continue;
                if (int.TryParse(p[1], out int x) && int.TryParse(p[2], out int z) && float.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float fb) && !float.IsNaN(fb))
                    if (next.Count < MaxHoles) next[Key(x, z)] = fb;
            }
            s_holes = next;
        }

        internal static string SaveLines()
        {
            var h = s_holes;
            if (h.Count == 0) return "";
            var sb = new StringBuilder();
            foreach (var kv in h) sb.Append('\n').Append("h ").Append((int)(kv.Key >> 32)).Append(' ').Append((int)(uint)kv.Key).Append(' ').Append(kv.Value.ToString("0.0000", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        // ---- tick: characters breaking thin ice (owner side, every peer) + hole expiry (server) ----
        private static float s_next, s_expireAt, s_sumAt;
        private static readonly AccessTools.FieldRef<Character, Collider> LastGround = AccessTools.FieldRefAccess<Character, Collider>("m_lastGroundCollider");
        private static readonly AccessTools.FieldRef<Character, ZNetView> CharNView = AccessTools.FieldRefAccess<Character, ZNetView>("m_nview");

        internal static bool IsOurIceCollider(Collider c)
            => c != null && (c.gameObject.name == "IceShell" || c.gameObject.name.StartsWith("fires_placed_ice_", System.StringComparison.Ordinal));

        // 0.2.1188 (Fire: "companions somehow end up under the ice?", shot 671): an owned character below a SOLID ice surface at its
        // column (more than 0.3 m under it) that Vedr has not frozen into a cube (ZDO "vedr_frozen" == 0) is lifted onto the ice
        // (surface + 0.2 m, same x/z, velocity zeroed), at most once per 2 s per character. Attached / boat riders are left alone.
        internal static int Unstuck;
        private static readonly Dictionary<int, float> s_unstickAt = new Dictionary<int, float>();
        private static float s_unstickLogAt;
        private static void Unstick(Character c, ZNetView nv)
        {
            if (c.IsAttached()) return;
            if (c is Player pl && pl.GetControlledShip() != null) return;
            Vector3 p = c.transform.position;
            if (!IceField.TryWater(p, out float surface) || surface <= p.y + 0.3f) return;
            if (!WaterStateField.IceSolidAt(p.x, p.z, surface)) return;
            var zdo = nv.GetZDO();
            if (zdo != null && zdo.GetFloat("vedr_frozen", 0f) > 0f) return;   // Vedr's ice cube owns it
            int id = c.GetInstanceID();
            if (s_unstickAt.TryGetValue(id, out float at) && Time.time < at) return;
            if (s_unstickAt.Count > 512) s_unstickAt.Clear();
            s_unstickAt[id] = Time.time + 2f;
            var to = new Vector3(p.x, surface + 0.2f, p.z);
            var body = c.GetComponent<Rigidbody>();
            if (body != null) { body.velocity = Vector3.zero; body.position = to; }
            c.transform.position = to;
            Unstuck++;
            if (Time.unscaledTime >= s_unstickLogAt)
            {
                s_unstickLogAt = Time.unscaledTime + 2f;
                FiresLogger.LogDebug($"[WaterIce] unstuck {Utils.GetPrefabName(c.gameObject)} from under the ice at ({p.x:0.0},{p.z:0.0}): was y {p.y:0.00}, surface {surface:0.00} (reason: below solid ice, not vedr_frozen) (0.2.1188)");
            }
        }

        internal static void Tick()
        {
            if (Time.time < s_next) return;
            s_next = Time.time + 0.5f;
            if (IceField.AppliedExposure > 0f || s_holes.Count > 0 || IceFrost.Base > 0.02f)   // 0.2.1179: + frost polish (Deep North ice has frost at exposure 0)
            {
                foreach (var c in Character.GetAllCharacters())
                {
                    if (c == null || c.IsDead() || c.IsFlying()) continue;
                    var nv = CharNView(c);
                    if (nv == null || !nv.IsValid() || !nv.IsOwner()) continue;
                    if (!c.IsOnGround()) { Unstick(c, nv); continue; }   // 0.2.1188: under the sheet is never "on ground" ice
                    if (!IsOurIceCollider(LastGround(c))) continue;
                    Vector3 p = c.transform.position;
                    if (!IceField.TryIceAt(p, out float cover, out float thick) || cover <= 0f) continue;
                    if (thick >= IceField.Walkable) IceFrost.Walked(c, p);   // 0.2.1179: walking on solid ice polishes the frost
                    if (thick >= BreakBelow) continue;
                    float chance = Mathf.Lerp(0.60f, 0.05f, Mathf.InverseLerp(IceField.Walkable, BreakBelow, thick));
                    if (Random.value >= chance) continue;
                    BreakAt(p, HoleRadius, $"{Utils.GetPrefabName(c.gameObject)} on thin ice {thick:0.000} m (breaks under {BreakBelow:0.00} m, chance {chance:P0} per 0.5 s)");
                }
            }
            var zn = ZNet.instance;
            if (zn != null && zn.IsServer() && Time.time >= s_expireAt && s_holes.Count > 0)
            {
                s_expireAt = Time.time + 10f;
                float f = IceField.AppliedExposure;
                var h = s_holes;
                List<long> gone = null;
                foreach (var kv in h) if (f - kv.Value >= 2.5f || f < kv.Value - 1f) (gone ??= new List<long>()).Add(kv.Key);
                if (gone != null)
                {
                    var next = new Dictionary<long, float>(h);
                    foreach (var k in gone) next.Remove(k);
                    s_holes = next; Refrozen += gone.Count; Dirty = true;
                    IceField.BumpRevision();
                    Touch(h, next);
                    FiresLogger.LogDebug($"[WaterIce] {gone.Count} hole column(s) dropped (re-frozen past 2.5 day or thawed), {next.Count} left (0.2.1170)");
                    if (ZRoutedRpc.instance != null) ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, RpcHoles, Pack());
                }
            }
            if (Time.time >= s_sumAt && FiresLogger.DebugEnabled)
            {
                s_sumAt = Time.time + 30f;
                if (Breaks + Refrozen + ShipHits + FrozenIn + Released + Unstuck > 0)
                    FiresLogger.LogDebug($"[WaterIce] 30 s: breaks {Breaks}, holes {s_holes.Count} (re-frozen/dropped {Refrozen}), ship hull hits {ShipHits}, froze in {FrozenIn}, released {Released}, unstuck from under the ice {Unstuck} (0.2.1188)");
                Breaks = Refrozen = ShipHits = FrozenIn = Released = Unstuck = 0;
            }
        }

        // ---- freeze-in: Floating objects and ships, owner side, ~2 Hz per object ----
        private sealed class Held { public float Next; public bool On, WasKinematic; }
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Component, Held> s_held = new System.Runtime.CompilerServices.ConditionalWeakTable<Component, Held>();
        private static float s_freezeLogAt, s_releaseLogAt;

        private static bool Skip(Component c, ZNetView nv)
        {
            if (c.GetComponent<Character>() != null || c.GetComponent<Fish>() != null || c.GetComponent<SnowRoller>() != null) return true;   // [seasons]' ice cubes
            var zdo = nv != null && nv.IsValid() ? nv.GetZDO() : null;
            return zdo != null && zdo.GetFloat("vedr_frozen", 0f) > 0f;
        }

        // true = held (the caller may skip its own physics)
        internal static bool FreezeIn(Component c, Rigidbody body, ZNetView nv)
        {
            if (c == null || body == null) return false;
            var h = s_held.GetOrCreateValue(c);
            if (h.On && (nv == null || !nv.IsValid() || !nv.IsOwner())) { Release(c, body, h, "ownership moved"); return false; }
            if (Time.time < h.Next) return h.On;
            h.Next = Time.time + 0.5f + Random.value * 0.05f;
            if (IceField.AppliedExposure <= 0f && !h.On) return false;
            if (nv == null || !nv.IsValid() || !nv.IsOwner() || Skip(c, nv)) return h.On;
            Vector3 p = body.position;
            bool solid = IceField.TryIceAt(p, out float cover, out float thick) && thick >= IceField.Walkable
                         && IceField.IceAmount(cover, thick, WaterStateField.IceSolid) >= WaterStateField.IceSolid;
            if (solid && !h.On)
            {
                h.On = true; h.WasKinematic = body.isKinematic;
                body.velocity = Vector3.zero; body.angularVelocity = Vector3.zero;
                body.isKinematic = true;
                FrozenIn++;
                if (Time.time >= s_freezeLogAt) { s_freezeLogAt = Time.time + 2f; FiresLogger.LogDebug($"[WaterIce] froze in {Utils.GetPrefabName(c.gameObject)} at ({p.x:0.0},{p.z:0.0}) (ice {thick:0.00} m) (0.2.1170)"); }
            }
            else if (!solid && h.On) Release(c, body, h, cover <= 0f ? "break" : "thaw");
            return h.On;
        }

        private static void Release(Component c, Rigidbody body, Held h, string why)
        {
            h.On = false;
            body.isKinematic = h.WasKinematic;
            Released++;
            if (Time.time >= s_releaseLogAt) { s_releaseLogAt = Time.time + 2f; FiresLogger.LogDebug($"[WaterIce] released {Utils.GetPrefabName(c.gameObject)} ({why}) (0.2.1170)"); }
        }

        // ---- ships (owner side, ~2 Hz): break thin ice ahead, hull damage + slow on thick ice ----
        private static readonly Dictionary<int, float> s_shipNext = new Dictionary<int, float>();
        private static float s_shipLogAt;

        internal static void ShipTick(Ship ship)
        {
            if (ship == null) return;
            var nv = ship.GetComponent<ZNetView>();
            var body = ship.GetComponent<Rigidbody>();
            if (nv == null || !nv.IsValid() || !nv.IsOwner() || body == null) return;
            if (FreezeIn(ship, body, nv)) return;   // frozen in: no breaking, no sailing until the ice breaks or thaws
            int id = ship.GetInstanceID();
            if (s_shipNext.TryGetValue(id, out float next) && Time.time < next) return;
            s_shipNext[id] = Time.time + 0.5f;
            if (IceField.AppliedExposure <= 0f) return;
            Vector3 v = body.velocity; v.y = 0f;
            float speed = v.magnitude;
            if (speed < 0.5f) return;
            Vector3 fwd = v / speed, p = ship.transform.position;
            for (int k = 0; k < 3; k++)
            {
                Vector3 q = p + fwd * (2f + k * 2f);
                if (!IceField.TryIceAt(q, out float cover, out float thick) || cover <= 0f || thick <= 0f) continue;
                if (thick < ShipBreakBelow) { BreakAt(q, ShipHoleRadius, $"ship {Utils.GetPrefabName(ship.gameObject)} through {thick:0.00} m ice at {speed:0.0} m/s"); continue; }
                // thick ice: hull damage scaled by speed, and slowed
                var d = ship.GetComponent<IDestructible>();
                if (d != null)
                {
                    var hit = new HitData();
                    hit.m_damage.m_blunt = 10f * speed;
                    hit.m_point = q; hit.m_dir = -fwd;
                    d.Damage(hit);
                }
                body.velocity = new Vector3(body.velocity.x * 0.6f, body.velocity.y, body.velocity.z * 0.6f);
                ShipHits++;
                if (Time.time >= s_shipLogAt) { s_shipLogAt = Time.time + 2f; FiresLogger.LogDebug($"[WaterIce] ship hull damage: {Utils.GetPrefabName(ship.gameObject)} on {thick:0.00} m ice at {speed:0.0} m/s -> {10f * speed:0} blunt, slowed x0.6 (thick from {ShipBreakBelow:0.00} m) (0.2.1170)"); }
                break;
            }
        }

        private static bool s_fishSaid;
        internal static void NoteFishingBlocked(Vector3 p)
        {
            if (s_fishSaid) return;
            s_fishSaid = true;
            FiresLogger.LogDebug($"[WaterIce] fishing blocked at ({p.x:0.0},{p.z:0.0}): solid ice there has no liquid level, the float rests on it and fish live only in open water (0.2.1170)");
        }

        [HarmonyPatch(typeof(Ship), nameof(Ship.CustomFixedUpdate))]
        private static class Patch_Ship
        {
            private static void Prefix(Ship __instance) => ShipTick(__instance);
        }

        [HarmonyPatch(typeof(Floating), nameof(Floating.CustomFixedUpdate))]
        private static class Patch_Floating
        {
            private static readonly AccessTools.FieldRef<Floating, Rigidbody> Body = AccessTools.FieldRefAccess<Floating, Rigidbody>("m_body");
            private static readonly AccessTools.FieldRef<Floating, ZNetView> NView = AccessTools.FieldRefAccess<Floating, ZNetView>("m_nview");
            private static bool Prefix(Floating __instance)
            {
                if (__instance == null) return true;
                if (IceField.AppliedExposure <= 0f) return true;
                bool held = FreezeIn(__instance, Body(__instance), NView(__instance));
                if (held && __instance.GetComponent<FishingFloat>() != null) NoteFishingBlocked(__instance.transform.position);
                return !held;   // held in the ice: no buoyancy this step
            }
        }
    }
}

namespace VerdantsAscent.Terrain.Voxel
{
    // 0.2.1170: remesh only the generated-water chunks around a broken hole (no world-wide remesh).
    internal static class IceChunkRemesh
    {
        internal static void Around(float x, float y, float z)
        {
            int cx = Mathf.FloorToInt(x / VoxelChunk.CELLS), cy = Mathf.FloorToInt(y / VoxelChunk.CELLS), cz = Mathf.FloorToInt(z / VoxelChunk.CELLS);
            for (int dy = -1; dy <= 0; dy++)
                for (int dz = -1; dz <= 1; dz++)
                    for (int dx = -1; dx <= 1; dx++)
                        if (VoxelWorld.TryGetChunk(new Vector3Int(cx + dx, cy + dy, cz + dz), out var c) && c != null && c.HasGenWater)
                            VoxelMeshPipeline.EnqueueRemesh(c);
        }
    }
}
