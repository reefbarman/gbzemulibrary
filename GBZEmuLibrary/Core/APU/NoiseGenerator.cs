using System;
using System.Collections.Generic;
using System.Linq;

namespace GBZEmuLibrary
{
    internal class NoiseGenerator : EnvelopeGenerator
    {
        private const int MAX_SAMPLES_PER_CHANNEL = (GameBoySchema.MAX_DMG_CLOCK_CYCLES / (Sound.SAMPLE_RATE / 2)) / 2; //TODO determine what this is

        private int _divRatio;
        private int _widthMode;
        private int _clockShift;

        private int _linearFeedbackShiftRegister = 1;

        private Queue<int> _samplesLeft  = new Queue<int>(MAX_SAMPLES_PER_CHANNEL + 1);
        private Queue<int> _samplesRight = new Queue<int>(MAX_SAMPLES_PER_CHANNEL + 1);

        public NoiseGenerator() : base(MathSchema.MAX_6_BIT_VALUE)
        {
            for (var i = 0; i < MAX_SAMPLES_PER_CHANNEL; i++)
            {
                _samplesLeft.Enqueue(0);
                _samplesRight.Enqueue(0);
            }
        }

        public override void Reset()
        {
            base.Reset();

            _divRatio   = 0;
            _widthMode  = 0;
            _clockShift = 0;
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

            SetFrequency();

            _volume         = _initialVolume;
            _envelopePeriod = _initialEnvelopePeriod == 0 ? 8 : _initialEnvelopePeriod;
        }

        public void SetDivRatio(byte data)
        {
            _divRatio = Helpers.GetBits(data, 3);
        }

        public void SetWidthMode(byte data)
        {
            _widthMode = Helpers.GetBitsIsolated(data, 4, 1);
        }

        public void SetClockShift(byte data)
        {
            _clockShift = Helpers.GetBitsIsolated(data, 4, 4);
        }

        public override void GetCurrentSample(ref int leftChannel, ref int rightChannel)
        {
            leftChannel  += FilterSamples(_samplesLeft);
            rightChannel += FilterSamples(_samplesRight);
        }

        protected override int GetSample()
        {
            return Helpers.TestBit(_linearFeedbackShiftRegister, 0) ? 0 : (MathSchema.MAX_4_BIT_VALUE - 1 & _volume);
        }

        protected override void UpdateFrequency(int cycles)
        {
            _frequencyCount += cycles;

            if (_frequencyCount >= _frequency)
            {
                _frequencyCount -= _frequency;

                if (_clockShift < 14)
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

            var leftSample  = 0;
            var rightSample = 0;

            base.GetCurrentSample(ref leftSample, ref rightSample);

            _samplesLeft.Enqueue(leftSample);

            if (_samplesLeft.Count > MAX_SAMPLES_PER_CHANNEL)
            {
                _samplesLeft.Dequeue();
            }

            _samplesRight.Enqueue(rightSample);

            if (_samplesRight.Count > MAX_SAMPLES_PER_CHANNEL)
            {
                _samplesRight.Dequeue();
            }
        }

        private void SetFrequency()
        {
            var freq = 8;

            switch (_divRatio)
            {
                case 0:
                    freq = 8 << _clockShift;
                    break;
                case 1:
                    freq = 16 << _clockShift;
                    break;
                case 2:
                    freq = 32 << _clockShift;
                    break;
                case 3:
                    freq = 48 << _clockShift;
                    break;
                case 4:
                    freq = 64 << _clockShift;
                    break;
                case 5:
                    freq = 80 << _clockShift;
                    break;
                case 6:
                    freq = 96 << _clockShift;
                    break;
                case 7:
                    freq = 112 << _clockShift;
                    break;
            }

            _frequency = freq;
        }

        private int FilterSamples(Queue<int> samplesRight)
        {
            return (int)samplesRight.Average();
        }
    }
}
