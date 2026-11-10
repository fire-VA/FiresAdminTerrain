using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // 0.2.1104 (overnight #6, RAM for low-end PCs): one line per world load naming FAT's biggest resident statics, what they
    // hold now and what the same data took before 1104's lossless cuts (the minimap's whole pre-stamp copy, the water
    // guards' bool-per-cell mask).
    internal static class FatRam
    {
        private static float s_at = -1f;
        private static bool s_said;

        [HarmonyPatch(typeof(ZoneSystem), "Update")]
        private static class Patch_ZoneSystem_Update
        {
            private static void Postfix()
            {
                if (s_said) return;
                if (s_at < 0f) { s_at = Time.realtimeSinceStartup + 60f; return; }
                if (Time.realtimeSinceStartup < s_at) return;
                s_said = true;
                try { Say(); } catch (System.Exception ex) { FiresLogger.LogDebug($"[FatRam] bail: {ex.Message}"); }
            }
        }

        [HarmonyPatch(typeof(ZNet), "OnDestroy")]
        private static class Patch_ZNet_OnDestroy
        {
            private static void Postfix() { s_said = false; s_at = -1f; }
        }

        private static string Mb(long bytes) => (bytes / (1024d * 1024d)).ToString("0.00");

        private static void Say()
        {
            var grid = FiresWorldGenBase.GridShorts;
            long gridB = grid != null ? grid.Length * 2L : 0;
            long classB = FiresWorldGenBase.ClassBytes, lavaB = FiresWorldGenBase.LavaBytes;
            long mapNow = FiresMinimapIslands.ResidentBytes, mapWas = FiresMinimapIslands.DenseBytes;
            long guardNow = FiresWaterGuards.ResidentBytes, guardWas = FiresWaterGuards.DenseBytes;
            long now = gridB + classB + lavaB + mapNow + guardNow;
            long was = gridB + classB + lavaB + mapWas + guardWas;
            FiresLogger.LogInfo($"[FatRam] statics: minimap stamp restore Color32+int[{mapNow / 8}] = {Mb(mapNow)} MB (was whole map copy {Mb(mapWas)} MB); "
                + $"water guards byte[bits] = {Mb(guardNow)} MB (was bool[] {Mb(guardWas)} MB); _gridS short[{(grid != null ? grid.Length : 0)}] = {Mb(gridB)} MB; "
                + $"_classS byte[] = {Mb(classB)} MB; _lavaS byte[] = {Mb(lavaB)} MB; total {Mb(now)} MB (was {Mb(was)} MB)");
        }
    }
}
