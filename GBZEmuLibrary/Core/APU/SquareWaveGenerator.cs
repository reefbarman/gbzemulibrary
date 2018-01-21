using System;

namespace GBZEmuLibrary
{
    // Ref 1 - https://emu-docs.org/Game%20Boy/gb_sound.txt
    // Ref 2 - http://gbdev.gg8.se/wiki/articles/Gameboy_sound_hardware
    internal class SquareWaveGenerator : Generator
    {
        private int  _initialSweepPeriod;
        private int  _sweepPeriod;
        private int  _shiftSweep;
        private bool _negateSweep;
        private bool _sweepEnabled;
        private bool _sweepNegated;

        private int _dutyCycle;
        private int _wavePos;

        private int  _initialVolume;
        private int  _volume;
        private int  _envelopePeriod;
        private int  _initialEnvelopePeriod;
        private bool _addEnvelope;

        private int _shadowFrequency;

        public SquareWaveGenerator() : base(MathSchema.MAX_6_BIT_VALUE)
        {
        }

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
                _enabled = false;
            }

            _negateSweep = newMode;

            _initialSweepPeriod = Helpers.GetBitsIsolated(data, 4, 3);
            _sweepPeriod        = _initialSweepPeriod;
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
            _volume = _initialVolume;
        }

        public override void Init()
        {
            _wavePos            = 0;
            _frameSequenceTimer = 0;
            _sequenceTimer      = 0;
        }

        public override void Reset()
        {
            base.Reset();

            _initialSweepPeriod = 0;
            _shiftSweep         = 0;
            _negateSweep        = false;

            _dutyCycle = 0;

            _initialVolume         = 0;
            _envelopePeriod        = 0;
            _initialEnvelopePeriod = 0;
            _addEnvelope           = false;

            _shadowFrequency = 0;
        }

        public override void HandleTrigger()
        {
            _enabled = _dacEnabled;

            if (_totalLength == 0)
            {
                /* If a channel is triggered when the frame sequencer's next step is one that doesn't clock the length counter 
                 * and the length counter is now enabled and length is being set to 64(256 for wave channel) because it was 
                 * previously zero, it is set to 63 instead(255 for wave channel). */
                _totalLength = _lengthEnabled && (_sequenceTimer % 2 != 0) ? MathSchema.MAX_6_BIT_VALUE - 1 : MathSchema.MAX_6_BIT_VALUE;
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

        protected override int GetSample()
        {
            return (APUSchema.DUTY_WAVE_FORM[_dutyCycle][_wavePos] * 15) & _volume;
        }

        protected override void UpdateSweep()
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

                        if (sweepFreq < MathSchema.MAX_11_BIT_VALUE && _shiftSweep > 0)
                        {
                            _shadowFrequency = sweepFreq;
                            SetFrequency(sweepFreq);
                            CalculateNewFrequency();
                        }
                    }
                }
            }
        }

        protected override void UpdateEnvelop()
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
                        _volume =  Math.Min(Math.Max(_volume, 0), MathSchema.MAX_4_BIT_VALUE - 1);
                    }
                }
            }
        }

        protected override void UpdateFrequency(int cycles)
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

            if (sweepFreq >= MathSchema.MAX_11_BIT_VALUE)
            {
                _enabled = false;
            }

            return sweepFreq;
        }
    }
}
