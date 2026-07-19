using System;

namespace GBZEmuLibrary
{
    /// <summary>
    /// Reconstructs the mixed APU level at 4x output rate and decimates it through a low-pass FIR.
    /// </summary>
    internal sealed class BandLimitedAudioRenderer
    {
        private const int OversampleFactor = 4;
        private const int FilterTaps = 64;
        private const double Cutoff = 15.0 / 128.0;

        private static readonly double[] Filter = CreateFilter();

        private readonly float[][] _buffer =
        {
            new float[GameBoySchema.MAX_AUDIO_FRAMES_PER_VIDEO_FRAME * 2],
            new float[GameBoySchema.MAX_AUDIO_FRAMES_PER_VIDEO_FRAME * 2]
        };
        private readonly double[] _leftHistory = new double[FilterTaps];
        private readonly double[] _rightHistory = new double[FilterTaps];

        private double _cyclesPerOversample;
        private double _cycleCounter;
        private double _leftAccumulator;
        private double _rightAccumulator;
        private int _historyIndex;
        private int _oversamplePhase;
        private int _currentBuffer;
        private int _currentFrame;

        public BandLimitedAudioRenderer()
        {
            SetClockRate(GameBoySchema.MAX_DMG_CLOCK_CYCLES);
        }

        public void SetClockRate(double clockRate)
        {
            _cyclesPerOversample = clockRate / (Sound.SAMPLE_RATE * OversampleFactor);
        }

        public void Reset()
        {
            _cycleCounter = 0;
            _leftAccumulator = 0;
            _rightAccumulator = 0;
            _historyIndex = 0;
            _oversamplePhase = 0;
            _currentBuffer = 0;
            _currentFrame = 0;
            Array.Clear(_leftHistory, 0, _leftHistory.Length);
            Array.Clear(_rightHistory, 0, _rightHistory.Length);
            Array.Clear(_buffer[0], 0, _buffer[0].Length);
            Array.Clear(_buffer[1], 0, _buffer[1].Length);
        }

        public void Update(int left, int right, int cycles)
        {
            var cyclesLeft = (double)cycles;
            while (cyclesLeft > 0)
            {
                var cyclesToBoundary = _cyclesPerOversample - _cycleCounter;
                var integratedCycles = Math.Min(cyclesLeft, cyclesToBoundary);
                _leftAccumulator += left * integratedCycles;
                _rightAccumulator += right * integratedCycles;
                _cycleCounter += integratedCycles;
                cyclesLeft -= integratedCycles;

                if (_cycleCounter + 1e-9 < _cyclesPerOversample)
                {
                    continue;
                }

                PushOversample(
                    _leftAccumulator / _cyclesPerOversample,
                    _rightAccumulator / _cyclesPerOversample);
                _cycleCounter -= _cyclesPerOversample;
                _leftAccumulator = 0;
                _rightAccumulator = 0;
            }
        }

        public float[] GetSamples(out int sampleFrameCount)
        {
            sampleFrameCount = _currentFrame;
            _currentFrame = 0;

            var output = _buffer[_currentBuffer];
            _currentBuffer = (_currentBuffer + 1) % _buffer.Length;
            Array.Clear(_buffer[_currentBuffer], 0, _buffer[_currentBuffer].Length);
            return output;
        }

        private void PushOversample(double left, double right)
        {
            _leftHistory[_historyIndex] = left;
            _rightHistory[_historyIndex] = right;
            _historyIndex = (_historyIndex + 1) % FilterTaps;
            _oversamplePhase = (_oversamplePhase + 1) % OversampleFactor;
            if (_oversamplePhase != 0 || _currentFrame >= GameBoySchema.MAX_AUDIO_FRAMES_PER_VIDEO_FRAME)
            {
                return;
            }

            var filteredLeft = 0.0;
            var filteredRight = 0.0;
            var history = _historyIndex;
            for (var tap = 0; tap < FilterTaps; tap++)
            {
                history = (history + FilterTaps - 1) % FilterTaps;
                filteredLeft += _leftHistory[history] * Filter[tap];
                filteredRight += _rightHistory[history] * Filter[tap];
            }

            var outputIndex = _currentFrame * 2;
            _buffer[_currentBuffer][outputIndex] = (float)filteredLeft;
            _buffer[_currentBuffer][outputIndex + 1] = (float)filteredRight;
            _currentFrame++;
        }

        private static double[] CreateFilter()
        {
            var filter = new double[FilterTaps];
            var center = (FilterTaps - 1) / 2.0;
            var sum = 0.0;
            for (var tap = 0; tap < FilterTaps; tap++)
            {
                var distance = tap - center;
                var sinc = Math.Abs(distance) < double.Epsilon
                    ? 2 * Cutoff
                    : Math.Sin(2 * Math.PI * Cutoff * distance) / (Math.PI * distance);
                var window = 0.42
                             - (0.5 * Math.Cos(2 * Math.PI * tap / (FilterTaps - 1)))
                             + (0.08 * Math.Cos(4 * Math.PI * tap / (FilterTaps - 1)));
                filter[tap] = sinc * window;
                sum += filter[tap];
            }

            for (var tap = 0; tap < FilterTaps; tap++)
            {
                filter[tap] /= sum;
            }

            return filter;
        }
    }
}
