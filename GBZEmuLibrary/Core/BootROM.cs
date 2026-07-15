using System;

namespace GBZEmuLibrary
{
    /// <summary>
    /// Stores and selects host-supplied boot-ROM images for one emulator instance.
    /// </summary>
    internal sealed class BootROM
    {
        public const int DMG_SIZE = 0x100;
        public const int GBC_SIZE = 0x900;

        private static readonly byte[] Empty = new byte[0];

        private byte[] _dmgBootROM;
        private byte[] _gbcBootROM;

        public byte[] Bytes { get; private set; } = Empty;

        public byte[] GBCBootROM => _gbcBootROM;

        public bool HasGBCBootROM => _gbcBootROM != null;

        public bool IsGBCSelected { get; private set; }

        /// <summary>
        /// Clears all firmware images and active overlay selection for this emulator.
        /// </summary>
        public void Clear()
        {
            _dmgBootROM = null;
            _gbcBootROM = null;
            Bytes = Empty;
            IsGBCSelected = false;
        }

        /// <summary>
        /// Validates and stores a private copy of a DMG or CGB boot-ROM image.
        /// </summary>
        public void Load(byte[] data)
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

        /// <summary>
        /// Selects a compatible firmware overlay, optionally applying the shortened DMG animation patch.
        /// </summary>
        public bool TrySetBootMode(bool gbc, bool shortBoot)
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
