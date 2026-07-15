using System;
using System.IO;

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

        private static byte[] _defaultDMGBootROM;
        private static byte[] _defaultGBCBootROM;

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
        /// Fills any slot without a host-supplied image with the embedded GBZEmu boot ROM.
        /// </summary>
        public void EnsureDefaults()
        {
            if (_dmgBootROM == null)
            {
                if (_defaultDMGBootROM == null)
                {
                    _defaultDMGBootROM = LoadEmbedded("dmg_boot.bin", DMG_SIZE);
                }

                _dmgBootROM = _defaultDMGBootROM;
            }

            if (_gbcBootROM == null)
            {
                if (_defaultGBCBootROM == null)
                {
                    _defaultGBCBootROM = LoadEmbedded("cgb_boot.bin", GBC_SIZE);
                }

                _gbcBootROM = _defaultGBCBootROM;
            }
        }

        /// <summary>
        /// Loads an embedded boot-ROM image; the result is shared, so callers must never mutate it.
        /// </summary>
        private static byte[] LoadEmbedded(string name, int expectedSize)
        {
            using (var stream = typeof(BootROM).Assembly.GetManifestResourceStream($"GBZEmuLibrary.Resources.{name}"))
            {
                if (stream == null)
                {
                    throw new InvalidOperationException($"Embedded boot ROM resource missing: {name}");
                }

                using (var buffer = new MemoryStream())
                {
                    stream.CopyTo(buffer);
                    var bytes = buffer.ToArray();

                    if (bytes.Length != expectedSize)
                    {
                        throw new InvalidOperationException($"Embedded boot ROM {name} is {bytes.Length} bytes, expected {expectedSize}.");
                    }

                    return bytes;
                }
            }
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
