using System;

namespace GBZEmuLibrary
{
    /// <summary>
    /// Emulates channel 3's programmable wave table, sample buffer, and active wave-RAM access behavior.
    /// </summary>
    internal class WaveGenerator : Generator
    {
        // SameSuite measures a three-tick channel 3 trigger pipeline; each channel 3 timer tick is two CPU clocks.
        private const int TriggerDelayClocks = 6;

        private int _volumeLevel;
        private int _volumeShift;

        private readonly byte[] _waveTable = new byte[(APUSchema.WAVE_TABLE_END - APUSchema.WAVE_TABLE_START) * 2];
        private int _wavePos;
        private byte _currentSample;
        private int _clocksSinceWaveByteRead;
        private int _frequencyReload;

        public WaveGenerator() : base(byte.MaxValue + 1)
        {
        }

        /// <summary>
        /// Writes wave RAM while applying the current-byte redirection and DMG fetch-window restriction.
        /// </summary>
        public void WriteWaveByte(byte data, int address, bool gbcMode)
        {
            var index = address - APUSchema.WAVE_TABLE_START;

            if (Status)
            {
                if (!gbcMode && _clocksSinceWaveByteRead >= 2)
                {
                    return;
                }

                index = _wavePos / 2;
            }

            _waveTable[index * 2] = (byte)Helpers.GetBitsIsolated(data, 4, 4);
            _waveTable[index * 2 + 1] = Helpers.GetBits(data, 4);
        }

        /// <summary>
        /// Reads channel registers or the packed wave-RAM byte at the requested address.
        /// </summary>
        public override byte ReadByte(int address)
        {
            if (address >= APUSchema.WAVE_3_DAC && address < APUSchema.NOISE_4_UNUSED)
            {
                int register;

                switch (address)
                {
                    case APUSchema.WAVE_3_DAC:
                        // Register Format E--- ---- DAC power
                        register = (_dacEnabled ? 1 : 0) << 7;
                        return (byte)(0x7F | register);

                    case APUSchema.WAVE_3_LENGTH_LOAD:
                        return 0xFF;

                    case APUSchema.WAVE_3_VOLUME:
                        // Register Format -VV- ---- Volume code (00=0%, 01=100%, 10=50%, 11=25%)
                        register = _volumeLevel << 5;
                        return (byte)(0x9F | register);

                    case APUSchema.WAVE_3_FREQUENCY_LSB:
                        return 0xFF;

                    case APUSchema.WAVE_3_FREQUENCY_MSB:
                        // Register Format TL-- -FFF Trigger, Length enable, Frequency MSB (Only interested in length enabled)
                        register = (_lengthEnabled ? 1 : 0) << 6;
                        return (byte)(0xBF | register);
                }

                throw new IndexOutOfRangeException();
            }

            return ReadWaveByte(address - APUSchema.WAVE_TABLE_START);
        }

        /// <summary>
        /// Reads wave RAM while applying the current-byte redirection and DMG fetch-window restriction.
        /// </summary>
        public byte ReadWaveByte(int address, bool gbcMode)
        {
            var index = address - APUSchema.WAVE_TABLE_START;

            if (Status)
            {
                if (!gbcMode && _clocksSinceWaveByteRead >= 2)
                {
                    return 0xFF;
                }

                index = _wavePos / 2;
            }

            return ReadWaveByte(index);
        }

        public void SetVolume(byte data)
        {
            // Val Format -VV- ----
            _volumeLevel = Helpers.GetBitsIsolated(data, 5, 2);

            switch (_volumeLevel)
            {
                case 0:
                    _volumeShift = 4;
                    break;
                case 1:
                    _volumeShift = 0;
                    break;
                case 2:
                    _volumeShift = 1;
                    break;
                case 3:
                    _volumeShift = 2;
                    break;
            }
        }

        public override void Init()
        {
            base.Init();
            _wavePos = 0;
            _currentSample = 0;
            _clocksSinceWaveByteRead = int.MaxValue;
            SetFrequency(0);
        }

        public override void Reset()
        {
            base.Reset();

            SetVolume(0);
            _clocksSinceWaveByteRead = int.MaxValue;
        }

        /// <summary>
        /// Changes the period used after the current channel 3 timer interval completes.
        /// </summary>
        public override void SetFrequency(int freq)
        {
            _originalFrequency = freq;
            _frequencyReload = GetFrequencyTimerPeriod(freq);

            if (!Status)
            {
                _frequency = _frequencyReload;
                _frequencyCount = 0;
            }
        }

        /// <summary>
        /// Applies revision-specific active-retrigger behavior before restarting channel 3.
        /// </summary>
        public void HandleTrigger(bool gbcMode)
        {
            // On DMG, corruption occurs during the final 2 MHz APU tick before the next wave byte fetch.
            if (!gbcMode && Status && _frequency - _frequencyCount == 2)
            {
                CorruptWaveRamOnDmgRetrigger();
            }

            HandleTrigger();
        }

        /// <summary>
        /// Restarts channel 3's length and frequency state after a trigger write.
        /// </summary>
        public override void HandleTrigger()
        {
            _enabled = _dacEnabled;

            if (_totalLength == 0)
            {
                /* If a channel is triggered when the frame sequencer's next step is one that doesn't clock the length counter 
                 * and the length counter is now enabled and length is being set to 64(256 for wave channel) because it was 
                 * previously zero, it is set to 63 instead(255 for wave channel). */
                _totalLength = _lengthEnabled && (_sequenceTimer % 2 != 0) ? byte.MaxValue : byte.MaxValue + 1;
            }

            SetFreqTimer(_originalFrequency);
            _frequencyCount = -TriggerDelayClocks;
            _clocksSinceWaveByteRead = int.MaxValue;

            // Trigger resets the index to sample 0; the first timer step advances to sample 1.
            _wavePos = 0;
        }

        protected override int GetSample()
        {
            return _currentSample >> _volumeShift;
        }

        /// <summary>
        /// Converts channel 3's period to CPU clocks; its sample timer advances twice as fast as pulse channels.
        /// </summary>
        protected override int GetFrequencyTimerPeriod(int freq)
        {
            return (MathSchema.MAX_11_BIT_VALUE - freq) * 2;
        }

        protected override void SetFreqTimer(int freq)
        {
            _frequencyReload = GetFrequencyTimerPeriod(freq);
            _frequency = _frequencyReload;
            _frequencyCount = 0;
        }

        protected override void UpdateFrequency(int cycles)
        {
            if (_clocksSinceWaveByteRead < int.MaxValue - cycles)
            {
                _clocksSinceWaveByteRead += cycles;
            }

            if (!Status)
            {
                return;
            }

            _frequencyCount += cycles;

            while (_frequencyCount >= _frequency)
            {
                _frequencyCount -= _frequency;
                _wavePos = (_wavePos + 1) % 32;
                _currentSample = _waveTable[_wavePos];
                _frequency = _frequencyReload;
                _clocksSinceWaveByteRead = _frequencyCount;
            }
        }

        /// <summary>
        /// Models the original DMG's wave-RAM bus corruption when channel 3 is retriggered during a byte fetch.
        /// </summary>
        private void CorruptWaveRamOnDmgRetrigger()
        {
            var fetchedByte = ((_wavePos + 1) % 32) / 2;

            if (fetchedByte < 4)
            {
                WritePackedWaveByte(0, ReadWaveByte(fetchedByte));
                return;
            }

            // The DMG copies the aligned four-byte block containing the fetched byte into wave bytes 0-3.
            var source = fetchedByte & ~3;
            var first = ReadWaveByte(source);
            var second = ReadWaveByte(source + 1);
            var third = ReadWaveByte(source + 2);
            var fourth = ReadWaveByte(source + 3);

            WritePackedWaveByte(0, first);
            WritePackedWaveByte(1, second);
            WritePackedWaveByte(2, third);
            WritePackedWaveByte(3, fourth);
        }

        private byte ReadWaveByte(int index)
        {
            return (byte)((_waveTable[index * 2] << 4) | _waveTable[(index * 2) + 1]);
        }

        /// <summary>
        /// Stores one packed wave-RAM byte in the channel's nibble-addressed sample table.
        /// </summary>
        private void WritePackedWaveByte(int index, byte data)
        {
            _waveTable[index * 2] = (byte)(data >> 4);
            _waveTable[index * 2 + 1] = (byte)(data & 0x0F);
        }
    }
}
