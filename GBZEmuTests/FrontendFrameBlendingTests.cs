using GBZEmuFrontend;
using GBZEmuLibrary;
using Raylib_cs;

namespace GBZEmuTests;

/// <summary>
/// Verifies frontend LCD persistence without changing the raw core framebuffer contract.
/// </summary>
public sealed class FrontendFrameBlendingTests
{
    /// <summary>
    /// Verifies exact Off, Subtle, and Classic persistence weights and away-from-zero midpoint rounding.
    /// </summary>
    [Theory]
    [InlineData("off", 2, 1, 3)]
    [InlineData("subtle", 2, 1, 2)]
    [InlineData("classic", 1, 1, 2)]
    public void PersistenceModesProduceExpectedOutput(
        string persistenceId,
        byte expectedRed,
        byte expectedGreen,
        byte expectedBlue)
    {
        var blender = new FrameBlender();
        var destination = CreateDestination();
        blender.Process(CreateFrame(0, 0, 0), destination, "off");

        blender.Process(CreateFrame(2, 1, 3), destination, persistenceId);

        Assert.Equal((expectedRed, expectedGreen, expectedBlue), GetRGB(destination[0]));
    }

    /// <summary>
    /// Verifies that Off bypasses blending while retaining the latest raw frame for later persistence.
    /// </summary>
    [Fact]
    public void OffPassesThroughAndUpdatesHistory()
    {
        var blender = new FrameBlender();
        var destination = CreateDestination();

        blender.Process(CreateFrame(20, 40, 60), destination, "off");
        blender.Process(CreateFrame(100, 120, 140), destination, "off");
        Assert.Equal((100, 120, 140), GetRGB(destination[0]));

        blender.Process(CreateFrame(140, 160, 180), destination, "classic");
        Assert.Equal((120, 140, 160), GetRGB(destination[0]));
    }

    /// <summary>
    /// Verifies that reset makes the next frame seed history and present directly at every persistence strength.
    /// </summary>
    [Theory]
    [InlineData("subtle")]
    [InlineData("classic")]
    public void ResetSeedsTheNextFrameDirectly(string persistenceId)
    {
        var blender = new FrameBlender();
        var destination = CreateDestination();
        blender.Process(CreateFrame(0, 0, 0), destination, persistenceId);

        blender.Reset();
        blender.Process(CreateFrame(200, 180, 160), destination, persistenceId);

        Assert.Equal((200, 180, 160), GetRGB(destination[0]));
    }

    /// <summary>
    /// Verifies Modern Balanced correction applies the CGB curve and green-blue LCD mixing.
    /// </summary>
    [Theory]
    [InlineData(0, 0, 255, 0, 107, 255)]
    [InlineData(0, 255, 0, 0, 213, 0)]
    [InlineData(82, 165, 123, 88, 190, 149)]
    [InlineData(255, 255, 255, 255, 255, 255)]
    public void ModernBalancedCorrectsCgbColors(
        byte red,
        byte green,
        byte blue,
        byte expectedRed,
        byte expectedGreen,
        byte expectedBlue)
    {
        var blender = new FrameBlender();
        var destination = CreateDestination();

        blender.Process(CreateFrame(red, green, blue), destination, "off", correctCgbColors: true);

        Assert.Equal((expectedRed, expectedGreen, expectedBlue), GetRGB(destination[0]));
    }

    /// <summary>
    /// Verifies nonlinear CGB correction is applied independently before adjacent corrected frames are blended.
    /// </summary>
    [Fact]
    public void CgbCorrectionPrecedesFrameBlending()
    {
        var blender = new FrameBlender();
        var destination = CreateDestination();

        blender.Process(CreateFrame(0, 0, 0), destination, "classic", correctCgbColors: true);
        blender.Process(CreateFrame(0, 0, 255), destination, "classic", correctCgbColors: true);

        Assert.Equal((0, 54, 128), GetRGB(destination[0]));
    }

    /// <summary>
    /// Verifies corrected presentation retains raw history across a correction toggle without double correction.
    /// </summary>
    [Fact]
    public void RawHistorySurvivesCgbCorrectionToggleWithoutDoubleCorrection()
    {
        var blender = new FrameBlender();
        var destination = CreateDestination();

        blender.Process(CreateFrame(0, 0, 255), destination, "off", correctCgbColors: true);
        Assert.Equal((0, 107, 255), GetRGB(destination[0]));

        blender.Process(CreateFrame(0, 0, 0), destination, "classic", correctCgbColors: true);
        Assert.Equal((0, 54, 128), GetRGB(destination[0]));

        blender.Process(CreateFrame(0, 0, 255), destination, "classic", correctCgbColors: false);
        Assert.Equal((0, 0, 128), GetRGB(destination[0]));
    }

    /// <summary>
    /// Verifies that the destination must hold one complete display frame.
    /// </summary>
    [Fact]
    public void ProcessRejectsUndersizedDestination()
    {
        var blender = new FrameBlender();
        var destination = new Raylib_cs.Color[(Display.HORIZONTAL_RESOLUTION * Display.VERTICAL_RESOLUTION) - 1];

        var exception = Assert.Throws<ArgumentException>(
            () => blender.Process(CreateFrame(0, 0, 0), destination, "off"));

        Assert.Equal("destination", exception.ParamName);
    }

    /// <summary>
    /// Verifies that unknown persistence identifiers cannot silently select an unintended weight.
    /// </summary>
    [Fact]
    public void ProcessRejectsUnknownPersistenceId()
    {
        var blender = new FrameBlender();

        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => blender.Process(CreateFrame(0, 0, 0), CreateDestination(), "medium"));

        Assert.Equal("persistenceId", exception.ParamName);
    }

    /// <summary>
    /// Verifies CGB correction is limited to native CGB presentation and can be bypassed explicitly.
    /// </summary>
    [Theory]
    [InlineData(HardwareModel.CgbE, CartridgeCompatibility.CgbCompatible, false, true)]
    [InlineData(HardwareModel.CgbE, CartridgeCompatibility.CgbOnly, false, true)]
    [InlineData(HardwareModel.DmgB, CartridgeCompatibility.CgbCompatible, false, false)]
    [InlineData(HardwareModel.Mgb, CartridgeCompatibility.CgbCompatible, false, false)]
    [InlineData(HardwareModel.CgbE, CartridgeCompatibility.DmgOnly, false, false)]
    [InlineData(HardwareModel.CgbE, CartridgeCompatibility.CgbCompatible, true, false)]
    [InlineData(HardwareModel.AgbA, CartridgeCompatibility.DmgOnly, false, false)]
    [InlineData(HardwareModel.AgbA, CartridgeCompatibility.CgbCompatible, false, false)]
    [InlineData(HardwareModel.AgbA, CartridgeCompatibility.CgbOnly, false, false)]
    public void CgbCorrectionRequiresNativeCgbPresentation(
        HardwareModel hardwareModel,
        CartridgeCompatibility compatibility,
        bool rawColors,
        bool expected)
    {
        Assert.Equal(expected, Frontend.ShouldCorrectCgbColors(hardwareModel, compatibility, rawColors));
    }

    /// <summary>
    /// Verifies that MGB uses the retained DMG analog approximation until a measured Pocket coefficient is adopted.
    /// </summary>
    [Theory]
    [InlineData(HardwareModel.DmgB, false)]
    [InlineData(HardwareModel.Mgb, false)]
    [InlineData(HardwareModel.CgbE, true)]
    [InlineData(HardwareModel.Sgb2, false)]
    [InlineData(HardwareModel.AgbA, true)]
    public void AudioFilterPolicyIsExplicitForImplementedModels(HardwareModel model, bool expectedCgbFilter)
    {
        Assert.Equal(expectedCgbFilter, Frontend.ShouldUseCgbAudioFilter(model));
    }

    /// <summary>
    /// Verifies that the frontend exposes independent raw-frame and raw-color modes while both effects remain enabled by default.
    /// </summary>
    [Fact]
    public void RawPresentationOptionsAreOptIn()
    {
        using var rom = TestRom.Create(0x00);

        var presented = FrontendOptions.Parse([rom.Path]);
        var raw = FrontendOptions.Parse([rom.Path, "--raw-frames", "--raw-colors"]);

        Assert.False(presented.RawFrames);
        Assert.False(presented.RawColors);
        Assert.True(raw.RawFrames);
        Assert.True(raw.RawColors);
    }

    private static GBZEmuLibrary.Color[,] CreateFrame(byte red, byte green, byte blue)
    {
        var frame = new GBZEmuLibrary.Color[Display.HORIZONTAL_RESOLUTION, Display.VERTICAL_RESOLUTION];
        for (var y = 0; y < Display.VERTICAL_RESOLUTION; y++)
        {
            for (var x = 0; x < Display.HORIZONTAL_RESOLUTION; x++)
            {
                frame[x, y] = new GBZEmuLibrary.Color(red, green, blue);
            }
        }

        return frame;
    }

    private static Raylib_cs.Color[] CreateDestination()
    {
        return new Raylib_cs.Color[Display.HORIZONTAL_RESOLUTION * Display.VERTICAL_RESOLUTION];
    }

    private static (byte R, byte G, byte B) GetRGB(Raylib_cs.Color color)
    {
        return (color.R, color.G, color.B);
    }
}
