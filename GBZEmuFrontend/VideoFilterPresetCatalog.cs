namespace GBZEmuFrontend;

/// <summary>
/// Defines stable video-filter identifiers and expands named presets into semantic controls.
/// </summary>
internal static class VideoFilterPresetCatalog
{
    public const string RawPresetId = "raw";
    public const string CleanPresetId = "clean";
    public const string LcdPresetId = "lcd";
    public const string ReflectiveLcdPresetId = "lcd-reflective";
    public const string CustomPresetId = "custom";

    public const string RawColorProfileId = "raw";
    public const string ModernBalancedColorProfileId = "modern-balanced";

    public const string OffPersistenceId = "off";
    public const string SubtlePersistenceId = "subtle";
    public const string ClassicPersistenceId = "classic";

    public const string OffEffectId = "off";
    public const string SubtleEffectId = "subtle";
    public const string StrongEffectId = "strong";

    /// <summary>
    /// Expands a named preset while preserving the current integer scale.
    /// </summary>
    public static VideoFilterSettings ApplyPreset(string presetId, VideoFilterSettings current)
    {
        ArgumentNullException.ThrowIfNull(current);

        return presetId switch
        {
            RawPresetId => Create(
                RawPresetId,
                RawColorProfileId,
                OffPersistenceId,
                OffEffectId,
                OffEffectId,
                current.IntegerScale),
            CleanPresetId => Create(
                CleanPresetId,
                ModernBalancedColorProfileId,
                OffPersistenceId,
                OffEffectId,
                OffEffectId,
                current.IntegerScale),
            LcdPresetId => Create(
                LcdPresetId,
                ModernBalancedColorProfileId,
                ClassicPersistenceId,
                SubtleEffectId,
                SubtleEffectId,
                current.IntegerScale),
            ReflectiveLcdPresetId => Create(
                ReflectiveLcdPresetId,
                ModernBalancedColorProfileId,
                ClassicPersistenceId,
                SubtleEffectId,
                StrongEffectId,
                current.IntegerScale),
            CustomPresetId => Create(
                CustomPresetId,
                current.CgbColorProfile,
                current.Persistence,
                current.PixelGrid,
                current.Glare,
                current.IntegerScale),
            _ => ApplyPreset(CustomPresetId, current)
        };
    }

    /// <summary>
    /// Returns the named preset matching the semantic controls, or <c>custom</c> when none match.
    /// </summary>
    public static string MatchPreset(VideoFilterSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (Matches(settings, RawColorProfileId, OffPersistenceId, OffEffectId, OffEffectId))
        {
            return RawPresetId;
        }

        if (Matches(settings, ModernBalancedColorProfileId, OffPersistenceId, OffEffectId, OffEffectId))
        {
            return CleanPresetId;
        }

        if (Matches(settings, ModernBalancedColorProfileId, ClassicPersistenceId, SubtleEffectId, SubtleEffectId))
        {
            return LcdPresetId;
        }

        return Matches(settings, ModernBalancedColorProfileId, ClassicPersistenceId, SubtleEffectId, StrongEffectId)
            ? ReflectiveLcdPresetId
            : CustomPresetId;
    }

    /// <summary>
    /// Reports whether a serialized preset identifier is supported.
    /// </summary>
    public static bool IsPresetId(string? value)
    {
        return value is RawPresetId or CleanPresetId or LcdPresetId or ReflectiveLcdPresetId or CustomPresetId;
    }

    /// <summary>
    /// Reports whether a serialized CGB color-profile identifier is supported.
    /// </summary>
    public static bool IsColorProfileId(string? value)
    {
        return value is RawColorProfileId or ModernBalancedColorProfileId;
    }

    /// <summary>
    /// Reports whether a serialized persistence identifier is supported.
    /// </summary>
    public static bool IsPersistenceId(string? value)
    {
        return value is OffPersistenceId or SubtlePersistenceId or ClassicPersistenceId;
    }

    /// <summary>
    /// Reports whether a serialized spatial-effect identifier is supported.
    /// </summary>
    public static bool IsEffectId(string? value)
    {
        return value is OffEffectId or SubtleEffectId or StrongEffectId;
    }

    private static VideoFilterSettings Create(
        string presetId,
        string cgbColorProfile,
        string persistence,
        string pixelGrid,
        string glare,
        int integerScale)
    {
        return new VideoFilterSettings(
            presetId,
            cgbColorProfile,
            persistence,
            pixelGrid,
            glare,
            Math.Clamp(integerScale, 1, 10));
    }

    private static bool Matches(
        VideoFilterSettings settings,
        string cgbColorProfile,
        string persistence,
        string pixelGrid,
        string glare)
    {
        return settings.CgbColorProfile == cgbColorProfile
            && settings.Persistence == persistence
            && settings.PixelGrid == pixelGrid
            && settings.Glare == glare;
    }
}
