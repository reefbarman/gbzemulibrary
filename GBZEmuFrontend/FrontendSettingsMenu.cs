namespace GBZEmuFrontend;

internal enum FrontendSettingsMenuRow
{
    Preset,
    CgbColor,
    Persistence,
    PixelGrid,
    Glare,
    IntegerScale,
    Apply,
    ResetDefaults,
    Back,
    Count
}

internal enum FrontendSettingsMenuAction
{
    None,
    Apply,
    Back
}

/// <summary>
/// Owns editable frontend settings-menu state without depending on Raylib input or drawing APIs.
/// </summary>
internal sealed class FrontendSettingsMenu
{
    private static readonly string[] PresetIds =
    [
        VideoFilterPresetCatalog.RawPresetId,
        VideoFilterPresetCatalog.CleanPresetId,
        VideoFilterPresetCatalog.LcdPresetId,
        VideoFilterPresetCatalog.ReflectiveLcdPresetId,
        VideoFilterPresetCatalog.CustomPresetId
    ];

    private static readonly string[] ColorProfileIds =
    [
        VideoFilterPresetCatalog.RawColorProfileId,
        VideoFilterPresetCatalog.ModernBalancedColorProfileId
    ];

    private static readonly string[] PersistenceIds =
    [
        VideoFilterPresetCatalog.OffPersistenceId,
        VideoFilterPresetCatalog.SubtlePersistenceId,
        VideoFilterPresetCatalog.ClassicPersistenceId
    ];

    private static readonly string[] EffectIds =
    [
        VideoFilterPresetCatalog.OffEffectId,
        VideoFilterPresetCatalog.SubtleEffectId,
        VideoFilterPresetCatalog.StrongEffectId
    ];

    private readonly ResolvedVideoFilterSettings _resolved;

    public FrontendSettingsMenu(FrontendOptions options, ResolvedVideoFilterSettings resolved)
    {
        ArgumentNullException.ThrowIfNull(options);
        _resolved = resolved ?? throw new ArgumentNullException(nameof(resolved));
        WorkingSettings = resolved.Persisted;
    }

    public FrontendSettingsMenuRow SelectedRow { get; private set; }
    public VideoFilterSettings WorkingSettings { get; private set; }

    /// <summary>
    /// Moves selection through all settings and action rows with wraparound.
    /// </summary>
    public void MoveSelection(int delta)
    {
        var count = (int)FrontendSettingsMenuRow.Count;
        SelectedRow = (FrontendSettingsMenuRow)(((int)SelectedRow + delta % count + count) % count);
    }

    /// <summary>
    /// Cycles the selected editable value, ignoring rows owned by a CLI override.
    /// </summary>
    public void AdjustSelected(int delta)
    {
        if (delta == 0 || IsSelectedRowOverridden)
        {
            return;
        }

        WorkingSettings = SelectedRow switch
        {
            FrontendSettingsMenuRow.Preset => WorkingSettings.WithPreset(
                Cycle(PresetIds, WorkingSettings.PresetId, delta)),
            FrontendSettingsMenuRow.CgbColor => WorkingSettings.WithCgbColorProfile(
                Cycle(ColorProfileIds, WorkingSettings.CgbColorProfile, delta)),
            FrontendSettingsMenuRow.Persistence => WorkingSettings.WithPersistence(
                Cycle(PersistenceIds, WorkingSettings.Persistence, delta)),
            FrontendSettingsMenuRow.PixelGrid => WorkingSettings.WithPixelGrid(
                Cycle(EffectIds, WorkingSettings.PixelGrid, delta)),
            FrontendSettingsMenuRow.Glare => WorkingSettings.WithGlare(
                Cycle(EffectIds, WorkingSettings.Glare, delta)),
            FrontendSettingsMenuRow.IntegerScale => WorkingSettings.WithIntegerScale(
                CycleScale(WorkingSettings.IntegerScale, delta)),
            _ => WorkingSettings
        };
    }

    /// <summary>
    /// Activates action rows or cycles a selected value row forward.
    /// </summary>
    public FrontendSettingsMenuAction ActivateSelected()
    {
        switch (SelectedRow)
        {
            case FrontendSettingsMenuRow.Apply:
                return FrontendSettingsMenuAction.Apply;
            case FrontendSettingsMenuRow.ResetDefaults:
                ResetEditableDefaults();
                return FrontendSettingsMenuAction.None;
            case FrontendSettingsMenuRow.Back:
                return FrontendSettingsMenuAction.Back;
            default:
                AdjustSelected(1);
                return FrontendSettingsMenuAction.None;
        }
    }

    /// <summary>
    /// Returns the visible value, substituting the one-run effective value for overridden rows.
    /// </summary>
    public string GetVisibleValue(FrontendSettingsMenuRow row)
    {
        var settings = IsRowOverridden(row) ? _resolved.Effective : WorkingSettings;
        return row switch
        {
            FrontendSettingsMenuRow.Preset => settings.PresetId,
            FrontendSettingsMenuRow.CgbColor => settings.CgbColorProfile,
            FrontendSettingsMenuRow.Persistence => settings.Persistence,
            FrontendSettingsMenuRow.PixelGrid => settings.PixelGrid,
            FrontendSettingsMenuRow.Glare => settings.Glare,
            FrontendSettingsMenuRow.IntegerScale => $"{settings.IntegerScale}x",
            _ => string.Empty
        };
    }

    /// <summary>
    /// Reports whether the selected row is read-only because command-line input owns it for this run.
    /// </summary>
    public bool IsRowOverridden(FrontendSettingsMenuRow row)
    {
        return row switch
        {
            FrontendSettingsMenuRow.Preset => _resolved.PresetOverridden,
            FrontendSettingsMenuRow.CgbColor => _resolved.ColorOverridden,
            FrontendSettingsMenuRow.Persistence => _resolved.PersistenceOverridden,
            FrontendSettingsMenuRow.PixelGrid => _resolved.PixelGridOverridden,
            FrontendSettingsMenuRow.Glare => _resolved.GlareOverridden,
            FrontendSettingsMenuRow.IntegerScale => _resolved.ScaleOverridden,
            _ => false
        };
    }

    public bool IsSelectedRowOverridden => IsRowOverridden(SelectedRow);

    private void ResetEditableDefaults()
    {
        var defaults = VideoFilterSettings.CompatibilityDefault;
        var current = WorkingSettings;
        WorkingSettings = new VideoFilterSettings(
            _resolved.PresetOverridden ? current.PresetId : defaults.PresetId,
            _resolved.ColorOverridden ? current.CgbColorProfile : defaults.CgbColorProfile,
            _resolved.PersistenceOverridden ? current.Persistence : defaults.Persistence,
            _resolved.PixelGridOverridden ? current.PixelGrid : defaults.PixelGrid,
            _resolved.GlareOverridden ? current.Glare : defaults.Glare,
            _resolved.ScaleOverridden ? current.IntegerScale : defaults.IntegerScale).Normalize();
    }

    private static string Cycle(string[] values, string current, int delta)
    {
        var index = Array.IndexOf(values, current);
        if (index < 0)
        {
            index = 0;
        }

        var next = (index + delta % values.Length + values.Length) % values.Length;
        return values[next];
    }

    private static int CycleScale(int current, int delta)
    {
        const int minimum = 1;
        const int count = 10;
        return ((current - minimum + delta % count + count) % count) + minimum;
    }
}
