using System;
using System.Collections.Generic;

namespace GBZEmuLibrary
{
    /// <summary>
    /// Identifies the device selected by the MMU's fixed address-routing table.
    /// </summary>
    internal enum MemoryAddressOwner
    {
        Cartridge,
        Gpu,
        WorkRam,
        Joypad,
        Serial,
        Divider,
        Timer,
        Apu,
        Dma,
        Compatibility,
        UnmappedIo,
        MainMemory
    }

    /// <summary>
    /// Routes the Game Boy address space to cartridges, hardware devices, and fallback memory.
    /// </summary>
    internal class MMU
    {
        public Func<byte> GetSpeedState;
        public Action<byte> OnPendingSpeedSwitch;

        public bool InBootROM => _mainMemory.InBootROM;
        internal CompatibilityModeRegisters CompatibilityModeRegisters => _compatibilityModeRegisters;
        internal DMAController DmaController => _dmaController;

        private readonly Dictionary<int, IMemoryUnit> _memoryUnitLookup = new Dictionary<int, IMemoryUnit>();

        private readonly WorkRAM _workRAM = new WorkRAM();
        private readonly APU _apu;
        private readonly BootROM _bootROM;
        private readonly GPU _gpu;
        private readonly SerialRegisters _serialRegisters;
        private readonly DMAController _dmaController;
        private readonly CompatibilityModeRegisters _compatibilityModeRegisters = new CompatibilityModeRegisters();
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
                cart, gpu, _workRAM, joypad, serialRegisters, divideRegister, timer, apu, _dmaController,
                _compatibilityModeRegisters, new UnmappedIO()
            };

            messageBus.OnReadCgbDmaSourceByte = ReadCgbDmaSourceByte;
            messageBus.OnWriteCgbDmaDestinationByte = WriteCgbDmaDestinationByte;
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
            _compatibilityModeRegisters.Init(
                hardwareModel,
                hardwareModel == HardwareModel.CgbE && mode == GBCMode.GBCCompatibility);
            // APU hardware must be selected before Emulator.Start applies its post-boot reset profile.
            _apu.Init(mode, hardwareModel);
            _serialRegisters.Init(mode);
        }

        /// <summary>
        /// Reports the fixed routing-table owner for focused internal address-map tests.
        /// </summary>
        internal MemoryAddressOwner GetAddressOwner(int address)
        {
            var memoryUnit = _memoryUnitLookup[address];
            if (memoryUnit is Cartridge) return MemoryAddressOwner.Cartridge;
            if (memoryUnit is GPU) return MemoryAddressOwner.Gpu;
            if (memoryUnit is WorkRAM) return MemoryAddressOwner.WorkRam;
            if (memoryUnit is Joypad) return MemoryAddressOwner.Joypad;
            if (memoryUnit is SerialRegisters) return MemoryAddressOwner.Serial;
            if (memoryUnit is DivideRegister) return MemoryAddressOwner.Divider;
            if (memoryUnit is Timer) return MemoryAddressOwner.Timer;
            if (memoryUnit is APU) return MemoryAddressOwner.Apu;
            if (memoryUnit is DMAController) return MemoryAddressOwner.Dma;
            if (memoryUnit is CompatibilityModeRegisters) return MemoryAddressOwner.Compatibility;
            if (memoryUnit is UnmappedIO) return MemoryAddressOwner.UnmappedIo;
            return MemoryAddressOwner.MainMemory;
        }

        /// <summary>
        /// Reads an address through its owning memory unit and applies register-specific read behavior.
        /// </summary>
        public byte ReadByte(int address)
        {
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
        /// Begins a CPU read and captures the OAM-DMA ownership and bus value visible at T1.
        /// </summary>
        internal CpuBusTransaction BeginCpuRead(int address, CpuMachineCycleKind kind)
        {
            var blockedByOamDma = IsCpuAccessBlockedByOamDma(address);
            var readsAtCompletion = !blockedByOamDma && ReadsDeviceStateAtCompletion(address);
            var readDataLatchedBeforeCompletion = !blockedByOamDma && !readsAtCompletion;
            var readDataBeforeCompletion = readDataLatchedBeforeCompletion
                ? IsCgbIoReadBlocked(address) ? (byte)0xFF : ReadByte(address)
                : (byte)0;
            return new CpuBusTransaction(
                kind,
                (ushort)address,
                0,
                blockedByOamDma,
                blockedByOamDma ? ReadOamDmaBlockedCpuValue(address) : (byte)0,
                readDataLatchedBeforeCompletion,
                readDataBeforeCompletion);
        }

        /// <summary>
        /// Begins a CPU write, captures T1 OAM-DMA ownership, and applies evidence-backed device-owner latches.
        /// </summary>
        internal CpuBusTransaction BeginCpuWrite(byte data, int address)
        {
            var blockedByOamDma = IsCpuAccessBlockedByOamDma(address);
            var memoryUnit = _memoryUnitLookup[address];
            var writeDataLatchedBeforeCompletion = !blockedByOamDma &&
                                                   WritesDeviceDataBeforeCompletion(memoryUnit, address);
            if (writeDataLatchedBeforeCompletion)
            {
                WriteByte(data, address);
            }

            return new CpuBusTransaction(
                CpuMachineCycleKind.MemoryWrite,
                (ushort)address,
                data,
                blockedByOamDma,
                0,
                writeDataLatchedBeforeCompletion: writeDataLatchedBeforeCompletion);
        }

        /// <summary>
        /// Returns whether a clocked device consumes CPU write data before the canonical transaction completion.
        /// Timer, DIV, and APU behavior is sampled by the T4 hardware update; LCDC enable timing establishes the PPU
        /// startup phase consumed by that update. Other GPU registers remain T4-entry writes.
        /// </summary>
        private static bool WritesDeviceDataBeforeCompletion(IMemoryUnit memoryUnit, int address)
        {
            return memoryUnit is DivideRegister ||
                   memoryUnit is Timer ||
                   memoryUnit is APU ||
                   memoryUnit is GPU && address == MemorySchema.GPU_REGISTERS_START;
        }

        /// <summary>
        /// Completes a CPU read through the current compatibility policy without resampling OAM-DMA ownership.
        /// </summary>
        internal byte CompleteCpuRead(in CpuBusTransaction transaction)
        {
            if (transaction.OamDmaBlockedAtT1)
            {
                return transaction.OamDmaBusValueAtT1;
            }

            if (IsCgbIoReadBlocked(transaction.Address))
            {
                return 0xFF;
            }

            return transaction.ReadDataLatchedBeforeCompletion
                ? transaction.ReadDataBeforeCompletion
                : ReadByte(transaction.Address);
        }

        /// <summary>
        /// Returns whether the selected device drives CPU read data from its state at transaction completion.
        /// </summary>
        private bool ReadsDeviceStateAtCompletion(int address)
        {
            var memoryUnit = _memoryUnitLookup[address];
            return memoryUnit is GPU && _gpu.ReadsCpuDataAtCompletion(address) ||
                   memoryUnit is DMAController && _dmaController.ReadsCpuDataAtCompletion(address);
        }

        /// <summary>
        /// Applies CPU writes whose devices consume data at T4 entry, before the fourth hardware clock advances.
        /// </summary>
        internal void LatchCpuWriteAtT4(in CpuBusTransaction transaction)
        {
            if (transaction.OamDmaBlockedAtT1 ||
                transaction.WriteDataLatchedBeforeCompletion ||
                IsCgbIoWriteBlocked(transaction.Address) ||
                WritesPpuMemoryAtCompletion(transaction.Address))
            {
                return;
            }

            var memoryUnit = _memoryUnitLookup[transaction.Address];
            if (memoryUnit is DMAController)
            {
                _dmaController.WriteByteForCpuCompletion(transaction.WriteData, transaction.Address);
                return;
            }

            WriteByte(transaction.WriteData, transaction.Address);
        }

        /// <summary>
        /// Completes deferred PPU-memory writes after T4 without resampling T1 OAM-DMA ownership.
        /// </summary>
        internal void CompleteCpuWrite(in CpuBusTransaction transaction)
        {
            if (transaction.OamDmaBlockedAtT1 ||
                transaction.WriteDataLatchedBeforeCompletion ||
                IsCgbIoWriteBlocked(transaction.Address) ||
                !WritesPpuMemoryAtCompletion(transaction.Address))
            {
                return;
            }

            _gpu.WriteByteForCpu(transaction.WriteData, transaction.Address);
        }

        private bool WritesPpuMemoryAtCompletion(int address)
        {
            return _memoryUnitLookup[address] is GPU &&
                   ((address >= MemorySchema.VIDEO_RAM_START && address < MemorySchema.VIDEO_RAM_END) ||
                    (address >= MemorySchema.SPRITE_ATTRIBUTE_TABLE_START &&
                     address < MemorySchema.SPRITE_ATTRIBUTE_TABLE_END));
        }

        /// <summary>
        /// Returns the value driven to a CPU read when OAM DMA owns the selected bus at T1.
        /// </summary>
        private byte ReadOamDmaBlockedCpuValue(int address)
        {
            return address >= MemorySchema.SPRITE_ATTRIBUTE_TABLE_START &&
                   address < MemorySchema.SPRITE_ATTRIBUTE_TABLE_END
                ? (byte)0xFF
                : _dmaController.OamDmaBusValue;
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
        /// Advances DMA engines by one raw CPU clock, including CGB double speed.
        /// </summary>
        internal void AdvanceDmaRawClock()
        {
            _dmaController.AdvanceRawClock();
        }

        /// <summary>
        /// Gets whether an active CGB HBlank DMA block currently owns the CPU memory bus.
        /// </summary>
        internal bool IsCpuStalledByHBlankDma => _dmaController.IsCpuStalledByHBlankDma;

        /// <summary>
        /// Reads IF through the interrupt controller's untimed control-plane port.
        /// </summary>
        internal byte ReadInterruptRequestControl()
        {
            return ReadByte(MemorySchema.INTERRUPT_REQUEST_REGISTER);
        }

        /// <summary>
        /// Writes IF through the interrupt controller's untimed control-plane port.
        /// </summary>
        internal void WriteInterruptRequestControl(byte data)
        {
            WriteByte(data, MemorySchema.INTERRUPT_REQUEST_REGISTER);
        }

        /// <summary>
        /// Reads IE through the interrupt controller's untimed control-plane port.
        /// </summary>
        internal byte ReadInterruptEnableControl()
        {
            return ReadByte(MemorySchema.INTERRUPT_ENABLE_REGISTER_START);
        }

        /// <summary>
        /// Reads an address through the debugger/internal untimed port.
        /// </summary>
        internal byte ReadByteUntimed(int address)
        {
            return ReadByte(address);
        }

        /// <summary>
        /// Writes an address through the debugger/internal untimed port.
        /// </summary>
        internal void WriteByteUntimed(byte data, int address)
        {
            WriteByte(data, address);
        }

        /// <summary>
        /// Reads a CGB DMA source through the mapped memory device without CPU bus restrictions.
        /// </summary>
        private byte ReadCgbDmaSourceByte(int address)
        {
            return ReadByte(address);
        }

        /// <summary>
        /// Writes a CGB DMA destination through the mapped memory device without CPU bus restrictions.
        /// </summary>
        private void WriteCgbDmaDestinationByte(byte data, int address)
        {
            WriteByte(data, address);
        }

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

                if (address == MemorySchema.BOOT_ROM_DISABLE_REGISTER)
                {
                    _compatibilityModeRegisters.Lock();
                    if (_hardwareModel == HardwareModel.CgbE || _hardwareModel == HardwareModel.AgbA)
                    {
                        _gpu.SetDmgObjectPriority(_compatibilityModeRegisters.UsesDmgObjectPriority);
                    }

                    if (_mode == GBCMode.GBCCompatibility)
                    {
                        _gpu.EnterDmgCompatibilityMode();
                    }
                }

                return;
            }

            throw new IndexOutOfRangeException();
        }

        /// <summary>
        /// Applies the resolved AGB-A skip-boot register handoff before cartridge execution begins.
        /// </summary>
        public void ApplyStartupProfile(HardwareStartupProfile profile)
        {
            _compatibilityModeRegisters.ApplyStartupProfile(profile);
            _gpu.SetDmgObjectPriority(_compatibilityModeRegisters.UsesDmgObjectPriority);
        }

        private bool IsCgbIoReadBlocked(int address)
        {
            return IsCgbIoWindowBlocked(address) &&
                   address != MemorySchema.CPU_MODE_SELECT_REGISTER &&
                   address != MemorySchema.OBJECT_PRIORITY_REGISTER;
        }

        private bool IsCgbIoWriteBlocked(int address)
        {
            return IsCgbIoWindowBlocked(address) &&
                   address != MemorySchema.BOOT_ROM_DISABLE_REGISTER &&
                   address != MemorySchema.CPU_MODE_SELECT_REGISTER &&
                   address != MemorySchema.OBJECT_PRIORITY_REGISTER;
        }

        private bool IsCgbIoWindowBlocked(int address)
        {
            if (address < MemorySchema.CGB_IO_REGISTERS_START || address >= MemorySchema.CGB_IO_REGISTERS_END)
            {
                return false;
            }

            return _mode == GBCMode.NoGBC ||
                   _mode == GBCMode.GBCCompatibility && !_mainMemory.InBootROM;
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

            WriteByte(0, MemorySchema.BOOT_ROM_DISABLE_REGISTER);

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
