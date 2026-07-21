using GBZEmuLibrary;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using EmulatorColor = GBZEmuLibrary.Color;

namespace GBZEmuTests;

/// <summary>
/// Compares completed emulator framebuffers with hardware-specific reference images.
/// </summary>
internal static class FramebufferComparer
{
    /// <summary>
    /// Returns a bounded mismatch description, or <see langword="null"/> when every normalized pixel matches.
    /// </summary>
    public static string? Compare(EmulatorColor[,] actual, string referencePath, HardwareMode hardware)
    {
        using var reference = Image.Load<Rgba32>(referencePath);
        if (reference.Width != Display.HORIZONTAL_RESOLUTION || reference.Height != Display.VERTICAL_RESOLUTION)
        {
            return $"Reference dimensions are {reference.Width}x{reference.Height}, expected {Display.HORIZONTAL_RESOLUTION}x{Display.VERTICAL_RESOLUTION}.";
        }

        var differentPixels = 0;
        var firstDifference = string.Empty;

        for (var y = 0; y < Display.VERTICAL_RESOLUTION; y++)
        {
            for (var x = 0; x < Display.HORIZONTAL_RESOLUTION; x++)
            {
                var expected = reference[x, y];
                var pixel = Normalize(actual[x, y], hardware);
                if (pixel.R == expected.R && pixel.G == expected.G && pixel.B == expected.B)
                {
                    continue;
                }

                differentPixels++;
                if (firstDifference.Length == 0)
                {
                    firstDifference = $"first difference at ({x}, {y}): expected #{expected.R:X2}{expected.G:X2}{expected.B:X2}, actual #{pixel.R:X2}{pixel.G:X2}{pixel.B:X2}";
                }
            }
        }

        return differentPixels == 0 ? null : $"{differentPixels} pixels differ; {firstDifference}.";
    }

    private static EmulatorColor Normalize(EmulatorColor color, HardwareMode hardware)
    {
        if (hardware == HardwareMode.Dmg)
        {
            var intensity = (byte)(255 - (color.SgbIndex * 85));
            return new EmulatorColor(intensity, intensity, intensity);
        }

        return new EmulatorColor(ExpandFiveBit(color.R), ExpandFiveBit(color.G), ExpandFiveBit(color.B));
    }

    private static byte ExpandFiveBit(byte value)
    {
        return (byte)(value | (value >> 5));
    }
}
