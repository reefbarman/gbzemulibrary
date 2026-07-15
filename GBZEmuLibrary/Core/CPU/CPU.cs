using System;
using System.Collections.Generic;

namespace GBZEmuLibrary
{
    /// <summary>
    /// Emulates the LR35902 CPU, including instruction execution, cycle signaling, and interrupt state.
    /// </summary>
    internal partial class CPU
    {
        /// <summary>
        /// Signals elapsed CPU clocks so hardware subsystems advance from the same timing source.
        /// </summary>
        public Action<int> OnClockTick;

        /// <summary>
        /// Signals completion of a prepared CGB speed switch so clocked hardware can reset required state.
        /// </summary>
        public Action OnSpeedSwitch;

        public int SpeedFactor => _doubleSpeed ? 2 : 1;

        private readonly MMU _mmu;
        private readonly InterruptHandler _interruptHandler;

        private ushort _pc;
        private StackPointer _sp;
        private Registers _registers;
        private int _pendingInterruptEnabled = -1;
        private ulong _instructionCount;

        private Dictionary<byte, Action> _instructions;
        private Dictionary<byte, Action> _instructionsCB;

        private bool _haltSkip;
        private bool _memoryCyclePending;
        private bool _pendingSpeedSwitch;
        private bool _doubleSpeed;

        private GBCMode _gbcMode = GBCMode.NoGBC;

        /// <summary>
        /// Creates a CPU connected to the MMU and interrupt bus owned by the same emulator.
        /// </summary>
        public CPU(MMU mmu, MessageBus messageBus)
        {
            _mmu = mmu;
            _mmu.GetSpeedState = GetSpeedState;
            _mmu.OnPendingSpeedSwitch = OnPendingSpeedSwitch;

            _interruptHandler = new InterruptHandler(_mmu);
            messageBus.OnRequestInterrupt = i => _interruptHandler.RequestInterrupt(i);

            InitInstructions();
        }

        /// <summary>
        /// Executes one instruction, HALT cycle, or interrupt-dispatch sequence.
        /// </summary>
        public bool Process()
        {
            try
            {
                if (_interruptHandler.Halted)
                {
                    IncrementClock();
                    ServicePendingInterrupt(true);
                    return true;
                }

                if (Debug())
                {
                    return false;
                }

                if (ServicePendingInterrupt(false))
                {
                    return true;
                }

                var instructionAddress = _pc;
                var instruction = ReadByte(_pc++);

                // An interrupt asserted during opcode fetch suppresses that instruction and uses the fetch as M1.
                FlushPendingMemoryCycle();
                if (_interruptHandler.InterruptsEnabled && _interruptHandler.HasPendingInterrupt())
                {
                    _pc = instructionAddress;
                    ServicePendingInterrupt(true);
                    return true;
                }

                if (_haltSkip)
                {
                    _pc = (ushort)(_pc - 1);
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

                _instructionCount++;

                if (_pendingInterruptEnabled >= 0 && _pendingInterruptEnabled-- == 0)
                {
                    _interruptHandler.InterruptsEnabled = true;
                }

                return true;
            }
            finally
            {
                FlushPendingMemoryCycle();
            }
        }

        /// <summary>
        /// Resets CPU registers, execution state, and startup values for the selected hardware mode.
        /// </summary>
        public void Reset(bool usingBootROM, GBCMode gbcMode)
        {
            _gbcMode = gbcMode;
            _pendingInterruptEnabled = -1;
            _memoryCyclePending = false;
            _instructionCount = 0;

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
                _pc = 0x100;
            }

            _mmu.Reset(usingBootROM);
        }

        /// <summary>
        /// Services the highest-priority pending interrupt with hardware-ordered internal and stack cycles.
        /// </summary>
        /// <param name="firstCycleElapsed">
        /// Whether the first dispatch M-cycle was already consumed by HALT or an opcode fetch.
        /// </param>
        private bool ServicePendingInterrupt(bool firstCycleElapsed)
        {
            if (!_interruptHandler.InterruptsEnabled || !_interruptHandler.HasPendingInterrupt())
            {
                return false;
            }

            _interruptHandler.InterruptsEnabled = false;
            _interruptHandler.Halted = false;

            if (!firstCycleElapsed)
            {
                IncrementClock();
            }

            // Interrupt entry has one additional internal M-cycle before the two stack writes.
            IncrementClock();
            WriteByte((byte)(_pc >> 8), --_sp.SP);

            // The upper-byte stack write can change IE, so priority and cancellation are resolved afterwards.
            var interrupt = _interruptHandler.GetHighestPriorityPendingInterrupt();
            WriteByte((byte)_pc, --_sp.SP);

            if (interrupt < 0)
            {
                _pc = 0;
            }
            else
            {
                _interruptHandler.ClearInterruptRequest(interrupt);
                _pc = _interruptHandler.GetServiceRoutine(interrupt);
            }

            IncrementClock();
            return true;
        }

        /// <summary>
        /// Implements DI by disabling IME immediately and cancelling a delayed EI that has not taken effect.
        /// </summary>
        private void DisableInterrupts()
        {
            _interruptHandler.InterruptsEnabled = false;
            _pendingInterruptEnabled = -1;
        }

        /// <summary>
        /// Implements EI's one-instruction delay without allowing repeated EI instructions to restart that delay.
        /// </summary>
        private void EnableInterruptsDelayed()
        {
            if (!_interruptHandler.InterruptsEnabled && _pendingInterruptEnabled < 0)
            {
                _pendingInterruptEnabled = 1;
            }
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
