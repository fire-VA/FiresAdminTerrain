using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // 0.2.1086 (Fire 10-26: "snow it still not working ... painted biomes should get normal goddamn biome logic"): his painted Deep
    // North hut on SuperFlatSkyCaves2 had "[PieceSnow] ... WearNTear biome None" next to "heightmap biome DeepNorth": vanilla
    // WearNTear.UpdateBiome takes its biome only from ZoneSystem.GetGroundData's heightmap, a raycast from 5000 m down whose hit
    // on voxel ground (or a sky island above) is a collider with no Heightmap component, so m_heightmap stays null, m_biome stays
    // None and the Deep North snow build-up (WearNTear.UpdateWear: m_biome == DeepNorth) never runs: a bare roof.
    //  * WearNTear: when UpdateBiome leaves None, the piece takes WorldGenerator.GetBiome(its position) (the paint-first chain:
    //    BiomeOverride / BiomePaint, then super flat; caves and islands keep their own answer). On super flat a piece re-reads its
    //    biome whenever the paint changes (BiomePaintVersion).
    //  * fat_biome_probe (client, cheat) and a Debug line every 30 s at the player: every biome entry point's answer and the snow
    //    chain (Core DepthAt, a fresh zone fill, the nearest piece's WearNTear biome).
    internal static class FatSuperFlatBiomeProbe
    {
        private static readonly AccessTools.FieldRef<WearNTear, Heightmap.Biome> PieceBiome = AccessTools.FieldRefAccess<WearNTear, Heightmap.Biome>("m_biome");
        private static readonly AccessTools.FieldRef<Heightmap, BiomeSector[]> Corners = AccessTools.FieldRefAccess<Heightmap, BiomeSector[]>("m_cornerBiomes");
        private static readonly List<WearNTear> AllPieces = (List<WearNTear>)AccessTools.Field(typeof(WearNTear), "s_allInstances").GetValue(null);
        private static readonly ConditionalWeakTable<WearNTear, StrongBox<int>> s_pieceVersion = new ConditionalWeakTable<WearNTear, StrongBox<int>>();
        private static int s_resolved, s_refreshed;
        private static float s_pieceLogAt;

        [HarmonyPatch(typeof(WearNTear), "UpdateBiome")]
        private static class PieceBiomePatch
        {
            private static void Postfix(WearNTear __instance)
            {
                var wg = WorldGenerator.instance;
                if (wg == null) return;
                ref Heightmap.Biome biome = ref PieceBiome(__instance);
                bool none = biome == Heightmap.Biome.None;
                if (!none && !FatSuperFlat.On) return;
                int v = Zones.BiomePaintVersion.Current;
                var stamp = s_pieceVersion.GetOrCreateValue(__instance);
                if (!none && stamp.Value == v) return;
                stamp.Value = v;
                var before = biome;
                var now = wg.GetBiome(__instance.transform.position);
                if (now == Heightmap.Biome.None || now == before) return;
                biome = now;
                if (none) s_resolved++; else s_refreshed++;
                if (FiresLogger.DebugEnabled && Time.unscaledTime >= s_pieceLogAt)
                {
                    s_pieceLogAt = Time.unscaledTime + 30f;
                    var p = __instance.transform.position;
                    FiresLogger.LogDebug($"[FatSuperFlat] piece biome: {__instance.name} at {p.x:0},{p.y:0},{p.z:0} {before} -> {now} (GetBiome(position); no heightmap under it = {none}); {s_resolved} resolved from None, {s_refreshed} refreshed on paint change this session");
                }
            }
        }

        [HarmonyPatch(typeof(Terminal), "InitTerminal")]
        private static class Command
        {
            private static bool s_registered;
            private static void Postfix()
            {
                if (s_registered) return;
                s_registered = true;
                new Terminal.ConsoleCommand("fat_biome_probe",
                    "[FiresAdminTerrain] every biome entry point's answer at the player (paint vs world), plus the snow chain",
                    args => args.Context?.AddString(Probe(true)), isCheat: true);
            }
        }

        // The 30 s Debug line at the player (client only).
        [HarmonyPatch(typeof(Player), "Update")]
        private static class Tick
        {
            private static float s_at;
            private static void Postfix(Player __instance)
            {
                if (__instance != Player.m_localPlayer || !FiresLogger.DebugEnabled || !FatDiag.BiomePaint || Time.unscaledTime < s_at) return;   // 0.2.1104: the 30 s per-entry probe (11 lines) under Diagnostics BiomePaint; fat_biome_probe still prints on demand
                s_at = Time.unscaledTime + 30f;
                Probe(false);
            }
        }

        private static string Probe(bool info)
        {
            var player = Player.m_localPlayer;
            var wg = WorldGenerator.instance;
            if (player == null || wg == null) return "[FatSuperFlat] biome probe: no player / world";
            Vector3 p = player.transform.position;
            float x = p.x, z = p.z;
            string paint = Zones.BiomePaint.TryPaintedSectorAnyScope(x, z, out var pb, out _) ? pb.ToString() : "none";
            string head = $"[FatSuperFlat] biome at {x:0},{z:0}: ";
            string tail = $" (paint {paint})";
            var lines = new List<string>();
            void Row(string entry, string answer) => lines.Add(head + entry + " -> " + answer + tail);

            Row("WorldGenerator.GetBiome(x,z)", wg.GetBiome(x, z).ToString());
            Row("WorldGenerator.GetBiome(position)", wg.GetBiome(p).ToString());
            Row("WorldGenerator.GetBiome(x,0,z) [Core snow samples]", wg.GetBiome(new Vector3(x, 0f, z)).ToString());
            Row("WorldGenerator.GetBiomeSector(x,z)", wg.GetBiomeSector(x, z)?.Biome.ToString() ?? "null");
            Row("WorldGenerator.GetBiomeSector(position)", wg.GetBiomeSector(p)?.Biome.ToString() ?? "null");
            var hm = Heightmap.FindHeightmap(p);
            Row("Heightmap.FindBiome", hm != null ? Heightmap.FindBiome(p).ToString() : "None (no heightmap)");
            var corners = hm != null ? Corners(hm) : null;
            Row("heightmap corner biomes", corners == null ? "none" : string.Join("/", System.Array.ConvertAll(corners, c => c?.Biome.ToString() ?? "null")));
            Vector3 g = p;
            string ground = "no ZoneSystem";
            if (ZoneSystem.instance != null)
            {
                ZoneSystem.instance.GetGroundData(ref g, out _, out var gb, out _, out var ghm);
                ground = ghm != null ? gb.ToString() : "None (ray hit no heightmap)";
            }
            Row("ZoneSystem.GetGroundData", ground);
            Row("Player.GetCurrentBiome", player.GetCurrentBiome().ToString());
            Row("EnvMan.GetCurrentBiome", EnvMan.instance != null ? EnvMan.instance.GetCurrentBiome().ToString() : "no EnvMan");

            WearNTear nearest = null;
            float best = float.MaxValue;
            if (AllPieces != null)
                foreach (var w in AllPieces)
                {
                    if (w == null) continue;
                    float d = (w.transform.position - p).sqrMagnitude;
                    if (d < best) { best = d; nearest = w; }
                }
            string piece = nearest == null ? "no piece" : $"{PieceBiome(nearest)} ({nearest.name} {Mathf.Sqrt(best):0} m)";
            Row("WearNTear biome of nearest piece", piece);
            float depth = VoxelSnowDepth.ProbeDepthMetres(p);
            int fill = VoxelSnowDepth.ProbeZoneFill(ZoneSystem.GetZone(p));
            lines.Add($"[FatSuperFlat] snow at {x:0},{z:0}: Core DepthAt {(depth < 0f ? "unbound" : depth.ToString("0.00") + " m")}, zone fill {(fill < 0 ? "unbound" : fill + "/1024 texels")}, WearNTear biome of nearest piece {piece}");
            string s = string.Join("\n", lines);
            if (info) FiresLogger.LogInfo(s); else FiresLogger.LogDebug(s);
            return s;
        }
    }
}
