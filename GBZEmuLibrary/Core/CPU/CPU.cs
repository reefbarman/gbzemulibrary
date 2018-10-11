using System;
using System.Collections.Generic;

namespace GBZEmuLibrary
{
    internal partial class CPU
    {
        public Action<int> OnClockTick;

        public int SpeedFactor => _doubleSpeed ? 2 : 1;

        private readonly MMU              _mmu;
        private readonly InterruptHandler _interruptHandler;

        private ushort       _pc;
        private StackPointer _sp;
        private Registers    _registers;
        private int         _pendingInterruptDisabled;
        private int         _pendingInterruptEnabled;

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

        public void Process()
        {
            Debug();
            //TODO determine the best way to handle stop
            /*if (_stopped)
            {
                _processCount++;
                return 0;
            }*/

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
                _instructions[instruction]();
            }
            else
            {
                throw new NotImplementedException($"Instruction not implemented: {instruction:X}");
            }

            // we are trying to disable interrupts, however interrupts get disabled after the next instruction
            //TODO determine if disabling is handled different https://github.com/AntonioND/giibiiadvance/blob/master/docs/TCAGBD.pdf (section 3.3)
            if (_pendingInterruptDisabled >= 0 && _pendingInterruptDisabled-- == 0)
            {
                _interruptHandler.InterruptsEnabled = false;
            }

            if (_pendingInterruptEnabled >= 0 && _pendingInterruptEnabled-- == 0)
            {
                _interruptHandler.InterruptsEnabled = true;
            }
        }

        public void UpdateInterrupts()
        {
            _interruptHandler.Update();
        }

        public void Reset(bool usingBootROM, GBCMode gbcMode)
        {
            _gbcMode = gbcMode;

            if (usingBootROM)
            {
                _registers.A = (byte)(_gbcMode != GBCMode.NoGBC ? 0x11 : 0x01);
            }
            else
            {
                _mmu.WriteByte(0, MemorySchema.BOOT_ROM_DISABLE_REGISTER);

                _registers.AF = (ushort)(_gbcMode != GBCMode.NoGBC ? 0x11B0 : 0x01B0);
                _registers.BC = 0x0013;
                _registers.DE = 0x00D8;
                _registers.HL = 0x014D;

                _sp.SP = 0xFFFE;
                _pc   = 0x100;
            }

            _mmu.Reset(usingBootROM);
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
            _pendingSpeedSwitch = Helpers.TestBit(data, 0);
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
