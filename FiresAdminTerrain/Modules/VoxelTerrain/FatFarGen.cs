using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Terrain.BcImport;
using VerdantsAscent.Terrain.Zones;

namespace VerdantsAscent.Terrain.Voxel
{
    // 0.2.1483 ([worldgen], Fire 11-18: "we will need to build the API and the vanilla read path ... Ebm and fat are both ours and can be built
    // to work together ... ebm would need to be able to properly read the f****** world information from FAT when both are installed").
    // World generation as FAT makes it, for EBM's far vegetation replay on its WORKER threads (FatApi FarGen*, FarDataApiVersion 3).
    // Contract agreed with [visual]. No new world logic: every answer runs FAT's own code, by the path that is safe off the main thread:
    //  - SampleZone: the REAL HeightmapBuilder.Build (vanilla runs it on its own builder thread, so every patch on it and on the WorldGenerator
    //    calls it makes already has to be thread-safe) on the caller's thread. FAT's Build patches give it the builder's corner scope
    //    (BiomePaint.Sectors.cs:239) and pinned zone view (ZoneModePatches.cs:177), so the heights, mask and corner sectors are exactly what a
    //    zone's Heightmap gets before ApplyModifiers (world generation, no player edits). FatGenTiming does not log from these threads.
    //  - SampleSector: GetBiomeSector(Vector3) as PlaceVegetation sees it on the main thread: the zone gate (ZoneModeGate.TryGateSector), vanilla's
    //    sector table (WorldGenerator.GetBiomeSector body, read directly), then FAT's own paint / provider decision (BiomePaint.Paint).
    //    Not included (main-thread or scene-only): skyland / cave layers, super flat island sectors (super flat has no vegetation), BiomeOverride.Agree.
    //  - BiomeAt: the Heightmap.GetBiome postfix (BiomePaint.Ground.cs:67): paint, then territories, unless the answer is Ocean. Cave-floor paint
    //    answers on the main thread only (VoxelLayerPaint) and environment boxes are scene objects: not included.
    //  - ForestFactor: vanilla WorldGenerator.GetForestFactor's body (installed assembly) plus BcForestFactorPatch's scale + map (BC worlds only).
    // The "not ours" rule (0.2.1484, [lead] 11-18: 1483 stood down on Fire's full stack because Expand World Data patches WorldGenerator.Initialize /
    // Pregenerate / VersionSetup, world-setup methods these paths never call). The worker paths enter vanilla ONLY through HeightmapBuilder.Build and
    // WorldGenerator.GetBiomeSector(float, float, bool) (the zone gate's swapped-zone lookup); everything else is FAT statics or direct table reads.
    // At Init the set R of WorldGenerator / HeightmapBuilder methods reachable from Build is read from the ORIGINAL IL (Harmony
    // PatchProcessor.GetOriginalInstructions, call / callvirt / newobj operands, closures included): R is what vanilla's own builder thread runs.
    // A patch on a method in R, ours or not, is already run off the main thread by vanilla, so it is allowed (Fire: our rule is about not messing
    // with things off the main thread; vanilla does it here). Patches outside R (EWD's world setup) are never reached and are ignored. Init refuses
    // only if one of our two entry points is NOT in R (a game update changed Build), so nothing on these paths runs a patch vanilla keeps on the
    // main thread. EWD's main-thread Heightmap.GetBiome nature-biome postfix (EWD 1.79 Patcher.PatchNature, with nature overrides) is not reproduced.
    internal static class FatFarGen
    {
        internal const int FlagFiresGen = 1, FlagBcWorld = 2, FlagZoneMode = 4, FlagSuperFlat = 8, FlagRing = 16, FlagVoxelOnly = 32,
                           FlagPaintOrProviders = 64, FlagOceanGapRemoved = 128;

        private static volatile WorldGenerator s_gen;
        private static volatile World s_world;
        private static volatile string s_foreign;
        private static volatile bool s_ready;
        private static Action<HeightmapBuilder, HeightmapBuilder.HMBuildData> s_build;

        /// <summary>What FAT changes in world generation on this world (bit flags), any thread.</summary>
        internal static int Flags
        {
            get
            {
                int f = 0;
                if (FiresWorldGenBase.Active) f |= FlagFiresGen;
                if (BcMigratedWorld.Active) f |= FlagBcWorld;
                if (ZoneModeGate.Active) f |= FlagZoneMode;
                if (FiresWorldProfile.CurrentIsSuperFlat) f |= FlagSuperFlat;
                if (FiresWorldProfile.CurrentIsRing) f |= FlagRing;
                if (VoxelOnlyMode.Active) f |= FlagVoxelOnly;
                if (BiomePaint.Any || BiomeOverride.ProviderCount > 0) f |= FlagPaintOrProviders;
                if (BcOceanGap.Removed) f |= FlagOceanGapRemoved;
                return f;
            }
        }

        internal static string ForeignPatches => s_foreign;

        private static bool IsOurs(string owner)
            => owner != null && (owner.StartsWith("com.Fire.", StringComparison.Ordinal) || owner == "VerdantsAscent.TileHammerCompat.Ruler" || owner == "VerdantsAscent.WecTerrainBridge");

        /// <summary>Main thread, every world start (EBM's Start). Null = ready; else why not (no world yet, Build not found, foreign patches).</summary>
        internal static string Init()
        {
            s_ready = false;
            var gen = WorldGenerator.instance;
            if (gen == null) return "no world generator yet";
            if (s_build == null)
            {
                var mi = AccessTools.Method(typeof(HeightmapBuilder), "Build", new[] { typeof(HeightmapBuilder.HMBuildData) });
                if (mi == null) return "HeightmapBuilder.Build(HMBuildData) not found";
                s_build = (Action<HeightmapBuilder, HeightmapBuilder.HMBuildData>)Delegate.CreateDelegate(typeof(Action<HeightmapBuilder, HeightmapBuilder.HMBuildData>), mi);
            }
            s_world = AccessTools.Field(typeof(WorldGenerator), "m_world")?.GetValue(gen) as World;

            // R: what vanilla's builder thread runs (Build and everything it reaches in WorldGenerator / HeightmapBuilder), from the original IL.
            var build = AccessTools.Method(typeof(HeightmapBuilder), "Build", new[] { typeof(HeightmapBuilder.HMBuildData) });
            var sector = AccessTools.Method(typeof(WorldGenerator), nameof(WorldGenerator.GetBiomeSector), new[] { typeof(float), typeof(float), typeof(bool) });
            var reach = ReachableFrom(build);
            string refuse = null;
            if (!reach.Contains(Key(build))) refuse = "HeightmapBuilder.Build could not be read";
            else if (sector == null || !reach.Contains(Key(sector))) refuse = "WorldGenerator.GetBiomeSector(float, float, bool) is no longer reached by HeightmapBuilder.Build";

            var offThread = new List<string>(); var setupOnly = new List<string>();
            foreach (MethodBase m in Harmony.GetAllPatchedMethods())
            {
                if (m == null || (m.DeclaringType != typeof(WorldGenerator) && m.DeclaringType != typeof(HeightmapBuilder))) continue;
                var info = Harmony.GetPatchInfo(m);
                if (info == null) continue;
                foreach (var owner in info.Owners)
                    if (!IsOurs(owner)) (reach.Contains(Key(m)) ? offThread : setupOnly).Add($"{m.DeclaringType.Name}.{m.Name} by {owner}");
            }
            s_foreign = refuse;
            s_gen = gen;
            s_ready = true;
            string msg = refuse != null
                ? $"[FarGen] stands down: {refuse}; the far-gen calls answer false."
                : $"[FarGen] ready for EBM's far vegetation (FatApi FarDataApiVersion {FatApi.FarDataApiVersion}): world flags {Flags}, zone ground by the real " +
                  $"HeightmapBuilder.Build ({reach.Count} vanilla builder-thread methods read from IL). Not-ours patches on that path (vanilla runs them off the " +
                  $"main thread too, so they are included): {(offThread.Count > 0 ? string.Join(", ", offThread) : "none")}. Not-ours patches never reached " +
                  $"(world setup / main thread): {(setupOnly.Count > 0 ? string.Join(", ", setupOnly) : "none")}.";
            // 0.2.1484 ([lead]: 1483 logged the stand-down on every call): one Info per distinct answer, not per call.
            if (msg != s_lastInitLog) { s_lastInitLog = msg; FiresLogger.LogInfo(msg); }
            s_offThreadForeign = offThread.Count > 0 ? string.Join(", ", offThread) : null;
            return refuse;
        }

        private static string s_lastInitLog;
        private static volatile string s_offThreadForeign;
        /// <summary>Not-ours patches on the builder-thread path the far-gen calls run (informational; they are included, as vanilla's builder runs them).</summary>
        internal static string OffThreadForeignPatches => s_offThreadForeign;

        // Breadth-first over the original IL: every WorldGenerator / HeightmapBuilder method (and their compiler-generated closures) a call, callvirt
        // or newobj reaches from `root`. Main thread, at Init.
        internal static string Key(MethodBase m) => m == null ? "" : m.Module.ModuleVersionId + ":" + m.MetadataToken;   // stable identity (ReflectedType may differ)

        private static HashSet<string> ReachableFrom(MethodBase root)
        {
            var seen = new HashSet<string>();
            if (root == null) return seen;
            var queue = new Queue<MethodBase>();
            seen.Add(Key(root)); queue.Enqueue(root);
            while (queue.Count > 0)
            {
                var m = queue.Dequeue();
                List<CodeInstruction> il;
                try { il = PatchProcessor.GetOriginalInstructions(m); }
                catch (Exception) { continue; }
                if (il == null) continue;
                foreach (var ci in il)
                {
                    if (!(ci.operand is MethodBase callee)) continue;
                    if (ci.opcode != System.Reflection.Emit.OpCodes.Call && ci.opcode != System.Reflection.Emit.OpCodes.Callvirt && ci.opcode != System.Reflection.Emit.OpCodes.Newobj
                        && ci.opcode != System.Reflection.Emit.OpCodes.Ldftn) continue;
                    var t = callee.DeclaringType;
                    while (t != null && t.DeclaringType != null && t.Name.StartsWith("<", StringComparison.Ordinal)) t = t.DeclaringType;   // closures -> their owner
                    if (t != typeof(WorldGenerator) && t != typeof(HeightmapBuilder)) continue;
                    if (seen.Add(Key(callee))) queue.Enqueue(callee);
                }
            }
            return seen;
        }

        private static bool Usable => s_ready && s_foreign == null && s_gen != null;

        /// <summary>
        /// Any thread. The zone's ground as its Heightmap gets it from HeightmapBuilder.Build (before ApplyModifiers): (width+1)^2 heights and mask
        /// alphas, row-major by z (index z * (width+1) + x), vertex (0,0) at (zx*64 - width*scale/2, zz*64 - width*scale/2); the 4 corner sectors in
        /// the builder's order (-,-), (+,-), (-,+), (+,+); edge = the corner biomes differ (Heightmap.IsBiomeEdge); painted = biomes a paint circle
        /// adds over the zone square (Heightmap.HaveBiome postfix). Writes only into the caller's arrays (vanilla's HMBuildData allocates inside Build).
        /// </summary>
        internal static bool SampleZone(int zx, int zz, int width, float scale, float[] heights, float[] maskAlpha, BiomeSector[] corners4, out bool edge, out Heightmap.Biome painted)
        {
            edge = false; painted = Heightmap.Biome.None;
            var gen = s_gen; var hb = HeightmapBuilder.instance; var build = s_build;
            int n1 = width + 1, n = n1 * n1;
            if (!Usable || hb == null || build == null || width <= 0 || heights == null || heights.Length < n || maskAlpha == null || maskAlpha.Length < n || corners4 == null || corners4.Length < 4) return false;
            var centre = new Vector3(zx * 64f, 0f, zz * 64f);
            var data = new HeightmapBuilder.HMBuildData(centre, width, scale, false, gen);
            FatGenTiming.t_farApi = true;
            try { build(hb, data); }
            finally { FatGenTiming.t_farApi = false; }
            var bh = data.m_baseHeights; var bm = data.m_baseMask; var cb = data.m_cornerBiomes;
            if (bh == null || bh.Count < n || bm == null || bm.Length < n || cb == null || cb.Length < 4) return false;
            for (int i = 0; i < n; i++) { heights[i] = bh[i]; maskAlpha[i] = bm[i].a; }
            for (int k = 0; k < 4; k++) corners4[k] = cb[k];
            Heightmap.Biome b0 = cb[0]?.Biome ?? Heightmap.Biome.None;
            edge = (cb[1]?.Biome ?? Heightmap.Biome.None) != b0 || (cb[2]?.Biome ?? Heightmap.Biome.None) != b0 || (cb[3]?.Biome ?? Heightmap.Biome.None) != b0;
            painted = BiomePaint.FarZoneGrounds(centre, width * scale);
            return true;
        }

        /// <summary>Any thread. The biome sector at (x, z) as PlaceVegetation's GetBiomeSector(Vector3) gives it (see the class note). Null = not usable.</summary>
        internal static BiomeSector SampleSector(float x, float z)
        {
            var gen = s_gen;
            if (!Usable) return null;
            BiomeSector r = null;
            if (!ZoneModeGate.TryGateSector(gen, x, z, false, ref r)) r = VanillaSector(s_world, x, z);
            BiomePaint.FarPaintSector(x, z, ref r);
            return r;
        }

        // Vanilla WorldGenerator.GetBiomeSector(float, float, bool clamp = false) -> (int, int) body, read directly (WorldGenerator.cs:689-714).
        private static BiomeSector VanillaSector(World world, float x, float z)
        {
            var data = world?.m_biomeData;
            if (data == null) return BiomeSector.EmptyBlackForest;
            if (!data.IsReady) return BiomeSector.EmptyMeadows;
            int gx = AltBiomeWorldData.WorldSpaceToMapSpace(x), gy = AltBiomeWorldData.WorldSpaceToMapSpace(z);
            if (gx < 0) gx = 0; if (gy < 0) gy = 0; if (gx >= 2048) gx = 2047; if (gy >= 2048) gy = 2047;
            return data.PointSectors[gx, gy] ?? BiomeSector.EmptyMeadows;
        }

        /// <summary>Any thread. The point biome a zone's Heightmap.GetBiome returns, given its corner-weighted answer (see the class note).</summary>
        internal static Heightmap.Biome BiomeAt(float x, float z, Heightmap.Biome cornerWeighted)
        {
            if (cornerWeighted != Heightmap.Biome.Ocean && BiomeOverride.TryAt(x, z, out var ground)) return ground;
            return cornerWeighted;
        }

        /// <summary>Any thread. WorldGenerator.GetForestFactor at (x, z) as FAT makes it.</summary>
        internal static float ForestFactor(float x, float z)
        {
            var pos = new Vector3(x, 0f, z);   // vanilla's Fbm(Vector3) reads x and z only (DUtils.Fbm(Vector3) -> Fbm(new Vector2(p.x, p.z)))
            var q = pos;
            var bc = BcMigratedWorld.Generator;
            if (bc != null) q *= bc.Settings.ForestScale;                     // BcForestFactorPatch prefix
            float num = 0.4f;
            float f = DUtils.Fbm(q * 0.01f * num, 3, 1.6f, 0.7f);            // vanilla body (installed WorldGenerator.GetForestFactor)
            if (bc != null) BcMigratedWorld.TryApplyForest(pos, ref f);       // BcForestFactorPatch postfix (unscaled position)
            return f;
        }
    }
}
