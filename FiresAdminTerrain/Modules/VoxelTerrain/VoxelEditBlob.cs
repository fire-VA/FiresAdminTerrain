namespace VerdantsAscent.Terrain.Voxel
{
    // ── One reader for the anchor edit blob (0.2.1455, [ghost]) ─────────────────────────────────────────────────────────────────
    // VoxelChunk.ExportBrushEdits' layout: lo[S] hi[S] (sbyte edit bounds per sample), then an optional mop tail
    // [MopTailMarker][count lo][count hi][col lo, col hi]*, then an optional paint tail [PaintTailMarker] + S bytes.
    // Shared by the server guard (VoxelAnchorGuard) and the per-sample undo merge (FatUndo, Voxheim diff P6), so the layout is read
    // in one place next to the chunk that writes it.
    internal static class VoxelEditBlob
    {
        internal const int S = VoxelChunk.SampleCount;

        internal static void ReadTails(byte[] raw, out int mopAt, out int mopLen, out byte[] paint)
        {
            mopAt = S * 2; mopLen = 0; paint = null;
            int paintAt = mopAt;
            if (raw.Length > mopAt + 2 && raw[mopAt] == VoxelChunk.MopTailMarker)
            {
                int cnt = raw[mopAt + 1] | (raw[mopAt + 2] << 8);
                mopLen = 3 + cnt * 2;
                if (raw.Length < mopAt + mopLen) mopLen = raw.Length - mopAt;
                paintAt = mopAt + mopLen;
            }
            if (raw.Length >= paintAt + 1 + S && raw[paintAt] == VoxelChunk.PaintTailMarker)
            {
                paint = new byte[S];
                System.Buffer.BlockCopy(raw, paintAt + 1, paint, 0, S);
            }
        }

        internal static byte PaintOf(byte[] paint, int i) => paint != null ? paint[i] : (byte)0;

        internal static bool SameMop(byte[] a, byte[] b)
        {
            ReadTails(a, out int aAt, out int aLen, out _);
            ReadTails(b, out int bAt, out int bLen, out _);
            if (aLen != bLen) return false;
            for (int k = 0; k < aLen; k++) if (a[aAt + k] != b[bAt + k]) return false;
            return true;
        }

        /// <summary>A blob from bounds, the mop tail copied from mopSource, and the paint (written only when any sample has paint).</summary>
        internal static byte[] Build(byte[] lo, byte[] hi, byte[] mopSource, int mopAt, int mopLen, byte[] paint)
        {
            bool anyPaint = false;
            if (paint != null) for (int i = 0; i < S && !anyPaint; i++) anyPaint = paint[i] != 0;
            var blob = new byte[S * 2 + mopLen + (anyPaint ? 1 + S : 0)];
            System.Buffer.BlockCopy(lo, 0, blob, 0, S);
            System.Buffer.BlockCopy(hi, 0, blob, S, S);
            if (mopLen > 0) System.Buffer.BlockCopy(mopSource, mopAt, blob, S * 2, mopLen);
            if (anyPaint)
            {
                blob[S * 2 + mopLen] = VoxelChunk.PaintTailMarker;
                System.Buffer.BlockCopy(paint, 0, blob, S * 2 + mopLen + 1, S);
            }
            return blob;
        }

        /// <summary>
        /// Voxheim diff P6 (our own design): move `now` to `target` only where nobody changed it since `expected` (the state the step
        /// left). Per sample (both bounds and the paint together): now == expected -> target's value; else now's value stays and counts
        /// as a conflict. The mop tail moves only when it is still the expected one. Null when a blob is unreadable.
        /// </summary>
        internal static byte[] Merge(byte[] target, byte[] expected, byte[] now, out int moved, out int conflicts)
        {
            moved = conflicts = 0;
            if (target == null || expected == null || now == null || target.Length < S * 2 || expected.Length < S * 2 || now.Length < S * 2) return null;
            ReadTails(target, out int tMopAt, out int tMopLen, out byte[] tPaint);
            ReadTails(expected, out _, out _, out byte[] ePaint);
            ReadTails(now, out int nMopAt, out int nMopLen, out byte[] nPaint);
            var lo = new byte[S]; var hi = new byte[S];
            byte[] paint = null;
            for (int i = 0; i < S; i++)
            {
                byte nl = now[i], nh = now[S + i], np = PaintOf(nPaint, i);
                bool untouched = nl == expected[i] && nh == expected[S + i] && np == PaintOf(ePaint, i);
                bool wants = target[i] != nl || target[S + i] != nh || PaintOf(tPaint, i) != np;
                byte ol = nl, oh = nh, op = np;
                if (wants)
                {
                    if (untouched) { ol = target[i]; oh = target[S + i]; op = PaintOf(tPaint, i); moved++; }
                    else conflicts++;
                }
                lo[i] = ol; hi[i] = oh;
                if (op != 0) { if (paint == null) paint = new byte[S]; paint[i] = op; }
            }
            bool mopUntouched = SameMop(now, expected);
            return mopUntouched ? Build(lo, hi, target, tMopAt, tMopLen, paint) : Build(lo, hi, now, nMopAt, nMopLen, paint);
        }
    }
}
