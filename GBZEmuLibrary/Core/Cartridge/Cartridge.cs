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
        internal CartridgeHeader Header => _header;
        internal byte[] ROMBytes => _cartMemory;
        /// <summary>
        /// Gets whether the loaded cartridge declares an MBC5 rumble motor.
        /// </summary>
        public bool HasRumble => _header != null && _header.HasRumble;

        /// <summary>
        /// Gets the current motor-enable output for the loaded rumble cartridge.
        /// </summary>
        public bool RumbleActive { get; private set; }

        /// <summary>
        /// Gets the fraction of normalized hardware cycles for which the motor was enabled during the most recently
        /// completed frame.
        /// </summary>
        public float RumbleStrength { get; private set; }

        /// <summary>
        /// Raised after each completed rumble-capable hardware frame with its cycle-integrated motor duty.
        /// </summary>
        internal event Action<float> RumbleStrengthUpdated;

        /// <summary>
        /// Raised synchronously when an MBC5 rumble cartridge changes its motor-enable bit.
        /// </summary>
        public event Action<bool> RumbleChanged;

        private readonly BootROM _bootROM;
        private readonly Func<long> _getUnixTimestamp;
        [SaveStateIgnore]
        private byte[] _cartMemory;

        [SaveStateIgnore]
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
        private int _rumbleOnCycles;
        private int _rumbleOffCycles;

        private BankingMode _bankMode;

        /// <summary>
        /// Creates a cartridge whose header can inspect the boot ROM owned by the same emulator instance.
        /// </summary>
        public Cartridge(BootROM bootROM, Func<long> getUnixTimestamp = null)
        {
            _bootROM = bootROM;
            _getUnixTimestamp = getUnixTimestamp ?? (() => DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        }

        /// <summary>
        /// Loads cartridge bytes from a file and uses its full filename as the persistent cartridge identity.
        /// </summary>
        public bool LoadFile(string file, string saveLocation)
        {
            if (!File.Exists(file))
            {
                return false;
            }

            var cart = File.ReadAllBytes(file);
            CartridgeInspection.Inspect(cart);
            LoadBytes(cart, Path.GetFileName(file), saveLocation, true);
            return true;
        }

        /// <summary>
        /// Loads one privately owned cartridge image and opens persistent RAM under the supplied logical identity.
        /// </summary>
        internal void LoadBytes(byte[] cart, string romIdentity, string saveLocation, bool structureValidated)
        {
            if (cart == null)
            {
                throw new ArgumentNullException(nameof(cart));
            }

            ResetRumbleOutput();
            _header = new CartridgeHeader(cart, _bootROM, structureValidated);
            _cartMemory = cart;
            _mbc1Multicart = IsMBC1Multicart(cart);
            _mbc3RTC = _header.HasRTC ? new MBC3RTC() : null;

            var ramSize = _header.BankingMode == CartridgeSchema.MBCMode.MBC2
                ? MBC2RamSize
                : CartridgeSchema.RAM_BANK_SIZE * _header.RAMBanks;
            _externalRAM = new ExternalRAM(saveLocation, romIdentity, ramSize);
            if (_header.BankingMode == CartridgeSchema.MBCMode.NoMBC && _header.RAMBanks > 0)
            {
                _externalRAM.Enabled = true;
            }

            if (_mbc3RTC != null)
            {
                var rtcData = _externalRAM.ReadRTCTrailer();
                if (rtcData != null)
                {
                    _mbc3RTC.Load(rtcData, _getUnixTimestamp());
                }
            }
        }

        /// <summary>
        /// Turns off cartridge output, persists RTC state, and closes external RAM. Safe to call repeatedly.
        /// </summary>
        public void Terminate()
        {
            try
            {
                if (_externalRAM == null)
                {
                    return;
                }

                if (_mbc3RTC != null)
                {
                    _externalRAM.WriteRTCTrailer(_mbc3RTC.Save(_getUnixTimestamp()));
                }

                _externalRAM.Dispose();
                _externalRAM = null;
            }
            finally
            {
                // Publish the off transition after storage cleanup so a host callback cannot prevent save flushing.
                SetRumble(false);
                RumbleStrength = 0f;
                _rumbleOnCycles = 0;
                _rumbleOffCycles = 0;
            }
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
                            var ramBankMask = _header.HasRumble ? 0x07 : 0x0F;
                            _ramBank = _header.RAMBanks == 0 ? 0 : (data & ramBankMask) % _header.RAMBanks;
                            SetRumble(_header.HasRumble && Helpers.TestBit(data, 3));
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
        /// Writes a physical cartridge-RAM bank without changing the guest-visible mapper registers or RAM-enable
        /// latch. GameShark/Action Replay bank codes use this path at VBlank.
        /// </summary>
        internal void WriteExternalRamBanked(byte data, int address, int bank)
        {
            if (_externalRAM == null || address < MemorySchema.EXTERNAL_RAM_START ||
                address >= MemorySchema.EXTERNAL_RAM_END || bank < 0)
            {
                return;
            }

            if (_header.BankingMode == CartridgeSchema.MBCMode.MBC2)
            {
                if (bank == 0)
                {
                    _externalRAM.WriteByte((byte)(data & 0x0F), (address - MemorySchema.EXTERNAL_RAM_START) & 0x1FF);
                }

                return;
            }

            var physicalAddress = address - MemorySchema.EXTERNAL_RAM_START +
                                  bank * CartridgeSchema.RAM_BANK_SIZE;
            if (physicalAddress < _externalRAM.Length)
            {
                _externalRAM.WriteByte(data, physicalAddress);
            }
        }

        /// <summary>
        /// Advances cartridge timers and accumulates rumble duty from normalized hardware clocks.
        /// </summary>
        public void Update(int clocks)
        {
            _mbc3RTC?.Update(clocks);
            if (!HasRumble)
            {
                return;
            }

            if (RumbleActive)
            {
                _rumbleOnCycles += clocks;
            }
            else
            {
                _rumbleOffCycles += clocks;
            }

            if (_rumbleOnCycles + _rumbleOffCycles >= Display.CLOCK_CYCLES_PER_FRAME)
            {
                CompleteRumbleFrame();
            }
        }

        /// <summary>
        /// Publishes the completed frame's motor duty and begins a new integration window.
        /// </summary>
        private void CompleteRumbleFrame()
        {
            var totalCycles = _rumbleOnCycles + _rumbleOffCycles;
            RumbleStrength = totalCycles > 0
                ? (float)_rumbleOnCycles / totalCycles
                : 0f;
            _rumbleOnCycles = 0;
            _rumbleOffCycles = 0;
            RumbleStrengthUpdated?.Invoke(RumbleStrength);
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
            var bankCount = GetEffectiveROMBankCount();
            return (bankCount & (bankCount - 1)) == 0
                ? bank & (bankCount - 1)
                : bank % bankCount;
        }

        /// <summary>
        /// Uses all complete physical ROM banks when homebrew under-declares its cartridge size.
        /// </summary>
        private int GetEffectiveROMBankCount()
        {
            var physicalBankCount = _header.Length / CartridgeSchema.ROM_BANK_SIZE;
            return Math.Max(_header.ROMBanks, physicalBankCount);
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

            _romBank %= GetEffectiveROMBankCount();
        }

        /// <summary>
        /// Updates the emulated cartridge motor output and publishes only actual state transitions.
        /// </summary>
        private void SetRumble(bool active)
        {
            if (RumbleActive == active)
            {
                return;
            }

            RumbleActive = active;
            RumbleChanged?.Invoke(active);
        }

        /// <summary>
        /// Clears both raw and frame-integrated motor output before loading a new cartridge.
        /// </summary>
        private void ResetRumbleOutput()
        {
            SetRumble(false);
            RumbleStrength = 0f;
            _rumbleOnCycles = 0;
            _rumbleOffCycles = 0;
        }

        /// <summary>
        /// Synchronizes a host motor after a state restore changed the cartridge's serialized output latch.
        /// </summary>
        internal void PublishRestoredRumbleState(bool previousState)
        {
            if (previousState != RumbleActive)
            {
                RumbleChanged?.Invoke(RumbleActive);
            }

            if (HasRumble)
            {
                RumbleStrengthUpdated?.Invoke(RumbleStrength);
            }
        }
    }
}
