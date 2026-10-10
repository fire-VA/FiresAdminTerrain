using System;

namespace VerdantsAscent.Terrain.Voxel
{
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════
    // THE ROCK — what the ground is MADE OF, as a pure function of world position.
    //
    // Agreed in docs/CANYONS_AND_GORGES.md §3a as a DEPENDENCY, not an extra, because several systems are
    // already faking what it provides:
    //   • ErodeHydraulic's bedrock term is `1/(1 + metresAboveSea·0.0025)` — it asserts "high ground is rock"
    //     because it had nothing better. With a real field that becomes lithology, and a soft-rock massif can
    //     erode while a low granite shield resists.
    //   • Canyon BENCHING is impossible without it: stair-stepped walls come from hard layers capping soft.
    //     A uniform slope does not read as a canyon however deep it is.
    //   • Caves are currently placed with no reference to the rock they are in. Limestone is where cave
    //     systems belong, and karst/springs are a prioritised gap in docs/LANDFORMS.md.
    //   • Cliffs and coastal shape — resistant headlands, soft bays.
    //
    // ⚠⚠ IT VARIES WITH ALTITUDE, NOT JUST MAP POSITION — this is the whole point and the easiest thing to
    // get wrong. Benching comes from BEDDING: horizontal layers, hard over soft. A purely 2D hardness map
    // gives regional variation and then perfectly uniform walls, so canyons come out smooth-sided and the
    // main visual payoff is lost. The form, straight from the spec:
    //
    //      hardness(x, z, y) = regionalRock(x, z) × bedding(y + tilt(x, z))
    //
    // — a slow 2D field choosing rock character, a periodic function of HEIGHT giving the beds, and a slow
    // regional TILT so the same bed outcrops at different altitudes in different places. That is ordinary
    // stratigraphy, and it is what makes mesa country read as mesa country.
    //
    // Fits the meta-rule in docs/LANDFORMS.md: features are REGIONAL — one slow field gates one process,
    // like `rugg`.
    //
    // ⚠ PURE FUNCTION OF (x, y, z, seed), and deliberately built on System.Math with its own value noise —
    // no UnityEngine, no stored grid, no state. Two consequences, both of which this project has paid for
    // before: the studio and the game cannot compute different rock (GEN_VERSION 102, where the two disagreed
    // for every seed ever generated because `Mathf.SmoothStep` and the studio shim's `smoothstep` are
    // different functions sharing a name), and nothing here can be non-deterministic under Parallel.For
    // (GEN_VERSION 131). Keep it that way: no `Mathf`, no fields, no caches.
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════
    public static class FiresRock
    {
        /// What KIND of rock a region is. Chosen for the processes that will read it, not for geological
        /// completeness: bedded-and-soluble (caves, karst springs), bedded-and-not (benching without karst),
        /// and two massive types that bench poorly but resist strongly.
        public enum Rock : byte
        {
            Sandstone = 0,   // bedded, soft, the classic canyon/mesa former — hard caps over soft beds
            Limestone = 1,   // bedded, soluble ⇒ THE cave and karst-spring rock
            Granite   = 2,   // massive, hard, no bedding — shields and domes, gorges rather than canyons
            Basalt    = 3,   // massive, hard, weak columnar banding — plateaus and stepped traps
        }

        // Province scale. ~4 km provinces: big enough that a canyon stays in one rock for its whole length
        // (a canyon that changes lithology every 200 m is noise, not geology), small enough that a 21 km
        // world holds several distinct rock countries.
        const float PROVINCE_FREQ = 1f / 4200f;

        // Bedding. Real sedimentary beds run centimetres to tens of metres; the ones that MATTER visually are
        // the resistant caps that hold a bench, so the period is set to the scale a player reads as a step.
        const float BED_PERIOD_MIN = 14f;    // metres of section per hard/soft couplet, in tightly bedded country
        const float BED_PERIOD_MAX = 46f;    // ...and in massively bedded country
        const float TILT_M = 110f;           // how far a given bed wanders in altitude across the map

        // Hardness range. Never 0 (nothing is infinitely erodible) and never quite 1 outside true massive rock,
        // so consumers can multiply by it without special cases.
        const float HARD_MIN = 0.18f;

        // ── hash value noise, self-contained and deterministic (same construction as FiresTerrain3D) ────────
        static float Hash(int x, int y, int z, int seed)
        {
            uint h = (uint)(x * 374761393 + y * 668265263 + z * 2147483647 + seed * 1274126177);
            h = (h ^ (h >> 13)) * 1274126177u;
            h ^= h >> 16;
            return (int)(h & 0xFFFFFF) / (float)0xFFFFFF;   // 0..1
        }

        static float Smooth(float t) => t * t * (3f - 2f * t);
        static float Lerp(float a, float b, float t) => a + (b - a) * t;
        static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;

        /// GLSL smoothstep semantics — a smoothed inverse-lerp, NOT Unity's value interpolation.
        /// Spelled out here rather than borrowed: see the GEN_VERSION 102 note in the header.
        static float Smooth01(float edge0, float edge1, float x)
        {
            if (edge1 <= edge0) return x < edge0 ? 0f : 1f;
            return Smooth(Clamp01((x - edge0) / (edge1 - edge0)));
        }

        static float Value2(float x, float z, int seed)
        {
            int xi = (int)Math.Floor(x), zi = (int)Math.Floor(z);
            float fx = Smooth(x - xi), fz = Smooth(z - zi);
            float c00 = Hash(xi, 0, zi, seed), c10 = Hash(xi + 1, 0, zi, seed);
            float c01 = Hash(xi, 0, zi + 1, seed), c11 = Hash(xi + 1, 0, zi + 1, seed);
            return Lerp(Lerp(c00, c10, fx), Lerp(c01, c11, fx), fz);
        }

        /// Fractal 2D noise, −1..1.
        static float Fbm2(float x, float z, float freq, int oct, int seed)
        {
            float sum = 0f, amp = 0.5f, tot = 0f;
            for (int o = 0; o < oct; o++)
            {
                sum += (Value2(x * freq, z * freq, seed + o * 101) - 0.5f) * 2f * amp;
                tot += amp; amp *= 0.5f; freq *= 2f;
            }
            return sum / tot;
        }

        // ── THE REGIONAL FIELD ──────────────────────────────────────────────────────────────────────────────
        // TWO independent slow fields rather than one, because rock character is not a single axis. `massive`
        // asks whether the country is crystalline/volcanic or layered; `soluble` asks, within layered country,
        // whether it is limestone. Thresholding one field would make the four types a fixed sequence and put
        // granite always next to basalt, which is a pattern a player would eventually read as a stripe.
        static float MassiveField(float wx, float wz, int seed)
            => Fbm2(wx, wz, PROVINCE_FREQ, 3, seed + 7717) * 0.5f + 0.5f;   // 0..1

        static float SolubleField(float wx, float wz, int seed)
            => Fbm2(wx, wz, PROVINCE_FREQ * 1.37f, 3, seed + 3391) * 0.5f + 0.5f;

        /// The rock province at this column. A 2D question by definition — one province, one column, all the
        /// way down; the bedding inside it is what varies with height.
        public static Rock ClassAt(float wx, float wz, int seed)
        {
            float massive = MassiveField(wx, wz, seed);
            if (massive > 0.66f)
                // Within massive country, the same soluble field doubles as a crystalline/volcanic split — it
                // is uncorrelated with `massive`, so granite and basalt provinces interleave freely.
                return SolubleField(wx, wz, seed) > 0.5f ? Rock.Granite : Rock.Basalt;
            return SolubleField(wx, wz, seed) > 0.60f ? Rock.Limestone : Rock.Sandstone;
        }

        /// Base erosion resistance of a rock type, before bedding. 0..1.
        public static float BaseHardness(Rock r)
        {
            switch (r)
            {
                case Rock.Granite:   return 0.94f;   // massive crystalline — the shield that outlasts everything around it
                case Rock.Basalt:    return 0.82f;   // hard, but jointed, so it fails in columns rather than resisting evenly
                case Rock.Limestone: return 0.55f;   // strong dry, but it DISSOLVES — hence caves, and hence sheer dry walls
                default:             return 0.42f;   // sandstone/shale — the soft, deeply bedded canyon former
            }
        }

        /// How strongly a rock expresses BEDDING (0 = massive, 1 = strongly layered). This is what decides
        /// whether a wall benches or runs smooth, so it is a property of the rock and not a global constant.
        public static float BeddingStrength(Rock r)
        {
            switch (r)
            {
                case Rock.Granite:   return 0.05f;   // essentially none — granite walls are smooth sweeps and domes
                case Rock.Basalt:    return 0.35f;   // flow contacts and colonnade: real steps, but coarse ones
                case Rock.Limestone: return 0.70f;
                default:             return 1.00f;   // sandstone benches hardest, which is why canyon country is sandstone
            }
        }

        /// Metres of altitude the local bedding is displaced by — the slow regional dip. Exposed because the
        /// canyon profile wants the same number: a bench must sit at the bed it is cut from, and if the
        /// incision pass recomputed its own tilt the benches would drift off the rock that makes them.
        public static float TiltAt(float wx, float wz, int seed)
            => Fbm2(wx, wz, PROVINCE_FREQ * 0.8f, 2, seed + 5153) * TILT_M;

        /// The stratigraphic height at this point — altitude corrected for regional dip. Two columns a
        /// kilometre apart are in the SAME bed at different real altitudes, which is what makes a bench line
        /// traceable across a landscape instead of ringing every hill at one contour.
        public static float SectionY(float wx, float y, float wz, int seed) => y + TiltAt(wx, wz, seed);

        /// A STRATIGRAPHIC COLUMN, not a waveform: resistance 0..1 at this height in the section.
        ///
        /// ⚠ THE FIRST VERSION WAS A COSINE OF FIXED PERIOD AND IT LOOKED MACHINED. Rendering a 6 km section
        /// showed the failure immediately — every couplet identical thickness, identical contrast, evenly
        /// stacked to the horizon, like corrugated card. Real rock does not do that, and a canyon cut into it
        /// would come out with evenly-spaced ripples instead of the handful of strong benches that give a wall
        /// its character. The Grand Canyon reads the way it does because its units are DIFFERENT FROM EACH
        /// OTHER — a massive cliff-forming sandstone, a thin shale, a thick slope-former — with named
        /// formations a geologist can pick out at a distance.
        ///
        /// So the column is built from per-BED properties instead of a global period:
        ///   • a vertical domain WARP stretches and compresses groups of beds, so thickness varies;
        ///   • each bed index hashes to its OWN body resistance and its OWN cap thickness.
        /// A bed whose cap is thin and hard is a bench former; one that is uniformly soft is a slope former.
        /// Both come out of the same two lines, which is what keeps this a function and not a rock database.
        public static float BedResistance(float wx, float y, float wz, int seed)
        {
            // Mean bed thickness is regional: some sections are thinly laminated, some massively bedded.
            float t = MassiveField(wx + 9000f, wz - 4000f, seed);
            float period = Lerp(BED_PERIOD_MIN, BED_PERIOD_MAX, t);
            float sec = SectionY(wx, y, wz, seed);

            // VERTICAL domain warp — a function of height, so it stretches the column rather than tilting it.
            // Without this every bed is the same thickness and the wall is corrugated card.
            float sRaw = sec / period;
            float warp = (Value2(sRaw * 0.22f, 17.3f, seed + 2029) - 0.5f) * 1.9f;
            float s = sRaw + warp;

            int bed = (int)Math.Floor(s);
            float f = s - bed;                                   // 0 at the base of this bed, 1 at its top

            // This bed's own character. Hashing the INDEX (not the position) is what makes a bed a single
            // continuous unit across the whole map — trace it sideways and it is still the same rock, which
            // is precisely what a bench needs in order to be a bench and not a smear.
            float bodyH = 0.10f + 0.55f * Hash(bed, 0, 0, seed + 4441);          // slope-former .. resistant body
            float capH  = 0.62f + 0.38f * Hash(bed, 0, 0, seed + 8821);          // the resistant lid on top
            float capT  = 0.10f + 0.34f * Hash(bed, 0, 0, seed + 1553);          // ...and how thick that lid is

            // Cap sits at the TOP of its bed with a soft contact, so an eroding wall undercuts the soft body
            // and leaves the lid standing proud — the overhang at a bench lip.
            float capMix = Smooth01(1f - capT - 0.06f, 1f - capT + 0.06f, f);
            return Lerp(bodyH, capH, capMix);
        }

        /// EROSION RESISTANCE at a point in the rock, 0..1. This is the field consumers multiply by.
        ///
        /// ⚠ THE PROPERTIES BLEND ACROSS A CONTACT EVEN THOUGH THE CLASS DOES NOT. Reading `ClassAt` and
        /// taking that type's constants makes hardness a STEP FUNCTION at every province boundary, and the
        /// first section render showed exactly that: knife-edge vertical walls where granite meets sandstone.
        /// Anything that consumes this — erosion, canyon profiles — would then print a dead-straight line
        /// across the map at each contact, which is the family of artifact this project has spent whole
        /// sessions removing (the "circuit trace" rivers, the straight-edged flat polygons). So the constants
        /// are interpolated over the SAME two fields the class is thresholded from: a contact becomes a couple
        /// of hundred metres of transitional ground, which is what a fault zone or a facies change looks like
        /// anyway. `ClassAt` stays discrete, because a cave pass asking "is this limestone?" needs a yes or no.
        public static float Hardness(float wx, float y, float wz, int seed)
        {
            float m = MassiveField(wx, wz, seed);
            float s = SolubleField(wx, wz, seed);
            float massiveness = Smooth01(0.58f, 0.74f, m);                       // 0 layered .. 1 crystalline/volcanic
            float graniteness = Smooth01(0.42f, 0.58f, s);                       // within massive country
            float limeyness   = Smooth01(0.52f, 0.68f, s);                       // within layered country

            float layeredBase = Lerp(BaseHardness(Rock.Sandstone), BaseHardness(Rock.Limestone), limeyness);
            float massiveBase = Lerp(BaseHardness(Rock.Basalt),    BaseHardness(Rock.Granite),   graniteness);
            float base_ = Lerp(layeredBase, massiveBase, massiveness);

            float layeredBed = Lerp(BeddingStrength(Rock.Sandstone), BeddingStrength(Rock.Limestone), limeyness);
            float massiveBed = Lerp(BeddingStrength(Rock.Basalt),    BeddingStrength(Rock.Granite),   graniteness);
            float bedStr = Lerp(layeredBed, massiveBed, massiveness);
            if (bedStr <= 0.001f) return base_;
            // The bed's own resistance swings around the province's mean, centred so a region's average stays
            // what BaseHardness says it is. Amplitude scales with how bedded the rock is, so granite stays a
            // smooth sweep and sandstone steps hard.
            float bed = BedResistance(wx, y, wz, seed);        // 0 soft .. 1 resistant
            float swing = 0.38f * bedStr;
            float h = base_ + (bed - 0.5f) * 2f * swing;
            return h < HARD_MIN ? HARD_MIN : h > 1f ? 1f : h;
        }
    }
}
