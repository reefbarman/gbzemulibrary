namespace GBZEmuFrontend;

internal sealed class FrontendOptions
{
    public string? ROMPath { get; init; }
    public string? ROMDirectory { get; init; }
    public IReadOnlyList<string> BootROMPaths { get; init; } = [];
    public string? SGBBootROMPath { get; init; }
    public string? SGB2BootROMPath { get; init; }
    public required string SaveDirectory { get; init; }
    public int Scale { get; init; } = 4;
    public bool ForceDMG { get; init; }
    public bool ForceSGB { get; init; }
    public bool ForceSGB2 { get; init; }
    public bool SkipBootROM { get; init; }
    public bool StartPaused { get; init; }
    public bool RawFrames { get; init; }
    public bool RawColors { get; init; }

    public static FrontendOptions Parse(string[] args)
    {
        if (args.Length == 0)
        {
            throw new ArgumentException("A ROM path or --rom-dir is required.");
        }

        string? romPath = null;
        string? romDirectory = null;
        string? bootROMPath = null;
        string? bootROMDirectory = null;
        string? sgbBootROMPath = null;
        string? sgb2BootROMPath = null;
        string? saveDirectory = null;
        var scale = 4;
        var forceDMG = false;
        var forceSGB = false;
        var forceSGB2 = false;
        var skipBootROM = false;
        var startPaused = false;
        var rawFrames = false;
        var rawColors = false;

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
                case "--bootrom-dir":
                    bootROMDirectory = ReadValue(args, ref i, "--bootrom-dir");
                    break;
                case "--sgb-bootrom":
                    sgbBootROMPath = ReadValue(args, ref i, "--sgb-bootrom");
                    break;
                case "--sgb2-bootrom":
                    sgb2BootROMPath = ReadValue(args, ref i, "--sgb2-bootrom");
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
                case "--sgb":
                    forceSGB = true;
                    break;
                case "--sgb2":
                    forceSGB2 = true;
                    break;
                case "--skip-bios":
                    skipBootROM = true;
                    break;
                case "--paused":
                    startPaused = true;
                    break;
                case "--raw-frames":
                    rawFrames = true;
                    break;
                case "--raw-colors":
                    rawColors = true;
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

        if ((forceDMG ? 1 : 0) + (forceSGB ? 1 : 0) + (forceSGB2 ? 1 : 0) > 1)
        {
            throw new ArgumentException("--dmg, --sgb, and --sgb2 are mutually exclusive.");
        }

        romPath = romPath == null ? null : Path.GetFullPath(romPath);
        romDirectory = romDirectory == null ? null : Path.GetFullPath(romDirectory);
        bootROMPath = bootROMPath == null ? null : Path.GetFullPath(bootROMPath);
        bootROMDirectory = bootROMDirectory == null ? null : Path.GetFullPath(bootROMDirectory);
        if (bootROMPath != null && Directory.Exists(bootROMPath))
        {
            bootROMDirectory ??= bootROMPath;
        }
        sgbBootROMPath = ResolveExplicitBootROM(sgbBootROMPath, "SGB");
        sgb2BootROMPath = ResolveExplicitBootROM(sgb2BootROMPath, "SGB2");

        if (bootROMDirectory != null && Directory.Exists(bootROMDirectory))
        {
            sgbBootROMPath ??= FindFirstCandidate(bootROMDirectory, SgbBootRomNames, DmgBootRomSize);
            sgb2BootROMPath ??= FindFirstCandidate(bootROMDirectory, Sgb2BootRomNames, DmgBootRomSize);
        }
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

        Directory.CreateDirectory(saveDirectory);

        return new FrontendOptions
        {
            ROMPath = romPath,
            ROMDirectory = romDirectory,
            BootROMPaths = ResolveBootROMs(bootROMPath, bootROMDirectory),
            SGBBootROMPath = sgbBootROMPath,
            SGB2BootROMPath = sgb2BootROMPath,
            SaveDirectory = saveDirectory,
            Scale = scale,
            ForceDMG = forceDMG,
            ForceSGB = forceSGB,
            ForceSGB2 = forceSGB2,
            SkipBootROM = skipBootROM,
            StartPaused = startPaused,
            RawFrames = rawFrames,
            RawColors = rawColors
        };
    }

    public const string Usage =
        "Usage: dotnet run --project GBZEmuFrontend -- (<rom-path> | --rom-dir <path>) [options]\n" +
        "\n" +
        "Options:\n" +
        "  --rom-dir <path>  Select a ROM from this directory\n" +
        "  --bootrom <path>      Boot ROM image; overrides a directory match of its type\n" +
        "  --bootrom-dir <path>  Directory searched for boot ROMs by common names\n" +
        "                        (dmg_boot/dmg_bios/gb_bios and cgb_boot/cgb_bios/\n" +
        "                        gbc_bios .bin); the built-in GBZEmu boot ROMs fill any\n" +
        "                        slot without an external image\n" +
        "  --sgb-bootrom <path>  Explicit 256-byte SGB boot ROM\n" +
        "  --sgb2-bootrom <path> Explicit 256-byte SGB2 boot ROM\n" +
        "  --save-dir <path> Save directory\n" +
        "  --scale <1-10>    Integer window scale\n" +
        "  --dmg             Force DMG mode\n" +
        "  --sgb             Run on Super Game Boy hardware with border output\n" +
        "  --sgb2            Run on Super Game Boy 2 hardware with border output\n" +
        "  --skip-bios       Skip the boot ROM animation entirely\n" +
        "  --paused          Start emulation paused\n" +
        "  --raw-frames      Disable LCD frame blending for raw framebuffer inspection\n" +
        "  --raw-colors      Disable CGB Modern Balanced color correction\n" +
        "\n" +
        "Controls:\n" +
        "  Arrow keys  D-pad / ROM selection\n" +
        "  X           A\n" +
        "  Z           B\n" +
        "  Enter       Start / select ROM\n" +
        "  Right Shift Select\n" +
        "  P           Pause or resume\n" +
        "  N           Step one frame; hold to repeat while paused\n" +
        "  F5 / F8     Quick-save / quick-load the current ROM\n" +
        "  R           Hold to rewind retained checkpoints\n" +
        "  Tab         Hold for 4x fast-forward\n" +
        "\n" +
        "Controller:\n" +
        "  D-pad/stick       D-pad\n" +
        "  East / South      A / B\n" +
        "  Start / Back      Start / Select\n" +
        "  North / West      Quick-save / quick-load\n" +
        "  Left/Right bumper Rewind / fast-forward\n" +
        "  Stick clicks      Pause / frame-step\n" +
        "  Escape      Quit";

    // DMG and CGB boot ROM images have exact hardware sizes; the library slots
    // loaded images by size the same way.
    private const int DmgBootRomSize = 256;
    private const int GbcBootRomSize = 2304;

    // Common file names for dumped or replacement boot ROMs, searched in order.
    private static readonly string[] DmgBootRomNames = ["dmg_boot.bin", "dmg_bios.bin", "gb_bios.bin", "dmg.bin"];
    private static readonly string[] GbcBootRomNames = ["cgb_boot.bin", "cgb_bios.bin", "gbc_bios.bin", "cgb.bin"];
    private static readonly string[] SgbBootRomNames = ["sgb_boot.bin", "sgb_bios.bin", "sgb.bin"];
    private static readonly string[] Sgb2BootRomNames = ["sgb2_boot.bin", "sgb2_bios.bin", "sgb2.bin"];

    /// <summary>
    /// Resolves --bootrom/--bootrom-dir into the image files to load. The
    /// directory contributes the first existing, correctly sized file per common
    /// name list; an explicit file loads last so it wins its size slot. Missing
    /// paths are not fatal; the library's built-in GBZEmu boot ROMs cover any
    /// slot left unfilled.
    /// </summary>
    private static List<string> ResolveBootROMs(string? bootROMPath, string? bootROMDirectory)
    {
        var paths = new List<string>();

        // Accept a directory passed to --bootrom as a directory search too.
        if (bootROMPath != null && Directory.Exists(bootROMPath))
        {
            bootROMDirectory ??= bootROMPath;
            bootROMPath = null;
        }

        if (bootROMDirectory != null)
        {
            if (Directory.Exists(bootROMDirectory))
            {
                AddFirstCandidate(paths, bootROMDirectory, DmgBootRomNames, DmgBootRomSize);
                AddFirstCandidate(paths, bootROMDirectory, GbcBootRomNames, GbcBootRomSize);
            }
            else
            {
                Console.WriteLine($"Boot ROM directory not found at {bootROMDirectory}; using the built-in GBZEmu boot ROMs.");
            }
        }

        if (bootROMPath != null)
        {
            if (!File.Exists(bootROMPath))
            {
                Console.WriteLine($"Boot ROM not found at {bootROMPath}; using the built-in GBZEmu boot ROM for its slot.");
            }
            else if (!IsBootRomSized(bootROMPath))
            {
                throw new ArgumentException(
                    $"Boot ROM must be a {DmgBootRomSize}-byte DMG image or a {GbcBootRomSize}-byte CGB image: {bootROMPath}");
            }
            else
            {
                paths.Add(bootROMPath);
            }
        }

        foreach (var path in paths)
        {
            Console.WriteLine($"Using external boot ROM: {path}");
        }

        return paths;
    }

    private static void AddFirstCandidate(List<string> paths, string directory, string[] names, int expectedSize)
    {
        foreach (var name in names)
        {
            var candidate = Path.Combine(directory, name);
            if (!File.Exists(candidate))
            {
                continue;
            }

            if (new FileInfo(candidate).Length != expectedSize)
            {
                Console.WriteLine($"Ignoring {candidate}: expected a {expectedSize}-byte image.");
                continue;
            }

            paths.Add(candidate);
            return;
        }
    }

    private static string? FindFirstCandidate(string directory, string[] names, int expectedSize)
    {
        foreach (var name in names)
        {
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate) && new FileInfo(candidate).Length == expectedSize)
            {
                Console.WriteLine($"Using external boot ROM: {candidate}");
                return candidate;
            }
        }

        return null;
    }

    private static string? ResolveExplicitBootROM(string? path, string model)
    {
        if (path == null)
        {
            return null;
        }

        path = Path.GetFullPath(path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"{model} boot ROM not found.", path);
        }
        if (new FileInfo(path).Length != DmgBootRomSize)
        {
            throw new ArgumentException($"{model} boot ROM must be exactly {DmgBootRomSize} bytes: {path}");
        }

        Console.WriteLine($"Using external {model} boot ROM: {path}");
        return path;
    }

    private static bool IsBootRomSized(string path)
    {
        var length = new FileInfo(path).Length;
        return length == DmgBootRomSize || length == GbcBootRomSize;
    }

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
