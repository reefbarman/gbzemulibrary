using System;

namespace GBZEmuLibrary
{
    internal abstract class Generator
    {
        public int ChannelState { get; set; }
        public bool Enabled { get; set; } = true;
        public bool Status => _enabled && _dacEnabled;

        /// <summary>
        /// Returns the channel's current four-bit generation-circuit output for CGB PCM readback.
        /// </summary>
        public byte DigitalOutput => Status ? (byte)(GetSample() & 0x0F) : (byte)0;

        protected bool _dacEnabled;
        protected bool _enabled;

        protected int _maxLength;
        protected int _totalLength;
        protected bool _lengthEnabled;

        protected int _originalFrequency;
        protected int _frequency;
        protected int _frequencyCount;

        protected int _sequenceTimer;

        protected Generator(int maxLength)
        {
            _maxLength = maxLength;
        }

        public abstract byte ReadByte(int address);
        public abstract void HandleTrigger();
        protected abstract int GetSample();
        protected abstract void UpdateFrequency(int cycles);

        public virtual void Init()
        {
            _sequenceTimer = 0;
        }

        public virtual void Reset()
        {
            ChannelState = 0;
            ToggleDAC(false);

            SetLength(0);
            _lengthEnabled = false;

            SetFrequency(0);
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

        public void Update(bool powered, int cycles)
        {
            if (powered)
            {
                UpdateFrequency(cycles);
            }
        }

        /// <summary>
        /// Applies one shared DIV-APU frame-sequencer tick to this channel.
        /// </summary>
        public void ClockFrameSequencer()
        {
            // Length clocks at 256 Hz on steps 0, 2, 4, and 6.
            if (_sequenceTimer % 2 == 0)
            {
                UpdateLength();
            }

            // Channel 1 sweep clocks at 128 Hz on steps 2 and 6.
            if ((_sequenceTimer + 2) % 4 == 0)
            {
                UpdateSweep();
            }

            // Envelopes clock at 64 Hz on step 7.
            if (_sequenceTimer == 7)
            {
                UpdateEnvelop();
            }

            _sequenceTimer = (_sequenceTimer + 1) % 8;
        }

        public void ToggleDAC(bool enabled)
        {
            _dacEnabled = enabled;
            _enabled &= _dacEnabled;
        }

        public void SetLength(byte data)
        {
            _totalLength = _maxLength - data;
        }

        public void ToggleLength(bool enabled)
        {
            var previousState = _lengthEnabled;
            _lengthEnabled = enabled;

            /* Extra length clocking occurs when writing to NRx4 when the frame sequencer's next step is one
             * that doesn't clock the length counter. In this case, if the length counter was PREVIOUSLY disabled
             * and now enabled and the length counter is not zero, it is decremented. If this decrement makes it zero
             * and trigger is clear, the channel is disabled. On the CGB-02, the length counter only has to have been
             * disabled before; the current length enable state doesn't matter. This breaks at least one game
             * (Prehistorik Man), and was fixed on CGB-04 and CGB-05.*/
            if (!previousState && _lengthEnabled && _sequenceTimer % 2 != 0)
            {
                UpdateLength();
            }
        }

        /// <summary>
        /// Applies an 11-bit channel period and reloads the channel-specific frequency timer.
        /// </summary>
        public virtual void SetFrequency(int freq)
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

        protected virtual void UpdateLength()
        {
            if (_totalLength > 0 && _lengthEnabled)
            {
                _enabled &= --_totalLength > 0;
            }
        }

        /// <summary>
        /// Reloads the channel timer from an 11-bit period using the pulse-channel clock divider.
        /// </summary>
        protected virtual void SetFreqTimer(int freq)
        {
            _frequency = GetFrequencyTimerPeriod(freq);
            _frequencyCount = 0;
        }

        /// <summary>
        /// Converts an 11-bit hardware period into CPU clocks for this channel's timer.
        /// </summary>
        protected virtual int GetFrequencyTimerPeriod(int freq)
        {
            return (MathSchema.MAX_11_BIT_VALUE - freq) * 4;
        }
    }
}
