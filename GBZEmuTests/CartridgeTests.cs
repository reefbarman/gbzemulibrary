using System.Buffers.Binary;
using GBZEmuLibrary;

namespace GBZEmuTests;

/// <summary>
/// Verifies cartridge-controller banking, persistent RAM, and controller register decoding.
/// </summary>
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
    /// Exercises every switchable ROM bank in the worldwide Pokémon Crystal MBC3 geometry and verifies both
    /// boundaries of the 16 KiB window. Bank zero must remain fixed, while raw bank zero remaps to bank one.
    /// </summary>
    [Fact]
    public void Mbc3PreservesCrystalSizedRomBankIntegrity()
    {
        using var rom = CreateMbc3BankedRom();
        var emulator = EmulatorFactory.Start(rom);

        Assert.Equal(0x00, emulator.Debug.PeekByte(0x0000));
        Assert.Equal(0xFF, emulator.Debug.PeekByte(0x3FFF));

        for (var bank = 1; bank < 128; bank++)
        {
            emulator.Debug.PokeByte((byte)bank, 0x2000);

            Assert.Equal((byte)bank, emulator.Debug.PeekByte(0x4000));
            Assert.Equal((byte)(bank ^ 0xFF), emulator.Debug.PeekByte(0x7FFF));
            Assert.Equal(0x00, emulator.Debug.PeekByte(0x0000));
            Assert.Equal(0xFF, emulator.Debug.PeekByte(0x3FFF));
        }

        emulator.Debug.PokeByte(0x00, 0x2000);
        Assert.Equal(0x01, emulator.Debug.PeekByte(0x4000));

        emulator.Debug.PokeByte(0x80, 0x2000);
        Assert.Equal(0x01, emulator.Debug.PeekByte(0x4000));
        emulator.Terminate();
    }

    /// <summary>
    /// Verifies MBC3 RAM/RTC selection and latch writes do not alter the selected switchable ROM bank.
    /// Crystal interleaves these controller operations with asset and code reads from banked ROM.
    /// </summary>
    [Fact]
    public void Mbc3ControlWritesDoNotDisturbRomBank()
    {
        using var rom = CreateMbc3BankedRom();
        var emulator = EmulatorFactory.Start(rom);
        emulator.Debug.PokeByte(0x42, 0x2000);

        emulator.Debug.PokeByte(0x03, 0x4000);
        emulator.Debug.PokeByte(0x00, 0x6000);
        emulator.Debug.PokeByte(0x01, 0x6000);

        Assert.Equal(0x42, emulator.Debug.PeekByte(0x4000));
        Assert.Equal(0xBD, emulator.Debug.PeekByte(0x7FFF));
        emulator.Terminate();
    }

    /// <summary>
    /// Writes distinct values to all four MBC3 RAM banks, confirms an RTC-register selection does not alias RAM,
    /// then restarts the emulator and verifies every bank reloads from the save file.
    /// </summary>
    [Fact]
    public void Mbc3RamBanksRemainDistinctAfterSaveReload()
    {
        using var rom = CreateMbc3BankedRom();
        var saveDirectory = Path.Combine(Path.GetTempPath(), $"gbzemu-mbc3-{Guid.NewGuid():N}");
        Directory.CreateDirectory(saveDirectory);

        try
        {
            var first = StartWithSaveDirectory(rom, saveDirectory);
            first.Debug.PokeByte(0x0A, 0x0000);
            for (var bank = 0; bank < 4; bank++)
            {
                first.Debug.PokeByte((byte)bank, 0x4000);
                first.Debug.PokeByte((byte)(0x30 + bank), 0xA000);
            }

            first.Debug.PokeByte(0x08, 0x4000);
            first.Debug.PokeByte(0x88, 0xA000);
            first.Terminate();

            var second = StartWithSaveDirectory(rom, saveDirectory);
            second.Debug.PokeByte(0x0A, 0x0000);
            for (var bank = 0; bank < 4; bank++)
            {
                second.Debug.PokeByte((byte)bank, 0x4000);
                Assert.Equal((byte)(0x30 + bank), second.Debug.PeekByte(0xA000));
            }

            second.Terminate();
        }
        finally
        {
            Directory.Delete(saveDirectory, true);
        }
    }

    /// <summary>
    /// Verifies timer-capable MBC3 saves append BGB-compatible RTC metadata after raw RAM and apply elapsed UTC time
    /// on reload without changing the saved latch until the cartridge receives another latch sequence.
    /// </summary>
    [Fact]
    public void Mbc3RtcPersistsAfterRawRamAndCatchesUpOnReload()
    {
        using var rom = CreateMbc3BankedRom();
        var saveDirectory = Path.Combine(Path.GetTempPath(), $"gbzemu-mbc3-rtc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(saveDirectory);
        long unixTimestamp = 10_000;

        try
        {
            var first = new Cartridge(new BootROM(), () => unixTimestamp);
            Assert.True(first.LoadFile(rom.Path, saveDirectory));
            first.WriteByte(0x0A, 0x0000);
            first.WriteByte(0x5A, 0xA000);
            first.WriteByte(MBC3RTC.SecondsRegister, 0x4000);
            first.WriteByte(10, 0xA000);
            first.WriteByte(0, 0x6000);
            first.WriteByte(1, 0x6000);
            first.Terminate();

            var savePath = Path.Combine(saveDirectory, $"{Path.GetFileName(rom.Path)}.sav");
            var saveData = File.ReadAllBytes(savePath);
            Assert.Equal((4 * CartridgeSchema.RAM_BANK_SIZE) + MBC3RTC.PersistenceSize, saveData.Length);
            Assert.Equal(0x5A, saveData[0]);
            Assert.Equal(10, BinaryPrimitives.ReadInt32LittleEndian(saveData.AsSpan(4 * CartridgeSchema.RAM_BANK_SIZE, 4)));
            Assert.Equal(unixTimestamp, BinaryPrimitives.ReadInt64LittleEndian(saveData.AsSpan(saveData.Length - 8, 8)));

            unixTimestamp += 90;
            var second = new Cartridge(new BootROM(), () => unixTimestamp);
            Assert.True(second.LoadFile(rom.Path, saveDirectory));
            second.WriteByte(0x0A, 0x0000);
            second.WriteByte(MBC3RTC.SecondsRegister, 0x4000);
            Assert.Equal(10, second.ReadByte(0xA000));

            second.WriteByte(0, 0x6000);
            second.WriteByte(1, 0x6000);
            Assert.Equal(40, second.ReadByte(0xA000));
            second.WriteByte(MBC3RTC.MinutesRegister, 0x4000);
            Assert.Equal(1, second.ReadByte(0xA000));
            second.WriteByte(0, 0x4000);
            Assert.Equal(0x5A, second.ReadByte(0xA000));
            second.Terminate();
        }
        finally
        {
            Directory.Delete(saveDirectory, true);
        }
    }

    /// <summary>
    /// Verifies timer-only MBC3 cartridges persist a trailer without RAM and corrupt trailers are replaced safely.
    /// </summary>
    [Fact]
    public void Mbc3RtcHandlesZeroRamAndCorruptTrailers()
    {
        using var rom = CreateCartridge(0x0F, 0x00, 0x00);
        var saveDirectory = Path.Combine(Path.GetTempPath(), $"gbzemu-mbc3-rtc-zero-{Guid.NewGuid():N}");
        Directory.CreateDirectory(saveDirectory);
        const long unixTimestamp = 20_000;

        try
        {
            var first = new Cartridge(new BootROM(), () => unixTimestamp);
            Assert.True(first.LoadFile(rom.Path, saveDirectory));
            first.WriteByte(0x0A, 0x0000);
            first.WriteByte(MBC3RTC.SecondsRegister, 0x4000);
            first.WriteByte(7, 0xA000);
            first.WriteByte(0, 0x6000);
            first.WriteByte(1, 0x6000);
            first.Terminate();
            first.Terminate();

            var savePath = Path.Combine(saveDirectory, $"{Path.GetFileName(rom.Path)}.sav");
            var saveData = File.ReadAllBytes(savePath);
            Assert.Equal(MBC3RTC.PersistenceSize, saveData.Length);
            Assert.Equal(7, BinaryPrimitives.ReadInt32LittleEndian(saveData.AsSpan(0, 4)));

            File.WriteAllBytes(savePath, saveData[..20]);
            var second = new Cartridge(new BootROM(), () => unixTimestamp + 100);
            Assert.True(second.LoadFile(rom.Path, saveDirectory));
            second.WriteByte(0x0A, 0x0000);
            second.WriteByte(MBC3RTC.SecondsRegister, 0x4000);
            second.WriteByte(0, 0x6000);
            second.WriteByte(1, 0x6000);
            Assert.Equal(0, second.ReadByte(0xA000));
            second.Terminate();
            Assert.Equal(MBC3RTC.PersistenceSize, new FileInfo(savePath).Length);
        }
        finally
        {
            Directory.Delete(saveDirectory, true);
        }
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
    /// Runs a synthetic MBC5 rumble ROM and verifies the public host output, transition event, and shutdown reset.
    /// </summary>
    [Fact]
    public void Mbc5RumbleRomPublishesMotorTransitions()
    {
        using var rom = CreateCartridge(0x1C, 0x00, 0x00);
        var bytes = File.ReadAllBytes(rom.Path);
        var program = new byte[]
        {
            0x3E, 0x08,       // LD A, $08
            0xEA, 0x00, 0x40, // LD ($4000), A
            0x18, 0xFE        // JR -2
        };
        Array.Copy(program, 0, bytes, 0x100, program.Length);
        File.WriteAllBytes(rom.Path, bytes);

        var emulator = EmulatorFactory.Start(rom);
        var transitions = new List<bool>();
        emulator.RumbleChanged += transitions.Add;

        Assert.True(emulator.SupportsRumble);
        Assert.False(emulator.RumbleActive);

        emulator.Update();

        Assert.True(emulator.RumbleActive);
        Assert.Equal(new[] { true }, transitions);

        emulator.Terminate();
        emulator.Terminate();

        Assert.False(emulator.RumbleActive);
        Assert.Equal(new[] { true, false }, transitions);
    }

    /// <summary>
    /// Verifies that rumble cartridges reserve RAM-bank bit 3 for the motor while ordinary MBC5 cartridges retain
    /// all four RAM-bank bits.
    /// </summary>
    [Fact]
    public void Mbc5RumbleReservesBitThreeFromRamBankSelection()
    {
        using var rumbleRom = CreateCartridge(0x1E, 0x00, 0x04);
        var rumble = EmulatorFactory.Start(rumbleRom);
        rumble.Debug.PokeByte(0x0A, 0x0000);
        rumble.Debug.PokeByte(0x08, 0x4000);
        rumble.Debug.PokeByte(0x44, 0xA000);
        rumble.Debug.PokeByte(0x00, 0x4000);

        Assert.False(rumble.RumbleActive);
        Assert.Equal(0x44, rumble.Debug.PeekByte(0xA000));
        rumble.Terminate();

        using var ordinaryRom = CreateCartridge(0x1B, 0x00, 0x04);
        var ordinary = EmulatorFactory.Start(ordinaryRom);
        ordinary.Debug.PokeByte(0x0A, 0x0000);
        ordinary.Debug.PokeByte(0x08, 0x4000);
        ordinary.Debug.PokeByte(0x88, 0xA000);
        ordinary.Debug.PokeByte(0x00, 0x4000);

        Assert.False(ordinary.SupportsRumble);
        Assert.NotEqual(0x88, ordinary.Debug.PeekByte(0xA000));
        ordinary.Debug.PokeByte(0x08, 0x4000);
        Assert.Equal(0x88, ordinary.Debug.PeekByte(0xA000));
        ordinary.Terminate();
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

    /// <summary>
    /// Creates a synthetic 2 MiB MBC3+timer+RAM+battery cartridge matching worldwide Pokémon Crystal geometry.
    /// Each bank has distinct boundary bytes so tests can detect incorrect selection, wrapping, or window offsets.
    /// </summary>
    private static TestRom CreateMbc3BankedRom()
    {
        const int bankCount = 128;
        var rom = TestRom.Create(0x00);
        var bytes = new byte[bankCount * CartridgeSchema.ROM_BANK_SIZE];
        for (var bank = 0; bank < bankCount; bank++)
        {
            var offset = bank * CartridgeSchema.ROM_BANK_SIZE;
            bytes[offset] = (byte)bank;
            bytes[offset + CartridgeSchema.ROM_BANK_SIZE - 1] = (byte)(bank ^ 0xFF);
        }

        bytes[CartridgeSchema.GBC_MODE_LOC] = 0x80;
        bytes[CartridgeSchema.MBC_MODE_LOC] = 0x10;
        bytes[CartridgeSchema.ROM_BANK_NUM_LOC] = 0x06;
        bytes[CartridgeSchema.RAM_BANK_NUM_LOC] = 0x03;
        File.WriteAllBytes(rom.Path, bytes);
        return rom;
    }
}
