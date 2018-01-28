using System;

namespace GBZEmuLibrary
{
    internal class CartridgeHeader
    {
        public int Length { get; }
        public GBCMode GBCMode { get; private set; } = GBCMode.NoGBC;
        public CartridgeSchema.MBCMode BankingMode { get; private set; } = CartridgeSchema.MBCMode.NoMBC;
        public int ROMBanks { get; private set; } = 1;
        public int RAMBanks { get; private set; } = 1;

        public CartridgeHeader(byte[] cart)
        {
            Length = cart.Length;
            ParseGBCMode(cart);
            ParseMBCMode(cart);
            ParseROMBanks(cart);
            ParseRAMBanks(cart);
        }

        private void ParseGBCMode(byte[] cart)
        {
            switch (cart[CartridgeSchema.GBC_MODE_LOC])
            {
                case 0x80:
                    GBCMode = GBCMode.GBCSupport;
                    break;
                case 0xC0:
                    GBCMode = GBCMode.GBCOnly;
                    break;
            }
        }

        private void ParseMBCMode(byte[] cart)
        {
            var code = cart[CartridgeSchema.MBC_MODE_LOC];

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
                    ROMBanks = BankingMode == CartridgeSchema.MBCMode.MBC1 ? 63 : 64;
                    break;
                case 0x06:
                    ROMBanks = BankingMode == CartridgeSchema.MBCMode.MBC1 ? 125 : 128;
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
    }
}
