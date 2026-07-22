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
    /// Verifies that the first frame is shown directly and the next frame averages adjacent raw frames.
    /// </summary>
    [Fact]
    public void BlenderCombinesAdjacentFramesAfterFirstFrame()
    {
        var blender = new FrameBlender();
        var destination = CreateDestination();
        var first = CreateFrame(16, 32, 48);
        var second = CreateFrame(48, 65, 82);

        blender.Process(first, destination, true);
        Assert.Equal((16, 32, 48), GetRGB(destination[0]));

        blender.Process(second, destination, true);
        Assert.Equal((32, 49, 65), GetRGB(destination[0]));
    }

    /// <summary>
    /// Verifies that raw mode bypasses blending while retaining the latest raw frame for a later blended frame.
    /// </summary>
    [Fact]
    public void RawModePassesThroughAndUpdatesHistory()
    {
        var blender = new FrameBlender();
        var destination = CreateDestination();

        blender.Process(CreateFrame(20, 40, 60), destination, false);
        blender.Process(CreateFrame(100, 120, 140), destination, false);
        Assert.Equal((100, 120, 140), GetRGB(destination[0]));

        blender.Process(CreateFrame(140, 160, 180), destination, true);
        Assert.Equal((120, 140, 160), GetRGB(destination[0]));
    }

    /// <summary>
    /// Verifies that reset prevents pixels from a previous ROM or presentation run entering the next frame.
    /// </summary>
    [Fact]
    public void ResetClearsPreviousFrameHistory()
    {
        var blender = new FrameBlender();
        var destination = CreateDestination();
        blender.Process(CreateFrame(0, 0, 0), destination, true);

        blender.Reset();
        blender.Process(CreateFrame(200, 180, 160), destination, true);

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

        blender.Process(CreateFrame(red, green, blue), destination, blend: false, correctCgbColors: true);

        Assert.Equal((expectedRed, expectedGreen, expectedBlue), GetRGB(destination[0]));
    }

    /// <summary>
    /// Verifies nonlinear CGB correction occurs before adjacent corrected frames are blended.
    /// </summary>
    [Fact]
    public void CgbCorrectionPrecedesFrameBlending()
    {
        var blender = new FrameBlender();
        var destination = CreateDestination();

        blender.Process(CreateFrame(0, 0, 0), destination, blend: true, correctCgbColors: true);
        blender.Process(CreateFrame(0, 0, 255), destination, blend: true, correctCgbColors: true);

        Assert.Equal((0, 54, 128), GetRGB(destination[0]));
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
