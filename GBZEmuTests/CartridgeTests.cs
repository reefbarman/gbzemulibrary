using GBZEmuLibrary;

namespace GBZEmuTests;

public sealed class CartridgeTests
{
    /// <summary>
    /// Verifies that MBC2 exposes its built-in 512 x 4-bit RAM with upper read bits set and mirrors it
    /// throughout the external-RAM window. This protects save compatibility and the MBC2 nibble-width contract.
    /// </summary>
    [Fact]
    public void Mbc2RamStoresNibblesAndMirrorsAddresses()
    {
        using var rom = TestRom.Create(0x00);
        var bytes = File.ReadAllBytes(rom.Path);
        bytes[0x147] = 0x06;
        File.WriteAllBytes(rom.Path, bytes);
        var emulator = EmulatorFactory.Start(rom);

        emulator.Debug.PokeByte(0x0A, 0x0000);
        emulator.Debug.PokeByte(0xAB, 0xA000);

        Assert.Equal(0xFB, emulator.Debug.PeekByte(0xA000));
        Assert.Equal(0xFB, emulator.Debug.PeekByte(0xA200));
        emulator.Terminate();
    }

    /// <summary>
    /// Selects banks that distinguish modulo normalization from power-of-two masking for the uncommon
    /// 72-, 80-, and 96-bank MBC1 geometries. Both ROM windows are checked because mode 1 remaps bank zero.
    /// </summary>
    [Theory]
    [InlineData(0x52, 72, 95, 23)]
    [InlineData(0x53, 80, 95, 15)]
    [InlineData(0x54, 96, 127, 31)]
    public void Mbc1NormalizesNonPowerOfTwoRomGeometries(byte sizeCode, int bankCount, byte rawBank, byte expectedBank)
    {
        using var rom = CreateBankedRom(sizeCode, bankCount);
        var emulator = EmulatorFactory.Start(rom);

        emulator.Debug.PokeByte((byte)(rawBank & 0x1F), 0x2000);
        emulator.Debug.PokeByte((byte)(rawBank >> 5), 0x4000);

        Assert.Equal(expectedBank, emulator.Debug.PeekByte(0x4000));

        emulator.Debug.PokeByte(1, 0x6000);
        Assert.Equal((byte)(((rawBank >> 5) << 5) % bankCount), emulator.Debug.PeekByte(0x0000));
        emulator.Terminate();
    }

    /// <summary>
    /// Writes distinct values to two MBC5 RAM banks, restarts the emulator, and verifies both values reload.
    /// This protects bank isolation and persistent-save behavior rather than only testing the bank register itself.
    /// </summary>
    [Fact]
    public void Mbc5RamBanksRemainDistinctAfterSaveReload()
    {
        using var rom = CreateCartridge(0x1B, 0x00, 0x03);
        var saveDirectory = Path.Combine(Path.GetTempPath(), $"gbzemu-mbc5-{Guid.NewGuid():N}");
        Directory.CreateDirectory(saveDirectory);

        try
        {
            var first = StartWithSaveDirectory(rom, saveDirectory);
            first.Debug.PokeByte(0x0A, 0x0000);
            first.Debug.PokeByte(1, 0x4000);
            first.Debug.PokeByte(0x11, 0xA000);
            first.Debug.PokeByte(2, 0x4000);
            first.Debug.PokeByte(0x22, 0xA000);
            first.Terminate();

            var second = StartWithSaveDirectory(rom, saveDirectory);
            second.Debug.PokeByte(0x0A, 0x0000);
            second.Debug.PokeByte(1, 0x4000);
            Assert.Equal(0x11, second.Debug.PeekByte(0xA000));
            second.Debug.PokeByte(2, 0x4000);
            Assert.Equal(0x22, second.Debug.PeekByte(0xA000));
            second.Terminate();
        }
        finally
        {
            Directory.Delete(saveDirectory, true);
        }
    }

    /// <summary>
    /// Writes the MBC5 RAM-bank register on a cartridge declaring no RAM and verifies it remains a safe no-op.
    /// Some ROM-only MBC5 software probes controller registers, so a zero-bank header must not divide by zero.
    /// </summary>
    [Fact]
    public void Mbc5RamBankWriteIsSafeWithoutRam()
    {
        using var rom = CreateCartridge(0x19, 0x00, 0x00);
        var emulator = EmulatorFactory.Start(rom);

        emulator.Debug.PokeByte(3, 0x4000);

        Assert.Equal(0xFF, emulator.Debug.PeekByte(0xA000));
        emulator.Terminate();
    }

    /// <summary>
    /// Confirms that address bit A8 selects between MBC2 RAM-enable and ROM-bank writes.
    /// Without this hardware gating, ordinary bank writes can accidentally enable RAM and corrupt saves.
    /// </summary>
    [Fact]
    public void Mbc2UsesAddressBitEightForRamAndRomRegisters()
    {
        using var rom = TestRom.Create(0x00);
        var bytes = File.ReadAllBytes(rom.Path);
        bytes[0x147] = 0x06;
        File.WriteAllBytes(rom.Path, bytes);
        var emulator = EmulatorFactory.Start(rom);

        emulator.Debug.PokeByte(0x0A, 0x0100);
        Assert.Equal(0xFF, emulator.Debug.PeekByte(0xA000));

        emulator.Debug.PokeByte(0x0A, 0x0000);
        emulator.Debug.PokeByte(0x05, 0xA000);
        Assert.Equal(0xF5, emulator.Debug.PeekByte(0xA000));
        emulator.Terminate();
    }

    private static Emulator StartWithSaveDirectory(TestRom rom, string saveDirectory)
    {
        var emulator = new Emulator();
        Assert.True(emulator.Start(new Emulator.Config
        {
            ROMPath = rom.Path,
            SaveLocation = saveDirectory,
            BootMode = BootMode.DMG | BootMode.Skip
        }));
        return emulator;
    }

    private static TestRom CreateCartridge(byte typeCode, byte romSizeCode, byte ramSizeCode)
    {
        var rom = TestRom.Create(0x00);
        var bytes = File.ReadAllBytes(rom.Path);
        bytes[0x147] = typeCode;
        bytes[0x148] = romSizeCode;
        bytes[0x149] = ramSizeCode;
        File.WriteAllBytes(rom.Path, bytes);
        return rom;
    }

    private static TestRom CreateBankedRom(byte sizeCode, int bankCount)
    {
        var rom = TestRom.Create(0x00);
        var bytes = new byte[bankCount * 0x4000];
        for (var bank = 0; bank < bankCount; bank++)
        {
            bytes[bank * 0x4000] = (byte)bank;
        }

        bytes[0x147] = 0x01;
        bytes[0x148] = sizeCode;
        bytes[0x149] = 0x00;
        File.WriteAllBytes(rom.Path, bytes);
        return rom;
    }
}
