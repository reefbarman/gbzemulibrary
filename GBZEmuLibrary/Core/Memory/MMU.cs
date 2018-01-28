using System;
using System.Collections.Generic;
using System.Text;

namespace GBZEmuLibrary
{
    internal class MMU
    {
        public bool         InBootROM { get; set; } = true;
        public Func<byte>   GetSpeedState;
        public Action<byte> OnPendingSpeedSwitch;

        private readonly Cartridge      _cartridge;
        private readonly GPU            _gpu;
        private readonly Timer          _timer;
        private readonly DivideRegister _divideRegister;
        private readonly Joypad         _joypad;
        private readonly APU            _apu;

        private readonly WorkRAM _workRAM;

        private readonly byte[] _memory = new byte[MemorySchema.MAX_RAM_SIZE];

        public MMU(Cartridge cart, GPU gpu, Timer timer, DivideRegister divideRegister, Joypad joypad, APU apu)
        {
            _cartridge      = cart;
            _gpu            = gpu;
            _timer          = timer;
            _divideRegister = divideRegister;
            _joypad         = joypad;
            _apu            = apu;

            _workRAM = new WorkRAM();
        }

        public void Init(GBCMode mode)
        {
            _workRAM.Init(mode);
        }

        public string DumpMemString()
        {
            var builder = new StringBuilder();

            for (var address = 0; address < MemorySchema.MAX_RAM_SIZE; address++)
            {
                if (address < MemorySchema.ROM_END)
                {
                    builder.Append($"ROM :{address:X4} {ReadByte(address):X4}\n");
                    continue;
                }

                if (address < MemorySchema.VIDEO_RAM_END)
                {
                    builder.Append($"VRAM:{address:X4} {ReadByte(address):X4}\n");
                    continue;
                }

                if (address < MemorySchema.EXTERNAL_RAM_END)
                {
                    builder.Append($"ERAM:{address:X4} {ReadByte(address):X4}\n");
                    continue;
                }

                if (address < MemorySchema.WORK_RAM_END)
                {
                    builder.Append($"WRAM:{address:X4} {ReadByte(address):X4}\n");
                    continue;
                }

                if (address < MemorySchema.ECHO_RAM_END)
                {
                    builder.Append($"ERAM:{address:X4} {ReadByte(address):X4}\n");
                    continue;
                }

                if (address < MemorySchema.SPRITE_ATTRIBUTE_TABLE_END)
                {
                    builder.Append($"OAM :{address:X4} {ReadByte(address):X4}\n");
                    continue;
                }

                if (address < MemorySchema.RESTRICTED_RAM_END)
                {
                    builder.Append($"----:{address:X4} {ReadByte(address):X4}\n");
                    continue;
                }

                if (address < MemorySchema.HIGH_RAM_START)
                {
                    builder.Append($"IO :{address:X4} {ReadByte(address):X4}\n");
                    continue;
                }

                if (address < MemorySchema.HIGH_RAM_END)
                {
                    builder.Append($"HRAM:{address:X4} {ReadByte(address):X4}\n");
                    continue;
                }

                if (address < MemorySchema.INTERRUPT_ENABLE_REGISTER_END)
                {
                    builder.Append($"INTR:{address:X4} {ReadByte(address):X4}\n");
                }
            }

            return builder.ToString();
        }

        public byte[] DumpMem()
        {
            var dump = new List<byte>(MemorySchema.MAX_RAM_SIZE);

            for (var address = 0; address < MemorySchema.MAX_RAM_SIZE; address++)
            {
                dump.Add(ReadByte(address));
            }

            return dump.ToArray();
        }

        public byte ReadByte(int address)
        {
            if (address < MemorySchema.ROM_END)
            {
                if (InBootROM)
                {
                    if (address < MemorySchema.BOOT_ROM_SECTION_1_END || address >= MemorySchema.BOOT_ROM_SECTION_2_START && address < MemorySchema.BOOT_ROM_SECTION_2_END)
                    {
                        return BootROM.Bytes[address];
                    }
                }

                return _cartridge.ReadByte(address);
            }

            if (address < MemorySchema.VIDEO_RAM_END)
            {
                return _gpu.ReadByte(address);
            }

            if (address < MemorySchema.EXTERNAL_RAM_END)
            {
                return _cartridge.ReadByte(address);
            }

            if (address < MemorySchema.ECHO_RAM_SWITCHABLE_END)
            {
                return _workRAM.ReadByte(address);
            }

            if (address < MemorySchema.SPRITE_ATTRIBUTE_TABLE_END)
            {
                return _gpu.ReadByte(address); //TODO REFACTOR WHEN FULLY IMPLEMENTED
            }

            if (address < MemorySchema.RESTRICTED_RAM_END)
            {
                return _memory[address]; //TODO REFACTOR WHEN FULLY IMPLEMENTED
            }

            if (address == MemorySchema.JOYPAD_REGISTER)
            {
                return _joypad.ReadByte(address);
            }

            if (address == MemorySchema.DIVIDE_REGISTER)
            {
                return _divideRegister.ReadByte(address);
            }

            if (address >= MemorySchema.TIMER_START && address < MemorySchema.TIMER_END)
            {
                return _timer.ReadByte(address);
            }

            if (address >= MemorySchema.APU_REGISTERS_START && address < MemorySchema.APU_REGISTERS_END)
            {
                return _apu.ReadByte(address);
            }

            if (address >= MemorySchema.GPU_REGISTERS_START && address < MemorySchema.GPU_REGISTERS_END)
            {
                return _gpu.ReadByte(address);
            }

            if (address == MemorySchema.CPU_SPEED_SWITCH_REGISTER)
            {
                return (byte)GetSpeedState?.Invoke();
            }

            if (address == MemorySchema.GPU_VRAM_BANK_REGISTER)
            {
                return _gpu.ReadByte(address);
            }

            if (address == MemorySchema.BOOT_ROM_DISABLE_REGISTER)
            {
                return _memory[address];
            }

            if (address >= MemorySchema.GPU_GBC_BG_PALETTE_INDEX_REGISTER && address <= MemorySchema.GPU_GBC_SPRITE_PALETTE_DATA_REGISTER)
            {
                return _gpu.ReadByte(address);
            }

            if (address == MemorySchema.SWITCHABLE_WORK_RAM_REGISTER)
            {
                return _workRAM.ReadByte(address);
            }

            if (address < MemorySchema.HIGH_RAM_END)
            {
            }

            if (address >= MemorySchema.INTERRUPT_ENABLE_REGISTER_START && address < MemorySchema.INTERRUPT_ENABLE_REGISTER_END)
            {
            }

            return _memory[address];
        }

        public void WriteByte(byte data, int address)
        {
            //TODO improve this interface
            if (address == 0xFF02 && data == 0x81)
            {
                Console.Write(Encoding.ASCII.GetString(new[] {ReadByte(0xFF01)}));
            }

            if (address < MemorySchema.ROM_END)
            {
                _cartridge.WriteByte(data, address);
                return;
            }

            if (address < MemorySchema.VIDEO_RAM_END)
            {
                _gpu.WriteByte(data, address);
                return;
            }

            if (address < MemorySchema.EXTERNAL_RAM_END)
            {
                _cartridge.WriteByte(data, address);
                return;
            }

            if (address < MemorySchema.ECHO_RAM_SWITCHABLE_END)
            {
                _workRAM.WriteByte(data, address);
                return;
            }

            if (address < MemorySchema.SPRITE_ATTRIBUTE_TABLE_END)
            {
                _gpu.WriteByte(data, address);
                return; //TODO refactor
            }

            if (address < MemorySchema.RESTRICTED_RAM_END)
            {
                _memory[address] = data;
                return; //TODO refactor
            }

            if (address == MemorySchema.JOYPAD_REGISTER)
            {
                _joypad.WriteByte(data, address);
                return;
            }

            if (address == MemorySchema.DIVIDE_REGISTER)
            {
                _divideRegister.WriteByte(data, address);
                return;
            }

            if (address >= MemorySchema.TIMER_START && address < MemorySchema.TIMER_END)
            {
                _timer.WriteByte(data, address);
                return;
            }

            if (address >= MemorySchema.APU_REGISTERS_START && address < MemorySchema.APU_REGISTERS_END)
            {
                _apu.WriteByte(data, address);
                return;
            }

            if (address >= MemorySchema.GPU_REGISTERS_START && address < MemorySchema.GPU_REGISTERS_END)
            {
                if (address == MemorySchema.DMA_REGISTER)
                {
                    ProcessDMATranser(data);
                    return;
                }

                _gpu.WriteByte(data, address);
                return;
            }

            if (address == MemorySchema.CPU_SPEED_SWITCH_REGISTER)
            {
                OnPendingSpeedSwitch?.Invoke(data);
                return;
            }

            if (address == MemorySchema.GPU_VRAM_BANK_REGISTER)
            {
                _gpu.WriteByte(data, address);
                return;
            }

            if (address == MemorySchema.BOOT_ROM_DISABLE_REGISTER)
            {
                _memory[address] = data;
                InBootROM        = false;
                return;
            }

            if (address >= MemorySchema.GPU_GBC_BG_PALETTE_INDEX_REGISTER && address <= MemorySchema.GPU_GBC_SPRITE_PALETTE_DATA_REGISTER)
            {
                _gpu.WriteByte(data, address);
                return;
            }

            if (address == MemorySchema.SWITCHABLE_WORK_RAM_REGISTER)
            {
                _workRAM.WriteByte(data, address);
                return;
            }

            if (address < MemorySchema.HIGH_RAM_END)
            {
            }

            if (address >= MemorySchema.INTERRUPT_ENABLE_REGISTER_START && address < MemorySchema.INTERRUPT_ENABLE_REGISTER_END)
            {
            }

            _memory[address] = data;
        }

        public void Reset(bool usingBootROM)
        {
            _apu.Reset();

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

        private void ProcessDMATranser(byte data)
        {
            var address = data << 8;

            for (var i = 0; i < (MemorySchema.SPRITE_ATTRIBUTE_TABLE_END - MemorySchema.SPRITE_ATTRIBUTE_TABLE_START); i++)
            {
                WriteByte(ReadByte(address + i), MemorySchema.SPRITE_ATTRIBUTE_TABLE_START + i);
            }
        }
    }
}
