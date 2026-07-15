namespace GBZEmuLibrary
{
    /// <summary>
    /// Exposes the TIMA, TMA, and TAC registers of the shared timer state to the MMU.
    /// </summary>
    internal class Timer : IMemoryUnit
    {
        private readonly TimerState _timerState;

        /// <summary>
        /// Creates the programmable-timer register adapter for the supplied timer state.
        /// </summary>
        public Timer(TimerState timerState)
        {
            _timerState = timerState;
        }

        /// <summary>
        /// Writes a programmable-timer register and applies its hardware side effects.
        /// </summary>
        public void WriteByte(byte data, int address)
        {
            _timerState.WriteTimer(data, address);
        }

        /// <summary>
        /// Returns whether this device owns the TIMA, TMA, or TAC address.
        /// </summary>
        public bool CanReadWriteByte(int address)
        {
            return address >= MemorySchema.TIMER_START && address < MemorySchema.TIMER_END;
        }

        /// <summary>
        /// Reads a programmable-timer register through its hardware-visible representation.
        /// </summary>
        public byte ReadByte(int address)
        {
            return _timerState.ReadTimer(address);
        }
    }
}
