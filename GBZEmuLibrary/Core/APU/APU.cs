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

        //Using double buffering
        private readonly byte[][] _buffer =
        {
            new byte[((Sound.SAMPLE_RATE / GameBoySchema.TARGET_FRAMERATE) * 2) + 2],
            new byte[((Sound.SAMPLE_RATE / GameBoySchema.TARGET_FRAMERATE) * 2) + 2],
        };

        private int _currentBuffer;
        private int _currentByte;

        private byte _leftChannelVolume;
        private byte _rightChannelVolume;

        private bool _leftVinEnabled;
        private bool _rightVinEnabled;

        private int _unusedBits;

        public APU()
        {
            _maxCyclesPerSample = GameBoySchema.MAX_DMG_CLOCK_CYCLES / (float)Sound.SAMPLE_RATE;

            _channel1 = new SquareWaveGenerator();
            _channel2 = new SquareWaveGenerator();
            _channel3 = new WaveGenerator();
            _channel4 = new NoiseGenerator();
        }

        public void ToggleChannel(Sound.Channel channel, bool enabled)
        {
            switch (channel)
            {
                case Sound.Channel.Channel1:
                    _channel1.Enabled = enabled;
                    break;
                case Sound.Channel.Channel2:
                    _channel2.Enabled = enabled;
                    break;
                case Sound.Channel.Channel3:
                    _channel3.Enabled = enabled;
                    break;
                case Sound.Channel.Channel4:
                    _channel4.Enabled = enabled;
                    break;
            }
        }

        public byte[] GetSoundSamples()
        {
            _currentByte = 0;

            var outSamples = _buffer[_currentBuffer];
            _currentBuffer = (_currentBuffer + 1) % _buffer.Length;

            Array.Clear(_buffer[_currentBuffer], 0, _buffer[_currentBuffer].Length);

            return outSamples;
        }

        public void Reset()
        {
            _powered = true;

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

            _powered = false;
        }

        public void WriteByte(byte data, int address)
        {
            //TODO CGB may not write to length counters when powered off
            if (!_powered && !(address >= APUSchema.WAVE_TABLE_START && address < APUSchema.WAVE_TABLE_END) && Array.IndexOf(APUSchema.REGISTERS_ALWAYS_WRITTEN, address) == -1)
            {
                return;
            }

            _memory[address - MemorySchema.APU_REGISTERS_START] = data;

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

                    if (_powered)
                    {
                        _channel1.SetDutyCycle(data);
                    }
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

                    if (_powered)
                    {
                        _channel2.SetDutyCycle(data);
                    }
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

                    _rightVinEnabled = Helpers.TestBit(data, 3);
                    _leftVinEnabled  = Helpers.TestBit(data, 7);

                    break;

                case APUSchema.STEREO_SELECT:
                    // Register Format 8 bits 
                    // Lower 4 bits represent Right Channel for Channels 1-4
                    // Higher 4 bits represent Left Channel for Channels 1-4
                    StereoSelect(data);
                    break;

                case APUSchema.SOUND_ENABLED:
                    // Register Format P--- NW21 Power control/status, Channel length statuses
                    HandlePowerToggle(Helpers.TestBit(data, 7));
                    _unusedBits = Helpers.GetBitsIsolated(data, 4, 3);
                    break;

                default:
                    if (address >= APUSchema.UNUSED_START && address < APUSchema.UNUSED_END || address == APUSchema.SQUARE_2_UNUSED || address == APUSchema.NOISE_4_UNUSED)
                    {
                        return;
                    }

                    if (address >= APUSchema.WAVE_TABLE_START && address < APUSchema.WAVE_TABLE_END)
                    {
                        _channel3.WriteByte(data, address);
                        return;
                    }

                    throw new IndexOutOfRangeException();
            }
        }

        public byte ReadByte(int address)
        {
            var realByte = ReadByteInternal(address);
            var lastByteORed = ReadByteLast(address);
            var lastByte = _memory[address - MemorySchema.APU_REGISTERS_START];

            if (realByte != lastByteORed)
            {
                Console.WriteLine($"Real: {Convert.ToString(realByte, 2)}, Last: {Convert.ToString(lastByte, 2)}");
                Helpers.NoOp();
            }

            switch (address)
            {
                case APUSchema.SQUARE_1_SWEEP_PERIOD:
                    return realByte;

                case APUSchema.SQUARE_1_DUTY_LENGTH_LOAD:
                    return realByte;

                case APUSchema.SQUARE_1_VOLUME_ENVELOPE:
                    return realByte;

                case APUSchema.SQUARE_1_FREQUENCY_LSB:
                    return lastByte;

                case APUSchema.SQUARE_1_FREQUENCY_MSB:
                    return realByte;

                case APUSchema.SQUARE_2_UNUSED:
                    return lastByteORed;

                case APUSchema.SQUARE_2_DUTY_LENGTH_LOAD:
                    return lastByteORed;

                case APUSchema.SQUARE_2_VOLUME_ENVELOPE:
                    return lastByteORed;

                case APUSchema.SQUARE_2_FREQUENCY_LSB:
                    return lastByte;

                case APUSchema.SQUARE_2_FREQUENCY_MSB:
                    return lastByteORed;

                case APUSchema.WAVE_3_DAC:
                    return lastByteORed;

                case APUSchema.WAVE_3_LENGTH_LOAD:
                    return lastByteORed;

                case APUSchema.WAVE_3_VOLUME:
                    return lastByteORed;

                case APUSchema.WAVE_3_FREQUENCY_LSB:
                    return lastByte;

                case APUSchema.WAVE_3_FREQUENCY_MSB:
                    return lastByteORed;

                case APUSchema.NOISE_4_UNUSED:
                    return lastByteORed;

                case APUSchema.NOISE_4_LENGTH_LOAD:
                    return lastByteORed;

                case APUSchema.NOISE_4_VOLUME_ENVELOPE:
                    return lastByteORed;

                case APUSchema.NOISE_4_CLOCK_WIDTH_DIVISOR:
                    return lastByteORed;

                case APUSchema.NOISE_4_TRIGGER:
                    return lastByteORed;

                case APUSchema.VIN_VOL_CONTROL:
                    return lastByteORed;

                case APUSchema.STEREO_SELECT:
                    return lastByteORed;

                case APUSchema.SOUND_ENABLED:
                    return lastByteORed;


                default:
                    return realByte;
            }
        }

        private byte ReadByteLast(int address)
        {
            var lastByte = _memory[address - MemorySchema.APU_REGISTERS_START];

            switch (address)
            {
                case APUSchema.SQUARE_1_SWEEP_PERIOD:
                    return (byte)(0x80 | lastByte);

                case APUSchema.SQUARE_1_DUTY_LENGTH_LOAD:
                    return (byte)(0x3F | lastByte);

                case APUSchema.SQUARE_1_VOLUME_ENVELOPE:
                    return lastByte;

                case APUSchema.SQUARE_1_FREQUENCY_LSB:
                    return 0xFF;

                case APUSchema.SQUARE_1_FREQUENCY_MSB:
                    return (byte)(0xBF | lastByte);

                case APUSchema.SQUARE_2_UNUSED:
                    return 0xFF;

                case APUSchema.SQUARE_2_DUTY_LENGTH_LOAD:
                    return (byte)(0x3F | lastByte);

                case APUSchema.SQUARE_2_VOLUME_ENVELOPE:
                    return lastByte;

                case APUSchema.SQUARE_2_FREQUENCY_LSB:
                    return 0xFF;

                case APUSchema.SQUARE_2_FREQUENCY_MSB:
                    return (byte)(0xBF | lastByte);

                case APUSchema.WAVE_3_DAC:
                    return (byte)(0x7F | lastByte);

                case APUSchema.WAVE_3_LENGTH_LOAD:
                    return 0xFF;

                case APUSchema.WAVE_3_VOLUME:
                    return (byte)(0x9F | lastByte);

                case APUSchema.WAVE_3_FREQUENCY_LSB:
                    return 0xFF;

                case APUSchema.WAVE_3_FREQUENCY_MSB:
                    return (byte)(0xBF | lastByte);

                case APUSchema.NOISE_4_UNUSED:
                    return 0xFF;

                case APUSchema.NOISE_4_LENGTH_LOAD:
                    return 0xFF;

                case APUSchema.NOISE_4_VOLUME_ENVELOPE:
                    return lastByte;

                case APUSchema.NOISE_4_CLOCK_WIDTH_DIVISOR:
                    return lastByte;

                case APUSchema.NOISE_4_TRIGGER:
                    return (byte)(0xBF | lastByte);

                case APUSchema.VIN_VOL_CONTROL:
                    return lastByte;

                case APUSchema.STEREO_SELECT:
                    return lastByte;

                case APUSchema.SOUND_ENABLED:
                    return (byte)(0x70 | (_powered ? (1 << 7) : 0)
                                       | (_unusedBits << 4) // unused bits
                                       | (_channel1.Status ? (1 << 0) : 0)
                                       | (_channel2.Status ? (1 << 1) : 0)
                                       | (_channel3.Status ? (1 << 2) : 0)
                                       | (_channel4.Status ? (1 << 3) : 0));


                default:
                    if (address >= APUSchema.UNUSED_START && address < APUSchema.UNUSED_END)
                    {
                        return 0xFF;
                    }

                    if (address >= APUSchema.WAVE_TABLE_START && address < APUSchema.WAVE_TABLE_END)
                    {
                        return _channel3.ReadByte(address);
                    }

                    throw new IndexOutOfRangeException();
            }
        }

        private byte ReadByteInternal(int address)
        {
            if (address >= APUSchema.SQUARE_1_SWEEP_PERIOD && address < APUSchema.SQUARE_2_UNUSED)
            {
                return _channel1.ReadByte(address);
            }

            if (address >= APUSchema.SQUARE_2_UNUSED && address < APUSchema.WAVE_3_DAC)
            {
                return _channel2.ReadByte(address);
            }

            if (address >= APUSchema.WAVE_3_DAC && address < APUSchema.NOISE_4_UNUSED || address >= APUSchema.WAVE_TABLE_START && address < APUSchema.WAVE_TABLE_END)
            {
                return _channel3.ReadByte(address);
            }

            if (address >= APUSchema.NOISE_4_UNUSED && address < APUSchema.VIN_VOL_CONTROL)
            {
                return _channel4.ReadByte(address);
            }

            int register;

            switch (address)
            {
                case APUSchema.VIN_VOL_CONTROL:
                    // Register Format ALLL BRRR Vin L enable, Left vol, Vin R enable, Right vol
                    register = _rightChannelVolume | ((_rightVinEnabled ? 1 : 0) << 3) | (_leftChannelVolume << 4) | ((_leftVinEnabled ? 1 : 0) << 7);
                    return (byte)(0x00 | register);

                case APUSchema.STEREO_SELECT:
                    // Register Format 8 bits 
                    // Lower 4 bits represent Right Channel for Channels 1-4
                    // Higher 4 bits represent Left Channel for Channels 1-4
                    register = ((_channel1.ChannelState & APUSchema.CHANNEL_RIGHT) != 0 ? 1 : 0)
                               | (((_channel2.ChannelState & APUSchema.CHANNEL_RIGHT) != 0 ? 1 : 0) << 1)
                               | (((_channel3.ChannelState & APUSchema.CHANNEL_RIGHT) != 0 ? 1 : 0) << 2)
                               | (((_channel4.ChannelState & APUSchema.CHANNEL_RIGHT) != 0 ? 1 : 0) << 3)
                               | (((_channel1.ChannelState & APUSchema.CHANNEL_LEFT) != 0 ? 1 : 0) << 4)
                               | (((_channel2.ChannelState & APUSchema.CHANNEL_LEFT) != 0 ? 1 : 0) << 5)
                               | (((_channel3.ChannelState & APUSchema.CHANNEL_LEFT) != 0 ? 1 : 0) << 6)
                               | (((_channel4.ChannelState & APUSchema.CHANNEL_LEFT) != 0 ? 1 : 0) << 7);
                    return (byte)(0x00 | register);

                case APUSchema.SOUND_ENABLED:
                    // Register Format P--- NW21 Power control/status, Channel length statuses
                    register = (_powered ? (1 << 7) : 0)
                               | (_unusedBits << 4) // unused bits
                               | (_channel1.Status ? (1 << 0) : 0)
                               | (_channel2.Status ? (1 << 1) : 0)
                               | (_channel3.Status ? (1 << 2) : 0)
                               | (_channel4.Status ? (1 << 3) : 0);

                    return (byte)(0x70 | register);


                default:
                    if (address >= APUSchema.UNUSED_START && address < APUSchema.UNUSED_END)
                    {
                        return 0xFF;
                    }

                    throw new IndexOutOfRangeException();
            }
        }

        public void Update(int cycles)
        {
            _channel1.Update(_powered, cycles);
            _channel2.Update(_powered, cycles);
            _channel3.Update(_powered, cycles);
            _channel4.Update(_powered, cycles);

            _cycleCounter += cycles;

            //Check if ready to get sample
            if (_cycleCounter < _maxCyclesPerSample)
            {
                return;
            }

            _cycleCounter -= _maxCyclesPerSample;

            var leftChannel  = 0;
            var rightChannel = 0;

            if (_powered)
            {
                _channel1.GetCurrentSample(ref leftChannel, ref rightChannel);
                _channel2.GetCurrentSample(ref leftChannel, ref rightChannel);
                _channel3.GetCurrentSample(ref leftChannel, ref rightChannel);
                _channel4.GetCurrentSample(ref leftChannel, ref rightChannel);
            }

            if (_currentByte * 2 < _buffer[_currentBuffer].Length - 1)
            {
                _buffer[_currentBuffer][_currentByte * 2]     = (byte)((leftChannel * (1 + _leftChannelVolume)) / 8);
                _buffer[_currentBuffer][_currentByte * 2 + 1] = (byte)((rightChannel * (1 + _rightChannelVolume)) / 8);

                _currentByte++;
            }
        }

        private void StereoSelect(byte val)
        {
            _channel1.ChannelState = GetChannelState(val, Sound.Channel.Channel1);
            _channel2.ChannelState = GetChannelState(val, Sound.Channel.Channel2);
            _channel3.ChannelState = GetChannelState(val, Sound.Channel.Channel3);
            _channel4.ChannelState = GetChannelState(val, Sound.Channel.Channel4);
        }

        private int GetChannelState(byte val, Sound.Channel channel)
        {
            var channelState = 0;

            // Testing bits 0-3 
            if (Helpers.TestBit(val, (int)channel - 1))
            {
                channelState |= APUSchema.CHANNEL_RIGHT;
            }

            // Testing bits 4-7
            if (Helpers.TestBit(val, (int)channel + 3))
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

                for (var i = 0; i < (APUSchema.SOUND_ENABLED - MemorySchema.APU_REGISTERS_START); i++)
                {
                    _memory[i] = 0;
                }

                _leftChannelVolume  = 0;
                _rightChannelVolume = 0;

                _leftVinEnabled  = false;
                _rightVinEnabled = false;
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
