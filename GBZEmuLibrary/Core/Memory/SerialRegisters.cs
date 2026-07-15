using System;

namespace GBZEmuLibrary
{
    /// <summary>
    /// Emulates the serial data and control registers used by the link port and test-ROM diagnostics.
    /// </summary>
    internal sealed class SerialRegisters : IMemoryUnit
    {
        private const byte DMG_CONTROL_WRITABLE_MASK = 0x81;
        private const byte DMG_CONTROL_UNUSED_BITS = 0x7E;
        private const byte CGB_CONTROL_WRITABLE_MASK = 0x83;
        private const byte CGB_CONTROL_UNUSED_BITS = 0x7C;

        public event Action<byte> ByteTransferred;

        private byte _data;
        private byte _control;
        private bool _gbcMode;

        /// <summary>
        /// Selects whether SC bit 1 is the CGB fast-clock control or an unused DMG read-high bit.
        /// </summary>
        public void Init(GBCMode mode)
        {
            _gbcMode = mode != GBCMode.NoGBC;
        }

        /// <summary>
        /// Returns whether this device owns the serial data or control register.
        /// </summary>
        public bool CanReadWriteByte(int address)
        {
            return address == MemorySchema.SERIAL_DATA_REGISTER ||
                   address == MemorySchema.SERIAL_CONTROL_REGISTER;
        }

        /// <summary>
        /// Reads serial state with the DMG control register's unused bits pulled high.
        /// </summary>
        public byte ReadByte(int address)
        {
            switch (address)
            {
                case MemorySchema.SERIAL_DATA_REGISTER:
                    return _data;
                case MemorySchema.SERIAL_CONTROL_REGISTER:
                    return (byte)(_control | (_gbcMode ? CGB_CONTROL_UNUSED_BITS : DMG_CONTROL_UNUSED_BITS));
                default:
                    throw new IndexOutOfRangeException();
            }
        }

        /// <summary>
        /// Writes serial state and completes internal-clock diagnostic transfers immediately.
        /// </summary>
        public void WriteByte(byte data, int address)
        {
            switch (address)
            {
                case MemorySchema.SERIAL_DATA_REGISTER:
                    _data = data;
                    return;
                case MemorySchema.SERIAL_CONTROL_REGISTER:
                    _control = (byte)(data & (_gbcMode ? CGB_CONTROL_WRITABLE_MASK : DMG_CONTROL_WRITABLE_MASK));

                    if (Helpers.TestBit(_control, 7) && Helpers.TestBit(_control, 0))
                    {
                        _control &= 0x7F;
                        ByteTransferred?.Invoke(_data);
                    }

                    return;
                default:
                    throw new IndexOutOfRangeException();
            }
        }
    }
}
