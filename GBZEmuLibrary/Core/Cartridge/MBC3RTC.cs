using System;

namespace GBZEmuLibrary
{
    /// <summary>
    /// Emulates MBC3 live/latched clock registers, halt and carry state, plus BGB-compatible persistence data.
    /// </summary>
    internal sealed class MBC3RTC
    {
        internal const byte SecondsRegister = 0x08;
        internal const byte MinutesRegister = 0x09;
        internal const byte HoursRegister = 0x0A;
        internal const byte DaysLowRegister = 0x0B;
        internal const byte DaysHighRegister = 0x0C;
        internal const int PersistenceSize = 48;

        private const int LegacyPersistenceSize = 44;
        private const int SecondsPerMinute = 60;
        private const int SecondsPerHour = 60 * SecondsPerMinute;
        private const int SecondsPerDay = 24 * SecondsPerHour;
        private const int DaysPerCycle = 512;
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

            if (register == SecondsRegister)
            {
                _subSecondClocks = 0;
            }

            _liveRegisters[RegisterIndex(register)] = MaskRegister(register, value);
        }

        /// <summary>
        /// Serializes live and latched registers plus a UTC timestamp using the BGB-compatible 48-byte RTC trailer.
        /// </summary>
        public byte[] Save(long unixTimestamp)
        {
            var data = new byte[PersistenceSize];
            for (var index = 0; index < _liveRegisters.Length; index++)
            {
                WriteInt32(data, index * 4, _liveRegisters[index]);
                WriteInt32(data, 20 + (index * 4), _latchedRegisters[index]);
            }

            WriteInt64(data, 40, unixTimestamp);
            return data;
        }

        /// <summary>
        /// Restores a BGB-compatible 44- or 48-byte RTC trailer and applies elapsed UTC seconds to the live clock.
        /// </summary>
        public bool Load(byte[] data, long currentUnixTimestamp)
        {
            if (data == null || (data.Length != LegacyPersistenceSize && data.Length != PersistenceSize))
            {
                return false;
            }

            for (var index = 0; index < _liveRegisters.Length; index++)
            {
                _liveRegisters[index] = MaskRegister((byte)(SecondsRegister + index), (byte)ReadInt32(data, index * 4));
                _latchedRegisters[index] = MaskRegister((byte)(SecondsRegister + index), (byte)ReadInt32(data, 20 + (index * 4)));
            }

            _subSecondClocks = 0;
            var savedTimestamp = data.Length == PersistenceSize
                ? ReadInt64(data, 40)
                : (long)(uint)ReadInt32(data, 40);
            if (savedTimestamp >= 0 && currentUnixTimestamp >= 0 &&
                currentUnixTimestamp > savedTimestamp && !IsHalted())
            {
                AdvanceSeconds(currentUnixTimestamp - savedTimestamp);
            }

            return true;
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

        private static byte MaskRegister(byte register, byte value)
        {
            switch (register)
            {
                case SecondsRegister:
                case MinutesRegister:
                    return (byte)(value & 0x3F);
                case HoursRegister:
                    return (byte)(value & 0x1F);
                case DaysHighRegister:
                    return (byte)(value & 0xC1);
                default:
                    return value;
            }
        }

        private bool IsHalted()
        {
            return (_liveRegisters[RegisterIndex(DaysHighRegister)] & HaltMask) != 0;
        }

        private void AdvanceSeconds(long seconds)
        {
            // Invalid masked register values have special one-tick behavior; normalize them before bulk arithmetic.
            while (seconds > 0 && HasOutOfRangeTime())
            {
                IncrementSecond();
                seconds--;
            }

            if (seconds == 0)
            {
                return;
            }

            var daysHighIndex = RegisterIndex(DaysHighRegister);
            var day = _liveRegisters[RegisterIndex(DaysLowRegister)] |
                      ((_liveRegisters[daysHighIndex] & 0x01) << 8);
            var currentSecondsWithinDay = _liveRegisters[RegisterIndex(SecondsRegister)] +
                                          (_liveRegisters[RegisterIndex(MinutesRegister)] * SecondsPerMinute) +
                                          (_liveRegisters[RegisterIndex(HoursRegister)] * SecondsPerHour);
            var combinedSecondsWithinDay = currentSecondsWithinDay + (seconds % SecondsPerDay);
            var totalDays = day + (seconds / SecondsPerDay) + (combinedSecondsWithinDay / SecondsPerDay);
            var wrappedDay = (int)(totalDays % DaysPerCycle);
            var secondsWithinDay = (int)(combinedSecondsWithinDay % SecondsPerDay);

            _liveRegisters[RegisterIndex(SecondsRegister)] = (byte)(secondsWithinDay % SecondsPerMinute);
            _liveRegisters[RegisterIndex(MinutesRegister)] = (byte)(secondsWithinDay / SecondsPerMinute % SecondsPerMinute);
            _liveRegisters[RegisterIndex(HoursRegister)] = (byte)(secondsWithinDay / SecondsPerHour);
            _liveRegisters[RegisterIndex(DaysLowRegister)] = (byte)wrappedDay;
            _liveRegisters[daysHighIndex] = (byte)((_liveRegisters[daysHighIndex] & 0xC0) | (wrappedDay >> 8));
            if (totalDays >= DaysPerCycle)
            {
                _liveRegisters[daysHighIndex] |= CarryMask;
            }
        }

        private bool HasOutOfRangeTime()
        {
            return _liveRegisters[RegisterIndex(SecondsRegister)] > 59 ||
                   _liveRegisters[RegisterIndex(MinutesRegister)] > 59 ||
                   _liveRegisters[RegisterIndex(HoursRegister)] > 23;
        }

        private static int ReadInt32(byte[] data, int offset)
        {
            return data[offset] |
                   (data[offset + 1] << 8) |
                   (data[offset + 2] << 16) |
                   (data[offset + 3] << 24);
        }

        private static long ReadInt64(byte[] data, int offset)
        {
            return (uint)ReadInt32(data, offset) | ((long)ReadInt32(data, offset + 4) << 32);
        }

        private static void WriteInt32(byte[] data, int offset, int value)
        {
            data[offset] = (byte)value;
            data[offset + 1] = (byte)(value >> 8);
            data[offset + 2] = (byte)(value >> 16);
            data[offset + 3] = (byte)(value >> 24);
        }

        private static void WriteInt64(byte[] data, int offset, long value)
        {
            WriteInt32(data, offset, (int)value);
            WriteInt32(data, offset + 4, (int)(value >> 32));
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
