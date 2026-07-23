using System;

namespace GBZEmuLibrary
{
    /// <summary>
    /// Emulates the serial data and control registers used by the link port and test-ROM diagnostics.
    /// </summary>
    internal sealed class SerialRegisters : IMemoryUnit
    {
        private const byte TRANSFER_ENABLE_MASK = 0x80;
        private const byte INTERNAL_CLOCK_MASK = 0x01;
        private const byte FAST_CLOCK_MASK = 0x02;
        private const byte DMG_CONTROL_WRITABLE_MASK = TRANSFER_ENABLE_MASK | INTERNAL_CLOCK_MASK;
        private const byte DMG_CONTROL_UNUSED_BITS = 0x7E;
        private const byte CGB_CONTROL_WRITABLE_MASK = TRANSFER_ENABLE_MASK | FAST_CLOCK_MASK | INTERNAL_CLOCK_MASK;
        private const byte CGB_CONTROL_UNUSED_BITS = 0x7C;
        private const int SLOW_CLOCK_DIVIDER_BIT = 8;
        private const int FAST_CLOCK_DIVIDER_BIT = 3;
        private const int TRANSFER_BITS = 8;
        private const ushort DMG_POST_BOOT_CLOCK_DIVIDER = 0xABC8;

        public event Action<byte> ByteTransferred;

        private readonly MessageBus _messageBus;
        private byte _data;
        private byte _control;
        private byte _outgoingData;
        private ushort _clockDivider;
        private int _shiftedBits;
        private bool _gbcMode;

        /// <summary>
        /// Creates serial state connected to the interrupt bus for its owning emulator.
        /// </summary>
        public SerialRegisters(MessageBus messageBus)
        {
            _messageBus = messageBus;
        }

        /// <summary>
        /// Selects whether SC bit 1 is the CGB fast-clock control or an unused DMG read-high bit.
        /// </summary>
        public void Init(GBCMode mode)
        {
            _gbcMode = mode != GBCMode.NoGBC;
            _data = 0;
            _control = 0;
            _outgoingData = 0;
            _clockDivider = 0;
            _shiftedBits = 0;
        }

        /// <summary>
        /// Restores the serial clock phase produced by the selected startup path.
        /// </summary>
        public void Reset(bool usingBootROM)
        {
            // The serial clock has an independent reset-aligned divider. DMG skip reproduces the phase measured by
            // Mooneye's boot_sclk_align fixture; boot-ROM execution advances naturally from hardware reset at zero.
            _clockDivider = usingBootROM || _gbcMode ? (ushort)0 : DMG_POST_BOOT_CLOCK_DIVIDER;
            _data = 0;
            _control = 0;
            _outgoingData = 0;
            _shiftedBits = 0;
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
        /// Advances an internally clocked transfer from falling edges of the reset-aligned serial divider.
        /// </summary>
        public void Update(int cycles)
        {
            var dividerBit = _gbcMode && (_control & FAST_CLOCK_MASK) != 0
                ? FAST_CLOCK_DIVIDER_BIT
                : SLOW_CLOCK_DIVIDER_BIT;

            for (var i = 0; i < cycles; i++)
            {
                var oldClockSignal = (_clockDivider & (1 << dividerBit)) != 0;
                _clockDivider++;
                var newClockSignal = (_clockDivider & (1 << dividerBit)) != 0;

                if (TransferUsesInternalClock() && oldClockSignal && !newClockSignal)
                {
                    ShiftBit();
                }
            }
        }

        /// <summary>
        /// Writes the serial data or control register and starts or cancels transfers through SC bit 7.
        /// </summary>
        public void WriteByte(byte data, int address)
        {
            switch (address)
            {
                case MemorySchema.SERIAL_DATA_REGISTER:
                    _data = data;
                    return;
                case MemorySchema.SERIAL_CONTROL_REGISTER:
                    var transferWasEnabled = (_control & TRANSFER_ENABLE_MASK) != 0;
                    _control = (byte)(data & (_gbcMode ? CGB_CONTROL_WRITABLE_MASK : DMG_CONTROL_WRITABLE_MASK));

                    if (!transferWasEnabled && (_control & TRANSFER_ENABLE_MASK) != 0)
                    {
                        _outgoingData = _data;
                        _shiftedBits = 0;
                    }

                    return;
                default:
                    throw new IndexOutOfRangeException();
            }
        }

        /// <summary>
        /// Shifts one disconnected-link input bit high and completes the transfer after eight serial clocks.
        /// </summary>
        private void ShiftBit()
        {
            _data = (byte)((_data << 1) | 0x01);
            _shiftedBits++;
            var timingEvent = new TimingEvent(TimingEventKind.SerialBitShifted, value: (byte)_shiftedBits);
            _messageBus.ObserveTiming(in timingEvent);

            if (_shiftedBits < TRANSFER_BITS)
            {
                return;
            }

            _control &= unchecked((byte)~TRANSFER_ENABLE_MASK);
            ByteTransferred?.Invoke(_outgoingData);
            _messageBus.RequestInterrupt(Interrupts.Serial);
        }

        /// <summary>
        /// Returns whether SC requests a transfer driven by the Game Boy's internal serial clock.
        /// </summary>
        private bool TransferUsesInternalClock()
        {
            return (_control & (TRANSFER_ENABLE_MASK | INTERNAL_CLOCK_MASK)) ==
                   (TRANSFER_ENABLE_MASK | INTERNAL_CLOCK_MASK);
        }
    }
}
