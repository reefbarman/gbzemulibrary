using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace GBZEmuLibrary
{
    internal class WaveGenerator : Generator
    {
        private int _volumeLevel;
        private int _volumeShift;

        private byte[] _waveTable = new byte[(APUSchema.WAVE_TABLE_END - APUSchema.WAVE_TABLE_START) * 2];
        private int    _wavePos;
        private byte   _currentSample;

        public WaveGenerator() : base(byte.MaxValue + 1)
        {
        }

        public void WriteByte(byte data, int address)
        {
            var index = address - APUSchema.WAVE_TABLE_START;

            _waveTable[index * 2]     = (byte)Helpers.GetBitsIsolated(data, 4, 4);
            _waveTable[index * 2 + 1] = Helpers.GetBits(data, 4);
        }

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

            var index = (address - APUSchema.WAVE_TABLE_START);
            return (byte)((_waveTable[index * 2] << 4) | _waveTable[(index * 2) + 1]);
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
            _wavePos       = 0;
            _currentSample = 0;
        }

        public override void Reset()
        {
            base.Reset();

            SetVolume(0);
        }

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

            _wavePos = 0;
        }

        protected override int GetSample()
        {
            return _currentSample >> _volumeShift;
        }

        protected override void UpdateFrequency(int cycles)
        {
            _frequencyCount += cycles;

            if (_frequencyCount >= _frequency)
            {
                _frequencyCount -= _frequency;
                _wavePos        =  (_wavePos + 1) % 32;
                _currentSample  =  _waveTable[_wavePos];
            }
        }
    }
}
