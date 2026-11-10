using BepInEx.Configuration;

namespace VerdantsAscent.Terrain.Voxel
{
    // The whole-world terrain mode (Stage W in the plan). Vanilla = untouched
    // vanilla heightmap zones (safe default for release). VoxelWrap = the W1+
    // streamer wraps each zone's live vanilla heightfield as voxel density and
    // carves procedural features FROM it (caves/grottos/riverbeds) — vanilla
    // surface, locations and biomes stay valid because we only subtract.
    public enum VoxelWorldMode { Vanilla, VoxelWrap, VoxelOnly }

    // SERVER-OWNER TERRAIN MODE (the one prominent selector, default Off).
    // Governs how much of the world becomes voxel, so the mod is safe to drop
    // onto ANY server:
    //   Off         — dormant. Terrain 100% vanilla; only the admin voxel
    //                 BRUSH tools work (they never touch un-drawn ground).
    //   PlacedOnly  — vanilla terrain stays vanilla; a zone converts only where
    //                 a pickaxe dig or voxel brush edited it or a sky island floats over it.
    //                 Sky islands only exist under FullVoxel's generator, so
    //                 today this converts exactly what ZoneReplace does.
    //   ZoneReplace — vanilla until a pickaxe dig or voxel brush edits a zone;
    //                 that zone converts to voxel ground identical to vanilla
    //                 plus the edits. Hoe/cultivator edits never convert.
    //   FullVoxel   — the complete swap: every near zone wraps with generated
    //                 caves, river beds and hydrology water, and the Fires
    //                 world generator runs (still name-gated to Fires* worlds).
    // PlacedOnly removed (it converted exactly what ZoneReplace does; its extra, sky-island zones, needs
    // FullVoxel). The other values keep their numbers. A config still set to PlacedOnly falls back to Off, logged once at Info.
    public enum FiresTerrainMode { Off = 0, ZoneReplace = 2, FullVoxel = 3 }

    // W4 base-generation gate. Auto = worlds named "Fires*" use the Fires
    // generator, everything else stays vanilla (deterministic on every peer
    // with zero network sync — the world name arrives in the handshake).
    // ForceOn exists for renamed test worlds; NEVER use it on a world that
    // already has builds — the terrain shifts wholesale.
    public enum FiresWorldGenMode { Auto, ForceOn, Off }

    // Voxel config. The whole module is additive + gated so it can be switched
    // off without touching the tile terrain or water systems.
    // Plan: Tools/FIRES_VOXEL_TERRAIN_PLAN.md
    public static class FiresVoxelConfig
    {
        private const string S = "Voxel Terrain";   // Formerly "VoxelTerrain (P0 prototype)"; values carried over
        internal const string OldS = "VoxelTerrain (P0 prototype)";
        private const string SW = "Voxel Terrain - World";   // Formerly "VoxelTerrain World"

        public static ConfigEntry<bool> Enabled;
        public static ConfigEntry<bool> ShowToolsInBuildMenu;
        public static ConfigEntry<float> SnowTrailTrench;   // 0.2.1094
        public static ConfigEntry<float> SnowDepthBudgetMs;   // 0.2.1196
        public static ConfigEntry<bool> CreatureSnowTrails;   // 0.2.1129
        public static ConfigEntry<float> WalkTrenchWidth;   // 0.2.1418
        public static ConfigEntry<bool> RegenVerbose; // 0.2.1223 fires_regen per-zone lines
        public static ConfigEntry<bool> GenWaterCache, DeepPumpIdle; // 0.2.1267 perf (A/B keys)
        public static ConfigEntry<bool> DeepPumpFirst; // 0.2.1268 perf (A/B key)
        public static ConfigEntry<bool> WaterDryColumnAutoProbe, TerrainOpRoutesOverVoxelEdits, ZoneRevealHold, WaterTrimFanOut, ApplySlotsTimed, SnowRetryBackoff, RiverStrokeOneClutterPass, RiverWaterWaitsForSwap;   // 0.2.1346
        public static ConfigEntry<bool> ClutterColliderOnGroundChange, ClutterSkipNoPatch, ClutterWetBandComplete, ClutterHoldMidSwapZone, WaterBedCache, WaterColumnAsksOnce, WaterSliceEveryAsk, WaterQuietWindow;   // 0.2.1347: A/B keys, default ON, off = 1346
        public static ConfigEntry<bool> ClutterWaterAtOwnSpot, RiverBankFill, RiverFallsLayLast, RiverPaintFollowsWater, RiverFallShape, RiverBankFillMore;   // 0.2.1348: default ON, off = 1347
        public static ConfigEntry<bool> WaterAnchorRepair, WaterFloorGate;   // 0.2.1349: default ON
        public static ConfigEntry<bool> RemeshHoldUntilSwap, StarveKeepsAge, SwapBeforePaint, ZoneRevealKeepsOutOfReach;   // 0.2.1353 (Fire: "get this terrain loading thing fixed"): default ON, off = 1352
        public static ConfigEntry<bool> WaterAnchorWriteBack;   // 0.2.1352 (Fire: "theres basically no river mesh that doesnt end up broken on relog"): default ON
        public static ConfigEntry<bool> BuildNearestFirst, WaterGroundSignature, WaterHoldUntilSwap, WaterSheetSliced, MopRefreshQueued;   // 0.2.1344: A/B keys, default ON (regression fixes), off = the 1343 path
        public static ConfigEntry<bool> StreamerScanSpread, TileMeshSpread, DistantPaintSpread, WetMaskRowLimit; // 0.2.1319 perf (A/B keys, PLAN_fat_frame_spread)
        public static ConfigEntry<bool> SnowDepthStepResumable; // 0.2.1322 perf (A/B key, PLAN_fat_driver_stalls F1)
        public static ConfigEntry<bool> ImpostorRebuildOnChange; // 0.2.1371
        public static ConfigEntry<bool> ShovelOnHeightmap; // 0.2.1376
        public static ConfigEntry<float> SnowSparkle; // 0.2.1405
        public static ConfigEntry<bool> BiomePaintMapTool, BiomePaintCustomBrushes, BiomePaintRegrowOnSave, BiomePaintSwapLocations, BiomePaintChangeHeight; // 0.2.1373
        public static ConfigEntry<float> BiomePaintBlendWidth; // 0.2.1373
        public static ConfigEntry<BepInEx.Configuration.KeyboardShortcut> BiomePaintWipeToggle; // 0.2.1374
        public static ConfigEntry<bool> ImpostorSkipBehindCamera; // 0.2.1372
        public static ConfigEntry<bool> ImpostorFrustumCull; // 0.2.1396
        public static ConfigEntry<bool> ImpostorGpuResident; // 0.2.1399
        public static ConfigEntry<bool> IceCoverOnChange; // 0.2.1292
        public static ConfigEntry<bool> IceCoverReuse; // 0.2.1396
        public static ConfigEntry<bool> IceCoverVedrWaits; // 0.2.1401
        public static ConfigEntry<bool> CaveChecksSkipNoVoxel, SkylandHoldSkipNoIslands, DeepQueueSnowBasesOnly; // 0.2.1369 perf
        public static ConfigEntry<bool> IceCoverVanilla; // 0.2.1360
        public static ConfigEntry<bool> SwimUnderSurfaceIce; // 0.2.1367
        public static ConfigEntry<float> DeepSnowSlowOutside; // 0.2.1278
        public static ConfigEntry<bool> TrailOpBatching;   // [saddles] trail op batching prep (FatTrailBatch)
        public static ConfigEntry<bool> RefineOffThread;   // 0.2.1197 [saddles] G refinement + normal guard on a worker thread
        public static ConfigEntry<float> CultivatedDepth, CultivatedNormalBoost;   // 0.2.1098 (Fire: "our cultivated ground still has no depth")
        public static ConfigEntry<UnityEngine.KeyCode> GhostUpKey, GhostDownKey, GhostFreezeKey, GhostLargeStepKey;
        public static ConfigEntry<float> CarveDrawSpeed, FillDrawSpeed, ShapeDrawSpeed, WaterDrawSpeed, LevelDrawSpeed, MopDrawSpeed, DabStepDepth;
        public static ConfigEntry<bool> LogTimings;
        public static ConfigEntry<bool> ProbeMatchesMesh, WaterProbeMatchesMesh, ProbeDeltaLog, SwimFollowsDrawnWater, WaterOnlyChunks;   // Proven fixes are always on and their keys are gone (zone gate, floater skip, reseat, spawn gate restart)
        public static ConfigEntry<int> MaxLoadedChunks;

        // Stage W master switch — DESIGNED IN FROM THE START:
        // the whole-world voxel wrap must always be a config choice, and
        // Terrain Mode defaults to Off. The admin BRUSH tools work in every mode
        // (they are sparse, additive and independent of the streamer).
        // 0.2.1336 (Fire): the "Terrain Mode" key is gone. The one choice is the world's, made at creation and kept in its .profile: Terrain
        // Heightmap = zones swap only on edit, VoxelWrap / VoxelOnly = zones swap automatically. This is only what an old cfg said, read once
        // from the orphaned entry, for a world that has no profile yet (FiresWorldProfile.Infer pins it) and the New World screen's default.
        public static bool LegacyConfigFullVoxel { get; private set; }
        public static ConfigEntry<bool> CustomGeneration;   // legacy master: OFF forces 100% vanilla (== TerrainMode.Off; kept for back-compat)
        public static ConfigEntry<VoxelWorldMode> WorldMode;
        public static ConfigEntry<BcImport.FiresOceanGap> OceanGap;

        // ── Derived gates (the whole mod reads these, not the raw entries) ──
        // The world's profile speaks first, the configuration second (FiresWorldProfile).
        public static FiresTerrainMode Mode
        {
            // 0.2.1336: the world's own profile on every peer, single player too (never two meshes). No world yet = dormant. A server
            // always has a profile from its world load on; a client's arrives in the handshake ahead of PeerInfo.
            get
            {
                var profile = FiresWorldProfile.Current;
                if (profile != null) return profile.TerrainMode();
                return ZNet.instance != null ? FiresTerrainMode.ZoneReplace : FiresTerrainMode.Off;
            }
        }

        // A server other players join (dedicated, or a host with the server open).
        public static bool ServerConvertsEdits
            => ZNet.instance != null && ZNet.instance.IsServer() && !ZNet.IsSinglePlayer;

        // 0.2.1334: any peer of a multiplayer session (dedicated server, open host, joined client); not single player.
        public static bool MultiplayerPeer
            => ZNet.instance != null && !ZNet.IsSinglePlayer;

        public static VoxelWorldMode WorldKind
            => FiresWorldProfile.Current != null
                ? FiresWorldProfile.Current.WorldKind(WorldMode?.Value ?? VoxelWorldMode.VoxelWrap)
                : (WorldMode?.Value ?? VoxelWorldMode.VoxelWrap);
        // The streamer runs (meshes voxel content) in every mode except Off.
        // Legacy CustomGeneration=OFF still forces dormant for existing configs.
        public static bool StreamerRuns =>
            Enabled != null && Enabled.Value
            && (CustomGeneration == null || CustomGeneration.Value)
            && Mode != FiresTerrainMode.Off;
        public static bool WrapsAllTerrain => Mode == FiresTerrainMode.FullVoxel;      // auto-wrap near zones
        public static bool WrapsBrushedZones => Mode == FiresTerrainMode.ZoneReplace;   // wrap voxel-brushed zones
        public static bool WrapsIslandZones => false;   // Was PlacedOnly (removed); FullVoxel wraps every near zone anyway
        public static bool GeneratesTerrain => Mode == FiresTerrainMode.FullVoxel;     // Fires worldgen active
        // Generated features exist only in FullVoxel; a zone converted in any
        // other mode is exactly the vanilla ground plus brush edits.
        public static bool GeneratesCaves
            => WrapsAllTerrain && (FiresWorldProfile.Current?.Caves ?? (GenerateCaves != null && GenerateCaves.Value));
        // Rivers are the exception: they convert only the zones a channel runs
        // through (VoxelRiverNetwork.TouchesZone), so they no longer need the
        // whole-world wrap. ZoneReplace + PlacedOnly carve too.
        // 0.2.1336: only where zones swap automatically. A heightmap world stays vanilla until edited (1334's Off rule had turned the carve on there).
        public static bool CarvesRiverBeds
            => StreamerRuns && WrapsAllTerrain && (FiresWorldProfile.Current?.Hydrology ?? (HydrologyEnabled != null && HydrologyEnabled.Value));
        // Only the coarse 20m solve needs the allowance; a Fires world's own 2m
        // field already knows where its banks are.
        public static float CoarseBankRelief => BankRelief != null ? BankRelief.Value : 0.4f;
        public static bool PlacesHydrologyWater
            => StreamerRuns && WrapsAllTerrain && (FiresWorldProfile.Current?.Hydrology ?? true) && HydrologyPlaceWater != null && HydrologyPlaceWater.Value;
        public static bool BuildsSkyIslands
            => WrapsAllTerrain && (FiresWorldProfile.Current?.SkyIslands ?? (SkyIslands == null || SkyIslands.Value));
        public static ConfigEntry<bool> WrapAllLoaded;
        public static ConfigEntry<float> WrapRadius;
        public static ConfigEntry<bool> GenerateCaves;
        public static ConfigEntry<float> CaveMinY;
        public static ConfigEntry<float> CaveMaxDepth;
        public static ConfigEntry<float> CaveWidth;
        public static ConfigEntry<float> CaveFrequency;
        public static ConfigEntry<float> CaveFrequencyY;
        public static ConfigEntry<float> CaveSurfacePinch;
        public static ConfigEntry<float> CavernScale;
        public static ConfigEntry<float> CavernRarity;
        public static ConfigEntry<float> CavernBoost;
        public static ConfigEntry<float> EntranceRarity;
        public static ConfigEntry<float> SinkholeRarity;
        public static ConfigEntry<float> SinkholeSpacing;
        public static ConfigEntry<float> SinkholeRadius;
        public static ConfigEntry<float> SinkholeDepth;
        public static ConfigEntry<bool> VoxelWater;
        public static ConfigEntry<bool> LegacyMeshWater;
        public static ConfigEntry<float> WaterDiagInterval;
        public static ConfigEntry<float> WaterPaintShoreBlend;
        public static ConfigEntry<float> WaterBankFreeboard;
        public static ConfigEntry<float> WaterMinChannelDepth;
        public static ConfigEntry<bool> WaterfallMist;
        public static ConfigEntry<float> WaterfallMistRange;
        public static ConfigEntry<float> WaterfallBrightness;
        public static ConfigEntry<float> WaterfallShapeProbeRadius;   // 0.2.1321 (Fire x266 z1114 curtain probe)
        public static float WaterfallShapeProbeMetres => WaterfallShapeProbeRadius != null ? WaterfallShapeProbeRadius.Value : 0f;
        private const float DefaultWaterfallMistRange = 200f;
        public static bool WaterfallMistOn => WaterfallMist == null || WaterfallMist.Value;
        public static float WaterfallMistRangeMetres => WaterfallMistRange != null ? WaterfallMistRange.Value : DefaultWaterfallMistRange;
        // (AutoPourWater, WaterfallBreakStep, WaterMaxSheetDepth, PondKeep,
        //  MountainPondKeep DELETED — the systems they tuned are gone: gen
        //  water is mesh-only, the fall/mid-air cuts died with the smooth
        //  sampler, and the natural-basin keep rolls died with the
        //  deliberate-depressions doctrine.)
        public static ConfigEntry<bool> SkyIslands;
        public static ConfigEntry<float> SkyIslandRarity;
        public static ConfigEntry<float> SkylandSiteChance, SkylandSiteChanceSuperFlat;   // 0.2.1096
        public static ConfigEntry<string> SkylandMiniSites;   // 0.2.1137: tiny-island sites (Fire: yes to a mini-site list)
        public static ConfigEntry<bool> VegWaterEcology;
        public static ConfigEntry<bool> FaithfulBakeWater;
        public static ConfigEntry<float> VegTreeWaterClearance;
        public static ConfigEntry<string> VegTreeNames;
        public static ConfigEntry<string> VegWaterSinkerNames;
        public static ConfigEntry<bool> CaveContentEnabled;
        public static ConfigEntry<float> CaveContentDensity;
        public static ConfigEntry<float> CaveRockScale;
        public static ConfigEntry<bool> CaveWaterEnabled;
        public static ConfigEntry<float> CaveWaterChance;
        public static ConfigEntry<float> CaveWaterMinHeadroom;
        public static ConfigEntry<float> CaveWaterRadius;
        public static ConfigEntry<bool> CaveEnvEnabled;
        public static ConfigEntry<string> CaveEnvironment;
        public static ConfigEntry<bool> SeamLips;
        public static ConfigEntry<bool> FrontierLips;
        public static ConfigEntry<bool> SeamAuditAuto;
        public static ConfigEntry<float> BrushDrawSpeed;
        public static ConfigEntry<bool> FarQuiltEnabled;
        public static ConfigEntry<float> FarQuiltNearRadius;
        public static ConfigEntry<float> FarQuiltFarRadius;
        public static ConfigEntry<FiresWorldGenMode> WorldGenMode;
        public static ConfigEntry<int> WorldGenResolution;
        public static ConfigEntry<int> WorldGenDroplets;
        public static ConfigEntry<string> WorldGenVahcBase;
        public static ConfigEntry<string> WorldGenImageBase;
        public static ConfigEntry<float> WorldGenImageMinY;
        public static ConfigEntry<float> WorldGenImageMaxY;
        public static ConfigEntry<string> WorldGenBiomeImage;
        public static ConfigEntry<bool> HydrologyEnabled;
        public static ConfigEntry<bool> HydrologyPlaceWater;
        public static ConfigEntry<float> RiverThreshold;
        public static ConfigEntry<float> RiverMaxDepth;
        public static ConfigEntry<float> SpringSpacing;
        public static ConfigEntry<float> RiverWidthMin;
        public static ConfigEntry<float> RiverWidthMax;
        public static ConfigEntry<float> RiverFreeboard;
        public static ConfigEntry<float> BankRelief;
        public static ConfigEntry<float> RiverMaxNewCut;
        public static ConfigEntry<float> RiverMeander;
        public static ConfigEntry<float> EstuaryLength;
        public static ConfigEntry<float> EstuaryFlare;
        public static ConfigEntry<bool> FrozenWaterIdle;
        public static ConfigEntry<bool> ChunkCacheEnabled;
        public static ConfigEntry<float> ChunkCacheSizeMB;
        public static ConfigEntry<float> WrapHeadlessCap;
        public static ConfigEntry<bool> ServerBuildsCollision;
        // A dedicated server meshes voxel colliders: always in VoxelOnly, and in VoxelWrap unless switched off.
        internal static bool ServerCollides => WorldKind == VoxelWorldMode.VoxelOnly || ServerBuildsCollision == null || ServerBuildsCollision.Value;
        public static ConfigEntry<float> WrapKeepRadius;
        public static ConfigEntry<float> VegetationDensityScale;
        public static ConfigEntry<bool> VegetationWarmUp;
        public static ConfigEntry<bool> BakeReadAhead;
        public static ConfigEntry<float> ClutterDensityScale;
        public static ConfigEntry<float> GrassRenderDistance;
        public static ConfigEntry<bool> ImpostorsEnabled;
        public static ConfigEntry<float> ImpostorRadius;
        public static ConfigEntry<float> ImpostorForwardRadius;
        public static ConfigEntry<float> ImpostorNearSkip;
        public static ConfigEntry<float> ImpostorDensity;
        public static ConfigEntry<bool> TerrainCastShadows;
        public static ConfigEntry<int> MeshAppliesPerFrame;
        public static ConfigEntry<int> MeshBufferBudget;

        private static bool _bound;

        // THE SERVER OWNS THE TERRAIN.
        //
        // Every entry below feeds the GROUND a peer builds: the mode selectors, the
        // cave carve, the river/hydrology carve, sky islands and the generator base.
        // A client holding different values builds a different world from the same
        // seed — it renders and collides against terrain the server never had, while
        // the server saves edits and places content against its own. Nothing warns;
        // it just silently disagrees. So these ride the mod's ConfigSync: a joining
        // client takes the server's values, and cannot edit them while the server
        // holds the lock.
        //
        // PER-MACHINE settings are deliberately NOT here. Wrap radius, chunk cap,
        // cache size, mesh budgets, impostors, grass distance, shadows, seam lips,
        // draw speed, log toggles and the build-menu listing change cost or looks,
        // never shape — forcing them would make a weak client run the server's
        // budget for no correctness gain.
        internal static void RegisterServerAuthority(FiresCore.Sync.ConfigSync sync)
        {
            if (sync == null) return;
            int n = 0;
            void Own<T>(ConfigEntry<T> entry) { if (entry == null) return; sync.AddConfigEntry(entry); n++; }

            Own(Enabled); Own(CustomGeneration); Own(WorldMode); Own(WorldGenMode); Own(OceanGap);

            Own(GenerateCaves); Own(CaveMinY); Own(CaveMaxDepth); Own(CaveWidth);
            Own(CaveFrequency); Own(CaveFrequencyY); Own(CaveSurfacePinch);
            Own(CavernScale); Own(CavernRarity); Own(CavernBoost); Own(EntranceRarity);
            Own(SinkholeRarity); Own(SinkholeSpacing); Own(SinkholeRadius); Own(SinkholeDepth);

            Own(VoxelWater); Own(WaterBankFreeboard); Own(WaterMinChannelDepth);
            Own(HydrologyEnabled); Own(HydrologyPlaceWater);
            Own(RiverThreshold); Own(RiverMaxDepth); Own(SpringSpacing);
            Own(RiverWidthMin); Own(RiverWidthMax); Own(RiverFreeboard); Own(BankRelief);
            Own(RiverMaxNewCut); Own(RiverMeander); Own(EstuaryLength); Own(EstuaryFlare);

            Own(SkyIslands); Own(SkyIslandRarity); Own(SkylandMiniSites);   // 0.2.1137

            // 0.2.1135: the rich-cave roll decides WHICH zones get ore, so every peer must roll the same zones (the test and the map read it on a client).
            Own(CaveContentConfig.RichCaveChanceEntry);
            // 0.2.1137: the existing-world location replan (cave sites + sky island sites) must be decided the same on every peer.
            Own(VerdantsAscent.Terrain.Zones.FatLocationAccelerator.ApplyReplanExisting);

            Own(WorldGenResolution); Own(WorldGenDroplets); Own(WorldGenVahcBase);
            Own(WorldGenImageBase); Own(WorldGenImageMinY); Own(WorldGenImageMaxY);
            Own(WorldGenBiomeImage);

            FiresLogger.LogInfo($"[FiresVoxel] {n} terrain-shaping setting(s) are server-owned; a client takes the "
                                + "server's values on join. Performance and visual settings stay local.");
        }

        public static void Bind(ConfigFile cfg)
        {
            if (_bound || cfg == null) return;
            _bound = true;
            FatDiag.Bind(cfg);   // 0.2.1102: per-feature diagnostic log switches (default OFF)

            Enabled = cfg.Bind(S, "Enabled", true,
                "Voxel terrain tools and voxel ground (carve, fill, level, paint). Edits are saved with the world and synced to every player.");
            ShowToolsInBuildMenu = cfg.Bind(S, "Show tools in build menu", true,
                "List the Fires terrain/water tools on the hammer. They are admin-only regardless; "
                + "turn this off for a vanilla-looking build menu without giving up admin rights.");
            // 0.2.1098 (Fire: "our cultivated ground still has no depth"; vanilla tilled soil is only a normal map on flat ground): visual-only
            // depth for cultivated voxel ground, read by FiresVoxelHeightmap.shader (_FiresCultDepth / _FiresCultDepthNormal). 0 / 0 = vanilla.
            CultivatedDepth = cfg.Bind(S, "Cultivated depth (m)", 0.12f,
                new ConfigDescription("Cultivated voxel ground: how deep its furrows and clods read (parallax height, visual only, never terrain data; 0.2.1100). 0 = vanilla flat look.", new AcceptableValueRange<float>(0f, 0.3f)));
            CultivatedNormalBoost = cfg.Bind(S, "Cultivated normal boost", 1.5f,
                new ConfigDescription("Cultivated voxel ground: extra strength of vanilla's tilled-soil normal map plus furrow rows (visual only). 0 = vanilla.", new AcceptableValueRange<float>(0f, 5f)));
            // 0.2.1094 (Fire: "Yes, smooth trench"): how far vanilla's deep-snow walk trail sinks the voxel snow raise.
            // 0.2.1301 (Fire 10-30, final: "we will go with Core's raised terrain so it can remain vanilla compatible when it's not over a
            // voxel chunk, and keep our own good voxel snow for voxel chunks"): FAT's heightmap snow skin is OFF for every install - a NEW
            // key name (default false) so the old saved "true" no longer reaches it (the old "Heightmap snow (depth + trails)" is left
            // unread). On vanilla heightmap ground Core's raised terrain is the snow; voxel snow is unchanged.
            // 0.2.1264 ([fgn] round S perf: the skins were 786k tris / 24 renderers, drawn again in the shadow passes): the outer ring of
            // skins (zones 2 away from the camera) can be lighter. Both default ON; kept only if an A/B shows the gain.
            // 0.2.1268 ([fgn] PatchTimer, Fire 10-30 standing: FAT's ZNet.Update postfixes 158 ms/s; the 1267 pump line: queue 300k, cache 18). A/B keys, default ON.
            // 0.2.1270 ([fgn]: 1268's skin build still at its 1 ms cap every frame, worst 7.5 ms = the one-frame mesh finish). A/B keys, default ON.
            // 0.2.1274 (proof round 02:24: town trails never redrawn - 9 skins built / 30 s vs 20 full depth re-samples; trail edges 0.25 m walls). A/B keys, default ON.
            DeepSnowSlowOutside = cfg.Bind(S, "Deep snow slow outside the snow biomes", 0.5f, new ConfigDescription("How much of vanilla deep-snow slow applies in seasonal snow outside the Deep North and Mountains: 0 = none, 1 = full vanilla. Those two biomes always keep vanilla; snowshoes still remove it (0.2.1278, Fire: light slow).", new AcceptableValueRange<float>(0f, 1f)));
            SwimUnderSurfaceIce = cfg.Bind(S, "Swim under ice at the water surface", true, "Ice and other covers lying on the water are not a cave roof, so swimming under them holds (0.2.1367). Off: anything solid overhead drops the water.");
            IceCoverVanilla = cfg.Bind(S, "Waves fade under vanilla ice", true, "Vanilla sea ice plates and Vedr floes join the ice cover texture, so water shaders hide waves under them (0.2.1360). Off: FAT ice and Vedr lake ice only.");
            CaveChecksSkipNoVoxel = cfg.Bind(S, "Cave checks skip worlds without voxel ground", true, "With no voxel ground there are no caves, so cave checks answer at once (0.2.1369). Off: every check runs, with the same answer.");
            SkylandHoldSkipNoIslands = cfg.Bind(S, "Skyland hold skips worlds without skylands", true, "With no skylands and no voxel ground, the per-character skyland hold answers at once (0.2.1369). Off: every check runs, with the same answer.");
            DeepQueueSnowBasesOnly = cfg.Bind(S, "Deep ground queue asks snow bases only", true, "In winter, the deep ground tint queue skips zones with no snow base, as it does in summer (0.2.1369; needs a Core with ZoneMayHaveSnowBase, an older Core keeps the old question). Off: winter queues every zone. Read once per game session.");
            IceCoverOnChange = cfg.Bind(S, "Ice cover refills only on change", true, "The ice cover texture other mods' water shaders read refills only when the camera moved, the ice changed (FAT's or Vedr's) or 30 s passed, instead of every 3 s (0.2.1292; perf, A/B).");
            IceCoverReuse = cfg.Bind(S, "Ice cover keeps what it already knows", true, "When only the camera moved, the ice map keeps the part it already filled and reads only the new strip, with a whole fill at least every 30 s. Less work while travelling. Off reads the whole map each time.");
            IceCoverVedrWaits = cfg.Bind(S, "Ice cover waits for the refresh on lake ice changes", true, "When Vedr's lake ice changes, the ice map takes it in at its next refresh (every 3 s) instead of reading the whole map again at once. Vedr reports a change for every lake that loads while you travel. Off = read the whole map on every change.");
            DeepPumpFirst = cfg.Bind(S, "Deep-cell pump before the tint", true, "The deep-snow ground pump answers its queue first on its own budget, then the heightmap tint runs (the tint used the whole shared budget and waited forever on the pump).");
            // 0.2.1319 (PLAN_fat_frame_spread_20261031 item 2): heavy one-frame ops spread over frames. A/B keys, default OFF (0.2.1320: perf changes stay off until their A/B proves them); off = the old one-frame path.
            // 0.2.1344 (r22, Fire: fill from closest out).
            BuildNearestFirst = cfg.Bind(S, "Voxel build order nearest first", true, "Zone wraps start with the eligible zone nearest the player, and the mesher and the mesh apply take the pending chunk nearest the player or camera. Far work still drains. Live edits keep their urgent lane. Off: list order and first-in, first-out.");
            WaterGroundSignature = cfg.Bind(S, "Placed water ignores remeshes with unchanged ground", true, "A voxel chunk remesh whose density did not change (paint, snow, refinement, a border mirror) drops no placed-water cap tiles and rebuilds no sheet. Any real ground change and a first mesh still do. Off: every Version change does.");
            WaterHoldUntilSwap = cfg.Bind(S, "Placed water holds rebuilds until the zone swaps", true, "While a zone is meshing toward its swap, the remeshes of its chunks hold their placed-water rebuilds and run them once when the swap lands (or after 20 s). A sheet never drawn yet still shows at once. Off: each remesh queues its rebuilds.");
            WaterSheetSliced = cfg.Bind(S, "Placed water sheet build sliced over frames", true, "A placed-water sheet rebuild runs in slices inside the frame budget and goes live whole in one frame. Sheets a tool stroke changes stay immediate. Off: each build runs whole in one frame.");
            // 0.2.1346 (PLAN_fat_zone_swap_20261102): regression fixes, default ON; off = the 1345 path.
            WaterDryColumnAutoProbe = cfg.Bind(S, "Placed water dry column auto probe", false, "Diagnostic: with Debug logging on, scan the 8 m around the player for dry placed-water columns once a second (measured 62-80 ms a run). Off: the console command fat_water_probe still answers on demand.");
            TerrainOpRoutesOverVoxelEdits = cfg.Bind(S, "Terrain op over voxel edits edits the voxels", true, "A level, raise, lower, slope, min or max from World Edit Commands or Infinity Hammer whose circle touches a chunk with voxel brush edits reshapes the voxels, because a TerrainComp raise cannot fill a carved column. Open ground keeps the TerrainComp; paint still reaches it. Off: only a cave under the centre column routes.");
            ZoneRevealHold = cfg.Bind(S, "Zone swap reveals everything in one frame", true, "Water sheets, fall curtains, shore pieces, underwater clutter and grass resets of a zone with a swap coming wait and land in the swap's frame; the swap waits up to 3 s for the zone's paint and snow. No swap coming, or 60 s in reach with no swap, releases them. Off: they show as they build.");
            WaterTrimFanOut = cfg.Bind(S, "Placed water rebuilds only where ground can change the answer", true, "A chunk remesh or heightmap regenerate rebuilds only drawn sheets it can reach, once after the last zone around them swaps; a heightmap that regenerates to the same heights, or is about to be replaced by voxel ground, rebuilds nothing. Off: every neighbour in reach, every time.");
            ApplySlotsTimed = cfg.Bind(S, "Voxel mesh apply slots by time", true, "Mesh applies and collider assigns per frame are limited by time, not a count, and a slow frame gets a proportionally larger share up to the driver budget, so low fps does not throttle the drain. Off: the per-frame count and the fixed 3 ms.");
            SnowRetryBackoff = cfg.Bind(S, "Snow first show retries back off", true, "A zone that answers no snow is asked again after 1, 2, 4, 8 s, and at once if the snow cover changes, instead of every second for 20 s. Off: every second.");
            RiverStrokeOneClutterPass = cfg.Bind(S, "River stroke one clutter pass", true, "Grass resets during a river stroke are held and run once when the carve, its water and its zone swaps are done (at most 30 s after the carve). Off: resets run through the stroke.");
            RiverWaterWaitsForSwap = cfg.Bind(S, "River water waits for the ground swap", true, "A river disk whose bed is not seen at 10 s keeps waiting while its zone's chunks are still building or swapping (up to 45 s). A disk still refused tells the admin on screen and logs a Warning. Off: refused at 10 s with an Info line.");
            // 0.2.1348 (Fire: mesh on slopes, floating clutter, falls, bed paint): default ON, off = 1347.
            ClutterWaterAtOwnSpot = cfg.Bind(S, "Water clutter needs water at its own spot", true, "Lilies and vass in a patch on our water need water at their own position and are judged against that surface. Lilies also need water under their whole pad. Off: every instance is judged against the patch level.");
            RiverBankFill = cfg.Bind(S, "River banks filled under the water", true, "When a river stroke has landed, ground under its drawn water outside the channel is filled to the water level plus the freeboard, up to 6 m of gap. Off: only the fill on the path centre.");
            RiverFallsLayLast = cfg.Bind(S, "River falls placed last", true, "A river stroke places its waterfalls after its water and bank fill, from the real water surfaces. A fall with no water above or below is dropped with a log line. Off: placed during the carve from planned heights.");
            RiverPaintFollowsWater = cfg.Bind(S, "River bed paint follows the water", true, "When a river stroke has landed, its wet bed and shore paint are written again before its one grass pass. Off: paint only when each disk first answers.");
            // 0.2.1349 (Fire: "every time we log back in its broken"): default ON.
            WaterAnchorRepair = cfg.Bind(S, "Water anchor repair on world load", true, "Server: at world load a saved water group with no anchor gets one, so its disks reach clients after a relog. Only adds anchors. Off: no repair.");
            // 0.2.1353 (Fire: "get this terrain loading thing fixed"): default ON, off = 1352.
            RemeshHoldUntilSwap = cfg.Bind(S, "Voxel remeshes wait for the zone swap", true, "While a zone is loading in, a chunk rebuild that cannot change its ground (border seam, lips, paint refinement) waits and runs once when the zone swaps, or after 20 s, so the mesh buffers go to chunks still waiting for their first mesh. First meshes, edits and saved records never wait. Off: every rebuild queues at once.");
            StarveKeepsAge = cfg.Bind(S, "Voxel chunk waiting for a buffer keeps its age", true, "A chunk that finds every mesh buffer busy goes back in line with its original age, so the oldest-first turn can still reach it. Off: each retry counts as new.");
            SwapBeforePaint = cfg.Bind(S, "Zone swap does not wait for paint and snow", true, "A zone swaps as soon as its meshes and colliders are ready, and its paint and snow land in the frames right after. Off: the swap waits up to 3 s for them.");
            ZoneRevealKeepsOutOfReach = cfg.Bind(S, "Zone reveal keeps holding out of reach", true, "Water and shore pieces held for a zone swap stay held if the zone falls out of wrap reach with its swap still coming, until it swaps or unloads. Off: they show when the zone leaves reach.");
            // 0.2.1352 (Fire: "theres basically no river mesh that doesnt end up broken on relog"): default ON, off = 1350.
            WaterAnchorWriteBack = cfg.Bind(S, "Water cached disks written back to anchors", true, "Server: at world load any saved water disk no anchor holds is written to its group's anchor, so it reaches clients after a relog. Removed disks stay removed. Needs the anchor repair key on. Off: a group with an anchor keeps only the disks that anchor already holds.");
            WaterFloorGate = cfg.Bind(S, "Pond disks stay on their own floor", true, "A pond disk more than 8 m from the level of every pond disk it overlaps starts its own water body. River disks are exempt, so falls still join. Off: first overlap joins.");
            // 0.2.1349 (Fire on 1348: more edge fill, better fall placement): default ON, off = 1348.
            RiverFallShape = cfg.Bind(S, "River falls span the channel at the real brink", true, "A river fall starts where the water first leaves the upper surface and spans the open channel between its banks. Off: the lip follows the surface down and the width reads the disk.");
            RiverBankFillMore = cfg.Bind(S, "River banks filled further out", true, "Bank fill reaches 2 m further, with a wider brush, and also fills columns whose ground is up to 12 m below the water. Off: 6 m and the sheet reach only.");
            // 0.2.1347: A/B keys, default ON, off = 1346.
            ClutterColliderOnGroundChange = cfg.Bind(S, "Clutter re-rolls on a collider only when the ground changed", true, "A voxel chunk's new collider re-rolls the grass over it only when the chunk's density or vertex count changed. A remesh for paint, snow or a border mirror keeps the grass. Off: every new collider re-rolls.");
            ClutterSkipNoPatch = cfg.Bind(S, "Clutter reset skips ground with no grass patch", true, "A grass reset that touches no existing grass patch stops at once, before it marks any window. Grass there is generated fresh when you get there. Off: it goes through the reset windows.");
            ClutterWetBandComplete = cfg.Bind(S, "Clutter re-rolls on a wet stamp only when the band changed", true, "A heightmap's wet band re-rolls its grass once, when the band is fully built and differs from the band last rolled. Off: every stamp that writes the mask re-rolls, one per partly built row set.");
            ClutterHoldMidSwapZone = cfg.Bind(S, "Clutter resets wait for the zone swap", true, "A grass reset centred in a zone mid-swap waits and runs once when the zone swaps (at most 20 s). Off: it waits only when its whole circle is inside zones mid-swap, which a chunk's circle rarely is.");
            WaterBedCache = cfg.Bind(S, "Placed water bed reads cached", true, "Placed water reads the bed under a column once per tile and level, until the ground there really changes. Off: every ask reads the voxels.");
            WaterColumnAsksOnce = cfg.Bind(S, "Placed water column asks its level once", true, "One sheet build asks a column's disk level once, and the debug proof lines skip columns they already checked. Off: asked again for the surface.");
            WaterSliceEveryAsk = cfg.Bind(S, "Placed water slice checks time on every ask", true, "A sliced sheet build checks the frame budget after every corner and skirt ask, so a slice holds close to the budget. Off: every 16 corners and 8 vertices.");
            WaterQuietWindow = cfg.Bind(S, "Placed water rebuilds wait for a quiet moment", true, "A drawn sheet rebuilt in the last 0.5 s waits until its ground has been quiet for 0.5 s (at most 4 s), so a load rebuilds it once instead of many times. The first change after a quiet spell, first shows and tool strokes stay immediate. Off: every 0.5 s while neighbours land.");
            MopRefreshQueued = cfg.Bind(S, "Mop mask arrival refresh queued", true, "A mop mask arriving with a chunk record refreshes the water bodies around it through a queue: once per body, over several frames. The same mask coming back with a reloaded chunk refreshes only what was built while it was away. Off: every body regenerates at once, in the frame of the record.");
            StreamerScanSpread = cfg.Bind(S, "Voxel streamer zone scan spread over frames", false, "The voxel streamer's wrap/swap decision pass over the loaded heightmaps continues over the next frames at about 1 ms a frame instead of running in one frame. Chunk spawn / remove still happen in the frame they are decided (0.2.1319; perf, A/B).");
            TileMeshSpread = cfg.Bind(S, "Tile mesh render rebuild spread", false, "An edited terrain tile's visible mesh (normals, seam normals, paint mask) is rebuilt in a later frame with shared FAT budget left; its collider is still rebuilt in the same frame (0.2.1319; perf, A/B).");
            DistantPaintSpread = cfg.Bind(S, "Distant paint copy spread", false, "Distant low-detail terrain paint is composed row by row over frames into a scratch buffer, then uploaded once (the tile keeps its old paint until then) (0.2.1319; perf, A/B).");
            WetMaskRowLimit = cfg.Bind(S, "Wet mask band time limit inside a row", false, "The wet shore band build checks its time limit inside a row as well as between rows, so one call cannot run long on a very wide heightmap; only whole rows are stamped (0.2.1319; perf, A/B).");
            // 0.2.1322 ([lead] 10-31, PLAN_fat_driver_stalls F1: one snow-depth step ~12 ms against its 2 ms budget, 177/183 windows with 16-85 ms frames).
            SnowDepthStepResumable = cfg.Bind(S, "Snow depth step resumable", false, "Deep snow on voxel ground: a zone's snow depth refill (grid + texture) is cut into row slices that fit the snow depth rebuild budget and carry on next frame; the zone's new snow shows only once its step is complete (same snow, no half state). A zone changed again while its step runs restarts that step once. Off = the whole step in one frame as before (0.2.1322; perf, A/B).");
            // 0.2.1267 (Fire 10-30 standing fps 22-30; fire_hand_0005: FatHeightmapSnow 307k tris x4.7 passes = the biggest render item,
            // skins rebuilt 14x / 30 s standing, generated-water lookups 3.5 ms/s, deep-cell pump at its 15 ms/s cap). A/B keys, default ON.
            // 0.2.1269 (Fire 10-30 shots 364 / 365: a lit seam along z 96, 1 m from him = the 0.5 m / 1 m grid line since 1267: on the thin Meadows
            // sheet the 1 m triangles cut under the bumpier ground next to the camera zone). Renamed key so the old "true" in the cfg is not kept: default OFF.
            GenWaterCache = cfg.Bind(S, "Generated water lookup cache", true,
                "Swim / water checks reuse the generated river / lake surface per 2 m cell for 1 s instead of recomputing it every physics step.");
            DeepPumpIdle = cfg.Bind(S, "Deep-cell pump idles when standing", true,
                "The deep-snow ground tint pump runs at a tenth of its rate while the camera has not moved more than 4 m in 5 s and no new heightmap is waiting.");
            RegenVerbose = cfg.Bind("BcImport", "Regen verbose logging", false,
                "fires_regen: log every zone (generated with BcImport, skip reasons, location traces). Off = one progress line per 1% and a summary at the end.");
            SnowTrailTrench = cfg.Bind(S, "Snow trail trench", 1f,
                new ConfigDescription("Deep snow on voxel ground: how far a walked trail (vanilla's deep-snow walk paint) sinks into the snow. 0 = flat paint on top of the snow, 1 = down to the ground. Visual only.",
                    new AcceptableValueRange<float>(0f, 1f)));
            // 0.2.1196 ([ghost] fly53: 150 s / 6 s spikes of 150-200 ms = every loaded snow zone rebuilt in one frame).
            SnowDepthBudgetMs = cfg.Bind(S, "Snow depth rebuild budget (ms)", 2f,
                new ConfigDescription("Deep snow on voxel ground: milliseconds per frame for rebuilding changed zone snow depth; the rest carries on next frame. 0 = all in one frame (old behaviour). Same snow; a big change spreads over a few frames.",
                    new AcceptableValueRange<float>(0f, 20f)));
            // 0.2.1129 (Fire: "Creatures should make paths in the snow as well ... deer or boar or humanoid skeletons carve out paths").
            WalkTrenchWidth = cfg.Bind(S, "Walk trench width", 0.8f, new ConfigDescription("Deep snow on voxel ground: width of the trench the player walks, wall to wall (m). 0.2.1418 (Fire: trails \"FAR too fat\"; [lead]: Vedr's rule, ~1.4 m with the soft edge, core under 1 m): 0.8. Was 1.0.", new AcceptableValueRange<float>(0.6f, 1.5f)));
            CreatureSnowTrails = cfg.Bind(S, "Creature snow trails", false,
                "Deep snow on voxel ground: walking creatures (animals, monsters, companions) carve a trench like the player's, "
                + "as wide as the creature (at least 0.9 m, giants up to 6 m). Written by the creature's owner; refills with snowfall. Visual only.");
            TrailOpBatching = cfg.Bind(S, "Batch trail terrain ops", true,
                "Snow trails (player walk + creatures): stamps go to the terrain's owner in batches (every 0.5 s or 16 stamps) and are "
                + "applied with one terrain save per batch instead of one per stamp. Same trench, far less work on the peer that owns the "
                + "terrain. Every peer needs this FAT version for its batches to apply.");
            RefineOffThread = cfg.Bind(S, "Refine trail ground off the main thread", true,
                "Cultivated soil, snow trails and digs: the fine 0.25 m ground mesh near the player is built on a worker thread instead "
                + "of the main game thread. Same ground; the frame no longer pays for it. A rebuilt chunk shows one frame later.");
            // Carving must be much slower (one click used to carve a massive tunnel), with its carve
            // speed configurable (draw speed), same with fill and level. Dabs per second while the button is held, per
            // tool family, and how deep one dab may reach past the ground at the cursor.
            const string TOOLS = "Voxel Terrain - Tools";   // Formerly "VoxelTerrain Tools"
            CarveDrawSpeed = cfg.Bind(TOOLS, "Carve draw speed", 3f,
                new ConfigDescription("Carve and restore: dabs per second while the mouse button is held (one click = one dab).",
                    new AcceptableValueRange<float>(0.5f, 30f)));
            FillDrawSpeed = cfg.Bind(TOOLS, "Fill draw speed", 3f,
                new ConfigDescription("Fill: dabs per second while the mouse button is held (one click = one dab).",
                    new AcceptableValueRange<float>(0.5f, 30f)));
            ShapeDrawSpeed = cfg.Bind(TOOLS, "Shape draw speed", 3f,
                new ConfigDescription("Cube and sphere stamps: dabs per second while the mouse button is held.",
                    new AcceptableValueRange<float>(0.5f, 30f)));
            // 0.2.1026 (Fire 10-24: our tools follow the Infinity Hammer conventions for the placement ghost). Our own entries,
            // defaulting to IH's binds, so a player without IH has the same keys. None of them is a vanilla bind.
            GhostUpKey = cfg.Bind(TOOLS, "Ghost up key", UnityEngine.KeyCode.PageUp,
                "V-tools: raise the tool's ghost (its height offset); held, it repeats. Infinity Hammer's Move up default.");
            GhostDownKey = cfg.Bind(TOOLS, "Ghost down key", UnityEngine.KeyCode.PageDown,
                "V-tools: lower the tool's ghost. Infinity Hammer's Move down default.");
            GhostLargeStepKey = cfg.Bind(TOOLS, "Ghost large step modifier", UnityEngine.KeyCode.LeftAlt,
                "Held with the ghost up or down key: 5x steps (Infinity Hammer's large moves). Shift held instead: 0.1 m steps.");
            GhostFreezeKey = cfg.Bind(TOOLS, "Ghost freeze key", UnityEngine.KeyCode.Keypad0,
                "V-tools: freeze the ghost where it is; it stops following the crosshair and clicks act at the frozen ghost. Press again to release. Infinity Hammer's Freeze selection default.");
            WaterDrawSpeed = cfg.Bind(TOOLS, "Water tool draw speed", 4f,
                new ConfigDescription("River and pour: dabs per second while the mouse button is held.",
                    new AcceptableValueRange<float>(0.5f, 30f)));
            LevelDrawSpeed = cfg.Bind(TOOLS, "Level draw speed", 3f,
                new ConfigDescription("Vlevel: dabs per second while the mouse button is held (one click = one dab).",
                    new AcceptableValueRange<float>(0.5f, 30f)));
            MopDrawSpeed = cfg.Bind(TOOLS, "Mop draw speed", 4f,
                new ConfigDescription("Vmop (remove water, LeftAlt = restore): dabs per second while the mouse button is held.",
                    new AcceptableValueRange<float>(0.5f, 30f)));
            DabStepDepth = cfg.Bind(TOOLS, "Dab step depth", 1f,
                new ConfigDescription("Carve and fill: how far (m) one dab reaches past the ground at the cursor. Smaller = slower, "
                    + "finer digging; the brush size still sets the width.", new AcceptableValueRange<float>(0.25f, 32f)));
            const string PERF = "Voxel Terrain - Performance";   // Formerly "VoxelTerrain Performance"
            // UNDER MEASUREMENT — the first reading (load 12.53ms vs generate
            // 5.31ms) was contaminated: the timed region included an
            // File.SetLastAccessTimeUtc LRU touch, i.e. a filesystem metadata
            // WRITE on every hit. That is fixed (LRU is in memory now), so the
            // number needs re-taking before density caching is judged.
            //
            // Standing caveat regardless of the result: the bottleneck measured
            // on the dedi was the main-thread APPLY queue (swapped=0 with
            // chunks sitting in `queued=N`), and neither this nor a mesh cache
            // touches that — both only make the worker produce FASTER into an
            // already-backed-up queue.
            ChunkCacheEnabled = cfg.Bind(PERF, "Chunk cache", true,
                new ConfigDescription("Cache generated chunk density to disk so revisiting an area skips regeneration entirely. Chunks are otherwise rebuilt from scratch EVERY visit — deterministic, but the cost repeats forever. Keyed by world seed + every cave/river/sinkhole setting, so changing any of them re-derives automatically instead of serving stale terrain. Player edits are never cached."));
            ChunkCacheSizeMB = cfg.Bind(PERF, "Chunk cache size limit (MB)", 512f,
                new ConfigDescription("Disk budget for the chunk cache. Oldest-accessed entries are evicted on a background sweep once the store exceeds this. Only Full chunks cost anything (~25% of them); uniform solid/air chunks are a single byte.",
                    new AcceptableValueRange<float>(64f, 4096f)));
            FrozenWaterIdle = cfg.Bind(PERF, "Frozen water idles", true,
                new ConfigDescription("Frozen liquid volumes skip vanilla's per-frame Update — most importantly the once-per-second height refresh of 4,225 blocking main-thread raycasts PER VOLUME. One full pass still runs every ~2s per volume so multiplayer freeze/unfreeze and ZDO changes propagate. OFF = vanilla behavior: every frozen river keeps raycasting forever."));
            WrapHeadlessCap = cfg.Bind(PERF, "Voxel wrap radius (headless)", 160f,
                new ConfigDescription("How far (metres) around each PEER a dedicated server voxelises. Headless wraps every loaded zone rather than one player radius, so this cap is what actually bounds server load. THE biggest lever: a 64m zone is ~16 chunks and each must be generated, meshed and APPLIED before the zone can swap. At the old 256 (a 9x9 envelope) the dedi measured zones=44 chunks=654 with swapped=0 pendingSwap=44 — not one zone ever finished, all stuck in the apply queue. Vanilla keeps 3x3 zones active (~96m) plus a distant ring. Clients use 'Wrap Radius' instead.",
                    new AcceptableValueRange<float>(96f, 512f)));
            DediTextureTrim.Bind(cfg, PERF);   // RAM trim (default off)
            FatNativeTrace.Bind(cfg, PERF);    // Native crash hunt (default off)
            ServerBuildsCollision = cfg.Bind(PERF, "Server builds voxel collision", true,
                new ConfigDescription("Dedicated server in World Terrain Mode VoxelWrap: mesh voxel COLLIDERS (no renderers) for the zones "
                    + "around each peer and hide vanilla's heightmap collider there, so what the server simulates (creatures, drops, "
                    + "physics) stands on the voxel ground - caves, carved rivers and every player edit - as the clients do. Off = the "
                    + "server keeps voxel data only and its physics stands on vanilla's ground (FAT 0.2.651 and older). VoxelOnly always "
                    + "builds them. Bounded by 'Voxel wrap radius (headless)'."));
            WrapKeepRadius = cfg.Bind(PERF, "Voxel wrap keep radius", 128f,
                new ConfigDescription("Floor the memory governor may never ease the wrap radius below. In VoxelOnly the voxel collider is the ONLY ground, so a player's own footprint must always stay wrapped — set this too low under memory pressure and players fall through the world. Keep it at or above vanilla's active area (~96m corner).",
                    new AcceptableValueRange<float>(96f, 256f)));
            VegetationDensityScale = cfg.Bind(PERF, "Vegetation density scale", 0.7f,
                new ConfigDescription("Multiplies how many TREES/BUSHES each zone places (ZoneVegetation m_min/m_max). 1 = vanilla, 0.4 = 60% fewer. This is the tree tri budget — the single biggest lever on a forest zone. Pickables, ore and spawners are exempt, so berries and copper keep vanilla counts. Only affects zones generated AFTER the change; already-generated zones keep their trees.",
                    new AcceptableValueRange<float>(0.02f, 2f)));
            VegetationWarmUp = cfg.Bind(PERF, "Warm vegetation on the server", true,
                "Dedicated server only: after the world loads, make and discard one of each vegetation prefab (one a frame), so the "
                + "first tree or rock of each kind a player's exploration spawns does not stall the server (the first Beech1 cost "
                + "about 0.45 s, mostly waiting on disk). Off = vanilla behaviour.");
            BakeReadAhead = cfg.Bind(PERF, "Read the terrain cache ahead on join", true,
                "A studio-baked world's terrain cache (hundreds of MB) is read on a worker during the join and the server holds "
                + "PeerInfo until the client says it is done, so the join's main-thread freeze is seconds shorter. Per machine: on "
                + "a server it means 'wait for the client's report', on a client 'read ahead and report'. Off on both = the join "
                + "reads the cache inside PeerInfo, as before FAT 0.2.617.");
            ClutterDensityScale = cfg.Bind(PERF, "Clutter density scale", 0.55f,
                new ConfigDescription("Multiplies grass/clutter instance count per patch. 1 = vanilla, 0.5 = half the blades, 0 = none. APPLIES LIVE: changing it clears and regrows the grass around you so the slider is honest (it used to only affect patches built AFTER the change — moving it looked like it did nothing).",
                    new AcceptableValueRange<float>(0f, 1.5f)));
            GrassRenderDistance = cfg.Bind(PERF, "Grass render distance", 0f,
                new ConfigDescription("Override grass render distance in metres (ClutterSystem m_distance); grass tris scale with the SQUARE of this. 0 = keep vanilla. Try 60-80 to trim the carpet. Applies live.",
                    new AcceptableValueRange<float>(0f, 160f)));
            // LIVE RETUNE — the density lever only affects patches built
            // AFTER the value changes, so without this the slider read as
            // dead/all-or-nothing: nothing on screen moved until vanilla
            // happened to rebuild patches. ResetGrass clears every patch and
            // the spread system regrows them at the new density immediately.
            System.EventHandler clutterRetune = (_, __) =>
            {
                try
                {
                    var cam = Utils.GetMainCamera();
                    var center = cam != null ? cam.transform.position
                        : (Player.m_localPlayer != null ? Player.m_localPlayer.transform.position : UnityEngine.Vector3.zero);
                    // Big radius so the whole visible carpet regrows at the new
                    // density — the slider then reads honestly in place.
                    if (ClutterSystem.instance != null) ClutterSystem.instance.ResetGrass(center, 512f);
                }
                catch (System.Exception ex)
                { FiresLogger.LogWarning($"[FiresVoxel] clutter retune failed: {ex.Message}"); }
            };
            ClutterDensityScale.SettingChanged += clutterRetune;
            GrassRenderDistance.SettingChanged += clutterRetune;
            // 0.2.1402 (Fire 11-06: hidden-by-terrain impostors not culled, blocky up close, frame drops; "make sure they are off by default for now"):
            // default false. Same key, so a cfg that already says true keeps it; nothing is rewritten.
            ImpostorsEnabled = cfg.Bind(PERF, "Impostors enabled", false,
                new ConfigDescription("Draw far billboard trees and rocks at all. Off by default until the culling and look fix lands. Turning this OFF removes every distant billboard and the cost of baking and drawing them; the distance past where real trees render is simply empty. Applies live."));
            FarVegImpostors.Enabled = ImpostorsEnabled.Value;
            ImpostorsEnabled.SettingChanged += (_, __) =>
            {
                FarVegImpostors.Enabled = ImpostorsEnabled.Value;
                FarVegImpostors.MarkDirty();
            };

            ImpostorRadius = cfg.Bind(PERF, "Impostor radius", 2400f,
                new ConfigDescription("Far billboard trees/rocks draw radius (metres) around the player, all directions.",
                    new AcceptableValueRange<float>(600f, 6000f)));
            ImpostorForwardRadius = cfg.Bind(PERF, "Impostor forward radius", 3000f,
                new ConfigDescription("Far billboards extend to THIS radius in the direction you're looking (fades out to the sides), so you see trees/rocks up a distant mountain without paying for them behind you. Must be >= Impostor radius.",
                    new AcceptableValueRange<float>(600f, 10000f)));
            ImpostorNearSkip = cfg.Bind(PERF, "Impostor near skip", 300f,
                new ConfigDescription("Billboards start THIS far out (metres), past where the real trees render — closer impostors just hide behind the real forest and waste frames.",
                    new AcceptableValueRange<float>(96f, 800f)));
            const string BP = "Biome Paint";
            BiomePaintMapTool = cfg.Bind(BP, "Biome paint: map tool", true, "Admins get a Biome button on the large map: Shift+Alt+drag an outline or a strip, then Paint. Needs no other mod; with RPG Maker it stacks under the Territory button.");
            BiomePaintCustomBrushes = cfg.Bind(BP, "Biome paint: custom biomes on brushes", true, "The Chaos Hammer and the map tool list the biomes Expand World registered from its yamls (0.2.1373). Needs Expand World with Biome data on; with none registered nothing changes.");
            BiomePaintRegrowOnSave = cfg.Bind(BP, "Biome paint: regrow on save", true, "Painting a custom biome clears and regrows the vegetation in its area (0.2.1373). Off: paint now, reset later with fires_regen biome=.");
            BiomePaintSwapLocations = cfg.Bind(BP, "Biome paint: swap locations on save", true, "Painting a custom biome replaces the locations in its area (0.2.1373). Off: locations stay.");
            BiomePaintChangeHeight = cfg.Bind(BP, "Biome paint: change land height", true, "A custom biome's circle also turns the land to the height from its yaml, on vanilla and converted worlds (0.2.1373). Off: new paint keeps the land's shape.");
            BiomePaintBlendWidth = cfg.Bind(BP, "Biome paint: blend width", 32f, new ConfigDescription("Metres inside a painted edge over which the land blends to the new height. The server's value is used by everyone (0.2.1373).", new AcceptableValueRange<float>(0f, 96f)));
            BiomePaintWipeToggle = cfg.Bind(BP, "Biome paint: wipe builds toggle key", new BepInEx.Configuration.KeyboardShortcut(UnityEngine.KeyCode.Backslash), "With a Paint brush selected, toggles between keeping and wiping builds in the circle. Always starts on keeping; a wipe counts first and asks again (0.2.1374).");
            SnowSparkle = cfg.Bind(S, "Snow sparkle", FatSnowLookDefaults.DefaultSparkle, new ConfigDescription("How much the snow glints in the light, voxel snow and (while FAT is loaded) FTS's heightmap snow (0.2.1405; was FTS [17 - FiresHeightmap] 08 SnowSparkle, same default).", new AcceptableValueRange<float>(0f, 1f)));
            ShovelOnHeightmap = cfg.Bind(S, "Snow shovel patches on heightmap", true, "The snow shovel clears its wheel-sized patch on normal heightmap ground with snow too, as on voxel ground (0.2.1376). Off: vanilla's own op, Deep North only.");
            ImpostorSkipBehindCamera = cfg.Bind(PERF, "Impostor skip behind camera", true, "Far billboards draw only the directions the camera can see, with a wide margin (0.2.1372). Same trees in view. Off: every direction draws every frame, for comparing.");
            ImpostorSkipBehindCamera.SettingChanged += (_, __) => FarVegImpostors.MarkDirty();
            ImpostorFrustumCull = cfg.Bind(PERF, "Impostor frustum cull", true, "Far billboards also skip groups whose area is outside the camera view, with a margin for fast turns. Same trees in view, less to draw. Off: only the wider direction check runs.");
            ImpostorGpuResident = cfg.Bind(PERF, "Impostor GPU resident instances", true, "Far billboards keep their positions on the graphics card, uploaded once when they change, instead of sending every position to it each frame. Same trees, same look, less CPU each frame. Falls back to the old way on its own if the GPU cannot. Off: the old way.");
            ImpostorGpuResident.SettingChanged += (_, __) => FarVegImpostors.MarkDirty();
            ImpostorRebuildOnChange = cfg.Bind(PERF, "Impostor rebuild only on change", true, "Over the tile cap, far billboard batches rebuild only when a tile is really dropped, not every frame (0.2.1371). Same trees. Off: the old every-frame rebuild, for comparing.");
            ImpostorDensity = cfg.Bind(PERF, "Impostor density", 0.85f,
                new ConfigDescription("Scales how many billboards get placed. Lower = a thinner, scattered distant-forest feel (and cheaper); higher = continuous distant treeline coverage (distant mountains/forests read as covered, not sparse).",
                    new AcceptableValueRange<float>(0.1f, 1.5f)));

            // All four are re-read every tick, but the placed tile set is only rebuilt when the
            // player moves or turns, so a change could sit invisible until then.
            ImpostorRadius.SettingChanged += (_, __) => FarVegImpostors.MarkDirty();
            ImpostorForwardRadius.SettingChanged += (_, __) => FarVegImpostors.MarkDirty();
            ImpostorNearSkip.SettingChanged += (_, __) => FarVegImpostors.MarkDirty();
            ImpostorDensity.SettingChanged += (_, __) => FarVegImpostors.MarkDirty();

            TerrainCastShadows = cfg.Bind(PERF, "Terrain casts shadows", true,
                new ConfigDescription("Whether voxel terrain chunks cast shadows. Every live chunk is submitted to the shadow pass a SECOND time, so with several hundred chunks loaded this is a large share of draw calls and shadow submissions. Turning it OFF is one of the biggest single frame wins available; the cost is that cliffs/overhangs stop casting onto the ground below (terrain still RECEIVES shadows from trees, buildings and everything else). Applies live."));

            TerrainCastShadows.SettingChanged += (_, __) =>
            {
                try { VerdantsAscent.Terrain.Voxel.VoxelWorld.RefreshAllViewShadows(); }
                catch (System.Exception ex)
                { FiresLogger.LogWarning($"[FiresVoxel] terrain shadow refresh failed: {ex.Message}"); }
            };

            MeshAppliesPerFrame = cfg.Bind(PERF, "Mesh applies per frame", 2,
                new ConfigDescription("How many finished chunk meshes the MAIN THREAD uploads (and colliders it assigns) each frame. Higher = terrain streams in faster, but each apply is a mesh upload on the frame — too high and streaming-in hitches. A headless server renders nothing, so it uses 4x this automatically.",
                    new AcceptableValueRange<int>(1, 16)));
            MeshBufferBudget = cfg.Bind(PERF, "Mesh buffer budget", 32,
                new ConfigDescription("Hard ceiling on concurrently-live mesh buffers (~0.6MB each) — the mesh pipeline's memory cap. A worker that can't get one waits for the main thread to return a buffer instead of allocating, so pipeline RAM can never run away (this is what stopped the out-of-memory crashes). 16 measurably starved workers during fast flight (pool pinned 0, starved count climbing) — 32 keeps them meshing ahead for ~10MB more. A headless server uses 2x this automatically.",
                    new AcceptableValueRange<int>(4, 64)));

            LogTimings = cfg.Bind(S, "Log timings", false,
                "Log per-chunk seed/mesh/bake/apply milliseconds (measurement runs only — thousands of lines per session, and the logging itself costs frames).");
            // The ground probe read 0.18 m above the collider at a pad rim.
            ProbeMatchesMesh = cfg.Bind(S, "Ground probe matches the mesh", false,   // New behaviour ships OFF (team rule)
                "ON: FAT's ground height probe (placements, tools, tests) reads the same surface the terrain mesh and collider show (the Surface Nets vertices around the point). OFF (default): the old single-column read, which can sit up to ~0.2 m above the collider beside steps, slopes and pad edges.");
            WaterProbeMatchesMesh = cfg.Bind(S, "Water edges use the mesh-matched probe", false,
                "OFF (default): water sheets, ponds and the liquid bridge keep the old single-column floor read, so the approved water look does not move. ON: they use the mesh-matched probe too (shorelines can shift by up to ~0.2 m at steep banks); turn on only after a golden-shot check.");
            SwimFollowsDrawnWater = cfg.Bind(S, "Swim level follows the drawn water", false,
                "OFF (default): swimming and floating in generated rivers/lakes read the stored water columns (the old way), which can sit below the drawn water surface: by the sheet's smoothing, and by a whole reach step on a sloped river. ON: inside the water sheet they read the same smoothed heights the sheet is drawn from, so you swim AT the surface you see; at the sheet's edge the old rule stays. fat_swim_probe <x> <z> shows both levels at a point. Turn on only after a golden swim-line check.");
            WaterOnlyChunks = cfg.Bind(S, "Water-only chunks draw their sheet", false,
                "OFF (default): a 32 m terrain layer with no ground in it is not built, so where a lake is deeper than its layer (bed below it) nothing draws the water there (square dry holes in big deep lakes). ON: the layer holding a column's generated water level is built anyway (meshing empty, about 80 KB) so its water sheet draws. Applies to zones built after the change (re-enter the area). Debug line per chunk.");
            ProbeDeltaLog = cfg.Bind(S, "Ground probe delta log", false,
                "Debug: log old vs mesh-matched probe heights (one line per spot, at most 20 per minute) to measure the difference. Measurement runs only.");
            MaxLoadedChunks = cfg.Bind(S, "Max loaded chunks", 2048,
                new ConfigDescription("Safety cap on live voxel chunks (32m cubes, ~83KB each). A capped-out brush would skip chunks and leave holes, so brushes warn loudly when the cap bites.",
                    new AcceptableValueRange<int>(16, 8192)));
            // The four keys of the renamed section keep their values
            FatConfigRetire.Move(cfg, OldS, "Enabled", Enabled);
            FatConfigRetire.Move(cfg, OldS, "Show tools in build menu", ShowToolsInBuildMenu);
            FatConfigRetire.Move(cfg, OldS, "Log timings", LogTimings);
            FatConfigRetire.Move(cfg, OldS, "Max loaded chunks", MaxLoadedChunks);

            OceanGap = cfg.Bind(SW, "Ashlands and Deep North ocean gap", BcImport.FiresOceanGap.Auto,
                "The Ocean ring 1.0 carves between the main landmass and Ashlands (south) and Deep North (north), "
                + "at 12,000m.\n"
                + "• Auto — removed on a world migrated from BetterContinents, kept on every other world. "
                + "A BC map drew its own coastline out to the edge and never asked for a ring of Ocean through it, "
                + "which is why a converted world shows a band of sea across the top and bottom.\n"
                + "• Keep — never removed here. ExpandWorldData's own 'Ashlands gap' / 'Deep North gap' "
                + "settings still decide.\n"
                + "• Remove — removed on every world.\n"
                + "Works alongside ExpandWorldData rather than against it: this only ever writes 'no gap', so if "
                + "either mod removes it, it is gone, and neither unpatches the other.");

            // A config written before PlacedOnly was removed still says so; BepInEx then keeps the default (Off).
            // 0.2.1336: the removed "Terrain Mode" key stays orphaned in an old cfg; its saved value is read here and nothing writes it.
            try
            {
                if (System.IO.File.Exists(cfg.ConfigFilePath))
                    foreach (var line in System.IO.File.ReadAllLines(cfg.ConfigFilePath))
                        if (line.Trim().Replace(" ", "") == "TerrainMode=FullVoxel") { LegacyConfigFullVoxel = true; FiresLogger.LogDebug("[FiresVoxel] old cfg says Terrain Mode = FullVoxel: used only for a world with no .profile yet and the New World default"); break; }
            }
            catch (System.Exception ex) { FiresLogger.LogWarning($"[FiresVoxel] could not read the old Terrain Mode from the cfg: {ex.Message}"); }
            CustomGeneration = cfg.Bind(SW, "Fires Custom Generation", true,
                "MASTER SWITCH for all whole-world terrain replacement. ON = the World Terrain Mode + World " +
                "Generation settings below apply (voxel wrap/only + Fires worldgen). OFF = terrain runs 100% " +
                "VANILLA regardless of those two settings — no height/biome/river/mesh overrides at all — so " +
                "the mod behaves as a plain add-on whose voxel BRUSH tools still work for drawing meshes. Flip " +
                "this one toggle instead of setting both World Terrain Mode=Vanilla AND World Generation=Off.");

            WorldMode = cfg.Bind(SW, "World Terrain Mode", VoxelWorldMode.VoxelWrap,
                "Whole-world terrain system. Vanilla = untouched vanilla zones. VoxelWrap = zones near the " +
                "player are wrapped: the vanilla heightfield becomes voxel terrain (same surface, same biomes, " +
                "same locations) with deterministic procedural caves carved from it, and the vanilla heightmap " +
                "render+collider are hidden per zone. Toggling back to Vanilla unwinds live. Voxel BRUSH tools " +
                "work in both modes. Generated caves are pure functions of the world seed — they regenerate " +
                "identically; player edit deltas persist independently (P1).");
            WrapAllLoaded = cfg.Bind(SW, "Wrap All Loaded Zones", true,
                "Wrap EVERY zone as it streams in (login + exploration), ignoring Wrap Radius. Zones only " +
                "unwrap when they unload. Off = radius-gated wrapping around the player.");
            WrapRadius = cfg.Bind(SW, "Wrap Radius", 160f,
                new ConfigDescription("Zones whose center is within this many metres of the player get wrapped (Chebyshev). 160 ≈ 5x5 zones — pushes the voxel↔vanilla frontier past the visible detail range.",
                    new AcceptableValueRange<float>(48f, 320f)));
            GenerateCaves = cfg.Bind(SW, "Generate Caves", true,
                "Carve deterministic spaghetti-cave networks under the surface of wrapped zones (FullVoxel only).");
            CaveMinY = cfg.Bind(SW, "Cave Min Y", 34f,
                new ConfigDescription("No caves below this world height. Default sits just above the y=30 water table — flooded caves come with the voxel-water milestone.",
                    new AcceptableValueRange<float>(-200f, 200f)));
            CaveMaxDepth = cfg.Bind(SW, "Cave Max Depth", 48f,
                new ConfigDescription("Caves only carve within this many metres below the surface.",
                    new AcceptableValueRange<float>(8f, 128f)));
            CaveWidth = cfg.Bind(SW, "Cave Width", 0.05f,
                new ConfigDescription("Noise band width — bigger = fatter, more frequent tunnels (0.05 ≈ 4m radius).",
                    new AcceptableValueRange<float>(0.01f, 0.2f)));
            CaveFrequency = cfg.Bind(SW, "Cave Frequency", 0.025f,
                new ConfigDescription("Horizontal noise frequency — smaller = longer, straighter tunnel systems.",
                    new AcceptableValueRange<float>(0.005f, 0.1f)));
            CaveFrequencyY = cfg.Bind(SW, "Cave Frequency Y", 0.04f,
                new ConfigDescription("Vertical noise frequency — higher than horizontal keeps tunnels flat-ish and walkable.",
                    new AcceptableValueRange<float>(0.005f, 0.15f)));
            CaveSurfacePinch = cfg.Bind(SW, "Cave Surface Pinch", 6f,
                new ConfigDescription("Tunnels pinch shut within this many metres of the surface; occasional breakthroughs become natural entrances.",
                    new AcceptableValueRange<float>(1f, 24f)));
            CavernScale = cfg.Bind(SW, "Cavern Scale", 0.22f,
                new ConfigDescription("Size of the cavern octave relative to tunnel noise. SMALLER = larger, rarer chambers; larger = more frequent small rooms. This is what makes passages open out into caverns instead of running at one width forever.",
                    new AcceptableValueRange<float>(0.05f, 1f)));
            CavernRarity = cfg.Bind(SW, "Cavern Rarity", 0.62f,
                new ConfigDescription("How high the cavern octave must run before a spot becomes a chamber. HIGHER = rarer caverns. 0.62 gives an occasional room off the tunnel network.",
                    new AcceptableValueRange<float>(0.4f, 0.9f)));
            CavernBoost = cfg.Bind(SW, "Cavern Boost", 0.11f,
                new ConfigDescription("Extra tunnel width inside a chamber (same units as Cave Width). This is the difference between a corridor and a cavern you can look up in.",
                    new AcceptableValueRange<float>(0f, 0.3f)));
            EntranceRarity = cfg.Bind(SW, "Cave Entrance Rarity", 0.58f,
                new ConfigDescription("Tunnels normally pinch shut near the surface (Cave Surface Pinch) so they stay buried. Where this sparse mask runs high the pinch is released and the tunnel breaks through as a natural entrance. LOWER = more entrances.",
                    new AcceptableValueRange<float>(0.3f, 0.95f)));
            SinkholeRarity = cfg.Bind(SW, "Sinkhole Rarity", 0.72f,
                new ConfigDescription("Share of candidate sites that hold NO shaft. HIGHER = rarer: 0.72 leaves roughly one open drop per four spacing cells. These are the big open shafts you can fall into.",
                    new AcceptableValueRange<float>(0.0f, 0.98f)));
            SinkholeSpacing = cfg.Bind(SW, "Sinkhole Spacing", 120f,
                new ConfigDescription("Metres between sinkhole CANDIDATE sites. At most ONE shaft is ever cut per cell of this size, so the roof between sites is never opened — raise it for fewer, more isolated entrances.",
                    new AcceptableValueRange<float>(32f, 512f)));
            SinkholeRadius = cfg.Bind(SW, "Sinkhole Radius", 6.5f,
                new ConfigDescription("Widest carve radius at a sinkhole's centre (metres). Tapers to nothing at its rim.",
                    new AcceptableValueRange<float>(3f, 25f)));
            SinkholeDepth = cfg.Bind(SW, "Sinkhole Depth", 26f,
                new ConfigDescription("How far below the surface a sinkhole shaft reaches (metres). It must reach the tunnel layer to open into a cave system rather than being a pit.",
                    new AcceptableValueRange<float>(8f, 60f)));

            VoxelWater = cfg.Bind(SW, "Voxel Water", true,
                new ConfigDescription("CARVE + FILL in one pass: the voxel terrain emits its river/lake water sheet from the SAME reconciled per-column level the bed carve was cut for. Static mesh — no sim, no threads, no raycasts, no settle, no ZDOs; the z-buffer cuts the shoreline against the banks exactly like vanilla's ocean. Swim reads the same field, so seen == swum."));
            LegacyMeshWater = cfg.Bind(SW, "Legacy Mesh Water", false,
                new ConfigDescription("OFF (default): generated water comes from vanilla LiquidVolume pours. ON: the old per-zone meshed water field in Modules/VoxelTerrain/LegacyMesh. Two backends drawing the same rivers at once is why work kept splitting — leave this off unless comparing them."));
            WaterPaintShoreBlend = cfg.Bind(SW, "Water Paint Shore Blend", 2.5f,
                new ConfigDescription("How far the wet riverbed paint keeps fading ABOVE the waterline, in metres of RISE. The band follows the bank: broad on a shallow slope, tight on a steep one. Raise it for a wider damp margin, 0 for a hard edge at the water.",
                    new AcceptableValueRange<float>(0f, 12f)));
            WaterDiagInterval = cfg.Bind(SW, "Water Diagnostic Interval", 45f,
                new ConfigDescription("Seconds between generated-water diagnostic log lines: which gate dried how many cells, plus an automatic va_water_scan of the bodies around you. 0 = off.",
                    new AcceptableValueRange<float>(0f, 600f)));
            WaterBankFreeboard = cfg.Bind(SW, "Water Bank Freeboard", 0.45f,
                new ConfigDescription("How far BELOW the lower bank edge a river/lake surface is seated. This is the WAVE CREST BUDGET, not a cosmetic offset: the mesh is wave-displaced at render time, so a surface seated flush with the bank has its crests break over it and the water climbs the bank. Raise it if you raise wave amplitude; lower it if the water sits visibly too deep in its channel.",
                    new AcceptableValueRange<float>(0f, 2f)));
            WaterMinChannelDepth = cfg.Bind(SW, "Water Min Channel Depth", 1.5f,
                new ConfigDescription("How deep the bed is carved under the reconciled water surface. Banks NEVER move with this — only the cut deepens. The sheet/swim sit 0.4m below that surface (bank-relief seat-down), so the actual water column is this minus 0.4: at 1.5 the column is the original 1.1m. Raise for deeper rivers, banks stay put.",
                    new AcceptableValueRange<float>(0f, 4f)));
            WaterfallMist = cfg.Bind(SW, "Waterfall Mist", true,
                "Mist at every waterfall: spray off the lip, a cloud where the water lands on rock or water (with a faint rainbow when the sun is behind you), and the cloud a skyland fall turns into where it thins out in mid-air. Applies at once.");
            WaterfallMistRange = cfg.Bind(SW, "Waterfall Mist Range", DefaultWaterfallMistRange,
                new ConfigDescription("Waterfall mist runs only within this many metres of the camera. Applies at once.",
                    new AcceptableValueRange<float>(50f, 600f)));
            // 0.2.1321 (Fire x266 z1114: curtains not following an overhang): Debug-only shape probe, 0 = off.
            WaterfallShapeProbeRadius = cfg.Bind(SW, "Waterfall shape probe radius", 0f,
                new ConfigDescription("Debug only: within this many metres of you, every waterfall curtain column logs its lip, fall length and per-row height / offset / landed / measured face distance when it is shaped. 0 = off. Applies at once.",
                    new AcceptableValueRange<float>(0f, 64f)));
            WaterfallBrightness = cfg.Bind(SW, "Waterfall Brightness", 0.85f,
                new ConfigDescription("Brightness of the light falling water gives off (1 = full). Lower keeps stacked or overlapping falls from reading as solid white. Applies at once.",
                    new AcceptableValueRange<float>(0f, 2f)));
            SkyIslands = cfg.Bind(SW, "Sky Islands", true,
                "Rare floating islands, 200-300m above the local terrain: flat-ish carved top, pointed ridged underside. Pure voxel additions — the map, spawns and vanilla systems never see them. FullVoxel only. Applied when the world loads; existing chunk caches self-invalidate.");
            SkyIslandRarity = cfg.Bind(SW, "Sky Island Rarity", 0.30f,
                new ConfigDescription("Chance that each ~1.5km placement cell rolls an island. 0.30 is roughly one island per 5 square km. 0 disables placement.",
                    new AcceptableValueRange<float>(0f, 1f)));
            // 0.2.1096 (Fire: skylands lost their locations): the chance a skyland holds a location site of its biome, rolled per world seed + island.
            SkylandSiteChance = cfg.Bind(SW, "Skyland location chance", 0.7f,
                new ConfigDescription("Chance a sky island holds a location (ruins, camp, runestone) from its own biome on a normal world. Rolled per world seed and island, so every peer agrees. Islands already dressed keep what they have.",
                    new AcceptableValueRange<float>(0f, 1f)));
            SkylandSiteChanceSuperFlat = cfg.Bind(SW, "Skyland location chance on super flat", 1f,
                new ConfigDescription("The same on a super flat world, where the skylands and caves are the only places locations stand.",
                    new AcceptableValueRange<float>(0f, 1f)));
            SkylandMiniSites = cfg.Bind(SW, "Skyland mini sites",
                "Meadows:Runestone_Meadows:3:2.1,Meadows:Runestone_Boars:3:2.1,BlackForest:Runestone_BlackForest:3:2.1,Swamp:Runestone_Swamps:3:2.1,"
                + "Mountain:Waymarker01:3:1.6,Mountain:Waymarker02:3:1.9,Plains:Runestone_Plains:3:2.1,Mistlands:Runestone_Mistlands:3:2.1,AshLands:Runestone_Ashlands:3:2.1",
                "Small locations for sky islands too narrow for any of their biome's sites (Biome:Location:clear m:footprint m, comma separated). "
                + "Only worlds using the 0.2.1137 site rule. Server-owned: it decides island shapes.");
            VegWaterEcology = cfg.Bind(SW, "Vegetation Water Ecology", true,
                "Vegetation placement measures altitude against the LOCAL generated water surface instead of sea level. The whole vanilla shoreline ecology ports to rivers, lakes and swamps: trees keep their distance, vass and waterlilies stand in the shallows, swamp trees wade. Only affects zones generated after the change.");
            FaithfulBakeWater = cfg.Bind(SW, "Faithful Bake Water", false,
                "Experimental: a FAITHFUL studio bake (such as ATomTest) also builds the smooth water surface from its own baked water, so its rivers and lakes are stored, swum, carved to their channel depth and grown on like any generated world's. Off = the bake's inland water stays unqueryable (only the sea). Read when the world loads; set it the same on the server and every client.");
            VegTreeWaterClearance = cfg.Bind(SW, "Vegetation Tree Water Clearance", 1f,
                new ConfigDescription("Extra metres of min-altitude added to tree prefabs, above the local water surface, so trees rarely stand at the very edge of a bank. 0 = vanilla bands only.",
                    new AcceptableValueRange<float>(0f, 5f)));
            VegTreeNames = cfg.Bind(SW, "Vegetation Tree Names", "beech,birch,oak,firtree,pinetree,yggashoot",
                "Prefab name substrings (comma separated, case-insensitive) treated as trees for the water clearance. Swamp trees are deliberately absent — they belong in the water.");
            VegWaterSinkerNames = cfg.Bind(SW, "Vegetation Water Sinker Names", "stub,log,trunk",
                "Prefab name substrings allowed to seed INTO shallow water (min altitude lowered to -0.8m vs the local surface): stumps and logs in river beds. Rocks already carry negative bands in vanilla and need no help.");

            CaveContentEnabled = cfg.Bind(SW, "Cave Content Enabled", true,
                "Dress generated caves from each biome's tables (section 'VoxelTerrain Cave Content' and one section per biome): pickables, ceiling hangers, rubble, ore nodes, restocking chests and monster spawners. The server places it once per zone and records the zone, so it never doubles up.");
            CaveContentDensity = cfg.Bind(SW, "Cave Content Density", 1f,
                new ConfigDescription("Scales how much of everything gets placed in caves. 0 = bare caves.",
                    new AcceptableValueRange<float>(0f, 3f)));
            CaveRockScale = cfg.Bind(SW, "Cave Rock Scale", 0.45f,
                new ConfigDescription("Uniform scale applied to cave rubble. Surface rock prefabs are sized for open terrain and read far too large underground, so they are shrunk to rubble proportions.",
                    new AcceptableValueRange<float>(0.15f, 1.5f)));
            CaveContentConfig.Bind(cfg);
            TreeSeedDrops.Bind(cfg);
            TreeSaplingPrefabs.Bind(cfg);

            CaveWaterEnabled = cfg.Bind(SW, "Cave Water Enabled", true,
                "Pool water in the lowest point of large caverns. Uses the normal FiresLiquid pour, so it drapes the carved floor, spreads to the bank, and you swim in it exactly as you do in any other water. Where a passage runs below the pool level it floods naturally, which is what produces submerged routes through to dry cave beyond.");
            CaveWaterChance = cfg.Bind(SW, "Cave Water Chance", 0.35f,
                new ConfigDescription("Chance that a qualifying cavern in a zone gets a pool. At most one pool is seeded per zone (the deepest cavern floor found), so this is really 'how many of the big caverns are flooded'.",
                    new AcceptableValueRange<float>(0f, 1f)));
            CaveWaterMinHeadroom = cfg.Bind(SW, "Cave Water Min Headroom", 5.5f,
                new ConfigDescription("A cavern must have at least this much floor-to-ceiling clearance before it is considered for a pool — keeps water out of crawl-height passages.",
                    new AcceptableValueRange<float>(2.5f, 20f)));
            CaveWaterRadius = cfg.Bind(SW, "Cave Water Radius", 8f,
                new ConfigDescription("Pour radius seeded at the cavern's low point. The settle engine spreads it to the surrounding rock, so this is a starting size, not the final pool shape.",
                    new AcceptableValueRange<float>(3f, 24f)));

            CaveEnvEnabled = cfg.Bind(SW, "Cave Environment Enabled", true,
                "Force a cave environment (dark/fog) while the player stands under a VOXEL ceiling (honest up-raycast; replaces the old underside-mesh trick). Underwater env wins while swimming.");
            CaveEnvironment = cfg.Bind(SW, "Cave Environment", "Crypt",
                "EnvMan environment name forced inside voxel caves (vanilla examples: Crypt, Caves, SunkenCrypt).");

            FarQuiltEnabled = cfg.Bind(SW, "Far Quilt Enabled", true,
                "W3-A4 — draw far terrain from OUR generator (render-only tiles in the voxel terrain " +
                "material) and stop rendering vanilla distant-LOD heightmaps. Off = vanilla distant LOD " +
                "renders our heights as fallback.");
            FarQuiltNearRadius = cfg.Bind(SW, "Far Quilt Near Radius", 384f,
                new ConfigDescription("Per-zone 64m quilt tiles out to this range (they also cover zones still " +
                    "streaming in). Voxel-solid zones hide their tile automatically.",
                    new AcceptableValueRange<float>(128f, 1024f)));
            FarQuiltFarRadius = cfg.Bind(SW, "Far Quilt Far Radius", 1536f,
                new ConfigDescription("256m supertiles out to this range beyond the near ring. Ocean-floor " +
                    "supertiles that never break the waterline are skipped.",
                    new AcceptableValueRange<float>(512f, 4096f)));
            SeamLips = cfg.Bind(SW, "Seam Lips (legacy)", false,
                "Legacy zone-edge shingle lips. Superseded by canonical border ownership (rings mirror the " +
                "owning chunk, so boundary geometry is bit-identical on both sides). Leave OFF; enable only " +
                "if residual hairlines appear, and report them (va_voxel_seamscan says which planes diverge).");
            FrontierLips = cfg.Bind(SW, "Frontier Lips", false,
                "Emit shingle lips on faces with no covering neighbour (wrap frontier). They seal the " +
                "half-cell trench at the loaded-world edge but render as bright ribbons on slopes and " +
                "wherever a neighbour zone is slow to swap — leave OFF unless the frontier trench bothers " +
                "you more than the ribbons.");
            SeamAuditAuto = cfg.Bind(SW, "Seam Audit Auto", false,
                "Background MESH-truth seam sentinel: every 8s, compare the actual rendered vertex buffers " +
                "of adjacent chunks near the player and log any face with tears/holes/shading mismatches " +
                "plus full forensics. Silent when clean. va_voxel_seamaudit runs it on demand at any radius.");
            BrushDrawSpeed = cfg.Bind(SW, "Brush Draw Speed", 0.5f,
                new BepInEx.Configuration.ConfigDescription(
                    "How fast held-click brush strokes deposit/remove material (terrain AND water tools). " +
                    "1 = full speed (the old behavior that got away from you), lower = proportionally fewer " +
                    "dabs per frame for precise work. Also settable in-game via va_brush_drawspeed.",
                    new BepInEx.Configuration.AcceptableValueRange<float>(0.05f, 1f)));
            VerdantsAscent.Terrain.Persistence.TileBrushModes.DrawSpeed = BrushDrawSpeed.Value;
            BrushDrawSpeed.SettingChanged += (_, __) =>
                VerdantsAscent.Terrain.Persistence.TileBrushModes.DrawSpeed = BrushDrawSpeed.Value;
            WorldGenMode = cfg.Bind(SW, "World Generation", FiresWorldGenMode.Auto,
                "W4 — the Fires base worldgen (FBM continents + ridged ranges + droplet erosion replacing " +
                "WorldGenerator.GetBaseHeight; vanilla river carving off). Auto = only worlds whose NAME starts " +
                "with 'Fires' (deterministic on every peer, no sync needed — server AND clients must both run " +
                "this mod). ForceOn = every non-menu world (NEVER on a world with existing builds — terrain " +
                "shifts wholesale). Off = vanilla generation everywhere.");
            WorldGenResolution = cfg.Bind(SW, "WorldGen Resolution", 10501,
                new ConfigDescription("Base heightfield grid size (cells per side over the 21km world). 10501 = 2m " +
                    "cells — fine enough to carve 2m streams and 4-10m rivers NATIVELY (the whole point: the old " +
                    "2048/10m grid could not represent water narrower than a cell). The hydrology is resolution-" +
                    "independent, so this only changes fidelity + first-gen time (~2.5min at 2m, cached forever after). " +
                    "MUST match on server and all clients (they each generate the world deterministically). Changing " +
                    "this on an existing Fires world regenerates a DIFFERENT cache — leave it alone once a world exists. " +
                    "21001 = 1m cells, matching vanilla's own heightmap spacing (the finest that is meaningful), but the " +
                    "grid is one resident array PER FIELD for the whole world, so 1m costs ~840MB resident and ~1.7GB " +
                    "as the float array it generates from, per field. The load log prints the exact cost.",
                    new AcceptableValueRange<int>(512, 21001)));
            WorldGenDroplets = cfg.Bind(SW, "WorldGen Erosion Droplets", 400000,
                new ConfigDescription("Droplet-erosion budget carving dendritic valley networks into the base grid at " +
                    "first generation, expressed at the 2048 REFERENCE grid — the engine scales it with the cell count " +
                    "so erosion DENSITY stays constant at any resolution (400000 → ~10.5M droplets at 2m). More = " +
                    "deeper filigree, longer first load. Cached after first build.",
                    new AcceptableValueRange<int>(0, 8000000)));
            WorldGenVahcBase = cfg.Bind(SW, "WorldGen VAHC Base", "",
                "Optional EXACT base map: filename or full path of a VAHC v2 heightcache (.bin). Relative names " +
                "resolve against config/FiresRPGmaker/Debug/Cache (the FUC one-root cache). When set, this map " +
                "replaces the random generator (erosion droplets still apply once). The file must exist on the " +
                "SERVER and EVERY CLIENT before the world's first load, or peers desync. Empty = the Fires random generator.");
            WorldGenImageBase = cfg.Bind(SW, "WorldGen Image Base", "",
                "Greyscale heightmap PNG (16-bit preferred, BC convention) used as the EXACT base map. Relative " +
                "paths resolve against config/FiresRPGmaker/Debug/Cache. Erosion + river carve still apply — paint " +
                "the macro design, rivers are derived. Must exist on SERVER and EVERY CLIENT before first load. " +
                "Takes precedence over VAHC Base.");
            WorldGenImageMinY = cfg.Bind(SW, "WorldGen Image Min Y", -40f,
                "World height (metres) for image BLACK. Water level is 30.");
            WorldGenImageMaxY = cfg.Bind(SW, "WorldGen Image Max Y", 200f,
                "World height (metres) for image WHITE.");
            WorldGenBiomeImage = cfg.Bind(SW, "WorldGen Biome Image", "",
                "Biome-color PNG (BetterContinents palette: Meadows 00FF00, BlackForest 007F00, Swamp 7F7F00, " +
                "Mountain FFFFFF, Plains FFFF00, Mistlands 7F7F7F, AshLands FF0000, DeepNorth 00FFFF, Ocean " +
                "0000FF). BLACK keeps the terrain-derived biome — paint only where you want control. Relative " +
                "paths resolve against config/FiresRPGmaker/Debug/Cache; needed on server AND all clients.");
            HydrologyEnabled = cfg.Bind(SW, "Hydrology Enabled", true,
                "W2 — bake world hydrology at login (flow accumulation over the world heightfield) and CARVE " +
                "river beds into voxel terrain (any Terrain Mode but Off: rivers convert only the zones they run through). Deterministic from the world seed.");
            HydrologyPlaceWater = cfg.Bind(SW, "Hydrology Place Water", true,
                "Auto-place water along carved rivers (any Terrain Mode but Off): bank-following SPRING pours at river " +
                "headwaters and settling water volumes on ponds. Idempotent (skips where a pour already exists).");
            RiverThreshold = cfg.Bind(SW, "River Threshold", 90f,
                new ConfigDescription("Catchment (hydrology CELLS, 400m each) required before a river carves. Lower = more/denser streams. 260 only qualified the handful of valleys vanilla had already cut, which is why a converted world got no new rivers.",
                    new AcceptableValueRange<float>(40f, 4000f)));
            RiverMaxDepth = cfg.Bind(SW, "River Max Depth", 6f,
                new ConfigDescription("Deepest bed carve (metres) for the largest rivers.",
                    new AcceptableValueRange<float>(1.5f, 16f)));
            SpringSpacing = cfg.Bind(SW, "Spring Spacing", 140f,
                new ConfigDescription("Minimum metres between auto-placed springs.",
                    new AcceptableValueRange<float>(48f, 512f)));

            RiverWidthMin = cfg.Bind(SW, "River Width Min", 5f,
                new ConfigDescription("Narrowest channel width (metres), at the river threshold — headwater streams.",
                    new AcceptableValueRange<float>(2f, 20f)));
            RiverWidthMax = cfg.Bind(SW, "River Width Max", 18f,
                new ConfigDescription("Widest channel width (metres), for the largest rivers.",
                    new AcceptableValueRange<float>(6f, 60f)));
            RiverFreeboard = cfg.Bind(SW, "River Freeboard", 0.5f,
                new ConfigDescription("Metres the water surface sits below the original ground, so the banks stay visible above the waterline.",
                    new AcceptableValueRange<float>(0f, 3f)));
            BankRelief = cfg.Bind(SW, "Coarse Grid Bank Relief", 0.4f,
                new ConfigDescription("EXTRA metres the generated water surface sits below the level solved from the world grid, ON CONVERTED WORLDS ONLY. That solve runs at 20m while the ground it has to sit in is 1m, so the level it computes can land slightly ABOVE the real bank and the water pokes over the lip. This buys the inaccuracy back as visible bank. Applied at the source, so the carve, the sheet, the shore paint and the swim surface all agree.",
                    new AcceptableValueRange<float>(0f, 2f)));
            RiverMaxNewCut = cfg.Bind(SW, "River Max New Cut", 6f,
                new ConfigDescription("Deepest cut (metres) below the ORIGINAL ground the carve may take to push a channel over dry ground. A reach needing more ends there as a sink instead of trenching through a ridge.",
                    new AcceptableValueRange<float>(1f, 16f)));
            RiverMeander = cfg.Bind(SW, "River Meander", 6f,
                new ConfigDescription("Lateral meander amplitude (metres) along a channel. 0 = follow the solved path exactly.",
                    new AcceptableValueRange<float>(0f, 20f)));
            EstuaryLength = cfg.Bind(SW, "Estuary Length", 80f,
                new ConfigDescription("Length (metres) of the flare where a river meets the ocean; the channel grades to the sea plane over this run.",
                    new AcceptableValueRange<float>(0f, 300f)));
            EstuaryFlare = cfg.Bind(SW, "Estuary Flare", 2.5f,
                new ConfigDescription("Channel width multiplier at a river mouth.",
                    new AcceptableValueRange<float>(1f, 6f)));
        }
    }
}
