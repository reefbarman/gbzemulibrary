using System;

namespace GBZEmuLibrary
{
    internal class WorkRAM : IMemoryUnit
    {
        private const int MAX_NUM_RAM_BANKS = 8;

        private readonly byte[] _memory = new byte[MemorySchema.MAX_WORK_RAM_BANK_SIZE * MAX_NUM_RAM_BANKS];

        private int _ramBank = 1;

        private GBCMode _mode;

        public void Init(GBCMode mode)
        {
            _mode = mode;
        }

        public bool CanReadWriteByte(int address)
        {
            if (address >= MemorySchema.WORK_RAM_START && address < MemorySchema.ECHO_RAM_SWITCHABLE_END)
            {
                return true;
            }

            if (address == MemorySchema.SWITCHABLE_WORK_RAM_REGISTER)
            {
                return true;
            }

            return false;
        }

        public byte ReadByte(int address)
        {
            if (address < MemorySchema.WORK_RAM_END)
            {
                return _memory[address - MemorySchema.WORK_RAM_START];
            }

            if (address < MemorySchema.WORK_RAM_SWITCHABLE_END)
            {
                return _memory[address - MemorySchema.WORK_RAM_START + GetBankOffset()];
            }

            if (address < MemorySchema.ECHO_RAM_END)
            {
                return _memory[address - MemorySchema.WORK_RAM_START - MemorySchema.WORK_RAM_ECHO_OFFSET];
            }

            if (address < MemorySchema.ECHO_RAM_SWITCHABLE_END)
            {
                return _memory[address - MemorySchema.WORK_RAM_START - MemorySchema.WORK_RAM_ECHO_SWITCHABLE_OFFSET + GetBankOffset()];
            }
            
            if (address == MemorySchema.SWITCHABLE_WORK_RAM_REGISTER)
            {
                return (byte)_ramBank;
            }

            throw new IndexOutOfRangeException();
        }

        public void WriteByte(byte data, int address)
        {
            if (address < MemorySchema.WORK_RAM_END)
            {
                _memory[address - MemorySchema.WORK_RAM_START] = data;
                return;
            }

            if (address < MemorySchema.WORK_RAM_SWITCHABLE_END)
            {
                _memory[address - MemorySchema.WORK_RAM_START + GetBankOffset()] = data;
                return;
            }

            if (address < MemorySchema.ECHO_RAM_END)
            {
                _memory[address - MemorySchema.WORK_RAM_START - MemorySchema.WORK_RAM_ECHO_OFFSET] = data;
                return;
            }

            if (address < MemorySchema.ECHO_RAM_SWITCHABLE_END)
            {
                _memory[address - MemorySchema.WORK_RAM_START - MemorySchema.WORK_RAM_ECHO_SWITCHABLE_OFFSET + GetBankOffset()] = data;
            }

            if (address == MemorySchema.SWITCHABLE_WORK_RAM_REGISTER)
            {
                _ramBank = Math.Max((int)Helpers.GetBits(data, 3), 1);
            }
        }

        private int GetBankOffset()
        {
            return _mode != GBCMode.NoGBC ? (_ramBank - 1) * MemorySchema.MAX_WORK_RAM_BANK_SIZE : 0;
        }
    }
}
