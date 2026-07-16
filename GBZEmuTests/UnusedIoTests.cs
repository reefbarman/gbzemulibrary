using GBZEmuLibrary;

namespace GBZEmuTests;

public sealed class UnusedIoTests
{
    /// <summary>
    /// Verifies that fixed holes between implemented DMG I/O registers ignore writes and always read as 0xFF.
    /// </summary>
    [Theory]
    [InlineData(0xFF03)]
    [InlineData(0xFF08)]
    [InlineData(0xFF09)]
    [InlineData(0xFF0A)]
    [InlineData(0xFF0B)]
    [InlineData(0xFF0C)]
    [InlineData(0xFF0D)]
    [InlineData(0xFF0E)]
    public void FixedUnusedRegistersIgnoreWrites(int address)
    {
        var registers = new UnmappedIO();

        registers.WriteByte(0x00, address);
        Assert.Equal(0xFF, registers.ReadByte(address));

        registers.WriteByte(0xFF, address);
        Assert.Equal(0xFF, registers.ReadByte(address));
    }

    /// <summary>
    /// Verifies that the CGB-only I/O window is hidden behind 0xFF pull-ups while the CPU runs in DMG mode.
    /// </summary>
    [Fact]
    public void DmgModeHidesCgbOnlyIoWindow()
    {
        using var rom = TestRom.Create(0x00);
        var emulator = EmulatorFactory.Start(rom);

        for (var address = 0xFF4C; address <= 0xFF7F; address++)
        {
            emulator.Debug.PokeByte(0x00, address);
            Assert.Equal(0xFF, emulator.Debug.PeekByte(address));

            emulator.Debug.PokeByte(0xFF, address);
            Assert.Equal(0xFF, emulator.Debug.PeekByte(address));
        }

        emulator.Terminate();
    }

    /// <summary>
    /// Verifies that mode gating does not hide the CGB work-RAM bank register from a CGB host.
    /// </summary>
    [Fact]
    public void CgbModeExposesCgbOnlyIoRegisters()
    {
        using var rom = TestRom.Create(0x00);
        var emulator = new Emulator();
        Assert.True(emulator.Start(new Emulator.Config
        {
            ROMPath = rom.Path,
            SaveLocation = Path.GetTempPath(),
            BootMode = BootMode.GBC | BootMode.Force | BootMode.Skip
        }));

        emulator.Debug.PokeByte(0x03, MemorySchema.SWITCHABLE_WORK_RAM_REGISTER);

        Assert.Equal(0xFB, emulator.Debug.PeekByte(MemorySchema.SWITCHABLE_WORK_RAM_REGISTER));
        emulator.Terminate();
    }
}
