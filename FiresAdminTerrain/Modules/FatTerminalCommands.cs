using System;
using HarmonyLib;

namespace VerdantsAscent.Terrain
{
    // 0.2.1501 ([2ic] / [lead] load-time candidate after the 1498 [FatLoad] row "Harmony patch classes 1692 ms"): FAT had 16 separate Harmony patch
    // classes on Terminal.InitTerminal (13 compiled in every build, 2 only with the Testing folder, 1 not compiled at all). Harmony rebuilds the
    // patched method each time another patch is added to it, so every extra class on the same target is paid again at start-up. The 13 always-compiled
    // ones now have NO attribute of their own; this ONE postfix calls their bodies, unchanged, in the order their files compile (csproj order).
    // Each body keeps its own "registered once" guard. Each call is wrapped on its own, so one failing registration cannot stop the others
    // (a throwing postfix used to abort the rest of the chain). The two Testing classes (FatCircleTest, FatWaterPersistTests) keep their own patch,
    // because Modules\Testing is compiled only when AdminToolsTest.cs exists, and a call from here would not build without it.
    [HarmonyPatch(typeof(Terminal), nameof(Terminal.InitTerminal))]
    internal static class FatTerminalCommands
    {
        private static void Postfix()
        {
            Run("placed water probe", global::VerdantsAscent.Water.Core.PlacedWaterProbe.Register.Postfix);
            Run("placed water layers", global::VerdantsAscent.Water.Core.PlacedWaterLayersCommand.Register.Postfix);
            Run("cell probe", global::VerdantsAscent.Terrain.Voxel.FatCellProbe.Postfix);
            Run("cultivate lumps", global::VerdantsAscent.Terrain.Voxel.FatCultivateLumps.Command.Postfix);
            Run("render dump", global::VerdantsAscent.Terrain.Voxel.FatRenderDump.Command.Postfix);
            Run("water brush + debug commands", global::VerdantsAscent.Terrain.FatVanillaHooks.Terminal_InitTerminal.Postfix);
            Run("voxel zone look", global::VerdantsAscent.Terrain.Voxel.VoxelZoneLook.ShaderCommand.Register);
            Run("biome override", global::VerdantsAscent.Terrain.Zones.BiomeOverride.Command.Register);
            Run("super flat biome probe", global::VerdantsAscent.Terrain.Voxel.FatSuperFlatBiomeProbe.Command.Postfix);
            Run("river check", global::VerdantsAscent.Water.Tools.RiverCheckCommand.Register.Postfix);
#if FIRES_TESTS
            // 0.2.1502: test-only files (csproj: compiled, with FIRES_TESTS defined, only when Modules\Testing\AdminToolsTest.cs exists). 1501 called
            // the first three unconditionally, which builds here but not from a clone without the Testing folder; they are inside FIRES_TESTS now.
            // The two Testing classes (FatCircleTest, FatWaterPersistTests) are merged here too, so FAT has ONE InitTerminal patch in every build.
            Run("wet memo check", global::VerdantsAscent.Terrain.Voxel.FatWetMemoCheck.Postfix);
            Run("shore poses", global::VerdantsAscent.Terrain.Voxel.FatShorePoses.Postfix);
            Run("content tests", global::VerdantsAscent.Terrain.Zones.FatContentTests.Postfix);
            Run("circle test", global::VerdantsAscent.Terrain.Testing.FatCircleTest.Patch_Register.Postfix);
            Run("water persist tests", global::VerdantsAscent.Terrain.Testing.FatWaterPersistTests.Postfix);
#endif
        }

        private static void Run(string what, Action register)
        {
            try { register(); }
            catch (Exception ex) { FiresLogger.LogWarning($"[FatTerminal] console commands '{what}' failed to register: {ex.Message}"); }
        }
    }
}
