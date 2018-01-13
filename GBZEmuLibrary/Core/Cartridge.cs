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

        private byte _romBank = 1;
        private int _ramBank;

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

                            switch (Helpers.GetBits(data, 4))
                            {
                                case 0xA:
                                    _ramEnabled = true;
                                    break;
                                case 0x0:
                                    _ramEnabled = false;
                                    break;
                            }
                            break;
                        default:
                            throw new ArgumentOutOfRangeException();
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
                            var newBank = _romBank;

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
                                var newBank = _romBank;

                                Helpers.ResetHighBits(ref newBank, 3);
                                Helpers.ResetLowBits(ref data, 5);

                                newBank |= data;

                                SetROMBank(newBank);
                            }
                            else
                            {
                                _ramBank = Helpers.GetBits(data, 2);
                            }
                            break;
                        case MBCMode.MBC3:
                            //TODO RTC register select
                            _ramBank = Helpers.GetBits(data, 2);
                            break;
                        default:
                            throw new ArgumentOutOfRangeException();
                    }
                }
                else if (address < 0x8000)
                {
                    if (_bankingMode == MBCMode.MBC1)
                    {
                        _romBankingEnabled = !Helpers.TestBit(data, 1);
                        _ramBank = _romBankingEnabled ? 0 : _ramBank;
                    }
                }
            }
            else if (address >= MemorySchema.EXTERNAL_RAM_START && address < MemorySchema.EXTERNAL_RAM_END && _ramEnabled)
            {
                _externalRAM[(address - MemorySchema.EXTERNAL_RAM_START) + (_ramBank * CartridgeSchema.RAM_BANK_SIZE)] = data;
            }
        }

        private void SetROMBank(byte bank)
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
        }
    }
}
