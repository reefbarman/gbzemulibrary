using GBZEmuLibrary;

namespace GBZEmuHeadless;

/// <summary>
/// Defines deterministic ROM execution, capture, boot, save, and input settings for the headless host.
/// </summary>
public sealed class HeadlessOptions
{
    public required string ROMPath { get; init; }
    public required string OutputDirectory { get; init; }
    public required string SaveDirectory { get; init; }
    public string? AudioOutputPath { get; init; }
    public string? BootROMPath { get; init; }
    public HardwareModel? HardwareModel { get; init; }
    public IReadOnlyList<HeadlessInputEvent> InputEvents { get; init; } = [];
    public int Frames { get; init; }
    public int CaptureStartFrame { get; init; }
    public int CaptureEndFrame { get; init; }
    public int CaptureEvery { get; init; } = 1;
    public bool SkipBootROM { get; init; }

    public static HeadlessOptions Parse(string[] args)
    {
        if (args.Length == 0)
        {
            throw new ArgumentException("A ROM path is required.");
        }

        string? romPath = null;
        string? outputDirectory = null;
        string? saveDirectory = null;
        string? audioOutputPath = null;
        int? captureStart = null;
        int? captureEnd = null;
        var frames = 1;
        var captureEvery = 1;
        HardwareModel? hardwareModel = null;
        var skipBootROM = false;
        string? bootROMPath = null;
        var inputEvents = new List<HeadlessInputEvent>();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--frames":
                    frames = ParsePositiveInteger(ReadValue(args, ref i, "--frames"), "--frames");
                    break;
                case "--capture-frames":
                    ParseCaptureRange(ReadValue(args, ref i, "--capture-frames"), out captureStart, out captureEnd);
                    break;
                case "--capture-every":
                    captureEvery = ParsePositiveInteger(ReadValue(args, ref i, "--capture-every"), "--capture-every");
                    break;
                case "--output":
                    outputDirectory = ReadValue(args, ref i, "--output");
                    break;
                case "--save-dir":
                    saveDirectory = ReadValue(args, ref i, "--save-dir");
                    break;
                case "--audio-out":
                    audioOutputPath = ReadValue(args, ref i, "--audio-out");
                    break;
                case "--bootrom":
                    bootROMPath = ReadValue(args, ref i, "--bootrom");
                    break;
                case "--model":
                    hardwareModel = ParseHardwareModel(ReadValue(args, ref i, "--model"));
                    break;
                case "--input":
                    inputEvents.Add(HeadlessInputEvent.Parse(ReadValue(args, ref i, "--input")));
                    break;
                case "--skip-bootrom":
                    skipBootROM = true;
                    break;
                default:
                    if (args[i].StartsWith("-", StringComparison.Ordinal))
                    {
                        throw new ArgumentException($"Unknown option: {args[i]}");
                    }

                    if (romPath != null)
                    {
                        throw new ArgumentException("Only one ROM path can be supplied.");
                    }

                    romPath = args[i];
                    break;
            }
        }

        if (romPath == null)
        {
            throw new ArgumentException("A ROM path is required.");
        }

        romPath = Path.GetFullPath(romPath);
        if (!File.Exists(romPath))
        {
            throw new FileNotFoundException("ROM file not found.", romPath);
        }

        if (bootROMPath != null && skipBootROM)
        {
            throw new ArgumentException("--bootrom and --skip-bootrom are mutually exclusive.");
        }

        outputDirectory = Path.GetFullPath(outputDirectory ?? Path.Combine(Directory.GetCurrentDirectory(), "headless-output"));
        saveDirectory = Path.GetFullPath(saveDirectory ?? Path.Combine(outputDirectory, "saves"));
        audioOutputPath = audioOutputPath == null ? null : Path.GetFullPath(audioOutputPath);
        bootROMPath = bootROMPath == null ? null : Path.GetFullPath(bootROMPath);
        captureStart ??= frames;
        captureEnd ??= frames;

        if (captureStart < 1 || captureEnd < captureStart || captureEnd > frames)
        {
            throw new ArgumentException("--capture-frames must be within 1..--frames and use START or START-END.");
        }

        if (bootROMPath != null && !File.Exists(bootROMPath))
        {
            throw new FileNotFoundException("Boot ROM file not found.", bootROMPath);
        }

        if (inputEvents.Any(input => input.Frame > frames))
        {
            throw new ArgumentException("Input event frames must be within 1..--frames.");
        }

        return new HeadlessOptions
        {
            ROMPath = romPath,
            OutputDirectory = outputDirectory,
            SaveDirectory = saveDirectory,
            AudioOutputPath = audioOutputPath,
            BootROMPath = bootROMPath,
            HardwareModel = hardwareModel,
            InputEvents = inputEvents.OrderBy(input => input.Frame).ToArray(),
            Frames = frames,
            CaptureStartFrame = captureStart.Value,
            CaptureEndFrame = captureEnd.Value,
            CaptureEvery = captureEvery,
            SkipBootROM = skipBootROM
        };
    }

    public const string Usage =
        "Usage: dotnet run --project GBZEmuHeadless -- <rom-path> [options]\n" +
        "\n" +
        "Options:\n" +
        "  --frames <count>              Frames to execute; defaults to 1\n" +
        "  --capture-frames <start[-end]> Inclusive capture range; defaults to final frame\n" +
        "  --capture-every <count>       Capture every Nth frame in the range; defaults to 1\n" +
        "  --output <path>               Capture/report directory; defaults to ./headless-output\n" +
        "  --save-dir <path>             Save directory; defaults to <output>/saves\n" +
        "  --audio-out <path>            Write exact interleaved float32 core amplitudes for every frame\n" +
        "  --model <model>               Hardware model: DmgB, Mgb, CgbE, Sgb2, or AgbA\n" +
        "                                Defaults to DmgB for DMG-only ROMs and CgbE otherwise\n" +
        "  --bootrom <path>              External boot ROM for the resolved hardware model\n" +
        "                                The built-in boot ROM is used when omitted\n" +
        "  --skip-bootrom                Skip boot ROM execution (mutually exclusive with --bootrom)\n" +
        "  --input <frame:button:state>   Apply a button down/up event before a frame\n" +
        "  -h, --help                    Show this help\n" +
        "\n" +
        "Buttons: Right, Left, Up, Down, A, B, Select, Start\n" +
        "States: down, up";

    private static HardwareModel ParseHardwareModel(string value)
    {
        if (!Enum.TryParse<HardwareModel>(value, true, out var model) ||
            !Enum.IsDefined(typeof(HardwareModel), model) ||
            !string.Equals(value, model.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Unknown hardware model: {value}. Expected DmgB, Mgb, CgbE, Sgb2, or AgbA.");
        }

        return model;
    }

    private static string ReadValue(string[] args, ref int index, string option)
    {
        if (++index >= args.Length)
        {
            throw new ArgumentException($"Missing value for {option}.");
        }

        return args[index];
    }

    private static int ParsePositiveInteger(string value, string option)
    {
        if (!int.TryParse(value, out var parsed) || parsed <= 0)
        {
            throw new ArgumentException($"{option} must be a positive integer.");
        }

        return parsed;
    }

    private static void ParseCaptureRange(string value, out int? start, out int? end)
    {
        var separator = value.IndexOf('-');
        if (separator < 0)
        {
            start = ParsePositiveInteger(value, "--capture-frames");
            end = start;
            return;
        }

        start = ParsePositiveInteger(value[..separator], "--capture-frames");
        end = ParsePositiveInteger(value[(separator + 1)..], "--capture-frames");
    }
}

/// <summary>
/// Represents one deterministic joypad transition applied immediately before an emulated frame.
/// </summary>
public readonly record struct HeadlessInputEvent(int Frame, JoypadButtons Button, bool Pressed)
{
    public static HeadlessInputEvent Parse(string value)
    {
        var fields = value.Split(':');
        if (fields.Length != 3 || !int.TryParse(fields[0], out var frame) || frame <= 0)
        {
            throw new ArgumentException("--input must use FRAME:BUTTON:down or FRAME:BUTTON:up.");
        }

        if (!Enum.TryParse<JoypadButtons>(fields[1], true, out var button) || button == JoypadButtons.Count)
        {
            throw new ArgumentException($"Unknown joypad button: {fields[1]}");
        }

        var pressed = fields[2].ToLowerInvariant() switch
        {
            "down" => true,
            "up" => false,
            _ => throw new ArgumentException("Input state must be 'down' or 'up'.")
        };

        return new HeadlessInputEvent(frame, button, pressed);
    }
}
