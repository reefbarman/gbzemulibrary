using GBZEmuLibrary;

namespace GBZEmuTests;

/// <summary>
/// Verifies MBC3 RTC timing, latching, overflow, halt, and register-width behavior independently of cartridge routing.
/// </summary>
public sealed class Mbc3RtcTests
{
    /// <summary>
    /// Verifies one-second advancement and confirms reads remain on the previous snapshot until another latch.
    /// </summary>
    [Fact]
    public void ClockAdvancesLiveStateButReadsOnlyChangeAfterLatch()
    {
        var rtc = new MBC3RTC();
        rtc.Write(MBC3RTC.SecondsRegister, 0);
        rtc.Latch();

        rtc.Update(GameBoySchema.MAX_DMG_CLOCK_CYCLES);

        Assert.Equal(0, rtc.Read(MBC3RTC.SecondsRegister));
        rtc.Latch();
        Assert.Equal(1, rtc.Read(MBC3RTC.SecondsRegister));
    }

    /// <summary>
    /// Verifies the full seconds-to-day cascade and 9-bit day overflow carry behavior.
    /// </summary>
    [Fact]
    public void RegisterOverflowCascadesAndSetsDayCarry()
    {
        var rtc = new MBC3RTC();
        rtc.Write(MBC3RTC.SecondsRegister, 59);
        rtc.Write(MBC3RTC.MinutesRegister, 59);
        rtc.Write(MBC3RTC.HoursRegister, 23);
        rtc.Write(MBC3RTC.DaysLowRegister, 0xFF);
        rtc.Write(MBC3RTC.DaysHighRegister, 0x01);

        rtc.Update(GameBoySchema.MAX_DMG_CLOCK_CYCLES);
        rtc.Latch();

        Assert.Equal(0, rtc.Read(MBC3RTC.SecondsRegister));
        Assert.Equal(0, rtc.Read(MBC3RTC.MinutesRegister));
        Assert.Equal(0, rtc.Read(MBC3RTC.HoursRegister));
        Assert.Equal(0, rtc.Read(MBC3RTC.DaysLowRegister));
        Assert.Equal(0x80, rtc.Read(MBC3RTC.DaysHighRegister));
    }

    /// <summary>
    /// Verifies halting preserves both register values and the accumulated fractional second until the clock resumes.
    /// </summary>
    [Fact]
    public void HaltPreservesSubSecondPhase()
    {
        var rtc = new MBC3RTC();
        rtc.Update(GameBoySchema.MAX_DMG_CLOCK_CYCLES * 9 / 10);
        rtc.Write(MBC3RTC.DaysHighRegister, 0x40);
        rtc.Update(GameBoySchema.MAX_DMG_CLOCK_CYCLES);
        rtc.Write(MBC3RTC.DaysHighRegister, 0x00);
        rtc.Update(GameBoySchema.MAX_DMG_CLOCK_CYCLES / 5);
        rtc.Latch();

        Assert.Equal(1, rtc.Read(MBC3RTC.SecondsRegister));
    }

    /// <summary>
    /// Verifies register masks, non-seconds writes preserving phase, and seconds writes resetting the divider phase.
    /// </summary>
    [Fact]
    public void WritesApplyRegisterMasksAndSecondsResetPhase()
    {
        var rtc = new MBC3RTC();
        rtc.Update(GameBoySchema.MAX_DMG_CLOCK_CYCLES / 2);
        rtc.Write(MBC3RTC.SecondsRegister, 0xFF);
        rtc.Write(MBC3RTC.MinutesRegister, 0xFF);
        rtc.Write(MBC3RTC.HoursRegister, 0xFF);
        rtc.Write(MBC3RTC.DaysHighRegister, 0xFF);
        rtc.Latch();

        Assert.Equal(0x3F, rtc.Read(MBC3RTC.SecondsRegister));
        Assert.Equal(0x3F, rtc.Read(MBC3RTC.MinutesRegister));
        Assert.Equal(0x1F, rtc.Read(MBC3RTC.HoursRegister));
        Assert.Equal(0xC1, rtc.Read(MBC3RTC.DaysHighRegister));

        rtc.Write(MBC3RTC.DaysHighRegister, 0x00);
        rtc.Update(GameBoySchema.MAX_DMG_CLOCK_CYCLES / 2);
        rtc.Write(MBC3RTC.MinutesRegister, 0);
        rtc.Update(GameBoySchema.MAX_DMG_CLOCK_CYCLES / 2);
        rtc.Latch();
        Assert.Equal(0, rtc.Read(MBC3RTC.SecondsRegister));

        rtc.Write(MBC3RTC.SecondsRegister, 0);
        rtc.Update(GameBoySchema.MAX_DMG_CLOCK_CYCLES / 2);
        rtc.Latch();
        Assert.Equal(0, rtc.Read(MBC3RTC.SecondsRegister));
    }

    /// <summary>
    /// Verifies masked out-of-range minute values wrap locally without incorrectly carrying into hours.
    /// </summary>
    [Fact]
    public void OutOfRangeMinuteIncrementDoesNotCarryToHours()
    {
        var rtc = new MBC3RTC();
        rtc.Write(MBC3RTC.SecondsRegister, 59);
        rtc.Write(MBC3RTC.MinutesRegister, 0x3F);
        rtc.Write(MBC3RTC.HoursRegister, 0);

        rtc.Update(GameBoySchema.MAX_DMG_CLOCK_CYCLES);
        rtc.Latch();

        Assert.Equal(0, rtc.Read(MBC3RTC.SecondsRegister));
        Assert.Equal(0, rtc.Read(MBC3RTC.MinutesRegister));
        Assert.Equal(0, rtc.Read(MBC3RTC.HoursRegister));
    }
}
