using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using BepInEx;
using HarmonyLib;

namespace VerdantsAscent.Terrain.BcImport
{
    // Marks each stage of a world save in FAT's log, from the save thread, because the rig's quiet filter drops vanilla's own
    // "World save (n/5)" lines. `-firessavecrash <stage>` on the command line makes the process kill itself at that stage of
    // its first world save, the way a crash would, for the save-safety crash test (Tools\SAVE_SAFETY_AUDIT.md, section 4):
    // chunk, chunks, db2, fwl2 and ok stop just before that file's writer flushes, so the file is left torn; oldgen stops after
    // the previous generation's main files are deleted and before its old chunk versions are. Without the argument nothing
    // here changes a save.
    [HarmonyPatch]
    internal static class SaveWatchStages
    {
        private const string CrashArgument = "-firessavecrash";
        private const string OldGenerationStage = "oldgen";
        private static readonly string[] Stages = { "chunk", "chunks", "db2", "fwl2", "ok", OldGenerationStage };
        private const string ResultsFolder = "FiresTests";
        private const string CrashRecordFile = "savecrash.txt";
        private const string StampFormat = "yyyy-MM-dd HH:mm:ss.fff";

        private static readonly AccessTools.FieldRef<FileWriter, string> WriterPath = AccessTools.FieldRefAccess<FileWriter, string>("m_path");
        private static readonly Lazy<string> ArmedStage = new Lazy<string>(ReadCrashArgument);

        private static volatile bool s_worldSaving;
        private static readonly Stopwatch s_saveClock = new Stopwatch();
        private static uint s_saveNumber;

        private static string Save => $"[SaveWatch] world save _main.{s_saveNumber}";
        private static string Elapsed => s_saveClock.ElapsedMilliseconds.ToString("N0", CultureInfo.InvariantCulture) + " ms";

        [HarmonyPatch(typeof(SaveSystem), nameof(SaveSystem.BeginSave))]
        [HarmonyPostfix]
        private static void Started()
        {
            s_saveNumber = SaveSystem.GetSaveNumber();
            s_saveClock.Restart();
            s_worldSaving = true;
            string armed = ArmedStage.Value == null ? "" : $" CRASH TEST ARMED: this process kills itself at stage '{ArmedStage.Value}'.";
            FiresLogger.LogInfo($"{Save} (1/5): started, writing chunk files.{armed}");
        }

        [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.SaveChunks))]
        [HarmonyPostfix]
        private static void ChunksWritten(bool __result)
        {
            if (!s_worldSaving) return;
            FiresLogger.LogInfo($"{Save} (2/5): chunk files and _main.{s_saveNumber}.chunks " + (__result ? "written" : "FAILED") + $" at {Elapsed}.");
        }

        [HarmonyPatch(typeof(World), nameof(World.SaveWorldFWLData), new[] { typeof(DateTime), typeof(FileWriter) },
            new[] { ArgumentType.Normal, ArgumentType.Out })]
        [HarmonyPrefix]
        private static void DataWritten()
        {
            if (!s_worldSaving) return;
            FiresLogger.LogInfo($"{Save} (3/5): _main.{s_saveNumber}.db2 written at {Elapsed}, writing its .fwl2.");
        }

        [HarmonyPatch(typeof(World), nameof(World.SaveWorldFWLData), new[] { typeof(DateTime), typeof(FileWriter) },
            new[] { ArgumentType.Normal, ArgumentType.Out })]
        [HarmonyPostfix]
        private static void MetaWritten()
        {
            if (!s_worldSaving) return;
            FiresLogger.LogInfo($"{Save} (4/5): _main.{s_saveNumber}.fwl2 written at {Elapsed}, then .ok and removing the save before it.");
        }

        [HarmonyPatch(typeof(SaveSystem), nameof(SaveSystem.EndSave))]
        [HarmonyPrefix]
        private static void Ended(bool writeOK)
        {
            if (!s_worldSaving) return;
            s_worldSaving = false;
            FiresLogger.LogInfo($"{Save} (5/5): " + (writeOK ? "done" : "FAILED, its new files were removed") + $" at {Elapsed}.");
        }

        // From BcSaveDeleteWatch's ZDOMan.DeleteOldChunks prefix: .ok is written and the previous save's main files are gone.
        internal static void BeforeOldChunkRotation()
        {
            if (s_worldSaving && ArmedStage.Value == OldGenerationStage) Crash(OldGenerationStage, "before the old chunk versions are deleted");
        }

        [HarmonyPatch(typeof(FileWriter), nameof(FileWriter.Finish))]
        [HarmonyPrefix]
        private static void BeforeFlush(FileWriter __instance)
        {
            string stage = ArmedStage.Value;
            if (stage == null || !s_worldSaving || stage == OldGenerationStage) return;
            string path = WriterPath(__instance);
            if (path != null && path.EndsWith("." + stage, StringComparison.Ordinal)) Crash(stage, path);
        }

        private static void Crash(string stage, string where)
        {
            string line = $"{DateTime.Now.ToString(StampFormat, CultureInfo.InvariantCulture)} world save _main.{s_saveNumber} killed at stage "
                + $"'{stage}' ({where}) at {Elapsed}";
            try
            {
                string folder = Path.Combine(Paths.BepInExRootPath, ResultsFolder);
                Directory.CreateDirectory(folder);
                File.AppendAllText(Path.Combine(folder, CrashRecordFile), line + Environment.NewLine);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            FiresLogger.LogWarning("[SaveWatch] CRASH TEST: " + line);
            Process.GetCurrentProcess().Kill();
        }

        private static string ReadCrashArgument()
        {
            string[] args = Environment.GetCommandLineArgs();
            int at = Array.FindIndex(args, a => string.Equals(a, CrashArgument, StringComparison.OrdinalIgnoreCase));
            if (at < 0) return null;
            string stage = at + 1 < args.Length ? args[at + 1].ToLowerInvariant() : null;
            if (Stages.Contains(stage)) return stage;
            FiresLogger.LogError($"[SaveWatch] {CrashArgument} needs one of {string.Join(", ", Stages)}; got '{stage}'. The crash test is OFF.");
            return null;
        }
    }
}
