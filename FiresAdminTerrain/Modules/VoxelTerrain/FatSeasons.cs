using System.Collections.Generic;
using FiresCore.Bridge;
using UnityEngine;
using VerdantsAscent.Terrain.Biome;
using VerdantsAscent.Terrain.Zones;

namespace VerdantsAscent.Terrain.Voxel
{
    /// <summary>
    /// FAT's side of Vedr's seasons, read through Core's SeasonBridge (no Vedr reference; with Vedr absent nothing here
    /// runs). Voxel ground and the far quilt store and cache the TRUE biome colour; winter is added only when colours are
    /// handed to the renderer, and live meshes are recoloured nearest-first when Vedr's snow biomes change, so no season
    /// is ever written into chunk data or the chunk cache. Still water in Vedr's winter biomes freezes from the shallow
    /// edges inward as winter sets in and thaws from the deep centre outward in spring (FiresWorldGenBase.SeasonalFrozenAt),
    /// in a few steps, each remeshing only the chunks that hold water, and only while Vedr's "Freeze lakes and rivers" is on.
    /// FAT also tells Vedr which ground and water are its own, so Vedr's lake ice stays off voxel ground and FAT water.
    /// Earthquake scars and meteor craters do land on voxel ground: Vedr adds them to the vanilla heightmap, whose
    /// regeneration rewraps the zone, and the voxel columns are built from those heights, so the scar is cut into the
    /// chunks with real collision and the ground regenerates as it was when the scar heals. Fissures are the exception:
    /// Vedr leaves them out of the heightmap here and FatCracks cuts them in 3D. A scarred chunk never reads or writes
    /// the chunk cache (VoxelChunk.PrepareGenerate). Skylands, legacy tiles, environment boxes and legacy
    /// biome paint snow by their own biome, and the zone-mode border walls and far tops wear the ground's winter like
    /// the land beside them.
    /// </summary>
    internal static class FatSeasons
    {
        private const float ZoneSize = 64f;
        private const float ZoneHalf = 32f;
        private const int RecoloursPerFrame = 8;
        private const int IceSteps = 4;
        private const float IcePollSeconds = 2f;
        private const float PaintRimBlendM = 4f;
        private const float PaintRimBlendMaxShareOfRadius = 0.5f;
        private const float ChunkSampleRingM = 1f;

        private static readonly List<VoxelChunkView> s_views = new List<VoxelChunkView>();
        private static readonly List<Recolour> s_jobs = new List<Recolour>();
        private static readonly List<VoxelChunk> s_waterChunks = new List<VoxelChunk>();
        private static readonly List<Vector3> s_vertexScratch = new List<Vector3>(4096);
        private static readonly List<Color32> s_colorScratch = new List<Color32>(4096);

        private static int s_mask;
        private static int s_iceStep;
        private static int s_nextJob;
        private static RecolourDriver s_recolour;

        private static int s_memoZx = int.MinValue, s_memoZz = int.MinValue;
        private static Vector4 s_d0, s_d1, s_d2, s_d3;
        private static float s_s0, s_s1, s_s2, s_s3;
        private static bool s_memoFlat;

        /// <summary>Season-free colours are kept per chunk view only when Vedr is installed, so nothing is spent without it.</summary>
        internal static bool Tracking => SeasonBridge.IsAvailable;

        internal static void Register()
        {
            SeasonBridge.RegisterGroundOwner(VoxelZoneStreamer.OwnsGround);
            SeasonBridge.RegisterGroundScarDrawer(VoxelZoneStreamer.OwnsGround, VoxelZoneStreamer.IsGroundSettled);
            SeasonBridge.RegisterGroundCrackDrawer(VoxelZoneStreamer.OwnsGround);
            SeasonBridge.GroundScarsChanged += FatCracks.OnGroundScarsChanged;
            SeasonBridge.RegisterWaterOwner(OwnsWater);
            SeasonBridge.VisualsChanged += OnVisualsChanged;
            s_mask = SeasonBridge.SnowTerrainBiomeMask;
            if (!SeasonBridge.IsAvailable)
            {
                FiresLogger.LogInfo("[FiresSeasons] Vedr is not installed: FAT ground and water keep their summer look.");
                return;
            }
            Object.DontDestroyOnLoad(new GameObject("FiresSeasonIce").AddComponent<IceDriver>().gameObject);
            FiresLogger.LogInfo("[FiresSeasons] Vedr seasons found: voxel ground snows at render time, still water freezes in winter; " +
                                "quake scars, 3D cracks and meteor craters are cut into voxel ground, Vedr's lake ice stays off it and FAT water.");
        }

        /// <summary>Ground damage from Vedr reaches into this chunk's samples, which run from one metre before its origin to CELLS past it (main thread): its ground is temporary.</summary>
        internal static bool IsScarred(Vector3 chunkOrigin) =>
            SeasonBridge.IsGroundScarredIn(chunkOrigin.x - ChunkSampleRingM, chunkOrigin.z - ChunkSampleRingM,
                chunkOrigin.x + VoxelChunk.CELLS, chunkOrigin.z + VoxelChunk.CELLS);

        private static bool OwnsWater(Vector3 point) =>
            VoxelZoneStreamer.OwnsGround(point) || FiresWorldGenBase.IsWetRawAt(point.x, point.z);

        /// <summary>Metres of water depth the ice covers at a point right now: 0 outside Vedr's winter biomes or out of season.</summary>
        internal static float IceReachAt(float wx, float wz)
        {
            if (s_iceStep == 0 || (SeasonBridge.WinterBiomeMask & (int)BiomeManager.GetBiomeAt(wx, wz)) == 0) return 0f;
            return FiresWorldGenBase.MaxSeasonalIceDepthM * s_iceStep / IceSteps;
        }

        /// <summary>The snow weight rounded to a few ice steps (none while Vedr's lake freezing is off), so meshes and the swim test always agree on the same front.</summary>
        private sealed class IceDriver : MonoBehaviour
        {
            private float _nextPoll;

            private void Update()
            {
                if (Time.unscaledTime < _nextPoll) return;
                _nextPoll = Time.unscaledTime + IcePollSeconds;
                bool freezing = SeasonBridge.FreezesLakes;
                int step = freezing ? Mathf.Clamp(Mathf.FloorToInt(SeasonBridge.SnowAmount01 * IceSteps), 0, IceSteps) : 0;
                if (step == s_iceStep) return;
                s_iceStep = step;
                int remeshed = RemeshWaterChunks();
                string why = freezing ? "" : " Vedr's lake freezing is off;";
                FiresLogger.LogInfo($"[FiresSeasons] ice step {step}/{IceSteps} (reaches {FiresWorldGenBase.MaxSeasonalIceDepthM * step / IceSteps:0.#} m deep):{why} remeshing {remeshed} water chunk(s).");
            }
        }

        private static int RemeshWaterChunks()
        {
            s_waterChunks.Clear();
            foreach (VoxelChunk chunk in VoxelWorld.AllChunks)
                if (chunk != null && chunk.HasGenWater) s_waterChunks.Add(chunk);
            foreach (VoxelChunk chunk in s_waterChunks) VoxelMeshPipeline.EnqueueRemesh(chunk);
            if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null) FiresFarWater.RebuildForSeason();
            int count = s_waterChunks.Count;
            s_waterChunks.Clear();
            return count;
        }

        private static void OnVisualsChanged(int season, int stage)
        {
            int mask = SeasonBridge.SnowTerrainBiomeMask;
            if (mask == s_mask) return;
            s_mask = mask;
            s_memoZx = int.MinValue;
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return;
            FarQuilt.RecolourForSeason();
            s_jobs.Clear();
            VoxelWorld.CollectViews(s_views);
            foreach (VoxelChunkView view in s_views)
                if (view != null) s_jobs.Add(new Recolour(view.WorldCentre, () => { if (view != null) view.RecolourForSeason(); }));
            s_views.Clear();
            int views = s_jobs.Count;
            ZoneModeBorderWalls.CollectSeasonRecolours(s_jobs);
            ZoneModeSkylands.CollectSeasonRecolours(s_jobs);
            Persistence.TileMeshRenderer.CollectSeasonRecolours(s_jobs);
            Vector3 eye = Utils.GetMainCamera() != null ? Utils.GetMainCamera().transform.position : Vector3.zero;
            s_jobs.Sort((a, b) => (a.Centre - eye).sqrMagnitude.CompareTo((b.Centre - eye).sqrMagnitude));
            s_nextJob = 0;
            if (s_recolour == null) s_recolour = new GameObject("FiresSeasons").AddComponent<RecolourDriver>();
            s_recolour.enabled = true;
            FiresLogger.LogInfo($"[FiresSeasons] snow biomes now {(Heightmap.Biome)mask}: recolouring {views} voxel chunk view(s) and " +
                                $"{s_jobs.Count - views} border wall / far top / skyland / legacy tile piece(s) nearest first.");
        }

        /// <summary>
        /// One live mesh to repaint for a new season, and where it stands (the nearest go first). Jobs run a few per frame,
        /// so each one finds its mesh again when it runs and skips it if it has gone meanwhile.
        /// </summary>
        internal readonly struct Recolour
        {
            internal readonly Vector3 Centre;
            internal readonly System.Action Apply;

            internal Recolour(Vector3 centre, System.Action apply)
            {
                Centre = centre;
                Apply = apply;
            }
        }

        private sealed class RecolourDriver : MonoBehaviour
        {
            private void Update()
            {
                int end = Mathf.Min(s_nextJob + RecoloursPerFrame, s_jobs.Count);
                for (; s_nextJob < end; s_nextJob++) s_jobs[s_nextJob].Apply();
                if (s_nextJob < s_jobs.Count) return;
                s_jobs.Clear();
                enabled = false;
            }
        }

        /// <summary>The colours a voxel mesh is drawn with: the season-free ones, plus winter where it is winter.</summary>
        internal static List<Color32> RenderColors(List<Color32> seasonFree, List<Vector3> worldVerts)
        {
            if (s_mask == 0) return seasonFree;
            s_colorScratch.Clear();
            for (int i = 0; i < seasonFree.Count; i++)
                s_colorScratch.Add(WinterizeVoxel(seasonFree[i], worldVerts[i]));
            return s_colorScratch;
        }

        internal static List<Color32> RenderColors(Color32[] seasonFree, int count, Mesh mesh)
        {
            mesh.GetVertices(s_vertexScratch);
            s_colorScratch.Clear();
            for (int i = 0; i < count; i++)
                s_colorScratch.Add(WinterizeVoxel(seasonFree[i], s_vertexScratch[i]));
            return s_colorScratch;
        }

        /// <summary>
        /// Winter on the world's ground as drawn (after any biome paint tint): ground a biome paint circle has turned goes
        /// through <see cref="WinterizePainted"/>, the rest through <see cref="WorldWinter"/>.
        /// </summary>
        internal static Color32 Winterize(Color32 c, float wx, float wz)
        {
            if (s_mask == 0) return c;
            if (BiomePaint.TryGround(wx, wz, out var ground, out var circle)) return WinterizePainted(c, ground, circle, wx, wz);
            return WorldWinter(c, wx, wz);
        }

        /// <summary>The unpainted ground's colour at a point as drawn now (border walls and far tops, which take no paint).</summary>
        internal static Color32 UnpaintedGroundColour(float wx, float wz) => WorldWinter(BiomeCornerBlend.Sample(wx, wz), wx, wz);

        /// <summary>
        /// Ground that wears one biome outright (a skyland, a legacy tile, an environment box, legacy biome paint): its
        /// biome's colour, or the snow splat while Vedr snows over that biome. Reads Vedr's live mask, because Vedr starts
        /// repainting vanilla heightmaps before it raises the season event.
        /// </summary>
        internal static Color32 BiomeColour(Heightmap.Biome biome) =>
            (SeasonBridge.SnowTerrainBiomeMask & (int)biome) != 0 ? SnowColour : Heightmap.GetBiomeColor(biome);

        private static Color32 WinterizeVoxel(Color32 c, Vector3 v)
        {
            if (BiomePaint.TryGround(v.x, v.z, out var ground, out var circle)) return WinterizePainted(c, ground, circle, v.x, v.z);
            if (OnSkyland(c, v, out var skyland)) return BiomeColour(skyland);
            return WorldWinter(c, v.x, v.z);
        }

        /// <summary>
        /// Skyland ground wears its biome's pure colour (VoxelZoneStreamer.SkylandBiome), so it snows by that biome, not by
        /// the world's ground far below. Only vertices still wearing that pure colour count, so ground the skyland never
        /// tinted is left to the world's snow.
        /// </summary>
        private static bool OnSkyland(Color32 c, Vector3 v, out Heightmap.Biome skyland)
        {
            skyland = Heightmap.Biome.None;
            return ZoneModeRegistry.ModeActive && FiresSkyIslands.TrySkylandBiomeNear(v.x, v.z, out skyland)
                && SameColour(c, Heightmap.GetBiomeColor(skyland));
        }

        /// <summary>
        /// Vanilla winter parity: each zone corner whose biome Vedr snows over draws the Mountain snow splat instead of
        /// its own colour, blended across the zone like <see cref="BiomeCornerBlend"/>. Applied as a per-corner
        /// difference, so colours painted into the ground data keep their paint and only the snow is added.
        /// </summary>
        private static Color32 WorldWinter(Color32 c, float wx, float wz)
        {
            if (s_mask == 0) return c;
            CornerWeights(wx, wz, out float ix, out float iz);
            if (s_memoFlat) return c;
            Vector4 d = Vector4.Lerp(Vector4.Lerp(s_d0, s_d1, ix), Vector4.Lerp(s_d2, s_d3, ix), iz);
            return new Color32(Channel(c.r, d.x), Channel(c.g, d.y), Channel(c.b, d.z), Channel(c.a, d.w));
        }

        /// <summary>
        /// Painted ground in winter (Fire, 2026-09-27: snow follows the painted biome, blended). Deep in a circle the painted
        /// biome decides; across a band inside the rim the snow fades to what the world's own ground has there, so the rim
        /// meets the unpainted winter ground without a seam. Heightmaps (BiomePaint.TintHeightmap), voxel ground and the far
        /// tiles all draw painted ground through here.
        /// </summary>
        internal static Color32 WinterizePainted(Color32 painted, Heightmap.Biome ground, BiomePaint.Circle circle, float wx, float wz)
        {
            if (s_mask == 0) return painted;
            CornerWeights(wx, wz, out float ix, out float iz);
            float worldSnow = Mathf.Lerp(Mathf.Lerp(s_s0, s_s1, ix), Mathf.Lerp(s_s2, s_s3, ix), iz);
            float band = Mathf.Min(PaintRimBlendM, circle.R * PaintRimBlendMaxShareOfRadius);
            float dx = wx - circle.X, dz = wz - circle.Z;
            float inward = Mathf.Clamp01((circle.R - Mathf.Sqrt(dx * dx + dz * dz)) / band);
            float snow = Mathf.Lerp(worldSnow, SnowShare(ground), inward);
            return Color32.Lerp(painted, SnowColour, snow);
        }

        /// <summary>Where a point sits in its zone, for blending the zone's four corners (memoised per zone).</summary>
        private static void CornerWeights(float wx, float wz, out float ix, out float iz)
        {
            int zx = Mathf.FloorToInt((wx + ZoneHalf) / ZoneSize);
            int zz = Mathf.FloorToInt((wz + ZoneHalf) / ZoneSize);
            if (zx != s_memoZx || zz != s_memoZz) MemoCorners(zx, zz);
            ix = (wx - (zx * ZoneSize - ZoneHalf)) / ZoneSize;
            iz = (wz - (zz * ZoneSize - ZoneHalf)) / ZoneSize;
        }

        private static void MemoCorners(int zx, int zz)
        {
            s_memoZx = zx;
            s_memoZz = zz;
            float x0 = zx * ZoneSize - ZoneHalf, z0 = zz * ZoneSize - ZoneHalf;
            Heightmap.Biome b0 = BiomeManager.GetBiomeAt(x0, z0);
            Heightmap.Biome b1 = BiomeManager.GetBiomeAt(x0 + ZoneSize, z0);
            Heightmap.Biome b2 = BiomeManager.GetBiomeAt(x0, z0 + ZoneSize);
            Heightmap.Biome b3 = BiomeManager.GetBiomeAt(x0 + ZoneSize, z0 + ZoneSize);
            s_d0 = SnowDelta(b0);
            s_d1 = SnowDelta(b1);
            s_d2 = SnowDelta(b2);
            s_d3 = SnowDelta(b3);
            s_s0 = SnowShare(b0);
            s_s1 = SnowShare(b1);
            s_s2 = SnowShare(b2);
            s_s3 = SnowShare(b3);
            s_memoFlat = s_d0 == Vector4.zero && s_d1 == Vector4.zero && s_d2 == Vector4.zero && s_d3 == Vector4.zero;
        }

        private static Color32 SnowColour => Heightmap.GetBiomeColor(Heightmap.Biome.Mountain);

        /// <summary>1 where the ground is white now: a biome Vedr snows over, or one whose own colour is the snow splat.</summary>
        private static float SnowShare(Heightmap.Biome biome) =>
            (s_mask & (int)biome) != 0 || SameColour(Heightmap.GetBiomeColor(biome), SnowColour) ? 1f : 0f;

        private static bool SameColour(Color32 a, Color32 b) => a.r == b.r && a.g == b.g && a.b == b.b && a.a == b.a;

        private static Vector4 SnowDelta(Heightmap.Biome biome) =>
            (s_mask & (int)biome) == 0 ? Vector4.zero : AsVector(SnowColour) - AsVector(Heightmap.GetBiomeColor(biome));

        private static Vector4 AsVector(Color32 c) => new Vector4(c.r, c.g, c.b, c.a);

        private static byte Channel(byte value, float delta) => (byte)Mathf.Clamp(Mathf.RoundToInt(value + delta), byte.MinValue, byte.MaxValue);
    }
}
