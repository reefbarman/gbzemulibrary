using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using InsSchema = GBZEmuLibrary.InstructionSchema;
using InsSet = GBZEmuLibrary.InstructionSet;

namespace GBZEmuLibrary
{
    internal partial class CPU
    {
        public Action<int> OnClockTick;

        public int SpeedFactor => _doubleSpeed ? 2 : 1;

        private readonly MMU              _mmu;
        private readonly InterruptHandler _interruptHandler;

        #region DEBUGGING

        private          ulong         _processCount;
        private          ulong         _totalClocks;
        private          int           _previousPC;
        private          bool          _dumpMemory;
        private readonly int           _breakPC            = 0x02B7;
        private readonly ulong         _processRecordStart = ulong.MaxValue;
        private readonly ulong         _breakProcessCount  = 1601466; //708182;
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
        private bool _pendingSpeedSwitch;
        private bool _doubleSpeed;

        private GBCMode _gbcMode = GBCMode.NoGBC;

        public CPU(MMU mmu)
        {
            _mmu                      = mmu;
            _mmu.GetSpeedState        = GetSpeedState;
            _mmu.OnPendingSpeedSwitch = OnPendingSpeedSwitch;

            _interruptHandler                      =  new InterruptHandler(_mmu);
            _interruptHandler.IncrementClock       += IncrementClock;
            _interruptHandler.PushProgramCounter   += () => { Push(_pc); };
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
            return $"{_processCount}: TC: {_totalClocks} PC: {_pc:X4}, AF: {_registers.AF:X4}, BC: {_registers.BC:X4}, DE: {_registers.DE:X4}, HL: {_registers.HL:X4}, SP: {_sp.P:X4}, Z: {Helpers.TestBit(_registers.F, InsSchema.FLAG_Z)}, N: {Helpers.TestBit(_registers.F, InsSchema.FLAG_N)}, H: {Helpers.TestBit(_registers.F, InsSchema.FLAG_H)}, C: {Helpers.TestBit(_registers.F, InsSchema.FLAG_C)}";
            //return $"{_processCount}: TC: {_totalClocks} SL: {_mmu.ReadByte(0xFF44)} PC: {_pc - 1:X4}, AF: {_registers.AF:X4}, BC: {_registers.BC:X4}, DE: {_registers.DE:X4}, HL: {_registers.HL:X4}, SP: {_sp.P:X4}, Z: {Helpers.TestBit(_registers.F, InsSchema.FLAG_Z)}, N: {Helpers.TestBit(_registers.F, InsSchema.FLAG_N)}, H: {Helpers.TestBit(_registers.F, InsSchema.FLAG_H)}, C: {Helpers.TestBit(_registers.F, InsSchema.FLAG_C)}";
            //return $"{_processCount}: T: {Timer.TimerCounter()} TC: {_totalClocks} PC: {_pc - 1:X4}, AF: {_registers.AF:X4}, BC: {_registers.BC:X4}, DE: {_registers.DE:X4}, HL: {_registers.HL:X4}, SP: {_sp.P:X4}, Z: {Helpers.TestBit(_registers.F, InsSchema.FLAG_Z)}, N: {Helpers.TestBit(_registers.F, InsSchema.FLAG_N)}, H: {Helpers.TestBit(_registers.F, InsSchema.FLAG_H)}, C: {Helpers.TestBit(_registers.F, InsSchema.FLAG_C)}";
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
                _pc       = (ushort)(_pc - 1);
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

            // we are trying to disable interrupts, however interrupts get disabled after the next instruction
            //TODO determine if disabling is handled different https://github.com/AntonioND/giibiiadvance/blob/master/docs/TCAGBD.pdf (section 3.3)
            if (_pendingInterruptDisabled)
            {
                if (_mmu.ReadByte(_pc - 1) != InsSet.DI)
                {
                    _pendingInterruptDisabled           = false;
                    _interruptHandler.InterruptsEnabled = false;
                }
            }

            if (_pendingInterruptEnabled)
            {
                if (_mmu.ReadByte(_pc - 1) != InsSet.EI)
                {
                    _pendingInterruptEnabled            = false;
                    _interruptHandler.InterruptsEnabled = true;
                }
            }

            _processCount++;
        }

        public void UpdateInterrupts()
        {
            _interruptHandler.Update();
        }

        public void Reset(bool usingBios, GBCMode gbcMode)
        {
            _gbcMode = gbcMode;

            if (usingBios)
            {
                _registers.A = (byte)(_gbcMode != GBCMode.NoGBC ? 0x11 : 0x01);
            }
            else
            {
                _mmu.InBIOS = false;

                _registers.AF = (ushort)(_gbcMode != GBCMode.NoGBC ? 0x11B0 : 0x01B0);
                _registers.BC = 0x0013;
                _registers.DE = 0x00D8;
                _registers.HL = 0x014D;

                _sp.P = 0xFFFE;
                _pc   = 0x100;
            }

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

        private void OnPendingSpeedSwitch(byte data)
        {
            _pendingSpeedSwitch = Helpers.TestBit(data, 1);
        }

        private byte GetSpeedState()
        {
            byte data = 0;

            Helpers.SetBit(ref data, 0, _pendingSpeedSwitch);
            Helpers.SetBit(ref data, 7, _doubleSpeed);

            return data;
        }
    }
}
