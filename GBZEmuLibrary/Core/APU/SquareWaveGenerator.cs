using System;

namespace GBZEmuLibrary
{
    // Ref 1 - https://emu-docs.org/Game%20Boy/gb_sound.txt

    internal class SquareWaveGenerator : IGenerator
    {
        private const int MAX_11_BIT_VALUE = 2048; //2^11
        private const int MAX_4_BIT_VALUE = 16; //2^4

        public int Length => _totalLength;
        public int InitialVolume => _initialVolume;

        public bool Inited { get; set; }
        public bool Enabled => _totalLength > 0 && Inited;
        public int ChannelState { get; set; }

        private int _initialSweepPeriod;
        private int _sweepPeriod;
        private int _shiftSweep;
        private bool _negateSweep;

        private int _totalLength;

        private float _dutyCycle;
        private bool _dutyState;

        private int _initialVolume;
        private int _volume;
        private int _envelopePeriod;
        private int _initialEnvelopePeriod;
        private bool _addEnvelope;

        private int _originalFrequency;
        private int _frequency;
        private int _frequencyCount;

        private int _sequenceTimer;

        public void Update()
        {
            //256Hz
            if (_sequenceTimer % 2 == 0)
            {
                _totalLength = Math.Max(0, _totalLength - 1);
            }

            //128Hz
            if ((_sequenceTimer + 2) % 4 == 0)
            {
                _sweepPeriod--;

                if (_shiftSweep != 0 && _sweepPeriod == 0)
                {
                    _sweepPeriod = _initialSweepPeriod;

                    var sweepFreq = _originalFrequency + (_negateSweep ? -1 : 1) * (_originalFrequency >> _shiftSweep);

                    if (sweepFreq >= MAX_11_BIT_VALUE)
                    {
                        //TODO may need an actual enabled flag
                        _totalLength = 0;
                    }
                    else if (sweepFreq > 0)
                    {
                        SetFrequency(sweepFreq);
                    }
                }
            }

            //64Hz
            if (_sequenceTimer % 7 == 0)
            {
                _envelopePeriod--;

                if (_envelopePeriod == 0)
                {
                    _envelopePeriod = _initialEnvelopePeriod;
                    _volume += _addEnvelope ? 1 : -1;
                    _volume = Math.Max(_volume, 0);
                    _volume = Math.Min(_volume, MAX_4_BIT_VALUE - 1);
                }
            }

            _sequenceTimer = (_sequenceTimer + 1) % 8;

            if (Inited)
            {
                _frequencyCount++;

                if (_frequencyCount > _frequency * (_dutyState ? _dutyCycle : 1 - _dutyCycle))
                {
                    _frequencyCount = 0;
                    _dutyState      = !_dutyState;
                }
            }
        }

        public byte GetCurrentSample()
        {
            return (byte)(_dutyState ? _volume : -_volume);
        }

        public void SetSweep(byte data)
        {
            // Val Format -PPP NSSS
            _shiftSweep = Helpers.GetBitsIsolated(data, 0, 3);

            _negateSweep = Helpers.TestBit(data, 4);

            _initialSweepPeriod = Helpers.GetBitsIsolated(data, 4, 3);
            _sweepPeriod = _initialSweepPeriod;
        }

        public void SetLength(byte data)
        {
            // Val Format --LL LLLL
            _totalLength = 64 - Helpers.GetBits(data, 6);
        }

        public void SetLength(int length)
        {
            _totalLength = length;
        }

        public void SetDutyCycle(byte data)
        {
            // Val Format DD-- ----
            _dutyCycle = Helpers.GetBitsIsolated(data, 6, 2) * 0.25f;
            _dutyCycle = Math.Max(0.125f, _dutyCycle);
        }

        public void SetEnvelope(byte data)
        {
            // Val Format VVVV APPP
            _initialEnvelopePeriod = Helpers.GetBits(data, 3);
            _envelopePeriod = _initialEnvelopePeriod;

            _addEnvelope = Helpers.TestBit(data, 3);

            _initialVolume = Helpers.GetBitsIsolated(data, 4, 4);
            SetVolume(_initialVolume);
        }

        public void SetVolume(int volume)
        {
            _volume = volume;
        }

        public void SetFrequency(int freq)
        {
            _originalFrequency = freq;
            _frequency = Sound.SAMPLE_RATE / (GameBoySchema.MAX_DMG_CLOCK_CYCLES / ((MAX_11_BIT_VALUE - (freq % MAX_11_BIT_VALUE)) << 5));
        }
    }
}
