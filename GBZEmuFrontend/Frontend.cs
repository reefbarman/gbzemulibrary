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
    private bool _started;
    private bool _windowReady;
    private bool _audioReady;

    public void Run(FrontendOptions options)
    {
        var bootMode = options.ForceDMG ? BootMode.DMG | BootMode.Force : BootMode.GBC;
        if (options.BootROMPath == null)
        {
            bootMode |= BootMode.Skip;
        }

        _started = _emulator.Start(new Emulator.Config
        {
            ROMPath = options.ROMPath,
            SaveLocation = options.SaveDirectory,
            BootROMPath = options.BootROMPath,
            BootMode = bootMode
        });

        if (!_started)
        {
            throw new InvalidOperationException($"Failed to load ROM: {options.ROMPath}");
        }

        Raylib.SetConfigFlags(ConfigFlags.VSyncHint | ConfigFlags.HighDpiWindow);
        Raylib.InitWindow(
            Display.HORIZONTAL_RESOLUTION * options.Scale,
            Display.VERTICAL_RESOLUTION * options.Scale,
            $"GBZEmuFrontend - {Path.GetFileName(options.ROMPath)}");
        _windowReady = true;
        Raylib.SetTargetFPS(FramesPerSecond);

        var image = Raylib.GenImageColor(Display.HORIZONTAL_RESOLUTION, Display.VERTICAL_RESOLUTION, RaylibColor.Black);
        _texture = Raylib.LoadTextureFromImage(image);
        Raylib.UnloadImage(image);
        Raylib.SetTextureFilter(_texture, TextureFilter.Point);

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

        while (!Raylib.WindowShouldClose())
        {
            UpdateInput();
            _emulator.Update();
            UpdateVideo();
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
            Raylib.UnloadTexture(_texture);
            Raylib.CloseWindow();
            _windowReady = false;
        }

        if (_started)
        {
            _emulator.Terminate();
            _started = false;
        }
    }

    private void UpdateInput()
    {
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

        if (!_audioReady || !Raylib.IsAudioStreamProcessed(_audioStream))
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
