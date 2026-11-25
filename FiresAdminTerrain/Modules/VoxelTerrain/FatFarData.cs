using UnityEngine;
using VerdantsAscent.Terrain.Zones;

namespace VerdantsAscent.Terrain.Voxel
{
    // 0.2.1444 ([worldgen], lead 11-16: "ALL far-view DRAWING lives in EBM; FAT only SUPPLIES data through public APIs"). The far-terrain
    // DATA FAT hands to EBM through FatApi (TryFarHeight / TryFarColor). Nothing here draws, builds meshes, starts threads or touches
    // scene objects, so every call is safe from EBM's builder thread. Study notes: Tools\FARLANDS_TERRAIN_SOURCE_READ_20261116.md §5.
    internal static class FatFarData
    {
        /// <summary>
        /// Fires-generated (voxel-only) world: FAT's own FarQuilt draws the far ground there (unchanged), and heights come from FAT's
        /// generator, so the far-data calls answer false and EBM leaves the far ground to FAT.
        /// </summary>
        internal static bool FiresWorld => VoxelOnlyMode.Active;

        /// <summary>
        /// The height of a vanilla NEAR zone heightmap at (wx, wz), the way HeightmapBuilder.Build's non-distant branch makes it: the
        /// zone's four corner biomes, GetBiomeHeight once per distinct corner biome, then bilinear with smoothstep across the zone.
        /// A far tile built from this meets the loaded zone at the same height (vanilla's distant LOD samples differently, so it steps).
        /// World-gen only: add FatApi.TryFarEditDelta for player edits.
        /// </summary>
        internal static bool TryHeight(float wx, float wz, out float height)
        {
            height = 0f;
            WorldGenerator wg = WorldGenerator.instance;
            if (wg == null || FiresWorld) return false;
            // 0.2.1483 FIX ([worldgen], thread audit 11-18): the vanilla builder thread asks GetBiome / GetBiomeHeight inside BiomePaint's corner
            // scope, where FatBiomeResolver's redirect (an unlocked TileAnchorRegistry Dictionary read, FatBiomeResolver.cs:271), BiomeOverride and
            // super-flat island answers stand aside. EBM's tile thread calls this, so it runs in the same scope: thread-safe, and the same heights
            // the zones' Heightmaps get.
            BiomePaint.EnterCornerScope();
            try { height = ZoneHeight(wg, wx, wz); }
            finally { BiomePaint.ExitCornerScope(); }
            return true;
        }

        private static float ZoneHeight(WorldGenerator wg, float wx, float wz)
        {
            int zx = Mathf.FloorToInt((wx + 32f) / 64f), zz = Mathf.FloorToInt((wz + 32f) / 64f);
            float x0 = zx * 64f - 32f, z0 = zz * 64f - 32f;
            Heightmap.Biome b0 = wg.GetBiome(x0, z0), b1 = wg.GetBiome(x0 + 64f, z0);
            Heightmap.Biome b2 = wg.GetBiome(x0, z0 + 64f), b3 = wg.GetBiome(x0 + 64f, z0 + 64f);
            float h0 = wg.GetBiomeHeight(b0, wx, wz, out _);
            if (b1 == b0 && b2 == b0 && b3 == b0) return h0;
            float tx = DUtils.SmoothStep(0f, 1f, Mathf.Clamp01((wx - x0) / 64f)), tz = DUtils.SmoothStep(0f, 1f, Mathf.Clamp01((wz - z0) / 64f));
            float h1 = b1 == b0 ? h0 : wg.GetBiomeHeight(b1, wx, wz, out _);
            float h2 = b2 == b0 ? h0 : b2 == b1 ? h1 : wg.GetBiomeHeight(b2, wx, wz, out _);
            float h3 = b3 == b0 ? h0 : b3 == b1 ? h1 : b3 == b2 ? h2 : wg.GetBiomeHeight(b3, wx, wz, out _);
            return Mathf.Lerp(Mathf.Lerp(h0, h1, tx), Mathf.Lerp(h2, h3, tx), tz);
        }

        /// <summary>
        /// The splat colour of the ground at (wx, wz) as FAT's far tiles wear it, as terrain splat weights (Heightmap.GetBiomeColor
        /// convention: r Swamp, g Mountain / snow, b Black Forest, a Plains; Ashlands r+a, Mistlands b+a, 0 = Meadows):
        /// the vanilla distant-LOD colour (the biome SECTOR's colour, alt-biome texture override included), then FAT biome paint
        /// (a painted circle's ground), then Vedr winter (same snow as the near ground). False on Fires-generated worlds.
        /// Environment-box ground is not included (env boxes are scene objects); its world biome's colour is given.
        /// </summary>
        internal static bool TryColor(float wx, float wz, out Color32 color)
        {
            color = default;
            WorldGenerator wg = WorldGenerator.instance;
            if (wg == null || FiresWorld) return false;
            color = SectorSplat(wg.GetBiomeSector(wx, wz));
            if (BiomePaint.TryGround(wx, wz, out var ground)) color = SplatOf(ground);
            color = FatSeasons.WinterizeAnyThread(color, wx, wz, wg);
            return true;
        }

        /// <summary>
        /// 0.2.1492 (FatApi v5, [lead] + [visual] EBM 1.2.470 draws far snow in its shader as class x Vedr fill): TryColor WITHOUT the winter
        /// step: the vanilla distant-LOD colour (biome sector) then FAT biome paint, the TRUE ground colour. Any thread (pure tables).
        /// </summary>
        internal static bool TryTrueColor(float wx, float wz, out Color32 color)
        {
            color = default;
            WorldGenerator wg = WorldGenerator.instance;
            if (wg == null || FiresWorld) return false;
            color = SectorSplat(wg.GetBiomeSector(wx, wz));
            if (BiomePaint.TryGround(wx, wz, out var ground)) color = SplatOf(ground);
            return true;
        }

        // 0.2.1446 FIX ([worldgen], Fire 11-16 "no snow on the ground" after EBM's far tiles came on): NEVER call Heightmap.GetBiomeColor off the
        // main thread. Vedr's snowy terrain patches all three overloads (Vedr LandscapeSeasons.cs:369-396): the (float,float) and (BiomeSector)
        // overloads count a plain static depth (_paintingVanillaTerrain++ / --, not atomic) that the (Biome) prefix reads to paint winter biomes
        // white. 1444's TryColor called GetBiomeColor(BiomeSector) ~450k times from EBM's builder thread while the main thread painted chunks:
        // lost ++/-- left Vedr's counter off by one, so main-thread chunk paints saw depth 0 and drew summer colours (bare rock / sand).
        // These two are vanilla's own table (Heightmap.GetBiomeColor(Biome) / (BiomeSector), assembly_valheim Heightmap.cs:647-672), pure and
        // unpatched. Winter is added by FatSeasons.WinterizeAnyThread from Vedr's mask, so the far tiles still wear snow.
        internal static Color32 SplatOf(Heightmap.Biome biome)
        {
            switch (biome)
            {
                case Heightmap.Biome.Swamp: return new Color32(255, 0, 0, 0);
                case Heightmap.Biome.Mountain: return new Color32(0, 255, 0, 0);
                case Heightmap.Biome.BlackForest: return new Color32(0, 0, 255, 0);
                case Heightmap.Biome.Plains: return new Color32(0, 0, 0, 255);
                case Heightmap.Biome.AshLands: return new Color32(255, 0, 0, 255);
                case Heightmap.Biome.DeepNorth: return new Color32(0, 255, 0, 0);
                case Heightmap.Biome.Mistlands: return new Color32(0, 0, 255, 255);
                default: return new Color32(0, 0, 0, 0);
            }
        }

        internal static Color32 SectorSplat(BiomeSector sector)
        {
            if (sector == null) return SplatOf(Heightmap.Biome.Meadows);
            var alts = sector.AltBiomes;
            if (alts != null)
                foreach (AltBiome alt in alts)
                    if (alt != null && alt.m_terrainTextureOverride != Heightmap.Biome.None) return SplatOf(alt.m_terrainTextureOverride);
            return SplatOf(sector.Biome);
        }
    }
}
