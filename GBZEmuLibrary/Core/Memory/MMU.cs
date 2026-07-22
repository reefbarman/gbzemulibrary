using System;
using System.Collections.Generic;

namespace GBZEmuLibrary
{
    /// <summary>
    /// Routes the Game Boy address space to cartridges, hardware devices, and fallback memory.
    /// </summary>
    internal class MMU
    {
        public Func<byte> GetSpeedState;
        public Action<byte> OnPendingSpeedSwitch;

        public bool InBootROM => _mainMemory.InBootROM;

        private readonly Dictionary<int, IMemoryUnit> _memoryUnitLookup = new Dictionary<int, IMemoryUnit>();

        private readonly WorkRAM _workRAM = new WorkRAM();
        private readonly APU _apu;
        private readonly BootROM _bootROM;
        private readonly GPU _gpu;
        private readonly SerialRegisters _serialRegisters;
        private readonly DMAController _dmaController;
        [SaveStateIgnore]
        private readonly CheatCollection _cheats;

        private readonly MainMemory _mainMemory = new MainMemory();
        private GBCMode _mode;
        private HardwareModel _hardwareModel;

        /// <summary>
        /// Builds the fixed address-to-device lookup used by CPU, DMA, and debugger memory accesses.
        /// </summary>
        public MMU(Cartridge cart, GPU gpu, Timer timer, DivideRegister divideRegister, Joypad joypad, APU apu, SerialRegisters serialRegisters, BootROM bootROM, CheatCollection cheats, MessageBus messageBus)
        {
            _apu = apu;
            _bootROM = bootROM;
            _cheats = cheats;
            _gpu = gpu;
            _serialRegisters = serialRegisters;
            _dmaController = new DMAController(messageBus);

            var memoryUnits = new List<IMemoryUnit>
            {
                cart, gpu, _workRAM, joypad, serialRegisters, divideRegister, timer, apu, _dmaController, new UnmappedIO()
            };

            messageBus.OnReadByte = ReadByte;
            messageBus.OnWriteByte = WriteByte;
            messageBus.OnReadOamDmaSourceByte = ReadOamDmaSourceByte;
            messageBus.OnVBlank = ApplyGameSharkWrites;

            for (var address = 0; address < MemorySchema.MAX_RAM_SIZE; address++)
            {
                foreach (var memoryUnit in memoryUnits)
                {
                    if (memoryUnit.CanReadWriteByte(address))
                    {
                        _memoryUnitLookup[address] = memoryUnit;
                        break;
                    }
                }

                if (!_memoryUnitLookup.ContainsKey(address))
                {
                    _memoryUnitLookup[address] = _mainMemory;
                }
            }
        }

        /// <summary>
        /// Initializes mode-dependent memory and I/O register behavior.
        /// </summary>
        public void Init(GBCMode mode, HardwareModel hardwareModel)
        {
            _mode = mode;
            _hardwareModel = hardwareModel;
            _dmaController.Init(mode);
            _workRAM.Init(mode);
            // APU hardware must be selected before Emulator.Start applies its post-boot reset profile.
            _apu.Init(mode, hardwareModel);
            _serialRegisters.Init(mode);
        }

        /// <summary>
        /// Reads an address through its owning memory unit and applies register-specific read behavior.
        /// </summary>
        public byte ReadByte(int address)
        {
            // CGB-only I/O registers are inaccessible in DMG mode and expose the unused-register pull-up value.
            if (_mode == GBCMode.NoGBC &&
                address >= MemorySchema.CGB_IO_REGISTERS_START &&
                address < MemorySchema.CGB_IO_REGISTERS_END)
            {
                return 0xFF;
            }

            if (address < MemorySchema.ROM_END)
            {
                if (_mainMemory.InBootROM)
                {
                    if (address < MemorySchema.BOOT_ROM_SECTION_1_END || _bootROM.IsColorFamilySelected && address >= MemorySchema.BOOT_ROM_SECTION_2_START && address < MemorySchema.BOOT_ROM_SECTION_2_END)
                    {
                        return _bootROM.Bytes[address];
                    }
                }
            }

            if (address == MemorySchema.CPU_SPEED_SWITCH_REGISTER)
            {
                return (byte)GetSpeedState?.Invoke();
            }

            if (_memoryUnitLookup.ContainsKey(address))
            {
                var value = _memoryUnitLookup[address].ReadByte(address);

                if (address < MemorySchema.ROM_END)
                {
                    value = _cheats.ApplyGameGenie(address, value);
                }

                // IF only implements the five interrupt request bits; unused bits are pulled high on reads.
                return address == MemorySchema.INTERRUPT_REQUEST_REGISTER
                    ? (byte)(value | 0xE0)
                    : value;
            }

            throw new IndexOutOfRangeException();
        }

        /// <summary>
        /// Reads a CPU bus cycle, returning the DMA-driven value when OAM DMA owns the addressed bus.
        /// </summary>
        internal byte ReadByteForCpu(int address)
        {
            if (!IsCpuAccessBlockedByOamDma(address))
            {
                return ReadByte(address);
            }

            return address >= MemorySchema.SPRITE_ATTRIBUTE_TABLE_START &&
                   address < MemorySchema.SPRITE_ATTRIBUTE_TABLE_END
                ? (byte)0xFF
                : _dmaController.OamDmaBusValue;
        }

        /// <summary>
        /// Writes a CPU bus cycle unless DMG OAM DMA owns the target bus. HRAM and FF46 remain accessible.
        /// </summary>
        internal void WriteByteForCpu(byte data, int address)
        {
            if (!IsCpuAccessBlockedByOamDma(address))
            {
                if ((address >= MemorySchema.VIDEO_RAM_START && address < MemorySchema.VIDEO_RAM_END) ||
                    (address >= MemorySchema.SPRITE_ATTRIBUTE_TABLE_START &&
                     address < MemorySchema.SPRITE_ATTRIBUTE_TABLE_END))
                {
                    _gpu.WriteByteForCpu(data, address);
                }
                else
                {
                    WriteByte(data, address);
                }
            }
        }

        /// <summary>
        /// Writes CPU-visible PPU memory after the caller has sampled OAM-DMA bus ownership for the cycle.
        /// </summary>
        internal void WritePpuByteForCpu(byte data, int address)
        {
            _gpu.WriteByteForCpu(data, address);
        }

        /// <summary>
        /// Returns whether a CPU access is blocked by active OAM DMA on the selected hardware bus layout.
        /// </summary>
        internal bool IsCpuAccessBlockedByOamDma(int address)
        {
            if (!_dmaController.IsOamDmaActive || address == MemorySchema.DMA_REGISTER)
            {
                return false;
            }

            if (address >= MemorySchema.HIGH_RAM_START && address < MemorySchema.HIGH_RAM_END)
            {
                return false;
            }

            if (address >= MemorySchema.SPRITE_ATTRIBUTE_TABLE_START &&
                address < MemorySchema.SPRITE_ATTRIBUTE_TABLE_END)
            {
                return true;
            }

            var sourceHigh = _dmaController.ActiveOamDmaSourceHigh;
            if (_mode == GBCMode.NoGBC)
            {
                // DMG has a dedicated VRAM bus. A VRAM-sourced transfer therefore leaves the shared
                // cartridge/WRAM bus available; every other source occupies that shared bus instead.
                var sourceUsesVramBus = sourceHigh >= 0x80 && sourceHigh < 0xA0;
                var addressUsesVramBus = address >= MemorySchema.VIDEO_RAM_START &&
                                         address < MemorySchema.VIDEO_RAM_END;
                return sourceUsesVramBus == addressUsesVramBus;
            }

            if (sourceHigh < 0x80 || sourceHigh >= 0xA0 && sourceHigh < 0xC0 || sourceHigh >= 0xE0)
            {
                // ROM and cartridge RAM share the CGB external cartridge bus.
                return address < MemorySchema.ROM_END ||
                       address >= MemorySchema.EXTERNAL_RAM_START && address < MemorySchema.EXTERNAL_RAM_END;
            }

            if (sourceHigh < 0xA0)
            {
                return address >= MemorySchema.VIDEO_RAM_START && address < MemorySchema.VIDEO_RAM_END;
            }

            // Work RAM and its echo share the other CGB CPU bus.
            return address >= MemorySchema.WORK_RAM_START && address < MemorySchema.ECHO_RAM_SWITCHABLE_END;
        }

        /// <summary>
        /// Advances DMA engines from the raw CPU clock domain, including CGB double speed.
        /// </summary>
        internal void UpdateDma(int cycles)
        {
            _dmaController.Update(cycles);
        }

        /// <summary>
        /// Gets whether an active CGB HBlank DMA block currently owns the CPU memory bus.
        /// </summary>
        internal bool IsCpuStalledByHBlankDma => _dmaController.IsCpuStalledByHBlankDma;

        /// <summary>
        /// Reads an OAM DMA source through the mapped memory device without CPU-side DMA blocking.
        /// </summary>
        private byte ReadOamDmaSourceByte(int address)
        {
            if (address >= MemorySchema.VIDEO_RAM_START && address < MemorySchema.VIDEO_RAM_END)
            {
                return _gpu.ReadOamDmaSourceByte(address);
            }

            return ReadByte(address);
        }

        /// <summary>
        /// Applies enabled GameShark/Action Replay RAM writes once at the VBlank request boundary.
        /// Banked cartridge and CGB work RAM are addressed directly so guest-visible mapper state is not disturbed.
        /// </summary>
        private void ApplyGameSharkWrites()
        {
            var entries = _cheats.GameSharkEntries;
            for (var index = 0; index < entries.Count; index++)
            {
                var entry = entries[index];
                if (!entry.Enabled)
                {
                    continue;
                }

                if (entry.Bank.HasValue)
                {
                    if (entry.BankType == CheatBankType.CartridgeRam)
                    {
                        ((Cartridge)_memoryUnitLookup[entry.Address]).WriteExternalRamBanked(
                            entry.Value,
                            entry.Address,
                            entry.Bank.Value);
                        continue;
                    }

                    if (_workRAM.IsSwitchableAddress(entry.Address))
                    {
                        if (_mode == GBCMode.NoGBC)
                        {
                            if (entry.Bank.Value == 0)
                            {
                                WriteByte(entry.Value, entry.Address);
                            }

                            continue;
                        }

                        _workRAM.WriteBankedByte(entry.Value, entry.Address, entry.Bank.Value);
                        continue;
                    }

                    // GameShark bank prefixes describe CGB work-RAM banks. Other memory regions are bank zero.
                    if (entry.Bank.Value != 0)
                    {
                        continue;
                    }
                }

                WriteByte(entry.Value, entry.Address);
            }
        }

        /// <summary>
        /// Writes an address through its owning memory unit while preserving device side effects.
        /// </summary>
        public void WriteByte(byte data, int address)
        {
            // DMG hardware ignores writes to the CGB-only I/O window; FF50 remains the shared boot-ROM disable latch.
            if (_mode == GBCMode.NoGBC &&
                address >= MemorySchema.CGB_IO_REGISTERS_START &&
                address < MemorySchema.CGB_IO_REGISTERS_END &&
                address != MemorySchema.BOOT_ROM_DISABLE_REGISTER)
            {
                return;
            }

            if (address == MemorySchema.CPU_SPEED_SWITCH_REGISTER)
            {
                OnPendingSpeedSwitch?.Invoke(data);
                return;
            }

            // IF stores only the five implemented request bits; unused bits are supplied by the read path.
            if (address == MemorySchema.INTERRUPT_REQUEST_REGISTER)
            {
                data &= 0x1F;
            }

            if (_memoryUnitLookup.ContainsKey(address))
            {
                _memoryUnitLookup[address].WriteByte(data, address);

                if (address == MemorySchema.BOOT_ROM_DISABLE_REGISTER && _mode == GBCMode.GBCCompatibility)
                {
                    _gpu.EnterDmgCompatibilityMode();
                }

                return;
            }

            throw new IndexOutOfRangeException();
        }

        /// <summary>
        /// Restores post-boot register defaults or enables the selected boot-ROM overlay.
        /// </summary>
        public void Reset(bool usingBootROM)
        {
            _mainMemory.InBootROM = usingBootROM;
            _serialRegisters.Reset(usingBootROM);

            if (usingBootROM)
            {
                return;
            }

            // SGB2 starts with both joypad selection lines inactive; handheld profiles start with neither selected.
            WriteByte(_hardwareModel == HardwareModel.Sgb2 ? (byte)0x30 : (byte)0x00, MemorySchema.JOYPAD_REGISTER);
            WriteByte(0x00, 0xFF05);
            WriteByte(0x00, 0xFF06);
            WriteByte(0x00, 0xFF07);
            WriteByte(0x01, MemorySchema.INTERRUPT_REQUEST_REGISTER);
            WriteByte(0x91, 0xFF40);
            WriteByte(0x00, 0xFF42);
            WriteByte(0x00, 0xFF43);
            WriteByte(0x00, 0xFF45);
            WriteByte(0xFC, 0xFF47);
            WriteByte(0xFF, 0xFF48);
            WriteByte(0xFF, 0xFF49);
            WriteByte(0x00, 0xFF4A);
            WriteByte(0x00, 0xFF4B);
            WriteByte(0x00, 0xFFFF);
        }
    }
}
