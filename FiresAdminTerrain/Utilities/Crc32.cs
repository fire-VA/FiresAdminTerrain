namespace VerdantsAscent
{
    // CRC-32 (IEEE 802.3, the zip and PNG polynomial). The park pack checks each record with it (D6c): a record reads back
    // only as the bytes that were written. A client's replayed river grid is summed with it and compared with the server's
    // (D13). Of() sums a buffer at once; Start, Add and Finish sum values as they come, without building a buffer.
    internal static class Crc32
    {
        internal const uint Start = AllOnes;

        private const uint ReflectedPolynomial = 0xEDB88320u;
        private const uint AllOnes = 0xFFFFFFFFu;
        private const uint LowByteMask = 0xFFu;
        private const int ByteValues = 256;
        private const int BitsPerByte = 8;

        private static readonly uint[] Table = BuildTable();

        internal static uint Of(byte[] data, int offset, int count) => Finish(Add(Start, data, offset, count));

        internal static uint Add(uint running, byte[] data, int offset, int count)
        {
            int end = offset + count;
            for (int i = offset; i < end; i++) running = Step(running, data[i]);
            return running;
        }

        // The four bytes of `value`, lowest first (the order BinaryWriter writes them).
        internal static uint Add(uint running, int value)
        {
            for (int shift = 0; shift < sizeof(int) * BitsPerByte; shift += BitsPerByte) running = Step(running, (uint)(value >> shift));
            return running;
        }

        internal static uint Finish(uint running) => ~running;

        private static uint Step(uint running, uint value) => Table[(running ^ value) & LowByteMask] ^ (running >> BitsPerByte);

        private static uint[] BuildTable()
        {
            var table = new uint[ByteValues];
            for (uint value = 0; value < ByteValues; value++)
            {
                uint entry = value;
                for (int bit = 0; bit < BitsPerByte; bit++) entry = (entry & 1u) != 0 ? ReflectedPolynomial ^ (entry >> 1) : entry >> 1;
                table[value] = entry;
            }
            return table;
        }
    }
}
