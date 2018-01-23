using System;

namespace GBZEmuLibrary
{
    internal abstract class Generator
    {
        public int ChannelState { get; set; }
        public bool Enabled { get; set; } = true;

        protected bool _dacEnabled;
        protected bool _enabled;

        protected int  _maxLength;
        protected int  _totalLength;
        protected bool _lengthEnabled;

        protected int _originalFrequency;
        protected int _frequency;
        protected int _frequencyCount;

        protected int _frameSequenceTimer;
        protected int _sequenceTimer;

        protected Generator(int maxLength)
        {
            _maxLength = maxLength;
        }

        public abstract    void Init();
        public abstract    void HandleTrigger();
        protected abstract int  GetSample();

        public virtual void Reset()
        {
            ChannelState = 0;
            _enabled     = false;

            _totalLength   = 0;
            _lengthEnabled = false;

            _originalFrequency = 0;
            _frequency         = 0;
        }

        public virtual void GetCurrentSample(ref int leftChannel, ref int rightChannel)
        {
            if (_enabled && Enabled)
            {
                var sample = GetSample();

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

        public void ToggleDAC(bool enabled)
        {
            _dacEnabled =  enabled;
            _enabled    &= _dacEnabled;
        }

        public void SetLength(byte data)
        {
            _totalLength = _maxLength - data;
        }

        public void ToggleLength(bool enabled)
        {
            /* Extra length clocking occurs when writing to NRx4 when the frame sequencer's next step is one
             * that doesn't clock the length counter. In this case, if the length counter was PREVIOUSLY disabled
             * and now enabled and the length counter is not zero, it is decremented. If this decrement makes it zero
             * and trigger is clear, the channel is disabled. On the CGB-02, the length counter only has to have been
             * disabled before; the current length enable state doesn't matter. This breaks at least one game
             * (Prehistorik Man), and was fixed on CGB-04 and CGB-05.*/
            if (!_lengthEnabled && enabled && _sequenceTimer % 2 != 0)
            {
                UpdateLength();
            }

            _lengthEnabled = enabled;
        }

        public void SetFrequency(int freq)
        {
            _originalFrequency = freq;
            SetFreqTimer(freq);
        }

        protected virtual void UpdateSweep()
        {
        }

        protected virtual void UpdateEnvelop()
        {
        }

        protected void UpdateLength()
        {
            if (_totalLength > 0 && _lengthEnabled)
            {
                _enabled &= --_totalLength > 0;
            }
        }

        protected virtual void UpdateFrequency(int cycles)
        {
        }

        protected void SetFreqTimer(int freq)
        {
            _frequency = (MathSchema.MAX_11_BIT_VALUE - freq) * 4;
        }
    }
}
