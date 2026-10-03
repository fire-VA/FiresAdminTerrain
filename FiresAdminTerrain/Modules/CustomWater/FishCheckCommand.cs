using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    // ── `va_fish_check <x> <z>` and `va_fish_spawn <x> <z>`: the inland-fish proof made deterministic ([lead] 10-01, R29a:
    // vanilla's random 40-80 m spawn points rarely land in a 2-4.5 m river, so a silent round proves nothing) ──────────────
    // Admin-only test tooling; changes no behaviour. The contract test fishwater_test (Testing\FishWaterTest) runs the same
    // judging through Judge/Spawn below.
    //   va_fish_check  prints every water source at the point (the generated field, the voxel sheet, FAT's surface, the
    //                  ground) and what the clutter ecology finds there, then for each fish spawn entry the verdict of
    //                  vanilla's whole SpawnSystem.IsSpawnPointGood (with every FAT postfix, InlandFishSpawns included) and
    //                  InlandFishSpawns' own reason. Read-only.
    //   va_fish_spawn  spawns ONE fish through vanilla's SpawnSystem.Spawn at that point's bed, for the first fish entry the
    //                  inland gate passes there. The one gate it sets aside is "player within N m", since the admin running
    //                  it usually stands on the bank; every other refusal stops it.
    // One line each, tagged [FiresFishCheck], in the console and the log.
    internal static class FishCheckCommand
    {
        private const string Check = "va_fish_check", SpawnCommand = "va_fish_spawn", Tag = "[FiresFishCheck]";
        private const string PlayerGate = "player";
        internal const string Passes = "passes";
        private const string NoInlandWater = "no FAT inland water >= 1 m";

        private static readonly FieldInfo Instances = AccessTools.Field(typeof(SpawnSystem), "m_instances");
        private static readonly MethodInfo IsGood = AccessTools.Method(typeof(SpawnSystem), "IsSpawnPointGood");
        private static readonly MethodInfo SpawnOne = AccessTools.Method(typeof(SpawnSystem), "Spawn");

        // One fish entry judged at a point: vanilla's whole chain, and the inland gate's own reason.
        internal struct Verdict
        {
            public SpawnSystem.SpawnData Entry;
            public bool Good;            // SpawnSystem.IsSpawnPointGood with every postfix
            public Vector3 Point;        // the point it returned (the bed when the inland gate passed)
            public string Reason;        // Passes, NoInlandWater or the gate that refused
            public float Bed;            // the water's floor, when there is inland water
            // Spawnable by va_fish_spawn: the chain passed, or the inland gate refused only for the player standing near.
            public bool Spawnable => Good || Reason.StartsWith(PlayerGate);
            public Vector3 SpawnAt => Good ? Point : new Vector3(Point.x, Bed, Point.z);
        }

        internal static void Register()
        {
            new Terminal.ConsoleCommand(Check, "<x> <z> - judge FAT inland water at a point for every fish spawn entry (admin, read-only)",
                args => Run(args, false));
            new Terminal.ConsoleCommand(SpawnCommand, "<x> <z> - spawn one fish through vanilla's SpawnSystem at a point the inland gate passes (admin, test tool)",
                args => Run(args, true));
        }

        private static void Run(Terminal.ConsoleEventArgs args, bool spawn)
        {
            string cmd = spawn ? SpawnCommand : Check;
            void Say(string line) { FiresLogger.LogInfo(line); args.Context.AddString(line); }
            if (!FiresCore.Sync.AdminSyncing.IsLocalAdmin()) { Say($"{Tag} {cmd} refused: not an admin"); return; }
            var inv = CultureInfo.InvariantCulture;
            if (args.Args.Length < 3 || !float.TryParse(args.Args[1], NumberStyles.Float, inv, out float x)
                || !float.TryParse(args.Args[2], NumberStyles.Float, inv, out float z)) { Say($"{Tag} usage: {cmd} <x> <z>"); return; }
            var system = NearestSystem(new Vector3(x, 0f, z));
            if (system == null || ZoneSystem.instance == null) { Say($"{Tag} {cmd} refused: no SpawnSystem loaded here (stand within a loaded zone)"); return; }

            Say($"{Tag} {WaterLine(x, z, out _)}");
            Say($"{Tag} {ClutterLine(x, z, out _)}");
            var verdicts = Judge(system, x, z);
            if (verdicts.Count == 0) { Say($"{Tag} no fish spawn entry in this SpawnSystem's lists."); return; }
            foreach (var v in verdicts) Say($"{Tag} {Describe(v)}");
            if (!spawn) return;
            foreach (var v in verdicts)
            {
                if (!v.Spawnable) continue;
                Spawn(system, v);
                Say($"{Tag} spawned one {v.Entry.m_name} at ({v.SpawnAt.x:0.0}, {v.SpawnAt.y:0.0}, {v.SpawnAt.z:0.0}) through SpawnSystem.Spawn"
                    + (v.Good ? "." : $" (set aside: {v.Reason})."));
                return;
            }
            Say($"{Tag} {SpawnCommand}: nothing spawned; no fish entry passes here (see the reasons above).");
        }

        internal static string Describe(Verdict v)
            => $"{v.Entry.m_name}: IsSpawnPointGood {(v.Good ? "PASS" : "FAIL")} (point y {v.Point.y:0.0}); inland gate: {v.Reason}; "
             + $"bands altitude {v.Entry.m_minAltitude:0.##}..{v.Entry.m_maxAltitude:0.##}, ocean depth {v.Entry.m_minOceanDepth:0.##}..{v.Entry.m_maxOceanDepth:0.##}.";

        // Every enabled fish entry of the system's lists, once by name, judged at (x, z).
        internal static List<Verdict> Judge(SpawnSystem system, float x, float z)
        {
            var result = new List<Verdict>();
            var seen = new HashSet<string>();
            Vector3 ground = new Vector3(x, 0f, z);
            ZoneSystem.instance.GetGroundData(ref ground, out _, out _, out _, out _);
            foreach (var list in system.m_spawnLists)
            {
                if (list == null) continue;
                foreach (var entry in list.m_spawners)
                {
                    if (entry == null || !entry.m_enabled || !InlandFishSpawns.IsFish(entry) || !seen.Add(entry.m_name)) continue;
                    object[] call = { entry, new Vector3(x, 0f, z) };
                    bool good = (bool)IsGood.Invoke(system, call);
                    var v = new Verdict { Entry = entry, Good = good, Point = (Vector3)call[1], Reason = NoInlandWater };
                    if (InlandFishSpawns.TryInlandWater(ground, out float surface, out float bed))
                    {
                        v.Bed = bed;
                        v.Reason = InlandFishSpawns.Refusal(entry, ground, surface, bed) ?? Passes;
                    }
                    result.Add(v);
                }
            }
            return result;
        }

        // Vanilla's own spawn (prefab, level, ZDO), at the bed plus the entry's ground offset as SpawnSystem.UpdateSpawnList does.
        internal static void Spawn(SpawnSystem system, Verdict v)
            => SpawnOne.Invoke(system, new object[] { v.Entry, v.SpawnAt + Vector3.up * v.Entry.m_groundOffset, false });

        // Every water source at the point. wet = FAT inland water at least 1 m deep there.
        internal static string WaterLine(float x, float z, out bool wet)
        {
            Vector3 p = new Vector3(x, 0f, z);
            ZoneSystem.instance.GetGroundData(ref p, out _, out var biome, out _, out _);
            bool genReady = VerdantsAscent.Terrain.Voxel.GenWaterField.Ready, surfaceReady = VerdantsAscent.Terrain.Voxel.GenWaterField.SurfaceReady;
            string smooth = "n/a";
            if (genReady)
                smooth = VerdantsAscent.Terrain.Voxel.GenWaterField.TryWaterSurfaceSmooth(x, z, out float lvl, out float dep)
                    ? $"wet, level {lvl:0.0}, depth {dep:0.0}" : "dry";
            float sheet = VerdantsAscent.Terrain.Voxel.VoxelWaterSurface.SurfaceAt(p);
            float fat = FatWaterChecks.SurfaceAt(p);
            wet = InlandFishSpawns.TryInlandWater(p, out float surface, out float bed);
            return $"water at ({x:0}, {z:0}): ground y {p.y:0.0}, biome {biome}; generated field ready {genReady}, surface ready {surfaceReady} "
                + $"(Fires generator {VerdantsAscent.Terrain.Voxel.FiresWorldGenBase.Active}): {smooth}; voxel sheet "
                + (sheet > -9999f ? $"{sheet:0.0}" : "none") + $"; FAT surface {(fat <= -9999f ? "none" : fat < p.y ? $"none here (only the sea plane {fat:0.0}, under the ground)" : fat.ToString("0.0"))}; "
                + (wet ? $"inland water surface {surface:0.0}, bed {bed:0.0}, depth {surface - bed:0.0}" : "no inland water >= 1 m");
        }

        // What the clutter ecology finds for a grass patch centred here. grows = it moves the patch to inland water.
        internal static string ClutterLine(float x, float z, out bool grows)
        {
            bool on = VerdantsAscent.Terrain.Voxel.FiresVegWaterEcology.Enabled;
            float level = 0f;
            grows = on && VerdantsAscent.Terrain.Voxel.ClutterSystem_GenerateVegPatch_WaterEcology.TryPatchWater(new Vector3(x, 0f, z), out level);
            return $"clutter here: ecology {(on ? "on" : "OFF")}, a grass patch centred here "
                + (grows ? $"grows against water at y {level:0.0}" : "finds no water (vanilla sea level)") + ".";
        }

        internal static SpawnSystem NearestSystem(Vector3 p)
        {
            SpawnSystem best = null;
            float bestD = float.MaxValue;
            if (Instances?.GetValue(null) is List<SpawnSystem> all)
                foreach (var s in all)
                {
                    if (s == null) continue;
                    float d = Utils.DistanceXZ(s.transform.position, p);
                    if (d < bestD) { bestD = d; best = s; }
                }
            return best;
        }
    }
}
