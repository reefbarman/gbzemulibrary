using System.Text.Json;

namespace GBZEmuFrontend;

/// <summary>
/// Reports a normalized frontend settings load and any non-fatal storage diagnostic.
/// </summary>
internal sealed class FrontendSettingsLoadResult
{
    /// <summary>
    /// Creates a load result containing normalized settings and an optional warning.
    /// </summary>
    public FrontendSettingsLoadResult(VideoFilterSettings settings, string? diagnostic)
    {
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        Diagnostic = diagnostic;
    }

    public VideoFilterSettings Settings { get; }
    public string? Diagnostic { get; }
}

/// <summary>
/// Persists schema-versioned frontend preferences beneath the platform application-data root.
/// </summary>
internal sealed class FrontendSettingsStore
{
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    /// <summary>
    /// Creates a store rooted at the platform-local application-data directory supplied by the host.
    /// </summary>
    public FrontendSettingsStore(string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            throw new ArgumentException("An application-data directory is required.", nameof(baseDirectory));
        }

        SettingsPath = Path.Combine(
            Path.GetFullPath(baseDirectory),
            "GBZEmu",
            "GBZEmuFrontend",
            "settings.json");
    }

    public string SettingsPath { get; }

    /// <summary>
    /// Loads normalized settings, falling back without failing frontend startup.
    /// </summary>
    public FrontendSettingsLoadResult Load()
    {
        if (!File.Exists(SettingsPath))
        {
            return new FrontendSettingsLoadResult(VideoFilterSettings.CompatibilityDefault, null);
        }

        try
        {
            var stored = JsonSerializer.Deserialize<StoredFrontendSettings>(
                File.ReadAllText(SettingsPath),
                SerializerOptions);
            if (stored?.SchemaVersion != SchemaVersion || stored.Video == null)
            {
                return Fallback("Frontend settings use an unsupported schema and were ignored.");
            }

            var fallback = VideoFilterSettings.CompatibilityDefault;
            var video = stored.Video;
            var settings = new VideoFilterSettings(
                video.PresetId ?? fallback.PresetId,
                video.CgbColorProfile ?? fallback.CgbColorProfile,
                video.Persistence ?? fallback.Persistence,
                video.PixelGrid ?? fallback.PixelGrid,
                video.Glare ?? fallback.Glare,
                video.IntegerScale ?? fallback.IntegerScale);
            var normalized = settings.Normalize();
            var diagnostic = HasInvalidValue(video)
                ? "Some frontend video settings were invalid and were reset."
                : null;
            return new FrontendSettingsLoadResult(normalized, diagnostic);
        }
        catch (Exception exception) when (exception is JsonException
            or IOException
            or UnauthorizedAccessException
            or NotSupportedException)
        {
            return Fallback($"Frontend settings could not be loaded and were ignored: {exception.Message}");
        }
    }

    /// <summary>
    /// Atomically replaces the persisted settings with normalized values.
    /// </summary>
    public void Save(VideoFilterSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var normalized = settings.Normalize();
        var stored = new StoredFrontendSettings
        {
            SchemaVersion = SchemaVersion,
            Video = new StoredVideoFilterSettings
            {
                PresetId = normalized.PresetId,
                CgbColorProfile = normalized.CgbColorProfile,
                Persistence = normalized.Persistence,
                PixelGrid = normalized.PixelGrid,
                Glare = normalized.Glare,
                IntegerScale = normalized.IntegerScale
            }
        };

        var directory = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = $"{SettingsPath}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(stored, SerializerOptions));
            File.Move(temporaryPath, SettingsPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static FrontendSettingsLoadResult Fallback(string diagnostic)
    {
        return new FrontendSettingsLoadResult(VideoFilterSettings.CompatibilityDefault, diagnostic);
    }

    private static bool HasInvalidValue(StoredVideoFilterSettings video)
    {
        return video.PresetId != null && !VideoFilterPresetCatalog.IsPresetId(video.PresetId)
            || video.CgbColorProfile != null && !VideoFilterPresetCatalog.IsColorProfileId(video.CgbColorProfile)
            || video.Persistence != null && !VideoFilterPresetCatalog.IsPersistenceId(video.Persistence)
            || video.PixelGrid != null && !VideoFilterPresetCatalog.IsEffectId(video.PixelGrid)
            || video.Glare != null && !VideoFilterPresetCatalog.IsEffectId(video.Glare)
            || video.IntegerScale is < 1 or > 10;
    }

    private sealed class StoredFrontendSettings
    {
        public int SchemaVersion { get; set; }
        public StoredVideoFilterSettings? Video { get; set; }
    }

    private sealed class StoredVideoFilterSettings
    {
        public string? PresetId { get; set; }
        public string? CgbColorProfile { get; set; }
        public string? Persistence { get; set; }
        public string? PixelGrid { get; set; }
        public string? Glare { get; set; }
        public int? IntegerScale { get; set; }
    }
}
