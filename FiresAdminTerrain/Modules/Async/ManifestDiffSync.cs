using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace VerdantsAscent.Async
{
    /// <summary>
    /// Generic manifest-diff sync primitive - "don't re-send what the peer already has".
    ///
    /// THE PRINCIPLE
    /// -------------
    /// Whenever the server fan-outs a batch of files to a joining client (cfg packs,
    /// saved NPC JSON, quest databases, territory blobs, UI layouts, sprite assets,
    /// anything file-backed), the naive implementation re-ships every byte on every
    /// login. That both wastes Steam bandwidth AND multiplies the risk of tripping the
    /// 512 KB per-message cap when Steam's send queue concatenates adjacent packages.
    ///
    /// <see cref="UIBuilder.UILayoutSyncRPC"/> already proves the right shape:
    ///  1. Server on peer-validated -> <see cref="RequestFromPeer"/>("UILayouts").
    ///  2. Client receives -> enumerates its local folder for that namespace,
    ///     replies with {relativePath =&gt; sha256}.
    ///  3. Server caches the manifest per (peer, namespace) and, while looping over
    ///     its own files, simply skips any whose local hash matches the peer's.
    ///
    /// This helper generalises that pattern so every sync pipeline plugs into the same
    /// two routed-RPCs instead of re-inventing the handshake. One pair of RPCs covers
    /// every namespace because the namespace key is carried in the package payload.
    ///
    /// USAGE
    /// -----
    ///  * At mod init (BOTH sides - the client is the responder, the server is the
    ///    requester, and a listen-server is both):
    ///        <c>ManifestDiffSync.EnsureRegistered();</c>
    ///        <c>ManifestDiffSync.RegisterFolder("SavedNPCs",
    ///            () =&gt; SavedNpcsFolder, "*.json", SearchOption.TopDirectoryOnly);</c>
    ///
    ///  * At peer-join on the server:
    ///        <c>ManifestDiffSync.RequestFromPeer(peerUid, "SavedNPCs");</c>
    ///        <c>yield return ManifestDiffSync.WaitForManifest(peerUid, "SavedNPCs", 3f);</c>
    ///
    ///  * Before sending a file:
    ///        <c>if (ManifestDiffSync.HasSameHash(peerUid, "SavedNPCs", relPath, localHash)) continue;</c>
    ///
    ///  * On peer disconnect (optional cleanup):
    ///        <c>ManifestDiffSync.ForgetPeer(peerUid);</c>
    ///
    /// The helper never touches the file payload itself - it only brokers the
    /// "does the peer already have this?" question. The caller is still responsible
    /// for the actual content RPC, chunking, and applying on the receiver side.
    /// </summary>
    public static class ManifestDiffSync
    {
        private const string RpcRequest = "FiresRPGmaker_Manifest_Request";
        private const string RpcReply = "FiresRPGmaker_Manifest_Reply";

        private sealed class FolderRegistration
        {
            public Func<string> FolderProvider;
            public string SearchPattern;
            public SearchOption Search;
        }

        private sealed class HashProviderRegistration
        {
            public Func<string> HashProvider;
        }

        private static readonly object _lock = new object();
        private static readonly Dictionary<string, FolderRegistration> _registered =
            new Dictionary<string, FolderRegistration>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, HashProviderRegistration> _hashProviders =
            new Dictionary<string, HashProviderRegistration>(StringComparer.OrdinalIgnoreCase);
        // key: (peerUid, namespace)  -> manifest dict (relativePath -> sha256 hex, lowercase)
        private static readonly Dictionary<ManifestKey, Dictionary<string, string>> _peerManifests =
            new Dictionary<ManifestKey, Dictionary<string, string>>();
        private static bool _rpcsRegistered;

        private readonly struct ManifestKey : IEquatable<ManifestKey>
        {
            public readonly long PeerUid;
            public readonly string Namespace;
            public ManifestKey(long uid, string ns) { PeerUid = uid; Namespace = ns ?? string.Empty; }
            public bool Equals(ManifestKey other) => PeerUid == other.PeerUid &&
                string.Equals(Namespace, other.Namespace, StringComparison.OrdinalIgnoreCase);
            public override bool Equals(object obj) => obj is ManifestKey k && Equals(k);
            public override int GetHashCode()
            {
                unchecked
                {
                    return (PeerUid.GetHashCode() * 397) ^
                        StringComparer.OrdinalIgnoreCase.GetHashCode(Namespace);
                }
            }
        }

        /// <summary>
        /// Idempotent registration of the two shared routed RPCs. Safe to call from
        /// anywhere; returns early if the RPC layer isn't ready yet (caller should
        /// retry at next convenient hook - e.g. <c>ZNet.Awake</c> postfix).
        ///
        /// Each <c>Register</c> is wrapped individually: if one RPC name collides
        /// with something already in <c>ZRoutedRpc.m_functions</c> (rare, but
        /// observed when another subsystem has stable-hashed the same string) we
        /// still bind whatever else we can instead of silently leaving both handlers
        /// unregistered - which would make every future manifest query time out and
        /// defeat the entire diff-sync system.
        /// </summary>
        public static void EnsureRegistered()
        {
            if (_rpcsRegistered) return;
            if (ZRoutedRpc.instance == null) return;

            TryRegisterRpc(RpcRequest, RPC_OnRequest);
            TryRegisterRpc(RpcReply, RPC_OnReply);
            _rpcsRegistered = true;
        }

        private static void TryRegisterRpc(string rpcName, Action<long, ZPackage> handler)
        {
            try
            {
                ZRoutedRpc.instance.Register<ZPackage>(rpcName, handler);
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[ManifestDiffSync] Register('{rpcName}') failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Register a folder that can be manifest-diffed under <paramref name="namespaceKey"/>.
        /// The <paramref name="folderProvider"/> delegate is resolved lazily at request time
        /// so <c>BepInEx.Paths.ConfigPath</c> and similar runtime values are safe to use.
        /// Re-registering the same namespace overwrites the previous entry.
        /// </summary>
        /// <param name="namespaceKey">Stable identifier shared by server + client. Case-insensitive.</param>
        /// <param name="folderProvider">Callback returning the absolute folder path. May return null/empty if the folder doesn't exist yet.</param>
        /// <param name="searchPattern">File pattern (e.g. <c>"*.json"</c>, <c>"*.cfg"</c>).</param>
        /// <param name="searchOption">Whether to recurse into subfolders.</param>
        public static void RegisterFolder(string namespaceKey, Func<string> folderProvider,
            string searchPattern, SearchOption searchOption)
        {
            if (string.IsNullOrEmpty(namespaceKey) || folderProvider == null) return;
            lock (_lock)
            {
                _registered[namespaceKey] = new FolderRegistration
                {
                    FolderProvider = folderProvider,
                    SearchPattern = string.IsNullOrEmpty(searchPattern) ? "*" : searchPattern,
                    Search = searchOption,
                };
            }
        }

        /// <summary>
        /// Blob-style registration: the namespace represents a single opaque payload
        /// (e.g. the compiled quest database, a territory snapshot) rather than a folder
        /// of files. The client responds to manifest queries with a <b>single</b> hash
        /// entry keyed by empty-string whose value comes from <paramref name="hashProvider"/>.
        ///
        /// Typical pattern: the client persists the SHA-256 of the last-received blob to
        /// disk via <see cref="RememberBlobHash"/> inside the content RPC handler, and the
        /// provider delegate reads that cached value back with <see cref="GetPersistedBlobHash"/>.
        /// That way, a client that already has the current build of the data on disk from
        /// a previous session doesn't receive it again.
        ///
        /// On the server, query with <see cref="PeerHasBlob"/> (or <see cref="HasSameHash"/>
        /// with <c>relativePath=""</c>) passing the server-side hash of what it would have
        /// sent.
        /// </summary>
        public static void RegisterHashProvider(string namespaceKey, Func<string> hashProvider)
        {
            if (string.IsNullOrEmpty(namespaceKey) || hashProvider == null) return;
            lock (_lock)
            {
                _hashProviders[namespaceKey] = new HashProviderRegistration { HashProvider = hashProvider };
            }
        }

        // ----- Persistent blob hash cache (on-disk) -----
        //
        // Blob-style namespaces (QuestDatabase, Territories) aren't backed by a folder we
        // can enumerate, but the client still needs to remember across sessions what
        // content it last received so future logins can skip the full resend when nothing
        // changed on the server. We stash one tiny file per namespace under
        // BepInEx/config/FiresAdminTerrain/cache/manifests/<key>.hash - each file is exactly
        // 64 hex chars of SHA-256, so the whole cache is a handful of bytes even across
        // dozens of namespaces.
        //
        // The cache is intentionally separate from the folder-registration mechanism
        // because (a) some call sites may want to drive the hash from an in-memory
        // snapshot (session-only), and (b) keeping the I/O behind an opt-in helper keeps
        // ManifestDiffSync safe to use in unit tests that can't create config dirs.

        private static string BlobCacheRoot =>
            Path.Combine(BepInEx.Paths.ConfigPath, "FiresAdminTerrain", "cache", "manifests");

        private static string BlobCachePath(string namespaceKey) =>
            Path.Combine(BlobCacheRoot, SanitizeKey(namespaceKey) + ".hash");

        private static string SanitizeKey(string namespaceKey)
        {
            if (string.IsNullOrEmpty(namespaceKey)) return "_";
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(namespaceKey.Length);
            for (int i = 0; i < namespaceKey.Length; i++)
            {
                char c = namespaceKey[i];
                sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Persist the SHA-256 of a blob the client just received so future manifest
        /// queries for <paramref name="namespaceKey"/> report it. Safe to call from any
        /// thread; silent-no-ops on I/O failure (we never want a manifest cache write
        /// to break the user's session).
        /// </summary>
        public static void RememberBlobHash(string namespaceKey, string hashHex)
        {
            if (string.IsNullOrEmpty(namespaceKey)) return;
            try
            {
                Directory.CreateDirectory(BlobCacheRoot);
                File.WriteAllText(BlobCachePath(namespaceKey), hashHex ?? string.Empty);
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[ManifestDiffSync] RememberBlobHash('{namespaceKey}') failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Retrieve the cached hash from a previous <see cref="RememberBlobHash"/> call.
        /// Returns empty string when nothing has been cached yet (never null).
        /// </summary>
        public static string GetPersistedBlobHash(string namespaceKey)
        {
            if (string.IsNullOrEmpty(namespaceKey)) return string.Empty;
            try
            {
                var path = BlobCachePath(namespaceKey);
                if (File.Exists(path)) return (File.ReadAllText(path) ?? string.Empty).Trim();
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[ManifestDiffSync] GetPersistedBlobHash('{namespaceKey}') failed: {ex.Message}");
            }
            return string.Empty;
        }

        /// <summary>
        /// Convenience for blob-style servers: one-shot registration that wires a hash
        /// provider to the persistent cache in one call. Equivalent to
        /// <c>RegisterHashProvider(ns, () =&gt; GetPersistedBlobHash(ns))</c>.
        /// </summary>
        public static void RegisterPersistedBlob(string namespaceKey)
        {
            if (string.IsNullOrEmpty(namespaceKey)) return;
            RegisterHashProvider(namespaceKey, () => GetPersistedBlobHash(namespaceKey));
        }

        /// <summary>
        /// Server-side blob-sync check: <c>true</c> iff the peer reported the same single
        /// hash we computed for the namespace's current content. Thin wrapper around
        /// <see cref="HasSameHash"/> with a fixed empty-string relativePath to match the
        /// wire format emitted by hash-provider registrations.
        /// </summary>
        public static bool PeerHasBlob(long peerUid, string namespaceKey, string serverHashHex)
        {
            return HasSameHash(peerUid, namespaceKey, string.Empty, serverHashHex);
        }

        /// <summary>
        /// Server-side: ask <paramref name="peerUid"/> to reply with its
        /// <paramref name="namespaceKey"/> manifest. Any previously cached manifest for
        /// this (peer, namespace) pair is cleared so the caller can reliably detect
        /// the fresh reply via <see cref="TryGetManifest"/>.
        /// </summary>
        public static void RequestFromPeer(long peerUid, string namespaceKey)
        {
            if (string.IsNullOrEmpty(namespaceKey)) return;
            EnsureRegistered();
            lock (_lock) _peerManifests.Remove(new ManifestKey(peerUid, namespaceKey));

            var pkg = new ZPackage();
            pkg.Write(namespaceKey);
            SafeRoutedRpc.InvokeSafe(peerUid, RpcRequest, pkg);
        }

        /// <summary>
        /// Returns the cached manifest (relativePath -&gt; sha256 hex, lowercase) for a
        /// (peer, namespace) pair, or false if none has arrived yet.
        /// </summary>
        public static bool TryGetManifest(long peerUid, string namespaceKey,
            out Dictionary<string, string> manifest)
        {
            lock (_lock)
            {
                return _peerManifests.TryGetValue(new ManifestKey(peerUid, namespaceKey), out manifest);
            }
        }

        /// <summary>
        /// Convenience check for sync loops: returns true iff the peer reported the
        /// same SHA-256 for <paramref name="relativePath"/> as our local copy. When no
        /// manifest has been received (timeout / older client), returns false so the
        /// caller falls back to the legacy "send everything" path and the peer still
        /// ends up synced - correctness over bandwidth.
        /// </summary>
        public static bool HasSameHash(long peerUid, string namespaceKey, string relativePath, string localHashHex)
        {
            if (string.IsNullOrEmpty(relativePath) || string.IsNullOrEmpty(localHashHex)) return false;
            if (!TryGetManifest(peerUid, namespaceKey, out var manifest) || manifest == null) return false;
            if (!manifest.TryGetValue(relativePath, out var remoteHash) || string.IsNullOrEmpty(remoteHash)) return false;
            return string.Equals(remoteHash, localHashHex, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Coroutine helper that polls <see cref="TryGetManifest"/> at 10 Hz until either
        /// the manifest arrives or <paramref name="timeoutSeconds"/> elapses. Lets a
        /// server-side sync coroutine <c>yield return</c> on the manifest without polling
        /// bookkeeping at every call site. Never throws.
        /// </summary>
        public static IEnumerator WaitForManifest(long peerUid, string namespaceKey, float timeoutSeconds)
        {
            if (string.IsNullOrEmpty(namespaceKey)) yield break;
            float elapsed = 0f;
            var wait = new WaitForSeconds(0.1f);
            while (elapsed < timeoutSeconds)
            {
                if (TryGetManifest(peerUid, namespaceKey, out _)) yield break;
                elapsed += 0.1f;
                yield return wait;
            }
        }

        /// <summary>Drop all cached manifests for a peer - call on disconnect.</summary>
        public static void ForgetPeer(long peerUid)
        {
            lock (_lock)
            {
                var toRemove = new List<ManifestKey>();
                foreach (var key in _peerManifests.Keys)
                    if (key.PeerUid == peerUid) toRemove.Add(key);
                for (int i = 0; i < toRemove.Count; i++) _peerManifests.Remove(toRemove[i]);
            }
        }

        /// <summary>
        /// Lowercase hex SHA-256 of a UTF-8 string. Server and client MUST use this
        /// helper so hashes compare byte-for-byte regardless of culture / endianness.
        /// </summary>
        public static string ComputeSha256(string text)
        {
            if (text == null) text = string.Empty;
            return ComputeSha256(Encoding.UTF8.GetBytes(text));
        }

        /// <summary>Lowercase hex SHA-256 of a byte array. Null / empty =&gt; hash of empty input.</summary>
        public static string ComputeSha256(byte[] bytes)
        {
            if (bytes == null) bytes = Array.Empty<byte>();
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(bytes);
                var sb = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++) sb.Append(hash[i].ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>
        /// Builds a (relativePath -&gt; sha256) manifest for one of our own registered
        /// namespaces. Safe to call at any time on either server or client. Returns an
        /// empty (non-null) dictionary when the namespace is unknown or the folder
        /// is missing / unreadable.
        ///
        /// Dispatch:
        ///  * Hash-provider registration (blob-style)  ? returns a single entry
        ///    <c>{"" =&gt; providerHash}</c>. Queried via <see cref="PeerHasBlob"/>.
        ///  * Folder registration                       ? enumerates the folder with the
        ///    registered pattern / search option, SHA-256s every file, and keys by
        ///    forward-slash relative path.
        ///
        /// Hash-provider wins if both are somehow registered on the same key (shouldn't
        /// happen in practice but kept deterministic).
        /// </summary>
        public static Dictionary<string, string> BuildLocalManifest(string namespaceKey)
        {
            var manifest = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            HashProviderRegistration hashReg;
            FolderRegistration reg;
            lock (_lock)
            {
                _hashProviders.TryGetValue(namespaceKey, out hashReg);
                _registered.TryGetValue(namespaceKey, out reg);
            }

            if (hashReg != null && hashReg.HashProvider != null)
            {
                string hash;
                try { hash = hashReg.HashProvider() ?? string.Empty; }
                catch (Exception ex)
                {
                    FiresLogger.LogWarning($"[ManifestDiffSync] hash provider for '{namespaceKey}' threw: {ex.Message}");
                    hash = string.Empty;
                }
                manifest[string.Empty] = hash;
                return manifest;
            }

            if (reg == null) return manifest;

            string folder;
            try { folder = reg.FolderProvider?.Invoke(); }
            catch { return manifest; }

            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return manifest;

            string[] files;
            try { files = Directory.GetFiles(folder, reg.SearchPattern, reg.Search); }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[ManifestDiffSync] BuildLocalManifest('{namespaceKey}') enumerate failed: {ex.Message}");
                return manifest;
            }

            for (int i = 0; i < files.Length; i++)
            {
                string file = files[i];
                string rel;
                try
                {
                    rel = file.Substring(folder.Length)
                        .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        .Replace('\\', '/');
                }
                catch { continue; }

                string sha;
                try
                {
                    using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var sha256 = SHA256.Create())
                    {
                        byte[] hash = sha256.ComputeHash(stream);
                        var sb = new StringBuilder(hash.Length * 2);
                        for (int j = 0; j < hash.Length; j++) sb.Append(hash[j].ToString("x2"));
                        sha = sb.ToString();
                    }
                }
                catch (Exception ex)
                {
                    FiresLogger.LogWarning($"[ManifestDiffSync] Hashing '{rel}' failed: {ex.Message}");
                    continue;
                }

                manifest[rel] = sha;
            }

            return manifest;
        }

        // -------- RPC handlers --------

        private static void RPC_OnRequest(long sender, ZPackage pkg)
        {
            if (pkg == null) return;
            string namespaceKey;
            try { namespaceKey = pkg.ReadString(); }
            catch { return; }
            if (string.IsNullOrEmpty(namespaceKey)) return;

            // Everyone can be asked for a manifest - on a listen-server the server is
            // both the asker and a potential responder for other peers. We just refuse
            // to enumerate namespaces that weren't registered locally.
            Dictionary<string, string> manifest;
            try { manifest = BuildLocalManifest(namespaceKey); }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[ManifestDiffSync] BuildLocalManifest('{namespaceKey}') threw: {ex.Message}");
                return;
            }

            var reply = new ZPackage();
            reply.Write(namespaceKey);
            reply.Write(manifest.Count);
            foreach (var kv in manifest)
            {
                reply.Write(kv.Key ?? string.Empty);
                reply.Write(kv.Value ?? string.Empty);
            }
            // Reply to the asker (sender). target=0 routes to the server on a client;
            // on the server, sender is the originating peer uid.
            SafeRoutedRpc.InvokeSafe(sender, RpcReply, reply);
        }

        private static void RPC_OnReply(long sender, ZPackage pkg)
        {
            if (pkg == null) return;
            try
            {
                string namespaceKey = pkg.ReadString();
                if (string.IsNullOrEmpty(namespaceKey)) return;

                int count = pkg.ReadInt();
                if (count < 0) count = 0;

                var dict = new Dictionary<string, string>(count, StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < count; i++)
                {
                    string rel = pkg.ReadString();
                    string sha = pkg.ReadString();
                    if (!string.IsNullOrEmpty(rel) && !string.IsNullOrEmpty(sha))
                        dict[rel] = sha;
                }

                lock (_lock)
                {
                    _peerManifests[new ManifestKey(sender, namespaceKey)] = dict;
                }
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[ManifestDiffSync] RPC_OnReply decode failed: {ex.Message}");
            }
        }
    }
}
