using GBZEmuFrontend;

namespace GBZEmuTests;

/// <summary>
/// Verifies frontend video-filter presets, customization, normalization, and schema-versioned persistence.
/// </summary>
public sealed class FrontendVideoFilterSettingsTests
{
    /// <summary>
    /// Verifies the default preserves the frontend's pre-settings color, persistence, effects, and scale behavior.
    /// </summary>
    [Fact]
    public void CompatibilityDefaultPreservesExistingFrontendBehavior()
    {
        AssertSettings(
            VideoFilterSettings.CompatibilityDefault,
            "custom",
            "modern-balanced",
            "classic",
            "off",
            "off",
            4);
    }

    /// <summary>
    /// Verifies every stable preset identifier expands to its complete semantic filter configuration.
    /// </summary>
    [Theory]
    [InlineData("raw", "raw", "off", "off", "off")]
    [InlineData("clean", "modern-balanced", "off", "off", "off")]
    [InlineData("lcd", "modern-balanced", "classic", "subtle", "subtle")]
    [InlineData("lcd-reflective", "modern-balanced", "classic", "subtle", "strong")]
    [InlineData("custom", "modern-balanced", "classic", "off", "off")]
    public void NamedPresetsExpandToExpectedComponents(
        string presetId,
        string cgbColorProfile,
        string persistence,
        string pixelGrid,
        string glare)
    {
        var settings = VideoFilterSettings.CompatibilityDefault.WithPreset(presetId);

        AssertSettings(settings, presetId, cgbColorProfile, persistence, pixelGrid, glare, 4);
        Assert.Equal(presetId, VideoFilterPresetCatalog.MatchPreset(settings));
    }

    /// <summary>
    /// Verifies changing an individual filter component leaves the named preset untouched and marks the result custom.
    /// </summary>
    [Fact]
    public void ComponentEditsCreateCustomSettings()
    {
        var preset = VideoFilterSettings.CompatibilityDefault.WithPreset("lcd");

        Assert.Equal("custom", preset.WithCgbColorProfile("raw").PresetId);
        Assert.Equal("custom", preset.WithPersistence("subtle").PresetId);
        Assert.Equal("custom", preset.WithPixelGrid("strong").PresetId);
        Assert.Equal("custom", preset.WithGlare("off").PresetId);
        AssertSettings(preset, "lcd", "modern-balanced", "classic", "subtle", "subtle", 4);
    }

    /// <summary>
    /// Verifies invalid identifiers fall back to compatibility-safe values and scale is clamped to its supported range.
    /// </summary>
    [Fact]
    public void NormalizeFallsBackFromInvalidValues()
    {
        var settings = new VideoFilterSettings(
            "unknown-preset",
            "unknown-profile",
            "unknown-persistence",
            "unknown-grid",
            "unknown-glare",
            -4);

        var normalized = settings.Normalize();

        AssertSettings(normalized, "custom", "modern-balanced", "classic", "off", "off", 1);
        Assert.Equal(10, normalized.WithIntegerScale(99).IntegerScale);
    }

    /// <summary>
    /// Verifies loading an absent settings file returns compatibility defaults without reporting a failure.
    /// </summary>
    [Fact]
    public void MissingSettingsLoadCompatibilityDefaultWithoutDiagnostic()
    {
        using var directory = new TemporaryDirectory();
        var store = new FrontendSettingsStore(directory.Path);

        var result = store.Load();

        AssertSettings(result.Settings, "custom", "modern-balanced", "classic", "off", "off", 4);
        Assert.Null(result.Diagnostic);
    }

    /// <summary>
    /// Verifies malformed JSON is ignored non-fatally with a visible diagnostic and compatibility fallback.
    /// </summary>
    [Fact]
    public void CorruptSettingsLoadCompatibilityDefaultWithDiagnostic()
    {
        using var directory = new TemporaryDirectory();
        var store = new FrontendSettingsStore(directory.Path);
        WriteSettingsFile(store, "{ not valid JSON");

        var result = store.Load();

        AssertSettings(result.Settings, "custom", "modern-balanced", "classic", "off", "off", 4);
        Assert.False(string.IsNullOrWhiteSpace(result.Diagnostic));
    }

    /// <summary>
    /// Verifies unsupported persisted schemas are ignored non-fatally with a visible diagnostic.
    /// </summary>
    [Fact]
    public void UnsupportedSchemaLoadsCompatibilityDefaultWithDiagnostic()
    {
        using var directory = new TemporaryDirectory();
        var store = new FrontendSettingsStore(directory.Path);
        WriteSettingsFile(store, """
            {
              "schemaVersion": 2,
              "video": {
                "presetId": "raw",
                "cgbColorProfile": "raw",
                "persistence": "off",
                "pixelGrid": "off",
                "glare": "off",
                "integerScale": 8
              }
            }
            """);

        var result = store.Load();

        AssertSettings(result.Settings, "custom", "modern-balanced", "classic", "off", "off", 4);
        Assert.False(string.IsNullOrWhiteSpace(result.Diagnostic));
    }

    /// <summary>
    /// Verifies invalid schema-v1 values are normalized without preventing startup and produce a diagnostic.
    /// </summary>
    [Fact]
    public void InvalidSchemaValuesNormalizeWithDiagnostic()
    {
        using var directory = new TemporaryDirectory();
        var store = new FrontendSettingsStore(directory.Path);
        WriteSettingsFile(store, """
            {
              "schemaVersion": 1,
              "video": {
                "presetId": "unknown-preset",
                "cgbColorProfile": "unknown-profile",
                "persistence": "unknown-persistence",
                "pixelGrid": "unknown-grid",
                "glare": "unknown-glare",
                "integerScale": 99
              }
            }
            """);

        var result = store.Load();

        AssertSettings(result.Settings, "custom", "modern-balanced", "classic", "off", "off", 10);
        Assert.False(string.IsNullOrWhiteSpace(result.Diagnostic));
    }

    /// <summary>
    /// Verifies all custom filter components survive a schema-v1 save and load round trip.
    /// </summary>
    [Fact]
    public void SaveLoadRoundTripPreservesNormalizedSettings()
    {
        using var directory = new TemporaryDirectory();
        var store = new FrontendSettingsStore(directory.Path);
        var settings = VideoFilterSettings.CompatibilityDefault
            .WithCgbColorProfile("raw")
            .WithPersistence("subtle")
            .WithPixelGrid("strong")
            .WithGlare("subtle")
            .WithIntegerScale(7);

        store.Save(settings);
        var result = store.Load();

        AssertSettings(result.Settings, "custom", "raw", "subtle", "strong", "subtle", 7);
        Assert.Null(result.Diagnostic);
    }

    /// <summary>
    /// Verifies a subsequent save atomically replaces an existing settings file with the latest values.
    /// </summary>
    [Fact]
    public void SaveOverwritesExistingSettings()
    {
        using var directory = new TemporaryDirectory();
        var store = new FrontendSettingsStore(directory.Path);
        store.Save(VideoFilterSettings.CompatibilityDefault.WithPreset("raw").WithIntegerScale(2));

        store.Save(VideoFilterSettings.CompatibilityDefault.WithPreset("lcd-reflective").WithIntegerScale(9));
        var result = store.Load();

        AssertSettings(result.Settings, "lcd-reflective", "modern-balanced", "classic", "subtle", "strong", 9);
        Assert.Null(result.Diagnostic);
    }

    /// <summary>
    /// Verifies persistence uses the schema-v1 application-data path and leaves no staging file after success.
    /// </summary>
    [Fact]
    public void SaveUsesExpectedPathAndCleansTemporaryFile()
    {
        using var directory = new TemporaryDirectory();
        var store = new FrontendSettingsStore(directory.Path);
        var expectedPath = System.IO.Path.Combine(
            directory.Path,
            "GBZEmu",
            "GBZEmuFrontend",
            "settings.json");

        store.Save(VideoFilterSettings.CompatibilityDefault);

        Assert.Equal(expectedPath, store.SettingsPath);
        Assert.True(File.Exists(expectedPath));
        Assert.False(File.Exists($"{expectedPath}.tmp"));
    }

    private static void AssertSettings(
        VideoFilterSettings settings,
        string presetId,
        string cgbColorProfile,
        string persistence,
        string pixelGrid,
        string glare,
        int integerScale)
    {
        Assert.Equal(presetId, settings.PresetId);
        Assert.Equal(cgbColorProfile, settings.CgbColorProfile);
        Assert.Equal(persistence, settings.Persistence);
        Assert.Equal(pixelGrid, settings.PixelGrid);
        Assert.Equal(glare, settings.Glare);
        Assert.Equal(integerScale, settings.IntegerScale);
    }

    private static void WriteSettingsFile(FrontendSettingsStore store, string contents)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(store.SettingsPath)!);
        File.WriteAllText(store.SettingsPath, contents);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"gbzemu-video-filter-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
