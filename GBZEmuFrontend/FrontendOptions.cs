namespace GBZEmuFrontend;

internal sealed class FrontendOptions
{
    public string? ROMPath { get; init; }
    public string? ROMDirectory { get; init; }
    public string? BootROMPath { get; init; }
    public required string SaveDirectory { get; init; }
    public int Scale { get; init; } = 4;
    public bool ForceDMG { get; init; }
    public bool StartPaused { get; init; }

    public static FrontendOptions Parse(string[] args)
    {
        if (args.Length == 0)
        {
            throw new ArgumentException("A ROM path or --rom-dir is required.");
        }

        string? romPath = null;
        string? romDirectory = null;
        string? bootROMPath = null;
        string? saveDirectory = null;
        var scale = 4;
        var forceDMG = false;
        var startPaused = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--rom-dir":
                    romDirectory = ReadValue(args, ref i, "--rom-dir");
                    break;
                case "--bootrom":
                    bootROMPath = ReadValue(args, ref i, "--bootrom");
                    break;
                case "--save-dir":
                    saveDirectory = ReadValue(args, ref i, "--save-dir");
                    break;
                case "--scale":
                    var value = ReadValue(args, ref i, "--scale");
                    if (!int.TryParse(value, out scale) || scale < 1 || scale > 10)
                    {
                        throw new ArgumentException("--scale must be an integer from 1 to 10.");
                    }
                    break;
                case "--dmg":
                    forceDMG = true;
                    break;
                case "--paused":
                    startPaused = true;
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

        if ((romPath == null) == (romDirectory == null))
        {
            throw new ArgumentException("Supply either one ROM path or --rom-dir, but not both.");
        }

        romPath = romPath == null ? null : Path.GetFullPath(romPath);
        romDirectory = romDirectory == null ? null : Path.GetFullPath(romDirectory);
        bootROMPath = bootROMPath == null ? null : Path.GetFullPath(bootROMPath);
        saveDirectory = saveDirectory == null
            ? romDirectory ?? Path.GetDirectoryName(romPath!) ?? Directory.GetCurrentDirectory()
            : Path.GetFullPath(saveDirectory);

        if (romPath != null && !File.Exists(romPath))
        {
            throw new FileNotFoundException("ROM file not found.", romPath);
        }

        if (romDirectory != null && !Directory.Exists(romDirectory))
        {
            throw new DirectoryNotFoundException($"ROM directory not found: {romDirectory}");
        }

        if (bootROMPath != null && !File.Exists(bootROMPath))
        {
            throw new FileNotFoundException("Boot ROM file not found.", bootROMPath);
        }

        Directory.CreateDirectory(saveDirectory);

        return new FrontendOptions
        {
            ROMPath = romPath,
            ROMDirectory = romDirectory,
            BootROMPath = bootROMPath,
            SaveDirectory = saveDirectory,
            Scale = scale,
            ForceDMG = forceDMG,
            StartPaused = startPaused
        };
    }

    public const string Usage =
        "Usage: dotnet run --project GBZEmuFrontend -- (<rom-path> | --rom-dir <path>) [options]\n" +
        "\n" +
        "Options:\n" +
        "  --rom-dir <path>  Select a ROM from this directory\n" +
        "  --bootrom <path>  Boot ROM image\n" +
        "  --save-dir <path> Save directory\n" +
        "  --scale <1-10>    Integer window scale\n" +
        "  --dmg             Force DMG mode\n" +
        "  --paused          Start emulation paused\n" +
        "\n" +
        "Controls:\n" +
        "  Arrow keys  D-pad / ROM selection\n" +
        "  X           A\n" +
        "  Z           B\n" +
        "  Enter       Start / select ROM\n" +
        "  Right Shift Select\n" +
        "  P           Pause or resume\n" +
        "  N           Step one frame; hold to repeat while paused\n" +
        "  Escape      Quit";

    private static string ReadValue(string[] args, ref int index, string option)
    {
        index++;
        if (index >= args.Length)
        {
            throw new ArgumentException($"{option} requires a value.");
        }

        return args[index];
    }
}
