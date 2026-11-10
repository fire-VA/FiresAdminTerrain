using System.Diagnostics;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Performance
{
    // 0.2.1409 ([lead] 11-08, FDT 1.1.1154 [CreateCensus] on Fire's 0107t+u: "_TerrainCompiler 27x 166.0 ms avg 6.148 max 15.60",
    // all of it inside TerrainComp.Awake; a compiler with no terrain data costs 0.4-0.5 ms): read-only split of vanilla TerrainComp.Awake
    // (TerrainComp.cs:36) so the cut is chosen from numbers, not a guess. Vanilla Awake = FindHeightmap + Initialize (5 arrays + the paint
    // mask's GetPixels) + CheckLoad -> Load (decompress, every Load prefix/postfix incl. FAT's and FAP's) -> m_hmap.Poke() (delayed 0 =
    // Heightmap.Regenerate at once: Generate (ApplyModifiers), RebuildCollisionMesh, RebuildRenderMesh; FGN's TerrainRebuildSpread may queue
    // it instead) -> ClutterSystem.ResetGrass. Also counts how often the compiler's rebuild hits a heightmap that was already built (its own
    // OnEnable build without the compiler's changes), and how many frames earlier: the "built twice" share.
    // Debug line every 30 s while compilers were created; Info line once. Nothing is changed.
    internal static class TerrainCompAwakeTiming
    {
        private enum S { Awake, Init, Load, Regen, Generate, Collision, Render, Grass, Count }
        private static readonly long[] s_t = new long[(int)S.Count];
        private static readonly long[] s_start = new long[(int)S.Count];
        private static long s_worstAwake;
        private static int s_comps, s_loaded, s_regenRan, s_regenHeld, s_builtBefore, s_builtSameFrame, s_framesSum, s_grassCalls;
        private static int s_depth;
        private static bool s_said;
        private static long s_logAt;
        private static Heightmap s_awakeMap;
        private static readonly System.Collections.Generic.Dictionary<Heightmap, int> s_lastBuilt = new System.Collections.Generic.Dictionary<Heightmap, int>();

        private static bool In => s_depth > 0;
        private static void Begin(S s) => s_start[(int)s] = Stopwatch.GetTimestamp();
        private static void End(S s) { long st = s_start[(int)s]; if (st != 0) { s_t[(int)s] += Stopwatch.GetTimestamp() - st; s_start[(int)s] = 0; } }

        [HarmonyPatch(typeof(TerrainComp), "Awake")]
        private static class Patch_Awake
        {
            [HarmonyPrefix, HarmonyPriority(Priority.First + 100)]
            private static void Prefix(TerrainComp __instance)
            {
                s_depth++;
                if (s_depth != 1) return;
                s_awakeMap = __instance != null ? Heightmap.FindHeightmap(__instance.transform.position) : null;
                Begin(S.Awake);
            }

            [HarmonyFinalizer, HarmonyPriority(Priority.Last - 100)]
            private static System.Exception Finalizer(System.Exception __exception)
            {
                if (s_depth == 1)
                {
                    long st = s_start[(int)S.Awake];
                    End(S.Awake);
                    long d = st != 0 ? Stopwatch.GetTimestamp() - st : 0;
                    if (d > s_worstAwake) s_worstAwake = d;
                    s_comps++;
                    s_awakeMap = null;
                    Report();
                }
                if (s_depth > 0) s_depth--;
                return __exception;
            }
        }

        [HarmonyPatch(typeof(TerrainComp), "Initialize")]
        private static class Patch_Init
        {
            [HarmonyPrefix, HarmonyPriority(Priority.First + 100)] private static void Prefix() { if (In) Begin(S.Init); }
            [HarmonyFinalizer, HarmonyPriority(Priority.Last - 100)] private static System.Exception Finalizer(System.Exception __exception) { if (In) End(S.Init); return __exception; }
        }

        // First/Last so every Load prefix and postfix (FAT BiomePaintPersistence, FatTrailFast, FAP VAClutterPaintTool) is inside.
        [HarmonyPatch(typeof(TerrainComp), "Load")]
        private static class Patch_Load
        {
            [HarmonyPrefix, HarmonyPriority(Priority.First + 100)] private static void Prefix() { if (In) Begin(S.Load); }
            [HarmonyPostfix, HarmonyPriority(Priority.Last - 100)] private static void Postfix(bool __result) { if (In && __result) s_loaded++; }
            [HarmonyFinalizer, HarmonyPriority(Priority.Last - 100)] private static System.Exception Finalizer(System.Exception __exception) { if (In) End(S.Load); return __exception; }
        }

        // Every Regenerate is noted (frame per heightmap) so a compiler's rebuild can say whether the tile was already built.
        // Above FGN's First prefix so its queueing decision is inside the timing; __runOriginal says whether the rebuild ran here.
        [HarmonyPatch(typeof(Heightmap), nameof(Heightmap.Regenerate))]
        private static class Patch_Regen
        {
            [HarmonyPrefix, HarmonyPriority(Priority.First + 100)]
            private static void Prefix(Heightmap __instance)
            {
                if (!In || __instance == null || !ReferenceEquals(__instance, s_awakeMap)) return;
                Begin(S.Regen);
                if (s_lastBuilt.TryGetValue(__instance, out int f))
                {
                    s_builtBefore++;
                    int ago = Time.frameCount - f;
                    s_framesSum += ago;
                    if (ago == 0) s_builtSameFrame++;
                }
            }

            [HarmonyPostfix, HarmonyPriority(Priority.Last - 100)]
            private static void Postfix(Heightmap __instance, bool __runOriginal)
            {
                if (__instance == null || __instance.IsDistantLod) return;
                if (__runOriginal)
                {
                    if (s_lastBuilt.Count > 4096) s_lastBuilt.Clear();
                    s_lastBuilt[__instance] = Time.frameCount;
                }
                if (!In || !ReferenceEquals(__instance, s_awakeMap)) return;
                End(S.Regen);
                if (__runOriginal) s_regenRan++; else s_regenHeld++;
            }
        }

        [HarmonyPatch(typeof(Heightmap), "Generate")]
        private static class Patch_Generate
        {
            [HarmonyPrefix, HarmonyPriority(Priority.First + 100)] private static void Prefix(Heightmap __instance) { if (In && ReferenceEquals(__instance, s_awakeMap)) Begin(S.Generate); }
            [HarmonyFinalizer, HarmonyPriority(Priority.Last - 100)] private static System.Exception Finalizer(Heightmap __instance, System.Exception __exception) { if (In && ReferenceEquals(__instance, s_awakeMap)) End(S.Generate); return __exception; }
        }

        [HarmonyPatch(typeof(Heightmap), "RebuildCollisionMesh")]
        private static class Patch_Collision
        {
            [HarmonyPrefix, HarmonyPriority(Priority.First + 100)] private static void Prefix(Heightmap __instance) { if (In && ReferenceEquals(__instance, s_awakeMap)) Begin(S.Collision); }
            [HarmonyFinalizer, HarmonyPriority(Priority.Last - 100)] private static System.Exception Finalizer(Heightmap __instance, System.Exception __exception) { if (In && ReferenceEquals(__instance, s_awakeMap)) End(S.Collision); return __exception; }
        }

        [HarmonyPatch(typeof(Heightmap), "RebuildRenderMesh")]
        private static class Patch_Render
        {
            [HarmonyPrefix, HarmonyPriority(Priority.First + 100)] private static void Prefix(Heightmap __instance) { if (In && ReferenceEquals(__instance, s_awakeMap)) Begin(S.Render); }
            [HarmonyFinalizer, HarmonyPriority(Priority.Last - 100)] private static System.Exception Finalizer(Heightmap __instance, System.Exception __exception) { if (In && ReferenceEquals(__instance, s_awakeMap)) End(S.Render); return __exception; }
        }

        [HarmonyPatch(typeof(ClutterSystem), nameof(ClutterSystem.ResetGrass))]
        private static class Patch_Grass
        {
            [HarmonyPrefix, HarmonyPriority(Priority.First + 100)] private static void Prefix() { if (In) { Begin(S.Grass); s_grassCalls++; } }
            [HarmonyFinalizer, HarmonyPriority(Priority.Last - 100)] private static System.Exception Finalizer(System.Exception __exception) { if (In) End(S.Grass); return __exception; }
        }

        [HarmonyPatch(typeof(Heightmap), "OnDestroy")]
        private static class Patch_HmDestroy
        {
            private static void Postfix(Heightmap __instance) { if (__instance != null) s_lastBuilt.Remove(__instance); }
        }

        private static void Report()
        {
            if (!s_said)
            {
                s_said = true;
                FiresLogger.LogInfo($"[TCAwake] timing TerrainComp.Awake (terrain compiler create) by step, Debug every 30 s (FAT {(global::VerdantsAscent.FiresAdminTerrain.PluginVersion)})");
            }
            long now = Stopwatch.GetTimestamp();
            if (s_logAt == 0) { s_logAt = now + 30 * Stopwatch.Frequency; return; }
            if (now < s_logAt) return;
            s_logAt = now + 30 * Stopwatch.Frequency;
            if (FiresLogger.DebugEnabled && s_comps > 0)
            {
                double k = 1000.0 / Stopwatch.Frequency;
                double aw = s_t[(int)S.Awake] * k, init = s_t[(int)S.Init] * k, load = s_t[(int)S.Load] * k, regen = s_t[(int)S.Regen] * k,
                       gen = s_t[(int)S.Generate] * k, col = s_t[(int)S.Collision] * k, ren = s_t[(int)S.Render] * k, grass = s_t[(int)S.Grass] * k;
                double rest = aw - init - load - regen - grass;
                FiresLogger.LogDebug($"[TCAwake] last 30 s: {s_comps} compiler(s) created ({s_loaded} with terrain data), Awake {aw:0.0} ms (avg {aw / s_comps:0.00}, worst {s_worstAwake * k:0.00}): "
                    + $"Initialize {init:0.0}, Load+patches {load:0.0}, Regenerate {regen:0.0} (ran {s_regenRan}, held by FGN {s_regenHeld}; Generate {gen:0.0}, collision {col:0.0}, render {ren:0.0}, other {regen - gen - col - ren:0.0}), "
                    + $"ResetGrass {grass:0.0} ({s_grassCalls}x), rest {rest:0.0}; tile already built before its compiler's rebuild: {s_builtBefore} "
                    + $"({s_builtSameFrame} the same frame, avg {(s_builtBefore > 0 ? (double)s_framesSum / s_builtBefore : 0):0.0} frame(s) earlier) (FAT {(global::VerdantsAscent.FiresAdminTerrain.PluginVersion)})");
            }
            System.Array.Clear(s_t, 0, s_t.Length);
            s_worstAwake = 0; s_comps = s_loaded = s_regenRan = s_regenHeld = s_builtBefore = s_builtSameFrame = s_framesSum = s_grassCalls = 0;
        }
    }
}
