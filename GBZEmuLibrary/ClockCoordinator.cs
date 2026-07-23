namespace GBZEmuLibrary
{
    /// <summary>
    /// Tracks CPU T-state position and emits base-speed clocks without losing odd CGB double-speed clocks.
    /// </summary>
    internal sealed class ClockCoordinator
    {
        private int _rawClockInMachineCycle;
        private int _baseClockDividerPhase;

        /// <summary>
        /// Gets whether the coordinator is aligned to the start of a CPU machine cycle.
        /// </summary>
        internal bool IsMachineCycleAligned => _rawClockInMachineCycle == 0;

        /// <summary>
        /// Gets the zero-based raw-clock position used by focused internal state tests.
        /// </summary>
        internal int RawClockInMachineCycle => _rawClockInMachineCycle;

        /// <summary>
        /// Gets the retained double-speed divider phase used by focused internal state tests.
        /// </summary>
        internal int BaseClockDividerPhase => _baseClockDividerPhase;

        /// <summary>
        /// Restores initial machine-cycle and base-divider alignment.
        /// </summary>
        internal void Reset()
        {
            _rawClockInMachineCycle = 0;
            _baseClockDividerPhase = 0;
        }

        /// <summary>
        /// Advances one raw CPU clock and reports its T-state and whether it emits one base-speed clock.
        /// </summary>
        internal ClockAdvance AdvanceRawClock(int speedFactor)
        {
            var tState = _rawClockInMachineCycle + 1;
            _rawClockInMachineCycle = tState == InstructionSchema.FOUR_CYCLES ? 0 : tState;

            bool emitsBaseClock;
            if (speedFactor == 1)
            {
                // Normal speed emits every raw clock and establishes the divider alignment used by a later switch.
                _baseClockDividerPhase = 0;
                emitsBaseClock = true;
            }
            else if (speedFactor == 2)
            {
                _baseClockDividerPhase ^= 1;
                emitsBaseClock = _baseClockDividerPhase == 0;
            }
            else
            {
                throw new System.ArgumentOutOfRangeException(nameof(speedFactor));
            }

            return new ClockAdvance(tState, emitsBaseClock);
        }
    }

    /// <summary>
    /// Describes one raw CPU-clock advancement without allocating scheduler state.
    /// </summary>
    internal readonly struct ClockAdvance
    {
        internal ClockAdvance(int tState, bool emitsBaseClock)
        {
            TState = tState;
            EmitsBaseClock = emitsBaseClock;
        }

        internal int TState { get; }
        internal bool EmitsBaseClock { get; }
    }
}
