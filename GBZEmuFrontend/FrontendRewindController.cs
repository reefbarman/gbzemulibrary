using GBZEmuLibrary;

namespace GBZEmuFrontend;

/// <summary>
/// Captures emulator checkpoints at a fixed frame cadence for bounded frontend rewind.
/// </summary>
internal sealed class FrontendRewindController
{
    public const int DefaultCaptureIntervalFrames = 6;
    public const int DefaultCapacity = 100;

    private readonly RewindBuffer _buffer;
    private readonly int _captureIntervalFrames;
    private int _framesSinceCapture;

    public int CheckpointCount => _buffer.Count;

    public FrontendRewindController(
        int capacity = DefaultCapacity,
        int captureIntervalFrames = DefaultCaptureIntervalFrames)
    {
        if (captureIntervalFrames < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(captureIntervalFrames));
        }

        _buffer = new RewindBuffer(capacity);
        _captureIntervalFrames = captureIntervalFrames;
    }

    /// <summary>
    /// Starts a new history with the emulator's current state as its first checkpoint.
    /// </summary>
    public void Reset(Emulator emulator)
    {
        _buffer.Clear();
        _framesSinceCapture = 0;
        _buffer.Capture(emulator);
    }

    /// <summary>
    /// Records elapsed emulated frames and captures the latest state whenever the cadence is crossed.
    /// </summary>
    public void FramesAdvanced(Emulator emulator, int frameCount)
    {
        if (frameCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frameCount));
        }

        _framesSinceCapture += frameCount;
        if (_framesSinceCapture < _captureIntervalFrames)
        {
            return;
        }

        _framesSinceCapture %= _captureIntervalFrames;
        _buffer.Capture(emulator);
    }

    /// <summary>
    /// Restores the preceding retained checkpoint.
    /// </summary>
    public bool TryRewind(Emulator emulator)
    {
        var restored = _buffer.TryRewind(emulator);
        if (restored)
        {
            _framesSinceCapture = 0;
        }

        return restored;
    }
}
