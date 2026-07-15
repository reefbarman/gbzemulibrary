namespace GBZEmuLibrary
{
    /// <summary>
    /// Emulates the MBC3 real-time clock's live registers, latched read snapshot, halt state, and day carry.
    /// Persistence across emulator sessions is handled separately from this in-session clock state.
    /// </summary>
    internal sealed class MBC3RTC
    {
        internal const byte SecondsRegister = 0x08;
        internal const byte MinutesRegister = 0x09;
        internal const byte HoursRegister = 0x0A;
        internal const byte DaysLowRegister = 0x0B;
        internal const byte DaysHighRegister = 0x0C;

        private const byte HaltMask = 0x40;
        private const byte CarryMask = 0x80;

        private readonly byte[] _liveRegisters = new byte[5];
        private readonly byte[] _latchedRegisters = new byte[5];
        private int _subSecondClocks;

        /// <summary>
        /// Advances the live clock by base-speed Game Boy clocks while preserving fractional-second phase.
        /// </summary>
        public void Update(int clocks)
        {
            if ((_liveRegisters[RegisterIndex(DaysHighRegister)] & HaltMask) != 0)
            {
                return;
            }

            _subSecondClocks += clocks;
            while (_subSecondClocks >= GameBoySchema.MAX_DMG_CLOCK_CYCLES)
            {
                _subSecondClocks -= GameBoySchema.MAX_DMG_CLOCK_CYCLES;
                IncrementSecond();
            }
        }

        /// <summary>
        /// Copies the live registers into the read snapshot without resetting the clock or sub-second phase.
        /// </summary>
        public void Latch()
        {
            for (var index = 0; index < _liveRegisters.Length; index++)
            {
                _latchedRegisters[index] = _liveRegisters[index];
            }
        }

        /// <summary>
        /// Reads the selected register from the most recently latched snapshot.
        /// </summary>
        public byte Read(byte register)
        {
            return IsRegister(register) ? _latchedRegisters[RegisterIndex(register)] : (byte)0xFF;
        }

        /// <summary>
        /// Writes a live RTC register using its hardware-visible bit width; seconds writes reset divider phase.
        /// </summary>
        public void Write(byte register, byte value)
        {
            if (!IsRegister(register))
            {
                return;
            }

            switch (register)
            {
                case SecondsRegister:
                    value &= 0x3F;
                    _subSecondClocks = 0;
                    break;
                case MinutesRegister:
                    value &= 0x3F;
                    break;
                case HoursRegister:
                    value &= 0x1F;
                    break;
                case DaysHighRegister:
                    value &= 0xC1;
                    break;
            }

            _liveRegisters[RegisterIndex(register)] = value;
        }

        /// <summary>
        /// Returns whether the selector maps one of the five MBC3 RTC registers.
        /// </summary>
        public static bool IsRegister(byte register)
        {
            return register >= SecondsRegister && register <= DaysHighRegister;
        }

        private static int RegisterIndex(byte register)
        {
            return register - SecondsRegister;
        }

        private void IncrementSecond()
        {
            var secondsIndex = RegisterIndex(SecondsRegister);
            var seconds = _liveRegisters[secondsIndex];
            if (seconds == 59)
            {
                _liveRegisters[secondsIndex] = 0;
                IncrementMinute();
                return;
            }

            // Hardware masks out-of-range values but only the normal terminal value carries to the next register.
            _liveRegisters[secondsIndex] = (byte)((seconds + 1) & 0x3F);
        }

        private void IncrementMinute()
        {
            var minutesIndex = RegisterIndex(MinutesRegister);
            var minutes = _liveRegisters[minutesIndex];
            if (minutes == 59)
            {
                _liveRegisters[minutesIndex] = 0;
                IncrementHour();
                return;
            }

            _liveRegisters[minutesIndex] = (byte)((minutes + 1) & 0x3F);
        }

        private void IncrementHour()
        {
            var hoursIndex = RegisterIndex(HoursRegister);
            var hours = _liveRegisters[hoursIndex];
            if (hours == 23)
            {
                _liveRegisters[hoursIndex] = 0;
                IncrementDay();
                return;
            }

            _liveRegisters[hoursIndex] = (byte)((hours + 1) & 0x1F);
        }

        private void IncrementDay()
        {
            var daysLowIndex = RegisterIndex(DaysLowRegister);
            var daysHighIndex = RegisterIndex(DaysHighRegister);
            var day = _liveRegisters[daysLowIndex] | ((_liveRegisters[daysHighIndex] & 0x01) << 8);
            day++;

            if (day > 0x1FF)
            {
                day = 0;
                _liveRegisters[daysHighIndex] |= CarryMask;
            }

            _liveRegisters[daysLowIndex] = (byte)day;
            _liveRegisters[daysHighIndex] = (byte)((_liveRegisters[daysHighIndex] & 0xC0) | (day >> 8));
        }
    }
}
