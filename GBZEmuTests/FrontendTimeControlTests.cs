using GBZEmuFrontend;

namespace GBZEmuTests;

/// <summary>
/// Verifies frontend persistence and rewind policy above the engine-neutral state APIs.
/// </summary>
public sealed class FrontendTimeControlTests
{
    [Fact]
    public void QuickSaveRoundTripRestoresRunningState()
    {
        using var rom = CreateCounterRom();
        var stateDirectory = Path.Combine(Path.GetTempPath(), $"gbzemu-frontend-state-{Guid.NewGuid():N}");
        var emulator = EmulatorFactory.Start(rom);
        var store = new QuickSaveStateStore(stateDirectory, rom.Path);

        try
        {
            emulator.Update();
            var expectedCounter = emulator.Debug.PeekByte(0xC000);
            store.Save(emulator);
            emulator.Update();

            Assert.True(store.TryLoad(emulator));
            Assert.Equal(expectedCounter, emulator.Debug.PeekByte(0xC000));
            Assert.EndsWith($"{Path.GetFileName(rom.Path)}.state", store.StatePath);
        }
        finally
        {
            emulator.Terminate();
            Directory.Delete(stateDirectory, recursive: true);
        }
    }

    [Fact]
    public void MissingQuickSaveDoesNotChangeEmulator()
    {
        using var rom = CreateCounterRom();
        var emulator = EmulatorFactory.Start(rom);
        var store = new QuickSaveStateStore(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.gb");
        var expectedCounter = emulator.Debug.PeekByte(0xC000);

        Assert.False(store.TryLoad(emulator));
        Assert.Equal(expectedCounter, emulator.Debug.PeekByte(0xC000));
        emulator.Terminate();
    }

    [Fact]
    public void RewindPolicyCapturesAtConfiguredFrameCadence()
    {
        using var rom = CreateCounterRom();
        var emulator = EmulatorFactory.Start(rom);
        var rewind = new FrontendRewindController(capacity: 3, captureIntervalFrames: 2);
        rewind.Reset(emulator);

        emulator.Update();
        rewind.FramesAdvanced(emulator, 1);
        Assert.Equal(1, rewind.CheckpointCount);

        emulator.Update();
        rewind.FramesAdvanced(emulator, 1);
        var expectedCounter = emulator.Debug.PeekByte(0xC000);
        Assert.Equal(2, rewind.CheckpointCount);

        emulator.AdvanceFrames(2, discardAudio: true);
        rewind.FramesAdvanced(emulator, 2);
        Assert.True(rewind.TryRewind(emulator));
        Assert.Equal(expectedCounter, emulator.Debug.PeekByte(0xC000));

        emulator.Terminate();
    }

    private static TestRom CreateCounterRom()
    {
        return TestRom.Create(
            0x21, 0x00, 0xC0,
            0x34,
            0x18, 0xFD);
    }
}
