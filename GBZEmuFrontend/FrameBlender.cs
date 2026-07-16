using GBZEmuLibrary;
using Raylib_cs;
using EmulatorColor = GBZEmuLibrary.Color;
using RaylibColor = Raylib_cs.Color;

namespace GBZEmuFrontend;

/// <summary>
/// Applies one-frame LCD persistence to completed emulator frames without changing the core framebuffer.
/// </summary>
internal sealed class FrameBlender
{
    private readonly EmulatorColor[] _previousFrame = new EmulatorColor[Display.HORIZONTAL_RESOLUTION * Display.VERTICAL_RESOLUTION];
    private bool _hasPreviousFrame;

    /// <summary>
    /// Converts a raw core framebuffer to presentation pixels and retains it for the next frame.
    /// </summary>
    public void Process(EmulatorColor[,] source, RaylibColor[] destination, bool blend, bool correctCgbColors = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);

        var pixelCount = Display.HORIZONTAL_RESOLUTION * Display.VERTICAL_RESOLUTION;
        if (destination.Length < pixelCount)
        {
            throw new ArgumentException("The destination must hold one complete display frame.", nameof(destination));
        }

        for (var y = 0; y < Display.VERTICAL_RESOLUTION; y++)
        {
            for (var x = 0; x < Display.HORIZONTAL_RESOLUTION; x++)
            {
                var index = y * Display.HORIZONTAL_RESOLUTION + x;
                var current = source[x, y];
                if (correctCgbColors)
                {
                    current = CgbColorCorrection.ApplyModernBalanced(current);
                }

                var output = blend && _hasPreviousFrame
                    ? Blend(_previousFrame[index], current)
                    : current;
                destination[index] = new RaylibColor(output.R, output.G, output.B, byte.MaxValue);
                _previousFrame[index] = current;
            }
        }

        _hasPreviousFrame = true;
    }

    /// <summary>
    /// Clears frame history so the next frame is presented without blending stale pixels.
    /// </summary>
    public void Reset()
    {
        _hasPreviousFrame = false;
    }

    private static EmulatorColor Blend(EmulatorColor previous, EmulatorColor current)
    {
        return new EmulatorColor(
            (byte)((previous.R + current.R + 1) / 2),
            (byte)((previous.G + current.G + 1) / 2),
            (byte)((previous.B + current.B + 1) / 2));
    }
}
