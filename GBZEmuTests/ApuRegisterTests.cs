using GBZEmuLibrary;

namespace GBZEmuTests;

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
    [Fact]
    public void PoweredOffApuIgnoresOrdinaryRegisterWrites()
    {
        var apu = new APU();
        apu.Reset();

        apu.WriteByte(0x00, APUSchema.SOUND_ENABLED);
        apu.WriteByte(0xF3, APUSchema.SQUARE_1_VOLUME_ENVELOPE);

        Assert.Equal(0x70, apu.ReadByte(APUSchema.SOUND_ENABLED));
        Assert.Equal(0x00, apu.ReadByte(APUSchema.SQUARE_1_VOLUME_ENVELOPE));
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
