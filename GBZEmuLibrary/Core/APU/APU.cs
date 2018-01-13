using System;

namespace GBZEmuLibrary
{
    internal class APU
    {
        private const int FRAME_SEQUENCER_UPDATE_THRESHOLD = Sound.SAMPLE_RATE / APUSchema.FRAME_SEQUENCER_RATE;

        private readonly byte[] _memory = new byte[MemorySchema.APU_REGISTERS_END - MemorySchema.APU_REGISTERS_START];

        private readonly SquareWaveGenerator _channel1;

        private bool _powered = true;

        private readonly int _maxCyclesPerSample;
        private int _cycleCounter;

        private int _frameSequenceTimer;

        private byte[] _buffer = new byte[(Sound.SAMPLE_RATE / GameBoySchema.TARGET_FRAMERATE) * 2];

        private int _currentByte = 0;

        public APU()
        {
            _maxCyclesPerSample = GameBoySchema.MAX_DMG_CLOCK_CYCLES / Sound.SAMPLE_RATE;

            _channel1 = new SquareWaveGenerator();
        }

        public byte[] GetSoundSamples()
        {
            //TODO may need to reset buffer
            _currentByte = 0;
            return _buffer;
        }

        public void Reset()
        {
            WriteByte(0x80, 0xFF10);
            WriteByte(0xBF, 0xFF11);
            WriteByte(0xF3, 0xFF12);
            WriteByte(0xBF, 0xFF14);
            WriteByte(0x3F, 0xFF16);
            WriteByte(0x00, 0xFF17);
            WriteByte(0xBF, 0xFF19);
            WriteByte(0x7F, 0xFF1A);
            WriteByte(0xFF, 0xFF1B);
            WriteByte(0x9F, 0xFF1C);
            WriteByte(0xBF, 0xFF1E);
            WriteByte(0xFF, 0xFF20);
            WriteByte(0x00, 0xFF21);
            WriteByte(0x00, 0xFF22);
            WriteByte(0xBF, 0xFF23);
            WriteByte(0x77, 0xFF24);
            WriteByte(0xF3, 0xFF25);
            WriteByte(0xF1, 0xFF26);
        }

        public void WriteByte(byte data, int address)
        {
            int freqLowerBits, freqHighBits;

            switch (address)
            {
                case APUSchema.SQUARE_1_SWEEP_PERIOD:
                    // Register Format -PPP NSSS Sweep period, negate, shift
                    _channel1.SetSweep(data);
                    break;
                case APUSchema.SQUARE_1_DUTY_LENGTH_LOAD:
                    // Register Format DDLL LLLL Duty, Length load (64-L)
                    _channel1.SetLength(data);
                    _channel1.SetDutyCycle(data);
                    break;
                case APUSchema.SQUARE_1_VOLUME_ENVELOPE:
                    // Register Format VVVV APPP Starting volume, Envelope add mode, period
                    _channel1.SetEnvelope(data);
                    break;
                case APUSchema.SQUARE_1_FREQUENCY_LSB:
                    // Register Format FFFF FFFF Frequency LSB

                    freqLowerBits = data;
                    freqHighBits = Helpers.GetBits(ReadByte(APUSchema.SQUARE_1_FREQUENCY_MSB), 3) << 8;

                    _channel1.SetFrequency(freqHighBits + freqLowerBits);
                    break;
                case APUSchema.SQUARE_1_FREQUENCY_MSB:
                    // Register Format TL-- -FFF Trigger, Length enable, Frequency MSB

                    freqLowerBits = ReadByte(APUSchema.SQUARE_1_FREQUENCY_LSB);
                    freqHighBits  = Helpers.GetBits(data, 3) << 8;

                    _channel1.SetFrequency(freqHighBits + freqLowerBits);

                    if (!Helpers.TestBit(data, 6))
                    {
                        _channel1.SetLength(0);
                    }

                    //Trigger Enabled
                    if (Helpers.TestBit(data, 7))
                    {
                        _channel1.Inited = true;

                        //TODO handle trigger
                        if (_channel1.Length == 0)
                        {
                            _channel1.SetLength(64);
                        }

                        _channel1.SetVolume(_channel1.InitialVolume);
                    }
                    break;

                case APUSchema.SQUARE_2_DUTY_LENGTH_LOAD:
                    break;
                case APUSchema.SQUARE_2_VOLUME_ENVELOPE:
                    break;
                case APUSchema.SQUARE_2_FREQUENCY_LSB:
                    break;
                case APUSchema.SQUARE_2_FREQUENCY_MSB:
                    break;

                case APUSchema.VIN_VOL_CONTROL:
                    // Register Format ALLL BRRR Vin L enable, Left vol, Vin R enable, Right vol

                    break;

                case APUSchema.STEREO_SELECT:
                    // Register Format 8 bits 
                    // Lower 4 bits represent Right Channel for Channels 1-4
                    // Higher 4 bits represent Left Channel for Channels 1-4
                    StereoSelect(data);
                    break;
                case APUSchema.SOUND_ENABLED:
                    HandlePowerToggle(Helpers.TestBit(data, 7));
                    break;
    }

            _memory[address - MemorySchema.APU_REGISTERS_START] = data;
        }

        public byte ReadByte(int address)
        {
            // TODO NRx3 & NRx4 return 0 upon reading
            return _memory[address - MemorySchema.APU_REGISTERS_START];
        }

        public void Update(int cycles)
        {
            if (!_powered)
            {
                return;
            }

            _cycleCounter += cycles;

            //Check if ready to get sample
            if (_cycleCounter < _maxCyclesPerSample)
            {
                return;
            }

            _cycleCounter -= _maxCyclesPerSample;

            _frameSequenceTimer++;

            if (_frameSequenceTimer >= FRAME_SEQUENCER_UPDATE_THRESHOLD)
            {
                _channel1.Update();
            }

            byte leftChannel = 0;
            byte rightChannel = 0;

            if (_channel1.Enabled)
            {
                var sample = _channel1.GetCurrentSample();

                if ((_channel1.ChannelState & APUSchema.CHANNEL_LEFT) != 0)
                {
                    leftChannel += sample;
                }

                if ((_channel1.ChannelState & APUSchema.CHANNEL_RIGHT) != 0)
                {
                    rightChannel += sample;
                }
            }

            //TODO need to determine best way to handle overflow
            if (_currentByte * 2 < _buffer.Length - 1)
            {
                _buffer[_currentByte * 2]     = leftChannel;
                _buffer[_currentByte * 2 + 1] = rightChannel;

                _currentByte++;
            }
        }

        private void StereoSelect(byte val)
        {
            _channel1.ChannelState = GetChannelState(val, 1);
        }

        private int GetChannelState(byte val, int channel)
        {
            var channelState = 0;

            // Testing bits 0-3 
            if (Helpers.TestBit(val, channel - 1)) 
            {
                channelState |= APUSchema.CHANNEL_RIGHT;
            }

            // Testing bits 4-7
            if (Helpers.TestBit(val, channel + 3))
            {
                channelState |= APUSchema.CHANNEL_LEFT;
            }

            return channelState;
        }

        private void HandlePowerToggle(bool newState)
        {
            if (!newState && _powered)
            {
                //Reset registers (except length counters on DMG)
            }
            else if (newState && !_powered)
            {
                //Reset frame sequencer
            }
        }
    }
}
