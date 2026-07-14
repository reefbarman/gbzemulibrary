using System.Numerics;
using GBZEmuLibrary;
using Raylib_cs;
using EmulatorColor = GBZEmuLibrary.Color;
using EmulatorSound = GBZEmuLibrary.Sound;
using RaylibColor = Raylib_cs.Color;

namespace GBZEmuFrontend;

internal sealed class Frontend : IDisposable
{
    private const int FramesPerSecond = 60;
    private const int AudioFramesPerUpdate = EmulatorSound.SAMPLE_RATE / FramesPerSecond;
    private const double FrameStepRepeatDelay = 0.4;
    private const double FrameStepRepeatInterval = 1.0 / 15.0;

    private readonly Emulator _emulator = new();
    private readonly RaylibColor[] _pixels = new RaylibColor[Display.HORIZONTAL_RESOLUTION * Display.VERTICAL_RESOLUTION];
    private readonly short[] _audioSamples = new short[AudioFramesPerUpdate * 2];
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
    private double _nextFrameStepTime;

    public void Run(FrontendOptions options)
    {
        Raylib.SetConfigFlags(ConfigFlags.VSyncHint | ConfigFlags.HighDpiWindow);
        Raylib.InitWindow(
            Display.HORIZONTAL_RESOLUTION * options.Scale,
            Display.VERTICAL_RESOLUTION * options.Scale,
            "GBZEmuFrontend - Select ROM");
        _windowReady = true;
        Raylib.SetTargetFPS(FramesPerSecond);

        var romPath = options.ROMPath ?? SelectROM(options.ROMDirectory!);
        if (romPath == null)
        {
            return;
        }

        _waitingForInputRelease = options.ROMPath == null;

        var bootMode = options.ForceDMG ? BootMode.DMG | BootMode.Force : BootMode.GBC;
        if (options.BootROMPath == null)
        {
            bootMode |= BootMode.Skip;
        }

        _started = _emulator.Start(new Emulator.Config
        {
            ROMPath = romPath,
            SaveLocation = options.SaveDirectory,
            BootROMPath = options.BootROMPath,
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
            Raylib.SetAudioStreamBufferSizeDefault(AudioFramesPerUpdate);
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

        while (!Raylib.WindowShouldClose())
        {
            UpdateInput();

            if (ShouldAdvanceFrame())
            {
                _emulator.Update();
                UpdateVideo();
                UpdateAudio();
            }

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

    private bool ShouldAdvanceFrame()
    {
        if (Raylib.IsKeyPressed(KeyboardKey.P))
        {
            SetPaused(!_paused);
        }

        if (!_paused)
        {
            ResetFrameStepRepeat();
            return true;
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
        else
        {
            Raylib.ResumeAudioStream(_audioStream);
        }
    }

    private void UpdateVideo()
    {
        var screen = _emulator.GetScreenData();

        for (var y = 0; y < Display.VERTICAL_RESOLUTION; y++)
        {
            for (var x = 0; x < Display.HORIZONTAL_RESOLUTION; x++)
            {
                EmulatorColor color = screen[x, y];
                _pixels[(y * Display.HORIZONTAL_RESOLUTION) + x] = new RaylibColor(color.R, color.G, color.B, byte.MaxValue);
            }
        }

        Raylib.UpdateTexture(_texture, _pixels);
    }

    private void UpdateAudio()
    {
        var source = _emulator.GetSoundSamples();

        if (_paused || !_audioReady || !Raylib.IsAudioStreamProcessed(_audioStream))
        {
            return;
        }

        var sampleCount = Math.Min(source.Length, _audioSamples.Length);
        var leftTotal = 0;
        var rightTotal = 0;
        var frameCount = sampleCount / 2;

        for (var i = 0; i < frameCount; i++)
        {
            leftTotal += source[i * 2];
            rightTotal += source[(i * 2) + 1];
        }

        var leftCenter = frameCount == 0 ? 0 : leftTotal / frameCount;
        var rightCenter = frameCount == 0 ? 0 : rightTotal / frameCount;

        for (var i = 0; i < frameCount; i++)
        {
            _audioSamples[i * 2] = ScaleSample(source[i * 2] - leftCenter);
            _audioSamples[(i * 2) + 1] = ScaleSample(source[(i * 2) + 1] - rightCenter);
        }

        if (sampleCount < _audioSamples.Length)
        {
            Array.Clear(_audioSamples, sampleCount, _audioSamples.Length - sampleCount);
        }

        Raylib.UpdateAudioStream(_audioStream, _audioSamples, AudioFramesPerUpdate);
    }

    private static short ScaleSample(int sample)
    {
        return (short)Math.Clamp(sample * 512, short.MinValue, short.MaxValue);
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
