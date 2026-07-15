namespace GBZEmuLibrary
{
    internal partial class CPU
    {
        /// <summary>
        /// Advances internal CPU cycles after completing any preceding memory bus cycle.
        /// </summary>
        private void IncrementClock(int clocks = 1)
        {
            FlushPendingMemoryCycle();

            for (var i = 0; i < clocks; i++)
            {
                AdvanceMachineCycle();
            }
        }

        /// <summary>
        /// Samples a bus read after the previous cycle and defers this read cycle's trailing clocks.
        /// </summary>
        private byte ReadByte(int address)
        {
            FlushPendingMemoryCycle();
            var data = _mmu.ReadByte(address);
            _memoryCyclePending = true;
            return data;
        }

        /// <summary>
        /// Performs a bus write after the previous cycle and defers this write cycle's trailing clocks.
        /// </summary>
        private void WriteByte(byte data, int address)
        {
            FlushPendingMemoryCycle();
            _mmu.WriteByte(data, address);
            _memoryCyclePending = true;
        }

        /// <summary>
        /// Completes a deferred read or write cycle so hardware observes bus access before its trailing clocks.
        /// </summary>
        private void FlushPendingMemoryCycle()
        {
            if (!_memoryCyclePending)
            {
                return;
            }

            _memoryCyclePending = false;
            AdvanceMachineCycle();
        }

        /// <summary>
        /// Advances one four-clock LR35902 machine cycle and all clocked hardware domains.
        /// </summary>
        private void AdvanceMachineCycle()
        {
            _totalClocks += InstructionSchema.FOUR_CYCLES;
            OnClockTick?.Invoke(InstructionSchema.FOUR_CYCLES);
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