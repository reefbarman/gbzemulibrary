namespace GBZEmuLibrary
{
    internal partial class CPU
    {
        private void IncrementClock(int clocks = 1)
        {
            for (var i = 0; i < clocks; i++)
            {
                _totalClocks += InstructionSchema.FOUR_CYCLES;
                OnClockTick?.Invoke(InstructionSchema.FOUR_CYCLES);
            }
        }

        private byte ReadByte(int address)
        {
            IncrementClock();
            return _mmu.ReadByte(address);
        }

        private void WriteByte(byte data, int address)
        {
            IncrementClock();
            _mmu.WriteByte(data, address);
        }

        private void SetFlag(int flag, bool val = true)
        {
            Helpers.SetBit(ref _registers.F, flag, val);
        }

        private bool TestFlag(int flag)
        {
            return Helpers.TestBit(_registers.F, flag);
        }

        private ushort Read16Bit(int address)
        {
            var lo = ReadByte(address);
            var high = ReadByte(address + 1);
            return (ushort)((high << 8) | lo);
        }

        private bool PendingInterrupt()
        {
            return (_mmu.ReadByte(MemorySchema.INTERRUPT_REQUEST_REGISTER) & _mmu.ReadByte(MemorySchema.INTERRUPT_ENABLE_REGISTER_START) & 0x1F) != 0;
        }
    }
}