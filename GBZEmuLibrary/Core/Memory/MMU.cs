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

        private readonly MainMemory _mainMemory = new MainMemory();

        /// <summary>
        /// Builds the fixed address-to-device lookup used by CPU, DMA, and debugger memory accesses.
        /// </summary>
        public MMU(Cartridge cart, GPU gpu, Timer timer, DivideRegister divideRegister, Joypad joypad, APU apu, SerialRegisters serialRegisters)
        {
            var memoryUnits = new List<IMemoryUnit>
            {
                cart, gpu, _workRAM, joypad, serialRegisters, divideRegister, timer, apu, new DMAController()
            };

            MessageBus.Instance.OnReadByte = ReadByte;
            MessageBus.Instance.OnWriteByte = WriteByte;

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
        /// Initializes mode-dependent work RAM behavior.
        /// </summary>
        public void Init(GBCMode mode)
        {
            _workRAM.Init(mode);
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
                    if (address < MemorySchema.BOOT_ROM_SECTION_1_END || BootROM.IsGBCSelected && address >= MemorySchema.BOOT_ROM_SECTION_2_START && address < MemorySchema.BOOT_ROM_SECTION_2_END)
                    {
                        return BootROM.Bytes[address];
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

            if (usingBootROM)
            {
                return;
            }

            WriteByte(0x00, 0xFF05);
            WriteByte(0x00, 0xFF06);
            WriteByte(0x00, 0xFF07);
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
