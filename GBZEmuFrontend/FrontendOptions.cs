using GBZEmuLibrary;

namespace GBZEmuFrontend;

/// <summary>
/// Reports persisted and effective video settings together with one-run CLI override ownership.
/// </summary>
internal sealed class ResolvedVideoFilterSettings
{
    public ResolvedVideoFilterSettings(
        VideoFilterSettings persisted,
        VideoFilterSettings effective,
        bool presetOverridden,
        bool colorOverridden,
        bool persistenceOverridden,
        bool pixelGridOverridden,
        bool glareOverridden,
        bool scaleOverridden)
    {
        Persisted = persisted ?? throw new ArgumentNullException(nameof(persisted));
        Effective = effective ?? throw new ArgumentNullException(nameof(effective));
        PresetOverridden = presetOverridden;
        ColorOverridden = colorOverridden;
        PersistenceOverridden = persistenceOverridden;
        PixelGridOverridden = pixelGridOverridden;
        GlareOverridden = glareOverridden;
        ScaleOverridden = scaleOverridden;
    }

    public VideoFilterSettings Persisted { get; }
    public VideoFilterSettings Effective { get; }
    public bool PresetOverridden { get; }
    public bool ColorOverridden { get; }
    public bool PersistenceOverridden { get; }
    public bool PixelGridOverridden { get; }
    public bool GlareOverridden { get; }
    public bool ScaleOverridden { get; }
}

/// <summary>
/// Parses frontend launch options while retaining presentation flags as one-run overrides.
/// </summary>
internal sealed class FrontendOptions
{
    public string? ROMPath { get; init; }
    public string? ROMDirectory { get; init; }
    public string? BootROMPath { get; init; }
    public HardwareModel? HardwareModel { get; init; }
    public required string SaveDirectory { get; init; }
    public int? ScaleOverride { get; init; }
    public string? FilterPresetOverride { get; init; }
    public bool SkipBootROM { get; init; }
    public bool StartPaused { get; init; }
    public bool RawFrames { get; init; }
    public bool RawColors { get; init; }

    /// <summary>
    /// Retains the legacy parser-facing scale while runtime presentation uses resolved settings.
    /// </summary>
    public int Scale => ScaleOverride ?? VideoFilterSettings.CompatibilityDefault.IntegerScale;

    /// <summary>
    /// Parses paths, hardware policy, and explicit one-run presentation overrides.
    /// </summary>
    public static FrontendOptions Parse(string[] args)
    {
        if (args.Length == 0)
        {
            throw new ArgumentException("A ROM path or --rom-dir is required.");
        }

        string? romPath = null;
        string? romDirectory = null;
        string? bootROMPath = null;
        HardwareModel? hardwareModel = null;
        string? saveDirectory = null;
        int? scaleOverride = null;
        string? filterPresetOverride = null;
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
                case "--model":
                    hardwareModel = ParseHardwareModel(ReadValue(args, ref i, "--model"));
                    break;
                case "--save-dir":
                    saveDirectory = ReadValue(args, ref i, "--save-dir");
                    break;
                case "--scale":
                    var value = ReadValue(args, ref i, "--scale");
                    if (!int.TryParse(value, out var scale) || scale < 1 || scale > 10)
                    {
                        throw new ArgumentException("--scale must be an integer from 1 to 10.");
                    }

                    scaleOverride = scale;
                    break;
                case "--filter-preset":
                    filterPresetOverride = ParseFilterPreset(ReadValue(args, ref i, "--filter-preset"));
                    break;
                case "--skip-bootrom":
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

        if (bootROMPath != null && skipBootROM)
        {
            throw new ArgumentException("--bootrom and --skip-bootrom are mutually exclusive.");
        }

        romPath = romPath == null ? null : Path.GetFullPath(romPath);
        if (romPath != null && !IsLaunchTargetPath(romPath))
        {
            throw new ArgumentException("The launch target must end in .gb, .gbc, .gb.json, or .gbc.json.");
        }

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
            HardwareModel = hardwareModel,
            SaveDirectory = saveDirectory,
            ScaleOverride = scaleOverride,
            FilterPresetOverride = filterPresetOverride,
            SkipBootROM = skipBootROM,
            StartPaused = startPaused,
            RawFrames = rawFrames,
            RawColors = rawColors
        };
    }

    /// <summary>
    /// Resolves persisted preferences and one-run command-line overrides in deterministic precedence order.
    /// </summary>
    public ResolvedVideoFilterSettings ResolveVideoSettings(VideoFilterSettings persisted)
    {
        ArgumentNullException.ThrowIfNull(persisted);

        var normalizedPersisted = persisted.Normalize();
        var effective = normalizedPersisted;
        var presetOverridden = FilterPresetOverride != null;
        if (FilterPresetOverride != null)
        {
            effective = effective.WithPreset(FilterPresetOverride);
        }

        if (RawColors)
        {
            effective = effective.WithCgbColorProfile(VideoFilterPresetCatalog.RawColorProfileId);
        }

        if (RawFrames)
        {
            effective = effective.WithPersistence(VideoFilterPresetCatalog.OffPersistenceId);
        }

        if (ScaleOverride.HasValue)
        {
            effective = effective.WithIntegerScale(ScaleOverride.Value);
        }

        return new ResolvedVideoFilterSettings(
            normalizedPersisted,
            effective,
            presetOverridden,
            presetOverridden || RawColors,
            presetOverridden || RawFrames,
            presetOverridden,
            presetOverridden,
            ScaleOverride.HasValue);
    }

    public const string Usage =
        "Usage: dotnet run --project GBZEmuFrontend -- (<rom-or-manifest-path> | --rom-dir <path>) [options]\n" +
        "\n" +
        "Options:\n" +
        "  --rom-dir <path>       Select a ROM from this directory and resolve its adjacent manifest\n" +
        "  <rom>.json             Launch an adjacent schema-v1 ROM manifest directly\n" +
        "  --model <model>        Hardware model: DmgB, Mgb, CgbE, Sgb2, or AgbA\n" +
        "                         Defaults to DmgB for DMG-only ROMs and CgbE otherwise\n" +
        "  --bootrom <path>       External boot ROM for the selected hardware model\n" +
        "                         The built-in boot ROM is used when this option is omitted\n" +
        "  --skip-bootrom         Skip boot ROM execution (mutually exclusive with --bootrom)\n" +
        "  --save-dir <path>      Save directory\n" +
        "  --scale <1-10>         Override the persisted integer window scale for this run\n" +
        "  --filter-preset <id>   Override with raw, clean, lcd, or lcd-reflective for this run\n" +
        "  --paused               Start emulation paused\n" +
        "  --raw-frames           Disable LCD frame blending for this run\n" +
        "  --raw-colors           Disable CGB Modern Balanced color correction for this run\n" +
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

    private static string ParseFilterPreset(string value)
    {
        return value switch
        {
            VideoFilterPresetCatalog.RawPresetId => value,
            VideoFilterPresetCatalog.CleanPresetId => value,
            VideoFilterPresetCatalog.LcdPresetId => value,
            VideoFilterPresetCatalog.ReflectiveLcdPresetId => value,
            _ => throw new ArgumentException(
                $"Unknown filter preset: {value}. Expected raw, clean, lcd, or lcd-reflective.")
        };
    }

    private static bool IsLaunchTargetPath(string path)
    {
        return path.EndsWith(".gb", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".gbc", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".gb.json", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".gbc.json", StringComparison.OrdinalIgnoreCase);
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
