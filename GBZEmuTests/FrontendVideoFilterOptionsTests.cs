using GBZEmuFrontend;
using GBZEmuLibrary;

namespace GBZEmuTests;

/// <summary>
/// Verifies frontend video-filter command-line parsing, override precedence, and persisted-setting isolation.
/// </summary>
public sealed class FrontendVideoFilterOptionsTests
{
    [Fact]
    public void ParseAcceptsExplicitRomManifestTarget()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"gbzemu-manifest-options-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var romPath = Path.Combine(directory, "game.gb");
        var manifestPath = $"{romPath}.json";
        File.WriteAllBytes(romPath, new byte[0x8000]);
        File.WriteAllText(manifestPath, "{\"schemaVersion\":1}");

        try
        {
            var options = FrontendOptions.Parse([manifestPath]);

            Assert.Equal(Path.GetFullPath(manifestPath), options.ROMPath);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ParseRejectsUnrelatedLaunchTargetExtension()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gbzemu-{Guid.NewGuid():N}.zip");
        File.WriteAllBytes(path, Array.Empty<byte>());

        try
        {
            var error = Assert.Throws<ArgumentException>(() => FrontendOptions.Parse([path]));

            Assert.Contains(".gb.json", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Verifies every supported command-line preset identifier is retained as a one-run override.
    /// </summary>
    [Theory]
    [InlineData("raw")]
    [InlineData("clean")]
    [InlineData("lcd")]
    [InlineData("lcd-reflective")]
    public void FilterPresetAcceptsSupportedIdentifiers(string presetId)
    {
        using var rom = TestRom.Create(0x00);

        var options = FrontendOptions.Parse([rom.Path, "--filter-preset", presetId]);

        Assert.Equal(presetId, options.FilterPresetOverride);
    }

    /// <summary>
    /// Verifies unsupported preset identifiers are rejected instead of silently becoming custom settings.
    /// </summary>
    [Theory]
    [InlineData("custom")]
    [InlineData("LCD")]
    [InlineData("crt")]
    public void FilterPresetRejectsUnsupportedIdentifiers(string presetId)
    {
        using var rom = TestRom.Create(0x00);

        var exception = Assert.Throws<ArgumentException>(
            () => FrontendOptions.Parse([rom.Path, "--filter-preset", presetId]));

        Assert.Contains("Unknown filter preset", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies a filter-preset option without its required value is rejected.
    /// </summary>
    [Fact]
    public void FilterPresetRequiresValue()
    {
        using var rom = TestRom.Create(0x00);

        var exception = Assert.Throws<ArgumentException>(
            () => FrontendOptions.Parse([rom.Path, "--filter-preset"]));

        Assert.Contains("--filter-preset requires a value", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies scale is absent unless explicitly supplied and retains an explicit supported value.
    /// </summary>
    [Fact]
    public void ScaleOverrideDistinguishesOmittedAndExplicitValues()
    {
        using var rom = TestRom.Create(0x00);

        var omitted = FrontendOptions.Parse([rom.Path]);
        var explicitScale = FrontendOptions.Parse([rom.Path, "--scale", "7"]);

        Assert.Null(omitted.ScaleOverride);
        Assert.Equal(7, explicitScale.ScaleOverride);
    }

    /// <summary>
    /// Verifies existing ROM, model, save-directory, and raw-option parsing remains intact.
    /// </summary>
    [Fact]
    public void ExistingPathModelSaveAndRawOptionsRemainSupported()
    {
        using var rom = TestRom.Create(0x00);
        var saveDirectory = Path.Combine(Path.GetTempPath(), $"gbzemu-frontend-options-{Guid.NewGuid():N}");

        try
        {
            var options = FrontendOptions.Parse(
            [
                rom.Path,
                "--model", "CgbE",
                "--save-dir", saveDirectory,
                "--raw-frames",
                "--raw-colors"
            ]);

            Assert.Equal(Path.GetFullPath(rom.Path), options.ROMPath);
            Assert.Equal(HardwareModel.CgbE, options.HardwareModel);
            Assert.Equal(Path.GetFullPath(saveDirectory), options.SaveDirectory);
            Assert.True(Directory.Exists(saveDirectory));
            Assert.True(options.RawFrames);
            Assert.True(options.RawColors);
            Assert.Null(options.FilterPresetOverride);
        }
        finally
        {
            if (Directory.Exists(saveDirectory))
            {
                Directory.Delete(saveDirectory, recursive: true);
            }
        }
    }

    /// <summary>
    /// Verifies persisted settings become effective unchanged when the command line supplies no presentation overrides.
    /// </summary>
    [Fact]
    public void PersistedSettingsAreEffectiveWithoutOverrides()
    {
        using var rom = TestRom.Create(0x00);
        var persisted = CreateCustomSettings(integerScale: 6);
        var options = FrontendOptions.Parse([rom.Path]);

        var resolved = options.ResolveVideoSettings(persisted);

        AssertSettings(resolved.Persisted, "custom", "raw", "subtle", "strong", "subtle", 6);
        AssertSettings(resolved.Effective, "custom", "raw", "subtle", "strong", "subtle", 6);
        AssertOverrideMask(resolved, false, false, false, false, false, false);
    }

    /// <summary>
    /// Verifies a full preset replaces all persisted visual components, preserves persisted scale, and owns every visual row.
    /// </summary>
    [Fact]
    public void FullPresetOverridesAllVisualRowsAndPreservesScale()
    {
        using var rom = TestRom.Create(0x00);
        var persisted = CreateCustomSettings(integerScale: 6);
        var options = FrontendOptions.Parse([rom.Path, "--filter-preset", "lcd-reflective"]);

        var resolved = options.ResolveVideoSettings(persisted);

        AssertSettings(resolved.Persisted, "custom", "raw", "subtle", "strong", "subtle", 6);
        AssertSettings(resolved.Effective, "lcd-reflective", "modern-balanced", "classic", "subtle", "strong", 6);
        AssertOverrideMask(resolved, true, true, true, true, true, false);
    }

    /// <summary>
    /// Verifies component raw flags take precedence over a full preset regardless of command-line argument order.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RawComponentFlagsWinOverPresetInEitherArgumentOrder(bool rawFlagsFirst)
    {
        using var rom = TestRom.Create(0x00);
        var args = rawFlagsFirst
            ? new[] { rom.Path, "--raw-frames", "--raw-colors", "--filter-preset", "lcd" }
            : new[] { rom.Path, "--filter-preset", "lcd", "--raw-frames", "--raw-colors" };
        var options = FrontendOptions.Parse(args);

        var resolved = options.ResolveVideoSettings(CreateCustomSettings(integerScale: 5));

        AssertSettings(resolved.Effective, "custom", "raw", "off", "subtle", "subtle", 5);
        AssertOverrideMask(resolved, true, true, true, true, true, false);
    }

    /// <summary>
    /// Verifies each legacy raw flag owns and changes only its corresponding visual component row.
    /// </summary>
    [Fact]
    public void RawFlagsMarkOnlyTheirOwnComponentRows()
    {
        using var rom = TestRom.Create(0x00);
        var persisted = VideoFilterSettings.CompatibilityDefault.WithPreset("lcd-reflective");

        var rawColors = FrontendOptions.Parse([rom.Path, "--raw-colors"])
            .ResolveVideoSettings(persisted);
        var rawFrames = FrontendOptions.Parse([rom.Path, "--raw-frames"])
            .ResolveVideoSettings(persisted);

        AssertSettings(rawColors.Effective, "custom", "raw", "classic", "subtle", "strong", 4);
        AssertOverrideMask(rawColors, false, true, false, false, false, false);
        AssertSettings(rawFrames.Effective, "custom", "modern-balanced", "off", "subtle", "strong", 4);
        AssertOverrideMask(rawFrames, false, false, true, false, false, false);
    }

    /// <summary>
    /// Verifies a scale override is independent from preset semantics and owns only the scale row.
    /// </summary>
    [Fact]
    public void ScaleOnlyOverridePreservesPresetAndMarksOnlyScale()
    {
        using var rom = TestRom.Create(0x00);
        var persisted = VideoFilterSettings.CompatibilityDefault
            .WithPreset("lcd-reflective")
            .WithIntegerScale(3);
        var options = FrontendOptions.Parse([rom.Path, "--scale", "8"]);

        var resolved = options.ResolveVideoSettings(persisted);

        AssertSettings(resolved.Effective, "lcd-reflective", "modern-balanced", "classic", "subtle", "strong", 8);
        AssertOverrideMask(resolved, false, false, false, false, false, true);
    }

    /// <summary>
    /// Verifies resolving one-run overrides neither mutates the supplied object nor writes overrides into persisted settings.
    /// </summary>
    [Fact]
    public void ResolutionDoesNotMutateOrPersistOverrides()
    {
        using var rom = TestRom.Create(0x00);
        var persisted = CreateCustomSettings(integerScale: 6);
        var options = FrontendOptions.Parse(
            [rom.Path, "--filter-preset", "lcd", "--raw-colors", "--raw-frames", "--scale", "9"]);

        var resolved = options.ResolveVideoSettings(persisted);

        AssertSettings(persisted, "custom", "raw", "subtle", "strong", "subtle", 6);
        AssertSettings(resolved.Persisted, "custom", "raw", "subtle", "strong", "subtle", 6);
        AssertSettings(resolved.Effective, "custom", "raw", "off", "subtle", "subtle", 9);
        Assert.NotSame(persisted, resolved.Effective);
    }

    private static VideoFilterSettings CreateCustomSettings(int integerScale)
    {
        return new VideoFilterSettings(
            "custom",
            "raw",
            "subtle",
            "strong",
            "subtle",
            integerScale);
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

    private static void AssertOverrideMask(
        ResolvedVideoFilterSettings resolved,
        bool preset,
        bool color,
        bool persistence,
        bool pixelGrid,
        bool glare,
        bool scale)
    {
        Assert.Equal(preset, resolved.PresetOverridden);
        Assert.Equal(color, resolved.ColorOverridden);
        Assert.Equal(persistence, resolved.PersistenceOverridden);
        Assert.Equal(pixelGrid, resolved.PixelGridOverridden);
        Assert.Equal(glare, resolved.GlareOverridden);
        Assert.Equal(scale, resolved.ScaleOverridden);
    }
}
