using System;
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

        private bool _nintendoCart = false;
        private byte _titleHash;

        /// <summary>
        /// Parses cartridge metadata without an emulator-specific boot ROM for custom DMG palette lookup.
        /// </summary>
        public CartridgeHeader(byte[] cart)
            : this(cart, null)
        {
        }

        /// <summary>
        /// Parses cartridge metadata using the supplied instance boot ROM for custom DMG palette lookup.
        /// </summary>
        internal CartridgeHeader(byte[] cart, BootROM bootROM)
        {
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
            switch (CartridgeMetadata.ClassifyCgbFlag(cart[CartridgeSchema.GBC_MODE_LOC]))
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

            switch (code)
            {
                case 0x00:
                    BankingMode = CartridgeSchema.MBCMode.NoMBC;
                    break;
                case 0x01:
                case 0x02:
                case 0x03:
                    BankingMode = CartridgeSchema.MBCMode.MBC1;
                    break;
                case 0x05:
                case 0x06:
                    BankingMode = CartridgeSchema.MBCMode.MBC2;
                    break;
                case 0x08:
                case 0x09:
                    BankingMode = CartridgeSchema.MBCMode.NoMBC;
                    break;
                case 0x0F:
                case 0x10:
                case 0x11:
                case 0x12:
                case 0x13:
                    BankingMode = CartridgeSchema.MBCMode.MBC3;
                    break;
                case 0x19:
                case 0x1A:
                case 0x1B:
                case 0x1C:
                case 0x1D:
                case 0x1E:
                    BankingMode = CartridgeSchema.MBCMode.MBC5;
                    break;
                default:
                    throw new NotImplementedException($"Unsupported MBC Mode: {code}");
            }
        }

        private void ParseROMBanks(byte[] cart)
        {

            switch (cart[CartridgeSchema.ROM_BANK_NUM_LOC])
            {
                case 0x00:
                    ROMBanks = 2;
                    break;
                case 0x01:
                    ROMBanks = 4;
                    break;
                case 0x02:
                    ROMBanks = 8;
                    break;
                case 0x03:
                    ROMBanks = 16;
                    break;
                case 0x04:
                    ROMBanks = 32;
                    break;
                case 0x05:
                    ROMBanks = 64;
                    break;
                case 0x06:
                    ROMBanks = 128;
                    break;
                case 0x07:
                    ROMBanks = 256;
                    break;
                case 0x08:
                    ROMBanks = 512;
                    break;
                case 0x52:
                    ROMBanks = 72;
                    break;
                case 0x53:
                    ROMBanks = 80;
                    break;
                case 0x54:
                    ROMBanks = 96;
                    break;
            }
        }

        private void ParseRAMBanks(byte[] cart)
        {
            switch (cart[CartridgeSchema.RAM_BANK_NUM_LOC])
            {
                case 0x00:
                    RAMBanks = 0;
                    break;
                case 0x01:
                case 0x02:
                    RAMBanks = 1;
                    break;
                case 0x03:
                    RAMBanks = 4;
                    break;
                case 0x04:
                    RAMBanks = 16;
                    break;
                case 0x05:
                    RAMBanks = 8;
                    break;
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
            if (_nintendoCart && GBCMode == GBCMode.NoGBC && bootROM != null && bootROM.HasGBCBootROM)
            {
                for (var i = MemorySchema.BOOT_ROM_CUSTOM_PALETTE_HASH_TABLE_START; i <= MemorySchema.BOOT_ROM_CUSTOM_PALETTE_HASH_TABLE_END; i++)
                {
                    if (_titleHash == bootROM.GBCBootROM[i])
                    {
                        CustomPalette = true;
                        break;
                    }
                }
            }
        }
    }
}
