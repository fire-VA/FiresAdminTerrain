using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using VerdantsAscent.Terrain.Voxel;
using Object = UnityEngine.Object;

namespace VerdantsAscent.Terrain.Zones
{
    // While the local player holds the Chunk Hammer with a seed to plant: a see-through ghost of the closed zone a strike
    // would raise, shaped like the land that will rise there and coloured by its biome. A location seed's land is
    // chosen by the server from its location list, so its ghost is a flat sheet in the seed's biome colour. It turns
    // red where the strike would be refused.
    internal static class ZoneModeSeedGhost
    {
        private const float TickSeconds = 0.1f;
        private const int Cells = 32;
        private const float Lift = 0.4f;
        private const float WaterLift = 0.15f;
        private const float FlatLift = 2f;
        private const byte Alpha = 120;

        private static GameObject s_ghost;
        private static Mesh s_mesh;
        private static bool s_visible;
        private static Vector2s s_zone;
        private static string s_seed;
        private static bool s_valid;
        private static int s_registryVersion = -1;
        private static bool s_sourceKnown;
        private static float s_nextTick;
        private static readonly List<Vector3> s_verts = new List<Vector3>((Cells + 1) * (Cells + 1));
        private static readonly List<Color32> s_colors = new List<Color32>((Cells + 1) * (Cells + 1));
        private static readonly List<int> s_tris = new List<int>(Cells * Cells * 6);

        internal static void ClientTick()
        {
            if (Time.time < s_nextTick) return;
            s_nextTick = Time.time + TickSeconds;
            var player = Player.m_localPlayer;
            if (player == null || !ZoneModeChunkSpawner.TryAimedSeed(player, out var seed)
                || !ZoneModeCommands.TryGetLookedAtZone(out var zone) || ZoneModeRegistry.IsOpen(zone))
            {
                Hide();
                return;
            }
            bool valid = ZoneModeChunkSpawner.Check(zone, player.transform.position, true) == null;
            if (s_visible && zone.x == s_zone.x && zone.y == s_zone.y && seed.Prefab == s_seed && valid == s_valid
                && s_registryVersion == ZoneModeRegistry.Version && (s_sourceKnown || !ZoneModeSwaps.Ready)) return;
            Show(zone, seed, valid);
        }

        private static void Show(Vector2s zone, ChunkSeed seed, bool valid)
        {
            if (!EnsureGhost()) return;
            s_zone = zone;
            s_seed = seed.Prefab;
            s_valid = valid;
            s_registryVersion = ZoneModeRegistry.Version;
            if (seed.IsLocation)
            {
                s_sourceKnown = true;
                BuildFlat(zone, BiomeColor(seed.Tier.Biome), valid);
            }
            else if (ZoneModePark.TryGetParked(zone, out var parked) && ZoneModePark.SeedMatches(seed, zone, parked))
            {
                s_sourceKnown = true;
                if (parked.Swapped) BuildMesh(zone, parked.Source, parked.Shift, valid);
                else BuildMesh(zone, zone, default, valid);
            }
            else if (seed.RestoresOnly)
            {
                s_sourceKnown = true;
                BuildMesh(zone, zone, default, valid);
            }
            else
            {
                s_sourceKnown = ZoneModeSwaps.TryChooseSource(zone, seed.Tier.Biome, out var source, out bool faithful);
                var shift = s_sourceKnown && !faithful ? new ZoneShift(ZoneModeLift.Choose(zone, source, default, out _), 0f, 0f) : default;
                BuildMesh(zone, s_sourceKnown ? source : zone, shift, valid);
            }
            s_ghost.SetActive(true);
            s_visible = true;
        }

        private static void BuildFlat(Vector2s zone, Color32 color, bool valid)
        {
            s_verts.Clear();
            s_colors.Clear();
            s_tris.Clear();
            float size = ZoneOpenSet.ZoneSize, x0 = zone.x * size - size * 0.5f, z0 = zone.y * size - size * 0.5f;
            float y = FiresWaterfalls.SeaLevel + FlatLift;
            for (int j = 0; j <= 1; j++)
                for (int i = 0; i <= 1; i++)
                {
                    s_verts.Add(new Vector3(x0 + i * size, y, z0 + j * size));
                    s_colors.Add(Tint(color, valid));
                }
            s_tris.AddRange(new[] { 0, 2, 1, 1, 2, 3 });
            s_mesh.Clear(false);
            s_mesh.SetVertices(s_verts);
            s_mesh.SetColors(s_colors);
            s_mesh.SetTriangles(s_tris, 0, calculateBounds: true);
        }

        // The ghost stands over `zone` in the shape of `source`'s land: the land a strike will raise there, blended
        // from the source's four corner biomes the way vanilla builds a zone heightmap.
        // The land `source` shows at `zone`, moved by `shift` (ZoneModeLift's lift, a location's slide). Sea-covered ground
        // shows water at the lifted sea, or at y30 when the land was lowered.
        private static void BuildMesh(Vector2s zone, Vector2s source, in ZoneShift shift, bool valid)
        {
            s_verts.Clear();
            s_colors.Clear();
            s_tris.Clear();
            float size = ZoneOpenSet.ZoneSize;
            float x0 = zone.x * size - size * 0.5f, z0 = zone.y * size - size * 0.5f, step = size / Cells;
            float shiftX = (source.x - zone.x) * size + shift.Dx, shiftZ = (source.y - zone.y) * size + shift.Dz;
            float lift = shift.Lift, sea = FiresWaterfalls.SeaLevel + Mathf.Max(0f, lift);
            var generator = WorldGenerator.instance;
            ZoneModeGate.EnterPlanning();
            try
            {
                float sx0 = x0 + shiftX, sz0 = z0 + shiftZ;
                var corners = generator == null ? null : new[]
                {
                    generator.GetBiome(sx0, sz0), generator.GetBiome(sx0 + size, sz0),
                    generator.GetBiome(sx0, sz0 + size), generator.GetBiome(sx0 + size, sz0 + size),
                };
                for (int j = 0; j <= Cells; j++)
                    for (int i = 0; i <= Cells; i++)
                    {
                        float x = x0 + i * step, z = z0 + j * step;
                        float ground = (corners != null ? SourceHeight(generator, corners, x + shiftX, z + shiftZ, i, j) : FiresWaterfalls.SeaLevel) + lift;
                        bool wet = ground < sea;
                        var biome = generator != null ? generator.GetBiome(x + shiftX, z + shiftZ) : Heightmap.Biome.Meadows;
                        s_verts.Add(new Vector3(x, (wet ? sea + WaterLift : ground) + Lift, z));
                        s_colors.Add(Tint(wet ? new Color32(40, 110, 170, Alpha) : BiomeColor(biome), valid));
                    }
            }
            finally { ZoneModeGate.ExitPlanning(); }
            for (int j = 0; j < Cells; j++)
                for (int i = 0; i < Cells; i++)
                {
                    int a = j * (Cells + 1) + i, b = a + 1, c = a + Cells + 1, d = c + 1;
                    s_tris.Add(a); s_tris.Add(c); s_tris.Add(b);
                    s_tris.Add(b); s_tris.Add(c); s_tris.Add(d);
                }
            s_mesh.Clear(false);
            s_mesh.SetVertices(s_verts);
            s_mesh.SetColors(s_colors);
            s_mesh.SetTriangles(s_tris, 0, calculateBounds: true);
        }

        private static float SourceHeight(WorldGenerator generator, Heightmap.Biome[] corners, float sx, float sz, int i, int j)
        {
            if (corners[1] == corners[0] && corners[2] == corners[0] && corners[3] == corners[0])
                return generator.GetBiomeHeight(corners[0], sx, sz, out _);
            float t2 = DUtils.SmoothStep(0f, 1f, (float)i / Cells), t1 = DUtils.SmoothStep(0f, 1f, (float)j / Cells);
            float h1 = generator.GetBiomeHeight(corners[0], sx, sz, out _), h2 = generator.GetBiomeHeight(corners[1], sx, sz, out _);
            float h3 = generator.GetBiomeHeight(corners[2], sx, sz, out _), h4 = generator.GetBiomeHeight(corners[3], sx, sz, out _);
            return DUtils.Lerp(DUtils.Lerp(h1, h2, t2), DUtils.Lerp(h3, h4, t2), t1);
        }

        private static Color32 Tint(Color32 color, bool valid)
            => valid ? color : Color32.Lerp(color, new Color32(220, 40, 30, Alpha), 0.65f);

        internal static Color32 BiomeColor(Heightmap.Biome biome)
        {
            switch (biome)
            {
                case Heightmap.Biome.BlackForest: return new Color32(40, 85, 45, Alpha);
                case Heightmap.Biome.Swamp: return new Color32(95, 80, 50, Alpha);
                case Heightmap.Biome.Mountain: return new Color32(225, 230, 240, Alpha);
                case Heightmap.Biome.Plains: return new Color32(200, 180, 90, Alpha);
                case Heightmap.Biome.Mistlands: return new Color32(110, 95, 125, Alpha);
                case Heightmap.Biome.AshLands: return new Color32(140, 55, 35, Alpha);
                case Heightmap.Biome.DeepNorth: return new Color32(235, 245, 255, Alpha);
                case Heightmap.Biome.Ocean: return new Color32(40, 110, 170, Alpha);
                default: return new Color32(110, 160, 65, Alpha);
            }
        }

        private static bool EnsureGhost()
        {
            if (s_ghost != null) return true;
            var shader = Shader.Find("Sprites/Default") ?? Shader.Find("UI/Default");
            if (shader == null)
            {
                FiresLogger.LogWarning("[FiresZones] no Sprites/Default or UI/Default shader: the chunk seed ghost stays hidden.");
                return false;
            }
            s_ghost = new GameObject("FiresChunkSeedGhost");
            Object.DontDestroyOnLoad(s_ghost);
            s_mesh = new Mesh { name = "FiresChunkSeedGhost" };
            s_ghost.AddComponent<MeshFilter>().sharedMesh = s_mesh;
            var renderer = s_ghost.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = new Material(shader) { name = "FiresChunkSeedGhost", renderQueue = 3100 };
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            s_ghost.SetActive(false);
            return true;
        }

        private static void Hide()
        {
            if (!s_visible) return;
            s_visible = false;
            if (s_ghost != null) s_ghost.SetActive(false);
        }

        internal static void Reset()
        {
            s_visible = false;
            s_seed = null;
            if (s_ghost != null) Object.Destroy(s_ghost);
            if (s_mesh != null) Object.Destroy(s_mesh);
            s_ghost = null;
            s_mesh = null;
        }
    }
}
