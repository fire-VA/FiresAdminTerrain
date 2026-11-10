using System;
using UnityEngine;

namespace VerdantsAscent.Terrain.FarForest
{
    // 0.2.1412 (FarLands #2, Tools\SPEC_FAT_FarForestPlacement.md §2.5): our own Xorshift128, written from the published algorithm
    // (Marsaglia 2003) and the documented seeding of UnityEngine.Random, so vanilla's vegetation placement can be replayed on a worker
    // thread. It is TRUSTED ONLY after FarRngSelfTest has compared it with UnityEngine.Random in this process. The float, range and
    // circle formulas are each picked from a short candidate list by that test (the one that matches every draw); until then the
    // replay runs on the main thread with the real UnityEngine.Random (exact by construction).
    internal struct FarRng
    {
        public uint X, Y, Z, W;

        // Which formula variant the self-test proved (0 = not proven yet). Static: one answer per process.
        internal static int ValueMode, RangeFloatMode, CircleMode;

        public static FarRng FromSeed(int seed)
        {
            var r = new FarRng();
            unchecked
            {
                r.X = (uint)seed;
                r.Y = r.X * 1812433253u + 1u;
                r.Z = r.Y * 1812433253u + 1u;
                r.W = r.Z * 1812433253u + 1u;
            }
            return r;
        }

        public static FarRng FromState(int s0, int s1, int s2, int s3)
            => new FarRng { X = unchecked((uint)s0), Y = unchecked((uint)s1), Z = unchecked((uint)s2), W = unchecked((uint)s3) };

        public bool SameState(int s0, int s1, int s2, int s3)
            => X == unchecked((uint)s0) && Y == unchecked((uint)s1) && Z == unchecked((uint)s2) && W == unchecked((uint)s3);

        public uint NextU32()
        {
            unchecked
            {
                uint t = X ^ (X << 11);
                X = Y; Y = Z; Z = W;
                W = W ^ (W >> 19) ^ t ^ (t >> 8);
                return W;
            }
        }

        // ---- candidate formulas (the self-test names the one that matches) ----
        // 0.2.1417: every step narrowed to float32 with an explicit cast. Mono may keep float intermediates at higher precision; Unity's
        // native Random rounds each step, and the 1415 self-test missed Range(float) by the last bit (seed 42's words reproduce
        // Range(0,1) / Range(2,10) / insideUnitCircle exactly with this formula: t*min + (1-t)*max; theta = Range(0, 2pi) first,
        // r = sqrt(Range(0,1)), x = r cos, y = r sin).
        private const float Inv23 = (float)(1.0 / 8388607.0);

        public float Value01(int mode)
        {
            float f = (float)((float)(NextU32() & 0x7FFFFFu) * Inv23);
            return mode == 2 ? (float)(1f - f) : f;
        }

        public float RangeF(float min, float max, int mode)
        {
            float t = Value01(1);
            if (mode == 2) return (float)(min + (float)((float)(max - min) * t));
            float a = (float)(min * t);
            float b = (float)((float)(1f - t) * max);
            return (float)(a + b);
        }

        public int RangeI(int min, int max)
        {
            unchecked
            {
                if (min < max) return min + (int)(NextU32() % (uint)(max - min));
                if (min > max) return min - (int)(NextU32() % (uint)(min - max));
                return min;
            }
        }

        public Vector2 InsideCircle(int mode)
        {
            if (mode == 2)
            {
                float r2 = (float)Math.Sqrt(RangeF(0f, 1f, RangeFloatMode == 0 ? 1 : RangeFloatMode));
                float th2 = RangeF(0f, Mathf.PI * 2f, RangeFloatMode == 0 ? 1 : RangeFloatMode);
                return new Vector2(r2 * (float)Math.Cos(th2), r2 * (float)Math.Sin(th2));
            }
            float th = RangeF(0f, Mathf.PI * 2f, RangeFloatMode == 0 ? 1 : RangeFloatMode);
            float r = (float)Math.Sqrt(RangeF(0f, 1f, RangeFloatMode == 0 ? 1 : RangeFloatMode));
            return new Vector2((float)(r * (float)Math.Cos(th)), (float)(r * (float)Math.Sin(th)));
        }

        // ---- the proven API the replay uses (only valid once FarRngSelfTest.Trusted) ----
        public float value => Value01(ValueMode);
        public float Range(float min, float max) => RangeF(min, max, RangeFloatMode);
        public int Range(int min, int max) => RangeI(min, max);
        public Vector2 insideUnitCircle => InsideCircle(CircleMode);
    }
}
