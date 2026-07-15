using System;
using System.IO;

namespace GBZEmuLibrary
{
    /// <summary>
    /// Emulates cartridge ROM, external RAM, and memory-bank controller register decoding.
    /// </summary>
    internal class Cartridge : IMemoryUnit
    {
        private enum BankingMode
        {
            ROMBank,
            RAMBank
        }

        public GBCMode GBCMode => _header.GBCMode;
        public bool CustomPalette => _header.CustomPalette;

        private readonly BootROM _bootROM;
        private byte[] _cartMemory;

        private CartridgeHeader _header;
        private ExternalRAM _externalRAM;
        private MBC3RTC _mbc3RTC;

        private const int MBC2RamSize = 512;
        private static readonly byte[] NintendoLogo =
        {
            0xCE, 0xED, 0x66, 0x66, 0xCC, 0x0D, 0x00, 0x0B,
            0x03, 0x73, 0x00, 0x83, 0x00, 0x0C, 0x00, 0x0D,
            0x00, 0x08, 0x11, 0x1F, 0x88, 0x89, 0x00, 0x0E,
            0xDC, 0xCC, 0x6E, 0xE6, 0xDD, 0xDD, 0xD9, 0x99,
            0xBB, 0xBB, 0x67, 0x63, 0x6E, 0x0E, 0xEC, 0xCC,
            0xDD, 0xDC, 0x99, 0x9F, 0xBB, 0xB9, 0x33, 0x3E
        };

        private int _romBank = 1;
        private int _ramBank;
        private byte _mbc3RamRtcSelect;
        private byte _mbc3LatchValue;
        private bool _mbc3LatchPrimed;
        private byte _mbc1Bank1;
        private byte _mbc1Bank2;
        private bool _mbc1Multicart;

        private BankingMode _bankMode;

        /// <summary>
        /// Creates a cartridge whose header can inspect the boot ROM owned by the same emulator instance.
        /// </summary>
        public Cartridge(BootROM bootROM)
        {
            _bootROM = bootROM;
        }

        /// <summary>
        /// Loads cartridge bytes, parses controller metadata, and opens the cartridge's persistent RAM store.
        /// </summary>
        public bool LoadFile(string file, string saveLocation)
        {
            if (File.Exists(file))
            {
                try
                {
                    var cart = File.ReadAllBytes(Path.Combine(Directory.GetCurrentDirectory(), file));
                    _header = new CartridgeHeader(cart, _bootROM);
                    _cartMemory = cart;
                    _mbc1Multicart = IsMBC1Multicart(cart);
                    _mbc3RTC = _header.HasRTC ? new MBC3RTC() : null;

                    var ramSize = _header.BankingMode == CartridgeSchema.MBCMode.MBC2
                        ? MBC2RamSize
                        : CartridgeSchema.RAM_BANK_SIZE * _header.RAMBanks;
                    _externalRAM = new ExternalRAM(saveLocation, Path.GetFileName(file), ramSize);

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

        /// <summary>
        /// Reads from the fixed or switchable ROM windows, external RAM, or the currently selected MBC register window.
        /// </summary>
        public byte ReadByte(int address)
        {
            if (address < MemorySchema.ROM_END)
            {
                if (_header.BankingMode == CartridgeSchema.MBCMode.MBC1)
                {
                    var bank = address < CartridgeSchema.ROM_BANK_SIZE ? GetMBC1LowerROMBank() : GetMBC1UpperROMBank();
                    var offset = address % CartridgeSchema.ROM_BANK_SIZE;
                    return _cartMemory[(bank * CartridgeSchema.ROM_BANK_SIZE) + offset];
                }

                if (address >= CartridgeSchema.ROM_BANK_SIZE)
                {
                    address = (address - CartridgeSchema.ROM_BANK_SIZE) + (_romBank * CartridgeSchema.ROM_BANK_SIZE);
                    address %= _header.Length;
                }

                return _cartMemory[address];
            }

            if (address >= MemorySchema.EXTERNAL_RAM_START && address < MemorySchema.EXTERNAL_RAM_END)
            {
                if (_header.BankingMode == CartridgeSchema.MBCMode.MBC2)
                {
                    return _externalRAM.Enabled
                        ? (byte)(0xF0 | _externalRAM.ReadByte((address - MemorySchema.EXTERNAL_RAM_START) & 0x1FF))
                        : (byte)0xFF;
                }

                if (!TryGetExternalRAMBank(out var ramBank))
                {
                    return _externalRAM.Enabled && _mbc3RTC != null && MBC3RTC.IsRegister(_mbc3RamRtcSelect)
                        ? _mbc3RTC.Read(_mbc3RamRtcSelect)
                        : (byte)0xFF;
                }

                address = (address - MemorySchema.EXTERNAL_RAM_START) + (ramBank * CartridgeSchema.RAM_BANK_SIZE);

                if (address < _externalRAM.Length && _externalRAM.Enabled)
                {
                    return _externalRAM.ReadByte(address);
                }

                return 0xFF;
            }

            throw new IndexOutOfRangeException();
        }

        /// <summary>
        /// Writes external RAM or updates the active memory-bank controller registers for the addressed ROM range.
        /// </summary>
        public void WriteByte(byte data, int address)
        {
            if (address < MemorySchema.ROM_END)
            {
                if (_header.BankingMode == CartridgeSchema.MBCMode.MBC2 && address < 0x4000)
                {
                    if (Helpers.TestBit(address, 8))
                    {
                        SetROMBank(Helpers.GetBits(data, 4));
                    }
                    else
                    {
                        _externalRAM.Enabled = Helpers.GetBits(data, 4) == 0x0A;
                    }

                    return;
                }

                //TODO determine how to get rid of magic numbers
                if (address < 0x2000)
                {
                    switch (_header.BankingMode)
                    {
                        case CartridgeSchema.MBCMode.MBC1:
                        case CartridgeSchema.MBCMode.MBC3:
                        case CartridgeSchema.MBCMode.MBC5:
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
                            _mbc1Bank1 = Helpers.GetBits(data, 5);
                            break;


                        case CartridgeSchema.MBCMode.MBC3:
                            SetROMBank(Helpers.GetBits(data, 7));
                            break;

                        case CartridgeSchema.MBCMode.MBC5:
                            newBank = address < 0x3000
                                ? (_romBank & 0x100) | data
                                : (_romBank & 0x0FF) | ((data & 0x01) << 8);
                            SetROMBank(newBank);
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
                            _mbc1Bank2 = Helpers.GetBits(data, 2);
                            break;

                        case CartridgeSchema.MBCMode.MBC3:
                            // Values 0x00-0x03 select RAM banks; 0x08-0x0C select RTC registers.
                            _mbc3RamRtcSelect = data;
                            break;

                        case CartridgeSchema.MBCMode.MBC5:
                            _ramBank = _header.RAMBanks == 0 ? 0 : Helpers.GetBits(data, 4) % _header.RAMBanks;
                            break;
                    }
                }
                else if (address < 0x8000)
                {
                    if (_header.BankingMode == CartridgeSchema.MBCMode.MBC1)
                    {
                        _bankMode = (BankingMode)Helpers.GetBits(data, 1);
                    }
                    else if (_header.BankingMode == CartridgeSchema.MBCMode.MBC3)
                    {
                        if (_mbc3LatchPrimed && _mbc3LatchValue == 0x00 && data == 0x01)
                        {
                            _mbc3RTC?.Latch();
                        }

                        _mbc3LatchValue = data;
                        _mbc3LatchPrimed = true;
                    }
                }
            }
            else if (address >= MemorySchema.EXTERNAL_RAM_START && address < MemorySchema.EXTERNAL_RAM_END && _externalRAM.Enabled)
            {
                if (_header.BankingMode == CartridgeSchema.MBCMode.MBC2)
                {
                    _externalRAM.WriteByte((byte)(data & 0x0F), (address - MemorySchema.EXTERNAL_RAM_START) & 0x1FF);
                    return;
                }

                if (!TryGetExternalRAMBank(out var ramBank))
                {
                    if (_mbc3RTC != null && MBC3RTC.IsRegister(_mbc3RamRtcSelect))
                    {
                        _mbc3RTC.Write(_mbc3RamRtcSelect, data);
                    }

                    return;
                }

                address = (address - MemorySchema.EXTERNAL_RAM_START) + (ramBank * CartridgeSchema.RAM_BANK_SIZE);

                if (address < _externalRAM.Length)
                {
                    _externalRAM.WriteByte(data, address);
                }
            }
        }

        /// <summary>
        /// Advances a timer-capable MBC3 cartridge from base-speed Game Boy clocks.
        /// </summary>
        public void Update(int clocks)
        {
            _mbc3RTC?.Update(clocks);
        }

        private int GetMBC1LowerROMBank()
        {
            if (_bankMode == BankingMode.ROMBank)
            {
                return 0;
            }

            var shift = _mbc1Multicart ? 4 : 5;
            return NormalizeMBC1ROMBank(_mbc1Bank2 << shift);
        }

        private int GetMBC1UpperROMBank()
        {
            var bank1 = _mbc1Bank1 == 0 ? 1 : _mbc1Bank1;
            if (_mbc1Multicart)
            {
                bank1 &= 0x0F;
            }

            var shift = _mbc1Multicart ? 4 : 5;
            return NormalizeMBC1ROMBank((_mbc1Bank2 << shift) | bank1);
        }

        private int NormalizeMBC1ROMBank(int bank)
        {
            var bankCount = _header.ROMBanks;
            return (bankCount & (bankCount - 1)) == 0
                ? bank & (bankCount - 1)
                : bank % bankCount;
        }

        private int GetMBC1RAMBank()
        {
            if (_bankMode == BankingMode.ROMBank || _header.RAMBanks == 0)
            {
                return 0;
            }

            return _mbc1Bank2 % _header.RAMBanks;
        }

        /// <summary>
        /// Resolves the external RAM bank selected by the active controller, returning false when MBC3 maps an RTC
        /// or invalid selector into the external-memory window instead of RAM.
        /// </summary>
        private bool TryGetExternalRAMBank(out int ramBank)
        {
            switch (_header.BankingMode)
            {
                case CartridgeSchema.MBCMode.MBC1:
                    ramBank = GetMBC1RAMBank();
                    return true;

                case CartridgeSchema.MBCMode.MBC3:
                    if (_mbc3RamRtcSelect > 0x03)
                    {
                        // RTC selectors and undefined values are intentionally treated as non-RAM selections.
                        ramBank = 0;
                        return false;
                    }

                    ramBank = _header.RAMBanks == 0 ? 0 : _mbc3RamRtcSelect % _header.RAMBanks;
                    return true;

                case CartridgeSchema.MBCMode.MBC5:
                    ramBank = _ramBank;
                    return true;

                default:
                    ramBank = 0;
                    return true;
            }
        }

        private bool IsMBC1Multicart(byte[] cart)
        {
            if (_header.BankingMode != CartridgeSchema.MBCMode.MBC1 || cart.Length != 0x100000)
            {
                return false;
            }

            var validLogoCount = 0;
            for (var subCartridge = 0; subCartridge < 4; subCartridge++)
            {
                var logoOffset = (subCartridge * 0x40000) + 0x104;
                var validLogo = true;

                for (var i = 0; i < NintendoLogo.Length; i++)
                {
                    if (cart[logoOffset + i] != NintendoLogo[i])
                    {
                        validLogo = false;
                        break;
                    }
                }

                if (validLogo)
                {
                    validLogoCount++;
                }
            }

            return validLogoCount >= 3;
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
