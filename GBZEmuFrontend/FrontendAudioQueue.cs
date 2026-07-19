namespace GBZEmuFrontend;

/// <summary>
/// Converts reusable core amplitude buffers into bounded, pre-rolled PCM for the Raylib audio stream.
/// </summary>
internal sealed class FrontendAudioQueue
{
    private const float DmgDcBlockerFeedback = 0.9960133f;
    private const float CgbDcBlockerFeedback = 0.9043098f;
    private const int OutputScale = 512;

    private readonly short[] _samples;
    private readonly int _startupFrames;
    private int _readFrame;
    private int _writeFrame;
    private int _queuedFrames;
    private int _droppedFrames;
    private float _feedback = DmgDcBlockerFeedback;
    private float _leftPreviousInput;
    private float _leftPreviousOutput;
    private float _rightPreviousInput;
    private float _rightPreviousOutput;
    private bool _primed;

    public FrontendAudioQueue(int capacityFrames, int startupFrames)
    {
        if (capacityFrames <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacityFrames));
        }

        if (startupFrames <= 0 || startupFrames > capacityFrames)
        {
            throw new ArgumentOutOfRangeException(nameof(startupFrames));
        }

        _samples = new short[capacityFrames * 2];
        _startupFrames = startupFrames;
    }

    public int CapacityFrames => _samples.Length / 2;
    public int QueuedFrames => _queuedFrames;
    public int DroppedFrames => _droppedFrames;
    public bool IsPrimed => _primed;

    public void SetHardwareModel(bool cgbHardware)
    {
        _feedback = cgbHardware ? CgbDcBlockerFeedback : DmgDcBlockerFeedback;
        Reset();
    }

    public void Enqueue(float[] source, int frameCount)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (frameCount < 0 || frameCount * 2 > source.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(frameCount));
        }

        var sourceFrame = Math.Max(0, frameCount - CapacityFrames);
        if (sourceFrame > 0)
        {
            _droppedFrames += sourceFrame;
            frameCount = CapacityFrames;
        }

        var overflow = Math.Max(0, frameCount - (CapacityFrames - _queuedFrames));
        if (overflow > 0)
        {
            _readFrame = (_readFrame + overflow) % CapacityFrames;
            _queuedFrames -= overflow;
            _droppedFrames += overflow;
        }

        for (var i = 0; i < frameCount; i++)
        {
            var sourceIndex = (sourceFrame + i) * 2;
            var destinationIndex = _writeFrame * 2;
            _samples[destinationIndex] = FilterSample(
                source[sourceIndex],
                ref _leftPreviousInput,
                ref _leftPreviousOutput);
            _samples[destinationIndex + 1] = FilterSample(
                source[sourceIndex + 1],
                ref _rightPreviousInput,
                ref _rightPreviousOutput);
            _writeFrame = (_writeFrame + 1) % CapacityFrames;
        }

        _queuedFrames += frameCount;
        _primed |= _queuedFrames >= _startupFrames;
    }

    public bool TryDequeue(short[] destination, int frameCount)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (frameCount < 0 || frameCount * 2 > destination.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(frameCount));
        }

        if (!_primed || _queuedFrames < frameCount)
        {
            return false;
        }

        for (var i = 0; i < frameCount; i++)
        {
            var sourceIndex = _readFrame * 2;
            destination[i * 2] = _samples[sourceIndex];
            destination[(i * 2) + 1] = _samples[sourceIndex + 1];
            _readFrame = (_readFrame + 1) % CapacityFrames;
        }

        _queuedFrames -= frameCount;
        return true;
    }

    public void Reset()
    {
        _readFrame = 0;
        _writeFrame = 0;
        _queuedFrames = 0;
        _droppedFrames = 0;
        _leftPreviousInput = 0;
        _leftPreviousOutput = 0;
        _rightPreviousInput = 0;
        _rightPreviousOutput = 0;
        _primed = false;
    }

    public void RequirePreroll()
    {
        _primed = false;
    }

    private short FilterSample(float sample, ref float previousInput, ref float previousOutput)
    {
        var output = sample - previousInput + (_feedback * previousOutput);
        previousInput = sample;
        previousOutput = output;
        return (short)Math.Clamp(output * OutputScale, short.MinValue, short.MaxValue);
    }
}
