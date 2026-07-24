using System;
using System.Collections.Generic;
using System.IO;

namespace GBZEmuLibrary
{
    /// <summary>
    /// Identifies a non-fatal cartridge-header inconsistency found while inspecting a ROM image.
    /// </summary>
    public enum CartridgeDiagnosticKind
    {
        NintendoLogoMismatch,
        HeaderChecksumMismatch,
        GlobalChecksumMismatch,
        PhysicalRomLargerThanDeclared
    }

    /// <summary>
    /// Describes one non-fatal cartridge-header or geometry inconsistency.
    /// </summary>
    public sealed class CartridgeDiagnostic
    {
        internal CartridgeDiagnostic(CartridgeDiagnosticKind kind, string message)
        {
            Kind = kind;
            Message = message;
        }

        /// <summary>
        /// Gets the category of cartridge inconsistency.
        /// </summary>
        public CartridgeDiagnosticKind Kind { get; }

        /// <summary>
        /// Gets a host-displayable diagnostic message that does not contain ROM data.
        /// </summary>
        public string Message { get; }
    }

    /// <summary>
    /// Validates cartridge structure and reports host-visible metadata for one complete Game Boy ROM image.
    /// </summary>
    public sealed class CartridgeInspection
    {
        private const int MinimumRomBanks = 2;
        private const int MaximumRomBanks = 512;
        private const int NintendoLogoOffset = 0x104;
        private const int HeaderChecksumStart = 0x134;
        private const int HeaderChecksumEnd = 0x14C;
        private const int HeaderChecksumOffset = 0x14D;
        private const int GlobalChecksumOffset = 0x14E;
        private const int CompleteHeaderLength = 0x150;

        private static readonly byte[] NintendoLogo =
        {
            0xCE, 0xED, 0x66, 0x66, 0xCC, 0x0D, 0x00, 0x0B,
            0x03, 0x73, 0x00, 0x83, 0x00, 0x0C, 0x00, 0x0D,
            0x00, 0x08, 0x11, 0x1F, 0x88, 0x89, 0x00, 0x0E,
            0xDC, 0xCC, 0x6E, 0xE6, 0xDD, 0xDD, 0xD9, 0x99,
            0xBB, 0xBB, 0x67, 0x63, 0x6E, 0x0E, 0xEC, 0xCC,
            0xDD, 0xDC, 0x99, 0x9F, 0xBB, 0xB9, 0x33, 0x3E
        };

        private CartridgeInspection(
            CartridgeCompatibility compatibility,
            int physicalRomBanks,
            int declaredRomBanks,
            int declaredRamBanks,
            IReadOnlyList<CartridgeDiagnostic> diagnostics)
        {
            Compatibility = compatibility;
            PhysicalRomBanks = physicalRomBanks;
            DeclaredRomBanks = declaredRomBanks;
            DeclaredRamBanks = declaredRamBanks;
            Diagnostics = diagnostics;
        }

        /// <summary>
        /// Gets the DMG/CGB compatibility declared by the cartridge header.
        /// </summary>
        public CartridgeCompatibility Compatibility { get; }

        /// <summary>
        /// Gets the number of complete 16 KiB ROM banks physically present in the image.
        /// </summary>
        public int PhysicalRomBanks { get; }

        /// <summary>
        /// Gets the ROM-bank count declared by the cartridge header.
        /// </summary>
        public int DeclaredRomBanks { get; }

        /// <summary>
        /// Gets the external-RAM bank count declared by the cartridge header.
        /// </summary>
        public int DeclaredRamBanks { get; }

        /// <summary>
        /// Gets non-fatal checksum, logo, and under-declared-geometry diagnostics.
        /// </summary>
        public IReadOnlyList<CartridgeDiagnostic> Diagnostics { get; }

        /// <summary>
        /// Validates a complete Game Boy ROM image before mapper construction or emulation startup.
        /// </summary>
        public static CartridgeInspection Inspect(byte[] romData)
        {
            if (romData == null)
            {
                throw new ArgumentNullException(nameof(romData));
            }

            if (romData.Length < CompleteHeaderLength)
            {
                throw new InvalidDataException("The ROM is too short to contain a complete cartridge header.");
            }

            if (romData.Length % CartridgeSchema.ROM_BANK_SIZE != 0)
            {
                throw new InvalidDataException("The ROM size must be a whole number of 16 KiB cartridge banks.");
            }

            var physicalRomBanks = romData.Length / CartridgeSchema.ROM_BANK_SIZE;
            if (physicalRomBanks < MinimumRomBanks || physicalRomBanks > MaximumRomBanks)
            {
                throw new InvalidDataException($"The ROM must contain between {MinimumRomBanks} and {MaximumRomBanks} cartridge banks.");
            }

            var cartridgeType = romData[CartridgeSchema.MBC_MODE_LOC];
            if (!CartridgeHeader.TryGetBankingMode(cartridgeType, out var bankingMode))
            {
                throw new NotSupportedException($"Unsupported cartridge type: 0x{cartridgeType:X2}.");
            }

            if (!CartridgeHeader.TryGetRomBanks(romData[CartridgeSchema.ROM_BANK_NUM_LOC], out var declaredRomBanks))
            {
                throw new InvalidDataException($"Unsupported ROM-size code: 0x{romData[CartridgeSchema.ROM_BANK_NUM_LOC]:X2}.");
            }

            if (declaredRomBanks > physicalRomBanks)
            {
                throw new InvalidDataException(
                    $"The cartridge header declares {declaredRomBanks} ROM banks, but the image contains only {physicalRomBanks}.");
            }

            ValidateRomGeometry(bankingMode, physicalRomBanks);

            if (!CartridgeHeader.TryGetRamBanks(romData[CartridgeSchema.RAM_BANK_NUM_LOC], out var declaredRamBanks))
            {
                throw new InvalidDataException($"Unsupported RAM-size code: 0x{romData[CartridgeSchema.RAM_BANK_NUM_LOC]:X2}.");
            }

            ValidateCartridgeTypeRamCapability(cartridgeType, declaredRamBanks);
            ValidateRamGeometry(cartridgeType, bankingMode, declaredRamBanks);

            var diagnostics = new List<CartridgeDiagnostic>(4);
            if (!HasValidNintendoLogo(romData))
            {
                diagnostics.Add(new CartridgeDiagnostic(
                    CartridgeDiagnosticKind.NintendoLogoMismatch,
                    "The cartridge Nintendo logo does not match the hardware boot logo."));
            }

            if (!HasValidHeaderChecksum(romData))
            {
                diagnostics.Add(new CartridgeDiagnostic(
                    CartridgeDiagnosticKind.HeaderChecksumMismatch,
                    "The cartridge header checksum does not match its header bytes."));
            }

            if (!HasValidGlobalChecksum(romData))
            {
                diagnostics.Add(new CartridgeDiagnostic(
                    CartridgeDiagnosticKind.GlobalChecksumMismatch,
                    "The cartridge global checksum does not match the ROM image."));
            }

            if (physicalRomBanks > declaredRomBanks)
            {
                diagnostics.Add(new CartridgeDiagnostic(
                    CartridgeDiagnosticKind.PhysicalRomLargerThanDeclared,
                    $"The image contains {physicalRomBanks} ROM banks while its header declares {declaredRomBanks}."));
            }

            return new CartridgeInspection(
                CartridgeMetadata.ClassifyCgbFlag(romData[CartridgeSchema.GBC_MODE_LOC]),
                physicalRomBanks,
                declaredRomBanks,
                declaredRamBanks,
                diagnostics.AsReadOnly());
        }

        private static void ValidateRomGeometry(CartridgeSchema.MBCMode bankingMode, int physicalRomBanks)
        {
            int maximumRomBanks;
            switch (bankingMode)
            {
                case CartridgeSchema.MBCMode.NoMBC:
                    maximumRomBanks = 2;
                    break;
                case CartridgeSchema.MBCMode.MBC1:
                case CartridgeSchema.MBCMode.MBC3:
                    maximumRomBanks = 128;
                    break;
                case CartridgeSchema.MBCMode.MBC2:
                    maximumRomBanks = 16;
                    break;
                case CartridgeSchema.MBCMode.MBC5:
                    maximumRomBanks = 512;
                    break;
                default:
                    throw new InvalidDataException("The cartridge ROM geometry cannot be represented by its memory controller.");
            }

            if (physicalRomBanks > maximumRomBanks)
            {
                throw new InvalidDataException(
                    $"The image contains {physicalRomBanks} ROM banks, but its memory controller can select at most {maximumRomBanks}.");
            }
        }

        private static void ValidateCartridgeTypeRamCapability(byte cartridgeType, int declaredRamBanks)
        {
            if (declaredRamBanks == 0)
            {
                return;
            }

            switch (cartridgeType)
            {
                case 0x08:
                case 0x09:
                case 0x02:
                case 0x03:
                case 0x10:
                case 0x12:
                case 0x13:
                case 0x1A:
                case 0x1B:
                case 0x1D:
                case 0x1E:
                    return;
                default:
                    throw new InvalidDataException(
                        $"Cartridge type 0x{cartridgeType:X2} does not provide external RAM but declares {declaredRamBanks} RAM banks.");
            }
        }

        private static void ValidateRamGeometry(
            byte cartridgeType,
            CartridgeSchema.MBCMode bankingMode,
            int declaredRamBanks)
        {
            int maximumRamBanks;
            switch (bankingMode)
            {
                case CartridgeSchema.MBCMode.NoMBC:
                    maximumRamBanks = 1;
                    break;
                case CartridgeSchema.MBCMode.MBC1:
                case CartridgeSchema.MBCMode.MBC3:
                    maximumRamBanks = 4;
                    break;
                case CartridgeSchema.MBCMode.MBC2:
                    if (declaredRamBanks != 0)
                    {
                        throw new InvalidDataException("MBC2 cartridges must not declare separate external RAM banks.");
                    }

                    return;
                case CartridgeSchema.MBCMode.MBC5:
                    var hasRumble = cartridgeType == 0x1C || cartridgeType == 0x1D || cartridgeType == 0x1E;
                    maximumRamBanks = hasRumble ? 8 : 16;
                    break;
                default:
                    throw new InvalidDataException("The cartridge RAM geometry cannot be represented by its memory controller.");
            }

            if (declaredRamBanks > maximumRamBanks)
            {
                throw new InvalidDataException(
                    $"The cartridge declares {declaredRamBanks} RAM banks, but its memory controller can select at most {maximumRamBanks}.");
            }
        }

        private static bool HasValidNintendoLogo(byte[] romData)
        {
            for (var index = 0; index < NintendoLogo.Length; index++)
            {
                if (romData[NintendoLogoOffset + index] != NintendoLogo[index])
                {
                    return false;
                }
            }

            return true;
        }

        private static bool HasValidHeaderChecksum(byte[] romData)
        {
            byte checksum = 0;
            for (var index = HeaderChecksumStart; index <= HeaderChecksumEnd; index++)
            {
                checksum = (byte)(checksum - romData[index] - 1);
            }

            return checksum == romData[HeaderChecksumOffset];
        }

        private static bool HasValidGlobalChecksum(byte[] romData)
        {
            var expected = (romData[GlobalChecksumOffset] << 8) | romData[GlobalChecksumOffset + 1];
            var actual = 0;
            for (var index = 0; index < romData.Length; index++)
            {
                if (index != GlobalChecksumOffset && index != GlobalChecksumOffset + 1)
                {
                    actual = (actual + romData[index]) & 0xFFFF;
                }
            }

            return actual == expected;
        }
    }
}
