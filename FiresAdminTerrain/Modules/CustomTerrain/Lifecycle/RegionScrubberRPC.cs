using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Lifecycle
{
    // Multiplayer sync for RegionScrubber operations.
    //
    // REQUEST PATTERN:
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
        private static ZRoutedRpc s_rpcOn;   // Also re-register on a new ZRoutedRpc (one per connection)

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
            if (ReferenceEquals(s_rpcOn, rpc)) return;   // instance identity only: a reset flag never re-registers on the same bus
            s_rpcOn = rpc;

            rpc.Register<ZPackage>(RPC_REQUEST_SCRUB, (sender, pkg) => RPC_HandleRequest(sender, pkg));
            rpc.Register<ZPackage>(RPC_APPLY_SCRUB,   (sender, pkg) => RPC_HandleApply(sender, pkg));
            rpc.Register<ZPackage>(RPC_REQUEST_BATCH, (sender, pkg) => RPC_HandleRequestBatch(sender, pkg));
            _registered = true;
            RegisteredHandlers = 3;
            s_queue.Clear();
            FiresLogger.LogVerbose("[RegionScrubberRPC] Registered 3 handlers (request + batch request + apply).");
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
            RegionScrubber.Stats.Sent++;
        }

        // ── 0.2.1060 batching (Fire's Deep North perf) ──────────────────
        // Automatic scrubs (vanilla terrain ops) queue here and go as ONE
        // batch REQUEST per BatchSeconds. A queued circle inside another
        // queued circle of the same scope is dropped. The server answers each
        // entry with the existing APPLY broadcast, so peers on an older FAT
        // still receive the applies; the batch REQUEST only goes to a server
        // that registered it (same FAT version, ConfigSync-checked).
        private const string RPC_REQUEST_BATCH = "FiresAdminTerrain_RegionScrub_RequestBatch";
        private const float BatchSeconds = 0.25f;
        internal const int BatchMax = 64;              // entries per batch (cap enforced on receive too)
        internal const float BatchMaxRadius = 128f;    // vanilla terrain ops are far smaller

        private struct Queued { public Vector3 Center; public float Radius; public RegionScrubber.Scope Scope; public RegionScrubber.Source Source; }
        private static readonly System.Collections.Generic.List<Queued> s_queue = new System.Collections.Generic.List<Queued>();
        private static float s_flushAt;
        private static GameObject s_runner;

        internal static void QueueRequest(Vector3 center, float radius, RegionScrubber.Scope scope, RegionScrubber.Source source)
        {
            for (int i = 0; i < s_queue.Count; i++)
            {
                var q = s_queue[i];
                if (q.Scope != scope) continue;
                float d = Vector2.Distance(new Vector2(q.Center.x, q.Center.z), new Vector2(center.x, center.z));
                if (d + radius <= q.Radius) { RegionScrubber.Stats.Duplicates++; return; }            // inside a queued one
                if (d + q.Radius <= radius) { s_queue[i] = new Queued { Center = center, Radius = radius, Scope = scope, Source = source }; RegionScrubber.Stats.Duplicates++; return; }
            }
            if (s_queue.Count == 0) s_flushAt = Time.unscaledTime + BatchSeconds;
            s_queue.Add(new Queued { Center = center, Radius = radius, Scope = scope, Source = source });
            if (s_queue.Count >= BatchMax) Flush();
            else EnsureRunner();
        }

        internal static void Flush()
        {
            if (s_queue.Count == 0) return;
            var rpc = ZRoutedRpc.instance;
            if (rpc == null) { s_queue.Clear(); return; }
            var pkg = new ZPackage();
            pkg.Write(s_queue.Count);
            foreach (var q in s_queue)
            {
                pkg.Write(q.Center);
                pkg.Write(q.Radius);
                pkg.Write((int)q.Scope);
                pkg.Write((int)q.Source);
            }
            rpc.InvokeRoutedRPC(ZRoutedRpc.Everybody, RPC_REQUEST_BATCH, pkg);
            RegionScrubber.Stats.Sent += s_queue.Count;
            RegionScrubber.Stats.Batches++;
            s_queue.Clear();
            RegionScrubber.Stats.MaybeReport();
        }

        private static void EnsureRunner()
        {
            if (s_runner != null) return;
            s_runner = new GameObject("FAT_RegionScrubBatch");
            Object.DontDestroyOnLoad(s_runner);
            s_runner.hideFlags = HideFlags.HideAndDontSave;
            s_runner.AddComponent<BatchRunner>();
        }

        private sealed class BatchRunner : MonoBehaviour
        {
            private void Update()
            {
                if (s_queue.Count > 0 && Time.unscaledTime >= s_flushAt) Flush();
            }
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
            RegionScrubber.ApplyLocally(center, radius, scope, RegionScrubber.Source.Rpc, source);

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

            RegionScrubber.ApplyLocally(center, radius, scope, RegionScrubber.Source.Rpc, source);
        }

        // 0.2.1060: a batch of automatic scrubs. Same rules as one REQUEST
        // (server only, admin only), plus caps: at most BatchMax entries,
        // finite centres, radius 0..BatchMaxRadius, only the scopes a vanilla
        // terrain op scrub uses; anything else rejects the whole batch.
        private static void RPC_HandleRequestBatch(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!AdminCheck.IsAdmin(sender))
            {
                FiresLogger.LogWarning($"[RegionScrubberRPC] Non-admin peer {sender} sent a scrub batch — rejected.");
                return;
            }
            const RegionScrubber.Scope allowed = RegionScrubber.Scope.VanillaBiome | RegionScrubber.Scope.Clutter | RegionScrubber.Scope.VanillaGrass;
            int count;
            var entries = new System.Collections.Generic.List<Queued>();
            try
            {
                count = pkg.ReadInt();
                if (count <= 0 || count > BatchMax) { FiresLogger.LogWarning($"[RegionScrubberRPC] scrub batch from peer {sender} rejected: {count} entries (max {BatchMax})."); return; }
                for (int i = 0; i < count; i++)
                {
                    Unpack(pkg, out var center, out var radius, out var scope, out var source);
                    bool finite = !float.IsNaN(center.x) && !float.IsInfinity(center.x) && !float.IsNaN(center.y) && !float.IsInfinity(center.y)
                        && !float.IsNaN(center.z) && !float.IsInfinity(center.z);
                    if (!finite || float.IsNaN(radius) || radius <= 0f || radius > BatchMaxRadius || (scope & ~allowed) != 0 || source != RegionScrubber.Source.VanillaTerrainOp)
                    {
                        FiresLogger.LogWarning($"[RegionScrubberRPC] scrub batch from peer {sender} rejected: bad entry {i} (r={radius}, scope={(int)scope}, src={(int)source}).");
                        return;
                    }
                    entries.Add(new Queued { Center = center, Radius = radius, Scope = scope, Source = source });
                }
            }
            catch (System.Exception ex)
            {
                FiresLogger.LogWarning($"[RegionScrubberRPC] scrub batch from peer {sender} unreadable ({ex.GetType().Name}) — rejected.");
                return;
            }
            foreach (var e in entries)
            {
                RegionScrubber.ApplyLocally(e.Center, e.Radius, e.Scope, RegionScrubber.Source.Rpc, e.Source);
                BroadcastApply(e.Center, e.Radius, e.Scope, e.Source);
            }
            FiresLogger.LogDebug($"[RegionScrubberRPC] BATCH REQUEST from peer {sender} approved: {entries.Count} scrub(s)");
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
