using System;
using System.IO;

namespace GBZEmuLibrary
{
    internal class Cartridge
    {
        private enum MBCMode
        {
            NO_MBC,
            MBC1,
            MBC2,
            MBC3
        } 

        private readonly byte[] _cartMemory = new byte[CartridgeSchema.MAX_CART_SIZE];
        private readonly byte[] _externalRAM = new byte[CartridgeSchema.MAX_CART_RAM_SIZE];

        // Cart Info
        private MBCMode _bankingMode = MBCMode.NO_MBC;

        private int _romBank = 1;
        private int _ramBank = 0;

        private bool _ramEnabled;
        private bool _romBankingEnabled = true;

        public bool LoadFile(string file)
        {
            //TODO check file exists

            try
            {
                var cart = File.ReadAllBytes(Path.Combine(Directory.GetCurrentDirectory(), file));
                Array.Copy(cart, _cartMemory, cart.Length);

                switch (_cartMemory[CartridgeSchema.MBC_MODE_LOC])
                {
                    case 0x00:
                        _bankingMode = MBCMode.NO_MBC;
                        break;
                    case 0x01:
                    case 0x02:
                    case 0x03:
                        _bankingMode = MBCMode.MBC1;
                        break;
                    case 0x05:
                    case 0x06:
                        _bankingMode = MBCMode.MBC2;
                        break;
                    case 0x08:
                    case 0x09:
                        _bankingMode = MBCMode.NO_MBC;
                        break;
                    default:
                        throw new NotImplementedException($"Unsupported MBC Mode: {_cartMemory[CartridgeSchema.MBC_MODE_LOC]}");
                }

                return true;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
            }

            return false;
        }

        public byte ReadByte(int address)
        {
            if (address < MemorySchema.ROM_END)
            {
                return address >= CartridgeSchema.ROM_BANK_SIZE ? _cartMemory[(address - CartridgeSchema.ROM_BANK_SIZE) + (_romBank * CartridgeSchema.ROM_BANK_SIZE)] : _cartMemory[address];
            }

            if (address >= MemorySchema.EXTERNAL_RAM_START && address < MemorySchema.EXTERNAL_RAM_END)
            {
                return _externalRAM[(address - MemorySchema.EXTERNAL_RAM_START) + (_ramBank * CartridgeSchema.RAM_BANK_SIZE)];
            }

            throw new IndexOutOfRangeException();
        }

        public void WriteByte(byte data, int address)
        {
            if (_bankingMode == MBCMode.MBC3)
            {
                throw new NotImplementedException();
            }

            if (address < MemorySchema.ROM_END)
            {
                //TODO determine how to get rid of magic numbers
                if (address < 0x2000)
                {
                    if (_bankingMode == MBCMode.MBC1 || _bankingMode == MBCMode.MBC2)
                    {
                        if (_bankingMode == MBCMode.MBC2 && Helpers.TestBit((ushort)address, 4)) //TODO remove magic number (but 4 is a special bit that has to be set for mbc2 ram writing mode)
                        {
                            return;
                        }

                        switch (data & 0xF)
                        {
                            case 0xA:
                                _ramEnabled = true;
                                break;
                            case 0x0:
                                _ramEnabled = false;
                                break;
                        }
                    }
                }
                else if (address < 0x4000)
                {
                    if (_bankingMode == MBCMode.MBC1 || _bankingMode == MBCMode.MBC2)
                    {
                        if (_bankingMode == MBCMode.MBC2)
                        {
                            SetROMBank(data & 0xF);
                            return;
                        }

                        SetROMBank((_romBank & 224) | (data & 31));
                    }
                }
                else if (address < 0x6000)
                {
                    if (_bankingMode == MBCMode.MBC1)
                    {
                        if (_romBankingEnabled)
                        {
                            SetROMBank((_romBank & 31) | (data & 224));
                        }
                        else
                        {
                            _ramBank = data & 0x3;
                        }
                    }
                }
                else if (address < 0x8000)
                {
                    if (_bankingMode == MBCMode.MBC1)
                    {
                        _romBankingEnabled = (data & 0x1) == 0 ? true : false;
                        _ramBank = _romBankingEnabled ? 0 : _ramBank;
                    }
                }
            }
            else if (address >= MemorySchema.EXTERNAL_RAM_START && address < MemorySchema.EXTERNAL_RAM_END && _ramEnabled)
            {
                _externalRAM[(address - MemorySchema.EXTERNAL_RAM_START) + (_ramBank * CartridgeSchema.RAM_BANK_SIZE)] = data;
            }
        }

        private void SetROMBank(int bank)
        {
            _romBank = bank == 0 ? 1 : bank;
        }
    }
}
