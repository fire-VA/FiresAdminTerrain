using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using FiresCore.Bridge;

namespace VerdantsAscent.Terrain.Voxel
{
    // World Gen Studio (Tools\WorldGenReal) turns a Gaea export folder into a baked Fires world. It is a separate app that never
    // ships with the mod (Fire, 2026-09-27), and a public mod neither launches programs nor reads folders outside the Valheim and
    // BepInEx folders (Fire, 2026-09-29): so the private FDT hosts the Studio behind Core's WorldGenStudioBridge (finds it,
    // checks the Gaea folder, starts it, has it import and bake, stops it), and FAT only asks. New World's "From Gaea" path
    // makes a work folder in Valheim's save data, asks the bridge to bake into it, and installs the bake where the game loads
    // it by name. Without FDT's host the button isn't there. Tools\GAEA_WORLD_BUTTON.md.
    internal static class FiresGaeaStudio
    {
        private const string WorkFolder = "gaea_work";
        private const string PlaceholderWorld = "world.bin";
        private const string FlowExtension = ".flow";
        private const string LogTag = "[GaeaStudio] ";
        private const int DefaultGridResolution = 10501;
        internal const float DefaultPeakY = 450f;
        internal const float DefaultWorldSize = 20000f;
        private const float MinPeakY = 60f;
        private const float MaxPeakY = 1500f;
        private const float MinWorldSize = 1000f;
        // 0.2.721: a world as wide as Expand World Size makes it (edge to edge) may be imported; 21000 without EWS.
        private static float MaxWorldSize
            => FatWorldSize.TryReadExpandWorldSize(out var ews, out _) ? UnityEngine.Mathf.Max(21000f, 2f * ews.Edge) : 21000f;

        // A Studio host is there (FDT, on a machine with the Studio); otherwise New World offers no Gaea import.
        internal static bool Available => WorldGenStudioBridge.Available;

        // Why `folder` can't be used, or null. Only that there is one: the host checks the folder itself (a public mod reads
        // nothing outside the Valheim folders) and says why through the bake's failure.
        internal static string FolderProblem(string folder)
            => string.IsNullOrEmpty(Clean(folder)) ? "Enter the Gaea export folder." : null;

        // The tallest peak's world-Y the map scales to (sea at y30), and the map's width in metres; Valheim's edge sits 10.5 km out.
        internal static string PeakProblem(string text, out float peakY)
            => Number(text, MinPeakY, MaxPeakY, out peakY) ? null : $"Tallest peak: a height from {MinPeakY:0} to {MaxPeakY:0}.";

        internal static string WorldSizeProblem(string text, out float metres)
            => Number(text, MinWorldSize, MaxWorldSize, out metres) ? null : $"World size: {MinWorldSize:0} to {MaxWorldSize:0} metres across.";

        private static bool Number(string text, float min, float max, out float value)
            => float.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value >= min && value <= max;

        // Explorer's "Copy as path" wraps the path in quotes.
        internal static string Clean(string folder) => folder?.Trim().Trim('"', '\'');

        internal sealed class Job
        {
            internal readonly Stopwatch Clock = Stopwatch.StartNew();
            internal volatile string Stage = "Starting World Gen Studio";
            internal volatile string Failure;
            internal volatile bool Finished;
            internal volatile bool Cancelled;
            internal object Handle;
            internal string Work;
            internal string WorldName;
            internal string Folder;
        }

        internal static Job Start(string folder, string worldName, float peakY, float worldSize)
        {
            var job = new Job { WorldName = worldName, Folder = Clean(folder) };
            try
            {
                job.Work = Path.Combine(FiresWorldRecords.Folder, WorkFolder, FiresWorldRecords.SafeName(worldName));
                if (Directory.Exists(job.Work)) Directory.Delete(job.Work, true);
                Directory.CreateDirectory(job.Work);
                // An empty world.bin keeps the Studio from generating its demo world first.
                File.WriteAllBytes(Path.Combine(job.Work, PlaceholderWorld), new byte[0]);
            }
            catch (Exception ex)
            {
                Finish(job, "The work folder could not be made: " + ex.Message);
                return job;
            }
            int resolution = FiresVoxelConfig.WorldGenResolution?.Value ?? DefaultGridResolution;
            job.Handle = WorldGenStudioBridge.StartBake(job.Folder, worldName, peakY, worldSize, resolution, job.Work,
                stage => job.Stage = stage,
                (ok, result) => OnDone(job, ok, result, peakY, worldSize));
            return job;
        }

        internal static void Cancel(Job job)
        {
            job.Cancelled = true;
            WorldGenStudioBridge.CancelBake(job.Handle);
        }

        // From the host, on any thread: install the bake on success, then clean the work folder either way.
        private static void OnDone(Job job, bool ok, string result, float peakY, float worldSize)
        {
            if (job.Finished) return;
            string failure = null;
            if (!ok) failure = job.Cancelled ? "Cancelled." : result ?? "World Gen Studio failed.";
            else
            {
                try
                {
                    job.Stage = "Installing the baked world";
                    Install(result, job.WorldName);
                    FiresLogger.LogInfo($"{LogTag}'{job.WorldName}' baked from {job.Folder} (peak y{peakY:0}, {worldSize:0} m) in {job.Clock.Elapsed.TotalSeconds:0} s.");
                }
                catch (Exception ex) { failure = ex.Message; }
            }
            Finish(job, failure);
        }

        private static void Finish(Job job, string failure)
        {
            if (failure != null)
            {
                job.Failure = failure;
                FiresLogger.LogWarning($"{LogTag}'{job.WorldName}' was not baked from {job.Folder}: {failure}");
            }
            try { if (job.Work != null && Directory.Exists(job.Work)) Directory.Delete(job.Work, true); } catch (IOException) { }
            job.Finished = true;
        }

        // `baked` is the host's firesgen_<name>_baked.bin inside the work folder FAT made; its .flow sits beside it.
        private static void Install(string baked, string worldName)
        {
            if (string.IsNullOrEmpty(baked) || !File.Exists(baked)) throw new Exception("World Gen Studio reported a bake but wrote no file.");
            if (!FiresWorldGenBase.IsLoadableBake(baked))
                throw new Exception("World Gen Studio is older than this FiresAdminTerrain (its bake format differs); rebuild the Studio.");
            string target = FiresWorldGenBase.BakedCachePath(worldName);
            File.Copy(baked, target, true);
            string flow = Path.ChangeExtension(baked, FlowExtension);
            string flowTarget = Path.ChangeExtension(target, FlowExtension);
            if (File.Exists(flow)) File.Copy(flow, flowTarget, true);
            else if (File.Exists(flowTarget)) File.Delete(flowTarget);
            // 0.2.805: the bake's big waterfall sinkholes (firesgen_<name>_sinkfalls.txt beside it). A bake without one
            // (faithful, or a studio older than 0.2.805) still overwrites the target, so an older world's cuts never linger.
            const string bakedSuffix = "_baked.bin";
            string cuts = baked.EndsWith(bakedSuffix, StringComparison.OrdinalIgnoreCase)
                ? baked.Substring(0, baked.Length - bakedSuffix.Length) + FatSinkFallCuts.Suffix : null;
            string cutsTarget = FiresWorldRecords.PathFor(worldName, FatSinkFallCuts.Suffix);
            if (cuts != null && File.Exists(cuts)) File.Copy(cuts, cutsTarget, true);
            else File.WriteAllText(cutsTarget, "# FAT big waterfall sinkholes: none in this bake\n");
        }
    }
}
