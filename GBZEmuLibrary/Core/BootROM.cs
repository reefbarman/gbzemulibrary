using System;

namespace GBZEmuLibrary
{
    internal static class BootROM
    {
        public const int DMG_SIZE = 0x100;
        public const int GBC_SIZE = 0x900;

        private static readonly byte[] Empty = new byte[0];

        private static byte[] _dmgBootROM;
        private static byte[] _gbcBootROM;

        public static byte[] Bytes { get; private set; } = Empty;

        public static byte[] GBCBootROM => _gbcBootROM;

        public static bool HasGBCBootROM => _gbcBootROM != null;

        public static bool IsGBCSelected { get; private set; }

        public static void Clear()
        {
            _dmgBootROM = null;
            _gbcBootROM = null;
            Bytes = Empty;
            IsGBCSelected = false;
        }

        public static void Load(byte[] data)
        {
            if (data == null)
            {
                return;
            }

            if (data.Length == DMG_SIZE)
            {
                _dmgBootROM = (byte[])data.Clone();
                return;
            }

            if (data.Length == GBC_SIZE)
            {
                _gbcBootROM = (byte[])data.Clone();
                return;
            }

            throw new ArgumentException("Boot ROM must be a 256-byte DMG image or a 2304-byte CGB image.", nameof(data));
        }

        public static bool TrySetBootMode(bool gbc, bool shortBoot)
        {
            var source = gbc ? _gbcBootROM : _dmgBootROM;

            if (source == null)
            {
                Bytes = Empty;
                IsGBCSelected = false;
                return false;
            }

            IsGBCSelected = gbc;

            if (!gbc && shortBoot)
            {
                Bytes = (byte[])source.Clone();
                Bytes[0x00FD] = 0x03;
            }
            else
            {
                Bytes = source;
            }

            return true;
        }
    }
}
