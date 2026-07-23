namespace GBZEmuLibrary
{
    internal partial class CPU
    {
        /// <summary>
        /// Advances internal CPU cycles.
        /// </summary>
        private void IncrementClock(int clocks = 1)
        {
            for (var i = 0; i < clocks; i++)
            {
                AdvanceMachineCycle();
            }
        }

        /// <summary>
        /// Completes a CPU read at T4 after advancing one synchronous structural transaction.
        /// </summary>
        private byte ReadByte(int address, CpuMachineCycleKind kind = CpuMachineCycleKind.MemoryRead)
        {
            var transaction = _mmu.BeginCpuRead(address, kind);
            AdvanceMachineCycle();
            return CompleteCpuReadAndObserve(in transaction);
        }

        /// <summary>
        /// Completes and observes a CPU read whose machine cycle has already elapsed.
        /// </summary>
        private byte CompleteCpuReadAndObserve(in CpuBusTransaction transaction)
        {
            var data = _mmu.CompleteCpuRead(in transaction);
            ObserveTiming(new TimingEvent(
                TimingEventKind.CpuReadObserved,
                transaction.Address,
                data,
                blockedByOamDma: transaction.OamDmaBlockedAtT1));
            return data;
        }

        /// <summary>
        /// Completes a CPU write at T4 during one synchronous structural transaction.
        /// </summary>
        private void WriteByte(byte data, int address)
        {
            var transaction = _mmu.BeginCpuWrite(data, address);
            AdvanceWriteMachineCycle(in transaction);
        }

        /// <summary>
        /// Latches a CPU write at T4 before clocked hardware advances through that state.
        /// </summary>
        private void LatchCpuWriteAtT4(in CpuBusTransaction transaction)
        {
            _mmu.LatchCpuWriteAtT4(in transaction);
        }

        /// <summary>
        /// Completes a CPU write after T4 and emits its timing observation.
        /// </summary>
        private void CompleteCpuWrite(in CpuBusTransaction transaction)
        {
            _mmu.CompleteCpuWrite(in transaction);
            ObserveTiming(new TimingEvent(
                TimingEventKind.CpuWriteObserved,
                transaction.Address,
                transaction.WriteData,
                blockedByOamDma: transaction.OamDmaBlockedAtT1));
        }

        /// <summary>
        /// Advances one four-clock LR35902 machine cycle and all clocked hardware domains.
        /// </summary>
        private void AdvanceMachineCycle()
        {
            BeginMachineCycle();
            for (var rawClock = 0; rawClock < InstructionSchema.FOUR_CYCLES; rawClock++)
            {
                OnClockTick?.Invoke(1);
            }
            EndMachineCycle();
        }

        /// <summary>
        /// Advances a CPU write through T1-T3, makes it visible at T4, then advances the T4 hardware clock.
        /// </summary>
        private void AdvanceWriteMachineCycle(in CpuBusTransaction transaction)
        {
            BeginMachineCycle();
            for (var rawClock = 1; rawClock < InstructionSchema.FOUR_CYCLES; rawClock++)
            {
                OnClockTick?.Invoke(1);
            }

            LatchCpuWriteAtT4(in transaction);
            OnClockTick?.Invoke(1);
            CompleteCpuWrite(in transaction);
            EndMachineCycle();
        }

        private void BeginMachineCycle()
        {
            ObserveTiming(new TimingEvent(
                TimingEventKind.MachineCycleStarted,
                clocks: InstructionSchema.FOUR_CYCLES));
            _totalClocks += InstructionSchema.FOUR_CYCLES;
        }

        private void EndMachineCycle()
        {
            ObserveTiming(new TimingEvent(
                TimingEventKind.MachineCycleCompleted,
                clocks: InstructionSchema.FOUR_CYCLES));
        }

        private void ObserveTiming(in TimingEvent timingEvent)
        {
            _timingObserver?.Observe(in timingEvent);
        }

        private void ObserveInterruptDispatchCycle(InterruptDispatchCycle cycle)
        {
            ObserveTiming(new TimingEvent(
                TimingEventKind.InterruptDispatchCycle,
                value: (byte)cycle));
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
            return _interruptHandler.HasPendingInterrupt();
        }
    }
}
