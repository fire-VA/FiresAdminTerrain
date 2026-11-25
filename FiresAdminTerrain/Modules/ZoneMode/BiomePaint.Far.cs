using UnityEngine;

namespace VerdantsAscent.Terrain.Zones
{
    // 0.2.1483 ([worldgen], Fire 11-18: "ebm would need to be able to properly read the world information from FAT when both are installed"):
    // the paint pieces FatFarGen (FatApi far-gen calls, EBM's far vegetation workers) needs, as the SAME code the patches run. No new logic:
    //  - the corner scope vanilla's builder thread runs in (HeightmapBuilder.Build prefix, BiomePaint.Sectors.cs:239), for FatFarData.TryHeight;
    //  - Paint() = the GetBiomeSector postfix's own decision (newest circle, then a provider), BiomePaint.Sectors.cs:44;
    //  - GroundsIn() = the Heightmap.HaveBiome postfix's painted grounds over a zone square, BiomePaint.Ground.cs:50.
    // All read the published snapshot (volatile reference, swapped whole) and [ThreadStatic] scopes; counters are Interlocked; PlainSector locks.
    internal static partial class BiomePaint
    {
        internal static void EnterCornerScope() => t_cornerScope++;
        internal static void ExitCornerScope() { if (t_cornerScope > 0) t_cornerScope--; }

        internal static void FarPaintSector(float wx, float wz, ref BiomeSector result) => Paint(wx, wz, ref result);

        internal static Heightmap.Biome FarZoneGrounds(Vector3 centre, float size) => Any ? GroundsIn(centre, size) : Heightmap.Biome.None;
    }
}
