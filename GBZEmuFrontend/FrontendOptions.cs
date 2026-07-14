namespace GBZEmuFrontend;

internal sealed class FrontendOptions
{
    public required string ROMPath { get; init; }
    public string? BootROMPath { get; init; }
    public required string SaveDirectory { get; init; }
    public int Scale { get; init; } = 4;
    public bool ForceDMG { get; init; }

    public static FrontendOptions Parse(string[] args)
    {
        if (args.Length == 0)
        {
            throw new ArgumentException("A ROM path is required.");
        }

        string? romPath = null;
        string? bootROMPath = null;
        string? saveDirectory = null;
        var scale = 4;
        var forceDMG = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
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
        bootROMPath = bootROMPath == null ? null : Path.GetFullPath(bootROMPath);
        saveDirectory = saveDirectory == null
            ? Path.GetDirectoryName(romPath) ?? Directory.GetCurrentDirectory()
            : Path.GetFullPath(saveDirectory);

        if (!File.Exists(romPath))
        {
            throw new FileNotFoundException("ROM file not found.", romPath);
        }

        if (bootROMPath != null && !File.Exists(bootROMPath))
        {
            throw new FileNotFoundException("Boot ROM file not found.", bootROMPath);
        }

        Directory.CreateDirectory(saveDirectory);

        return new FrontendOptions
        {
            ROMPath = romPath,
            BootROMPath = bootROMPath,
            SaveDirectory = saveDirectory,
            Scale = scale,
            ForceDMG = forceDMG
        };
    }

    public const string Usage =
        "Usage: dotnet run --project GBZEmuFrontend -- <rom-path> [--bootrom <path>] [--save-dir <path>] [--scale <1-10>] [--dmg]\n" +
        "\n" +
        "Controls:\n" +
        "  Arrow keys  D-pad\n" +
        "  X           A\n" +
        "  Z           B\n" +
        "  Enter       Start\n" +
        "  Right Shift Select\n" +
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
