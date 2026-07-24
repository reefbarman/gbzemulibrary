using GBZEmuLibrary;
using Raylib_cs;
using EmulatorColor = GBZEmuLibrary.Color;
using RaylibColor = Raylib_cs.Color;

namespace GBZEmuFrontend;

/// <summary>
/// Applies presentation-only CGB correction and adjacent-frame LCD persistence to completed frames.
/// </summary>
internal sealed class FrameBlender
{
    private readonly EmulatorColor[] _previousRawFrame = new EmulatorColor[Display.HORIZONTAL_RESOLUTION * Display.VERTICAL_RESOLUTION];
    private bool _hasPreviousFrame;

    /// <summary>
    /// Converts a raw core framebuffer to presentation pixels and retains raw history for the next frame.
    /// </summary>
    public void Process(
        EmulatorColor[,] source,
        RaylibColor[] destination,
        string persistenceId,
        bool correctCgbColors = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);

        var previousWeight = ResolvePreviousWeight(persistenceId);
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
                var currentRaw = source[x, y];
                var current = Correct(currentRaw, correctCgbColors);
                var output = current;
                if (previousWeight > 0 && _hasPreviousFrame)
                {
                    var previous = Correct(_previousRawFrame[index], correctCgbColors);
                    output = Blend(previous, current, previousWeight);
                }

                destination[index] = new RaylibColor(output.R, output.G, output.B, byte.MaxValue);
                _previousRawFrame[index] = currentRaw;
            }
        }

        _hasPreviousFrame = true;
    }

    /// <summary>
    /// Invalidates frame history so the next frame is presented without stale pixels.
    /// </summary>
    public void Reset()
    {
        _hasPreviousFrame = false;
    }

    private static double ResolvePreviousWeight(string persistenceId)
    {
        return persistenceId switch
        {
            VideoFilterPresetCatalog.OffPersistenceId => 0,
            VideoFilterPresetCatalog.SubtlePersistenceId => 0.25,
            VideoFilterPresetCatalog.ClassicPersistenceId => 0.50,
            _ => throw new ArgumentOutOfRangeException(
                nameof(persistenceId),
                persistenceId,
                "Unknown persistence level.")
        };
    }

    private static EmulatorColor Correct(EmulatorColor color, bool correctCgbColors)
    {
        return correctCgbColors ? CgbColorCorrection.ApplyModernBalanced(color) : color;
    }

    private static EmulatorColor Blend(EmulatorColor previous, EmulatorColor current, double previousWeight)
    {
        var currentWeight = 1 - previousWeight;
        return new EmulatorColor(
            BlendComponent(previous.R, current.R, previousWeight, currentWeight),
            BlendComponent(previous.G, current.G, previousWeight, currentWeight),
            BlendComponent(previous.B, current.B, previousWeight, currentWeight));
    }

    private static byte BlendComponent(
        byte previous,
        byte current,
        double previousWeight,
        double currentWeight)
    {
        return (byte)Math.Round(
            (previous * previousWeight) + (current * currentWeight),
            MidpointRounding.AwayFromZero);
    }
}
