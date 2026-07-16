using EmulatorColor = GBZEmuLibrary.Color;

namespace GBZEmuFrontend;

/// <summary>
/// Applies a modern-display approximation of the nonlinear, cross-coupled CGB LCD color response.
/// </summary>
internal static class CgbColorCorrection
{
    private const double MixingGamma = 1.6;

    private static readonly byte[] ComponentCurve =
    [
        0, 6, 12, 20, 28, 36, 45, 56,
        66, 76, 88, 100, 113, 125, 137, 149,
        161, 172, 182, 192, 202, 210, 218, 225,
        232, 238, 243, 247, 250, 252, 254, 255
    ];

    private static readonly EmulatorColor[] ModernBalancedColors = CreateModernBalancedColors();

    /// <summary>
    /// Converts one raw RGB555-expanded core pixel to the frontend's Modern Balanced presentation profile.
    /// </summary>
    public static EmulatorColor ApplyModernBalanced(EmulatorColor color)
    {
        var red = color.R >> 3;
        var green = color.G >> 3;
        var blue = color.B >> 3;
        return ModernBalancedColors[red | (green << 5) | (blue << 10)];
    }

    private static EmulatorColor[] CreateModernBalancedColors()
    {
        var colors = new EmulatorColor[1 << 15];
        for (var value = 0; value < colors.Length; value++)
        {
            var red = ComponentCurve[value & 0x1F];
            var green = ComponentCurve[(value >> 5) & 0x1F];
            var blue = ComponentCurve[(value >> 10) & 0x1F];
            var correctedGreen = green == blue ? green : MixGreenAndBlue(green, blue);
            colors[value] = new EmulatorColor(red, correctedGreen, blue);
        }

        return colors;
    }

    private static byte MixGreenAndBlue(byte green, byte blue)
    {
        var greenLinear = Math.Pow(green / 255.0, MixingGamma);
        var blueLinear = Math.Pow(blue / 255.0, MixingGamma);
        var mixed = Math.Pow((greenLinear * 3 + blueLinear) / 4, 1 / MixingGamma);
        return (byte)Math.Round(mixed * 255, MidpointRounding.AwayFromZero);
    }
}
