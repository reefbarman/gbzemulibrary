namespace GBZEmuLibrary
{
    internal static class BootROM
    {
        public static void SetBootMode(bool gbc, bool quickAnim)
        {
            if (gbc)
            {
                Bytes = GBCBootROM;
            }
            else
            {
                Bytes = Raw;

                if (quickAnim)
                {
                    Bytes         = ShortRaw;
                    Bytes[0x00FD] = 0x03;
                }
            }
        }

        public static byte[] Bytes { get; private set; }

        private static byte[] Raw { get; } =
        {
        };

        private static byte[] ShortRaw { get; } =
        {
        };

        public static byte[] GBCBootROM { get; } =
        {
        };
    }
}
