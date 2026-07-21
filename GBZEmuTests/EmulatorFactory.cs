using GBZEmuLibrary;

namespace GBZEmuTests;

internal static class EmulatorFactory
{
    public static Emulator Start(TestRom rom)
    {
        var compatibility = CartridgeMetadata.Read(rom.Path).Compatibility;
        var hardwareModel = compatibility == CartridgeCompatibility.DmgOnly
            ? HardwareModel.DmgB
            : HardwareModel.CgbE;
        var emulator = new Emulator();
        var started = emulator.Start(new Emulator.Config(hardwareModel)
        {
            ROMPath = rom.Path,
            SaveLocation = Path.GetTempPath(),
            BootRom = BootRomConfig.Skip()
        });

        Assert.True(started);
        return emulator;
    }
}
