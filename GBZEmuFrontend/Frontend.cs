using System.Numerics;
using GBZEmuLibrary;
using Raylib_cs;
using EmulatorSound = GBZEmuLibrary.Sound;
using RaylibColor = Raylib_cs.Color;

namespace GBZEmuFrontend;

internal sealed class Frontend : IDisposable
{
    private const int PresentationFramesPerSecond = 60;
    private const int AudioFramesPerBuffer = EmulatorSound.SAMPLE_RATE / PresentationFramesPerSecond;
    private const int AudioQueueCapacityFrames = AudioFramesPerBuffer * 2;
    private const int MaxCatchUpFrames = 5;
    private const float DcBlockerFeedback = 0.999f;
    private const double FrameDuration = 1.0 / Display.FRAME_RATE;
    private const double FrameStepRepeatDelay = 0.4;
    private const double FrameStepRepeatInterval = 1.0 / 15.0;

    private readonly Emulator _emulator = new();
    private readonly FrameBlender _frameBlender = new();
    private readonly RaylibColor[] _pixels = new RaylibColor[Display.HORIZONTAL_RESOLUTION * Display.VERTICAL_RESOLUTION];
    private readonly short[] _audioSamples = new short[AudioFramesPerBuffer * 2];
    private readonly short[] _audioQueue = new short[AudioQueueCapacityFrames * 2];
    private readonly Dictionary<KeyboardKey, JoypadButtons> _keyMap = new()
    {
        [KeyboardKey.Right] = JoypadButtons.Right,
        [KeyboardKey.Left] = JoypadButtons.Left,
        [KeyboardKey.Up] = JoypadButtons.Up,
        [KeyboardKey.Down] = JoypadButtons.Down,
        [KeyboardKey.X] = JoypadButtons.A,
        [KeyboardKey.Z] = JoypadButtons.B,
        [KeyboardKey.RightShift] = JoypadButtons.Select,
        [KeyboardKey.Enter] = JoypadButtons.Start
    };

    private Texture2D _texture;
    private AudioStream _audioStream;
    private string _windowTitle = string.Empty;
    private bool _started;
    private bool _windowReady;
    private bool _textureReady;
    private bool _audioReady;
    private bool _paused;
    private bool _waitingForInputRelease;
    private bool _frameStepRepeatArmed;
    private bool _audioNeedsReset;
    private bool _rawFrames;
    private bool _correctCgbColors;
    private bool _videoFrameReady;
    private int _audioQueueReadFrame;
    private int _audioQueueWriteFrame;
    private int _audioQueuedFrames;
    private double _nextFrameStepTime;
    private double _lastUpdateTime;
    private double _frameAccumulator;
    private float _leftDcInput;
    private float _leftDcOutput;
    private float _rightDcInput;
    private float _rightDcOutput;

    public void Run(FrontendOptions options)
    {
        Raylib.SetConfigFlags(ConfigFlags.VSyncHint | ConfigFlags.HighDpiWindow);
        Raylib.InitWindow(
            Display.HORIZONTAL_RESOLUTION * options.Scale,
            Display.VERTICAL_RESOLUTION * options.Scale,
            "GBZEmuFrontend - Select ROM");
        _windowReady = true;
        Raylib.SetTargetFPS(PresentationFramesPerSecond);

        var romPath = options.ROMPath ?? SelectROM(options.ROMDirectory!);
        if (romPath == null)
        {
            return;
        }

        _waitingForInputRelease = options.ROMPath == null;
        _rawFrames = options.RawFrames;
        _frameBlender.Reset();

        // Boot each cartridge on its native hardware: GBC-flagged carts get the GBC
        // boot ROM, everything else boots as an original DMG.
        var cgbCartridge = IsGBCCartridge(romPath);
        var cgbHardware = !options.ForceDMG && cgbCartridge;
        _correctCgbColors = ShouldCorrectCgbColors(options.ForceDMG, cgbCartridge, options.RawColors);
        var bootMode = options.ForceDMG ? BootMode.DMG | BootMode.Force
            : cgbHardware ? BootMode.GBC
            : BootMode.DMG;
        if (options.SkipBootROM)
        {
            bootMode |= BootMode.Skip;
        }

        _started = _emulator.Start(new Emulator.Config
        {
            ROMPath = romPath,
            SaveLocation = options.SaveDirectory,
            BootROMPaths = options.BootROMPaths.ToArray(),
            BootMode = bootMode
        });

        if (!_started)
        {
            throw new InvalidOperationException($"Failed to load ROM: {romPath}");
        }

        _windowTitle = $"GBZEmuFrontend - {Path.GetFileName(romPath)}";
        Raylib.SetWindowTitle(_windowTitle);

        var image = Raylib.GenImageColor(Display.HORIZONTAL_RESOLUTION, Display.VERTICAL_RESOLUTION, RaylibColor.Black);
        _texture = Raylib.LoadTextureFromImage(image);
        Raylib.UnloadImage(image);
        Raylib.SetTextureFilter(_texture, TextureFilter.Point);
        _textureReady = true;

        Raylib.InitAudioDevice();
        _audioReady = Raylib.IsAudioDeviceReady();
        if (_audioReady)
        {
            Raylib.SetAudioStreamBufferSizeDefault(AudioFramesPerBuffer);
            _audioStream = Raylib.LoadAudioStream(EmulatorSound.SAMPLE_RATE, 16, 2);
            Raylib.PlayAudioStream(_audioStream);
        }
        else
        {
            Console.Error.WriteLine("Audio device initialization failed; continuing without sound.");
        }

        if (options.StartPaused)
        {
            SetPaused(true);
        }

        _lastUpdateTime = Raylib.GetTime();

        while (!Raylib.WindowShouldClose())
        {
            UpdateInput();

            var stepFrame = ShouldStepFrame();
            if (AdvanceEmulation(stepFrame))
            {
                UpdateVideo();
            }

            UpdateAudio();
            Draw(options.Scale);
        }
    }

    public void Dispose()
    {
        if (_audioReady)
        {
            Raylib.StopAudioStream(_audioStream);
            Raylib.UnloadAudioStream(_audioStream);
            Raylib.CloseAudioDevice();
            _audioReady = false;
        }

        if (_windowReady)
        {
            if (_textureReady)
            {
                Raylib.UnloadTexture(_texture);
                _textureReady = false;
            }

            Raylib.CloseWindow();
            _windowReady = false;
        }

        if (_started)
        {
            _emulator.Terminate();
            _started = false;
        }
    }

    /// <summary>
    /// Reads the cartridge header's GBC flag so each ROM boots on its native hardware.
    /// </summary>
    private static bool IsGBCCartridge(string romPath)
    {
        using var stream = File.OpenRead(romPath);
        if (stream.Length <= 0x143)
        {
            return false;
        }

        stream.Position = 0x143;
        var gbcFlag = stream.ReadByte();
        return gbcFlag == 0x80 || gbcFlag == 0xC0;
    }

    /// <summary>
    /// Determines whether native CGB execution should use the frontend color profile.
    /// </summary>
    internal static bool ShouldCorrectCgbColors(bool forceDmg, bool cgbCartridge, bool rawColors)
    {
        return !forceDmg && cgbCartridge && !rawColors;
    }

    private static string? SelectROM(string romDirectory)
    {
        var romPaths = Directory.GetFiles(romDirectory)
            .Where(path => path.EndsWith(".gb", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".gbc", StringComparison.OrdinalIgnoreCase))
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var selectedIndex = 0;

        while (!Raylib.WindowShouldClose())
        {
            if (romPaths.Length > 0)
            {
                if (Raylib.IsKeyPressed(KeyboardKey.Up))
                {
                    selectedIndex = (selectedIndex + romPaths.Length - 1) % romPaths.Length;
                }

                if (Raylib.IsKeyPressed(KeyboardKey.Down))
                {
                    selectedIndex = (selectedIndex + 1) % romPaths.Length;
                }

                if (Raylib.IsKeyPressed(KeyboardKey.Enter))
                {
                    return romPaths[selectedIndex];
                }
            }

            DrawROMPicker(romDirectory, romPaths, selectedIndex);
        }

        return null;
    }

    private static void DrawROMPicker(string romDirectory, string[] romPaths, int selectedIndex)
    {
        const int padding = 24;
        const int titleFontSize = 24;
        const int itemFontSize = 18;
        const int itemHeight = 26;
        const int listTop = 84;

        var visibleItems = Math.Max(1, (Raylib.GetScreenHeight() - listTop - padding) / itemHeight);
        var firstVisible = Math.Max(0, selectedIndex - visibleItems + 1);
        var lastVisible = Math.Min(romPaths.Length, firstVisible + visibleItems);

        Raylib.BeginDrawing();
        Raylib.ClearBackground(RaylibColor.Black);
        Raylib.DrawText("Select a ROM", padding, 20, titleFontSize, RaylibColor.RayWhite);
        Raylib.DrawText(romDirectory, padding, 52, 14, RaylibColor.Gray);

        if (romPaths.Length == 0)
        {
            Raylib.DrawText("No .gb or .gbc files found. Press Escape to quit.", padding, listTop, itemFontSize, RaylibColor.Gray);
        }
        else
        {
            for (var i = firstVisible; i < lastVisible; i++)
            {
                var color = i == selectedIndex ? RaylibColor.Yellow : RaylibColor.RayWhite;
                var prefix = i == selectedIndex ? "> " : "  ";
                Raylib.DrawText($"{prefix}{Path.GetFileName(romPaths[i])}", padding, listTop + ((i - firstVisible) * itemHeight), itemFontSize, color);
            }
        }

        Raylib.EndDrawing();
    }

    private void UpdateInput()
    {
        if (_waitingForInputRelease)
        {
            _waitingForInputRelease = _keyMap.Keys.Any(key => Raylib.IsKeyDown(key));
            return;
        }

        foreach (var binding in _keyMap)
        {
            if (Raylib.IsKeyPressed(binding.Key))
            {
                _emulator.ButtonDown(binding.Value);
            }

            if (Raylib.IsKeyReleased(binding.Key))
            {
                _emulator.ButtonUp(binding.Value);
            }
        }
    }

    private bool ShouldStepFrame()
    {
        if (Raylib.IsKeyPressed(KeyboardKey.P))
        {
            SetPaused(!_paused);
        }

        if (!_paused)
        {
            ResetFrameStepRepeat();
            return false;
        }

        if (Raylib.IsKeyPressed(KeyboardKey.N))
        {
            _frameStepRepeatArmed = true;
            _nextFrameStepTime = Raylib.GetTime() + FrameStepRepeatDelay;
            return true;
        }

        if (!Raylib.IsKeyDown(KeyboardKey.N))
        {
            ResetFrameStepRepeat();
            return false;
        }

        if (!_frameStepRepeatArmed || Raylib.GetTime() < _nextFrameStepTime)
        {
            return false;
        }

        _nextFrameStepTime = Raylib.GetTime() + FrameStepRepeatInterval;
        return true;
    }

    private bool AdvanceEmulation(bool stepFrame)
    {
        var now = Raylib.GetTime();
        var elapsed = now - _lastUpdateTime;
        _lastUpdateTime = now;

        if (_paused)
        {
            _frameAccumulator = 0;

            if (!stepFrame)
            {
                return false;
            }

            AdvanceFrame(false);
            ResetAudioQueue();
            return true;
        }

        _frameAccumulator += Math.Min(elapsed, FrameDuration * MaxCatchUpFrames);

        var framesAdvanced = 0;
        while (_frameAccumulator >= FrameDuration && framesAdvanced < MaxCatchUpFrames)
        {
            AdvanceFrame(true);
            _frameAccumulator -= FrameDuration;
            framesAdvanced++;
        }

        return framesAdvanced > 0;
    }

    private void AdvanceFrame(bool queueAudio)
    {
        _emulator.Update();
        _frameBlender.Process(_emulator.GetScreenData(), _pixels, !_rawFrames, _correctCgbColors);
        _videoFrameReady = true;
        var source = _emulator.GetSoundSamples(out var sampleFrameCount);

        if (queueAudio && _audioReady)
        {
            QueueAudio(source, sampleFrameCount);
        }
    }

    private void ResetFrameStepRepeat()
    {
        _frameStepRepeatArmed = false;
        _nextFrameStepTime = 0;
    }

    private void SetPaused(bool paused)
    {
        _paused = paused;
        ResetFrameStepRepeat();
        Raylib.SetWindowTitle(paused ? $"{_windowTitle} [PAUSED]" : _windowTitle);

        if (!_audioReady)
        {
            return;
        }

        if (paused)
        {
            Raylib.PauseAudioStream(_audioStream);
        }
        else if (_audioNeedsReset)
        {
            Raylib.StopAudioStream(_audioStream);
            Raylib.PlayAudioStream(_audioStream);
            _audioNeedsReset = false;
        }
        else
        {
            Raylib.ResumeAudioStream(_audioStream);
        }
    }

    private void UpdateVideo()
    {
        if (!_videoFrameReady)
        {
            return;
        }

        Raylib.UpdateTexture(_texture, _pixels);
        _videoFrameReady = false;
    }

    private void QueueAudio(byte[] source, int frameCount)
    {
        if (frameCount <= 0)
        {
            return;
        }

        if (frameCount > AudioQueueCapacityFrames - _audioQueuedFrames)
        {
            var framesToDiscard = frameCount - (AudioQueueCapacityFrames - _audioQueuedFrames);
            _audioQueueReadFrame = (_audioQueueReadFrame + framesToDiscard) % AudioQueueCapacityFrames;
            _audioQueuedFrames -= framesToDiscard;
        }

        for (var i = 0; i < frameCount; i++)
        {
            _audioQueue[_audioQueueWriteFrame * 2] = FilterSample(source[i * 2], ref _leftDcInput, ref _leftDcOutput);
            _audioQueue[(_audioQueueWriteFrame * 2) + 1] = FilterSample(source[(i * 2) + 1], ref _rightDcInput, ref _rightDcOutput);
            _audioQueueWriteFrame = (_audioQueueWriteFrame + 1) % AudioQueueCapacityFrames;
        }

        _audioQueuedFrames += frameCount;
    }

    private void UpdateAudio()
    {
        if (_paused || !_audioReady)
        {
            return;
        }

        while (_audioQueuedFrames >= AudioFramesPerBuffer && Raylib.IsAudioStreamProcessed(_audioStream))
        {
            for (var i = 0; i < AudioFramesPerBuffer; i++)
            {
                _audioSamples[i * 2] = _audioQueue[_audioQueueReadFrame * 2];
                _audioSamples[(i * 2) + 1] = _audioQueue[(_audioQueueReadFrame * 2) + 1];
                _audioQueueReadFrame = (_audioQueueReadFrame + 1) % AudioQueueCapacityFrames;
            }

            _audioQueuedFrames -= AudioFramesPerBuffer;
            Raylib.UpdateAudioStream(_audioStream, _audioSamples, AudioFramesPerBuffer);
        }
    }

    private void ResetAudioQueue()
    {
        _audioQueueReadFrame = 0;
        _audioQueueWriteFrame = 0;
        _audioQueuedFrames = 0;
        _leftDcInput = 0;
        _leftDcOutput = 0;
        _rightDcInput = 0;
        _rightDcOutput = 0;
        _audioNeedsReset = _audioReady;
    }

    private static short FilterSample(int sample, ref float previousInput, ref float previousOutput)
    {
        var output = sample - previousInput + (DcBlockerFeedback * previousOutput);
        previousInput = sample;
        previousOutput = output;
        return (short)Math.Clamp(output * 512, short.MinValue, short.MaxValue);
    }

    private void Draw(int scale)
    {
        Raylib.BeginDrawing();
        Raylib.ClearBackground(RaylibColor.Black);
        Raylib.DrawTexturePro(
            _texture,
            new Rectangle(0, 0, Display.HORIZONTAL_RESOLUTION, Display.VERTICAL_RESOLUTION),
            new Rectangle(0, 0, Display.HORIZONTAL_RESOLUTION * scale, Display.VERTICAL_RESOLUTION * scale),
            Vector2.Zero,
            0,
            RaylibColor.White);
        Raylib.EndDrawing();
    }
}
