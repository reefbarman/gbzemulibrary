using System;

namespace GBZEmuLibrary
{
    internal abstract class EnvelopeGenerator : Generator
    {
        protected int  _initialVolume;
        protected int  _volume;
        protected int  _envelopePeriod;
        protected int  _initialEnvelopePeriod;
        protected bool _addEnvelope;

        protected EnvelopeGenerator(int maxLength) : base(maxLength)
        {
        }

        public override void Reset()
        {
            base.Reset();

            _initialVolume         = 0;
            _initialEnvelopePeriod = 0;
            _addEnvelope           = false;
        }

        public void SetEnvelope(byte data)
        {
            // Val Format VVVV APPP
            _initialEnvelopePeriod = Helpers.GetBits(data, 3);
            _envelopePeriod        = _initialEnvelopePeriod;

            _addEnvelope = Helpers.TestBit(data, 3);

            _initialVolume = Helpers.GetBitsIsolated(data, 4, 4);
            _volume        = _initialVolume;
        }

        protected override void UpdateEnvelop() 
        {
            if (_envelopePeriod > 0) {
                _envelopePeriod--;

                if (_envelopePeriod == 0) {
                    //The volume envelope and sweep timers treat a period of 0 as 8.
                    _envelopePeriod = _initialEnvelopePeriod == 0 ? 8 : _initialEnvelopePeriod;

                    if (_initialEnvelopePeriod > 0) {
                        _volume += _addEnvelope ? 1 : -1;
                        _volume =  Math.Min(Math.Max(_volume, 0), MathSchema.MAX_4_BIT_VALUE - 1);
                    }
                }
            }
        }
    }
}
