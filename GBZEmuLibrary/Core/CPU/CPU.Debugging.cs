using System.Diagnostics;
using System.IO;
using System.Text;

namespace GBZEmuLibrary
{
    internal partial class CPU
    {

        private          ulong         _processCount;
        private          ulong         _totalClocks;
        private readonly int           _breakPC            = 0;
        private readonly ulong         _processRecordStart = ulong.MaxValue;
        private readonly ulong         _breakProcessCount  = ulong.MaxValue;
        private readonly StringBuilder _opBuilder          = new StringBuilder();

        public override string ToString()
        {
            //return $"{_processCount}: TC: {_totalClocks} PC: {_pc:X4}, AF: {_registers.AF:X4}, BC: {_registers.BC:X4}, DE: {_registers.DE:X4}, HL: {_registers.HL:X4}, SP: {_sp.SP:X4}, Z: {Helpers.TestBit(_registers.F, InsSchema.FLAG_Z)}, N: {Helpers.TestBit(_registers.F, InsSchema.FLAG_N)}, H: {Helpers.TestBit(_registers.F, InsSchema.FLAG_H)}, C: {Helpers.TestBit(_registers.F, InsSchema.FLAG_C)}";
            return $"{_processCount}: TC: {_totalClocks} SL: {_mmu.ReadByte(0xFF44)} PC: {_pc - 1:X4}, AF: {_registers.AF:X4}, BC: {_registers.BC:X4}, DE: {_registers.DE:X4}, HL: {_registers.HL:X4}, SP: {_sp.SP:X4}, Z: {Helpers.TestBit(_registers.F, InstructionSchema.FLAG_Z)}, N: {Helpers.TestBit(_registers.F, InstructionSchema.FLAG_N)}, H: {Helpers.TestBit(_registers.F, InstructionSchema.FLAG_H)}, C: {Helpers.TestBit(_registers.F, InstructionSchema.FLAG_C)}";
            //return $"{_processCount}: T: {Timer.TimerCounter()} TC: {_totalClocks} PC: {_pc - 1:X4}, AF: {_registers.AF:X4}, BC: {_registers.BC:X4}, DE: {_registers.DE:X4}, HL: {_registers.HL:X4}, SP: {_sp.SP:X4}, Z: {Helpers.TestBit(_registers.F, InsSchema.FLAG_Z)}, N: {Helpers.TestBit(_registers.F, InsSchema.FLAG_N)}, H: {Helpers.TestBit(_registers.F, InsSchema.FLAG_H)}, C: {Helpers.TestBit(_registers.F, InsSchema.FLAG_C)}";
        }

        [Conditional("DEBUG")]
        private void Debug()
        {
            if (_processCount >= _processRecordStart)
            {
                _opBuilder.AppendLine(ToString());
            }

            if (_processCount == _breakProcessCount)
            {
                if (_opBuilder.Length > 0)
                {
                    File.WriteAllText("InstructionLog", _opBuilder.ToString());
                }

                DebugBreak();
            }

            if (_pc == _breakPC && !_mmu.InBootROM)
            {
                DebugBreak();
            }

            _processCount++;
        }

        [Conditional("DEBUG")]
        void DebugBreak()
        {
            if (Debugger.IsAttached)
            {
                Debugger.Break();
            }
        }
    }
}
