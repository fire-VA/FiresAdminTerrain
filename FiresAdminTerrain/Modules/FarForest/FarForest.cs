using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.FarForest
{
    // 0.2.1412 FarLands #2 (Tools\SPEC_FAT_FarForestPlacement.md), stage 1: the parts that need no Core engine.
    //   - [Far Forest] keys (all default OFF);
    //   - the far KINDS (spec §1): vanilla m_vegetation entries that are trees / big rocks, ranked by coverage, MaxKinds kept;
    //   - the RNG self-test (FarRngSelfTest) whenever any Far Forest key needs it;
    //   - the DRAW CENSUS (spec §2.5): how many UnityEngine.Random draws each vegetation prefab's Instantiate (Awake) makes inside the real
    //     PlaceVegetation, measured by stepping FarRng from the state before to the state after. The replay must consume exactly that
    //     many per placed instance, or every later draw of that entry shifts.
    // Nothing here draws or places anything yet; the replay, seen trees, cells and the Core hand-off come in the next stages.
    internal static class FarForest
    {
        private const string S = "Far Forest";
        internal static ConfigEntry<bool> Enable, DrawCensus, MatchTest;
        internal static ConfigEntry<int> MaxKinds;
        internal static ConfigEntry<float> MinKindHeight, ReachMeters;

        internal static void Bind(ConfigFile cfg)
        {
            Enable = cfg.Bind(S, "Enable", false, "FarLands far forest (FAT places, Core's impostor engine draws). Stage 2 (0.2.1413): far kinds, RNG self-test, vanilla placement replay (MatchTest proves it); nothing is drawn yet. Default off.");
            MatchTest = cfg.Bind(S, "MatchTest", false, "Debug self-check (host / dedicated server): every 30 s, vanilla's placement is replayed for the far kinds in settled loaded zones and compared with the real trees; a Debug line with the % reproduced. Read it on fresh, untouched ground. Default off.");
            ReachMeters = cfg.Bind(S, "ReachMeters", 2500f, new ConfigDescription("How far FAT places far trees (m); Core's ImpostorDistance caps what is drawn.", new AcceptableValueRange<float>(600f, 6000f)));
            MaxKinds = cfg.Bind(S, "MaxKinds", 24, new ConfigDescription("Most far kinds (trees / big rocks) registered, by coverage.", new AcceptableValueRange<int>(1, 64)));
            MinKindHeight = cfg.Bind(S, "MinKindHeight", 4f, new ConfigDescription("Vegetation shorter than this (m) is not a far kind.", new AcceptableValueRange<float>(0.5f, 40f)));
            DrawCensus = cfg.Bind(S, "DrawCensus", false, "Debug probe (host / single player): counts the Random draws each vegetation prefab makes when it is created inside vanilla's PlaceVegetation; a Debug table every 30 s. Default off.");
        }

        internal static bool AnyOn => (Enable != null && Enable.Value) || (DrawCensus != null && DrawCensus.Value) || (MatchTest != null && MatchTest.Value);

        // ---------------------------------------------------------------- kinds
        internal sealed class Kind
        {
            public string Prefab;
            public int Hash;
            public Heightmap.Biome Biome;
            public float Height, Weight;
            public string What;
        }

        internal static readonly List<Kind> Kinds = new List<Kind>();
        internal static readonly Dictionary<int, int> KindIndexByHash = new Dictionary<int, int>();   // prefab hash -> index in Kinds

        // FatVanillaHooks' one ZoneSystem.Start postfix.
        internal static void OnZoneSystemStart(ZoneSystem zs)
        {
            if (!AnyOn) return;
            var t = System.Diagnostics.Stopwatch.StartNew();
            FarRngSelfTest.Run();
            PatchCensusIfOn();
            if (Enable.Value || MatchTest.Value) PickKinds(zs);
            FiresLogger.LogDebug($"[FarForest] start took {t.Elapsed.TotalMilliseconds:0.0} ms (self-test + kinds)");
        }

        private static void PickKinds(ZoneSystem zs)
        {
            Kinds.Clear();
            var seen = new HashSet<int>();
            int considered = 0, tooSmall = 0, notTree = 0;
            foreach (var v in zs.m_vegetation)
            {
                if (v == null || !v.m_enable || v.m_prefab == null) continue;
                considered++;
                string what = v.m_prefab.GetComponent<TreeBase>() != null ? "tree"
                    : v.m_prefab.GetComponent<MineRock5>() != null ? "minerock5"
                    : v.m_prefab.GetComponent<MineRock>() != null ? "minerock"
                    : v.m_prefab.GetComponent<Destructible>() != null ? "destructible" : null;
                if (what == null) { notTree++; continue; }
                float h = Voxel.FarVegImpostors.PrefabHeight(v.m_prefab);
                if (h < MinKindHeight.Value) { tooSmall++; continue; }
                int hash = v.m_prefab.name.GetStableHashCode();
                float group = (v.m_groupSizeMin + v.m_groupSizeMax) * 0.5f;
                float count = v.m_max < 1f ? v.m_max : (v.m_min + v.m_max) * 0.5f;
                int biomes = CountBits((int)v.m_biome);
                float w = count * group * Mathf.Max(1, biomes);
                var existing = Kinds.FirstOrDefault(k => k.Hash == hash);
                if (existing != null) { existing.Weight += w; existing.Biome |= v.m_biome; continue; }   // one kind per prefab, several entries
                Kinds.Add(new Kind { Prefab = v.m_prefab.name, Hash = hash, Biome = v.m_biome, Height = h, Weight = w, What = what });
            }
            Kinds.Sort((a, b) => b.Weight.CompareTo(a.Weight));
            int dropped = Math.Max(0, Kinds.Count - MaxKinds.Value);
            if (dropped > 0) Kinds.RemoveRange(MaxKinds.Value, dropped);
            KindIndexByHash.Clear();
            for (int i = 0; i < Kinds.Count; i++) KindIndexByHash[Kinds[i].Hash] = i;
            FiresLogger.LogInfo($"[FarForest] on (stage 2, nothing drawn yet): {Kinds.Count} far kind(s) from {considered} vegetation entr(ies) " +
                                $"({notTree} not a tree/rock, {tooSmall} under {MinKindHeight.Value:0.#} m, {dropped} over MaxKinds); RNG {(FarRngSelfTest.Trusted ? "worker path" : "main-thread path")}.");
            FiresLogger.LogDebug("[FarForest] kinds: " + string.Join(", ", Kinds.Select(k => $"{k.Prefab} ({k.What}, {k.Height:0.#} m, {k.Biome}, w {k.Weight:0.#})")));
        }

        private static int CountBits(int v) { int n = 0; while (v != 0) { n += v & 1; v >>= 1; } return n; }

        // ---------------------------------------------------------------- draw census
        // 0.2.1415 ([lead] URGENT 11-08, Fire's log 0108d): PlaceVegetation's IL is NEVER changed for the census any more. 0.2.1412-1414
        // replaced its two Object.Instantiate<GameObject> calls with a wrapper, and Expand World Data's ReplacePlacementOperations
        // anchors on exactly those calls (with m_enable and ZNetView.SetLocalScale): its CodeMatcher ran off the end and EWD's whole
        // vegetation patch was dropped ("IL Compile Error"). Now: FAT's existing PlaceVegetation prefix / finalizer (no IL) set a
        // [ThreadStatic] "inside PlaceVegetation" depth, and ONLY while DrawCensus is on a prefix/postfix pair is patched onto the
        // non-generic Object.Instantiate(Object, Vector3, Quaternion), which Instantiate<T> calls; it measures the outermost call made
        // inside PlaceVegetation (an Awake that instantiates children is part of the same call). DrawCensus off = no patch at all.
        private sealed class Census { public int Calls, Zero, Unresolved, MinDraws = int.MaxValue, MaxDraws; public long SumDraws; }
        private static readonly Dictionary<string, Census> s_census = new Dictionary<string, Census>();
        private static float s_nextCensusLog;
        private const int MaxSteps = 256;

        // Words of FarRng per prefab Awake, proven by the census (-1 = unresolved). The replay reads it.
        internal static readonly Dictionary<int, int> AwakeWords = new Dictionary<int, int>();

        [ThreadStatic] private static int t_inVeg, t_instDepth;
        internal static void BeginPlaceVegetation() => t_inVeg++;                    // FatVanillaHooks' PlaceVegetation prefix
        internal static void EndPlaceVegetation() { if (t_inVeg > 0) t_inVeg--; }     // ... and its finalizer (runs on throw too)

        private static bool s_censusPatched;
        private struct CensusState { public bool Measure; public int A, B, C, D; }

        // Applied once, only when DrawCensus is on at ZoneSystem.Start (a debug probe; restart to remove).
        private static void PatchCensusIfOn()
        {
            if (s_censusPatched || DrawCensus == null || !DrawCensus.Value) return;
            var target = AccessTools.Method(typeof(UnityEngine.Object), nameof(UnityEngine.Object.Instantiate), new[] { typeof(UnityEngine.Object), typeof(Vector3), typeof(Quaternion) });
            if (target == null) { FiresLogger.LogInfo("[FarForest] census stands down: Object.Instantiate(Object, Vector3, Quaternion) not found."); return; }
            var h = new Harmony("com.Fire.FiresAdminTerrain.FarForestCensus");
            h.Patch(target, prefix: new HarmonyMethod(typeof(FarForest), nameof(CensusPrefix)), postfix: new HarmonyMethod(typeof(FarForest), nameof(CensusPostfix)));
            s_censusPatched = true;
            FiresLogger.LogInfo("[FarForest] DrawCensus on: Object.Instantiate measured while inside PlaceVegetation (PlaceVegetation's IL untouched); Debug table every 30 s.");
        }

        private static void CensusPrefix(UnityEngine.Object original, out CensusState __state)
        {
            __state = default;
            if (t_inVeg <= 0 || t_instDepth++ > 0 || !(original is GameObject)) return;
            __state.Measure = true;
            FarRngSelfTest.ReadState(out __state.A, out __state.B, out __state.C, out __state.D);
        }

        private static void CensusPostfix(UnityEngine.Object original, CensusState __state)
        {
            if (t_inVeg > 0 && t_instDepth > 0) t_instDepth--;
            if (!__state.Measure) return;
            var prefab = original as GameObject;
            if (prefab == null) return;
            FarRngSelfTest.ReadState(out int a1, out int b1, out int c1, out int d1);
            int draws = 0;
            var r = FarRng.FromState(__state.A, __state.B, __state.C, __state.D);
            while (!r.SameState(a1, b1, c1, d1) && draws <= MaxSteps) { r.NextU32(); draws++; }
            if (!s_census.TryGetValue(prefab.name, out var c)) s_census[prefab.name] = c = new Census();
            c.Calls++;
            int hash = prefab.name.GetStableHashCode();
            if (draws > MaxSteps) { c.Unresolved++; AwakeWords[hash] = -1; }
            else
            {
                if (draws == 0) c.Zero++;
                c.SumDraws += draws; c.MinDraws = Math.Min(c.MinDraws, draws); c.MaxDraws = Math.Max(c.MaxDraws, draws);
                if (!AwakeWords.TryGetValue(hash, out int known)) AwakeWords[hash] = draws;
                else if (known != draws) AwakeWords[hash] = -1;   // not constant: the replay can't consume a fixed count
            }
            if (Time.realtimeSinceStartup >= s_nextCensusLog)
            {
                s_nextCensusLog = Time.realtimeSinceStartup + 30f;
                LogCensus();
            }
        }

        private static void LogCensus()
        {
            int prefabs = s_census.Count, withDraws = s_census.Values.Count(c => c.MaxDraws > 0 || c.Unresolved > 0);
            var rows = s_census.Where(kv => kv.Value.MaxDraws > 0 || kv.Value.Unresolved > 0).OrderByDescending(kv => kv.Value.Calls)
                .Select(kv => $"{kv.Key} x{kv.Value.Calls}: words {(kv.Value.MinDraws == int.MaxValue ? "-" : kv.Value.MinDraws.ToString())}..{kv.Value.MaxDraws}{(kv.Value.Unresolved > 0 ? $", {kv.Value.Unresolved} unresolved" : "")}");
            FiresLogger.LogDebug($"[FarForest] census: {prefabs} vegetation prefab(s) created, {s_census.Values.Sum(c => c.Calls)} instance(s); {prefabs - withDraws} draw 0 words in Instantiate" +
                                 (withDraws > 0 ? "; drawing: " + string.Join("; ", rows) : "; none draws"));
        }
    }
}
