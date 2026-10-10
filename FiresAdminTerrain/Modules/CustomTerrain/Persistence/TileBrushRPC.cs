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
        private const string RPC_SLOPE      = "VATileBrushSlope";

        private static bool _registered;

        // Real handler count from the last Register() — feeds the 📡 RPCs
        // mini-banner (VAFatLoadSummary.EmitRpcs).
        public static int RegisteredHandlers { get; private set; }

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
            rpc.Register<ZPackage>(RPC_FLAT,       (sender, pkg) => RecvWithAdminGate(sender, TileOp.Level,     pkg, Recv_Flat));
            rpc.Register<ZPackage>(RPC_RAISE,      (sender, pkg) => RecvWithAdminGate(sender, TileOp.Raise,     pkg, Recv_Raise));
            rpc.Register<ZPackage>(RPC_PAINT,      (sender, pkg) => RecvWithAdminGate(sender, TileOp.Paint,     pkg, Recv_Paint));
            rpc.Register<ZPackage>(RPC_CLEAR,      (sender, pkg) => RecvWithAdminGate(sender, TileOp.Clear,     pkg, Recv_Clear));
            rpc.Register<ZPackage>(RPC_BIOME,      (sender, pkg) => RecvWithAdminGate(sender, TileOp.Biome,     pkg, Recv_Biome));
            rpc.Register<ZPackage>(RPC_HOLE_PUNCH, (sender, pkg) => RecvWithAdminGate(sender, TileOp.HolePunch, pkg, Recv_HolePunch));
            rpc.Register<ZPackage>(RPC_HOLE_PATCH, (sender, pkg) => RecvWithAdminGate(sender, TileOp.HolePatch, pkg, Recv_HolePatch));
            rpc.Register<ZPackage>(RPC_SMOOTH,     (sender, pkg) => RecvWithAdminGate(sender, TileOp.Smooth,    pkg, Recv_Smooth));
            rpc.Register<ZPackage>(RPC_GRAB,       (sender, pkg) => RecvWithAdminGate(sender, TileOp.Grab,      pkg, Recv_Grab));
            rpc.Register<ZPackage>(RPC_NOISE,      (sender, pkg) => RecvWithAdminGate(sender, TileOp.Noise,     pkg, Recv_Noise));
            rpc.Register<ZPackage>(RPC_SLOPE,      (sender, pkg) => RecvWithAdminGate(sender, TileOp.Slope,     pkg, Recv_Slope));

            RegisteredHandlers = 11;
            FiresLogger.LogVerbose($"[TileBrushRPC] Registered {RegisteredHandlers} RPC handlers (flat / raise / paint / clear / biome / hole_punch / hole_patch / smooth / grab / noise / slope) - admin-gated.");
        }

        // Resets the registration flag so the next ZRoutedRpc instance gets
        // fresh handlers. Without this, logout/reconnect keeps _registered=true
        // but the new ZRoutedRpc bus has no handlers and MP brush sync
        // silently breaks. Mirrors the pattern in RegionScrubberRPC.
        public static void ResetInitialized()
        {
            _registered = false;
        }

        // Which tile op an RPC carries. The gate keys its permission rule on this, so a
        // new op is a new enum member that no allow-list names, and is therefore denied.
        public enum TileOp
        {
            Level,
            Raise,
            Paint,
            Clear,
            Biome,
            HolePunch,
            HolePatch,
            Smooth,
            Grab,
            Noise,
            Slope,
        }

        // The ONLY ops a non-admin peer may send: the ones that finish a surface a player
        // already has, never the ones that change the shape of the world. An allow-list,
        // not a deny-list - anything absent here is refused, including every op added later.
        private static readonly System.Collections.Generic.HashSet<TileOp> NonAdminOps =
            new System.Collections.Generic.HashSet<TileOp> { TileOp.Level, TileOp.Paint, TileOp.Smooth };

        // Shared admin-guard wrapper - THE authority for tile edits, and the single point
        // for the check and the reject log. A client-side check is only a hint.
        private static void RecvWithAdminGate(long sender, TileOp op, ZPackage pkg, System.Action<ZPackage> apply)
        {
            if (!VerdantsAscent.Terrain.Lifecycle.AdminCheck.IsAdmin(sender) && !NonAdminOps.Contains(op))
            {
                FiresLogger.LogWarning($"[TileBrushRPC] Non-admin peer {sender} attempted {op} - rejected.");
                return;
            }
            apply(pkg);
        }

        // The client's own early-out, so a refusal is visible before the op is destroyed.
        // RecvWithAdminGate still decides; this only has to agree with it.
        public static bool LocalPeerMayEdit(TileOp op)
            => ZNet.instance == null || ZNet.instance.IsServer()
               || FiresCore.Sync.AdminSyncing.IsLocalAdmin()
               || NonAdminOps.Contains(op);

        // ── SEND helpers — fall back to local-only when no ZRoutedRpc ────

        public static void SendSlope(Vector3 center, float radius, float angle, float altitude, float amount, float smooth, bool square)
        {
            var rpc = ZRoutedRpc.instance;
            if (rpc == null || ZNet.instance == null)
            {
                TileBrush.BrushSlope(center, radius, angle, altitude, amount, smooth, square);
                return;
            }
            var pkg = new ZPackage();
            pkg.Write(center);
            pkg.Write(radius);
            pkg.Write(angle);
            pkg.Write(altitude);
            pkg.Write(amount);
            pkg.Write(smooth);
            pkg.Write(square);
            rpc.InvokeRoutedRPC(ZRoutedRpc.Everybody, RPC_SLOPE, pkg);
        }

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

        private static void Recv_Slope(ZPackage pkg)
        {
            var center = pkg.ReadVector3();
            float radius = pkg.ReadSingle();
            float angle = pkg.ReadSingle();
            float altitude = pkg.ReadSingle();
            float amount = pkg.ReadSingle();
            float smooth = pkg.ReadSingle();
            bool square = pkg.ReadBool();
            TileBrush.BrushSlope(center, radius, angle, altitude, amount, smooth, square);
        }

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

        // FAT's one ZNet.Awake hook (FatVanillaHooks) calls ResetInitialized
        // then Register, when ZRoutedRpc.instance exists, so the handlers wire
        // onto the new RPC bus after logout/reconnect (a sticky _registered
        // would leave the new bus with no handlers: a silent MP-sync break).
    }
}
