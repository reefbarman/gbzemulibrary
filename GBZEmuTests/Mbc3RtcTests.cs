using System.Buffers.Binary;
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
    /// Verifies the BGB-compatible trailer stores live registers, the latched snapshot, and a 64-bit UTC timestamp.
    /// </summary>
    [Fact]
    public void PersistenceUsesBgbCompatible48ByteLayout()
    {
        var rtc = new MBC3RTC();
        rtc.Write(MBC3RTC.SecondsRegister, 12);
        rtc.Write(MBC3RTC.MinutesRegister, 34);
        rtc.Latch();
        rtc.Write(MBC3RTC.SecondsRegister, 56);

        var data = rtc.Save(1_234_567_890);

        Assert.Equal(MBC3RTC.PersistenceSize, data.Length);
        Assert.Equal(56, BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0, 4)));
        Assert.Equal(34, BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4, 4)));
        Assert.Equal(12, BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(20, 4)));
        Assert.Equal(34, BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(24, 4)));
        Assert.Equal(1_234_567_890, BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(40, 8)));
    }

    /// <summary>
    /// Verifies loading preserves the saved latch while elapsed wall time advances only the live clock.
    /// </summary>
    [Fact]
    public void LoadingAdvancesLiveClockWithoutChangingSavedLatch()
    {
        var source = new MBC3RTC();
        source.Write(MBC3RTC.SecondsRegister, 10);
        source.Write(MBC3RTC.MinutesRegister, 1);
        source.Latch();
        var data = source.Save(1_000);

        var restored = new MBC3RTC();
        Assert.True(restored.Load(data, 1_090));
        Assert.Equal(10, restored.Read(MBC3RTC.SecondsRegister));
        Assert.Equal(1, restored.Read(MBC3RTC.MinutesRegister));

        restored.Latch();
        Assert.Equal(40, restored.Read(MBC3RTC.SecondsRegister));
        Assert.Equal(2, restored.Read(MBC3RTC.MinutesRegister));
    }

    /// <summary>
    /// Verifies legacy 44-byte timestamps load, halted clocks ignore elapsed time, and future timestamps do not rewind.
    /// </summary>
    [Fact]
    public void LoadingHandlesLegacyHaltedAndFutureTimestamps()
    {
        var source = new MBC3RTC();
        source.Write(MBC3RTC.SecondsRegister, 5);
        source.Latch();
        var legacyData = source.Save(2_000)[..44];

        var legacy = new MBC3RTC();
        Assert.True(legacy.Load(legacyData, 2_060));
        legacy.Latch();
        Assert.Equal(5, legacy.Read(MBC3RTC.SecondsRegister));
        Assert.Equal(1, legacy.Read(MBC3RTC.MinutesRegister));

        source.Write(MBC3RTC.DaysHighRegister, 0x40);
        source.Latch();
        var haltedData = source.Save(3_000);
        var halted = new MBC3RTC();
        Assert.True(halted.Load(haltedData, 30_000));
        halted.Latch();
        Assert.Equal(5, halted.Read(MBC3RTC.SecondsRegister));

        var future = new MBC3RTC();
        Assert.True(future.Load(source.Save(10_000), 9_000));
        future.Latch();
        Assert.Equal(5, future.Read(MBC3RTC.SecondsRegister));
    }

    /// <summary>
    /// Verifies persisted carry remains sticky and legacy timestamps above signed-32-bit range remain unsigned.
    /// </summary>
    [Fact]
    public void LoadingPreservesCarryAndUnsignedLegacyTimestamp()
    {
        var source = new MBC3RTC();
        source.Write(MBC3RTC.DaysHighRegister, 0x80);
        source.Latch();
        var legacyData = source.Save(3_000_000_000)[..44];

        var restored = new MBC3RTC();
        Assert.True(restored.Load(legacyData, 3_000_000_060));
        restored.Latch();

        Assert.Equal(0x80, restored.Read(MBC3RTC.DaysHighRegister));
        Assert.Equal(1, restored.Read(MBC3RTC.MinutesRegister));
    }

    /// <summary>
    /// Verifies loaded out-of-range values normalize without false carry and negative timestamps do not advance.
    /// </summary>
    [Fact]
    public void LoadingNormalizesOutOfRangeValuesAndIgnoresNegativeTimestamp()
    {
        var source = new MBC3RTC();
        source.Write(MBC3RTC.SecondsRegister, 59);
        source.Write(MBC3RTC.MinutesRegister, 0x3F);
        var data = source.Save(-1);

        var noCatchUp = new MBC3RTC();
        Assert.True(noCatchUp.Load(data, 100));
        noCatchUp.Latch();
        Assert.Equal(59, noCatchUp.Read(MBC3RTC.SecondsRegister));
        Assert.Equal(0x3F, noCatchUp.Read(MBC3RTC.MinutesRegister));

        var elapsedData = source.Save(1_000);
        var normalized = new MBC3RTC();
        Assert.True(normalized.Load(elapsedData, 1_001));
        normalized.Latch();
        Assert.Equal(0, normalized.Read(MBC3RTC.SecondsRegister));
        Assert.Equal(0, normalized.Read(MBC3RTC.MinutesRegister));
        Assert.Equal(0, normalized.Read(MBC3RTC.HoursRegister));
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
