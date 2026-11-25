using System.Collections.Generic;
using HarmonyLib;
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
        internal const float AlwaysFrozenReachM = 1000f;   // 0.2.1095: Deep North ice reaches every depth
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
            // 0.2.1097: the per-column state (WaterStateField, the one rule for placed and generated water: Deep North always - the
            // painted biome, the island's at height - other biomes in Vedr's winter, soft at biome edges). Solid ice freezes the column
            // to any depth; slush, crust or lava never freezes it here (slush stays swimmable; crust and lava have no liquid level).
            if (GenWaterField.Ready && GenWaterField.TryWaterSurfaceSmooth(wx, wz, out float level, out float depth) && depth > 0.05f)
            {
                var st = VerdantsAscent.Water.Core.WaterStateField.Sample(wx, wz, level);
                if (st.Ice >= VerdantsAscent.Water.Core.WaterStateField.IceSolid) return AlwaysFrozenReachM;
                if (st.Lava >= 0.05f || st.Crust >= 0.05f || st.Ice >= 0.2f) return 0f;
            }
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

        // 0.2.1095: a biome repaint can make generated water Deep North (ice) or Ashlands (lava): remesh the water chunks once per
        // paint change (SeasonalWaterFreeze.Tick sees the paint version), with or without Vedr.
        internal static int RemeshWaterForBiomeChange() => RemeshWaterChunks();

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
            System.Threading.Interlocked.Increment(ref s_winterRevision);   // 0.2.1444: FatApi.FarWinterRevision
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return;
            FarQuilt.RecolourForSeason();
            if (DistantSnow != null && DistantSnow.Value) RebuildDistantLod("Vedr's snow biomes changed");   // 0.2.1441
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
        /// Painted ground in winter (snow follows the painted biome, blended). Deep in a circle the painted
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
            float inward = Mathf.Clamp01(circle.Rim(wx, wz, band) / band);
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

        // ── 0.2.1441 (Fire 11-16, frozen lake x -1338 z 726: "run out of distance the fucking color fades and it just looks black") ──
        // Vanilla's distant-LOD tiles (TerrainLod, and FAT's FarTerrainVanilla ring) take their vertex colours from the worldgen biome
        // only (Heightmap.RebuildRenderMesh: m_isDistantLod -> GetBiomeColor(GetBiomeSector(wx, wz))), and Vedr's ground snow skips
        // every distant-LOD heightmap (Vedr SnowBaseMask / SnowHeightmapInject return on IsDistantLod). So in Vedr winter the near
        // ground is white and the far terrain keeps its summer biome colour, which reads dark under winter light. FarQuilt tiles and
        // voxel ground already go through Winterize; this gives the vanilla distant tiles the same winter, after vanilla (and FAT's
        // env-box / biome paint colours) wrote them. Season changes rebuild the distant tiles once (OnVisualsChanged).
        internal static BepInEx.Configuration.ConfigEntry<bool> DistantSnow;
        private static readonly AccessTools.FieldRef<Heightmap, Mesh> s_lodMesh = AccessTools.FieldRefAccess<Heightmap, Mesh>("m_renderMesh");
        private static readonly AccessTools.FieldRef<Heightmap, bool> s_lodIsDistant = AccessTools.FieldRefAccess<Heightmap, bool>("m_isDistantLod");
        private static readonly List<Color32> s_lodCols = new List<Color32>(8192);
        private static int s_lodTiles, s_lodChanged; private static double s_lodMs; private static float s_lodLogAt; private static bool s_lodSaid;

        internal static void BindDistantSnow(BepInEx.Configuration.ConfigFile cfg)
        {
            DistantSnow = cfg.Bind("Far Terrain", "WinterSnow", true,
                "In Vedr's winter, the distant terrain (past the loaded ground) is drawn with the same snow as the near ground, instead of its summer colour. "
                + "Off = vanilla: the far terrain keeps its summer biome colour. Applies live (the far terrain rebuilds once). Needs Vedr; does nothing without it.");
            DistantSnow.SettingChanged += (_, __) => RebuildDistantLod("[Far Terrain] WinterSnow changed");
        }

        /// <summary>FAT's one Heightmap.RebuildRenderMesh postfix (after the env-box and biome-paint colours): winter on a distant-LOD tile.</summary>
        internal static void WinterizeDistantLod(Heightmap hm)
        {
            if (s_mask == 0 || DistantSnow == null || !DistantSnow.Value || hm == null || !s_lodIsDistant(hm)) return;
            if (!VoxelOnlyRenderMesh.Allow(hm)) return;   // FAT's prefix skipped vanilla's rebuild (voxel-only world with FarQuilt): colours are old, never winterize twice
            var mesh = s_lodMesh(hm);
            if (mesh == null) return;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            mesh.GetColors(s_lodCols);
            int n = hm.m_width + 1;
            if (s_lodCols.Count != n * n) return;
            float scale = hm.m_scale;
            Vector3 p = hm.transform.position;
            float x0 = p.x - hm.m_width * scale * 0.5f, z0 = p.z - hm.m_width * scale * 0.5f;   // vanilla's own vertex origin (RebuildRenderMesh)
            int changed = 0;
            for (int i = 0; i < n; i++)
            {
                float wz = z0 + i * scale;
                int row = i * n;
                for (int j = 0; j < n; j++)
                {
                    Color32 c = s_lodCols[row + j];
                    Color32 w = Winterize(c, x0 + j * scale, wz);
                    if (w.r == c.r && w.g == c.g && w.b == c.b && w.a == c.a) continue;
                    s_lodCols[row + j] = w;
                    changed++;
                }
            }
            if (changed > 0) mesh.SetColors(s_lodCols);
            s_lodTiles++; s_lodChanged += changed;
            s_lodMs += (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (!s_lodSaid && changed > 0) { s_lodSaid = true; FiresLogger.LogInfo($"[FiresSeasons] winter snow drawn on the distant terrain too (first tile at {p.x:0},{p.z:0}: {changed} of {n * n} vertices whitened; 0.2.1441)"); }
            if (FiresLogger.DebugEnabled && Time.unscaledTime >= s_lodLogAt)
            {
                s_lodLogAt = Time.unscaledTime + 60f;
                FiresLogger.LogDebug($"[FiresSeasons] distant winter, last 60 s: {s_lodTiles} far tile(s), {s_lodChanged} vertices whitened, {s_lodMs:0.0} ms");
                s_lodTiles = s_lodChanged = 0; s_lodMs = 0;
            }
        }

        // Season change or key flip: vanilla rebuilds each distant tile from its biome colours, then the postfix above applies (or not).
        private static void RebuildDistantLod(string why)
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return;
            int n = 0;
            var all = VerdantsAscent.Terrain.Paint.DistantHeightCache.DistantTiles();   // 0.2.1442: GetAllHeightmaps() holds NEAR tiles only
            for (int i = 0; i < all.Count; i++)
            {
                var hm = all[i];
                if (hm == null || !hm.isActiveAndEnabled) continue;
                try { hm.Poke(); n++; } catch { }
            }
            if (n > 0) FiresLogger.LogInfo($"[FiresSeasons] {n} distant terrain tile(s) rebuilt ({why})");
        }

        /// <summary>1 where the ground is white now: a biome Vedr snows over, or one whose own colour is the snow splat.</summary>
        private static float SnowShare(Heightmap.Biome biome) =>
            (s_mask & (int)biome) != 0 || SameColour(Heightmap.GetBiomeColor(biome), SnowColour) ? 1f : 0f;

        private static bool SameColour(Color32 a, Color32 b) => a.r == b.r && a.g == b.g && a.b == b.b && a.a == b.a;

        private static Vector4 SnowDelta(Heightmap.Biome biome) =>
            (s_mask & (int)biome) == 0 ? Vector4.zero : AsVector(SnowColour) - AsVector(Heightmap.GetBiomeColor(biome));

        private static Vector4 AsVector(Color32 c) => new Vector4(c.r, c.g, c.b, c.a);

        private static byte Channel(byte value, float delta) => (byte)Mathf.Clamp(Mathf.RoundToInt(value + delta), byte.MinValue, byte.MaxValue);

        // ── 0.2.1444 (lead 11-16: far drawing lives in EBM, FAT supplies data; FatApi.TryFarColor from EBM's builder thread) ──
        // The same winter as Winterize, with no shared memo, so any thread may call it. Corner biomes come from the patched
        // WorldGenerator.GetBiome (BiomeManager.GetBiomeAt also reads environment boxes, which are scene objects: main thread only),
        // so ground an environment box forces keeps its world biome's winter here. Painted ground goes through the same rim blend.
        private static int s_winterRevision;
        internal static int WinterRevision => System.Threading.Volatile.Read(ref s_winterRevision);

        internal static Color32 WinterizeAnyThread(Color32 c, float wx, float wz, WorldGenerator wg)
        {
            int mask = s_mask;
            if (mask == 0 || wg == null) return c;
            int zx = Mathf.FloorToInt((wx + ZoneHalf) / ZoneSize), zz = Mathf.FloorToInt((wz + ZoneHalf) / ZoneSize);
            float x0 = zx * ZoneSize - ZoneHalf, z0 = zz * ZoneSize - ZoneHalf;
            float ix = (wx - x0) / ZoneSize, iz = (wz - z0) / ZoneSize;
            Heightmap.Biome b0 = wg.GetBiome(x0, z0), b1 = wg.GetBiome(x0 + ZoneSize, z0);
            Heightmap.Biome b2 = wg.GetBiome(x0, z0 + ZoneSize), b3 = wg.GetBiome(x0 + ZoneSize, z0 + ZoneSize);
            if (BiomePaint.TryGround(wx, wz, out var ground, out var circle))
            {
                float worldSnow = Mathf.Lerp(Mathf.Lerp(SnowShare(mask, b0), SnowShare(mask, b1), ix), Mathf.Lerp(SnowShare(mask, b2), SnowShare(mask, b3), ix), iz);
                float band = Mathf.Min(PaintRimBlendM, circle.R * PaintRimBlendMaxShareOfRadius);
                float inward = Mathf.Clamp01(circle.Rim(wx, wz, band) / band);
                return Color32.Lerp(c, FatFarData.SplatOf(Heightmap.Biome.Mountain), Mathf.Lerp(worldSnow, SnowShare(mask, ground), inward));   // 0.2.1446: pure table, never the patched GetBiomeColor off the main thread
            }
            Vector4 d0 = SnowDelta(mask, b0), d1 = SnowDelta(mask, b1), d2 = SnowDelta(mask, b2), d3 = SnowDelta(mask, b3);
            if (d0 == Vector4.zero && d1 == Vector4.zero && d2 == Vector4.zero && d3 == Vector4.zero) return c;
            Vector4 d = Vector4.Lerp(Vector4.Lerp(d0, d1, ix), Vector4.Lerp(d2, d3, ix), iz);
            return new Color32(Channel(c.r, d.x), Channel(c.g, d.y), Channel(c.b, d.z), Channel(c.a, d.w));
        }

        private static float SnowShare(int mask, Heightmap.Biome biome) =>
            (mask & (int)biome) != 0 || SameColour(FatFarData.SplatOf(biome), FatFarData.SplatOf(Heightmap.Biome.Mountain)) ? 1f : 0f;

        private static Vector4 SnowDelta(int mask, Heightmap.Biome biome) =>
            (mask & (int)biome) == 0 ? Vector4.zero : AsVector(FatFarData.SplatOf(Heightmap.Biome.Mountain)) - AsVector(FatFarData.SplatOf(biome));   // 0.2.1446: pure table (thread-safe)
    }
}
