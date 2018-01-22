using System;

namespace GBZEmuLibrary
{
    internal class APU
    {
        private readonly byte[] _memory = new byte[MemorySchema.APU_REGISTERS_END - MemorySchema.APU_REGISTERS_START];

        private readonly SquareWaveGenerator _channel1;
        private readonly SquareWaveGenerator _channel2;
        private readonly WaveGenerator       _channel3;
        private readonly NoiseGenerator      _channel4;

        private bool _powered;

        private readonly float _maxCyclesPerSample;
        private          float _cycleCounter;

        private readonly byte[] _buffer = new byte[((Sound.SAMPLE_RATE / GameBoySchema.TARGET_FRAMERATE) * 2) + 2];

        private int _currentByte;

        private byte _leftChannelVolume;
        private byte _rightChannelVolume;

        public APU()
        {
            _maxCyclesPerSample = GameBoySchema.MAX_DMG_CLOCK_CYCLES / (float)Sound.SAMPLE_RATE;

            _channel1 = new SquareWaveGenerator();
            _channel2 = new SquareWaveGenerator();
            _channel3 = new WaveGenerator();
            _channel4 = new NoiseGenerator();
        }

        public byte[] GetSoundSamples()
        {
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
            //TODO ignore writes if disabled

            int freqLowerBits, freqHighBits;

            switch (address)
            {
                case APUSchema.SQUARE_1_SWEEP_PERIOD:
                    // Register Format -PPP NSSS Sweep period, negate, shift
                    _channel1.SetSweep(data);
                    break;
                case APUSchema.SQUARE_1_DUTY_LENGTH_LOAD:
                    // Register Format DDLL LLLL Duty, Length load (64-L)
                    _channel1.SetLength(Helpers.GetBits(data, 6));
                    _channel1.SetDutyCycle(data);
                    break;
                case APUSchema.SQUARE_1_VOLUME_ENVELOPE:
                    // Register Format VVVV APPP Starting volume, Envelope add mode, period
                    _channel1.SetEnvelope(data);
                    _channel1.ToggleDAC(Helpers.GetBitsIsolated(data, 3, 5) != 0);
                    break;
                case APUSchema.SQUARE_1_FREQUENCY_LSB:
                    // Register Format FFFF FFFF Frequency LSB

                    freqLowerBits = data;
                    freqHighBits  = Helpers.GetBits(ReadByte(APUSchema.SQUARE_1_FREQUENCY_MSB), 3) << 8;

                    _channel1.SetFrequency(freqHighBits + freqLowerBits);
                    break;
                case APUSchema.SQUARE_1_FREQUENCY_MSB:
                    // Register Format TL-- -FFF Trigger, Length enable, Frequency MSB

                    freqLowerBits = ReadByte(APUSchema.SQUARE_1_FREQUENCY_LSB);
                    freqHighBits  = Helpers.GetBits(data, 3) << 8;

                    _channel1.SetFrequency(freqHighBits + freqLowerBits);

                    _channel1.ToggleLength(Helpers.TestBit(data, 6));

                    if (Helpers.TestBit(data, 7))
                    {
                        _channel1.HandleTrigger();
                    }

                    break;

                case APUSchema.SQUARE_2_DUTY_LENGTH_LOAD:
                    // Register Format DDLL LLLL Duty, Length load (64-L)
                    _channel2.SetLength(Helpers.GetBits(data, 6));
                    _channel2.SetDutyCycle(data);
                    break;
                case APUSchema.SQUARE_2_VOLUME_ENVELOPE:
                    // Register Format VVVV APPP Starting volume, Envelope add mode, period
                    _channel2.SetEnvelope(data);
                    _channel2.ToggleDAC(Helpers.GetBitsIsolated(data, 3, 5) != 0);
                    break;
                case APUSchema.SQUARE_2_FREQUENCY_LSB:
                    // Register Format FFFF FFFF Frequency LSB

                    freqLowerBits = data;
                    freqHighBits  = Helpers.GetBits(ReadByte(APUSchema.SQUARE_2_FREQUENCY_MSB), 3) << 8;

                    _channel2.SetFrequency(freqHighBits + freqLowerBits);
                    break;
                case APUSchema.SQUARE_2_FREQUENCY_MSB:
                    // Register Format TL-- -FFF Trigger, Length enable, Frequency MSB

                    freqLowerBits = ReadByte(APUSchema.SQUARE_2_FREQUENCY_LSB);
                    freqHighBits  = Helpers.GetBits(data, 3) << 8;

                    _channel2.SetFrequency(freqHighBits + freqLowerBits);

                    _channel2.ToggleLength(Helpers.TestBit(data, 6));

                    if (Helpers.TestBit(data, 7))
                    {
                        _channel2.HandleTrigger();
                    }

                    break;

                case APUSchema.WAVE_3_DAC:
                    // Register Format E--- ---- DAC power
                    _channel3.ToggleDAC(Helpers.TestBit(data, 7));
                    break;

                case APUSchema.WAVE_3_LENGTH_LOAD:
                    // Register Format LLLL LLLL Length load (256-L)
                    _channel3.SetLength(data);
                    break;

                case APUSchema.WAVE_3_VOLUME:
                    // Register Format -VV- ---- Volume code (00=0%, 01=100%, 10=50%, 11=25%)
                    _channel3.SetVolume(data);
                    break;

                case APUSchema.WAVE_3_FREQUENCY_LSB:
                    // Register Format FFFF FFFF Frequency LSB

                    freqLowerBits = data;
                    freqHighBits  = Helpers.GetBits(ReadByte(APUSchema.WAVE_3_FREQUENCY_MSB), 3) << 8;

                    _channel3.SetFrequency(freqHighBits + freqLowerBits);
                    break;

                case APUSchema.WAVE_3_FREQUENCY_MSB:
                    // Register Format TL-- -FFF Trigger, Length enable, Frequency MSB

                    freqLowerBits = ReadByte(APUSchema.WAVE_3_FREQUENCY_LSB);
                    freqHighBits  = Helpers.GetBits(data, 3) << 8;

                    _channel3.SetFrequency(freqHighBits + freqLowerBits);

                    _channel3.ToggleLength(Helpers.TestBit(data, 6));

                    //Trigger Enabled
                    if (Helpers.TestBit(data, 7))
                    {
                        _channel3.HandleTrigger();
                    }

                    break;

                case APUSchema.NOISE_4_LENGTH_LOAD:
                    // Register Format --LL LLLL Duty, Length load (64-L)
                    _channel4.SetLength(Helpers.GetBits(data, 6));
                    break;

                case APUSchema.NOISE_4_VOLUME_ENVELOPE:
                    // Register Format VVVV APPP Starting volume, Envelope add mode, period
                    _channel4.SetEnvelope(data);
                    _channel4.ToggleDAC(Helpers.GetBitsIsolated(data, 3, 5) != 0);
                    break;

                case APUSchema.NOISE_4_CLOCK_WIDTH_DIVISOR:
                    // Register Format SSSS WDDD Clock shift, Width mode of LFSR, Divisor code
                    _channel4.SetDivRatio(data);
                    _channel4.SetWidthMode(data);
                    _channel4.SetClockShift(data);
                    break;

                case APUSchema.NOISE_4_TRIGGER:
                    // Register Format TL-- ---- Trigger, Length enable
                    _channel4.ToggleLength(Helpers.TestBit(data, 6));

                    //Trigger Enabled
                    if (Helpers.TestBit(data, 7))
                    {
                        _channel4.HandleTrigger();
                    }

                    break;

                case APUSchema.VIN_VOL_CONTROL:
                    // Register Format ALLL BRRR Vin L enable, Left vol, Vin R enable, Right vol
                    _rightChannelVolume = Helpers.GetBits(data, 3);
                    _leftChannelVolume  = Helpers.GetBits((byte)(data >> 4), 3);

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

            if (address >= APUSchema.WAVE_TABLE_START && address < APUSchema.WAVE_TABLE_END)
            {
                _channel3.WriteByte(data, address);
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

            _channel1.Update(cycles);
            _channel2.Update(cycles);
            _channel3.Update(cycles);
            _channel4.Update(cycles);

            _cycleCounter += cycles;

            //Check if ready to get sample
            if (_cycleCounter < _maxCyclesPerSample)
            {
                return;
            }

            _cycleCounter -= _maxCyclesPerSample;

            var leftChannel  = 0;
            var rightChannel = 0;

            _channel1.GetCurrentSample(ref leftChannel, ref rightChannel);
            _channel2.GetCurrentSample(ref leftChannel, ref rightChannel);
            _channel3.GetCurrentSample(ref leftChannel, ref rightChannel);
            _channel4.GetCurrentSample(ref leftChannel, ref rightChannel);

            if (_currentByte * 2 < _buffer.Length - 1)
            {
                _buffer[_currentByte * 2]     = (byte)((leftChannel * (1 + _leftChannelVolume)) / 8);
                _buffer[_currentByte * 2 + 1] = (byte)((rightChannel * (1 + _rightChannelVolume)) / 8);

                _currentByte++;
            }
        }

        private void StereoSelect(byte val)
        {
            _channel1.ChannelState = GetChannelState(val, 1);
            _channel2.ChannelState = GetChannelState(val, 2);
            _channel3.ChannelState = GetChannelState(val, 3);
            _channel4.ChannelState = GetChannelState(val, 4);
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
                _channel1.Reset();
                _channel2.Reset();
                _channel3.Reset();
                _channel4.Reset();

                _leftChannelVolume  = 0;
                _rightChannelVolume = 0;
            }
            else if (newState && !_powered)
            {
                _channel1.Init();
                _channel2.Init();
                _channel3.Init();
                _channel4.Init();
            }

            _powered = newState;
        }
    }
}
