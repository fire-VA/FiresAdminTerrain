using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace VerdantsAscent.Terrain.Performance
{
    // 0.2.1498 ([lead] 11-19 split legs: "in world after" same7 54.5 s -> FAT only 72.6 s; BepInEx chainloader up at 18.3 s -> 30.4 s,
    // FAT the only plugin added): FAT's load had no step timings, so the +18 s could not be split. This clock measures, main thread only:
    //  - plugin start: each step of the plugin's Awake / FinishInit, the Harmony loop as one step plus its slowest patch classes;
    //  - world entry: the FAT steps that already time themselves (ZNetScene.Awake steps, world init, water guards, scene wiring) report here.
    // One Info line when the plugin start ends, one when the local player first spawns: FAT's measured total load contribution.
    // Read-only: no behaviour changes. Lines are Info because they print twice per session.
    internal static class FatLoadClock
    {
        private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;
        private static readonly List<KeyValuePair<string, double>> s_start = new List<KeyValuePair<string, double>>();
        private static readonly List<KeyValuePair<string, double>> s_world = new List<KeyValuePair<string, double>>();
        private static long s_mark, s_begin;
        private static float s_beginGameS = -1f;
        private static double s_startTotalMs, s_patchMs;
        private static int s_patchClasses;
        private static readonly List<KeyValuePair<string, double>> s_slowPatches = new List<KeyValuePair<string, double>>();
        private const int SlowPatchKeep = 12;
        private static bool s_startSaid, s_worldSaid;

        internal static void Begin()
        {
            s_begin = s_mark = Stopwatch.GetTimestamp();
            s_beginGameS = Time.realtimeSinceStartup;
        }

        internal static void Step(string name)
        {
            if (s_begin == 0L || s_startSaid) return;
            long now = Stopwatch.GetTimestamp();
            s_start.Add(new KeyValuePair<string, double>(name, (now - s_mark) * TicksToMs));
            s_mark = now;
        }

        // One Harmony patch class: Harmony's CreateClassProcessor + Patch() for this type.
        // 0.2.1499: split by outcome - types that patched something vs types Harmony looked at and skipped (no class-level [HarmonyPatch]),
        // so the line shows what walking every type costs apart from the patching itself.
        private static double s_skipMs; private static int s_skipTypes;
        internal static void PatchClass(Type type, long ticks, bool patched)
        {
            double ms = ticks * TicksToMs;
            s_patchMs += ms;
            s_patchClasses++;
            if (!patched) { s_skipMs += ms; s_skipTypes++; return; }
            if (s_slowPatches.Count < SlowPatchKeep) { s_slowPatches.Add(new KeyValuePair<string, double>(type.FullName, ms)); return; }
            int least = 0;
            for (int i = 1; i < s_slowPatches.Count; i++) if (s_slowPatches[i].Value < s_slowPatches[least].Value) least = i;
            if (ms > s_slowPatches[least].Value) s_slowPatches[least] = new KeyValuePair<string, double>(type.FullName, ms);
        }

        internal static void EndStart()
        {
            if (s_begin == 0L || s_startSaid) return;
            s_startSaid = true;
            s_startTotalMs = (Stopwatch.GetTimestamp() - s_begin) * TicksToMs;
            var sb = new StringBuilder();
            sb.Append(string.Format(CultureInfo.InvariantCulture, "[FatLoad] plugin start {0:0} ms (game clock {1:0.0} s -> {2:0.0} s):",
                s_startTotalMs, s_beginGameS, Time.realtimeSinceStartup));
            foreach (var kv in s_start) sb.Append(string.Format(CultureInfo.InvariantCulture, " {0} {1:0}", kv.Key, kv.Value)).Append(';');
            sb.Append(string.Format(CultureInfo.InvariantCulture, " Harmony {0} type(s) walked, {1:0} ms in total: {2} patched {3:0} ms, {4} skipped (no [HarmonyPatch]) {5:0} ms (0.2.1499); slowest patched:",
                s_patchClasses, s_patchMs, s_patchClasses - s_skipTypes, s_patchMs - s_skipMs, s_skipTypes, s_skipMs));
            s_slowPatches.Sort((a, b) => b.Value.CompareTo(a.Value));
            foreach (var kv in s_slowPatches) sb.Append(string.Format(CultureInfo.InvariantCulture, " {0} {1:0.0}", Short(kv.Key), kv.Value)).Append(',');
            sb.Append(" (ms; 0.2.1498)");
            FiresLogger.LogInfo(sb.ToString());
        }

        // A world-entry step FAT already timed (ms), main thread.
        internal static void World(string name, double ms)
        {
            if (s_worldSaid) return;
            s_world.Add(new KeyValuePair<string, double>(name, ms));
        }

        // The local player's first spawn: FAT's measured load contribution, once per session.
        internal static void EndWorld()
        {
            if (s_worldSaid) return;
            s_worldSaid = true;
            double world = 0;
            var sb = new StringBuilder();
            foreach (var kv in s_world) { world += kv.Value; sb.Append(string.Format(CultureInfo.InvariantCulture, " {0} {1:0}", kv.Key, kv.Value)).Append(';'); }
            FiresLogger.LogInfo(string.Format(CultureInfo.InvariantCulture,
                "[FatLoad] FAT's measured load contribution: plugin start {0:0} ms + world entry {1:0} ms = {2:0} ms (game clock {3:0.0} s at first spawn); world entry steps:{4} (0.2.1498; steps FAT does not time are not in it)",
                s_startTotalMs, world, s_startTotalMs + world, Time.realtimeSinceStartup, sb.Length > 0 ? sb.ToString() : " none reported"));
        }

        private static string Short(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return "?";
            int dot = fullName.LastIndexOf('.');
            return dot >= 0 && dot < fullName.Length - 1 ? fullName.Substring(dot + 1) : fullName;
        }
    }
}
