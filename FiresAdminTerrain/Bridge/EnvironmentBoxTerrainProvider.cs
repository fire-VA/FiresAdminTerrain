using UnityEngine;
using FiresCore.Bridge;
using FiresCore.Utilities;

namespace VerdantsAscent.Utilities
{
    /// <summary>
    /// FAT-side implementation of the Core env-box terrain seam. The portable env-box ENGINE now lives in Core
    /// (FiresCore.Utilities.EnvironmentBoxController) and drives env/skybox/enclosure itself; everything that
    /// depends on FAT's heightmap and clutter systems is wrapped here. Registered once in
    /// FiresAdminTerrain.Awake. The retained FAT pieces — EnvironmentBoxPatches (heightmap/clutter/biome
    /// patches) and EnvironmentBoxClutterSpawner (per-box biome clutter)
    /// stay in FAT and are reached only through this provider.
    /// </summary>
    public sealed class EnvironmentBoxTerrainProvider : IEnvironmentBoxTerrainProvider
    {
        public void OnBoxEnter(EnvironmentBoxController box, Heightmap.Biome biome, bool affectsCustomPaint, bool affectsVanillaPaint)
        {
            if (box == null) return;

            // Tell the paint patches a biome override is active (gates RebuildRenderMesh + paint overrides).
            EnvironmentBoxPatches.SetHasBiomeOverrideBoxes(true);

            if (!affectsVanillaPaint) return;

            Bounds b = box.GetWorldBounds();
            b.Expand(10f);
            EnvironmentBoxPatches.RefreshHeightmapsInBounds(b);
            EnvironmentBoxPatches.RefreshClutterInBounds(b);

            // Spawn biome-appropriate clutter via the FAT spawner (attached on demand — the controller no longer
            // auto-adds it). Bypasses vanilla altitude restrictions.
            var spawner = box.GetComponent<EnvironmentBoxClutterSpawner>() ?? box.gameObject.AddComponent<EnvironmentBoxClutterSpawner>();
            spawner.SpawnClutterForBiome(biome);
        }

        public void OnBoxExit(EnvironmentBoxController box)
        {
            if (box == null) return;

            var spawner = box.GetComponent<EnvironmentBoxClutterSpawner>();
            if (spawner != null) spawner.ClearAllClutter();

            EnvironmentBoxPatches.RestoreModifiedHeightmaps();

            Bounds b = box.GetWorldBounds();
            b.Expand(10f);
            EnvironmentBoxPatches.RefreshHeightmapsInBounds(b);
            EnvironmentBoxPatches.RefreshClutterInBounds(b);

            EnvironmentBoxPatches.SetHasBiomeOverrideBoxes(false);
        }

        public void RefreshTerrainInBounds(Bounds b)
        {
            EnvironmentBoxPatches.RefreshHeightmapsInBounds(b);
        }

        public void RefreshClutterInBounds(Bounds b)
        {
            EnvironmentBoxPatches.RefreshClutterInBounds(b);
        }

        public void RestoreTerrainInBounds(EnvironmentBoxController box)
        {
            if (box == null) return;

            var spawner = box.GetComponent<EnvironmentBoxClutterSpawner>();
            if (spawner != null) spawner.ClearAllClutter();

            EnvironmentBoxPatches.RestoreModifiedHeightmaps();

            Bounds b = box.GetWorldBounds();
            b.Expand(10f);
            EnvironmentBoxPatches.RefreshHeightmapsInBounds(b);
            EnvironmentBoxPatches.RefreshClutterInBounds(b);
        }
    }
}
