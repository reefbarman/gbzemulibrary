namespace GBZEmuLibrary
{
    /// <summary>
    /// Represents permanently unused DMG I/O addresses that ignore writes and return an open-bus value of 0xFF.
    /// </summary>
    internal sealed class UnmappedIO : IMemoryUnit
    {
        /// <summary>
        /// Returns whether the address is one of the fixed holes between the serial, timer, and interrupt registers.
        /// </summary>
        public bool CanReadWriteByte(int address)
        {
            return address == MemorySchema.UNUSED_IO_REGISTER ||
                   address >= MemorySchema.UNUSED_IO_RANGE_START && address < MemorySchema.UNUSED_IO_RANGE_END;
        }

        /// <summary>
        /// Returns the pull-up value exposed by unused I/O registers.
        /// </summary>
        public byte ReadByte(int address)
        {
            return 0xFF;
        }

        /// <summary>
        /// Ignores writes because these addresses have no backing hardware register.
        /// </summary>
        public void WriteByte(byte data, int address)
        {
        }
    }
}
