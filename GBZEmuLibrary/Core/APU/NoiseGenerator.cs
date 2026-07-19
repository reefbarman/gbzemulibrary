using System;

namespace GBZEmuLibrary
{
    /// <summary>
    /// Emulates channel 4's period divider, selectable-width LFSR, and volume envelope.
    /// </summary>
    internal class NoiseGenerator : EnvelopeGenerator
    {
        private int _divRatio;
        private int _widthMode;
        private int _clockShift;

        private int _linearFeedbackShiftRegister = 1;
        private int _noiseCounter;
        private int _counterCountdown;
        private int _alignmentClocks;
        private bool _backgroundCounterActive;
        private ApuHardwareRevision _hardwareRevision;

        public NoiseGenerator() : base(MathSchema.MAX_6_BIT_VALUE)
        {
        }

        /// <summary>
        /// Initializes the free-running noise divider to the reset NR43 period.
        /// </summary>
        public override void Init()
        {
            base.Init();
            _noiseCounter = 0;
            _counterCountdown = 0;
            _alignmentClocks = 0;
            _backgroundCounterActive = false;
        }

        public override byte ReadByte(int address)
        {
            int register;

            switch (address)
            {
                case APUSchema.NOISE_4_UNUSED:
                    return 0xFF;

                case APUSchema.NOISE_4_LENGTH_LOAD:
                    return 0xFF;

                case APUSchema.NOISE_4_VOLUME_ENVELOPE:
                    // Register Format VVVV APPP Starting volume, Envelope add mode, period
                    register = _initialEnvelopePeriod | (_addEnvelope ? 1 : 0) << 3 | _initialVolume << 4;
                    return (byte)(0x00 | register);

                case APUSchema.NOISE_4_CLOCK_WIDTH_DIVISOR:
                    // Register Format SSSS WDDD Clock shift, Width mode of LFSR, Divisor code
                    register = _divRatio | (_widthMode << 3) | (_clockShift << 4);
                    return (byte)(0x00 | register);

                case APUSchema.NOISE_4_TRIGGER:
                    // Register Format TL-- ---- Trigger, Length enable (Only interested in length enabled)
                    register = (_lengthEnabled ? 1 : 0) << 6;
                    return (byte)(0xBF | register);
            }

            throw new IndexOutOfRangeException();
        }

        public override void Reset()
        {
            base.Reset();

            _divRatio = 0;
            _widthMode = 0;
            _clockShift = 0;
            _noiseCounter = 0;
            _counterCountdown = 0;
            _alignmentClocks = 0;
            _backgroundCounterActive = false;
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

            _linearFeedbackShiftRegister = 0x7FFF;
            StartCounter();

            RestartEnvelope();
        }

        public void SetHardwareRevision(ApuHardwareRevision hardwareRevision)
        {
            _hardwareRevision = hardwareRevision;
        }

        /// <summary>
        /// Applies NR43's divisor, width, and clock-shift fields without restarting the free-running period phase.
        /// </summary>
        public void SetFrequencyParameters(byte data)
        {
            var oldDivRatio = _divRatio;
            var oldClockShift = _clockShift;
            _divRatio = Helpers.GetBits(data, 3);
            _widthMode = Helpers.GetBitsIsolated(data, 3, 1);
            _clockShift = Helpers.GetBitsIsolated(data, 4, 4);

            // On CGB-E, changing the selected counter tap clocks the LFSR when the write itself creates a rising edge.
            // Divisor and width writes otherwise retain both the counter and its current countdown.
            var oldBit = (_noiseCounter & (1 << oldClockShift)) != 0;
            var newBit = (_noiseCounter & (1 << _clockShift)) != 0;
            if (_hardwareRevision == ApuHardwareRevision.CgbE && _backgroundCounterActive && !oldBit && newBit)
            {
                StepLfsr();
            }

            if (_hardwareRevision == ApuHardwareRevision.CgbE && _backgroundCounterActive && oldDivRatio != _divRatio)
            {
                if ((_alignmentClocks & 4) != 0)
                {
                    _counterCountdown = GetDivisorPeriod();
                }
                else if (oldDivRatio == 0)
                {
                    _counterCountdown = GetDivisorPeriod() + 4;
                }
            }
        }

        protected override int GetSample()
        {
            return Helpers.TestBit(_linearFeedbackShiftRegister, 0) ? 0 : (MathSchema.MAX_4_BIT_VALUE - 1 & _volume);
        }

        protected override void UpdateFrequency(int cycles)
        {
            _alignmentClocks = (_alignmentClocks + cycles) & 7;

            if (!_backgroundCounterActive)
            {
                return;
            }

            var cyclesLeft = cycles;
            var divisorPeriod = GetDivisorPeriod();

            while (cyclesLeft >= _counterCountdown)
            {
                cyclesLeft -= _counterCountdown;
                _counterCountdown = divisorPeriod;

                var selectedBit = 1 << _clockShift;
                var oldBit = (_noiseCounter & selectedBit) != 0;
                _noiseCounter = (_noiseCounter + 1) & 0x3FFF;
                var newBit = (_noiseCounter & selectedBit) != 0;

                if (!oldBit && newBit && _enabled)
                {
                    StepLfsr();
                }
            }

            _counterCountdown -= cyclesLeft;
        }

        /// <summary>
        /// Starts channel 4's divider at the CGB-E alignment measured by SameSuite. The divider then free-runs
        /// across retriggers and NR43 writes until the APU is powered off.
        /// </summary>
        private void StartCounter()
        {
            var alignment = (_alignmentClocks >> 1) & 3;
            var countdown = _divRatio == 0 ? 6 : _divRatio * 4 + 6;

            if ((alignment & 1) != 0)
            {
                if (_divRatio == 0)
                {
                    countdown += _backgroundCounterActive ? -1 : 1;
                }
                else if ((alignment & 2) != 0)
                {
                    countdown -= 3;
                }
                else
                {
                    countdown--;
                }
            }
            else if (_divRatio != 0)
            {
                if ((alignment & 2) != 0)
                {
                    countdown -= 2;
                }
                else if (_divRatio > 1)
                {
                    countdown -= 4;
                }
            }

            _counterCountdown = countdown * 2;
            _backgroundCounterActive = true;
        }

        private int GetDivisorPeriod()
        {
            return _divRatio == 0 ? 4 : _divRatio * 8;
        }

        private void StepLfsr()
        {
            var xor = Helpers.GetBits(_linearFeedbackShiftRegister, 1) ^ Helpers.GetBitsIsolated(_linearFeedbackShiftRegister, 1, 1);
            _linearFeedbackShiftRegister >>= 1;
            Helpers.SetBit(ref _linearFeedbackShiftRegister, 14, xor != 0);

            if (_widthMode == 1)
            {
                Helpers.SetBit(ref _linearFeedbackShiftRegister, 6, xor != 0);
            }
        }
    }
}
