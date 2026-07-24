using GBZEmuFrontend;

namespace GBZEmuTests;

/// <summary>
/// Verifies text settings-menu navigation, editing, override ownership, and actions without a Raylib window.
/// </summary>
public sealed class FrontendSettingsMenuTests
{
    /// <summary>
    /// Verifies row navigation wraps in both directions.
    /// </summary>
    [Fact]
    public void SelectionWrapsAcrossAllRows()
    {
        var menu = CreateMenu();

        menu.MoveSelection(-1);
        Assert.Equal(FrontendSettingsMenuRow.Back, menu.SelectedRow);

        menu.MoveSelection(1);
        Assert.Equal(FrontendSettingsMenuRow.Preset, menu.SelectedRow);
    }

    /// <summary>
    /// Verifies visual component edits become custom while scale remains independent.
    /// </summary>
    [Fact]
    public void ComponentAndScaleEditsFollowPresetPolicy()
    {
        var menu = CreateMenu(VideoFilterSettings.CompatibilityDefault.WithPreset("lcd"));

        menu.MoveSelection(1);
        menu.AdjustSelected(1);
        Assert.Equal("custom", menu.WorkingSettings.PresetId);
        Assert.Equal("raw", menu.WorkingSettings.CgbColorProfile);

        menu.MoveSelection(4);
        menu.AdjustSelected(1);
        Assert.Equal("custom", menu.WorkingSettings.PresetId);
        Assert.Equal(5, menu.WorkingSettings.IntegerScale);
    }

    /// <summary>
    /// Verifies a CLI-owned row displays its effective value and ignores editing.
    /// </summary>
    [Fact]
    public void OverriddenRowsAreReadOnlyAndShowEffectiveValues()
    {
        using var rom = TestRom.Create(0x00);
        var options = FrontendOptions.Parse([rom.Path, "--raw-colors", "--scale", "8"]);
        var menu = new FrontendSettingsMenu(
            options,
            options.ResolveVideoSettings(VideoFilterSettings.CompatibilityDefault.WithPreset("lcd")));

        menu.MoveSelection(1);
        menu.AdjustSelected(1);
        Assert.Equal("modern-balanced", menu.WorkingSettings.CgbColorProfile);
        Assert.Equal("raw", menu.GetVisibleValue(FrontendSettingsMenuRow.CgbColor));
        Assert.True(menu.IsSelectedRowOverridden);

        menu.MoveSelection(4);
        menu.AdjustSelected(1);
        Assert.Equal(4, menu.WorkingSettings.IntegerScale);
        Assert.Equal("8x", menu.GetVisibleValue(FrontendSettingsMenuRow.IntegerScale));
    }

    /// <summary>
    /// Verifies reset changes only persisted editable rows and does not apply or close implicitly.
    /// </summary>
    [Fact]
    public void ResetDefaultsUpdatesWorkingCopyWithoutApplying()
    {
        using var rom = TestRom.Create(0x00);
        var persisted = new VideoFilterSettings("custom", "raw", "subtle", "strong", "strong", 7);
        var options = FrontendOptions.Parse([rom.Path, "--raw-frames"]);
        var menu = new FrontendSettingsMenu(options, options.ResolveVideoSettings(persisted));
        menu.MoveSelection((int)FrontendSettingsMenuRow.ResetDefaults);

        var action = menu.ActivateSelected();

        Assert.Equal(FrontendSettingsMenuAction.None, action);
        Assert.Equal("custom", menu.WorkingSettings.PresetId);
        Assert.Equal("modern-balanced", menu.WorkingSettings.CgbColorProfile);
        Assert.Equal("subtle", menu.WorkingSettings.Persistence);
        Assert.Equal("off", menu.WorkingSettings.PixelGrid);
        Assert.Equal("off", menu.WorkingSettings.Glare);
        Assert.Equal(4, menu.WorkingSettings.IntegerScale);
        Assert.Equal("off", menu.GetVisibleValue(FrontendSettingsMenuRow.Persistence));
    }

    /// <summary>
    /// Verifies Apply and Back rows produce explicit loop actions.
    /// </summary>
    [Fact]
    public void ActionRowsReturnExpectedActions()
    {
        var menu = CreateMenu();
        menu.MoveSelection((int)FrontendSettingsMenuRow.Apply);
        Assert.Equal(FrontendSettingsMenuAction.Apply, menu.ActivateSelected());

        menu.MoveSelection(2);
        Assert.Equal(FrontendSettingsMenuRow.Back, menu.SelectedRow);
        Assert.Equal(FrontendSettingsMenuAction.Back, menu.ActivateSelected());
    }

    private static FrontendSettingsMenu CreateMenu(VideoFilterSettings? persisted = null)
    {
        using var rom = TestRom.Create(0x00);
        var options = FrontendOptions.Parse([rom.Path]);
        return new FrontendSettingsMenu(
            options,
            options.ResolveVideoSettings(persisted ?? VideoFilterSettings.CompatibilityDefault));
    }
}
