namespace GBZEmuLibrary
{
    /// <summary>
    /// Emulates the Game Boy's shared 16-bit system counter, DIV register, and programmable TIMA timer.
    /// </summary>
    internal sealed class TimerState
    {
        private const int OVERFLOW_RELOAD_DELAY = 4;
        private const ushort DMG_POST_BOOT_SYSTEM_COUNTER = 0xABCC;
        private const ushort CGB_FALLBACK_SYSTEM_COUNTER = 0;
        private const byte TIMER_CONTROL_WRITABLE_MASK = 0x07;
        private const byte TIMER_CONTROL_UNUSED_BITS = 0xF8;
        private const byte TIMER_ENABLE_MASK = 0x04;
        private const byte TIMER_CONTROL_FREQUENCY_MASK = 0x03;
        private const int TIMER_DIVIDER_BIT_4096_HZ = 9;
        private const int TIMER_DIVIDER_BIT_262144_HZ = 3;
        private const int TIMER_DIVIDER_BIT_65536_HZ = 5;
        private const int TIMER_DIVIDER_BIT_16384_HZ = 7;
        private const int APU_DIVIDER_BIT_NORMAL_SPEED = 12;
        private const int APU_DIVIDER_BIT_DOUBLE_SPEED = 13;

        private ushort _systemCounter;
        private byte _tima;
        private byte _tma;
        private byte _tac;
        private int _overflowReloadClocks;
        private bool _reloadedThisMachineCycle;
        private bool _gbcMode;
        private bool _doubleSpeed;

        private readonly MessageBus _messageBus;

        /// <summary>
        /// Signals a falling edge of the DIV-APU source used by the audio frame sequencer.
        /// </summary>
        public System.Action OnApuClock;

        /// <summary>
        /// Creates timer state connected to the interrupt bus for its owning emulator.
        /// </summary>
        public TimerState(MessageBus messageBus)
        {
            _messageBus = messageBus;
        }

        /// <summary>
        /// Resets the counter and timer registers to the state produced by the selected startup path.
        /// </summary>
        public void Reset(bool usingBootROM, GBCMode mode)
        {
            // DMG skip reproduces the measured DMG ABC phase. CGB boot duration varies with cartridge data and
            // input, so the firmware-free fallback starts deterministically at zero instead of claiming DMG phase.
            _systemCounter = usingBootROM || mode != GBCMode.NoGBC
                ? CGB_FALLBACK_SYSTEM_COUNTER
                : DMG_POST_BOOT_SYSTEM_COUNTER;
            _tima = 0;
            _tma = 0;
            _tac = 0;
            _overflowReloadClocks = 0;
            _reloadedThisMachineCycle = false;
            _gbcMode = mode != GBCMode.NoGBC;
            _doubleSpeed = false;
        }

        /// <summary>
        /// Advances the system counter and clocks TIMA from falling edges of the TAC-selected counter bit.
        /// </summary>
        public void Update(int cycles)
        {
            _reloadedThisMachineCycle = false;

            for (var i = 0; i < cycles; i++)
            {
                // TIMA stays at 0 for one M-cycle after overflow before TMA is loaded and IF is requested.
                if (_overflowReloadClocks > 0 && --_overflowReloadClocks == 0)
                {
                    _tima = _tma;
                    _reloadedThisMachineCycle = true;
                    _messageBus.RequestInterrupt(Interrupts.Timer);
                }

                var oldTimerSignal = TimerSignal(_systemCounter, _tac);
                var oldApuSignal = ApuSignal(_systemCounter, _doubleSpeed);
                _systemCounter++;
                var newTimerSignal = TimerSignal(_systemCounter, _tac);
                var newApuSignal = ApuSignal(_systemCounter, _doubleSpeed);

                if (oldTimerSignal && !newTimerSignal)
                {
                    IncrementTimer();
                }

                if (oldApuSignal && !newApuSignal)
                {
                    OnApuClock?.Invoke();
                }
            }
        }

        /// <summary>
        /// Reads the visible upper byte of the internal 16-bit system counter.
        /// </summary>
        public byte ReadDivider()
        {
            return (byte)(_systemCounter >> 8);
        }

        /// <summary>
        /// Resets the full system counter and applies any timer edge caused by that reset.
        /// </summary>
        public void WriteDivider()
        {
            var oldTimerSignal = TimerSignal(_systemCounter, _tac);
            var oldApuSignal = ApuSignal(_systemCounter, _doubleSpeed);
            _systemCounter = 0;

            // Resetting a selected high divider bit creates the same falling edge as normal counting.
            if (oldTimerSignal)
            {
                IncrementTimer();
            }

            if (oldApuSignal)
            {
                OnApuClock?.Invoke();
            }
        }

        /// <summary>
        /// Selects the system-counter bit that keeps DIV-APU at 512 Hz in the active CPU speed mode.
        /// </summary>
        public void SetDoubleSpeed(bool enabled)
        {
            _doubleSpeed = enabled;
        }

        /// <summary>
        /// Reads TIMA, TMA, or TAC with the register's hardware-visible bit behavior.
        /// </summary>
        public byte ReadTimer(int address)
        {
            switch (address)
            {
                case MemorySchema.TIMA:
                    return _tima;
                case MemorySchema.TMA:
                    return _tma;
                case MemorySchema.TMC:
                    return (byte)(_tac | TIMER_CONTROL_UNUSED_BITS);
                default:
                    throw new System.IndexOutOfRangeException();
            }
        }

        /// <summary>
        /// Writes TIMA, TMA, or TAC while preserving overflow and edge-detector side effects.
        /// </summary>
        public void WriteTimer(byte data, int address)
        {
            switch (address)
            {
                case MemorySchema.TIMA:
                    // During the reload M-cycle, TMA continuously drives TIMA and overwrites CPU writes.
                    if (_reloadedThisMachineCycle)
                    {
                        return;
                    }

                    // A TIMA write during the preceding overflow-delay cycle cancels the pending reload.
                    _tima = data;
                    _overflowReloadClocks = 0;
                    break;
                case MemorySchema.TMA:
                    _tma = data;
                    // TMA also drives TIMA for the whole reload M-cycle, so both registers change together.
                    if (_reloadedThisMachineCycle)
                    {
                        _tima = data;
                    }
                    break;
                case MemorySchema.TMC:
                    WriteTimerControl(data);
                    break;
                default:
                    throw new System.IndexOutOfRangeException();
            }
        }

        /// <summary>
        /// Updates TAC and clocks TIMA when changing the selected input creates a hardware falling edge.
        /// </summary>
        private void WriteTimerControl(byte data)
        {
            var oldTimerSignal = TimerSignal(_systemCounter, _tac);
            var oldTimerEnabled = TimerEnabled(_tac);
            _tac = (byte)(data & TIMER_CONTROL_WRITABLE_MASK);
            var newTimerSignal = TimerSignal(_systemCounter, _tac);
            var disablingTimer = oldTimerEnabled && !TimerEnabled(_tac);

            // DMG clocks TIMA when an active timer is disabled; CGB suppresses that specific edge.
            if (oldTimerSignal && !newTimerSignal && (!_gbcMode || !disablingTimer))
            {
                IncrementTimer();
            }
        }

        /// <summary>
        /// Applies a timer input pulse and schedules the delayed reload when TIMA overflows.
        /// </summary>
        private void IncrementTimer()
        {
            if (_overflowReloadClocks > 0)
            {
                return;
            }

            _tima++;
            if (_tima == 0)
            {
                _overflowReloadClocks = OVERFLOW_RELOAD_DELAY;
            }
        }

        /// <summary>
        /// Returns the system-counter signal observed by the audio frame sequencer.
        /// </summary>
        private static bool ApuSignal(ushort systemCounter, bool doubleSpeed)
        {
            var bit = doubleSpeed ? APU_DIVIDER_BIT_DOUBLE_SPEED : APU_DIVIDER_BIT_NORMAL_SPEED;
            return (systemCounter & (1 << bit)) != 0;
        }

        /// <summary>
        /// Returns the gated system-counter signal observed by TIMA's falling-edge detector.
        /// </summary>
        private static bool TimerSignal(ushort systemCounter, byte tac)
        {
            if (!TimerEnabled(tac))
            {
                return false;
            }

            int bit;

            switch (tac & TIMER_CONTROL_FREQUENCY_MASK)
            {
                case 0:
                    bit = TIMER_DIVIDER_BIT_4096_HZ;
                    break;
                case 1:
                    bit = TIMER_DIVIDER_BIT_262144_HZ;
                    break;
                case 2:
                    bit = TIMER_DIVIDER_BIT_65536_HZ;
                    break;
                default:
                    bit = TIMER_DIVIDER_BIT_16384_HZ;
                    break;
            }

            return (systemCounter & (1 << bit)) != 0;
        }

        /// <summary>
        /// Returns whether TAC enables the programmable timer input.
        /// </summary>
        private static bool TimerEnabled(byte tac)
        {
            return (tac & TIMER_ENABLE_MASK) != 0;
        }
    }
}
