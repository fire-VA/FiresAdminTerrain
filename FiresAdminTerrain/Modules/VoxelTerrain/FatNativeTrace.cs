using System;
using System.IO;
using System.Text;
using System.Threading;
using BepInEx.Configuration;

namespace VerdantsAscent.Terrain.Voxel
{
    // For hunting a native client crash (ntdll c0000005) that always came after a big terraform
    // with the voxel queues deep). A crash-proof trace of every native step in FAT's voxel path: mesh apply, PhysX bake (worker), collider
    // assign, view/mesh destroy, ice/water collider swaps, zone wrap/unwrap. Each line is written AND flushed to its own file before the
    // step runs, so after a hard crash the file's last line names the step that was in progress (BepInEx's log loses its buffered tail).
    // File: BepInEx\FiresTests\fat_native_trace.log (rotated to .1 at 2 MB). Config: [VoxelTerrain Performance] "Native crash trace (slow, crash hunts only)", default off
    // (it syncs the disk per line, so it costs frame rate). Lines: "HH:mm:ss.fff t<thread> <step> <ids>".
    public static class FatNativeTrace   // public: EBM writes into the same file through Step(string, int, string)
    {
        internal static ConfigEntry<bool> Enabled;
        private static readonly object s_lock = new object();
        private static FileStream s_file;
        private static string s_path;
        private const long MaxBytes = 2L << 20;

        internal static void Bind(ConfigFile cfg, string section)
        {
            // With the trace on, FPS sat near 30 while running: the trace synced the disk ~360 times a second on the main thread
            // while terrain streamed in. OFF by default, under a NEW key so a config that saved the old key switched on does
            // not keep it on. Turn it on only for a crash hunt; it costs frame rate.
            Enabled = cfg.Bind(section, "Native crash trace (slow, crash hunts only)", false,
                "Crash hunt only, COSTS FRAME RATE while terrain streams in: write every native voxel step (mesh apply, collider bake/assign, destroy) to BepInEx\\FiresTests\\fat_native_trace.log, "
                + "flushed per line, so the last line after a native crash names the step. Turn it off again when the hunt is over.");
        }

        internal static bool On => Enabled != null && Enabled.Value;

        public static void Step(string step, int meshId = 0, string detail = null)
        {
            if (!On) return;
            try
            {
                string line = DateTime.Now.ToString("HH:mm:ss.fff") + " t" + Thread.CurrentThread.ManagedThreadId + " " + step
                    + (meshId != 0 ? " mesh " + meshId : "") + (detail != null ? " " + detail : "") + "\n";
                byte[] bytes = Encoding.ASCII.GetBytes(line);
                lock (s_lock)
                {
                    if (s_file == null) Open();
                    if (s_file == null) return;
                    if (s_file.Length + bytes.Length > MaxBytes) { s_file.Dispose(); s_file = null; Rotate(); Open(); if (s_file == null) return; }
                    s_file.Write(bytes, 0, bytes.Length);
                    s_file.Flush(true);
                }
            }
            catch { }
        }

        private static void Open()
        {
            try
            {
                if (s_path == null)
                {
                    string dir = Path.Combine(BepInEx.Paths.BepInExRootPath, "FiresTests");
                    Directory.CreateDirectory(dir);
                    s_path = Path.Combine(dir, "fat_native_trace.log");
                }
                s_file = new FileStream(s_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 4096, FileOptions.WriteThrough);
            }
            catch { s_file = null; }
        }

        private static void Rotate()
        {
            try
            {
                string old = s_path + ".1";
                if (File.Exists(old)) File.Delete(old);
                File.Move(s_path, old);
            }
            catch { }
        }
    }
}
