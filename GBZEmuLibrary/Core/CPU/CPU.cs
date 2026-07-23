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
        private bool _haltWakeOpcodePending;
        private byte _haltWakeOpcode;
        private bool _pendingSpeedSwitch;
        private bool _doubleSpeed;
        [SaveStateIgnore]
        private ITimingObserver _timingObserver;

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
            messageBus.OnIsCpuHalted = () => _interruptHandler.Halted;
            messageBus.OnGetCpuSpeedFactor = () => SpeedFactor;

            InitInstructions();
        }

        /// <summary>
        /// Executes one instruction, HALT cycle, or interrupt-dispatch sequence.
        /// </summary>
        public bool Process()
        {
            if (_mmu.IsCpuStalledByHBlankDma)
            {
                IncrementClock();
                return true;
            }

            if (_interruptHandler.Halted)
            {
                var wakeFetch = _mmu.BeginCpuRead(_pc, CpuMachineCycleKind.OpcodeFetch);
                AdvanceMachineCycle();
                if (_interruptHandler.Halted)
                {
                    return true;
                }

                var wakeOpcode = CompleteCpuReadAndObserve(in wakeFetch);
                if (ServicePendingInterrupt(true))
                {
                    return true;
                }

                // An IME-clear wake consumes this elapsed cycle as the next opcode fetch.
                _haltWakeOpcode = wakeOpcode;
                _haltWakeOpcodePending = true;
                return true;
            }

            if (Debug())
            {
                return false;
            }

            var instructionAddress = _pc;
            byte instruction;
            if (_haltWakeOpcodePending)
            {
                instruction = _haltWakeOpcode;
                _haltWakeOpcode = 0;
                _haltWakeOpcodePending = false;
                _pc++;
            }
            else
            {
                if (ServicePendingInterrupt(false))
                {
                    return true;
                }

                instruction = ReadByte(_pc++, CpuMachineCycleKind.OpcodeFetch);
            }

            // An interrupt asserted during opcode fetch suppresses that instruction and uses the fetch as M1.
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
                throw new NotImplementedException($"Instruction not implemented: {instruction:X2} at {instructionAddress:X4}");
            }

            _instructionCount++;

            if (_pendingInterruptEnabled >= 0 && _pendingInterruptEnabled-- == 0)
            {
                _interruptHandler.InterruptsEnabled = true;
            }

            return true;
        }

        /// <summary>
        /// Installs an optional internal observer for current machine-cycle and bus-access boundaries.
        /// </summary>
        internal void SetTimingObserver(ITimingObserver timingObserver)
        {
            _timingObserver = timingObserver;
        }

        /// <summary>
        /// Resets CPU registers, execution state, and startup values for the selected hardware mode.
        /// </summary>
        public void Reset(
            bool usingBootROM,
            HardwareModel hardwareModel,
            GBCMode gbcMode,
            HardwareStartupProfile startupProfile = null)
        {
            _gbcMode = gbcMode;
            _pendingInterruptEnabled = -1;
            _haltWakeOpcodePending = false;
            _haltWakeOpcode = 0;
            _pendingSpeedSwitch = false;
            _doubleSpeed = false;
            _instructionCount = 0;

            if (usingBootROM)
            {
                _registers.A = (byte)(_gbcMode != GBCMode.NoGBC ? 0x11 : 0x01);
            }
            else if (startupProfile != null)
            {
                ApplyStartupProfile(startupProfile);
            }
            else
            {
                switch (hardwareModel)
                {
                    case HardwareModel.DmgB:
                        _registers.AF = 0x01B0;
                        _registers.BC = 0x0013;
                        _registers.DE = 0x00D8;
                        _registers.HL = 0x014D;
                        break;
                    case HardwareModel.Mgb:
                        _registers.AF = 0xFFB0;
                        _registers.BC = 0x0013;
                        _registers.DE = 0x00D8;
                        _registers.HL = 0x014D;
                        break;
                    case HardwareModel.CgbE:
                        _registers.AF = 0x11B0;
                        _registers.BC = 0x0013;
                        _registers.DE = 0x00D8;
                        _registers.HL = 0x014D;
                        break;
                    case HardwareModel.Sgb2:
                        _registers.AF = 0xFF00;
                        _registers.BC = 0x0014;
                        _registers.DE = 0x0000;
                        _registers.HL = 0xC060;
                        break;
                    default:
                        throw new InvalidOperationException(
                            $"Hardware model {hardwareModel} does not have an implemented skip-boot CPU profile.");
                }

                _sp.SP = 0xFFFE;
                _pc = 0x100;
            }
        }

        private void ApplyStartupProfile(HardwareStartupProfile profile)
        {
            _registers.AF = profile.AF;
            _registers.BC = profile.BC;
            _registers.DE = profile.DE;
            _registers.HL = profile.HL;
            _sp.SP = profile.SP;
            _pc = profile.PC;
        }

        /// <summary>
        /// Services the highest-priority pending interrupt through five explicitly named machine cycles.
        /// </summary>
        /// <param name="firstCycleElapsed">
        /// Whether M1 was already consumed by HALT or a suppressed opcode fetch.
        /// </param>
        private bool ServicePendingInterrupt(bool firstCycleElapsed)
        {
            if (!_interruptHandler.InterruptsEnabled || !_interruptHandler.HasPendingInterrupt())
            {
                return false;
            }

            if (_haltSkip)
            {
                // EI followed by a bugged HALT services the buffered interrupt before another opcode fetch,
                // but the suppressed PC increment still makes the handler return to the HALT instruction.
                _pc = (ushort)(_pc - 1);
                _haltSkip = false;
            }

            _interruptHandler.InterruptsEnabled = false;
            _interruptHandler.Halted = false;
            var returnProgramCounter = _pc;

            ObserveInterruptDispatchCycle(InterruptDispatchCycle.First);
            if (!firstCycleElapsed)
            {
                IncrementClock();
            }

            ObserveInterruptDispatchCycle(InterruptDispatchCycle.Internal);
            IncrementClock();

            ObserveInterruptDispatchCycle(InterruptDispatchCycle.HighStackWrite);
            WriteByte((byte)(returnProgramCounter >> 8), --_sp.SP);

            // The upper-byte stack write can change IE, so priority and cancellation are resolved afterwards.
            var interrupt = _interruptHandler.GetHighestPriorityPendingInterrupt();
            ObserveTiming(new TimingEvent(
                TimingEventKind.InterruptSelected,
                value: unchecked((byte)interrupt)));

            ushort serviceRoutine = 0;
            if (interrupt >= 0)
            {
                // Acknowledgement precedes the low-write transaction so requests raised during M4 survive.
                _interruptHandler.ClearInterruptRequest(interrupt);
                ObserveTiming(new TimingEvent(
                    TimingEventKind.InterruptAcknowledged,
                    value: (byte)interrupt));
                serviceRoutine = _interruptHandler.GetServiceRoutine(interrupt);
            }

            ObserveInterruptDispatchCycle(InterruptDispatchCycle.LowStackWrite);
            WriteByte((byte)returnProgramCounter, --_sp.SP);

            _pc = serviceRoutine;
            ObserveInterruptDispatchCycle(InterruptDispatchCycle.Final);
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
            var instruction = ReadByte(_pc++, CpuMachineCycleKind.OpcodeFetch);

            if (_instructionsCB.ContainsKey(instruction))
            {
                _instructionsCB[instruction]();
            }
            else
            {
                throw new NotImplementedException($"CB instruction not implemented: {instruction:X2} at {(ushort)(_pc - 1):X4}");
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
