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

        private readonly MainMemory _mainMemory = new MainMemory();
        private GBCMode _mode;

        /// <summary>
        /// Builds the fixed address-to-device lookup used by CPU, DMA, and debugger memory accesses.
        /// </summary>
        public MMU(Cartridge cart, GPU gpu, Timer timer, DivideRegister divideRegister, Joypad joypad, APU apu, SerialRegisters serialRegisters, BootROM bootROM, MessageBus messageBus)
        {
            _apu = apu;
            _bootROM = bootROM;
            _gpu = gpu;
            _serialRegisters = serialRegisters;
            _dmaController = new DMAController(messageBus);

            var memoryUnits = new List<IMemoryUnit>
            {
                cart, gpu, _workRAM, joypad, serialRegisters, divideRegister, timer, apu, _dmaController, new UnmappedIO()
            };

            messageBus.OnReadByte = ReadByte;
            messageBus.OnWriteByte = WriteByte;

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
        public void Init(GBCMode mode)
        {
            _mode = mode;
            _workRAM.Init(mode);
            // APU mode must be selected before Emulator.Start applies its post-boot reset profile.
            _apu.Init(mode);
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
                    if (address < MemorySchema.BOOT_ROM_SECTION_1_END || _bootROM.IsGBCSelected && address >= MemorySchema.BOOT_ROM_SECTION_2_START && address < MemorySchema.BOOT_ROM_SECTION_2_END)
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

                // IF only implements the five interrupt request bits; unused bits are pulled high on reads.
                return address == MemorySchema.INTERRUPT_REQUEST_REGISTER
                    ? (byte)(value | 0xE0)
                    : value;
            }

            throw new IndexOutOfRangeException();
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

            // These deterministic values are shared by DMG ABC and the current CGB skip-boot profile.
            WriteByte(0x00, MemorySchema.JOYPAD_REGISTER);
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
