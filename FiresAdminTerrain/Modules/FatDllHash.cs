using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace VerdantsAscent.Terrain
{
    // The first 12 hex of FAT's own DLL SHA-256, which every F5 test's BEGIN line names (Tools\FIRES_F5_TESTS.md). Hashed once
    // on a thread-pool thread when the console starts, so no test pays for reading 10 MB on the main thread.
    internal static class FatDllHash
    {
        private const int HashChars = 12;
        private const string Unknown = "unknown";

        /// <summary>What Value holds until the hash is ready; a read or hash that fails ends as "unknown", never stays Pending.</summary>
        internal const string Pending = "hashing";

        private static Task<string> s_hash;

        internal static void Start()
        {
            if (s_hash != null) return;
            string path = typeof(FatDllHash).Assembly.Location;
            s_hash = Task.Run(() =>
            {
                try
                {
                    using (var sha = SHA256.Create())
                    using (var file = File.OpenRead(path))
                        return BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "").Substring(0, HashChars);
                }
                catch (Exception) { return Unknown; }
            });
        }

        internal static string Value => s_hash == null ? Unknown : s_hash.IsCompleted ? s_hash.Result : Pending;
    }
}
