using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using InsSet = GBZEmuLibrary.InstructionSet;
using InsCBSet = GBZEmuLibrary.CBInstructionSet;
using InsSchema = GBZEmuLibrary.InstructionSchema;

namespace GBZEmuLibrary
{
    internal partial class CPU
    {
        public Action<int> OnClockTick = null;

        private readonly MMU _mmu;
        private readonly InterruptHandler _interruptHandler;

        #region DEBUGGING
        private          ulong         _processCount;
        private          ulong         _totalClocks;
        private          int           _previousPC;
        private          bool          _dumpMemory;
        private readonly int           _breakPC            = 0x02B7;
        private readonly ulong         _processRecordStart = ulong.MaxValue;
        private readonly ulong         _breakProcessCount  = 293480;
        private readonly StringBuilder _opBuilder          = new StringBuilder();
        #endregion

        private ushort       _pc; 
        private StackPointer _sp;
        private Registers    _registers;
        private bool         _pendingInterruptDisabled;
        private bool         _pendingInterruptEnabled;

        private Dictionary<byte, Action> _instructions;
        private Dictionary<byte, Action> _instructionsCB;

        private bool _stopped;
        private bool _haltSkip;

        public CPU(MMU mmu)
        {
            _mmu = mmu;
            _interruptHandler = new InterruptHandler(_mmu);
            _interruptHandler.IncrementClock += IncrementClock;
            _interruptHandler.PushProgramCounter += () => { Push(_pc); };
            _interruptHandler.UpdateProgramCounter += (pc, joypadInterrupt) =>
            {
                _pc = pc;
                if (joypadInterrupt)
                {
                    _stopped = false;
                }
            };

            MessageBus.Instance.OnRequestInterrupt += i => _interruptHandler.RequestInterrupt(i);

            InitInstructions();
        }

        public override string ToString()
        {
            //return $"{_processCount}: TC: {_totalClocks} SL: {_mmu.ReadByte(0xFF44)} PC: {_pc - 1:X4}, AF: {_registers.AF:X4}, BC: {_registers.BC:X4}, DE: {_registers.DE:X4}, HL: {_registers.HL:X4}, SP: {_sp.P:X4}, Z: {Helpers.TestBit(_registers.F, InsSchema.FLAG_Z)}, N: {Helpers.TestBit(_registers.F, InsSchema.FLAG_N)}, H: {Helpers.TestBit(_registers.F, InsSchema.FLAG_H)}, C: {Helpers.TestBit(_registers.F, InsSchema.FLAG_C)}";
            return $"{_processCount}: T: {Timer.TimerCounter()} TC: {_totalClocks} PC: {_pc - 1:X4}, AF: {_registers.AF:X4}, BC: {_registers.BC:X4}, DE: {_registers.DE:X4}, HL: {_registers.HL:X4}, SP: {_sp.P:X4}, Z: {Helpers.TestBit(_registers.F, InsSchema.FLAG_Z)}, N: {Helpers.TestBit(_registers.F, InsSchema.FLAG_N)}, H: {Helpers.TestBit(_registers.F, InsSchema.FLAG_H)}, C: {Helpers.TestBit(_registers.F, InsSchema.FLAG_C)}";
        }

        public void Process()
        {
            //TODO check if worth keeping
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

                Helpers.NoOp();
            }

            if (_pc == _breakPC && !_mmu.InBIOS)
            {
                Helpers.NoOp();
            }

            //TODO determine the best way to handle stop
            /*if (_stopped)
            {
                _processCount++;
                return 0;
            }*/

            _previousPC = _pc;

            if (_interruptHandler.Halted)
            {
                IncrementClock();
                return;
            }

            var instruction = ReadByte(_pc++);

            if (_haltSkip)
            {
                _pc = (ushort)(_pc - 1);
                _haltSkip = false;
            }

            if (_instructions.ContainsKey(instruction))
            {
                if (_dumpMemory)
                {
                    File.WriteAllBytes("MemLog", _mmu.DumpMem());
                    _dumpMemory = false;
                }

                _instructions[instruction]();
            }
            else
            {
                File.WriteAllBytes("MemLog", _mmu.DumpMem());
                File.WriteAllText("InstructionLog", _opBuilder.ToString());
                throw new NotImplementedException($"Instruction not implemented: {instruction:X}");
            }

            if (_pc == MemorySchema.BIOS_END)
            {
                _mmu.InBIOS = false;
            }

            // we are trying to disable interrupts, however interrupts get disabled after the next instruction
            //TODO determine if disabling is handled different https://github.com/AntonioND/giibiiadvance/blob/master/docs/TCAGBD.pdf (section 3.3)
            if (_pendingInterruptDisabled)
            {
                if (_mmu.ReadByte(_pc - 1) != InsSet.DI)
                {
                    _pendingInterruptDisabled = false;
                    _interruptHandler.InterruptsEnabled = false;
                }
            }

            if (_pendingInterruptEnabled)
            {
                if (_mmu.ReadByte(_pc - 1) != InsSet.EI)
                {
                    _pendingInterruptEnabled = false;
                    _interruptHandler.InterruptsEnabled = true;
                }
            }

            _processCount++;
        }

        public void UpdateInterrupts()
        {
            _interruptHandler.Update();
        }

        public void Reset(bool usingBios)
        {
            if (usingBios)
            {
                return;
            }

            _mmu.InBIOS = false;

            _registers.AF = 0x01B0;
            _registers.BC = 0x0013;
            _registers.DE = 0x00D8;
            _registers.HL = 0x014D;

            _sp.P = 0xFFFE;
            _pc = 0x100;

            _mmu.Reset(usingBios);
        }

        private void ProcessExtended()
        {
            var instruction = ReadByte(_pc++);

            if (_instructionsCB.ContainsKey(instruction))
            {
                _instructionsCB[instruction]();
            }
            else
            {
                throw new NotImplementedException($"CB Instruction not implemented: {instruction:X}");
            }
        }
    }
}
