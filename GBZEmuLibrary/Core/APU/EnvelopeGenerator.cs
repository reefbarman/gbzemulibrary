using System;

namespace GBZEmuLibrary
{
    /// <summary>
    /// Provides the trigger-loaded volume envelope shared by the pulse and noise channels.
    /// </summary>
    internal abstract class EnvelopeGenerator : Generator
    {
        protected int _initialVolume;
        protected int _volume;
        protected int _envelopePeriod;
        protected int _initialEnvelopePeriod;
        protected bool _addEnvelope;

        private bool _envelopeLocked;
        private bool _envelopeWriteClockPending;

        protected EnvelopeGenerator(int maxLength) : base(maxLength)
        {
        }

        public override void Reset()
        {
            base.Reset();

            _initialVolume = 0;
            _volume = 0;
            _envelopePeriod = 0;
            _initialEnvelopePeriod = 0;
            _addEnvelope = false;
            _envelopeLocked = false;
            _envelopeWriteClockPending = false;
        }

        /// <summary>
        /// Applies an NRx2 write, including deterministic CGB active-channel volume transitions.
        /// </summary>
        public void SetEnvelope(byte data, ApuHardwareRevision hardwareRevision)
        {
            // Val Format VVVV APPP
            var newPeriod = Helpers.GetBits(data, 3);
            var newDirection = Helpers.TestBit(data, 3);
            var newInitialVolume = Helpers.GetBitsIsolated(data, 4, 4);

            if (hardwareRevision == ApuHardwareRevision.DmgB)
            {
                // Preserve the existing DMG write model; pre-CGB zombie behavior varies by revision and instance.
                _envelopePeriod = newPeriod;
                _volume = newInitialVolume;
                _envelopeLocked = false;
            }
            else if (_enabled && Helpers.GetBitsIsolated(data, 3, 5) != 0)
            {
                var oldPeriod = _initialEnvelopePeriod;
                ApplyCgbWriteTransition(newPeriod, newDirection);
                _envelopeWriteClockPending = newPeriod != 0 && oldPeriod == 0 && !_envelopeLocked;
            }

            if (newPeriod == 0)
            {
                _envelopePeriod = 0;
                _envelopeWriteClockPending = false;
            }

            _initialEnvelopePeriod = newPeriod;
            _addEnvelope = newDirection;
            _initialVolume = newInitialVolume;
        }

        /// <summary>
        /// Loads the live envelope state when the owning channel is triggered.
        /// </summary>
        protected void RestartEnvelope()
        {
            _volume = _initialVolume;
            _envelopePeriod = _initialEnvelopePeriod;
            _envelopeLocked = false;
            _envelopeWriteClockPending = false;
        }

        protected override void UpdateEnvelop()
        {
            if (_envelopePeriod > 0)
            {
                _envelopePeriod--;

                if (_envelopePeriod == 0)
                {
                    _envelopePeriod = _initialEnvelopePeriod;
                    StepEnvelope();
                }
            }
        }

        protected override void UpdateEnvelopeWriteClock()
        {
            // The sequencer index starts at zero before the first (odd-numbered) DIV-APU event.
            if (!_envelopeWriteClockPending || _sequenceTimer % 2 == 0)
            {
                return;
            }

            _envelopeWriteClockPending = false;
            _envelopePeriod = _initialEnvelopePeriod;
            StepEnvelope();
        }

        private void StepEnvelope()
        {
            if (_envelopeLocked)
            {
                return;
            }

            var nextVolume = _volume + (_addEnvelope ? 1 : -1);
            if (nextVolume < 0 || nextVolume >= MathSchema.MAX_4_BIT_VALUE)
            {
                _envelopeLocked = true;
                _envelopeWriteClockPending = false;
                return;
            }

            _volume = nextVolume;
        }

        private void ApplyCgbWriteTransition(int newPeriod, bool newDirection)
        {
            var oldPeriod = _initialEnvelopePeriod;
            var oldDirection = _addEnvelope;
            var shouldTick = newPeriod != 0 && oldPeriod == 0 && !_envelopeLocked;

            if (newPeriod == 0 && newDirection && oldPeriod == 0 && oldDirection && !_envelopeLocked)
            {
                shouldTick = true;
            }

            if (newDirection != oldDirection)
            {
                if (newDirection)
                {
                    _volume = oldPeriod == 0 && !_envelopeLocked
                        ? _volume ^ 0x0F
                        : (0x0E - _volume) & 0x0F;
                    shouldTick = false;
                }
                else
                {
                    _volume = (0x10 - _volume) & 0x0F;
                }
            }

            if (shouldTick)
            {
                _volume = (_volume + (newDirection ? 1 : -1)) & 0x0F;
            }
        }
    }
}
