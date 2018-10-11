using System;
using System.Collections.Generic;
using System.Text;

namespace GBZEmuLibrary
{
    internal class MMU
    {
        public Func<byte>   GetSpeedState;
        public Action<byte> OnPendingSpeedSwitch;

        public bool InBootROM => _mainMemory.InBootROM;

        private readonly Dictionary<int, IMemoryUnit> _memoryUnitLookup = new Dictionary<int, IMemoryUnit>();

        private readonly WorkRAM _workRAM = new WorkRAM();

        private readonly MainMemory _mainMemory = new MainMemory();

        public MMU(Cartridge cart, GPU gpu, Timer timer, DivideRegister divideRegister, Joypad joypad, APU apu)
        {
            var memoryUnits = new List<IMemoryUnit>
            {
                cart, gpu, _workRAM, joypad, divideRegister, timer, apu, new DMAController()
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

        public void Init(GBCMode mode)
        {
            _workRAM.Init(mode);
        }

        public byte ReadByte(int address)
        {
            if (address < MemorySchema.ROM_END)
            {
                if (_mainMemory.InBootROM)
                {
                    if (address < MemorySchema.BOOT_ROM_SECTION_1_END || address >= MemorySchema.BOOT_ROM_SECTION_2_START && address < MemorySchema.BOOT_ROM_SECTION_2_END)
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
                return _memoryUnitLookup[address].ReadByte(address);
            }

            throw new IndexOutOfRangeException();
        }

        public void WriteByte(byte data, int address)
        {
            //TODO improve this interface
            if (address == 0xFF02 && data == 0x81)
            {
                Console.Write(Encoding.ASCII.GetString(new[] {ReadByte(0xFF01)}));
            }

            if (address == MemorySchema.CPU_SPEED_SWITCH_REGISTER)
            {
                OnPendingSpeedSwitch?.Invoke(data);
                return;
            }

            if (_memoryUnitLookup.ContainsKey(address))
            {
                _memoryUnitLookup[address].WriteByte(data, address);
                return;
            }

            throw new IndexOutOfRangeException();
        }

        public void Reset(bool usingBootROM)
        {
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
