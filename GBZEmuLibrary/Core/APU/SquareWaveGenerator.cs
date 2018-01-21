using System;

namespace GBZEmuLibrary
{
    // Ref 1 - https://emu-docs.org/Game%20Boy/gb_sound.txt
    // Ref 2 - http://gbdev.gg8.se/wiki/articles/Gameboy_sound_hardware
    internal class SquareWaveGenerator : IGenerator
    {
        private const int MAX_11_BIT_VALUE = 2048; //2^11
        private const int MAX_6_BIT_VALUE  = 64;   //2^6
        private const int MAX_4_BIT_VALUE  = 16;   //2^4

        public int  ChannelState { get; set; }
        public bool Enabled      { get; private set; }

        private bool _dacEnabled;

        private int  _initialSweepPeriod;
        private int  _sweepPeriod;
        private int  _shiftSweep;
        private bool _negateSweep;
        private bool _sweepEnabled;
        private bool _sweepNegated;

        private int  _totalLength;
        private bool _lengthEnabled;

        private int _dutyCycle;
        private int _wavePos;

        private int  _initialVolume;
        private int  _volume;
        private int  _envelopePeriod;
        private int  _initialEnvelopePeriod;
        private bool _addEnvelope;

        private int _originalFrequency;
        private int _shadowFrequency;
        private int _frequency;
        private int _frequencyCount;

        private int _sequenceTimer;
        private int _frameSequenceTimer;

        public void SetSweep(byte data)
        {
            // Val Format -PPP NSSS
            _shiftSweep = Helpers.GetBits(data, 3);

            var newMode = Helpers.TestBit(data, 4);

            /* Clearing the sweep negate mode bit in NR10 after at least one sweep calculation has been made
             * using the negate mode since the last trigger causes the channel to be immediately disabled.
             * This prevents you from having the sweep lower the frequency then raise the frequency without
             * a trigger inbetween.*/
            if (_sweepEnabled && _sweepNegated && _negateSweep && !newMode)
            {
                Enabled = false;
            }

            _negateSweep = newMode;

            _initialSweepPeriod = Helpers.GetBitsIsolated(data, 4, 3);
            _sweepPeriod        = _initialSweepPeriod;
        }

        public void SetLength(byte data)
        {
            // Val Format --LL LLLL
            _totalLength = MAX_6_BIT_VALUE - Helpers.GetBits(data, 6);
        }

        public void ToggleLength(bool enabled)
        {
            _lengthEnabled = enabled;
        }

        public void SetDutyCycle(byte data)
        {
            // Val Format DD-- ----
            _dutyCycle = Helpers.GetBitsIsolated(data, 6, 2);
        }

        public void SetEnvelope(byte data)
        {
            // Val Format VVVV APPP
            _initialEnvelopePeriod = Helpers.GetBits(data, 3);
            _envelopePeriod        = _initialEnvelopePeriod;

            _addEnvelope = Helpers.TestBit(data, 3);

            _initialVolume = Helpers.GetBitsIsolated(data, 4, 4);
            SetVolume(_initialVolume);
        }

        public void ToggleDAC(byte data)
        {
            // Val Format Top 5 bits
            _dacEnabled =  Helpers.GetBitsIsolated(data, 3, 5) != 0;
            Enabled     &= _dacEnabled;
        }

        public void SetVolume(int volume)
        {
            _volume = volume;
        }

        public void SetFrequency(int freq)
        {
            _originalFrequency = freq;
            SetFreqTimer(freq);
        }

        public void Init()
        {
            _wavePos            = 0;
            _frameSequenceTimer = 0;
            _sequenceTimer      = 0;
        }

        public void Reset()
        {
            ChannelState = 0;

            _initialSweepPeriod = 0;
            _sweepPeriod        = 0;
            _shiftSweep         = 0;
            _negateSweep        = false;
            _sweepEnabled       = false;
            _sweepNegated       = false;

            _totalLength   = 0;
            _lengthEnabled = false;

            _dutyCycle = 0;

            _initialVolume         = 0;
            _volume                = 0;
            _envelopePeriod        = 0;
            _initialEnvelopePeriod = 0;
            _addEnvelope           = false;

            _originalFrequency = 0;
            _shadowFrequency   = 0;
            _frequency         = 0;
            _frequencyCount    = 0;

            _sequenceTimer      = 0;
            _frameSequenceTimer = 0;

            Enabled = false;
        }

        public void Update(int cycles)
        {
            _frameSequenceTimer += cycles;

            if (_frameSequenceTimer >= APUSchema.FRAME_SEQUENCER_UPDATE_THRESHOLD)
            {
                _frameSequenceTimer -= APUSchema.FRAME_SEQUENCER_UPDATE_THRESHOLD;

                //256Hz
                if (_sequenceTimer % 2 == 0)
                {
                    UpdateLength();
                }

                //128Hz
                if ((_sequenceTimer + 2) % 4 == 0)
                {
                    UpdateSweep();
                }

                //64Hz
                if (_sequenceTimer % 7 == 0)
                {
                    UpdateEnvelop();
                }

                _sequenceTimer = (_sequenceTimer + 1) % 8;
            }

            UpdateFrequency(cycles);
        }

        public void GetCurrentSample(ref int leftChannel, ref int rightChannel)
        {
            if (Enabled)
            {
                var sample = (APUSchema.DUTY_WAVE_FORM[_dutyCycle][_wavePos] * 15) & _volume;

                if ((ChannelState & APUSchema.CHANNEL_LEFT) != 0)
                {
                    leftChannel += sample;
                }

                if ((ChannelState & APUSchema.CHANNEL_RIGHT) != 0)
                {
                    rightChannel += sample;
                }
            }
        }

        public void HandleTrigger()
        {
            Enabled = _dacEnabled;

            if (_totalLength == 0)
            {
                /* If a channel is triggered when the frame sequencer's next step is one that doesn't clock the length counter 
                 * and the length counter is now enabled and length is being set to 64(256 for wave channel) because it was 
                 * previously zero, it is set to 63 instead(255 for wave channel). */
                _totalLength = _lengthEnabled && (_sequenceTimer % 2 != 0) ? MAX_6_BIT_VALUE - 1 : MAX_6_BIT_VALUE;
            }

            _sweepEnabled = (_shiftSweep != 0) || (_initialSweepPeriod != 0);

            SetFreqTimer(_originalFrequency);
            _shadowFrequency = _originalFrequency;
            _sweepPeriod     = _initialSweepPeriod == 0 ? 8 : _initialSweepPeriod;
            _sweepNegated    = false;
            if (_shiftSweep > 0)
            {
                CalculateNewFrequency();
            }

            _volume         = _initialVolume;
            _envelopePeriod = _initialEnvelopePeriod == 0 ? 8 : _initialEnvelopePeriod;
        }

        private void UpdateLength()
        {
            if (_totalLength > 0 && _lengthEnabled)
            {
                Enabled &= --_totalLength > 0;
            }
        }

        private void UpdateSweep()
        {
            if (_sweepPeriod > 0)
            {
                _sweepPeriod--;

                if (_sweepPeriod == 0)
                {
                    //The volume envelope and sweep timers treat a period of 0 as 8.
                    _sweepPeriod = _initialSweepPeriod == 0 ? 8 : _initialSweepPeriod;

                    if (_sweepEnabled && _initialSweepPeriod > 0)
                    {
                        var sweepFreq = CalculateNewFrequency();

                        if (sweepFreq < MAX_11_BIT_VALUE && _shiftSweep > 0)
                        {
                            _shadowFrequency = sweepFreq;
                            SetFrequency(sweepFreq);
                            CalculateNewFrequency();
                        }
                    }
                }
            }
        }

        private void UpdateEnvelop()
        {
            if (_envelopePeriod > 0)
            {
                _envelopePeriod--;

                if (_envelopePeriod == 0)
                {
                    //The volume envelope and sweep timers treat a period of 0 as 8.
                    _envelopePeriod = _initialEnvelopePeriod == 0 ? 8 : _initialEnvelopePeriod;

                    if (_initialEnvelopePeriod > 0)
                    {
                        _volume += _addEnvelope ? 1 : -1;
                        _volume =  Math.Min(Math.Max(_volume, 0), MAX_4_BIT_VALUE - 1);
                    }
                }
            }
        }

        private void UpdateFrequency(int cycles)
        {
            _frequencyCount += cycles;

            if (_frequencyCount >= _frequency)
            {
                _frequencyCount -= _frequency;
                _wavePos        =  (_wavePos + 1) % 8;
            }
        }

        private int CalculateNewFrequency()
        {
            var sweepFreq = _shadowFrequency + (_negateSweep ? -1 : 1) * (_shadowFrequency >> _shiftSweep);

            _sweepNegated = _negateSweep || _sweepNegated;

            if (sweepFreq >= MAX_11_BIT_VALUE)
            {
                Enabled = false;
            }

            return sweepFreq;
        }

        private void SetFreqTimer(int freq)
        {
            _frequency = (MAX_11_BIT_VALUE - freq) * 4;
        }
    }
}
