namespace GBZEmuLibrary
{
    /// <summary>
    /// Exposes the DIV register view of the shared system counter to the MMU.
    /// </summary>
    internal class DivideRegister : IMemoryUnit
    {
        private readonly TimerState _timerState;

        /// <summary>
        /// Creates the DIV register adapter for the supplied timer state.
        /// </summary>
        public DivideRegister(TimerState timerState)
        {
            _timerState = timerState;
        }

        /// <summary>
        /// Resets the complete system counter, as any write to DIV does on hardware.
        /// </summary>
        public void WriteByte(byte data, int address)
        {
            _timerState.WriteDivider();
        }

        /// <summary>
        /// Returns whether this device owns the DIV register address.
        /// </summary>
        public bool CanReadWriteByte(int address)
        {
            return address == MemorySchema.DIVIDE_REGISTER;
        }

        /// <summary>
        /// Reads the visible upper byte of the shared system counter.
        /// </summary>
        public byte ReadByte(int address)
        {
            return _timerState.ReadDivider();
        }
    }
}
