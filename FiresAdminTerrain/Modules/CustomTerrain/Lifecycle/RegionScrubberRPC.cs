using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Lifecycle
{
    // Multiplayer sync for RegionScrubber operations.
    //
    // REQUEST PATTERN (per user choice, MASTER_GAMEPLAN.md §11 Q2):
    //   1. Client invokes RPC_REQUEST_SCRUB via Everybody (reaches server).
    //   2. Server-only handler validates AdminSyncing.IsAdmin(sender).
    //      Non-admin → log + reject. Admin (or local server self-call) →
    //      apply locally via RegionScrubber.ApplyLocally, then broadcast
    //      RPC_APPLY_SCRUB via Everybody.
    //   3. Every peer (including the original requester and the server
    //      itself) receives RPC_APPLY_SCRUB and calls ApplyLocally with
    //      source=Rpc.
    //
    // This means non-admin clients never alter the world even momentarily
    // — their REQUEST is just a message; they don't apply locally until
    // the APPLY broadcast comes back. Avoids the visual-jank failure mode
    // where local-apply-then-ZDO-rollback shows a flicker.
    //
    // Mirrors AutoWaterPatches' admin-gated RPC pattern.
    public static class RegionScrubberRPC
    {
        // RPC channel names. Prefixed FiresAdminTerrain so we don't clash
        // with the legacy FiresRPGmaker_* channels if both mods are loaded.
        private const string RPC_REQUEST_SCRUB = "FiresAdminTerrain_RegionScrub_Request";
        private const string RPC_APPLY_SCRUB   = "FiresAdminTerrain_RegionScrub_Apply";

        private static bool _registered;

        // Real handler count from the last Register() — feeds the 📡 RPCs
        // mini-banner (VAFatLoadSummary.EmitRpcs).
        public static int RegisteredHandlers { get; private set; }

        // Register handlers on the ZRoutedRpc instance. Idempotent — safe
        // to call repeatedly. Called from ZNet.Awake postfix below so we
        // re-register on logout/reconnect (without this, _registered
        // staying true across sessions silently breaks MP sync).
        public static void Register()
        {
            var rpc = ZRoutedRpc.instance;
            if (rpc == null) return;
            if (_registered) return;

            rpc.Register<ZPackage>(RPC_REQUEST_SCRUB, (sender, pkg) => RPC_HandleRequest(sender, pkg));
            rpc.Register<ZPackage>(RPC_APPLY_SCRUB,   (sender, pkg) => RPC_HandleApply(sender, pkg));
            _registered = true;
            RegisteredHandlers = 2;
            FiresLogger.LogVerbose("[RegionScrubberRPC] Registered 2 handlers (request + apply).");
        }

        // Called on ZNet shutdown / logout so the next session re-registers
        // against the new ZRoutedRpc instance. Without this, _registered
        // is sticky across sessions and the new RPC bus has no handlers.
        public static void ResetInitialized()
        {
            _registered = false;
        }

        // ── Sender helpers ──────────────────────────────────────────────

        // Client → server request to apply a scrub. Server validates admin
        // status before honoring. Sender does NOT apply locally — the
        // APPLY broadcast will fan back and trigger local apply.
        public static void SendRequest(Vector3 center, float radius, RegionScrubber.Scope scope, RegionScrubber.Source source)
        {
            var rpc = ZRoutedRpc.instance;
            if (rpc == null) return;
            rpc.InvokeRoutedRPC(ZRoutedRpc.Everybody, RPC_REQUEST_SCRUB, Pack(center, radius, scope, source));
        }

        // Server → all peers apply broadcast. Skips the admin check on the
        // receive side (caller already validated). Every peer applies
        // locally, including the server itself.
        public static void BroadcastApply(Vector3 center, float radius, RegionScrubber.Scope scope, RegionScrubber.Source source)
        {
            var rpc = ZRoutedRpc.instance;
            if (rpc == null) return;
            rpc.InvokeRoutedRPC(ZRoutedRpc.Everybody, RPC_APPLY_SCRUB, Pack(center, radius, scope, source));
        }

        // ── Handlers ────────────────────────────────────────────────────

        private static void RPC_HandleRequest(long sender, ZPackage pkg)
        {
            // Server-only — clients don't validate other clients' requests.
            if (ZNet.instance == null || !ZNet.instance.IsServer())
                return;

            // Shared admin guard. sender==0 (local-server loopback) auto-trusted
            // by AdminCheck; non-admin peers get logged + rejected here.
            if (!AdminCheck.IsAdmin(sender))
            {
                FiresLogger.LogWarning($"[RegionScrubberRPC] Non-admin peer {sender} requested scrub — rejected.");
                return;
            }

            Unpack(pkg, out var center, out var radius, out var scope, out var source);

            // Apply locally (this writes the server's authoritative state
            // to ZDOs the server owns).
            RegionScrubber.ApplyLocally(center, radius, scope, RegionScrubber.Source.Rpc);

            // Broadcast APPLY so every other peer mirrors it. Sender will
            // receive this back and also apply (idempotent — already
            // applied here on the server, but other peers see it for the
            // first time).
            BroadcastApply(center, radius, scope, source);

            FiresLogger.LogInfo($"[RegionScrubberRPC] REQUEST from peer {sender} approved: center={center} r={radius:F1} scope={scope} src={source}");
        }

        private static void RPC_HandleApply(long sender, ZPackage pkg)
        {
            if (!AdminCheck.IsAdmin(sender))
            {
                FiresLogger.LogWarning($"[RegionScrubberRPC] Non-admin peer {sender} sent a scrub apply — rejected.");
                return;
            }
            Unpack(pkg, out var center, out var radius, out var scope, out var source);

            // Skip the server's own loopback (it already applied in
            // HandleRequest). Detected by sender being the server peer.
            if (ZNet.instance != null && ZNet.instance.IsServer())
            {
                FiresLogger.LogVerbose($"[RegionScrubberRPC] Skipping APPLY loopback on server.");
                return;
            }

            RegionScrubber.ApplyLocally(center, radius, scope, RegionScrubber.Source.Rpc);
        }

        // ── Pack/unpack ─────────────────────────────────────────────────

        private static ZPackage Pack(Vector3 center, float radius, RegionScrubber.Scope scope, RegionScrubber.Source source)
        {
            var pkg = new ZPackage();
            pkg.Write(center);
            pkg.Write(radius);
            pkg.Write((int)scope);
            pkg.Write((int)source);
            return pkg;
        }

        private static void Unpack(ZPackage pkg, out Vector3 center, out float radius, out RegionScrubber.Scope scope, out RegionScrubber.Source source)
        {
            center = pkg.ReadVector3();
            radius = pkg.ReadSingle();
            scope = (RegionScrubber.Scope)pkg.ReadInt();
            source = (RegionScrubber.Source)pkg.ReadInt();
        }

        // FAT's one ZNet.Awake hook (FatVanillaHooks) calls ResetInitialized
        // then Register every session, so the handlers attach to the fresh
        // ZRoutedRpc instance (a sticky _registered would break sync).
    }
}
