using GBZEmuLibrary;

namespace GBZEmuTests;

/// <summary>
/// Verifies evidence-backed model-specific startup state without enabling unfinished models in public metadata.
/// </summary>
public sealed class HardwareStartupProfileTests
{
    [Theory]
    [InlineData(0x80, 1)]
    [InlineData(0xC0, 2)]
    [InlineData(0xFF, 1)]
    public void AgbNativeColorProfileUsesDocumentedHandoff(int cgbFlag, int expectedMode)
    {
        var profile = HardwareStartupProfile.ResolveAgbA(CreateHeader((byte)cgbFlag));

        Assert.Equal(HardwareModel.AgbA, profile.HardwareModel);
        Assert.Equal((GBCMode)expectedMode, profile.ExecutionMode);
        Assert.Equal(0x1100, profile.AF);
        Assert.Equal(0x0100, profile.BC);
        Assert.Equal(0xFF56, profile.DE);
        Assert.Equal(0x000D, profile.HL);
        Assert.Equal(0xFFFE, profile.SP);
        Assert.Equal(0x0100, profile.PC);
        Assert.Equal(cgbFlag, profile.Key0);
        Assert.Equal(0x00, profile.ObjectPriority);
        Assert.False(profile.InstallCompatibilityPalettes);
    }

    [Theory]
    [InlineData(0x00, 0x01, 0x00, 0x007C)]
    [InlineData(0x0F, 0x10, 0x20, 0x007C)]
    [InlineData(0x43, 0x44, 0x00, 0x991A)]
    [InlineData(0x58, 0x59, 0x00, 0x991A)]
    [InlineData(0xFF, 0x00, 0xA0, 0x007C)]
    public void AgbDmgProfileAppliesLicensedTitleSumAndIncrementFlags(
        int titleChecksum,
        int expectedB,
        int expectedFlags,
        int expectedHl)
    {
        var profile = HardwareStartupProfile.ResolveAgbA(CreateHeader(0x00, (byte)titleChecksum, oldLicensee: 0x01));

        Assert.Equal(GBCMode.GBCCompatibility, profile.ExecutionMode);
        Assert.Equal((ushort)(0x1100 | expectedFlags), profile.AF);
        Assert.Equal((ushort)(expectedB << 8), profile.BC);
        Assert.Equal(0x0008, profile.DE);
        Assert.Equal(expectedHl, profile.HL);
        Assert.Equal(0x04, profile.Key0);
        Assert.Equal(0x01, profile.ObjectPriority);
        Assert.True(profile.InstallCompatibilityPalettes);
    }

    [Fact]
    public void AgbDmgProfileAcceptsNewNintendoLicenseCode()
    {
        var profile = HardwareStartupProfile.ResolveAgbA(CreateHeader(
            0x00,
            titleChecksum: 0x43,
            oldLicensee: 0x33,
            newLicensee: "01"));

        Assert.Equal(0x4400, profile.BC);
        Assert.Equal(0x991A, profile.HL);
    }

    [Theory]
    [InlineData(0x02, null)]
    [InlineData(0x33, "00")]
    public void AgbDmgProfileIgnoresTitleSumForOtherLicensees(int oldLicensee, string? newLicensee)
    {
        var profile = HardwareStartupProfile.ResolveAgbA(CreateHeader(
            0x00,
            titleChecksum: 0xFF,
            oldLicensee: (byte)oldLicensee,
            newLicensee: newLicensee));

        Assert.Equal(0x1100, profile.AF);
        Assert.Equal(0x0100, profile.BC);
        Assert.Equal(0x007C, profile.HL);
    }

    private static CartridgeHeader CreateHeader(
        byte cgbFlag,
        byte titleChecksum = 0,
        byte oldLicensee = 0,
        string? newLicensee = null)
    {
        var rom = new byte[0x8000];
        rom[0x143] = cgbFlag;
        rom[0x147] = 0x00;
        rom[0x148] = 0x00;
        rom[0x149] = 0x00;
        rom[0x14B] = oldLicensee;
        rom[0x134] = titleChecksum;
        if (newLicensee != null)
        {
            rom[0x144] = (byte)newLicensee[0];
            rom[0x145] = (byte)newLicensee[1];
        }

        return new CartridgeHeader(rom);
    }
}
