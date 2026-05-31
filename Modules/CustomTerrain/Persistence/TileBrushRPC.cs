using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Persistence
{
    // Multiplayer sync for tile brush ops. Each Cmd_* in TileBrushCommands
    // routes through here instead of calling TileBrush.* directly.
    //
    // Pattern (matches original RPG-maker TerrainPaintRPC semantics):
    //   Sender: ZRoutedRpc.InvokeRoutedRPC(Everybody, name, ...args)
    //   Receiver (every peer including sender): deserialize, call local
    //     TileBrush.* which iterates affected tiles and runs ApplyOpAndSave.
    //     Only the OWNER of each tile actually writes the ZDO; others
    //     ClaimOwnership-fail and skip. ZDO replication propagates the
    //     owner's write to everyone.
    //
    // Sender's local Cmd_* still runs the op locally (loopback). That's
    // OK because:
    //   * Sender owns most tiles in single-player + admin-host MP → does
    //     the actual work locally; received RPC is then a no-op (data
    //     already matches, ApplyOpAndSave returns 0 via no-op-skip).
    //   * In strict-server MP (player is non-host client), local apply
    //     succeeds for tiles they own (none) or skips; the RPC reaches
    //     the host who DOES own the tile, host applies + ZDO replicates
    //     back.
    //
    // Falls back to local-only (no RPC) when ZNet.instance or
    // ZRoutedRpc.instance is null (offline mode / pre-world).
    public static class TileBrushRPC
    {
        private const string RPC_FLAT       = "VATileBrushFlat";
        private const string RPC_RAISE      = "VATileBrushRaise";
        private const string RPC_PAINT      = "VATileBrushPaint";
        private const string RPC_CLEAR      = "VATileBrushClear";
        private const string RPC_BIOME      = "VATileBrushBiome";
        private const string RPC_HOLE_PUNCH = "VATileBrushHolePunch";
        private const string RPC_SMOOTH     = "VATileBrushSmooth"; // round-31 B-2
        private const string RPC_GRAB       = "VATileBrushGrab";   // round-37 B-6
        private const string RPC_NOISE      = "VATileBrushNoise";  // round-37 B-7
        private const string RPC_HOLE_PATCH = "VATileBrushHolePatch";

        private static bool _registered;

        public static void Register()
        {
            if (_registered) return;
            var rpc = ZRoutedRpc.instance;
            if (rpc == null) return;
            _registered = true;

            // ZPackage-based handlers — flexible payload, easy to extend.
            // Each handler is admin-gated via RecvWithAdminGate; non-admin
            // peers get logged + rejected. sender==0 (local-server self-call
            // loopback) is auto-trusted by AdminCheck.
            rpc.Register<ZPackage>(RPC_FLAT,       (sender, pkg) => RecvWithAdminGate(sender, "Flat",      pkg, Recv_Flat));
            rpc.Register<ZPackage>(RPC_RAISE,      (sender, pkg) => RecvWithAdminGate(sender, "Raise",     pkg, Recv_Raise));
            rpc.Register<ZPackage>(RPC_PAINT,      (sender, pkg) => RecvWithAdminGate(sender, "Paint",     pkg, Recv_Paint));
            rpc.Register<ZPackage>(RPC_CLEAR,      (sender, pkg) => RecvWithAdminGate(sender, "Clear",     pkg, Recv_Clear));
            rpc.Register<ZPackage>(RPC_BIOME,      (sender, pkg) => RecvWithAdminGate(sender, "Biome",     pkg, Recv_Biome));
            rpc.Register<ZPackage>(RPC_HOLE_PUNCH, (sender, pkg) => RecvWithAdminGate(sender, "HolePunch", pkg, Recv_HolePunch));
            rpc.Register<ZPackage>(RPC_HOLE_PATCH, (sender, pkg) => RecvWithAdminGate(sender, "HolePatch", pkg, Recv_HolePatch));
            rpc.Register<ZPackage>(RPC_SMOOTH,     (sender, pkg) => RecvWithAdminGate(sender, "Smooth",    pkg, Recv_Smooth));
            rpc.Register<ZPackage>(RPC_GRAB,       (sender, pkg) => RecvWithAdminGate(sender, "Grab",      pkg, Recv_Grab));
            rpc.Register<ZPackage>(RPC_NOISE,      (sender, pkg) => RecvWithAdminGate(sender, "Noise",     pkg, Recv_Noise));

            FiresLogger.LogInfo("[TileBrushRPC] Registered 7 RPC handlers (flat / raise / paint / clear / biome / hole_punch / hole_patch) — admin-gated.");
        }

        // Resets the registration flag so the next ZRoutedRpc instance gets
        // fresh handlers. Without this, logout/reconnect keeps _registered=true
        // but the new ZRoutedRpc bus has no handlers and MP brush sync
        // silently breaks. Mirrors the pattern in RegionScrubberRPC.
        public static void ResetInitialized()
        {
            _registered = false;
        }

        // Shared admin-guard wrapper — single point for the check + reject log.
        private static void RecvWithAdminGate(long sender, string opName, ZPackage pkg, System.Action<ZPackage> apply)
        {
            if (!VerdantsAscent.Terrain.Lifecycle.AdminCheck.IsAdmin(sender))
            {
                FiresLogger.LogWarning($"[TileBrushRPC] Non-admin peer {sender} attempted {opName} — rejected.");
                return;
            }
            apply(pkg);
        }

        // ── SEND helpers — fall back to local-only when no ZRoutedRpc ────

        public static void SendFlat(Vector3 center, float radius, float height, bool square, bool islandMode = false)
        {
            var rpc = ZRoutedRpc.instance;
            if (rpc == null || ZNet.instance == null)
            {
                TileBrush.BrushFlat(center, radius, height, square, islandMode);
                return;
            }
            var pkg = new ZPackage();
            pkg.Write(center);
            pkg.Write(radius);
            pkg.Write(height);
            pkg.Write(square);
            pkg.Write(islandMode); // round-30: tail-appended for back-compat with older clients (they'll read past end and default to false on EOF inside Recv_Flat)
            rpc.InvokeRoutedRPC(ZRoutedRpc.Everybody, RPC_FLAT, pkg);
        }

        public static void SendRaise(Vector3 center, float radius, float delta, float falloff, bool islandMode = false)
        {
            var rpc = ZRoutedRpc.instance;
            if (rpc == null || ZNet.instance == null)
            {
                TileBrush.BrushRaise(center, radius, delta, falloff, islandMode);
                return;
            }
            var pkg = new ZPackage();
            pkg.Write(center);
            pkg.Write(radius);
            pkg.Write(delta);
            pkg.Write(falloff);
            pkg.Write(islandMode); // round-30
            rpc.InvokeRoutedRPC(ZRoutedRpc.Everybody, RPC_RAISE, pkg);
        }

        public static void SendPaint(Vector3 center, float radius, Color32 color, bool square)
        {
            var rpc = ZRoutedRpc.instance;
            if (rpc == null || ZNet.instance == null)
            {
                TileBrush.BrushPaint(center, radius, color, square);
                return;
            }
            var pkg = new ZPackage();
            pkg.Write(center);
            pkg.Write(radius);
            pkg.Write((int)color.r);
            pkg.Write((int)color.g);
            pkg.Write((int)color.b);
            pkg.Write((int)color.a);
            pkg.Write(square);
            rpc.InvokeRoutedRPC(ZRoutedRpc.Everybody, RPC_PAINT, pkg);
        }

        public static void SendClear(Vector3 center, float radius)
        {
            var rpc = ZRoutedRpc.instance;
            if (rpc == null || ZNet.instance == null)
            {
                TileBrush.BrushClear(center, radius);
                return;
            }
            var pkg = new ZPackage();
            pkg.Write(center);
            pkg.Write(radius);
            rpc.InvokeRoutedRPC(ZRoutedRpc.Everybody, RPC_CLEAR, pkg);
        }

        // Round-31 B-2: Smooth brush RPC. Carries strength as a 0..1
        // float — the receiver's BrushSmooth handles iteration_strengths
        // + the per-tile snapshot lookup.
        public static void SendSmooth(Vector3 center, float radius, float strength)
        {
            var rpc = ZRoutedRpc.instance;
            if (rpc == null || ZNet.instance == null)
            {
                TileBrush.BrushSmooth(center, radius, strength);
                return;
            }
            var pkg = new ZPackage();
            pkg.Write(center);
            pkg.Write(radius);
            pkg.Write(strength);
            rpc.InvokeRoutedRPC(ZRoutedRpc.Everybody, RPC_SMOOTH, pkg);
        }

        // Round-37 B-6 Grab brush. Drags terrain by `dragDeltaY` (cursor's
        // cumulative Y delta from stroke start). Snapshot-based — held
        // brush waits for cursor motion instead of accumulating.
        public static void SendGrab(Vector3 center, float radius, float dragDeltaY)
        {
            var rpc = ZRoutedRpc.instance;
            if (rpc == null || ZNet.instance == null)
            {
                TileBrush.BrushGrab(center, radius, dragDeltaY);
                return;
            }
            var pkg = new ZPackage();
            pkg.Write(center);
            pkg.Write(radius);
            pkg.Write(dragDeltaY);
            rpc.InvokeRoutedRPC(ZRoutedRpc.Everybody, RPC_GRAB, pkg);
        }

        // Round-37 B-7 Noise brush. Perlin displacement; intensity ±range,
        // scale controls Perlin frequency.
        public static void SendNoise(Vector3 center, float radius, float intensity, float scale)
        {
            var rpc = ZRoutedRpc.instance;
            if (rpc == null || ZNet.instance == null)
            {
                TileBrush.BrushNoise(center, radius, intensity, scale);
                return;
            }
            var pkg = new ZPackage();
            pkg.Write(center);
            pkg.Write(radius);
            pkg.Write(intensity);
            pkg.Write(scale);
            rpc.InvokeRoutedRPC(ZRoutedRpc.Everybody, RPC_NOISE, pkg);
        }

        public static void SendBiome(Vector3 center, float radius, ushort biome)
        {
            var rpc = ZRoutedRpc.instance;
            if (rpc == null || ZNet.instance == null)
            {
                TileBrush.BrushBiome(center, radius, biome);
                return;
            }
            var pkg = new ZPackage();
            pkg.Write(center);
            pkg.Write(radius);
            pkg.Write((int)biome);
            rpc.InvokeRoutedRPC(ZRoutedRpc.Everybody, RPC_BIOME, pkg);
        }

        public static void SendHolePunch(Vector3 center, float radius)
        {
            var rpc = ZRoutedRpc.instance;
            if (rpc == null || ZNet.instance == null)
            {
                TileBrush.BrushHolePunch(center, radius);
                return;
            }
            var pkg = new ZPackage();
            pkg.Write(center);
            pkg.Write(radius);
            rpc.InvokeRoutedRPC(ZRoutedRpc.Everybody, RPC_HOLE_PUNCH, pkg);
        }

        public static void SendHolePatch(Vector3 center, float radius)
        {
            var rpc = ZRoutedRpc.instance;
            if (rpc == null || ZNet.instance == null)
            {
                TileBrush.BrushPatchHole(center, radius);
                return;
            }
            var pkg = new ZPackage();
            pkg.Write(center);
            pkg.Write(radius);
            rpc.InvokeRoutedRPC(ZRoutedRpc.Everybody, RPC_HOLE_PATCH, pkg);
        }

        // ── RECV handlers — runs local op; ApplyOpAndSave's owner check
        //                    + no-op-skip make this safe under loopback ──

        private static void Recv_Flat(ZPackage pkg)
        {
            var center = pkg.ReadVector3();
            float radius = pkg.ReadSingle();
            float height = pkg.ReadSingle();
            bool square = pkg.ReadBool();
            bool islandMode = TryReadBoolTail(pkg); // round-30: backwards-compat
            TileBrush.BrushFlat(center, radius, height, square, islandMode);
        }

        private static void Recv_Raise(ZPackage pkg)
        {
            var center = pkg.ReadVector3();
            float radius = pkg.ReadSingle();
            float delta = pkg.ReadSingle();
            float falloff = pkg.ReadSingle();
            bool islandMode = TryReadBoolTail(pkg); // round-30: backwards-compat
            TileBrush.BrushRaise(center, radius, delta, falloff, islandMode);
        }

        // Reads a tail-appended bool if present; returns false if the
        // package ran out (older clients haven't written this field yet).
        private static bool TryReadBoolTail(ZPackage pkg)
        {
            try { return pkg.ReadBool(); }
            catch { return false; }
        }

        private static void Recv_Smooth(ZPackage pkg)
        {
            var center = pkg.ReadVector3();
            float radius = pkg.ReadSingle();
            float strength = pkg.ReadSingle();
            TileBrush.BrushSmooth(center, radius, strength);
        }

        private static void Recv_Grab(ZPackage pkg)
        {
            var center = pkg.ReadVector3();
            float radius = pkg.ReadSingle();
            float dragDeltaY = pkg.ReadSingle();
            TileBrush.BrushGrab(center, radius, dragDeltaY);
        }

        private static void Recv_Noise(ZPackage pkg)
        {
            var center = pkg.ReadVector3();
            float radius = pkg.ReadSingle();
            float intensity = pkg.ReadSingle();
            float scale = pkg.ReadSingle();
            TileBrush.BrushNoise(center, radius, intensity, scale);
        }

        private static void Recv_Paint(ZPackage pkg)
        {
            var center = pkg.ReadVector3();
            float radius = pkg.ReadSingle();
            byte r = (byte)pkg.ReadInt();
            byte g = (byte)pkg.ReadInt();
            byte b = (byte)pkg.ReadInt();
            byte a = (byte)pkg.ReadInt();
            bool square = pkg.ReadBool();
            TileBrush.BrushPaint(center, radius, new Color32(r, g, b, a), square);
        }

        private static void Recv_Clear(ZPackage pkg)
        {
            var center = pkg.ReadVector3();
            float radius = pkg.ReadSingle();
            TileBrush.BrushClear(center, radius);
        }

        private static void Recv_HolePunch(ZPackage pkg)
        {
            var center = pkg.ReadVector3();
            float radius = pkg.ReadSingle();
            TileBrush.BrushHolePunch(center, radius);
        }

        private static void Recv_HolePatch(ZPackage pkg)
        {
            var center = pkg.ReadVector3();
            float radius = pkg.ReadSingle();
            TileBrush.BrushPatchHole(center, radius);
        }

        private static void Recv_Biome(ZPackage pkg)
        {
            var center = pkg.ReadVector3();
            float radius = pkg.ReadSingle();
            ushort biome = (ushort)pkg.ReadInt();
            TileBrush.BrushBiome(center, radius, biome);
        }

        // Register on ZNet.Awake — that's when ZRoutedRpc.instance becomes
        // available. ResetInitialized first so we wire fresh handlers on
        // the new ZRoutedRpc instance after logout/reconnect (without it,
        // _registered=true sticks across sessions and the new RPC bus has
        // no handlers → silent MP-sync break).
        [HarmonyPatch(typeof(ZNet), nameof(ZNet.Awake))]
        private static class ZNet_Awake_RegisterTileBrushRPC
        {
            private static void Postfix()
            {
                ResetInitialized();
                Register();
            }
        }
    }
}
