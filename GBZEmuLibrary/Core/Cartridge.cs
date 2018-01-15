using System;
using System.IO;
using System.Linq;

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
        private byte[] _externalRAM;

        // Cart Info
        private MBCMode _bankingMode = MBCMode.NO_MBC;

        private int _numRomBanks = 1;
        private int _romBank = 1;

        private int _numRamBanks = 1;
        private int _ramBank;

        private bool _ramEnabled;
        private bool _romBankingEnabled = true;

        private int _cartLength;

        public bool LoadFile(string file)
        {
            //TODO check file exists

            try
            {
                var cart = File.ReadAllBytes(Path.Combine(Directory.GetCurrentDirectory(), file));
                _cartLength = cart.Length;

                Array.Copy(cart, _cartMemory, _cartLength);

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
                    case 0x0F:
                    case 0x10:
                    case 0x11:
                    case 0x12:
                    case 0x13:
                        _bankingMode = MBCMode.MBC3;
                        break;
                    default:
                        throw new NotImplementedException($"Unsupported MBC Mode: {_cartMemory[CartridgeSchema.MBC_MODE_LOC]}");
                }

                //TODO mathematically replace this?
                switch (_cartMemory[CartridgeSchema.ROM_BANK_NUM_LOC])
                {
                    case 0x01:
                        _numRomBanks = 4;
                        break;
                    case 0x02:
                        _numRomBanks = 8;
                        break;
                    case 0x03:
                        _numRomBanks = 16;
                        break;
                    case 0x04:
                        _numRomBanks = 32;
                        break;
                    case 0x05:
                        _numRomBanks = _bankingMode == MBCMode.MBC1 ? 63 : 64;
                        break;
                    case 0x06:
                        _numRomBanks = _bankingMode == MBCMode.MBC1 ? 125 : 128;
                        break;
                    case 0x07:
                        _numRomBanks = 256;
                        break;
                    case 0x08:
                        _numRomBanks = 512;
                        break;
                    case 0x52:
                        _numRomBanks = 72;
                        break;
                    case 0x53:
                        _numRomBanks = 80;
                        break;
                    case 0x54:
                        _numRomBanks = 96;
                        break;
                }

                switch (_cartMemory[CartridgeSchema.RAM_BANK_NUM_LOC])
                {
                    case 0x00:
                        _numRamBanks = 0;
                        break;
                    case 0x01:
                    case 0x02:
                        _numRamBanks = 1;
                        break;
                    case 0x03:
                        _numRamBanks = 4;
                        break;
                    case 0x04:
                        _numRamBanks = 16;
                        break;
                    case 0x05:
                        _numRamBanks = 8;
                        break;
                }

                _externalRAM = Enumerable.Repeat<byte>(0xFF, CartridgeSchema.RAM_BANK_SIZE * _numRamBanks).ToArray();

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
                if (address >= CartridgeSchema.ROM_BANK_SIZE)
                {
                    address = (address - CartridgeSchema.ROM_BANK_SIZE) + (_romBank * CartridgeSchema.ROM_BANK_SIZE);
                    address %= _cartLength; //TODO determine if this is correct
                }

                return _cartMemory[address];
            }

            if (address >= MemorySchema.EXTERNAL_RAM_START && address < MemorySchema.EXTERNAL_RAM_END)
            {
                address = (address - MemorySchema.EXTERNAL_RAM_START) + ((_romBankingEnabled ? 0 : _ramBank) * CartridgeSchema.RAM_BANK_SIZE);

                if (address < _externalRAM.Length && _ramEnabled)
                {
                    return _externalRAM[address];
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
                    switch (_bankingMode)
                    {
                        case MBCMode.MBC1:
                        case MBCMode.MBC2:
                        case MBCMode.MBC3:
                            if (_bankingMode == MBCMode.MBC2 && Helpers.TestBit((ushort)address, 4)) //TODO remove magic number (but 4 is a special bit that has to be set for mbc2 ram writing mode)
                            {
                                return;
                            }

                            _ramEnabled = Helpers.GetBits(data, 4) == 0x0A;
                            break;
                    }
                }
                else if (address < 0x4000)
                {
                    switch (_bankingMode)
                    {
                        case MBCMode.NO_MBC: //TODO figure out why NO_MBC is writing data
                            break;
                        case MBCMode.MBC1:
                            //Override the lower 5 bits of the ROM Bank
                            int newBank = _romBank;

                            Helpers.ResetLowBits(ref newBank, 5);
                            newBank |= Helpers.GetBits(data, 5);

                            SetROMBank(newBank);
                            break;
                        case MBCMode.MBC2:
                            SetROMBank(Helpers.GetBits(data, 4));
                            break;
                        case MBCMode.MBC3:
                            SetROMBank(Helpers.GetBits(data, 7));
                            break;
                        default:
                            throw new ArgumentOutOfRangeException();
                    }
                }
                else if (address < 0x6000)
                {
                    switch (_bankingMode)
                    {
                        case MBCMode.MBC1:
                            if (_romBankingEnabled)
                            {
                                //Override the bits 5-6 of the romBank
                                int newBank = _romBank;

                                Helpers.ResetHighBits(ref newBank, 3);
                                newBank |= (byte)(data << 5);

                                SetROMBank(newBank);
                            }
                            else
                            {
                                _ramBank = Helpers.GetBits(data, 2) % _numRamBanks;
                            }
                            break;
                        case MBCMode.MBC3:
                            //TODO RTC register select
                            _ramBank = Helpers.GetBits(data, 2) % _numRamBanks;
                            break;
                    }
                }
                else if (address < 0x8000)
                {
                    if (_bankingMode == MBCMode.MBC1)
                    {
                        _romBankingEnabled = !Helpers.TestBit(data, 1);
                    }
                }
            }
            else if (address >= MemorySchema.EXTERNAL_RAM_START && address < MemorySchema.EXTERNAL_RAM_END && _ramEnabled)
            {
                address = (address - MemorySchema.EXTERNAL_RAM_START) + ((_romBankingEnabled ? 0 :_ramBank) * CartridgeSchema.RAM_BANK_SIZE);

                if (address < _externalRAM.Length)
                {
                    _externalRAM[address] = data;
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
                    if (_bankingMode == MBCMode.MBC1 || _romBank == 0)
                    {
                        _romBank++;
                    }

                    break;
            }

            _romBank %= _numRomBanks;
        }
    }
}
