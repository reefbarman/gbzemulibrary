using GBZEmuLibrary;

namespace GBZEmuTests;

/// <summary>
/// Verifies APU register semantics and focused channel behavior without requiring a complete ROM run.
/// </summary>
public sealed class ApuRegisterTests
{
    /// <summary>
    /// Replays the DMG unused-bit assertions from Mooneye's unused_hwio test against the APU register interface.
    /// </summary>
    [Fact]
    public void DmgRegistersApplyHardwareReadMasks()
    {
        var apu = new APU();
        apu.Reset();

        Assert.Equal(0xF1, apu.ReadByte(APUSchema.SOUND_ENABLED));
        AssertMaskedRead(apu, APUSchema.SQUARE_1_SWEEP_PERIOD, 0x80, 0x00, 0x80);
        AssertMaskedRead(apu, APUSchema.SQUARE_1_SWEEP_PERIOD, 0x80, 0x80, 0x80);
        AssertFullRead(apu, APUSchema.SQUARE_1_FREQUENCY_LSB, 0x00, 0xFF);
        AssertFullRead(apu, APUSchema.SQUARE_1_FREQUENCY_LSB, 0xA5, 0xFF);
        AssertFullRead(apu, APUSchema.SQUARE_2_FREQUENCY_LSB, 0x00, 0xFF);
        AssertFullRead(apu, APUSchema.SQUARE_2_FREQUENCY_LSB, 0xA5, 0xFF);
        AssertFullRead(apu, APUSchema.WAVE_3_FREQUENCY_LSB, 0x00, 0xFF);
        AssertFullRead(apu, APUSchema.WAVE_3_FREQUENCY_LSB, 0xA5, 0xFF);
        AssertMaskedRead(apu, APUSchema.WAVE_3_DAC, 0x7F, 0x00, 0x7F);
        AssertMaskedRead(apu, APUSchema.WAVE_3_DAC, 0x7F, 0x7F, 0x7F);
        AssertMaskedRead(apu, APUSchema.WAVE_3_VOLUME, 0x9F, 0x00, 0x9F);
        AssertMaskedRead(apu, APUSchema.WAVE_3_VOLUME, 0x9F, 0x9F, 0x9F);
        AssertMaskedRead(apu, APUSchema.NOISE_4_LENGTH_LOAD, 0xC0, 0x00, 0xC0);
        AssertMaskedRead(apu, APUSchema.NOISE_4_LENGTH_LOAD, 0xC0, 0xC0, 0xC0);
        AssertMaskedRead(apu, APUSchema.NOISE_4_TRIGGER, 0x3F, 0x00, 0x3F);
        AssertMaskedRead(apu, APUSchema.NOISE_4_TRIGGER, 0x3F, 0x3F, 0x3F);
        AssertMaskedRead(apu, APUSchema.SOUND_ENABLED, 0x70, 0x80, 0x70);
        AssertMaskedRead(apu, APUSchema.SOUND_ENABLED, 0x70, 0xF0, 0x70);
    }

    /// <summary>
    /// Powers off the APU and verifies ordinary register writes are ignored until NR52 powers it on again.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void PoweredOffApuIgnoresOrdinaryRegisterWrites(int mode)
    {
        var apu = new APU();
        apu.Init((GBCMode)mode);
        apu.Reset();

        apu.WriteByte(0x00, APUSchema.SOUND_ENABLED);
        apu.WriteByte(0xF3, APUSchema.SQUARE_1_VOLUME_ENVELOPE);

        Assert.Equal(0x70, apu.ReadByte(APUSchema.SOUND_ENABLED));
        Assert.Equal(0x00, apu.ReadByte(APUSchema.SQUARE_1_VOLUME_ENVELOPE));
    }

    /// <summary>
    /// Verifies powered-off length writes never restore square duty bits in either hardware mode.
    /// DMG applies only the hidden length value, while CGB ignores the entire write.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void PoweredOffLengthWritesDoNotRestoreDutyBits(int mode)
    {
        var apu = new APU();
        apu.Init((GBCMode)mode);
        apu.Reset();

        apu.WriteByte(0x00, APUSchema.SOUND_ENABLED);
        apu.WriteByte(0xFF, APUSchema.SQUARE_1_DUTY_LENGTH_LOAD);
        apu.WriteByte(0xFF, APUSchema.SQUARE_2_DUTY_LENGTH_LOAD);
        apu.WriteByte(0x80, APUSchema.SOUND_ENABLED);

        Assert.Equal(0x3F, apu.ReadByte(APUSchema.SQUARE_1_DUTY_LENGTH_LOAD));
        Assert.Equal(0x3F, apu.ReadByte(APUSchema.SQUARE_2_DUTY_LENGTH_LOAD));
    }

    /// <summary>
    /// Replays Blargg's adjacent two's-complement sweep boundaries and verifies retriggering uses the swept period.
    /// </summary>
    [Theory]
    [InlineData(0x5B0, true)]
    [InlineData(0x5B1, false)]
    public void SweepWriteBackSuppliesPeriodForRetrigger(int initialFrequency, bool expectedActive)
    {
        var apu = new APU();
        apu.WriteByte(0x80, APUSchema.SOUND_ENABLED);
        apu.WriteByte(0x08, APUSchema.SQUARE_1_VOLUME_ENVELOPE);
        apu.WriteByte(0x1C, APUSchema.SQUARE_1_SWEEP_PERIOD);
        WriteChannel1Frequency(apu, initialFrequency, trigger: true);

        // Frame sequencer step 2 is the first 128 Hz sweep clock after APU power-on.
        for (var step = 0; step < 3; step++)
        {
            apu.ClockFrameSequencer();
        }

        apu.WriteByte(0x01, APUSchema.SQUARE_1_SWEEP_PERIOD);
        apu.WriteByte((byte)(0xC0 | (initialFrequency >> 8)), APUSchema.SQUARE_1_FREQUENCY_MSB);

        Assert.Equal(expectedActive, (apu.ReadByte(APUSchema.SOUND_ENABLED) & 0x01) != 0);
    }

    /// <summary>
    /// Verifies channel 3 uses its two-clock timer multiplier and redirects active reads to the current wave byte.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ActiveWaveRamReadsFollowCurrentSampleByte(int mode)
    {
        var apu = CreateWaveApu((GBCMode)mode);

        apu.Update(10);
        Assert.Equal(0x12, apu.ReadByte(APUSchema.WAVE_TABLE_END - 1));

        apu.Update(4);
        Assert.Equal(0x34, apu.ReadByte(APUSchema.WAVE_TABLE_START));
    }

    /// <summary>
    /// Verifies channel 3's first fetch includes the fixed trigger pipeline delay measured by SameSuite.
    /// </summary>
    [Fact]
    public void WaveTriggerDelaysFirstSampleFetch()
    {
        var apu = CreateWaveApu(GBCMode.NoGBC);

        apu.Update(9);
        Assert.Equal(0xFF, apu.ReadByte(APUSchema.WAVE_TABLE_START));

        apu.Update(1);
        Assert.Equal(0x12, apu.ReadByte(APUSchema.WAVE_TABLE_START));
    }

    /// <summary>
    /// Verifies a channel 3 period write affects the reload after the current timer interval completes.
    /// </summary>
    [Fact]
    public void WaveFrequencyWriteAppliesAfterCurrentTimerInterval()
    {
        var apu = new APU();
        apu.Init(GBCMode.GBCSupport);
        apu.WriteByte(0x80, APUSchema.SOUND_ENABLED);
        apu.WriteByte(0x12, APUSchema.WAVE_TABLE_START);
        apu.WriteByte(0x34, APUSchema.WAVE_TABLE_START + 1);
        apu.WriteByte(0x80, APUSchema.WAVE_3_DAC);
        apu.WriteByte(0xFC, APUSchema.WAVE_3_FREQUENCY_LSB);
        apu.WriteByte(0x87, APUSchema.WAVE_3_FREQUENCY_MSB);

        apu.Update(14);
        apu.WriteByte(0xFE, APUSchema.WAVE_3_FREQUENCY_LSB);
        apu.Update(4);
        Assert.Equal(0x12, apu.ReadByte(APUSchema.WAVE_TABLE_START));

        apu.Update(4);
        Assert.Equal(0x34, apu.ReadByte(APUSchema.WAVE_TABLE_START));
    }

    /// <summary>
    /// Verifies DMG exposes active wave RAM for two clocks after a fetch while CGB keeps the current byte accessible.
    /// </summary>
    [Theory]
    [InlineData(0, 0xFF)]
    [InlineData(1, 0x12)]
    public void ActiveWaveRamReadWindowDependsOnHardwareMode(int mode, byte expectedAfterWindow)
    {
        var apu = CreateWaveApu((GBCMode)mode);

        apu.Update(10);
        apu.Update(1);
        Assert.Equal(0x12, apu.ReadByte(APUSchema.WAVE_TABLE_START));

        apu.Update(1);
        Assert.Equal(expectedAfterWindow, apu.ReadByte(APUSchema.WAVE_TABLE_START));
    }

    /// <summary>
    /// Verifies one subsystem update catches up every channel 3 sample period that elapsed.
    /// </summary>
    [Fact]
    public void WaveUpdateCatchesUpMultipleSamplePeriods()
    {
        var apu = CreateWaveApu(GBCMode.GBCSupport);

        apu.Update(18);

        Assert.Equal(0x34, apu.ReadByte(APUSchema.WAVE_TABLE_START));
    }

    /// <summary>
    /// Verifies a DMG retrigger closes the prior fetch window until the restarted channel fetches again.
    /// </summary>
    [Fact]
    public void DmgWaveRetriggerClosesPriorFetchWindow()
    {
        var apu = CreateWaveApu(GBCMode.NoGBC);
        apu.Update(10);
        Assert.Equal(0x12, apu.ReadByte(APUSchema.WAVE_TABLE_START));

        apu.WriteByte(0x87, APUSchema.WAVE_3_FREQUENCY_MSB);
        Assert.Equal(0xFF, apu.ReadByte(APUSchema.WAVE_TABLE_START));
    }

    /// <summary>
    /// Verifies a DMG retrigger immediately before fetching wave bytes 0-3 copies only the fetched byte into byte 0.
    /// </summary>
    [Fact]
    public void DmgWaveRetriggerCopiesLowQuarterByteIntoFirstByte()
    {
        var apu = CreateSequentialWaveApu(GBCMode.NoGBC);

        // Trigger two CPU clocks before byte 2's high nibble fetch, matching the final 2 MHz APU timer tick.
        apu.Update(6 + (2 * 2 * 4) - 2);
        apu.WriteByte(0x87, APUSchema.WAVE_3_FREQUENCY_MSB);
        apu.WriteByte(0x00, APUSchema.WAVE_3_DAC);

        Assert.Equal(0x22, apu.ReadByte(APUSchema.WAVE_TABLE_START));
        Assert.Equal(0x11, apu.ReadByte(APUSchema.WAVE_TABLE_START + 1));
        Assert.Equal(0x22, apu.ReadByte(APUSchema.WAVE_TABLE_START + 2));
        Assert.Equal(0x33, apu.ReadByte(APUSchema.WAVE_TABLE_START + 3));
    }

    /// <summary>
    /// Verifies a DMG retrigger immediately before fetching bytes 4-15 copies that byte's aligned block to bytes 0-3.
    /// </summary>
    [Theory]
    [InlineData(6, 4)]
    [InlineData(10, 8)]
    [InlineData(14, 12)]
    public void DmgWaveRetriggerCopiesFetchedAlignedBlock(int fetchedByte, int sourceBlock)
    {
        var apu = CreateSequentialWaveApu(GBCMode.NoGBC);

        apu.Update(6 + (fetchedByte * 2 * 4) - 2);
        apu.WriteByte(0x87, APUSchema.WAVE_3_FREQUENCY_MSB);
        apu.WriteByte(0x00, APUSchema.WAVE_3_DAC);

        for (var offset = 0; offset < 4; offset++)
        {
            Assert.Equal((byte)((sourceBlock + offset) * 0x11), apu.ReadByte(APUSchema.WAVE_TABLE_START + offset));
        }
    }

    /// <summary>
    /// Verifies a DMG retrigger outside the final pre-fetch APU tick does not corrupt wave RAM.
    /// </summary>
    [Fact]
    public void DmgWaveRetriggerOutsideFetchWindowLeavesWaveRamUnchanged()
    {
        var apu = CreateSequentialWaveApu(GBCMode.NoGBC);

        apu.Update(6 + (6 * 2 * 4));
        apu.WriteByte(0x87, APUSchema.WAVE_3_FREQUENCY_MSB);
        apu.WriteByte(0x00, APUSchema.WAVE_3_DAC);

        AssertSequentialWaveRam(apu);
    }

    /// <summary>
    /// Verifies CGB hardware does not apply the original DMG's pre-fetch retrigger corruption.
    /// </summary>
    [Fact]
    public void CgbWaveRetriggerDuringFetchLeavesWaveRamUnchanged()
    {
        var apu = CreateSequentialWaveApu(GBCMode.GBCSupport);

        apu.Update(6 + (6 * 2 * 4) - 2);
        apu.WriteByte(0x87, APUSchema.WAVE_3_FREQUENCY_MSB);
        apu.WriteByte(0x00, APUSchema.WAVE_3_DAC);

        AssertSequentialWaveRam(apu);
    }

    /// <summary>
    /// Verifies active CGB writes target the current wave byte regardless of the CPU-addressed wave-RAM byte.
    /// </summary>
    [Fact]
    public void CgbActiveWaveRamWriteTargetsCurrentByte()
    {
        var apu = CreateWaveApu(GBCMode.GBCSupport);
        apu.Update(10);

        apu.WriteByte(0xBC, APUSchema.WAVE_TABLE_END - 1);
        apu.WriteByte(0x00, APUSchema.WAVE_3_DAC);

        Assert.Equal(0xBC, apu.ReadByte(APUSchema.WAVE_TABLE_START));
        Assert.Equal(0x00, apu.ReadByte(APUSchema.WAVE_TABLE_END - 1));
    }

    /// <summary>
    /// Verifies active DMG writes target the current byte only during the two-clock wave-fetch window.
    /// </summary>
    [Fact]
    public void DmgActiveWaveRamWriteRequiresFetchWindow()
    {
        var apu = CreateWaveApu(GBCMode.NoGBC);
        apu.Update(11);
        apu.WriteByte(0xBC, APUSchema.WAVE_TABLE_END - 1);

        apu.Update(1);
        apu.WriteByte(0xDE, APUSchema.WAVE_TABLE_START + 1);
        apu.WriteByte(0x00, APUSchema.WAVE_3_DAC);

        Assert.Equal(0xBC, apu.ReadByte(APUSchema.WAVE_TABLE_START));
        Assert.Equal(0x34, apu.ReadByte(APUSchema.WAVE_TABLE_START + 1));
        Assert.Equal(0x00, apu.ReadByte(APUSchema.WAVE_TABLE_END - 1));
    }

    /// <summary>
    /// Verifies a DIV-induced frame-sequencer tick clocks an enabled length counter.
    /// </summary>
    [Fact]
    public void DivApuClockExpiresEnabledLengthCounter()
    {
        var apu = new APU();
        apu.Init(GBCMode.GBCSupport);
        apu.WriteByte(0x80, APUSchema.SOUND_ENABLED);
        apu.WriteByte(0x3F, APUSchema.SQUARE_1_DUTY_LENGTH_LOAD);
        apu.WriteByte(0xF0, APUSchema.SQUARE_1_VOLUME_ENVELOPE);
        apu.WriteByte(0xC0, APUSchema.SQUARE_1_FREQUENCY_MSB);
        Assert.Equal(0x01, apu.ReadByte(APUSchema.SOUND_ENABLED) & 0x01);

        apu.ClockFrameSequencer();

        Assert.Equal(0x00, apu.ReadByte(APUSchema.SOUND_ENABLED) & 0x01);
    }

    /// <summary>
    /// Verifies CGB PCM12 exposes channel 1 in the low nibble and channel 2 in the high nibble.
    /// </summary>
    [Fact]
    public void CgbPcm12PacksPulseChannelDigitalOutputs()
    {
        var apu = new APU();
        apu.Init(GBCMode.GBCSupport);
        apu.WriteByte(0x80, APUSchema.SOUND_ENABLED);
        apu.WriteByte(0x40, APUSchema.SQUARE_1_DUTY_LENGTH_LOAD);
        apu.WriteByte(0xF0, APUSchema.SQUARE_1_VOLUME_ENVELOPE);
        WriteChannel1Frequency(apu, 0, trigger: true);

        apu.WriteByte(0x00, APUSchema.PCM_12);

        Assert.Equal(0x0F, apu.ReadByte(APUSchema.PCM_12));
    }

    /// <summary>
    /// Verifies CGB PCM34 exposes channel 3 in the low nibble and channel 4 in the high nibble.
    /// </summary>
    [Fact]
    public void CgbPcm34PacksWaveAndNoiseDigitalOutputs()
    {
        var apu = new APU();
        apu.Init(GBCMode.GBCSupport);
        apu.WriteByte(0x80, APUSchema.SOUND_ENABLED);
        apu.WriteByte(0xAA, APUSchema.WAVE_TABLE_START);
        apu.WriteByte(0x80, APUSchema.WAVE_3_DAC);
        apu.WriteByte(0x20, APUSchema.WAVE_3_VOLUME);
        apu.WriteByte(0xFF, APUSchema.WAVE_3_FREQUENCY_LSB);
        apu.WriteByte(0x87, APUSchema.WAVE_3_FREQUENCY_MSB);

        apu.Update(8);

        Assert.Equal(0x0A, apu.ReadByte(APUSchema.PCM_34));
    }

    /// <summary>
    /// Verifies inactive channels contribute zero to the CGB PCM amplitude registers.
    /// </summary>
    [Fact]
    public void CgbPcmRegistersZeroInactiveChannels()
    {
        var apu = new APU();
        apu.Init(GBCMode.GBCSupport);
        apu.WriteByte(0x80, APUSchema.SOUND_ENABLED);

        Assert.Equal(0x00, apu.ReadByte(APUSchema.PCM_12));
        Assert.Equal(0x00, apu.ReadByte(APUSchema.PCM_34));
    }

    /// <summary>
    /// Powers on the APU and verifies complete read values so writable fields are not hidden by the unused-bit masks.
    /// </summary>
    [Fact]
    public void PoweredRegistersPreserveWritableBitsAndReadHighMasks()
    {
        var apu = new APU();
        apu.WriteByte(0x80, APUSchema.SOUND_ENABLED);

        AssertFullRead(apu, APUSchema.SQUARE_1_SWEEP_PERIOD, 0x00, 0x80);
        AssertFullRead(apu, APUSchema.WAVE_3_DAC, 0x00, 0x7F);
        AssertFullRead(apu, APUSchema.WAVE_3_VOLUME, 0x00, 0x9F);
        AssertFullRead(apu, APUSchema.NOISE_4_LENGTH_LOAD, 0x00, 0xFF);
        AssertFullRead(apu, APUSchema.NOISE_4_TRIGGER, 0x00, 0xBF);
        AssertFullRead(apu, APUSchema.SOUND_ENABLED, 0x80, 0xF0);
    }

    private static APU CreateWaveApu(GBCMode mode)
    {
        var apu = new APU();
        apu.Init(mode);
        apu.WriteByte(0x80, APUSchema.SOUND_ENABLED);
        apu.WriteByte(0x12, APUSchema.WAVE_TABLE_START);
        apu.WriteByte(0x34, APUSchema.WAVE_TABLE_START + 1);
        apu.WriteByte(0x80, APUSchema.WAVE_3_DAC);
        apu.WriteByte(0xFE, APUSchema.WAVE_3_FREQUENCY_LSB);
        apu.WriteByte(0x87, APUSchema.WAVE_3_FREQUENCY_MSB);
        return apu;
    }

    private static APU CreateSequentialWaveApu(GBCMode mode)
    {
        var apu = new APU();
        apu.Init(mode);
        apu.WriteByte(0x80, APUSchema.SOUND_ENABLED);

        for (var index = 0; index < 16; index++)
        {
            apu.WriteByte((byte)(index * 0x11), APUSchema.WAVE_TABLE_START + index);
        }

        apu.WriteByte(0x80, APUSchema.WAVE_3_DAC);
        apu.WriteByte(0xFE, APUSchema.WAVE_3_FREQUENCY_LSB);
        apu.WriteByte(0x87, APUSchema.WAVE_3_FREQUENCY_MSB);
        return apu;
    }

    private static void AssertSequentialWaveRam(APU apu)
    {
        for (var index = 0; index < 16; index++)
        {
            Assert.Equal((byte)(index * 0x11), apu.ReadByte(APUSchema.WAVE_TABLE_START + index));
        }
    }

    private static void WriteChannel1Frequency(APU apu, int frequency, bool trigger)
    {
        apu.WriteByte((byte)frequency, APUSchema.SQUARE_1_FREQUENCY_LSB);
        apu.WriteByte((byte)((frequency >> 8) | (trigger ? 0x80 : 0)), APUSchema.SQUARE_1_FREQUENCY_MSB);
    }

    private static void AssertFullRead(APU apu, int address, byte value, byte expected)
    {
        apu.WriteByte(value, address);
        Assert.Equal(expected, apu.ReadByte(address));
    }

    private static void AssertMaskedRead(APU apu, int address, byte mask, byte value, byte expected)
    {
        apu.WriteByte(value, address);
        Assert.Equal(expected, (byte)(apu.ReadByte(address) & mask));
    }
}
