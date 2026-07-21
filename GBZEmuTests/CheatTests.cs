using GBZEmuLibrary;

namespace GBZEmuTests;

public sealed class CheatTests
{
    [Theory]
    [InlineData("0A1-B9F", 0x01B9, 0x0A, null)]
    [InlineData("068-5FF-E66", 0x085F, 0x06, 0x03)]
    [InlineData("05d 49c e62", 0x3D49, 0x05, 0x02)]
    public void GameGenieParserDecodesDocumentedCodes(string code, int address, int value, int? compareValue)
    {
        var entry = CheatEntry.Parse(code);

        Assert.Equal(CheatFormat.GameGenie, entry.Format);
        Assert.Equal(address, entry.Address);
        Assert.Equal(value, entry.Value);
        Assert.Equal(compareValue, entry.CompareValue);
        Assert.Null(entry.Bank);
    }

    [Theory]
    [InlineData("01D8C8D3", 0xD3C8, 0xD8, null, null)]
    [InlineData("92-7F-00-D0", 0xD000, 0x7F, 2, CheatBankType.WorkRam)]
    [InlineData("82-AA-00-A0", 0xA000, 0xAA, 2, CheatBankType.CartridgeRam)]
    public void GameSharkParserDecodesLittleEndianAddressAndBank(
        string code,
        int address,
        int value,
        int? bank,
        CheatBankType? bankType)
    {
        var entry = CheatEntry.Parse(code);

        Assert.Equal(CheatFormat.GameSharkActionReplay, entry.Format);
        Assert.Equal(address, entry.Address);
        Assert.Equal(value, entry.Value);
        Assert.Equal(bank, entry.Bank);
        Assert.Equal(bankType, entry.BankType);
        Assert.Null(entry.CompareValue);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-code")]
    [InlineData("01000001")]
    [InlineData("99AA00D0")]
    [InlineData("01AA00FF")]
    public void ParserRejectsMalformedOrUnsupportedCodes(string? code)
    {
        Assert.False(CheatEntry.TryParse(code!, out var entry));
        Assert.Null(entry);
    }

    [Fact]
    public void CollectionOwnsEntryEnableDisableAndRemovalLifecycle()
    {
        var first = new Emulator().Cheats;
        var second = new Emulator().Cheats;
        var parsed = CheatEntry.Parse("0A1-B9F");
        var entry = first.Add(parsed, false);

        Assert.Single(first.Entries);
        Assert.False(entry.Enabled);
        Assert.Throws<InvalidOperationException>(() => second.Add(entry));

        first.SetEnabled(entry, true);
        Assert.True(entry.Enabled);
        Assert.Throws<ArgumentException>(() => second.SetEnabled(entry, false));

        Assert.True(first.Remove(entry));
        Assert.False(entry.Enabled);
        Assert.False(first.Remove(entry));
        Assert.Same(entry, second.Add(entry));

        second.Clear();
        Assert.Empty(second.Entries);
        Assert.False(entry.Enabled);
    }

    [Fact]
    public void GameGenieSubstitutesRomReadsAndFirstMatchingEntryWins()
    {
        using var rom = TestRom.Create(0x00);
        var emulator = new Emulator();
        var first = emulator.Cheats.Add("0A1-B9F");
        var second = emulator.Cheats.Add("0B1-B9F");
        StartDmg(emulator, rom.Path);

        Assert.Equal(0x0A, emulator.Debug.PeekByte(0x01B9));

        emulator.Cheats.SetEnabled(first, false);
        Assert.Equal(0x0B, emulator.Debug.PeekByte(0x01B9));

        emulator.Cheats.SetEnabled(second, false);
        Assert.Equal(0x00, emulator.Debug.PeekByte(0x01B9));
        emulator.Terminate();
    }

    [Fact]
    public void GameGenieCompareValueSelectsMappedRomBank()
    {
        var bytes = new byte[4 * 0x4000];
        bytes[0x100] = 0x76; // HALT
        bytes[0x147] = 0x01; // MBC1
        bytes[0x148] = 0x01; // Four ROM banks
        bytes[0x149] = 0x00;
        bytes[0x4000] = 0x02;
        bytes[0x8000] = 0x03;
        var path = WriteTemporaryRom(bytes);

        try
        {
            var emulator = new Emulator();
            emulator.Cheats.Add(EncodeGameGenie(0x99, 0x4000, 0x02));
            StartDmg(emulator, path);

            Assert.Equal(0x99, emulator.Debug.PeekByte(0x4000));

            emulator.Debug.PokeByte(0x02, 0x2000);
            Assert.Equal(0x03, emulator.Debug.PeekByte(0x4000));
            emulator.Terminate();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void GameSharkWritesOnceAtVBlankAndLaterEntriesWin()
    {
        using var rom = TestRom.Create(0x76);
        var emulator = new Emulator();
        var first = emulator.Cheats.Add("01AA00C0");
        var second = emulator.Cheats.Add("01BB00C0");
        StartDmg(emulator, rom.Path);
        emulator.Debug.PokeByte(0x11, 0xC000);

        Assert.Equal(0x11, emulator.Debug.PeekByte(0xC000));
        // The skip-boot PPU profile begins in mode 1; the first budget finishes that partial startup frame.
        emulator.Update();
        Assert.Equal(0x11, emulator.Debug.PeekByte(0xC000));
        emulator.Update();
        Assert.Equal(0xBB, emulator.Debug.PeekByte(0xC000));

        emulator.Cheats.SetEnabled(first, false);
        emulator.Cheats.SetEnabled(second, false);
        emulator.Debug.PokeByte(0x22, 0xC000);
        emulator.AdvanceFrames(2);
        Assert.Equal(0x22, emulator.Debug.PeekByte(0xC000));
        emulator.Terminate();
    }

    [Fact]
    public void BankedGameSharkWriteDoesNotChangeActiveCgbWorkRamBank()
    {
        using var rom = TestRom.Create(0x76);
        var bytes = File.ReadAllBytes(rom.Path);
        bytes[0x143] = 0x80;
        File.WriteAllBytes(rom.Path, bytes);
        var emulator = new Emulator();
        emulator.Cheats.Add("92AA00D0");
        StartCgb(emulator, rom.Path);

        emulator.Debug.PokeByte(0x02, MemorySchema.SWITCHABLE_WORK_RAM_REGISTER);
        emulator.Debug.PokeByte(0x22, MemorySchema.WORK_RAM_SWITCHABLE_START);
        emulator.Debug.PokeByte(0x03, MemorySchema.SWITCHABLE_WORK_RAM_REGISTER);
        emulator.Debug.PokeByte(0x33, MemorySchema.WORK_RAM_SWITCHABLE_START);

        emulator.AdvanceFrames(2);

        Assert.Equal(0xFB, emulator.Debug.PeekByte(MemorySchema.SWITCHABLE_WORK_RAM_REGISTER));
        Assert.Equal(0x33, emulator.Debug.PeekByte(MemorySchema.WORK_RAM_SWITCHABLE_START));
        emulator.Debug.PokeByte(0x02, MemorySchema.SWITCHABLE_WORK_RAM_REGISTER);
        Assert.Equal(0xAA, emulator.Debug.PeekByte(MemorySchema.WORK_RAM_SWITCHABLE_START));
        emulator.Terminate();
    }

    [Fact]
    public void BankedGameSharkWriteIgnoresUnavailableDmgWorkRamBank()
    {
        using var rom = TestRom.Create(0x76);
        var emulator = new Emulator();
        emulator.Cheats.Add("92AA00D0");
        StartDmg(emulator, rom.Path);
        emulator.Debug.PokeByte(0x22, MemorySchema.WORK_RAM_SWITCHABLE_START);

        emulator.AdvanceFrames(2);

        Assert.Equal(0x22, emulator.Debug.PeekByte(MemorySchema.WORK_RAM_SWITCHABLE_START));
        emulator.Terminate();
    }

    [Fact]
    public void BankedGameSharkWriteTargetsCartridgeRamWithoutChangingMbc5Bank()
    {
        using var rom = TestRom.Create(0x76);
        var bytes = File.ReadAllBytes(rom.Path);
        bytes[0x147] = 0x1A; // MBC5 + RAM
        bytes[0x149] = 0x03; // Four RAM banks
        File.WriteAllBytes(rom.Path, bytes);
        var savePath = rom.Path + ".sav";

        try
        {
            var emulator = new Emulator();
            emulator.Cheats.Add("82AA00A0");
            StartDmg(emulator, rom.Path);
            emulator.Debug.PokeByte(0x0A, 0x0000);
            emulator.Debug.PokeByte(0x01, 0x4000);
            emulator.Debug.PokeByte(0x11, MemorySchema.EXTERNAL_RAM_START);

            emulator.AdvanceFrames(2);

            Assert.Equal(0x11, emulator.Debug.PeekByte(MemorySchema.EXTERNAL_RAM_START));
            emulator.Debug.PokeByte(0x02, 0x4000);
            Assert.Equal(0xAA, emulator.Debug.PeekByte(MemorySchema.EXTERNAL_RAM_START));
            emulator.Terminate();
        }
        finally
        {
            File.Delete(savePath);
        }
    }

    [Fact]
    public void SaveStateRestoresModifiedRamButKeepsCurrentCheatConfiguration()
    {
        using var rom = TestRom.Create(0x76);
        var emulator = new Emulator();
        var entry = emulator.Cheats.Add("01AA00C0");
        StartDmg(emulator, rom.Path);
        emulator.AdvanceFrames(2);
        var state = emulator.CaptureState();

        emulator.Cheats.SetEnabled(entry, false);
        emulator.Debug.PokeByte(0x55, 0xC000);
        emulator.RestoreState(state);

        Assert.Equal(0xAA, emulator.Debug.PeekByte(0xC000));
        Assert.False(entry.Enabled);

        emulator.Debug.PokeByte(0x66, 0xC000);
        emulator.Update();
        Assert.Equal(0x66, emulator.Debug.PeekByte(0xC000));

        emulator.Cheats.SetEnabled(entry, true);
        emulator.Update();
        Assert.Equal(0xAA, emulator.Debug.PeekByte(0xC000));
        emulator.Terminate();
    }

    private static void StartDmg(Emulator emulator, string romPath)
    {
        Assert.True(emulator.Start(new Emulator.Config(HardwareModel.DmgB)
        {
            ROMPath = romPath,
            SaveLocation = Path.GetTempPath(),
            BootRom = BootRomConfig.Skip()
        }));
    }

    private static void StartCgb(Emulator emulator, string romPath)
    {
        Assert.True(emulator.Start(new Emulator.Config(HardwareModel.CgbE)
        {
            ROMPath = romPath,
            SaveLocation = Path.GetTempPath(),
            BootRom = BootRomConfig.Skip()
        }));
    }

    private static string EncodeGameGenie(byte value, ushort address, byte compareValue)
    {
        var transformedAddress = (ushort)(address ^ 0xF000);
        var encodedAddress = (ushort)((transformedAddress << 4) | (transformedAddress >> 12));
        var transformedCompare = (byte)(compareValue ^ 0xBA);
        var encodedCompare = (byte)((transformedCompare << 2) | (transformedCompare >> 6));
        return $"{value:X2}{encodedAddress:X4}{encodedCompare >> 4:X1}8{encodedCompare & 0x0F:X1}";
    }

    private static string WriteTemporaryRom(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"gbzemu-cheat-{Guid.NewGuid():N}.gb");
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
