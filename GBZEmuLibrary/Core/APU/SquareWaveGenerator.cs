using System;
using System.Diagnostics;

namespace GBZEmuLibrary
{
    // Ref 1 - https://emu-docs.org/Game%20Boy/gb_sound.txt
    // Ref 2 - http://gbdev.gg8.se/wiki/articles/Gameboy_sound_hardware
    /// <summary>
    /// Emulates a pulse channel, including channel 1's frequency-sweep shadow register and overflow behavior.
    /// </summary>
    internal class SquareWaveGenerator : EnvelopeGenerator
    {
        private int _initialSweepPeriod;
        private int _sweepPeriod;
        private int _shiftSweep;
        private bool _negateSweep;
        private bool _sweepEnabled;
        private bool _sweepNegated;
        private int _sweepOverflowCountdown;
        private int _sweepTriggerCalculationCountdown;
        private int _sweepRestartHoldCountdown;
        private ApuHardwareRevision _hardwareRevision;

        private int _dutyCycle;
        private int _latchedDutyCycle;
        private int _wavePos;
        private int _frequencyReload;
        private int _alignmentClocks;
        private bool _sampleSuppressed;
        private bool _didTick;
        private bool _justReloaded;

        private int _shadowFrequency;
        private bool _sweepFrequencyWritten;

        public SquareWaveGenerator() : base(MathSchema.MAX_6_BIT_VALUE)
        {
        }

        public void SetHardwareRevision(ApuHardwareRevision hardwareRevision)
        {
            _hardwareRevision = hardwareRevision;
        }

        public override void Init()
        {
            base.Init();
            _wavePos = 0;
            _latchedDutyCycle = 0;
            // CGB-E powers the pulse clock's 2 MHz low-frequency divider on in its high phase.
            // This phase is independent of the CPU start time and is reset whenever NR52 powers the APU on.
            _alignmentClocks = 2;
            _sampleSuppressed = false;
            _didTick = false;
            _justReloaded = false;
            SetFreqTimer(_originalFrequency);
        }

        public override void Reset()
        {
            base.Reset();

            _initialSweepPeriod = 0;
            _shiftSweep = 0;
            _sweepFrequencyWritten = false;
            _sweepOverflowCountdown = 0;
            _sweepTriggerCalculationCountdown = 0;
            _sweepRestartHoldCountdown = 0;
            SetSweepMode(false);

            _dutyCycle = 0;
            _latchedDutyCycle = 0;
            _alignmentClocks = 0;
            _sampleSuppressed = false;
            _didTick = false;
            _justReloaded = false;
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

        /// <summary>
        /// Applies the NR10 sweep pace, direction, and shift fields.
        /// </summary>
        public void SetSweep(byte data)
        {
            // Val Format -PPP NSSS
            _shiftSweep = Helpers.GetBits(data, 3);

            SetSweepMode(Helpers.TestBit(data, 3));

            _initialSweepPeriod = Helpers.GetBitsIsolated(data, 4, 3);

            if (_shiftSweep == 0)
            {
                _sweepOverflowCountdown = 0;
                _sweepTriggerCalculationCountdown = 0;
            }
        }

        public void SetDutyCycle(byte data)
        {
            // Val Format DD-- ----
            _dutyCycle = Helpers.GetBitsIsolated(data, 6, 2);
        }

        public void HandleDividerWrite()
        {
            if (Status)
            {
                // This core exposes the DIV reset before the write cycle's trailing clocks. SameSuite's CGB-E
                // pulse tables retain that bus cycle in the oscillator phase, so preserve one machine cycle here.
                _frequencyCount -= 4;
            }
        }

        public override void HandleTrigger()
        {
            var wasActive = Status;
            _enabled = _dacEnabled;

            if (_totalLength == 0)
            {
                /* If a channel is triggered when the frame sequencer's next step is one that doesn't clock the length counter 
                 * and the length counter is now enabled and length is being set to 64(256 for wave channel) because it was 
                 * previously zero, it is set to 63 instead(255 for wave channel). */
                _totalLength = _lengthEnabled && (_sequenceTimer % 2 != 0) ? MathSchema.MAX_6_BIT_VALUE - 1 : MathSchema.MAX_6_BIT_VALUE;
            }

            SetFreqTimer(_originalFrequency);
            _latchedDutyCycle = _dutyCycle;
            var lowFrequencyDivider = (_alignmentClocks >> 1) & 1;
            var triggerDelay = wasActive ? 4 - lowFrequencyDivider : 6 - lowFrequencyDivider;
            // The 2 MHz countdown ticks after delay + 1 phases; convert that interval to normal-speed clocks.
            // Reads and writes share the same bus-cycle placement in this core, so their relative timing needs no offset.
            _frequencyCount = -Math.Max(0, (triggerDelay * 2) - 2);
            if (!wasActive)
            {
                _sampleSuppressed = true;
            }
            _shadowFrequency = _originalFrequency;
            _didTick = false;
            _sweepPeriod = _initialSweepPeriod == 0 ? 8 : _initialSweepPeriod;
            _sweepNegated = false;
            _sweepOverflowCountdown = 0;
            _sweepTriggerCalculationCountdown = 0;
            // HandleTrigger runs before the write cycle's four trailing clocks. The CGB-E keeps the sweep
            // restart pipeline occupied for two additional 2 MHz phases compared with the DMG-B.
            var revisionHoldPhases = _hardwareRevision == ApuHardwareRevision.CgbE ? 2 : 0;
            _sweepRestartHoldCountdown = 4 + ((2 - lowFrequencyDivider + revisionHoldPhases) * 2);

            _sweepEnabled = (_shiftSweep != 0) || (_initialSweepPeriod != 0);

            if (_shiftSweep > 0)
            {
                // CGB-E performs the trigger overflow check through the 1 MHz sweep calculation pipeline.
                // An inactive channel takes one additional pipeline tick to enter that sequence.
                _sweepTriggerCalculationCountdown = (_shiftSweep + 2 + (wasActive ? 0 : 1)) * 4;
            }

            RestartEnvelope();
        }

        /// <summary>
        /// Changes the period used after the current pulse timer interval completes.
        /// </summary>
        public override void SetFrequency(int freq)
        {
            _originalFrequency = freq;
            _frequencyReload = GetFrequencyTimerPeriod(freq);

            if (_justReloaded || !Status)
            {
                _frequency = _frequencyReload;
                _frequencyCount = 0;
            }
        }

        public void SetFrequencyHigh(int freq, byte previousData, byte data, ApuHardwareRevision hardwareRevision)
        {
            var previousHighBits = previousData & 0x07;
            var newHighBits = data & 0x07;
            // SameSuite distinguishes AGB-A from the modeled CGB-E revision in this countdown window:
            // AGB-A retains the older parity-dependent pulse phase instead of replaying the prior step.
            if (hardwareRevision == ApuHardwareRevision.CgbE &&
                (data & 0x80) == 0 && Status && previousHighBits == 0x07 && newHighBits != 0x07 &&
                _didTick && !_justReloaded)
            {
                var clocksRemaining = _frequency - _frequencyCount;
                var oldPeriod = MathSchema.MAX_11_BIT_VALUE - 1 - _originalFrequency;
                if (clocksRemaining >= 2 && (clocksRemaining - 2) / 4 == oldPeriod)
                {
                    // CGB-D/E replays the previous pulse step when an active NR14 write leaves the $700 range
                    // in this countdown window. A coincident reload is handled by SetFrequency instead.
                    _wavePos = (_wavePos + 7) & 7;
                    _sampleSuppressed = false;
                }
            }

            SetFrequency(freq);
        }

        protected override int GetSample()
        {
            if (_sampleSuppressed)
            {
                return 0;
            }

            return (APUSchema.DUTY_WAVE_FORM[_latchedDutyCycle][_wavePos] * (MathSchema.MAX_4_BIT_VALUE - 1)) & _volume;
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
                        if (_shiftSweep == 0)
                        {
                            var overflowFrequency = CalculateNewFrequency(disableOnOverflow: false);
                            if (_sweepRestartHoldCountdown == 0 &&
                                overflowFrequency >= MathSchema.MAX_11_BIT_VALUE && !_negateSweep)
                            {
                                // Shift-zero calculations complete after the four-clock CGB-E pipeline delay.
                                _sweepOverflowCountdown = 4;
                            }
                        }
                        else
                        {
                            var sweepFreq = CalculateNewFrequency();

                            if (sweepFreq < MathSchema.MAX_11_BIT_VALUE)
                            {
                                _shadowFrequency = sweepFreq;
                                SetFrequency(sweepFreq);
                                _sweepFrequencyWritten = true;
                                var nextSweepFrequency = CalculateNewFrequency(disableOnOverflow: false);
                                if (nextSweepFrequency >= MathSchema.MAX_11_BIT_VALUE && !_negateSweep)
                                {
                                    // SameSuite measures eight normal-speed CPU machine cycles before the second check completes.
                                    _sweepOverflowCountdown = 32;
                                }
                            }
                        }
                    }
                }
            }
        }

        protected override void UpdateFrequency(int cycles)
        {
            if (_sweepRestartHoldCountdown > 0)
            {
                _sweepRestartHoldCountdown = Math.Max(0, _sweepRestartHoldCountdown - cycles);
            }

            if (_sweepTriggerCalculationCountdown > 0)
            {
                _sweepTriggerCalculationCountdown -= cycles;
                if (_sweepTriggerCalculationCountdown <= 0)
                {
                    _sweepTriggerCalculationCountdown = 0;
                    CalculateNewFrequency();
                }
            }

            if (_sweepOverflowCountdown > 0)
            {
                _sweepOverflowCountdown -= cycles;
                if (_sweepOverflowCountdown <= 0)
                {
                    _sweepOverflowCountdown = 0;
                    _enabled = false;
                }
            }

            _alignmentClocks = (_alignmentClocks + cycles) & 3;

            if (!Status)
            {
                _justReloaded = false;
                return;
            }

            _frequencyCount += cycles;
            _justReloaded = false;

            while (_frequencyCount >= _frequency)
            {
                _frequencyCount -= _frequency;
                _wavePos = (_wavePos + 1) % 8;
                _didTick = true;
                _latchedDutyCycle = _dutyCycle;
                _frequency = _frequencyReload;
                _sampleSuppressed = false;
                _justReloaded = _frequencyCount == 0;
            }
        }

        /// <summary>
        /// Reloads the pulse timer when the channel is triggered.
        /// </summary>
        protected override void SetFreqTimer(int freq)
        {
            _frequencyReload = GetFrequencyTimerPeriod(freq);
            _frequency = _frequencyReload;
            _frequencyCount = 0;
            _justReloaded = false;
        }

        /// <summary>
        /// Returns a period written by the sweep unit so the owning APU can mirror it into NR13 and NR14.
        /// </summary>
        public bool TryConsumeSweepFrequencyWrite(out int frequency)
        {
            frequency = _originalFrequency;

            if (!_sweepFrequencyWritten)
            {
                return false;
            }

            _sweepFrequencyWritten = false;
            return true;
        }

        private int CalculateNewFrequency(bool disableOnOverflow = true)
        {
            var sweepFreq = _shadowFrequency + (_negateSweep ? -1 : 1) * (_shadowFrequency >> _shiftSweep);

            _sweepNegated = _negateSweep || _sweepNegated;

            if (disableOnOverflow && sweepFreq >= MathSchema.MAX_11_BIT_VALUE)
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
