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

            // STAT and OAM availability are sampled at the end of the CPU read M-cycle. The complete
            // four-clock cycle is consumed before sampling, so there is no trailing memory cycle to defer.
            if (SamplesPpuStateAtEndOfReadCycle(address))
            {
                AdvanceMachineCycle();
                return _mmu.ReadByte(address);
            }

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

            // Scroll writes become visible at the end of their bus cycle. This matters when a raster effect updates
            // SCX or SCY while the PPU fetcher advances during the same machine cycle.
            if (WritesPpuScrollAtEndOfCycle(address))
            {
                AdvanceMachineCycle();
                _mmu.WriteByte(data, address);
                return;
            }

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

        /// <summary>
        /// Returns whether a CPU read observes PPU state after the read M-cycle has advanced the pixel clock.
        /// </summary>
        internal static bool SamplesPpuStateAtEndOfReadCycle(int address)
        {
            return address == MemorySchema.GPU_REGISTERS_START + 1 ||
                   (address >= MemorySchema.SPRITE_ATTRIBUTE_TABLE_START &&
                    address < MemorySchema.SPRITE_ATTRIBUTE_TABLE_END);
        }

        /// <summary>
        /// Returns whether a CPU write exposes a scroll-register value after its write M-cycle has elapsed.
        /// </summary>
        internal static bool WritesPpuScrollAtEndOfCycle(int address)
        {
            return address == MemorySchema.GPU_REGISTERS_START + 2 ||
                   address == MemorySchema.GPU_REGISTERS_START + 3;
        }

        private bool PendingInterrupt()
        {
            return (_mmu.ReadByte(MemorySchema.INTERRUPT_REQUEST_REGISTER) & _mmu.ReadByte(MemorySchema.INTERRUPT_ENABLE_REGISTER_START) & 0x1F) != 0;
        }
    }
}