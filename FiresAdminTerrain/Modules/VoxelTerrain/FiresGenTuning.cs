namespace VerdantsAscent.Terrain.Voxel
{
    // LIVE WORLDGEN DIALS. Every default here reproduces the shipped hardcoded
    // constant exactly, so the game is unchanged. The 3D studio overrides these
    // per /gen request so biomes + features can be dialed in without rebuilding
    // a game world; once a value reads right in the studio it's baked back into
    // this file (and stays the game's default). Read by BuildBiomes today; the
    // feature seeders join as we surface them.
    public static class FiresGenTuning
    {
        // ── Biome gates (BuildBiomes), checked in priority order ────────────
        public static float DeepNorthTemp      = -8f;    // tC below this + far north = DeepNorth
        // MOUNTAIN IS ELEVATION, NOT TEMPERATURE (ported from the sandbox's
        // MOUNT_START). Gating it on tC meant the −0.26/m lapse made every high
        // cell cold, and high ground is exactly where the rain shadow puts the
        // DRY land — so Mountain+DeepNorth ate the entire dry end of the
        // moisture percentile before Plains or Meadows were ever tested, and
        // both came out 0.0% (2026-09-19 log: no StartTemple, no Eikthyrnir,
        // and 1.0's AltBiomeWorldData then threw on the empty Meadows list).
        // Elevation also happens to be what a Valheim mountain actually is.
        public static float MountainStartM     = 200f;   // metres above sea where Mountain begins
        public static float MountainSnowTemp   = -12f;   // secondary polar snowline (well below the old -1)
        // SWAMP IS A PROGRESSION GATE, NOT SCENERY — iron only comes from crypts,
        // so a world with 1.5% swamp is unplayable however plausible it looks.
        // Four gates multiplied together is what starved it: district noise AND
        // near-flat AND moist AND low. Vanilla asks for noise > 0.60, a radius
        // band, and a height band — no slope and no moisture term at all.
        // These were first opened right up against a moisture field that was
        // BROKEN (raw median 0.000, so the moisture gate passed everything and
        // the noise gate carried the whole decision). With moisture fixed to a
        // real land-area percentile the same dials took 23.8% of the land, so
        // they are re-fitted here against the corrected field: noise back near
        // vanilla's 0.60, moisture at the wetter half, altitude tightened.
        public static float SwampNoiseGate     = 0.50f;  // district-noise threshold for swamp country
        public static float SwampSlopeMax      = 0.16f;  // swamps on gently sloping lowland, not only dead flat
        public static float SwampMoistMin      = 0.50f;  // …and damp rather than only the wettest
        public static float SwampAltMax        = 50f;    // …and low-ish (metres above sea)
        public static float MistEncMin         = 0.80f;  // Mistlands = enclosed valleys
        public static float MistMoistMin       = 0.45f;
        public static float MistRadius         = 5200f;  // …in the outer world
        public static float PlainsSlopeMax     = 0.15f;  // Plains roll a little
        // Moisture is a land-area PERCENTILE, so these read directly as shares
        // of the land: Plains = the driest 30%, Meadows = 0.30-0.56, BlackForest
        // = the wettest 44% — minus whatever Mountain/DeepNorth/Swamp/Mistlands
        // claim first. Sandbox bands (FiresBiome3D), which is where the 0.30 and
        // 0.56 numbers come from.
        public static float PlainsDryMax       = 0.30f;  // dry-flat → Plains
        public static float BlackForestMoistMin = 0.56f; // moist tail = BlackForest; else Meadows

        // ── Features ────────────────────────────────────────────────────────
        public static float ErosionDepthCap  = 6f;    // Erode: max carve below the pre-erosion surface (no bottomless holes)
        public static float SwampLevelPct     = 0.15f; // SeedSwampPonds: rim percentile for the flood level (higher=wetter)
        public static float SwampIslandGate   = 0.58f; // island noise gate (LOWER = more/bigger islands = less water)
        public static float SwampFeather      = 10f;   // rim feather metres (bigger=gentler swamp banks)
        public static float BankBuildCap      = 5f;    // bank enforcement: max bank raised to contain water before it retreats
        public static float LakeMaxDepth      = 6f;    // NormalizeBasinWater basin depth cap

        // ── Mountains (BuildGrid crest field) — natural massifs, not spikes ─────
        // The crest lift is a Gaussian cross-section on the range spine. Old
        // constants threw isolated 240m summit cones straight out of the ~66m
        // uplands (no foot). New defaults broaden the ridge, lower+widen the
        // summits, and add a wide low apron so massifs climb out of the plains
        // gradually and taper their flanks back to level ground.
        public static float MountainRidgeWidth    = 470f;   // crest cross-section width m (bigger = broader ranges). was 390
        public static float MountainCrestAmp      = 200f;   // base ridge lift m along the crest
        public static float MountainSummitAmp     = 150f;   // extra summit lift m (LOWER = less spiky). was 240
        public static float MountainSummitGate    = 0.62f;  // summit noise low threshold (LOWER = broader summits, fewer isolated cones). was 0.72
        public static float MountainApronAmp      = 55f;    // broad foot-apron rise m so massifs taper to level ground (0 = old sharp foot)
        public static float MountainApronWidthMul = 3.2f;   // apron width as a multiple of ridge width
        public static float MountainStructAmp     = 20f;    // ridged crest/spur detail amp m (LOWER = smoother slopes). was 24

        // Apply a name→value override (from the studio). Unknown names ignored.
        public static void Set(string name, float v)
        {
            switch (name)
            {
                case "ErosionDepthCap": ErosionDepthCap = v; break;
                case "SwampLevelPct": SwampLevelPct = v; break;
                case "SwampIslandGate": SwampIslandGate = v; break;
                case "SwampFeather": SwampFeather = v; break;
                case "BankBuildCap": BankBuildCap = v; break;
                case "LakeMaxDepth": LakeMaxDepth = v; break;
                case "DeepNorthTemp": DeepNorthTemp = v; break;
                case "MountainSnowTemp": MountainSnowTemp = v; break;
                case "MountainStartM": MountainStartM = v; break;
                case "SwampNoiseGate": SwampNoiseGate = v; break;
                case "SwampSlopeMax": SwampSlopeMax = v; break;
                case "SwampMoistMin": SwampMoistMin = v; break;
                case "SwampAltMax": SwampAltMax = v; break;
                case "MistEncMin": MistEncMin = v; break;
                case "MistMoistMin": MistMoistMin = v; break;
                case "MistRadius": MistRadius = v; break;
                case "PlainsSlopeMax": PlainsSlopeMax = v; break;
                case "PlainsDryMax": PlainsDryMax = v; break;
                case "BlackForestMoistMin": BlackForestMoistMin = v; break;
                case "MountainRidgeWidth": MountainRidgeWidth = v; break;
                case "MountainCrestAmp": MountainCrestAmp = v; break;
                case "MountainSummitAmp": MountainSummitAmp = v; break;
                case "MountainSummitGate": MountainSummitGate = v; break;
                case "MountainApronAmp": MountainApronAmp = v; break;
                case "MountainApronWidthMul": MountainApronWidthMul = v; break;
                case "MountainStructAmp": MountainStructAmp = v; break;
            }
        }
    }
}
