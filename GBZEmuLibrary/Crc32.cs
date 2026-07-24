namespace GBZEmuLibrary
{
    /// <summary>
    /// Computes the reflected IEEE CRC-32 used by BPS patch footers.
    /// </summary>
    internal static class Crc32
    {
        private const uint Polynomial = 0xEDB88320u;
        private static readonly uint[] Table = CreateTable();

        public static uint Compute(byte[] data, int offset, int length)
        {
            var crc = 0xFFFFFFFFu;
            var end = offset + length;
            for (var index = offset; index < end; index++)
            {
                crc = Table[(crc ^ data[index]) & 0xFF] ^ (crc >> 8);
            }

            return crc ^ 0xFFFFFFFFu;
        }

        private static uint[] CreateTable()
        {
            var table = new uint[256];
            for (uint index = 0; index < table.Length; index++)
            {
                var value = index;
                for (var bit = 0; bit < 8; bit++)
                {
                    value = (value & 1) != 0
                        ? (value >> 1) ^ Polynomial
                        : value >> 1;
                }

                table[index] = value;
            }

            return table;
        }
    }
}
