using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using GBZEmuLibrary;

namespace GBZEmuHeadless;

/// <summary>
/// Runs the engine-neutral emulator for a fixed frame budget and writes deterministic video diagnostics.
/// </summary>
public sealed class HeadlessRunner
{
    private const int ScrollYRegister = 0xFF42;
    private const int ScrollXRegister = 0xFF43;
    private const int LcdYCompareRegister = 0xFF45;
    private const int VramBankRegister = 0xFF4F;
    private const int VramStartAddress = 0x8000;
    private const int PolyStreamTileDataLength = (15 * 16 + 13) * 16;
    private const int BackgroundPaletteIndexRegister = 0xFF68;
    private const int BackgroundPaletteDataRegister = 0xFF69;
    private const int ObjectPaletteIndexRegister = 0xFF6A;
    private const int ObjectPaletteDataRegister = 0xFF6B;
    private const int WindowYRegister = 0xFF4A;
    private const int WindowXRegister = 0xFF4B;

    /// <summary>
    /// Executes one configured run and returns the absolute report path.
    /// </summary>
    public string Run(HeadlessOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Directory.CreateDirectory(options.OutputDirectory);
        Directory.CreateDirectory(options.SaveDirectory);
        DeletePreviousCaptures(options.OutputDirectory);

        var compatibility = CartridgeMetadata.Read(options.ROMPath).Compatibility;
        var hardwareModel = options.HardwareModel ?? ResolveAutomaticHardwareModel(compatibility);
        if (HardwareModelMetadata.IsImplemented(hardwareModel) &&
            !HardwareModelMetadata.SupportsCartridge(hardwareModel, compatibility))
        {
            throw new ArgumentException(
                $"Hardware model {hardwareModel} does not support {compatibility} cartridges.");
        }

        var bootRom = options.SkipBootROM
            ? BootRomConfig.Skip()
            : options.BootROMPath == null
                ? BootRomConfig.BuiltIn()
                : BootRomConfig.ExternalFile(options.BootROMPath);

        var emulator = new Emulator();
        try
        {
            if (!emulator.Start(new Emulator.Config(hardwareModel)
            {
                ROMPath = options.ROMPath,
                SaveLocation = options.SaveDirectory,
                BootRom = bootRom
            }))
            {
                throw new InvalidOperationException($"Failed to load ROM: {options.ROMPath}");
            }

            var eventsByFrame = options.InputEvents
                .GroupBy(input => input.Frame)
                .ToDictionary(group => group.Key, group => group.ToArray());
            var captures = new List<HeadlessCapture>();
            using var audioCapture = options.AudioOutputPath == null
                ? null
                : new RawAudioCapture(options.AudioOutputPath);

            for (var frame = 1; frame <= options.Frames; frame++)
            {
                if (eventsByFrame.TryGetValue(frame, out var frameEvents))
                {
                    foreach (var input in frameEvents)
                    {
                        if (input.Pressed)
                        {
                            emulator.ButtonDown(input.Button);
                        }
                        else
                        {
                            emulator.ButtonUp(input.Button);
                        }
                    }
                }

                emulator.Update();

                if (audioCapture != null)
                {
                    var samples = emulator.GetSoundSamples(out var sampleFrameCount);
                    audioCapture.Append(samples, sampleFrameCount);
                }

                if (ShouldCapture(options, frame))
                {
                    captures.Add(Capture(emulator, options.OutputDirectory, frame));
                }
            }

            var report = new HeadlessReport
            {
                ROMFile = Path.GetFileName(options.ROMPath),
                ROMSHA256 = HashFile(options.ROMPath),
                FramesExecuted = options.Frames,
                CaptureStartFrame = options.CaptureStartFrame,
                CaptureEndFrame = options.CaptureEndFrame,
                CaptureEvery = options.CaptureEvery,
                HardwareModel = hardwareModel.ToString(),
                BootRomSource = bootRom.Source.ToString(),
                Audio = audioCapture?.Complete(),
                InputEvents = options.InputEvents.Select(input => new HeadlessInputEventReport
                {
                    Frame = input.Frame,
                    Button = input.Button.ToString(),
                    State = input.Pressed ? "down" : "up"
                }).ToArray(),
                Captures = captures
            };
            var reportPath = Path.Combine(options.OutputDirectory, "report.json");
            File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                WriteIndented = true
            }) + Environment.NewLine);
            return Path.GetFullPath(reportPath);
        }
        finally
        {
            emulator.Terminate();
        }
    }

    private static HardwareModel ResolveAutomaticHardwareModel(CartridgeCompatibility compatibility)
    {
        return compatibility == CartridgeCompatibility.DmgOnly
            ? HardwareModel.DmgB
            : HardwareModel.CgbE;
    }

    private sealed class RawAudioCapture : IDisposable
    {
        private readonly string _path;
        private readonly FileStream _stream;
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private readonly List<int> _emulatorFrameSampleCounts = [];
        private int _sampleFrames;
        private int? _firstNonZeroSampleFrame;
        private float _minimumAmplitude = float.PositiveInfinity;
        private float _maximumAmplitude = float.NegativeInfinity;
        private bool _completed;

        public RawAudioCapture(string path)
        {
            _path = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            _stream = File.Create(_path);
        }

        public void Append(float[] samples, int sampleFrameCount)
        {
            var sampleCount = checked(sampleFrameCount * 2);
            if (sampleCount > samples.Length)
            {
                throw new InvalidOperationException("The core returned an invalid audio sample count.");
            }

            _emulatorFrameSampleCounts.Add(sampleFrameCount);
            var bytes = MemoryMarshal.AsBytes(samples.AsSpan(0, sampleCount));
            _stream.Write(bytes);
            _hash.AppendData(bytes);

            for (var i = 0; i < sampleCount; i++)
            {
                var amplitude = samples[i];
                _minimumAmplitude = Math.Min(_minimumAmplitude, amplitude);
                _maximumAmplitude = Math.Max(_maximumAmplitude, amplitude);
                if (_firstNonZeroSampleFrame == null && amplitude != 0)
                {
                    _firstNonZeroSampleFrame = _sampleFrames + (i / 2);
                }
            }

            _sampleFrames += sampleFrameCount;
        }

        public HeadlessAudioCapture Complete()
        {
            if (_completed)
            {
                throw new InvalidOperationException("The audio capture has already been completed.");
            }

            _completed = true;
            _stream.Flush();
            return new HeadlessAudioCapture
            {
                File = Path.GetFileName(_path),
                Format = "float32-le-stereo-amplitude",
                SampleRate = Sound.SAMPLE_RATE,
                SampleFrames = _sampleFrames,
                SHA256 = Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant(),
                MinimumAmplitude = _sampleFrames == 0 ? 0f : _minimumAmplitude,
                MaximumAmplitude = _sampleFrames == 0 ? 0f : _maximumAmplitude,
                FirstNonZeroSampleFrame = _firstNonZeroSampleFrame,
                EmulatorFrameSampleCounts = _emulatorFrameSampleCounts.ToArray()
            };
        }

        public void Dispose()
        {
            _stream.Dispose();
            _hash.Dispose();
        }
    }

    private static void DeletePreviousCaptures(string outputDirectory)
    {
        foreach (var path in Directory.EnumerateFiles(outputDirectory, "frame-*.ppm"))
        {
            File.Delete(path);
        }

        var reportPath = Path.Combine(outputDirectory, "report.json");
        if (File.Exists(reportPath))
        {
            File.Delete(reportPath);
        }
    }

    private static bool ShouldCapture(HeadlessOptions options, int frame)
    {
        return frame >= options.CaptureStartFrame &&
               frame <= options.CaptureEndFrame &&
               (frame - options.CaptureStartFrame) % options.CaptureEvery == 0;
    }

    private static HeadlessCapture Capture(Emulator emulator, string outputDirectory, int frame)
    {
        var imageName = $"frame-{frame:D6}.ppm";
        var framebuffer = emulator.GetScreenData();
        var rgb = GetRGBBytes(framebuffer, out var dominantColors);
        WritePpm(Path.Combine(outputDirectory, imageName), rgb);

        var cpu = emulator.Debug.GetCpuState();
        var ppu = emulator.Debug.GetPpuState();
        return new HeadlessCapture
        {
            Frame = frame,
            Image = imageName,
            FramebufferSHA256 = Convert.ToHexString(SHA256.HashData(rgb)).ToLowerInvariant(),
            UniqueColorCount = dominantColors.Count,
            DominantColors = dominantColors
                .Take(16)
                .Select(color => new HeadlessColorCount
                {
                    RGB = $"#{color.Key:X6}",
                    Pixels = color.Value
                })
                .ToArray(),
            TopRowSHA256 = HashBytes(rgb.AsSpan(0, Display.HORIZONTAL_RESOLUTION * 3)),
            RightColumnSHA256 = HashRightColumn(rgb),
            CgbBackgroundPaletteSHA256 = HashPalette(
                emulator,
                BackgroundPaletteIndexRegister,
                BackgroundPaletteDataRegister),
            CgbObjectPaletteSHA256 = HashPalette(
                emulator,
                ObjectPaletteIndexRegister,
                ObjectPaletteDataRegister),
            VramBank0TileDataSHA256 = HashVramBank(emulator, 0),
            VramBank1TileDataSHA256 = HashVramBank(emulator, 1),
            CPU = new HeadlessCpuState
            {
                PC = cpu.PC,
                SP = cpu.SP,
                AF = cpu.AF,
                BC = cpu.BC,
                DE = cpu.DE,
                HL = cpu.HL,
                InterruptsEnabled = cpu.InterruptsEnabled,
                Halted = cpu.Halted,
                DoubleSpeed = cpu.DoubleSpeed,
                TotalClockCycles = cpu.TotalClockCycles,
                ExecutedInstructionCount = cpu.ExecutedInstructionCount
            },
            PPU = new HeadlessPpuState
            {
                ScanLine = ppu.ScanLine,
                Mode = ppu.Mode,
                ModeClockCycles = ppu.ModeClockCycles,
                LCDC = ppu.LcdControl,
                STAT = ppu.LcdStatus,
                SCY = emulator.Debug.PeekByte(ScrollYRegister),
                SCX = emulator.Debug.PeekByte(ScrollXRegister),
                LYC = emulator.Debug.PeekByte(LcdYCompareRegister),
                WY = emulator.Debug.PeekByte(WindowYRegister),
                WX = emulator.Debug.PeekByte(WindowXRegister)
            }
        };
    }

    private static byte[] GetRGBBytes(Color[,] framebuffer, out IReadOnlyList<KeyValuePair<int, int>> dominantColors)
    {
        var rgb = new byte[Display.HORIZONTAL_RESOLUTION * Display.VERTICAL_RESOLUTION * 3];
        var colors = new Dictionary<int, int>();
        var offset = 0;

        for (var y = 0; y < Display.VERTICAL_RESOLUTION; y++)
        {
            for (var x = 0; x < Display.HORIZONTAL_RESOLUTION; x++)
            {
                var color = framebuffer[x, y];
                rgb[offset++] = color.R;
                rgb[offset++] = color.G;
                rgb[offset++] = color.B;
                var packedColor = color.R << 16 | color.G << 8 | color.B;
                colors.TryGetValue(packedColor, out var count);
                colors[packedColor] = count + 1;
            }
        }

        dominantColors = colors
            .OrderByDescending(color => color.Value)
            .ThenBy(color => color.Key)
            .ToArray();
        return rgb;
    }

    private static string HashRightColumn(byte[] rgb)
    {
        var column = new byte[Display.VERTICAL_RESOLUTION * 3];
        for (var y = 0; y < Display.VERTICAL_RESOLUTION; y++)
        {
            var sourceOffset = (y * Display.HORIZONTAL_RESOLUTION + Display.HORIZONTAL_RESOLUTION - 1) * 3;
            var destinationOffset = y * 3;
            column[destinationOffset] = rgb[sourceOffset];
            column[destinationOffset + 1] = rgb[sourceOffset + 1];
            column[destinationOffset + 2] = rgb[sourceOffset + 2];
        }

        return HashBytes(column);
    }

    private static string HashVramBank(Emulator emulator, byte bank)
    {
        var originalBank = emulator.Debug.PeekByte(VramBankRegister);
        var data = new byte[PolyStreamTileDataLength];
        try
        {
            emulator.Debug.PokeByte(bank, VramBankRegister);
            for (var offset = 0; offset < data.Length; offset++)
            {
                data[offset] = emulator.Debug.PeekByte(VramStartAddress + offset);
            }
        }
        finally
        {
            emulator.Debug.PokeByte(originalBank, VramBankRegister);
        }

        return HashBytes(data);
    }

    private static string HashPalette(Emulator emulator, int indexAddress, int dataAddress)
    {
        var originalIndex = emulator.Debug.PeekByte(indexAddress);
        var palette = new byte[64];
        try
        {
            for (var index = 0; index < palette.Length; index++)
            {
                emulator.Debug.PokeByte((byte)index, indexAddress);
                palette[index] = emulator.Debug.PeekByte(dataAddress);
            }
        }
        finally
        {
            emulator.Debug.PokeByte(originalIndex, indexAddress);
        }

        return HashBytes(palette);
    }

    private static string HashBytes(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static void WritePpm(string path, byte[] rgb)
    {
        var header = Encoding.ASCII.GetBytes($"P6\n{Display.HORIZONTAL_RESOLUTION} {Display.VERTICAL_RESOLUTION}\n255\n");
        using var output = File.Create(path);
        output.Write(header);
        output.Write(rgb);
    }

    private static string HashFile(string path)
    {
        using var input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
    }
}
