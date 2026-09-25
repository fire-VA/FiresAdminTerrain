namespace VerdantsAscent.Terrain.Voxel
{
    // ============================================================================
    //  WATER RULES — the single authority for every water-shape number.
    // ============================================================================
    //  Doctrine (user): water is defined per (water TYPE x BIOME). There is NO
    //  generic wet/dry pass tuned across all water at once — that is exactly what
    //  makes "fix one type, break another". Each type owns its own numbers here,
    //  so a change to (say) highland-mountain rivers moves ONLY high/steep river
    //  cells and can never bleed into lowland rivers or swamps.
    //
    //  Keying:
    //   - Biome-aware water (swamp/lake/pond/tarn/stream/basin) keys on the biome
    //     CODE (0 Meadows, 1 Forest, 2 Swamp, 3 Mountain, 4 Plains,
    //     5 Rainforest, 6 Wastelands, 7 Frostreach, 8 Ocean) — the byte stored in
    //     _biomeMap. Mountain (3) and Frostreach (7) are SEPARATE rows even where
    //     equal today, so one can be tuned without moving the other.
    //   - RIVERS are carved BEFORE biomes exist, so they key on the height/slope
    //     already computed at the thalweg (alt = g - SEA, slope = local relief).
    //     "Lowland" vs "highland/mountain" river is a SMOOTH blend on alt/slope,
    //     never a hard band — a band edge would step the channel and re-create the
    //     bank seam the user hates.
    //
    //  Rule: coincidental EQUAL literals across types get SEPARATE fields here, so
    //  tuning one never drags another with it.
    //
    //  Each field is extracted value-for-value from the old hardcoded constant;
    //  the headless FIRES_BAKE_FLOAT bake stays byte-identical (final.bin hash
    //  unchanged) across every extraction until a value is deliberately changed.
    // ============================================================================
    public static partial class FiresWorldGenBase
    {
        public static class WaterRules
        {
            // ── Biome BODY style — how a still-water body is BUILT per biome ─────
            // (user doctrine): excavation depths, berm crest height, and the shore
            // paint's reach/rise/weight + beach readiness. Indexed by biome code.
            // Read by the lake/pond/tarn seeders and the basin-normalize berm.
            public struct FWaterStyle
            {
                public float PondDepth;   // bowl centre dig for pond-size bodies (m, on top of the per-type base)
                public float LakeDepth;   // bowl centre dig for lake-size bodies
                public float BermH;       // berm crest height above the water level
                public float ShoreReach;  // paint horizontal falloff (m)
                public float ShoreRise;   // paint vertical rise cap (m)
                public float PaintWeight; // paint strength 0..1
                public float BeachBias;   // 0..1 — how readily river beaches form
            }
            static readonly FWaterStyle[] WATER_STYLES =
            {
                new FWaterStyle { PondDepth = 3.2f, LakeDepth = 4.6f, BermH = 0.9f, ShoreReach = 6f,  ShoreRise = 1.8f, PaintWeight = 1.0f,  BeachBias = 0.7f },  // Meadows
                new FWaterStyle { PondDepth = 3.2f, LakeDepth = 4.6f, BermH = 0.9f, ShoreReach = 5.5f, ShoreRise = 1.6f, PaintWeight = 0.9f, BeachBias = 0.5f },  // Forest
                new FWaterStyle { PondDepth = 1.6f, LakeDepth = 2.6f, BermH = 0.6f, ShoreReach = 5f,  ShoreRise = 1.0f, PaintWeight = 0.75f, BeachBias = 0.15f }, // Swamp — murky, shallow
                new FWaterStyle { PondDepth = 3.8f, LakeDepth = 5.2f, BermH = 0.9f, ShoreReach = 4.5f, ShoreRise = 1.2f, PaintWeight = 0f,   BeachBias = 0f },    // Mountain — snow shores STAY SNOW, zero paint
                new FWaterStyle { PondDepth = 3.0f, LakeDepth = 4.4f, BermH = 0.8f, ShoreReach = 7f,  ShoreRise = 1.8f, PaintWeight = 1.0f,  BeachBias = 0.8f },  // Plains — broad beaches
                new FWaterStyle { PondDepth = 3.4f, LakeDepth = 4.8f, BermH = 0.9f, ShoreReach = 5f,  ShoreRise = 1.5f, PaintWeight = 0.7f,  BeachBias = 0.3f },  // Rainforest
                new FWaterStyle { PondDepth = 2.6f, LakeDepth = 4.0f, BermH = 0.8f, ShoreReach = 6f,  ShoreRise = 1.5f, PaintWeight = 0.8f,  BeachBias = 0.4f },  // Wastelands
                new FWaterStyle { PondDepth = 3.8f, LakeDepth = 5.2f, BermH = 0.9f, ShoreReach = 4.5f, ShoreRise = 1.2f, PaintWeight = 0f,   BeachBias = 0f },    // Frostreach — snow shores stay snow
                new FWaterStyle { PondDepth = 0f,   LakeDepth = 0f,   BermH = 0f,   ShoreReach = 0f,  ShoreRise = 0f,   PaintWeight = 0f,    BeachBias = 0f },    // Ocean — sea rules own it
            };
            public static FWaterStyle WaterStyleFor(byte bio)
                => WATER_STYLES[bio < WATER_STYLES.Length ? bio : 0];

            // ── TARN (mountain/frostreach cirque bowls) ─────────────────────────
            // SeedMountainTarns. Small still bowls dug into cirques on biome 3/7.
            // Depth per biome comes from FWaterStyle.PondDepth (rows 3 & 7); these
            // are the tarn-shape numbers shared across the pass.
            public struct TarnRule
            {
                public float LatticeStep; // candidate-site sampling grid (m)
                public float KeepChance;  // fraction of lattice points kept (0..1)
                public float MinRadius;   // bowl radius floor (m)
                public float RadiusRange; // + hash*this (m) → radius spread
                public float CirqueGate;  // require the floor this far below the lowest rim (m) to accept a bowl
                public float LevelDrop;   // water sits this far below the lowest rim (m) — keeps every bank proud
                public float BaseDepth;   // bowl base dig, on top of the biome PondDepth (m)
            }
            public static TarnRule Tarn = new TarnRule
            {
                LatticeStep = 420f, KeepChance = 0.5f, MinRadius = 6f, RadiusRange = 12f,
                CirqueGate = 1.0f, LevelDrop = 0.85f, BaseDepth = 1.2f,   // (v105) LevelDrop 0.35→0.85: tarns join the unified body bank relief (−0.5, user-measured junction offset)
            };

            // ── LAKE / POND (lowland basins) ────────────────────────────────────
            // SeedLowlandLakes / TrySeedLakeSite. Meadows/Forest/Plains/Rainforest.
            // Depth/berm per biome come from FWaterStyle (Lake/PondDepth, BermH);
            // these are the lake-shape numbers. NOTE: BaseDepth/LevelDrop are their
            // OWN fields (equal to the tarn values today, but independently tunable).
            public struct LakeRule
            {
                public float BigChance;        // chance a site is lake-size vs pond-size (0..1)
                public float BigMinRadius;     // lake radius floor (m)
                public float BigRadiusRange;   // + hash*this (m)
                public float PondMinRadius;    // pond radius floor (m)
                public float PondRadiusRange;  // + hash*this (m)
                public float SiteSlopeMax;     // reject sites steeper than this (survey mean slope)
                public float RingRejectRelief; // reject cliff-straddling ring: ringMax-ringMin > this (m)
                public float BasinGate;        // require the floor this far below the lowest rim (m)
                public float LevelDrop;        // water sits this far below the lowest rim (m)
                public float BaseDepth;        // bowl base dig, on top of the biome Lake/PondDepth (m)
                public float WedgeGrade;       // berm wedge outward slope (m/m)
            }
            public static LakeRule Lake = new LakeRule
            {
                BigChance = 0.12f, BigMinRadius = 25f, BigRadiusRange = 20f,
                PondMinRadius = 7f, PondRadiusRange = 13f, SiteSlopeMax = 0.45f,
                RingRejectRelief = 7f, BasinGate = 1.5f, LevelDrop = 0.35f,
                BaseDepth = 1.2f, WedgeGrade = 0.32f,
            };

            // ── SWAMP / MARSH (biome 2 contained basin) ─────────────────────────
            // SeedSwampPonds — a raised outer bank sloping gently into the basin and
            // easing out into surrounding terrain, a flat inland sheet, 50/50
            // island/murk interior. The flood LEVEL is FiresGenTuning.SwampLevelPct
            // (a live studio dial), so it is not duplicated here; coastal bed offsets
            // stay inline for now.
            public struct SwampRule
            {
                public float Freeboard;        // bank crest stands this far above the sheet (m)
                public float InGrade;          // inner slope crest→basin (m/m) — gentle down into the marsh
                public float OutGrade;         // outer slope crest→natural (m/m) — eases into surrounding land
                public float BankReachM;       // outer-ramp band width (m)
                public float CoastalCutoff;    // level ≤ SEA+this → coastal (dig below sea, ocean fills; no berm)
                public float IslandNoiseScale; // island/murk coherent-noise scale (Fbm freq = 1/this)
                public float IslandFrac;       // isn above this = island tussock (higher = fewer, water-dominant); below = murk
                public float IslandRise;       // inland island base rise above the sheet (m)
                public float IslandAmp;        // inland island height amplitude (m)
                public float MurkDrop;         // inland murk base drop below the sheet (m)
                public float MurkAmp;          // inland murk depth amplitude (m)
            }
            public static SwampRule Swamp = new SwampRule
            {
                Freeboard = 2.5f, InGrade = 0.18f, OutGrade = 0.10f, BankReachM = 80f,
                CoastalCutoff = 4f, IslandNoiseScale = 18f, IslandFrac = 0.58f,
                IslandRise = 0.5f, IslandAmp = 3.0f, MurkDrop = 0.6f, MurkAmp = 2.2f,
            };

            // ── STREAM (mountain/frostreach headwater runoff) ───────────────────
            // SeedMountainStreams seeder-level levers. The channel stamp helpers
            // (StampChannelCell/StampRunoffCell) are SHARED with spillways and lake
            // outlets, so their internal bed/bank constants stay inline (not here).
            public struct StreamRule
            {
                public float LatticeStep;     // candidate-site sampling grid (m)
                public float KeepChance;      // fraction of lattice points kept (0..1)
                public float HeadwaterMinY;   // sources live above this height (m)
                public int   MinPathCells;    // discard traces shorter than this
                public float MeanderChance;   // chance to take the 2nd-lowest exit (wander)
                public float SourceLevelDrop; // start level = source-min − this (m)
                public float RavineDepthMin;  // gully depth floor (m)
                public float RavineDepthMax;  // gully depth grows to +this downstream (m)
                public float RavineDepthRate; // gully depth gain per path cell (m/cell)
            }
            public static StreamRule Stream = new StreamRule
            {
                LatticeStep = 280f, KeepChance = 0.55f, HeadwaterMinY = 85f,
                MinPathCells = 30, MeanderChance = 0.35f, SourceLevelDrop = 1.6f,
                RavineDepthMin = 1.8f, RavineDepthMax = 2.6f, RavineDepthRate = 0.012f,
            };

            // ── RIVER — PROCEDURAL (CarveRivers) ────────────────────────────────
            // Rivers are carved BEFORE biomes, so they key on the height/slope at the
            // thalweg. The MOUNTAIN-INCISION term IS the "highland river" control: it
            // adds depth as a SMOOTH function of alt (clamp01(alt/IncisionAltRef)) and
            // slope, so raising IncisionAltGain/IncisionSlopeGain deepens high/steep
            // rivers and leaves lowland (alt≈0 → term≈0) untouched. That is the
            // "tune highland rivers without touching lowland" lever, with no hard band.
            public struct RiverProfile
            {
                // width = clamp(WidthK * area^WidthP, WidthMin, WidthMax)
                public float AreaOnset, WidthK, WidthP, WidthMin, WidthMax;
                public float Freeboard;                         // level = traced surface − this (m)
                public float DepthMin, DepthMax, DepthCap;      // width→depth remap, capped after incision (m)
                public float IncisionAltRef, IncisionAltGain;   // +clamp01(alt/Ref)*Gain (m) — the highland depth
                public float IncisionSlopeRef, IncisionSlopeGain; // +clamp01(slope/Ref)*Gain (m) — steep-ground depth
                public float Incline;                           // valley-wall grade away from the channel (m/m) — MIDPOINT / imports
                public float SteepNarrow, SteepWide;            // (Phase 2) width→ChannelShape bank slope (m/m): brook = tight slot, trunk = broad canyon
                public float ValleyShaveMin, ValleyShaveMax;    // (Phase 2) width→max metres the valley pass may shave off one cell; THE canyon-depth budget
                public float IncisionBudgetMin, IncisionBudgetMax; // (Phase 2) width→how far below its own ground a reach may cut (m); replaces the flat +12
                // ── VALLEY FALLOFF, DECOUPLED FROM CHANNEL WIDTH (2026-08-13) ──────────────────────────────
                // UE's water bodies keep the landscape-blend distance as its OWN parameter (Falloff / Curve
                // Remap Width), separate from the water width, precisely because the two are not the same
                // quantity: the water is as wide as the discharge makes it, the VALLEY around it is as wide as
                // the river's history made it. We had a single global Incline, so a 2m brook and a 20m trunk
                // got an identical valley — which is why small streams read as slots cut into the ground
                // rather than as creeks with banks.
                // Nature scales the two together: meander belt / floodplain width runs roughly 10-14x bankfull
                // width (Leopold), so valley half-width Wv ≈ k·width. For a valley of a given relief H the wall
                // grade is H/Wv, i.e. INVERSELY proportional to channel width — wide rivers get gentle broad
                // floodplains, brooks get tight steep banks. Interpolated over the same WidthMin..WidthMax
                // range the rest of the channel family uses, with the old 0.45 sitting near the midpoint so
                // mid-size rivers are essentially unchanged.
                public float InclineNarrow, InclineWide;         // valley grade at WidthMin / at WidthMax (m/m)
            }
            public static RiverProfile RiverProc = new RiverProfile
            {
                // ── CHANNEL SIZE (2026-08-11, user spec: "rivers can be 10-20m wide in our world, streams will
                // be 10 and under … rivers should never be super shallow") ──────────────────────────────────
                // Calibrated against USGS regional curves (Michigan: W_ft = 8.19·DA_mi²^0.44) and then
                // DELIBERATELY exaggerated, because the honest number is unflattering: this world is 21×21 km,
                // so its biggest realistic catchment (~30-50 km²) would build a 7-9 m creek, not a river. We
                // scale up on purpose — a game world reads at human scale — and record that it IS a scaling
                // choice, not physics. WidthP stays 0.36 so the discharge relationship still shapes the family.
                //   K 0.042 @ P 0.36 →  onset 0.06 km² ≈ 2 m (brook) · 4 km² ≈ 10 m (river threshold)
                //                       · 30 km² ≈ 20 m (cap, the world's trunks)
                // DEPTH: the old law was RemapClamp(width, 3→22, 1.5→8), i.e. a 3 m brook got 1.5 m and a 22 m
                // river got 8 m — a width/depth ratio of 2-3 on EVERY channel. Real channels run w/d 10-40
                // (Rosgen's boundary is 12; >25 is "wide and shallow"), so ours were specced as slot canyons.
                // The remap endpoints now put a 2 m brook at 0.6 m and a 20 m river at ~2.9 m → w/d ≈ 3-7 at
                // the THALWEG (max depth, not mean), which is deeper than nature but honest about why: the
                // user's hard requirement is that a river never reads as a shallow smear, and a thalweg that
                // deep also guarantees the sheet always sits well inside its carve.
                // ═══ …EXCEPT IT DID NOT, AND 0.6/2.9 IS WHY (2026-08-19) ═════════════════════════════════
                // (user: "if the rivers carve deep enough then we can afford to lay the mesh at a steeper
                // angle where we need to by dropping it down into the carve further to avoid exposing the
                // edge of the meshes"; "as long as the river channel is deep enough we can smooth our ribbon
                // as needed without exposing the edges"; "rivers at 1.6 m deep probably aren't enough to
                // handle steeper descents".)
                // The claim above — "a thalweg that deep guarantees the sheet always sits well inside its
                // carve" — is the one thing this depth budget does NOT buy. MEASURED with `riverfield` on
                // seed 126369233 at n=10501: water depth p50 1.61 m, p90 3.14 m. There is no room in that to
                // drop a cross-section, so every time the ribbon could not be contained the generator had to
                // solve it by RAISING TERRAIN instead — the burial rim, which was firing on 58 % of every
                // riverbank in the world and is what reads as a mounded puddle.
                // The relationship is: the bed is cut FROM the level (`profile = wl − depth·ChannelShape`),
                // so the depth budget IS the headroom the level has to move down into. 0.6 m of headroom on a
                // brook is none. Restore roughly the old endpoints the note describes as "specced as slot
                // canyons" — but deliberately, and for a stated reason: w/d 10-40 is right for a river you are
                // looking at from the bank, and wrong for one whose surface must stay buried on a descent.
                // The w/d critique stands on its own terms; it was just answered with the wrong variable,
                // because a shallow channel does not make the river look wider, it makes the sheet pop out.
                AreaOnset = 60000f, WidthK = 0.042f, WidthP = 0.36f, WidthMin = 2f, WidthMax = 20f,
                Freeboard = 0.9f, DepthMin = 1.5f, DepthMax = 6.0f, DepthCap = 12f,
                IncisionAltRef = 120f, IncisionAltGain = 2.5f, IncisionSlopeRef = 2.4f, IncisionSlopeGain = 2f,
                Incline = 0.45f,
                // 0.75 at a 2m brook (≈37° — a tight incised bank you scramble down), 0.22 at a 20m river
                // (≈12° — a broad floodplain you walk out onto). Valley extent moves roughly 0.5x-2x versus
                // the old flat 0.45, so this is a bounded reshaping, not a new excavation budget.
                InclineNarrow = 0.75f, InclineWide = 0.22f,
                // ── PHASE 2 (2026-08-15): THE WALL CUT IS BUDGETED BY DISCHARGE ────────────────────────────
                // Both of these were effectively CONSTANTS, which is why a 2 m brook sawed through a ridge
                // exactly as readily as a 20 m trunk (docs/GORGES_AND_CAVES.md Phase 2).
                //
                // SteepNarrow/Wide replace `steep = RemapClamp(hOrig[i2], 3, 20, 2.9, 4.4)`. That ramp was
                // DEAD: it saturates at 20 m elevation while SEA is 30, so every land cell in the world took
                // the 4.4 endpoint. It was also read per-CELL, so one hydraulic cross-section could take
                // different wall angles on its two banks — the same inconsistency the incision bound was
                // fixed for in 2026-08-13 ("a cross-section is ONE hydraulic object"). Now read at the seed,
                // off width, like depth/level/bound: a brook cuts a TIGHT SLOT (steep walls, little material
                // moved), a trunk opens a BROADER canyon. m/m, so 5.0 ~ 79 deg and 3.2 ~ 73 deg.
                SteepNarrow = 5.0f, SteepWide = 3.2f,
                // IncisionBudget replaces the flat `+12f` in boundLo. Same idea, on the depth axis: how far
                // below its own natural ground a reach may cut. The trunk endpoint is left at 12 so the big
                // rivers that the 12 was chosen for (2026-08-11, "let it dig deeper than 4m - give it 12m")
                // are bit-identical; only small channels tighten.
                // ⚠ This one CAN dry a reach — boundLo holding the bed up inside the wetted width is exactly
                // the documented dry-break mechanism (s_boundHits / s_dryCore, [FiresWaterDiag] CARVE
                // DRY-BREAK). Baseline before this change: dryCore = 0 on all of 777/12345/220092304. That
                // must stay 0; if it does not, raise IncisionBudgetMin.
                IncisionBudgetMin = 6f, IncisionBudgetMax = 12f,
                // THE canyon-depth budget. The valley shave is the pass that actually digs gorges (measured
                // per-pass: Conform + tarns cut nothing past 3.5 m; InciseRivers owns every >20 m cell and the
                // 53.6 m max). Its grade was already discharge-keyed via InclineFor; its DEPTH cap was a flat
                // 24 m, so a 2 m brook could take 24 m off a ridge exactly like a 20 m trunk. Trunk endpoint
                // stays 24 m - the big rivers keep the canyons they have earned.
                ValleyShaveMin = 8f, ValleyShaveMax = 24f,
            };

            // ── RIVER — IMPORT (LoadGaeaBase / DEM) ─────────────────────────────
            // Same idea, different formula + slope UNIT (relief/ReliefRef on the source
            // grid, not slope/cellM). SEPARATE instance so tuning imports never moves
            // procedural rivers. channel = clamp(Base + clamp01(alt/AltRef)*AltGain +
            // clamp01(relief/ReliefRef)*ReliefGain, ChannelMin, ChannelMax).
            public struct RiverImportProfile
            {
                public float ChannelBase, AltRef, AltGain, ReliefRef, ReliefGain, ChannelMin, ChannelMax;
                public float MaxCutBase, MaxCutAdd; // maxCut = max(MaxCutBase, channel + MaxCutAdd)
                public float RiverDepth;            // min sink of the ribbon below its level (m)
            }
            public static RiverImportProfile RiverImport = new RiverImportProfile
            {
                ChannelBase = 0.5f, AltRef = 120f, AltGain = 2.5f, ReliefRef = 12f, ReliefGain = 2f,
                ChannelMin = 0.5f, ChannelMax = 5f, MaxCutBase = 8f, MaxCutAdd = 6f, RiverDepth = 1.5f,
            };
        }
    }
}
