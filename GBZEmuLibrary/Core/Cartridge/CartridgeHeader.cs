using System;
using System.IO;
using System.Text;

namespace GBZEmuLibrary
{
    /// <summary>
    /// Parses host-visible and controller-specific metadata from a Game Boy cartridge header.
    /// </summary>
    public class CartridgeHeader
    {
        public string Title { get; private set; }
        internal int Length { get; }
        internal GBCMode GBCMode { get; private set; } = GBCMode.NoGBC;
        internal CartridgeSchema.MBCMode BankingMode { get; private set; } = CartridgeSchema.MBCMode.NoMBC;
        internal int ROMBanks { get; private set; } = 1;
        internal int RAMBanks { get; private set; } = 1;
        internal bool HasRTC { get; private set; }
        internal bool HasRumble { get; private set; }
        internal bool CustomPalette { get; private set; }
        internal bool IsNintendoLicensed => _nintendoCart;
        internal byte CgbFlag { get; private set; }
        internal byte TitleChecksum => _titleHash;
        internal byte FourthTitleByte { get; private set; }

        private bool _nintendoCart = false;
        private byte _titleHash;

        /// <summary>
        /// Parses cartridge metadata without an emulator-specific boot ROM for custom DMG palette lookup.
        /// </summary>
        public CartridgeHeader(byte[] cart)
            : this(cart, null, false)
        {
        }

        /// <summary>
        /// Parses cartridge metadata using the supplied instance boot ROM for custom DMG palette lookup.
        /// </summary>
        internal CartridgeHeader(byte[] cart, BootROM bootROM)
            : this(cart, bootROM, false)
        {
        }

        /// <summary>
        /// Parses cartridge metadata after optionally reusing a caller's completed structural inspection.
        /// </summary>
        internal CartridgeHeader(byte[] cart, BootROM bootROM, bool structureValidated)
        {
            if (!structureValidated)
            {
                CartridgeInspection.Inspect(cart);
            }

            Length = cart.Length;
            ParseGBCMode(cart);
            ParseMBCMode(cart);
            ParseROMBanks(cart);
            ParseRAMBanks(cart);
            ParseLicenseCode(cart);
            ParseTitle(cart);
            ParseCustomPalette(bootROM);
        }

        private void ParseGBCMode(byte[] cart)
        {
            CgbFlag = cart[CartridgeSchema.GBC_MODE_LOC];
            switch (CartridgeMetadata.ClassifyCgbFlag(CgbFlag))
            {
                case CartridgeCompatibility.CgbCompatible:
                    GBCMode = GBCMode.GBCSupport;
                    break;
                case CartridgeCompatibility.CgbOnly:
                    GBCMode = GBCMode.GBCOnly;
                    break;
            }
        }

        private void ParseMBCMode(byte[] cart)
        {
            var code = cart[CartridgeSchema.MBC_MODE_LOC];
            HasRTC = code == 0x0F || code == 0x10;
            HasRumble = code == 0x1C || code == 0x1D || code == 0x1E;
            if (!TryGetBankingMode(code, out var bankingMode))
            {
                throw new NotSupportedException($"Unsupported cartridge type: 0x{code:X2}.");
            }

            BankingMode = bankingMode;
        }

        private void ParseROMBanks(byte[] cart)
        {
            if (!TryGetRomBanks(cart[CartridgeSchema.ROM_BANK_NUM_LOC], out var romBanks))
            {
                throw new InvalidDataException($"Unsupported ROM-size code: 0x{cart[CartridgeSchema.ROM_BANK_NUM_LOC]:X2}.");
            }

            ROMBanks = romBanks;
        }

        private void ParseRAMBanks(byte[] cart)
        {
            if (!TryGetRamBanks(cart[CartridgeSchema.RAM_BANK_NUM_LOC], out var ramBanks))
            {
                throw new InvalidDataException($"Unsupported RAM-size code: 0x{cart[CartridgeSchema.RAM_BANK_NUM_LOC]:X2}.");
            }

            RAMBanks = ramBanks;
        }

        internal static bool TryGetBankingMode(byte code, out CartridgeSchema.MBCMode bankingMode)
        {
            switch (code)
            {
                case 0x00:
                case 0x08:
                case 0x09:
                    bankingMode = CartridgeSchema.MBCMode.NoMBC;
                    return true;
                case 0x01:
                case 0x02:
                case 0x03:
                    bankingMode = CartridgeSchema.MBCMode.MBC1;
                    return true;
                case 0x05:
                case 0x06:
                    bankingMode = CartridgeSchema.MBCMode.MBC2;
                    return true;
                case 0x0F:
                case 0x10:
                case 0x11:
                case 0x12:
                case 0x13:
                    bankingMode = CartridgeSchema.MBCMode.MBC3;
                    return true;
                case 0x19:
                case 0x1A:
                case 0x1B:
                case 0x1C:
                case 0x1D:
                case 0x1E:
                    bankingMode = CartridgeSchema.MBCMode.MBC5;
                    return true;
                default:
                    bankingMode = CartridgeSchema.MBCMode.NoMBC;
                    return false;
            }
        }

        internal static bool TryGetRomBanks(byte code, out int romBanks)
        {
            switch (code)
            {
                case 0x00:
                    romBanks = 2;
                    return true;
                case 0x01:
                    romBanks = 4;
                    return true;
                case 0x02:
                    romBanks = 8;
                    return true;
                case 0x03:
                    romBanks = 16;
                    return true;
                case 0x04:
                    romBanks = 32;
                    return true;
                case 0x05:
                    romBanks = 64;
                    return true;
                case 0x06:
                    romBanks = 128;
                    return true;
                case 0x07:
                    romBanks = 256;
                    return true;
                case 0x08:
                    romBanks = 512;
                    return true;
                case 0x52:
                    romBanks = 72;
                    return true;
                case 0x53:
                    romBanks = 80;
                    return true;
                case 0x54:
                    romBanks = 96;
                    return true;
                default:
                    romBanks = 0;
                    return false;
            }
        }

        internal static bool TryGetRamBanks(byte code, out int ramBanks)
        {
            switch (code)
            {
                case 0x00:
                    ramBanks = 0;
                    return true;
                case 0x01:
                case 0x02:
                    ramBanks = 1;
                    return true;
                case 0x03:
                    ramBanks = 4;
                    return true;
                case 0x04:
                    ramBanks = 16;
                    return true;
                case 0x05:
                    ramBanks = 8;
                    return true;
                default:
                    ramBanks = 0;
                    return false;
            }
        }

        private void ParseLicenseCode(byte[] cart)
        {
            switch (cart[CartridgeSchema.OLD_LICENSE_CODE_LOC])
            {
                case 0x33:
                    if (cart[CartridgeSchema.NEW_LICENSE_CODE_LOC] == 0x30 && cart[CartridgeSchema.NEW_LICENSE_CODE_LOC + 1] == 0x31)
                    {
                        _nintendoCart = true;
                    }
                    break;
                case 0x01:
                    _nintendoCart = true;
                    break;
            }
        }

        private void ParseTitle(byte[] cart)
        {
            //TODO different end for GBC and new titles?
            byte titleHash = 0;
            var stillCollecting = true;
            FourthTitleByte = cart[CartridgeSchema.TITLE_LOC_START + 3];

            for (var i = CartridgeSchema.TITLE_LOC_START; i <= CartridgeSchema.TITLE_LOC_END; i++)
            {
                var data = cart[i];

                if (data != 0 && stillCollecting)
                {
                    Title += Encoding.ASCII.GetString(new[] { data });
                }
                else
                {
                    stillCollecting = false;
                }

                titleHash += data;
            }

            _titleHash = titleHash;
        }

        private void ParseCustomPalette(BootROM bootROM)
        {
            if (_nintendoCart && GBCMode == GBCMode.NoGBC && bootROM != null && bootROM.HasColorBootROM)
            {
                for (var i = MemorySchema.BOOT_ROM_CUSTOM_PALETTE_HASH_TABLE_START; i <= MemorySchema.BOOT_ROM_CUSTOM_PALETTE_HASH_TABLE_END; i++)
                {
                    if (_titleHash == bootROM.ColorBootROM[i])
                    {
                        CustomPalette = true;
                        break;
                    }
                }
            }
        }
    }
}
