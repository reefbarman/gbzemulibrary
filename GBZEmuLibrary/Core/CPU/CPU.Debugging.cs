namespace GBZEmuLibrary
{
    internal partial class CPU
    {
        internal System.Action LoadBBExecuted;
        internal System.Action BreakpointHit;

        private ulong _totalClocks;
        private TraceBuffer _traceBuffer;

        internal CpuDebugState GetDebugState()
        {
            return new CpuDebugState(
                _pc,
                _sp.SP,
                _registers.AF,
                _registers.BC,
                _registers.DE,
                _registers.HL,
                _interruptHandler.InterruptsEnabled,
                _pendingInterruptEnabled >= 0,
                _pendingInterruptDisabled >= 0,
                _interruptHandler.Halted,
                _doubleSpeed,
                _totalClocks,
                _instructionCount);
        }

        internal void SetTraceBuffer(TraceBuffer traceBuffer)
        {
            _traceBuffer = traceBuffer;
        }

        public override string ToString()
        {
            return $"{_instructionCount}: TC: {_totalClocks} SL: {_mmu.ReadByte(0xFF44)} PC: {_pc:X4}, AF: {_registers.AF:X4}, BC: {_registers.BC:X4}, DE: {_registers.DE:X4}, HL: {_registers.HL:X4}, SP: {_sp.SP:X4}, Z: {Helpers.TestBit(_registers.F, InstructionSchema.FLAG_Z)}, N: {Helpers.TestBit(_registers.F, InstructionSchema.FLAG_N)}, H: {Helpers.TestBit(_registers.F, InstructionSchema.FLAG_H)}, C: {Helpers.TestBit(_registers.F, InstructionSchema.FLAG_C)}";
        }

        private bool Debug()
        {
            if (_traceBuffer == null)
            {
                return false;
            }

            if (_traceBuffer.ShouldCapture(_instructionCount))
            {
                _traceBuffer.Add(ToString());
            }

            if (_traceBuffer.BreakProgramCounter == _pc && !_mmu.InBootROM)
            {
                BreakpointHit?.Invoke();
                return true;
            }

            return false;
        }

        private void LoadBB()
        {
            Load(ref _registers.B, _registers.B);
            LoadBBExecuted?.Invoke();
        }
    }
}
