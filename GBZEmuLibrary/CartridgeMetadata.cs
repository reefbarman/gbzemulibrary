using System;
using System.IO;

namespace GBZEmuLibrary
{
    /// <summary>
    /// Describes the hardware compatibility declared by a cartridge header.
    /// </summary>
    public enum CartridgeCompatibility
    {
        DmgOnly,
        CgbCompatible,
        CgbOnly
    }

    /// <summary>
    /// Reads lightweight cartridge metadata without constructing or starting an emulator.
    /// </summary>
    public sealed class CartridgeMetadata
    {
        private const int CgbFlagOffset = 0x143;
        private const byte CgbCompatibleFlag = 0x80;
        private const byte CgbOnlyFlag = 0xC0;

        private CartridgeMetadata(CartridgeCompatibility compatibility)
        {
            Compatibility = compatibility;
        }

        /// <summary>
        /// Gets the DMG/CGB compatibility declared by the cartridge header.
        /// </summary>
        public CartridgeCompatibility Compatibility { get; }

        /// <summary>
        /// Reads cartridge metadata from a ROM file without loading the complete image.
        /// </summary>
        public static CartridgeMetadata Read(string romPath)
        {
            if (string.IsNullOrWhiteSpace(romPath))
            {
                throw new ArgumentException("A ROM path is required.", nameof(romPath));
            }

            using (var stream = File.OpenRead(romPath))
            {
                if (stream.Length <= CgbFlagOffset)
                {
                    throw new InvalidDataException("The ROM is too short to contain a cartridge header.");
                }

                stream.Position = CgbFlagOffset;
                return new CartridgeMetadata(ClassifyCgbFlag((byte)stream.ReadByte()));
            }
        }

        /// <summary>
        /// Reads cartridge metadata from a ROM image or header buffer.
        /// </summary>
        public static CartridgeMetadata Read(byte[] romData)
        {
            if (romData == null)
            {
                throw new ArgumentNullException(nameof(romData));
            }

            if (romData.Length <= CgbFlagOffset)
            {
                throw new InvalidDataException("The ROM is too short to contain a cartridge header.");
            }

            return new CartridgeMetadata(ClassifyCgbFlag(romData[CgbFlagOffset]));
        }

        internal static CartridgeCompatibility ClassifyCgbFlag(byte cgbFlag)
        {
            if (cgbFlag == CgbOnlyFlag)
            {
                return CartridgeCompatibility.CgbOnly;
            }

            return (cgbFlag & CgbCompatibleFlag) != 0
                ? CartridgeCompatibility.CgbCompatible
                : CartridgeCompatibility.DmgOnly;
        }
    }
}
