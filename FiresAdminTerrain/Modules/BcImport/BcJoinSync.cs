using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.BcImport
{
    // A converted BetterContinents world's terrain lives in FAT's migrated data on the SERVER's disk. A joining client
    // never had it, so it generated the world on vanilla heights under the server's objects: "a BC world loaded without
    // BC". This hands the data over the way BetterContinents did (its ZNetPatch): the server holds the client's PeerInfo,
    // streams its own file in packets with backpressure, and only then lets PeerInfo run, so the client's WorldGenerator
    // initialises with the data already on disk. The server's biome grid (BiomeGridSync, D12), its lakes, rivers and
    // streams (RiverSync, D13) and a studio-baked world's terrain cache and river flow field (D14) follow the same way.
    // Every item is content-addressed, so a returning client that already holds it transfers nothing, and the loading
    // screen says what is downloading or loading (JoinSyncScreen). On a client the server is the only authority: its
    // announcement decides, never a local copy of a same-named world. Tools/BC_CHUNK_CONVERSION.md (D5, D11-D14).
    internal static class BcJoinSync
    {
        internal enum ServerAnswer
        {
            NoAnswer,
            NotBetterContinents,
            Data,
            Failed,
        }

        private enum Item
        {
            BetterContinentsData,
            BiomeGrid,
            Rivers,
            Bake,
            BakeFlow,
            BakeCuts,   // 0.2.806: the bake's big waterfall sinkholes (firesgen_<world>_sinkfalls.txt)
        }

        // What each item is called in the log and on screen, its cache file extension, its two RPCs, and how many of its
        // files the cache keeps (a studio bake is hundreds of MB).
        private sealed class ItemKind
        {
            internal readonly string Name, ScreenName, Extension, CachedRpc, StartRpc;
            internal readonly int KeptInCache;

            internal ItemKind(string name, string screenName, string extension, string cachedRpc, string startRpc, int keptInCache)
            {
                Name = name;
                ScreenName = screenName;
                Extension = extension;
                CachedRpc = cachedRpc;
                StartRpc = startRpc;
                KeptInCache = keptInCache;
            }
        }

        private const int CachedFilesKept = 4;
        private const int CachedBakesKept = 2;

        private static readonly ItemKind[] Kinds =
        {
            new ItemKind("BetterContinents data", "terrain maps", ".bcdata", "FiresAdminTerrain_BcCached", "FiresAdminTerrain_BcStart", CachedFilesKept),
            new ItemKind("biome grid", "biome map", ".biomegrid", "FiresAdminTerrain_BcGridCached", "FiresAdminTerrain_BcGridStart", CachedFilesKept),
            new ItemKind("lake, river and stream lists", "river lists", ".rivers", "FiresAdminTerrain_BcRiversCached",
                "FiresAdminTerrain_BcRiversStart", CachedFilesKept),
            new ItemKind("studio terrain cache", "terrain cache", ".bake", "FiresAdminTerrain_BcBakeCached", "FiresAdminTerrain_BcBakeStart",
                CachedBakesKept),
            new ItemKind("studio river flow field", "river flow field", ".bakeflow", "FiresAdminTerrain_BcFlowCached",
                "FiresAdminTerrain_BcFlowStart", CachedBakesKept),
            new ItemKind("big waterfall sinkholes", "sinkholes", ".bakecuts", "FiresAdminTerrain_BcCutsCached",
                "FiresAdminTerrain_BcCutsStart", CachedBakesKept),
        };

        private const string RpcHave = "FiresAdminTerrain_BcHave";
        private const string RpcNone = "FiresAdminTerrain_BcNone";
        private const string RpcPacket = "FiresAdminTerrain_BcPacket";
        // Client to server: false at connection ("I read a bake on a worker and will say when it is done"), true once done.
        private const string RpcBakeRead = "FiresAdminTerrain_BcBakeRead";
        private const string PeerInfoRpc = "PeerInfo";
        // A client takes what its Steam receive buffer holds each frame (FGN's 256 KB on the rig), and only whole messages are
        // handed over, so 128 KB packets left it one a frame: R3, 131 KB a frame at any fps. Smaller packets fill the buffer.
        private const int PacketBytes = 32 * 1024;
        private const int SendWindowBytes = 1024 * 1024;
        // PlayFab's GetSendQueueSize is the UNACKNOWLEDGED bytes x 0.25 (ZPlayFabSocket), so the 1 MB window let ~4 MB sit
        // unacked; vanilla's retransmit then resent that whole queue every 6 s and R53's 151 MB crawled at ~0.3 MB/s
        // ([fgn], 2026-09-28). 32 KB here is ~128 KB in flight, the scale VAngarde's character upload uses.
        private const int PlayFabSendWindowBytes = 32 * 1024;
        private const long FrameBudgetMs = 4;
        private const float StallSeconds = 60f;
        private const int IdHexChars = 32;
        private const int ProgressSteps = 10;
        // Smaller downloads finish within a few frames; the loading screen only names the ones a player waits for.
        private const int ScreenFromBytes = 4 * 1024 * 1024;
        // A client whose bake read never reports must not be stranded at the loading screen: PeerInfo runs after this anyway.
        private const float BakeReadWaitCapSeconds = 60f;
        private const double MillisecondsPerSecond = 1000d;
        private const double BytesPerMegabyte = 1024d * 1024d;
        private const double BytesPerKilobyte = 1024d;
        private const string CacheFolderName = "bcsync";
        private const string PartExtension = ".part";
        private const string FlowExtension = ".flow";

        private static readonly Action<ZNet, ZRpc, ZPackage> RunPeerInfo =
            AccessTools.MethodDelegate<Action<ZNet, ZRpc, ZPackage>>(AccessTools.Method(typeof(ZNet), "RPC_PeerInfo"));

        private static readonly Dictionary<ZRpc, HashSet<string>> s_clientHolds = new Dictionary<ZRpc, HashSet<string>>();
        private static readonly Dictionary<ZRpc, bool> s_bakeReads = new Dictionary<ZRpc, bool>();
        private static ZRpc s_server;
        private static readonly Dictionary<string, Task<string>> s_hashes = new Dictionary<string, Task<string>>();
        private static string s_gridLoggedId;
        private static string s_riversLoggedId;

        private static readonly ServerAnswer[] s_answers = new ServerAnswer[Kinds.Length];
        private static readonly string[] s_answerIds = new string[Kinds.Length];
        private static Item s_receivingItem;
        private static string s_receivingId;
        private static int s_expected;
        private static int s_received;
        private static int s_nextProgressStep;
        private static float s_receiveStartedAt;
        private static int s_receiveFrames;
        private static int s_lastReceiveFrame;
        private static FileStream s_part;
        private static SHA256 s_partHash;

        private static string CacheFolder => Path.Combine(Voxel.FiresWorldRecords.Folder, CacheFolderName);
        private static ItemKind KindOf(Item item) => Kinds[(int)item];
        private static string NameOf(Item item) => KindOf(item).Name;
        private static string CachePath(string id, Item item) => Path.Combine(CacheFolder, id + KindOf(item).Extension);
        private static string PartPath(string id) => Path.Combine(CacheFolder, id + PartExtension);

        // FAT's one ZNet.OnNewConnection hook (ZoneModeHandshake) calls this for every connection, server and client. Every
        // client handler is registered here, at connection time, so nothing the server streams can arrive before it.
        internal static void OnNewConnection(ZNet net, ZNetPeer peer)
        {
            if (net == null || peer?.m_rpc == null) return;
            if (net.IsServer())
            {
                peer.m_rpc.Register<ZPackage>(RpcHave, (rpc, pkg) => s_clientHolds[rpc] = ReadIds(pkg));
                peer.m_rpc.Register<bool>(RpcBakeRead, (rpc, done) => s_bakeReads[rpc] = done);
                peer.m_rpc.Register<ZPackage>(PeerInfoRpc, (rpc, pkg) => HoldPeerInfoOrAdmit(net, rpc, pkg));
                return;
            }
            ResetClient();
            peer.m_rpc.Register(RpcNone, rpc => s_answers[(int)Item.BetterContinentsData] = ServerAnswer.NotBetterContinents);
            foreach (Item item in Enum.GetValues(typeof(Item)))
            {
                Item each = item;
                peer.m_rpc.Register<string>(KindOf(each).CachedRpc, (rpc, id) => OnCached(each, id));
                peer.m_rpc.Register<string, int>(KindOf(each).StartRpc, (rpc, id, length) => OnStart(each, id, length));
            }
            peer.m_rpc.Register<int, ZPackage>(RpcPacket, (rpc, offset, pkg) => OnPacket(offset, pkg));
            peer.m_rpc.Register<ZPackage>(PeerInfoRpc, (rpc, pkg) => RunPeerInfoTimed(net, rpc, pkg));
            s_server = peer.m_rpc;
            peer.m_rpc.Invoke(RpcBakeRead, false);
            peer.m_rpc.Invoke(RpcHave, CachedIds());
        }

        // Client: the server's PeerInfo builds the world on the main thread, so what it costs there is the freeze a joining
        // player sees ([perf]: 7.8 s in R25, most of it reading the bake, which a worker now does ahead of it).
        // A PeerInfo still queued when this game has already dropped the connection is not run: vanilla returns at once for
        // a peer that is gone, but other mods' postfixes still ran and threw on it (M1, PlayFab: Server Devcommands'
        // RegisterRPCs, GetPeer(rpc).m_server), which read as a join failure of its own.
        private static void RunPeerInfoTimed(ZNet net, ZRpc rpc, ZPackage peerInfo)
        {
            if (!IsConnected(rpc))
            {
                FiresLogger.LogWarning("[BcImport] join: the server's PeerInfo arrived after this game had already closed the connection; not run.");
                return;
            }
            var clock = System.Diagnostics.Stopwatch.StartNew();
            RunPeerInfo(net, rpc, peerInfo);
            long readMs = Voxel.BakePreload.AdoptedReadMs;
            FiresLogger.LogInfo(readMs >= 0
                ? $"[BcImport] join: adopted the preloaded world in {clock.ElapsedMilliseconds:N0} ms (the server's PeerInfo on the "
                    + $"main thread; the terrain cache was read on a worker in {readMs:N0} ms while the loading screen ran)."
                : $"[BcImport] join: the server's PeerInfo took {clock.ElapsedMilliseconds:N0} ms on the main thread.");
        }

        // Client, once the server's bake is settled (from this game's cache, downloaded, or dropped): a worker reads it now,
        // and the server, which waits for this before PeerInfo, hears when it is done, so the world adopts the read instead
        // of blocking the main thread on it. A server without this wait simply ignores the report.
        private static bool ReadsBakeAhead => Voxel.FiresVoxelConfig.BakeReadAhead == null || Voxel.FiresVoxelConfig.BakeReadAhead.Value;

        private static void ReadBakeAhead(ServerAnswer answer, string id)
        {
            if (answer == ServerAnswer.Data && ReadsBakeAhead) Voxel.BakePreload.Start(CachePath(id, Item.Bake));
            ZRpc server = s_server;
            if (server != null && ZNet.instance != null) ZNet.instance.StartCoroutine(ReportBakeReadWhenDone(server));
        }

        private static IEnumerator ReportBakeReadWhenDone(ZRpc server)
        {
            while (Voxel.BakePreload.Running) yield return null;
            if (IsConnected(server)) server.Invoke(RpcBakeRead, true);
        }

        // ── Server ──────────────────────────────────────────────────────────
        // Server, as a studio bake loads: its hash starts on a worker at once, so the first joining client does not wait for
        // hundreds of MB to be hashed (R22: Core hashed the 377 MB bake on the main thread at every join, 3 s).
        internal static void Prehash(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            HashOf(path);
            string flow = Path.ChangeExtension(path, FlowExtension);
            if (File.Exists(flow)) HashOf(flow);
            string cuts = CutsBeside(path);
            if (cuts != null && File.Exists(cuts)) HashOf(cuts);
        }

        // 0.2.806: a bake's big waterfall sinkholes sit beside it as firesgen_<world>_sinkfalls.txt (FatSinkFallCuts).
        private static string CutsBeside(string bake)
        {
            const string bakedSuffix = "_baked.bin";
            return bake != null && bake.EndsWith(bakedSuffix, StringComparison.OrdinalIgnoreCase)
                ? bake.Substring(0, bake.Length - bakedSuffix.Length) + Voxel.FatSinkFallCuts.Suffix : null;
        }

        // Every join passes through here, so a fault in the sync must never strand a player at "Connecting". PeerInfo runs
        // exactly once: here when the sync has nothing to send, from SendThenAdmit when it streams, never after a refusal.
        private static void HoldPeerInfoOrAdmit(ZNet net, ZRpc rpc, ZPackage peerInfo)
        {
            bool admitNow;
            try { admitNow = !TryHold(net, rpc, peerInfo); }
            catch (Exception ex)
            {
                FiresLogger.LogError($"[BcImport] join sync failed for {Describe(rpc)} ({ex.GetType().Name}: {ex.Message}); "
                    + "letting it join without the terrain data.");
                admitNow = true;
            }
            if (admitNow) RunPeerInfo(net, rpc, peerInfo);
        }

        // True when the join is now owned by the sync (streaming, or refused); false when PeerInfo should run straight away.
        private static bool TryHold(ZNet net, ZRpc rpc, ZPackage peerInfo)
        {
            bool clientCanReceive = s_clientHolds.TryGetValue(rpc, out HashSet<string> holds);
            s_clientHolds.Remove(rpc);
            ForgetGonePeers();
            BcSaveLocation? data = BcMigratedWorld.ServerDataLocation;
            Task<BiomeGridSync.Blob> grid = BiomeGridSync.ServerBlob;
            RiverSync.Blob rivers = RiverSync.ServerBlob;
            string bake = Voxel.FiresWorldGenBase.LoadedBakePath;
            if (bake == null) s_bakeReads.Remove(rpc);
            if (data == null && grid == null && rivers == null && bake == null)
            {
                if (clientCanReceive) rpc.Invoke(RpcNone);
                return false;
            }
            if (!clientCanReceive)
            {
                if (data != null || bake != null)
                    FiresLogger.LogWarning($"[BcImport] join sync: {Describe(rpc)} runs a FAT without this join sync, so it gets no "
                        + "terrain data and will draw this world differently from the server.");
                return false;
            }
            if (data != null && data.Value.Source != FileHelpers.FileSource.Local)
            {
                FiresLogger.LogError($"[BcImport] join sync: this world's BetterContinents data is in {data.Value.Source} storage "
                    + $"({data.Value.Path}), which cannot be streamed, so {Describe(rpc)} was turned away rather than shown vanilla "
                    + "terrain. Move the world to local storage to host it.");
                Refuse(net, rpc);
                return true;
            }
            net.StartCoroutine(SendThenAdmit(net, rpc, peerInfo, data?.Path, grid, rivers, bake, holds));
            return true;
        }

        private static void ForgetGonePeers()
        {
            foreach (ZRpc gone in s_clientHolds.Keys.Where(rpc => !IsConnected(rpc)).ToList()) s_clientHolds.Remove(gone);
            foreach (ZRpc gone in s_bakeReads.Keys.Where(rpc => !IsConnected(rpc)).ToList()) s_bakeReads.Remove(gone);
        }

        // The BetterContinents data first (or "none"), then the biome grid, the lake, river and stream lists, a studio bake and
        // its flow field, then PeerInfo, all on the one ordered connection. A grid or list that could not be prepared is simply
        // not sent: the client builds its own. Terrain a client cannot build (BC data, a bake) that fails to send turns the
        // client away rather than letting it play different ground from the server's.
        private static IEnumerator SendThenAdmit(ZNet net, ZRpc rpc, ZPackage peerInfo, string path, Task<BiomeGridSync.Blob> grid,
            RiverSync.Blob rivers, string bake, HashSet<string> holds)
        {
            var outcome = new SendOutcome();
            if (path == null) rpc.Invoke(RpcNone);
            else
            {
                yield return net.StartCoroutine(SendFile(net, rpc, Item.BetterContinentsData, path, holds, outcome));
                if (!outcome.Ok) yield break;
            }
            if (grid != null)
            {
                while (!grid.IsCompleted) yield return null;
                BiomeGridSync.Blob blob = grid.IsFaulted ? null : grid.Result;
                if (blob == null) LogGridOnce(null, grid.Exception?.GetBaseException().Message);
                else
                {
                    LogGridOnce(blob, null);
                    yield return net.StartCoroutine(SendItem(rpc, Item.BiomeGrid, blob.Id, () => new MemoryStream(blob.Data, false), holds, outcome));
                    if (!Continue(net, rpc, outcome)) yield break;
                }
            }
            if (rivers != null)
            {
                LogRiversOnce(rivers);
                yield return net.StartCoroutine(SendItem(rpc, Item.Rivers, rivers.Id, () => new MemoryStream(rivers.Data, false), holds, outcome));
                if (!Continue(net, rpc, outcome)) yield break;
            }
            if (bake != null)
            {
                yield return net.StartCoroutine(SendFile(net, rpc, Item.Bake, bake, holds, outcome));
                if (!outcome.Ok) yield break;
                string flow = Path.ChangeExtension(bake, FlowExtension);
                if (File.Exists(flow))
                {
                    yield return net.StartCoroutine(SendFile(net, rpc, Item.BakeFlow, flow, holds, outcome));
                    if (!outcome.Ok) yield break;
                }
                string cuts = CutsBeside(bake);
                if (cuts != null && File.Exists(cuts))
                {
                    yield return net.StartCoroutine(SendFile(net, rpc, Item.BakeCuts, cuts, holds, outcome));
                    if (!outcome.Ok) yield break;
                }
                yield return net.StartCoroutine(AwaitBakeRead(rpc));
            }
            if (IsConnected(rpc)) RunPeerInfo(net, rpc, peerInfo);
        }

        // PeerInfo is what builds the client's world, and a vanilla client handles what the server sends after it only once it
        // has run, so the client cannot hold it itself; the server holds it here until the client's worker has read the bake
        // (ReadBakeAhead), which the loading screen shows. A client that never announced the report is not waited for.
        private static IEnumerator AwaitBakeRead(ZRpc rpc)
        {
            if (!ReadsBakeAhead || !s_bakeReads.ContainsKey(rpc))
            {
                s_bakeReads.Remove(rpc);
                yield break;
            }
            float started = Time.realtimeSinceStartup;
            while (IsConnected(rpc) && !BakeReadDone(rpc) && Time.realtimeSinceStartup - started < BakeReadWaitCapSeconds) yield return null;
            bool done = BakeReadDone(rpc);
            s_bakeReads.Remove(rpc);
            if (!IsConnected(rpc)) yield break;
            long waitedMs = (long)((Time.realtimeSinceStartup - started) * MillisecondsPerSecond);
            if (done)
                FiresLogger.LogInfo($"[BcImport] join sync: {Describe(rpc)} read the terrain cache on a worker; PeerInfo waited "
                    + $"{waitedMs:N0} ms for it, so its main thread only adopts the result.");
            else
                FiresLogger.LogWarning($"[BcImport] join sync: {Describe(rpc)} did not report its terrain cache read within "
                    + $"{BakeReadWaitCapSeconds:0} s; letting PeerInfo run (the client finishes the read on its main thread).");
        }

        private static bool BakeReadDone(ZRpc rpc) => s_bakeReads.TryGetValue(rpc, out bool done) && done;

        // A file on the server's disk: hashed on a worker (kept per file and write time), then sent like any item. Refuses the
        // client when it cannot be read or sent; outcome.Ok says the client has it.
        private static IEnumerator SendFile(ZNet net, ZRpc rpc, Item item, string path, HashSet<string> holds, SendOutcome outcome)
        {
            outcome.Ok = false;
            outcome.Gone = false;
            Task<string> hash = HashOf(path);
            while (!hash.IsCompleted) yield return null;
            if (hash.IsFaulted || string.IsNullOrEmpty(hash.Result))
            {
                FiresLogger.LogError($"[BcImport] join sync: {path} could not be read ({hash.Exception?.GetBaseException().Message}), "
                    + $"so {Describe(rpc)} was turned away rather than shown different terrain.");
                Refuse(net, rpc);
                yield break;
            }
            yield return net.StartCoroutine(SendItem(rpc, item, hash.Result, () => TryOpen(path, rpc), holds, outcome));
            if (!outcome.Gone && !outcome.Ok) Refuse(net, rpc);
        }

        // After an item the join goes on unless the client left or the send failed (then it is turned away).
        private static bool Continue(ZNet net, ZRpc rpc, SendOutcome outcome)
        {
            if (outcome.Gone) return false;
            if (outcome.Ok) return true;
            Refuse(net, rpc);
            return false;
        }

        private static void LogRiversOnce(RiverSync.Blob rivers)
        {
            if (s_riversLoggedId == rivers.Id) return;
            s_riversLoggedId = rivers.Id;
            FiresLogger.LogInfo($"[BcImport] join sync: this world's {RiverSync.Describe(rivers)} ({rivers.Id}) go to every joining client "
                + "that lacks them.");
        }

        private static void LogGridOnce(BiomeGridSync.Blob blob, string failure)
        {
            string key = blob?.Id ?? "failed";
            if (s_gridLoggedId == key) return;
            s_gridLoggedId = key;
            if (blob == null)
                FiresLogger.LogWarning($"[BcImport] join sync: this world's biome grid could not be prepared ({failure}), so joining "
                    + "clients build their own.");
            else
                FiresLogger.LogInfo($"[BcImport] join sync: this world's {BiomeGridSync.Describe(blob)} ({blob.Id}) goes to every joining "
                    + "client that lacks it.");
        }

        private sealed class SendOutcome
        {
            internal bool Ok;
            internal bool Gone;
        }

        // One item: "cached" when the client already holds this content, else streamed under backpressure within a small
        // per-frame budget. Ok = the client has it; Gone = the client left; neither = the send failed (logged).
        private static IEnumerator SendItem(ZRpc rpc, Item item, string id, Func<Stream> open, HashSet<string> holds, SendOutcome outcome)
        {
            outcome.Ok = false;
            outcome.Gone = false;
            if (!IsConnected(rpc))
            {
                outcome.Gone = true;
                yield break;
            }
            if (holds.Contains(id))
            {
                rpc.Invoke(KindOf(item).CachedRpc, id);
                FiresLogger.LogInfo($"[BcImport] join sync: {Describe(rpc)} already holds {NameOf(item)} {id}; nothing sent.");
                outcome.Ok = true;
                yield break;
            }

            var clock = System.Diagnostics.Stopwatch.StartNew();
            Stream opened = open();
            if (opened == null) yield break;
            using (Stream source = opened)
            {
                int length = (int)source.Length;
                rpc.Invoke(KindOf(item).StartRpc, id, length);
                var buffer = new byte[PacketBytes];
                var frame = new System.Diagnostics.Stopwatch();
                var pace = new SendPace();
                int sent = 0;
                float lastMove = Time.realtimeSinceStartup;
                while (sent < length)
                {
                    if (!IsConnected(rpc))
                    {
                        outcome.Gone = true;
                        yield break;
                    }
                    ISocket socket = rpc.GetSocket();
                    int window = socket is ZPlayFabSocket ? PlayFabSendWindowBytes : SendWindowBytes;
                    frame.Restart();
                    int sentBefore = sent;
                    bool sourceEnded = false;
                    pace.QueueAtFrameStart += socket.GetSendQueueSize();
                    while (sent < length && socket.GetSendQueueSize() < window && frame.ElapsedMilliseconds < FrameBudgetMs)
                    {
                        int read = source.Read(buffer, 0, Math.Min(PacketBytes, length - sent));
                        if (read <= 0)
                        {
                            sourceEnded = true;
                            break;
                        }
                        rpc.Invoke(RpcPacket, sent, new ZPackage(read == buffer.Length ? buffer : Head(buffer, read)));
                        try { socket.Flush(); }
                        catch (NotImplementedException) { }
                        sent += read;
                    }
                    pace.Count(sent < length && socket.GetSendQueueSize() >= window, frame.ElapsedMilliseconds >= FrameBudgetMs);
                    if (sent > sentBefore) lastMove = Time.realtimeSinceStartup;
                    if (sourceEnded || Time.realtimeSinceStartup - lastMove > StallSeconds)
                    {
                        FiresLogger.LogError($"[BcImport] join sync: {NameOf(item)} stopped at {sent / BytesPerMegabyte:0.#} of "
                            + $"{length / BytesPerMegabyte:0.#} MB for {Describe(rpc)} (the source ended early or the client took nothing "
                            + $"for {StallSeconds:0} s); disconnected.");
                        yield break;
                    }
                    yield return null;
                }
                double seconds = Math.Max(clock.Elapsed.TotalSeconds, double.Epsilon);
                FiresLogger.LogInfo($"[BcImport] join sync: sent {NameOf(item)} {id} ({sent / BytesPerMegabyte:0.#} MB) to {Describe(rpc)} in "
                    + $"{seconds:0.#} s ({sent / BytesPerMegabyte / seconds:0.#} MB/s). {pace.Describe(sent)}");
            }
            outcome.Ok = true;
        }

        // What held each frame of a send back: a full send queue means the connection (Steam's rate, or a client taking
        // packets slowly) set the pace; the frame budget means this loop did. Tells which one to raise.
        private sealed class SendPace
        {
            private const double PercentScale = 100d;

            internal long QueueAtFrameStart;
            private int _frames, _queueBound, _budgetBound;

            internal void Count(bool queueFull, bool budgetSpent)
            {
                _frames++;
                if (queueFull) _queueBound++;
                else if (budgetSpent) _budgetBound++;
            }

            internal string Describe(int sent)
            {
                int frames = Math.Max(_frames, 1);
                return $"Pace: {_frames} frame(s), {sent / BytesPerKilobyte / frames:0} KB a frame; {_queueBound * PercentScale / frames:0}% "
                    + $"held by a full send queue (the connection's pace), {_budgetBound * PercentScale / frames:0}% by the {FrameBudgetMs} ms "
                    + $"frame budget (this loop's pace); queue at frame start {QueueAtFrameStart / BytesPerKilobyte / frames:0} KB on average.";
            }
        }

        private static byte[] Head(byte[] buffer, int count)
        {
            var head = new byte[count];
            Buffer.BlockCopy(buffer, 0, head, 0, count);
            return head;
        }

        private static Stream TryOpen(string path, ZRpc rpc)
        {
            try { return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, PacketBytes); }
            catch (Exception ex)
            {
                FiresLogger.LogError($"[BcImport] join sync: {path} could not be opened ({ex.GetType().Name}: {ex.Message}), so "
                    + $"{Describe(rpc)} was turned away rather than shown different terrain.");
                return null;
            }
        }

        // Kept per file, size and write time, one worker hash each: a load that re-stores the maps in bands
        // (BcWorldData.StoreInBands) changes the file, and a bake is hashed once however many clients join.
        private static Task<string> HashOf(string path)
        {
            var info = new FileInfo(path);
            string stamp = path + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks;
            if (s_hashes.TryGetValue(stamp, out Task<string> hash) && !hash.IsFaulted) return hash;
            foreach (string stale in s_hashes.Keys.Where(key => key.StartsWith(path + "|", StringComparison.Ordinal)).ToList())
                s_hashes.Remove(stale);
            hash = Task.Run(() =>
            {
                using (var sha = SHA256.Create())
                using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, PacketBytes))
                    return Hex(sha.ComputeHash(file));
            });
            s_hashes[stamp] = hash;
            return hash;
        }

        /// <summary>The content id the join sync gives these bytes (BiomeGridSync names its blob with it).</summary>
        internal static string IdOf(byte[] data)
        {
            using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(data));
        }

        private static void Refuse(ZNet net, ZRpc rpc)
        {
            foreach (ZNetPeer peer in net.GetPeers())
            {
                if (peer.m_rpc != rpc) continue;
                net.Disconnect(peer);
                return;
            }
        }

        private static bool IsConnected(ZRpc rpc) => rpc?.GetSocket() != null && rpc.GetSocket().IsConnected();

        private static string Describe(ZRpc rpc) => rpc?.GetSocket()?.GetHostName() ?? "a client";

        // ── Client ──────────────────────────────────────────────────────────
        internal static ServerAnswer Answer(out string cachedPath) => AnswerFor(Item.BetterContinentsData, out cachedPath);

        internal static ServerAnswer BiomeGridAnswer(out string cachedPath) => AnswerFor(Item.BiomeGrid, out cachedPath);

        internal static ServerAnswer RiversAnswer(out string cachedPath) => AnswerFor(Item.Rivers, out cachedPath);

        internal static ServerAnswer BakeAnswer(out string cachedPath) => AnswerFor(Item.Bake, out cachedPath);

        internal static ServerAnswer BakeFlowAnswer(out string cachedPath) => AnswerFor(Item.BakeFlow, out cachedPath);
        internal static ServerAnswer BakeCutsAnswer(out string cachedPath) => AnswerFor(Item.BakeCuts, out cachedPath);

        private static ServerAnswer AnswerFor(Item item, out string cachedPath)
        {
            var answer = s_answers[(int)item];
            cachedPath = answer == ServerAnswer.Data ? CachePath(s_answerIds[(int)item], item) : null;
            return answer;
        }

        private static void ResetClient()
        {
            ClosePart(true);
            for (int i = 0; i < s_answers.Length; i++)
            {
                s_answers[i] = ServerAnswer.NoAnswer;
                s_answerIds[i] = null;
            }
            s_server = null;
            Voxel.BakePreload.Reset();
            JoinSyncScreen.Clear();
        }

        private static void SetAnswer(Item item, ServerAnswer answer, string id)
        {
            s_answers[(int)item] = answer;
            s_answerIds[(int)item] = id;
            if (item == Item.Bake) ReadBakeAhead(answer, id);
        }

        // Between items, and once they are all in: what the player waits for next is this PC loading what arrived (the bake
        // blocks for seconds inside PeerInfo), so the loading screen says so before that starts; JoinSyncScreen is cleared
        // once the world generator has initialised (FatVanillaHooks).
        private static void ShowWhatLoadsNext()
        {
            if (s_answers[(int)Item.Bake] == ServerAnswer.Data) JoinSyncScreen.ShowLoading(KindOf(Item.Bake).ScreenName);
            else if (s_answers[(int)Item.BetterContinentsData] == ServerAnswer.Data)
                JoinSyncScreen.ShowLoading(KindOf(Item.BetterContinentsData).ScreenName);
            else JoinSyncScreen.Clear();
        }

        private static void OnCached(Item item, string id)
        {
            string path = CachePath(id, item);
            if (!File.Exists(path))
            {
                SetAnswer(item, ServerAnswer.Failed, null);
                FiresLogger.LogError($"[BcImport] join sync: the server says this game holds its {NameOf(item)} {id}, but {path} is gone.");
                ShowWhatLoadsNext();
                return;
            }
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            SetAnswer(item, ServerAnswer.Data, id);
            FiresLogger.LogInfo($"[BcImport] join sync: the server's {NameOf(item)} {id} comes from this game's cache.");
            ShowWhatLoadsNext();
        }

        private static void OnStart(Item item, string id, int length)
        {
            ClosePart(true);
            Directory.CreateDirectory(CacheFolder);
            s_receivingItem = item;
            s_receivingId = id;
            s_expected = length;
            s_received = 0;
            s_nextProgressStep = 1;
            s_receiveStartedAt = Time.realtimeSinceStartup;
            s_receiveFrames = 0;
            s_lastReceiveFrame = -1;
            s_part = new FileStream(PartPath(id), FileMode.Create, FileAccess.Write, FileShare.None, PacketBytes);
            s_partHash = SHA256.Create();
            FiresLogger.LogInfo($"[BcImport] join sync: receiving the server's {NameOf(item)} {id} "
                + $"({length / BytesPerMegabyte:0.#} MB); it is kept, so this happens once per content.");
            if (length >= ScreenFromBytes) JoinSyncScreen.ShowDownload(KindOf(item).ScreenName, 0, length, 0f);
        }

        private static void OnPacket(int offset, ZPackage pkg)
        {
            if (s_part == null || offset != s_received)
            {
                FiresLogger.LogError($"[BcImport] join sync: packet at {offset} does not follow {s_received}; the data is dropped.");
                Item item = s_receivingItem;
                ClosePart(true);
                SetAnswer(item, ServerAnswer.Failed, null);
                ShowWhatLoadsNext();
                return;
            }
            if (Time.frameCount != s_lastReceiveFrame)
            {
                s_lastReceiveFrame = Time.frameCount;
                s_receiveFrames++;
            }
            byte[] bytes = pkg.GetArray();
            s_part.Write(bytes, 0, bytes.Length);
            s_partHash.TransformBlock(bytes, 0, bytes.Length, null, 0);
            s_received += bytes.Length;
            if (s_expected >= ScreenFromBytes)
                JoinSyncScreen.ShowDownload(KindOf(s_receivingItem).ScreenName, s_received, s_expected,
                    Time.realtimeSinceStartup - s_receiveStartedAt);
            if (s_expected > 0 && s_received * ProgressSteps >= s_nextProgressStep * (long)s_expected && s_received < s_expected)
            {
                FiresLogger.LogInfo($"[BcImport] join sync: {s_received / BytesPerMegabyte:0.#} of {s_expected / BytesPerMegabyte:0.#} MB.");
                s_nextProgressStep = (int)((long)s_received * ProgressSteps / s_expected) + 1;
            }
            if (s_received >= s_expected) FinishPart();
        }

        private static void FinishPart()
        {
            string id = s_receivingId;
            Item item = s_receivingItem;
            s_partHash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            string received = Hex(s_partHash.Hash);
            ClosePart(false);
            if (received != id)
            {
                File.Delete(PartPath(id));
                SetAnswer(item, ServerAnswer.Failed, null);
                FiresLogger.LogError($"[BcImport] join sync: the {NameOf(item)} arrived as {received}, not {id}; it is dropped.");
                ShowWhatLoadsNext();
                return;
            }
            string final = CachePath(id, item);
            if (File.Exists(final)) File.Delete(final);
            File.Move(PartPath(id), final);
            SetAnswer(item, ServerAnswer.Data, id);
            float seconds = Mathf.Max(Time.realtimeSinceStartup - s_receiveStartedAt, float.Epsilon);
            int frames = Math.Max(s_receiveFrames, 1);
            FiresLogger.LogInfo($"[BcImport] join sync: received {NameOf(item)} {id} ({s_received / BytesPerMegabyte:0.#} MB), verified, in "
                + $"{seconds:0.#} s over {s_receiveFrames} frame(s): {s_received / BytesPerMegabyte / seconds:0.#} MB/s, "
                + $"{s_received / BytesPerKilobyte / frames:0} KB a frame, {s_receiveFrames / seconds:0} frames a second while receiving.");
            PruneCache(id, item);
            ShowWhatLoadsNext();
        }

        private static void ClosePart(bool deletePart)
        {
            s_part?.Dispose();
            s_part = null;
            s_partHash?.Dispose();
            s_partHash = null;
            if (deletePart && s_receivingId != null && File.Exists(PartPath(s_receivingId))) File.Delete(PartPath(s_receivingId));
            if (deletePart) s_receivingId = null;
        }

        private static void PruneCache(string keep, Item item)
        {
            var stale = new DirectoryInfo(CacheFolder).GetFiles("*" + KindOf(item).Extension)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Skip(KindOf(item).KeptInCache)
                .Where(file => Path.GetFileNameWithoutExtension(file.Name) != keep);
            foreach (FileInfo file in stale) file.Delete();
        }

        private static ZPackage CachedIds()
        {
            var pkg = new ZPackage();
            string[] ids = Directory.Exists(CacheFolder)
                ? Kinds.SelectMany(kind => Directory.GetFiles(CacheFolder, "*" + kind.Extension)).Select(Path.GetFileNameWithoutExtension).ToArray()
                : Array.Empty<string>();
            pkg.Write(ids.Length);
            foreach (string id in ids) pkg.Write(id);
            return pkg;
        }

        private static HashSet<string> ReadIds(ZPackage pkg)
        {
            var ids = new HashSet<string>();
            int count = pkg.ReadInt();
            for (int i = 0; i < count; i++) ids.Add(pkg.ReadString());
            return ids;
        }

        private static string Hex(byte[] digest)
            => BitConverter.ToString(digest).Replace("-", "").Substring(0, IdHexChars).ToLowerInvariant();
    }
}
