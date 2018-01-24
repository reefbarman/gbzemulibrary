using System;
using System.Diagnostics;

namespace GBZEmuLibrary
{
    // Ref 1 - https://emu-docs.org/Game%20Boy/gb_sound.txt
    // Ref 2 - http://gbdev.gg8.se/wiki/articles/Gameboy_sound_hardware
    internal class SquareWaveGenerator : EnvelopeGenerator
    {
        private int  _initialSweepPeriod;
        private int  _sweepPeriod;
        private int  _shiftSweep;
        private bool _negateSweep;
        private bool _sweepEnabled;
        private bool _sweepNegated;

        private int _dutyCycle;
        private int _wavePos;

        private int _shadowFrequency;

        public SquareWaveGenerator() : base(MathSchema.MAX_6_BIT_VALUE)
        {
        }

        public override void Init()
        {
            base.Init();
            _wavePos = 0;
        }

        public override void Reset()
        {
            base.Reset();

            _initialSweepPeriod = 0;
            _shiftSweep         = 0;
            SetSweepMode(false);

            _dutyCycle = 0;
        }

        public override byte ReadByte(int address)
        {
            int register;

            switch (address)
            {
                case APUSchema.SQUARE_1_SWEEP_PERIOD:
                    // Register Format -PPP NSSS Sweep period, negate, shift
                    register = _shiftSweep | ((_negateSweep ? 1 : 0) << 3) | (_initialSweepPeriod << 4);
                    return (byte)(0x80 | register);
                    
                case APUSchema.SQUARE_1_DUTY_LENGTH_LOAD:
                case APUSchema.SQUARE_2_DUTY_LENGTH_LOAD:
                    // Register Format DDLL LLLL Duty, Length load (64-L) (Only first six bytes needed)
                    register = _dutyCycle << 6;
                    return (byte)(0x3F | register);

                case APUSchema.SQUARE_1_VOLUME_ENVELOPE:
                case APUSchema.SQUARE_2_VOLUME_ENVELOPE:
                    // Register Format VVVV APPP Starting volume, Envelope add mode, period
                    register = _initialEnvelopePeriod | (_addEnvelope ? 1 : 0) << 3 | _initialVolume << 4;
                    return (byte)(0x00 | register);

                case APUSchema.SQUARE_1_FREQUENCY_LSB:
                case APUSchema.SQUARE_2_FREQUENCY_LSB:
                    // Register Format FFFF FFFF Frequency LSB
                    return 0xFF;

                case APUSchema.SQUARE_1_FREQUENCY_MSB:
                case APUSchema.SQUARE_2_FREQUENCY_MSB:
                    // Register Format TL-- -FFF Trigger, Length enable, Frequency MSB (Only interested in length enabled)
                    register = (_lengthEnabled ? 1 : 0) << 6;
                    return (byte)(0xBF | register);

                case APUSchema.SQUARE_2_UNUSED:
                    return 0xFF;
            }

            throw new IndexOutOfRangeException();
        }

        public void SetSweep(byte data)
        {
            // Val Format -PPP NSSS
            _shiftSweep = Helpers.GetBits(data, 3);

            SetSweepMode(Helpers.TestBit(data, 3));

            _initialSweepPeriod = Helpers.GetBitsIsolated(data, 4, 3);
        }

        public void SetDutyCycle(byte data)
        {
            // Val Format DD-- ----
            _dutyCycle = Helpers.GetBitsIsolated(data, 6, 2);
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

            SetFreqTimer(_originalFrequency);
            _shadowFrequency = _originalFrequency;
            _sweepPeriod     = _initialSweepPeriod == 0 ? 8 : _initialSweepPeriod;
            _sweepNegated    = false;

            _sweepEnabled = (_shiftSweep != 0) || (_initialSweepPeriod != 0);

            if (_shiftSweep > 0)
            {
                CalculateNewFrequency();
            }

            _volume         = _initialVolume;
            _envelopePeriod = _initialEnvelopePeriod == 0 ? 8 : _initialEnvelopePeriod;
        }

        protected override int GetSample()
        {
            return (APUSchema.DUTY_WAVE_FORM[_dutyCycle][_wavePos] * (MathSchema.MAX_4_BIT_VALUE - 1)) & _volume;
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

        private void SetSweepMode(bool newMode)
        {
            /* Clearing the sweep negate mode bit in NR10 after at least one sweep calculation has been made
             * using the negate mode since the last trigger causes the channel to be immediately disabled.
             * This prevents you from having the sweep lower the frequency then raise the frequency without
             * a trigger inbetween.*/
            if (_sweepEnabled && _sweepNegated && _negateSweep && !newMode)
            {
                _enabled = false;
            }

            _negateSweep = newMode;
        }
    }
}
