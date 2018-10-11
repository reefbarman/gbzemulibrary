using System;
using System.IO;

namespace GBZEmuLibrary
{
    internal class Cartridge : IMemoryUnit
    {
        private enum BankingMode
        {
            ROMBank,
            RAMBank
        }

        public GBCMode GBCMode => _header.GBCMode;
        public bool CustomPalette => _header.CustomPalette;

        private readonly byte[] _cartMemory = new byte[CartridgeSchema.MAX_CART_SIZE];
        
        private CartridgeHeader _header;
        private ExternalRAM _externalRAM;

        private int _romBank = 1;
        private int _ramBank;

        private BankingMode _bankMode;

        public bool LoadFile(string file, string saveLocation)
        {
            if (File.Exists(file))
            {
                try
                {
                    var cart = File.ReadAllBytes(Path.Combine(Directory.GetCurrentDirectory(), file));
                    _header  = new CartridgeHeader(cart);

                    Array.Copy(cart, _cartMemory, _header.Length);

                    _externalRAM = new ExternalRAM(saveLocation, Path.GetFileName(file), CartridgeSchema.RAM_BANK_SIZE * _header.RAMBanks);

                    return true;
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine(e);
                }
            }

            return false;
        }

        public void Terminate()
        {
            _externalRAM.Dispose();
        }

        public bool CanReadWriteByte(int address)
        {
            if (address < MemorySchema.ROM_END)
            {
                return true;
            }

            if (address >= MemorySchema.EXTERNAL_RAM_START && address < MemorySchema.EXTERNAL_RAM_END)
            {
                return true;
            }

            return false;
        }

        public byte ReadByte(int address)
        {
            if (address < MemorySchema.ROM_END)
            {
                if (address >= CartridgeSchema.ROM_BANK_SIZE)
                {
                    address = (address - CartridgeSchema.ROM_BANK_SIZE) + (_romBank * CartridgeSchema.ROM_BANK_SIZE);
                    address %= _header.Length; //TODO determine if this is correct
                }

                return _cartMemory[address];
            }

            if (address >= MemorySchema.EXTERNAL_RAM_START && address < MemorySchema.EXTERNAL_RAM_END)
            {
                address = (address - MemorySchema.EXTERNAL_RAM_START) + ((_bankMode == BankingMode.RAMBank ?  _ramBank : 0) * CartridgeSchema.RAM_BANK_SIZE);

                if (address < _externalRAM.Length && _externalRAM.Enabled)
                {
                    return _externalRAM.ReadByte(address);
                }

                return 0xFF;
            }

            throw new IndexOutOfRangeException();
        }

        public void WriteByte(byte data, int address)
        {
            if (address < MemorySchema.ROM_END)
            {
                //TODO determine how to get rid of magic numbers
                if (address < 0x2000)
                {
                    switch (_header.BankingMode)
                    {
                        case CartridgeSchema.MBCMode.MBC1:
                        case CartridgeSchema.MBCMode.MBC2:
                        case CartridgeSchema.MBCMode.MBC3:
                        case CartridgeSchema.MBCMode.MBC5:
                            if (_header.BankingMode == CartridgeSchema.MBCMode.MBC2 && Helpers.TestBit((ushort)address, 4))
                            {
                                return;
                            }

                            _externalRAM.Enabled = Helpers.GetBits(data, 4) == 0x0A;
                            break;
                    }
                }
                else if (address < 0x4000)
                {
                    int newBank;

                    switch (_header.BankingMode)
                    {
                        case CartridgeSchema.MBCMode.NoMBC:
                            break;

                        case CartridgeSchema.MBCMode.MBC1:
                            //Override the lower 5 bits of the ROM Bank
                            newBank = _romBank;

                            Helpers.ResetLowBits(ref newBank, 5);
                            newBank |= Helpers.GetBits(data, 5);

                            SetROMBank(newBank);
                            break;

                        case CartridgeSchema.MBCMode.MBC2:
                            SetROMBank(Helpers.GetBits(data, 4));
                            break;

                        case CartridgeSchema.MBCMode.MBC3:
                            SetROMBank(Helpers.GetBits(data, 7));
                            break;

                        case CartridgeSchema.MBCMode.MBC5:
                            if (address < 0x3000)
                            {
                                //Override the lower 5 bits of the ROM Bank
                                newBank = _romBank;

                                Helpers.ResetLowBits(ref newBank, 8);
                                newBank |= Helpers.GetBits(data, 8);

                                SetROMBank(newBank);
                            }
                            else
                            {
                                newBank = _romBank;
                                Helpers.SetBit(ref newBank, 9, Helpers.TestBit(data, 1));
                                SetROMBank(newBank);
                            }

                            break;

                        default:
                            throw new ArgumentOutOfRangeException();
                    }
                }
                else if (address < 0x6000)
                {
                    switch (_header.BankingMode)
                    {
                        case CartridgeSchema.MBCMode.MBC1:
                            if (_bankMode == BankingMode.ROMBank)
                            {
                                //Override the bits 5-6 of the romBank
                                int newBank = _romBank;

                                Helpers.ResetHighBits(ref newBank, 3);
                                newBank |= (byte)(data << 5);

                                SetROMBank(newBank);
                            }
                            else
                            {
                                _ramBank = _header.RAMBanks == 0 ? 0 : Helpers.GetBits(data, 2) % _header.RAMBanks;
                            }
                            break;

                        case CartridgeSchema.MBCMode.MBC3:
                            //TODO RTC register select
                            _ramBank = Helpers.GetBits(data, 2) % _header.RAMBanks;
                            break;

                        case CartridgeSchema.MBCMode.MBC5:
                            _ramBank = Helpers.GetBits(data, 4) % _header.RAMBanks;
                            break;
                    }
                }
                else if (address < 0x8000)
                {
                    if (_header.BankingMode == CartridgeSchema.MBCMode.MBC1)
                    {
                        _bankMode = (BankingMode)Helpers.GetBits(data, 1);
                    }
                }
            }
            else if (address >= MemorySchema.EXTERNAL_RAM_START && address < MemorySchema.EXTERNAL_RAM_END && _externalRAM.Enabled)
            {
                address = (address - MemorySchema.EXTERNAL_RAM_START) + ((_bankMode == BankingMode.RAMBank ? _ramBank : 0) * CartridgeSchema.RAM_BANK_SIZE);

                if (address < _externalRAM.Length)
                {
                    _externalRAM.WriteByte(data, address);
                }
            }
        }

        private void SetROMBank(int bank)
        {
            _romBank = bank;

            switch (_romBank)
            {
                case 0x0:
                case 0x20:
                case 0x40:
                case 0x60:
                    if ((_header.BankingMode == CartridgeSchema.MBCMode.MBC1 || _romBank == 0) && _header.BankingMode != CartridgeSchema.MBCMode.MBC5)
                    {
                        _romBank++;
                    }

                    break;
            }

            _romBank %= _header.ROMBanks;
        }
    }
}
