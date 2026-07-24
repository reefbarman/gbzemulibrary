namespace GBZEmuFrontend;

/// <summary>
/// Defines the frontend's persisted video-filter selection independently from emulator state.
/// </summary>
internal sealed class VideoFilterSettings
{
    public static VideoFilterSettings CompatibilityDefault { get; } = new(
        VideoFilterPresetCatalog.CustomPresetId,
        VideoFilterPresetCatalog.ModernBalancedColorProfileId,
        VideoFilterPresetCatalog.ClassicPersistenceId,
        VideoFilterPresetCatalog.OffEffectId,
        VideoFilterPresetCatalog.OffEffectId,
        4);

    /// <summary>
    /// Creates a video-filter configuration from stable serialized identifiers.
    /// </summary>
    public VideoFilterSettings(
        string presetId,
        string cgbColorProfile,
        string persistence,
        string pixelGrid,
        string glare,
        int integerScale)
    {
        PresetId = presetId;
        CgbColorProfile = cgbColorProfile;
        Persistence = persistence;
        PixelGrid = pixelGrid;
        Glare = glare;
        IntegerScale = integerScale;
    }

    public string PresetId { get; }
    public string CgbColorProfile { get; }
    public string Persistence { get; }
    public string PixelGrid { get; }
    public string Glare { get; }
    public int IntegerScale { get; }

    /// <summary>
    /// Expands a named preset while retaining the independent viewport scale.
    /// </summary>
    public VideoFilterSettings WithPreset(string presetId)
    {
        return VideoFilterPresetCatalog.ApplyPreset(presetId, this);
    }

    /// <summary>
    /// Returns custom settings with the selected CGB color profile.
    /// </summary>
    public VideoFilterSettings WithCgbColorProfile(string cgbColorProfile)
    {
        var expanded = Normalize();
        return AsCustom(new VideoFilterSettings(
            CustomPresetId,
            cgbColorProfile,
            expanded.Persistence,
            expanded.PixelGrid,
            expanded.Glare,
            expanded.IntegerScale));
    }

    /// <summary>
    /// Returns custom settings with the selected temporal-persistence level.
    /// </summary>
    public VideoFilterSettings WithPersistence(string persistence)
    {
        var expanded = Normalize();
        return AsCustom(new VideoFilterSettings(
            CustomPresetId,
            expanded.CgbColorProfile,
            persistence,
            expanded.PixelGrid,
            expanded.Glare,
            expanded.IntegerScale));
    }

    /// <summary>
    /// Returns custom settings with the selected pixel-grid level.
    /// </summary>
    public VideoFilterSettings WithPixelGrid(string pixelGrid)
    {
        var expanded = Normalize();
        return AsCustom(new VideoFilterSettings(
            CustomPresetId,
            expanded.CgbColorProfile,
            expanded.Persistence,
            pixelGrid,
            expanded.Glare,
            expanded.IntegerScale));
    }

    /// <summary>
    /// Returns custom settings with the selected glare level.
    /// </summary>
    public VideoFilterSettings WithGlare(string glare)
    {
        var expanded = Normalize();
        return AsCustom(new VideoFilterSettings(
            CustomPresetId,
            expanded.CgbColorProfile,
            expanded.Persistence,
            expanded.PixelGrid,
            glare,
            expanded.IntegerScale));
    }

    /// <summary>
    /// Returns settings with a clamped integer viewport scale without changing the visual preset.
    /// </summary>
    public VideoFilterSettings WithIntegerScale(int integerScale)
    {
        return new VideoFilterSettings(
            PresetId,
            CgbColorProfile,
            Persistence,
            PixelGrid,
            Glare,
            integerScale).Normalize();
    }

    /// <summary>
    /// Replaces unsupported persisted values with compatibility-safe values.
    /// </summary>
    public VideoFilterSettings Normalize()
    {
        var fallback = CompatibilityDefault;
        var normalized = new VideoFilterSettings(
            VideoFilterPresetCatalog.IsPresetId(PresetId) ? PresetId : VideoFilterPresetCatalog.CustomPresetId,
            VideoFilterPresetCatalog.IsColorProfileId(CgbColorProfile)
                ? CgbColorProfile
                : fallback.CgbColorProfile,
            VideoFilterPresetCatalog.IsPersistenceId(Persistence)
                ? Persistence
                : fallback.Persistence,
            VideoFilterPresetCatalog.IsEffectId(PixelGrid)
                ? PixelGrid
                : fallback.PixelGrid,
            VideoFilterPresetCatalog.IsEffectId(Glare)
                ? Glare
                : fallback.Glare,
            Math.Clamp(IntegerScale, 1, 10));

        if (normalized.PresetId == CustomPresetId)
        {
            return normalized;
        }

        var expanded = VideoFilterPresetCatalog.ApplyPreset(normalized.PresetId, normalized);
        return expanded;
    }

    private static string CustomPresetId => VideoFilterPresetCatalog.CustomPresetId;

    private static VideoFilterSettings AsCustom(VideoFilterSettings settings)
    {
        var normalized = settings.Normalize();
        return new VideoFilterSettings(
            CustomPresetId,
            normalized.CgbColorProfile,
            normalized.Persistence,
            normalized.PixelGrid,
            normalized.Glare,
            normalized.IntegerScale);
    }
}
