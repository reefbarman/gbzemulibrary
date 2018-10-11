using System;

namespace GBZEmuLibrary
{
    internal class Timer : IMemoryUnit
    {
        private const int FREQ_4096 = 4096;
        private const int FREQ_262144 = 262144;
        private const int FREQ_65536 = 65536;
        private const int FREQ_16382 = 16382;

        private const int TIMER_ENABLED_BIT = 2;

        private byte[] _timerRAM = new byte[MemorySchema.TIMER_END - MemorySchema.TIMER_START];
        private int _cyclesPerTimerIncrement = GameBoySchema.MAX_DMG_CLOCK_CYCLES / FREQ_4096; //TODO can this be setup dynamically?
        private int _timerCounter = 0;
        private bool _timerEnabled = false;

        public void Update(int cycles)
        {
            if (!_timerEnabled)
            {
                return;
            }

            _timerCounter += cycles;

            while (_timerCounter >= _cyclesPerTimerIncrement)
            {
                _timerCounter -= _cyclesPerTimerIncrement;

                var tima = (byte)(ReadByte(MemorySchema.TIMA) + 1);

                if (tima == 0)
                {
                    tima = ReadByte(MemorySchema.TMA);
                    MessageBus.Instance.RequestInterrupt(Interrupts.Timer);
                }

                WriteByte((byte)tima, MemorySchema.TIMA);
            }
        }

        public void WriteByte(byte data, int address)
        {
            _timerRAM[address - MemorySchema.TIMER_START] = data;

            if (address == MemorySchema.TMC)
            {
                _timerEnabled = Helpers.TestBit(ReadByte(address), TIMER_ENABLED_BIT);
                _cyclesPerTimerIncrement = GetCurrentClockFrequency(data & 0x3);
            }
        }

        public bool CanReadWriteByte(int address)
        {
            return address >= MemorySchema.TIMER_START && address < MemorySchema.TIMER_END;
        }

        public byte ReadByte(int address)
        {
            return _timerRAM[address - MemorySchema.TIMER_START];
        }

        private int GetCurrentClockFrequency(int frequency)
        {
            switch (frequency)
            {
                case 0:
                    return GameBoySchema.MAX_DMG_CLOCK_CYCLES / FREQ_4096;
                case 1:
                    return GameBoySchema.MAX_DMG_CLOCK_CYCLES / FREQ_262144;
                case 2:
                    return GameBoySchema.MAX_DMG_CLOCK_CYCLES / FREQ_65536;
                case 3:
                    return GameBoySchema.MAX_DMG_CLOCK_CYCLES / FREQ_16382;
            }

            throw new IndexOutOfRangeException();
        }
    }
}
