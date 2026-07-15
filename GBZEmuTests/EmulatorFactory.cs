using GBZEmuLibrary;

namespace GBZEmuTests;

internal static class EmulatorFactory
{
    public static Emulator Start(TestRom rom)
    {
        var emulator = new Emulator();
        var started = emulator.Start(new Emulator.Config
        {
            ROMPath = rom.Path,
            SaveLocation = Path.GetTempPath(),
            BootMode = BootMode.DMG | BootMode.Skip
        });

        Assert.True(started);
        return emulator;
    }
}
