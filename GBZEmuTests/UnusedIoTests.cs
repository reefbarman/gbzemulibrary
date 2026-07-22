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
    /// Verifies that guest CPU access sees CGB-only I/O pull-ups while debugger access remains observational.
    /// </summary>
    [Fact]
    public void DmgCpuHidesCgbOnlyIoWindow()
    {
        using var rom = TestRom.Create(
            0x3E, 0x01,       // LD A, $01
            0xE0, 0x4D,       // LDH (KEY1), A
            0xF0, 0x4D,       // LDH A, (KEY1)
            0xEA, 0x00, 0xC0, // LD ($C000), A
            0x3E, 0x00,       // LD A, $00
            0xE0, 0x55,       // LDH (HDMA5), A
            0xF0, 0x55,       // LDH A, (HDMA5)
            0xEA, 0x01, 0xC0, // LD ($C001), A
            0x40);            // LD B, B
        var emulator = EmulatorFactory.Start(rom);
        emulator.Debug.LoadBBExecuted += emulator.Debug.RequestStop;

        emulator.Update();

        Assert.Equal(0xFF, emulator.Debug.PeekByte(0xC000));
        Assert.Equal(0xFF, emulator.Debug.PeekByte(0xC001));
        Assert.Equal(0x00, emulator.Debug.PeekByte(MemorySchema.CPU_SPEED_SWITCH_REGISTER));
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
        Assert.True(emulator.Start(new Emulator.Config(HardwareModel.CgbE)
        {
            ROMPath = rom.Path,
            SaveLocation = Path.GetTempPath(),
            BootRom = BootRomConfig.Skip()
        }));

        emulator.Debug.PokeByte(0x03, MemorySchema.SWITCHABLE_WORK_RAM_REGISTER);
        var pcm12 = emulator.Debug.PeekByte(APUSchema.PCM_12);
        var pcm34 = emulator.Debug.PeekByte(APUSchema.PCM_34);
        emulator.Debug.PokeByte(0xFF, APUSchema.PCM_12);
        emulator.Debug.PokeByte(0xFF, APUSchema.PCM_34);

        Assert.Equal(0xFB, emulator.Debug.PeekByte(MemorySchema.SWITCHABLE_WORK_RAM_REGISTER));
        Assert.Equal(pcm12, emulator.Debug.PeekByte(APUSchema.PCM_12));
        Assert.Equal(pcm34, emulator.Debug.PeekByte(APUSchema.PCM_34));
        emulator.Terminate();
    }
}
