using GBZEmuLibrary;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using EmulatorColor = GBZEmuLibrary.Color;

namespace GBZEmuTests;

/// <summary>
/// Verifies hardware-specific framebuffer normalization used by image-based ROM conformance tests.
/// </summary>
public sealed class FramebufferComparerTests
{
    /// <summary>
    /// Verifies DMG references use the palette-mapped shade while preserving the raw color ID for PPU priority.
    /// </summary>
    [Fact]
    public void DmgComparisonUsesPaletteMappedShade()
    {
        var framebuffer = new EmulatorColor[Display.HORIZONTAL_RESOLUTION, Display.VERTICAL_RESOLUTION];
        using var reference = new Image<Rgba32>(Display.HORIZONTAL_RESOLUTION, Display.VERTICAL_RESOLUTION);
        for (var y = 0; y < Display.VERTICAL_RESOLUTION; y++)
        {
            for (var x = 0; x < Display.HORIZONTAL_RESOLUTION; x++)
            {
                framebuffer[x, y] = new EmulatorColor(255, 255, 255)
                {
                    Index = 0,
                    SgbIndex = 0
                };
                reference[x, y] = new Rgba32(255, 255, 255);
            }
        }

        framebuffer[79, 64] = new EmulatorColor(0, 0, 0)
        {
            Index = 1,
            SgbIndex = 3
        };
        reference[79, 64] = new Rgba32(0, 0, 0);
        var referencePath = Path.Combine(Path.GetTempPath(), $"gbzemu-framebuffer-{Guid.NewGuid():N}.png");
        try
        {
            reference.SaveAsPng(referencePath);

            Assert.Null(FramebufferComparer.Compare(framebuffer, referencePath, HardwareMode.Dmg));
        }
        finally
        {
            File.Delete(referencePath);
        }
    }
}
