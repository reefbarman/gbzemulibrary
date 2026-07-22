using GBZEmuLibrary;

namespace GBZEmuTests;

/// <summary>
/// Verifies concrete hardware selection, typed firmware inputs, model-specific overlays, and retained boot handoffs.
/// </summary>
public sealed class BootRomTests
{
    private const ushort CartridgeEntryPoint = 0x100;
    private const int LongBootFrameBudget = 400;

    private static readonly int[] SkipProfileRegisters =
    {
        0xFF40, 0xFF42, 0xFF43, 0xFF45, 0xFF47, 0xFF48, 0xFF49, 0xFF4A, 0xFF4B,
        0xFF05, 0xFF06, 0xFF07, 0xFF0F, 0xFFFF,
        0xFF10, 0xFF11, 0xFF12, 0xFF14, 0xFF24, 0xFF25, 0xFF26
    };

    [Fact]
    public void BuiltInMgbFirmwareRetainsDmgPresentationWithModelHandoff()
    {
        var dmg = new BootROM();
        var mgb = new BootROM();

        dmg.Load(HardwareModel.DmgB, BootRomConfig.BuiltIn());
        mgb.Load(HardwareModel.Mgb, BootRomConfig.BuiltIn());

        Assert.Equal(0x100, dmg.Bytes.Length);
        Assert.Equal(0x100, mgb.Bytes.Length);
        Assert.False(dmg.IsColorFamilySelected);
        Assert.False(mgb.IsColorFamilySelected);

        var differences = Enumerable.Range(0, dmg.Bytes.Length)
            .Where(index => dmg.Bytes[index] != mgb.Bytes[index])
            .ToArray();
        Assert.Equal(new[] { 0x8C }, differences);
        Assert.Equal(0x01, dmg.Bytes[0x8C]);
        Assert.Equal(0xFF, mgb.Bytes[0x8C]);
    }

    [Fact]
    public void BuiltInAgbFirmwareUsesDistinctColorFamilyImage()
    {
        var cgb = new BootROM();
        var agb = new BootROM();

        cgb.Load(HardwareModel.CgbE, BootRomConfig.BuiltIn());
        agb.Load(HardwareModel.AgbA, BootRomConfig.BuiltIn());

        Assert.Equal(0x900, cgb.Bytes.Length);
        Assert.Equal(0x900, agb.Bytes.Length);
        Assert.True(cgb.IsColorFamilySelected);
        Assert.True(agb.IsColorFamilySelected);
        Assert.NotEqual(cgb.Bytes, agb.Bytes);
        Assert.Equal(new byte[] { 0x3E, 0x11, 0xB7, 0xC3, 0xFE, 0x00 }, agb.Bytes[0x6C1..0x6C7]);
        Assert.Equal(0xE0, agb.Bytes[0xFE]);
        Assert.Equal(0x50, agb.Bytes[0xFF]);
    }

    [Fact]
    public void AgbExternalByteArrayIsPrivatelyOwnedByColorFamilySlot()
    {
        var image = new byte[0x900];
        image[0] = 0xA5;
        image[0x200] = 0xC3;
        var config = BootRomConfig.ExternalBytes(image);
        image[0] = 0x5A;
        image[0x200] = 0x3C;
        var agb = new BootROM();

        agb.Load(HardwareModel.AgbA, config);

        Assert.True(agb.IsColorFamilySelected);
        Assert.Equal(0xA5, agb.Bytes[0]);
        Assert.Equal(0xC3, agb.Bytes[0x200]);
    }

    [Fact]
    public void AgbFirmwareSlotRejectsWrongExternalSize()
    {
        var agb = new BootROM();

        var error = Assert.Throws<ArgumentException>(() =>
            agb.Load(HardwareModel.AgbA, BootRomConfig.ExternalBytes(new byte[0x100])));

        Assert.Contains("2304", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MgbSkipBootUsesLateDmgStateWithPocketAccumulator()
    {
        using var rom = CreateRom(CartridgeCompatibility.DmgOnly);
        var emulator = Start(rom, HardwareModel.Mgb, BootRomConfig.Skip());
        var cpu = emulator.Debug.GetCpuState();

        Assert.Equal(0xFFB0, cpu.AF);
        Assert.Equal(0x0013, cpu.BC);
        Assert.Equal(0x00D8, cpu.DE);
        Assert.Equal(0x014D, cpu.HL);
        Assert.Equal(0xFFFE, cpu.SP);
        Assert.Equal(CartridgeEntryPoint, cpu.PC);
        Assert.Equal(0xAB, emulator.Debug.PeekByte(MemorySchema.DIVIDE_REGISTER));
        Assert.Equal(0x7E, emulator.Debug.PeekByte(MemorySchema.SERIAL_CONTROL_REGISTER));
        Assert.Equal(0xCF, emulator.Debug.PeekByte(MemorySchema.JOYPAD_REGISTER));

        emulator.Terminate();
    }

    [Theory]
    [InlineData(CartridgeCompatibility.DmgOnly, 0x1100, 0x0100, 0x0008, 0x007C, 0x04, 0xFF)]
    [InlineData(CartridgeCompatibility.CgbCompatible, 0x1100, 0x0100, 0xFF56, 0x000D, 0x80, 0xFE)]
    [InlineData(CartridgeCompatibility.CgbOnly, 0x1100, 0x0100, 0xFF56, 0x000D, 0xC0, 0xFE)]
    public void AgbSkipBootUsesResolvedHardwareHandoff(
        CartridgeCompatibility compatibility,
        int expectedAf,
        int expectedBc,
        int expectedDe,
        int expectedHl,
        int expectedKey0,
        int expectedObjectPriority)
    {
        using var rom = CreateRom(compatibility);
        var emulator = Start(rom, HardwareModel.AgbA, BootRomConfig.Skip());
        var cpu = emulator.Debug.GetCpuState();

        Assert.Equal(expectedAf, cpu.AF);
        Assert.Equal(expectedBc, cpu.BC);
        Assert.Equal(expectedDe, cpu.DE);
        Assert.Equal(expectedHl, cpu.HL);
        Assert.Equal(0xFFFE, cpu.SP);
        Assert.Equal(CartridgeEntryPoint, cpu.PC);
        Assert.Equal(expectedKey0, emulator.Debug.PeekByte(MemorySchema.CPU_MODE_SELECT_REGISTER));
        Assert.Equal(expectedObjectPriority, emulator.Debug.PeekByte(MemorySchema.OBJECT_PRIORITY_REGISTER));
        Assert.Equal(0x00, emulator.Debug.PeekByte(0x0000));

        if (compatibility == CartridgeCompatibility.DmgOnly)
        {
            Assert.Equal(
                new byte[] { 0xFF, 0x7F, 0xEF, 0x1B, 0x80, 0x61, 0x00, 0x00 },
                ReadPalette(emulator, 0xFF68, 0xFF69, 0));
            Assert.Equal(
                new byte[] { 0xFF, 0x7F, 0x1F, 0x42, 0xF2, 0x1C, 0x00, 0x00 },
                ReadPalette(emulator, 0xFF6A, 0xFF6B, 0));
        }

        emulator.Terminate();
    }

    [Fact]
    public void AgbDmgCompatibilityBlocksGuestCgbIoAfterHandoff()
    {
        using var rom = TestRom.Create(
            0x3E, 0x01,       // LD A, 1
            0xE0, 0x4D,       // LDH [KEY1], A
            0xF0, 0x4D,       // LDH A, [KEY1]
            0xEA, 0x00, 0xC0, // LD [C000], A
            0x18, 0xFE);      // JR -2
        var emulator = Start(rom, HardwareModel.AgbA, BootRomConfig.Skip());

        emulator.Update();

        Assert.Equal(0xFF, emulator.Debug.PeekByte(0xC000));
        Assert.Equal(0x04, emulator.Debug.PeekByte(MemorySchema.CPU_MODE_SELECT_REGISTER));
        Assert.Equal(0xFF, emulator.Debug.PeekByte(MemorySchema.OBJECT_PRIORITY_REGISTER));
        emulator.Terminate();
    }

    [Theory]
    [InlineData(HardwareModel.DmgB, CartridgeCompatibility.DmgOnly, false)]
    [InlineData(HardwareModel.DmgB, CartridgeCompatibility.DmgOnly, true)]
    [InlineData(HardwareModel.Mgb, CartridgeCompatibility.DmgOnly, false)]
    [InlineData(HardwareModel.Mgb, CartridgeCompatibility.DmgOnly, true)]
    [InlineData(HardwareModel.CgbE, CartridgeCompatibility.CgbCompatible, false)]
    [InlineData(HardwareModel.CgbE, CartridgeCompatibility.CgbCompatible, true)]
    [InlineData(HardwareModel.AgbA, CartridgeCompatibility.CgbCompatible, false)]
    [InlineData(HardwareModel.AgbA, CartridgeCompatibility.CgbCompatible, true)]
    [InlineData(HardwareModel.Sgb2, CartridgeCompatibility.DmgOnly, false)]
    [InlineData(HardwareModel.Sgb2, CartridgeCompatibility.DmgOnly, true)]
    public void Start_InitializesHostVisibleFramebufferBeforeFirstUpdate(
        HardwareModel model,
        CartridgeCompatibility compatibility,
        bool skipBootRom)
    {
        using var rom = CreateRom(compatibility);
        var emulator = Start(rom, model, skipBootRom ? BootRomConfig.Skip() : BootRomConfig.BuiltIn());
        var screen = emulator.GetScreenData();
        var expected = model == HardwareModel.CgbE || model == HardwareModel.AgbA
            ? new Color(byte.MaxValue, byte.MaxValue, byte.MaxValue)
            : Display.DefaultPalette[0];

        for (var y = 0; y < Display.VERTICAL_RESOLUTION; y++)
        {
            for (var x = 0; x < Display.HORIZONTAL_RESOLUTION; x++)
            {
                Assert.Equal(
                    (expected.R, expected.G, expected.B),
                    (screen[x, y].R, screen[x, y].G, screen[x, y].B));
            }
        }

        emulator.Terminate();
    }

    [Theory]
    [InlineData(HardwareModel.DmgB, CartridgeCompatibility.DmgOnly)]
    [InlineData(HardwareModel.Mgb, CartridgeCompatibility.DmgOnly)]
    [InlineData(HardwareModel.CgbE, CartridgeCompatibility.CgbCompatible)]
    [InlineData(HardwareModel.AgbA, CartridgeCompatibility.DmgOnly)]
    [InlineData(HardwareModel.AgbA, CartridgeCompatibility.CgbCompatible)]
    [InlineData(HardwareModel.AgbA, CartridgeCompatibility.CgbOnly)]
    public void BuiltInBootRomMatchesModelSpecificSkipProfileAtHandoff(
        HardwareModel model,
        CartridgeCompatibility compatibility)
    {
        using var rom = CreateRom(compatibility);
        var booted = Start(rom, model, BootRomConfig.BuiltIn());
        Assert.True(booted.Debug.RunUntilProgramCounter(CartridgeEntryPoint, LongBootFrameBudget));

        var skipped = Start(rom, model, BootRomConfig.Skip());
        var bootedState = booted.Debug.GetCpuState();
        var skippedState = skipped.Debug.GetCpuState();

        Assert.Equal(CartridgeEntryPoint, bootedState.PC);
        Assert.Equal(skippedState.AF, bootedState.AF);
        Assert.Equal(skippedState.BC, bootedState.BC);
        Assert.Equal(skippedState.DE, bootedState.DE);
        Assert.Equal(skippedState.HL, bootedState.HL);
        Assert.Equal(skippedState.SP, bootedState.SP);

        foreach (var address in SkipProfileRegisters)
        {
            Assert.True(
                skipped.Debug.PeekByte(address) == booted.Debug.PeekByte(address),
                $"I/O mismatch at {address:X4}: skip={skipped.Debug.PeekByte(address):X2} boot={booted.Debug.PeekByte(address):X2}");
        }

        if (model == HardwareModel.AgbA)
        {
            Assert.Equal(
                skipped.Debug.PeekByte(MemorySchema.CPU_MODE_SELECT_REGISTER),
                booted.Debug.PeekByte(MemorySchema.CPU_MODE_SELECT_REGISTER));
            Assert.Equal(
                skipped.Debug.PeekByte(MemorySchema.OBJECT_PRIORITY_REGISTER),
                booted.Debug.PeekByte(MemorySchema.OBJECT_PRIORITY_REGISTER));

            if (compatibility == CartridgeCompatibility.DmgOnly)
            {
                Assert.Equal(
                    ReadPalette(skipped, 0xFF68, 0xFF69, 0),
                    ReadPalette(booted, 0xFF68, 0xFF69, 0));
                Assert.Equal(
                    ReadPalette(skipped, 0xFF6A, 0xFF6B, 0),
                    ReadPalette(booted, 0xFF6A, 0xFF6B, 0));
                Assert.Equal(
                    ReadPalette(skipped, 0xFF6A, 0xFF6B, 8),
                    ReadPalette(booted, 0xFF6A, 0xFF6B, 8));
            }
        }

        booted.Terminate();
        skipped.Terminate();
    }

    [Fact]
    public void BuiltInDmgBootRomHoldsSettledLogoBeforeHandoff()
    {
        using var rom = CreateRom(CartridgeCompatibility.DmgOnly);
        var emulator = Start(rom, HardwareModel.DmgB, BootRomConfig.BuiltIn());
        var sawScrolling = false;

        for (var frame = 0; frame < LongBootFrameBudget; frame++)
        {
            emulator.Update();
            var scrollY = emulator.Debug.PeekByte(0xFF42);
            sawScrolling |= scrollY != 0;

            if (sawScrolling && scrollY == 0)
            {
                break;
            }
        }

        Assert.True(sawScrolling);
        Assert.Equal(0, emulator.Debug.PeekByte(0xFF42));

        for (var frame = 0; frame < 20; frame++)
        {
            emulator.Update();
            Assert.NotEqual(0x00, emulator.Debug.PeekByte(0x0000));
        }

        Assert.True(emulator.Debug.RunUntilProgramCounter(CartridgeEntryPoint, LongBootFrameBudget));
        emulator.Terminate();
    }

    [Fact]
    public void BuiltInCgbBootRomColorizesKnownDmgCart()
    {
        using var rom = CreateRom(CartridgeCompatibility.DmgOnly);
        var bytes = File.ReadAllBytes(rom.Path);
        bytes[0x134] = 0x88;
        bytes[0x14B] = 0x01;
        File.WriteAllBytes(rom.Path, bytes);

        var emulator = Start(rom, HardwareModel.CgbE, BootRomConfig.BuiltIn());
        Assert.True(emulator.Debug.RunUntilProgramCounter(CartridgeEntryPoint, LongBootFrameBudget));

        emulator.Debug.PokeByte(0x06, 0xFF68);
        Assert.Equal(0x00, emulator.Debug.PeekByte(0xFF69));
        emulator.Debug.PokeByte(0x3E, 0xFF68);
        Assert.Equal(0xC4, emulator.Debug.PeekByte(0xFF69));
        emulator.Debug.PokeByte(0x3F, 0xFF68);
        Assert.Equal(0x38, emulator.Debug.PeekByte(0xFF69));

        var ppu = emulator.Debug.GetPpuState();
        Assert.Equal(144, ppu.ScanLine);
        Assert.Equal(1, ppu.Mode);

        var retainedTileData = false;
        foreach (var bank in new byte[] { 0x00, 0x01 })
        {
            emulator.Debug.PokeByte(bank, 0xFF4F);
            for (var address = 0x8000; address < 0x9800; address++)
            {
                retainedTileData |= emulator.Debug.PeekByte(address) != 0;
            }

            for (var address = 0x9800; address <= 0x9FFF; address++)
            {
                Assert.Equal(0x00, emulator.Debug.PeekByte(address));
            }
        }

        Assert.True(retainedTileData);
        emulator.Debug.PokeByte(0x00, 0xFF4F);
        emulator.Terminate();
    }

    [Fact]
    public void BuiltInCgbBootRomSelectsDonkeyKongLandPalettes()
    {
        using var rom = CreateRom(CartridgeCompatibility.DmgOnly);
        var bytes = File.ReadAllBytes(rom.Path);
        "DONKEYKONGLAND95"u8.CopyTo(bytes.AsSpan(0x134, 16));
        bytes[0x14B] = 0x01;
        File.WriteAllBytes(rom.Path, bytes);

        var emulator = Start(rom, HardwareModel.CgbE, BootRomConfig.BuiltIn());
        Assert.True(emulator.Debug.RunUntilProgramCounter(CartridgeEntryPoint, LongBootFrameBudget));

        Assert.Equal(
            new byte[] { 0x1F, 0x23, 0x5F, 0x03, 0xF2, 0x00, 0x09, 0x00 },
            ReadPalette(emulator, 0xFF6A, 0xFF6B, 0));
        Assert.Equal(
            new byte[] { 0xFF, 0x7F, 0x1F, 0x42, 0xF2, 0x1C, 0x00, 0x00 },
            ReadPalette(emulator, 0xFF6A, 0xFF6B, 8));
        Assert.Equal(
            new byte[] { 0xFF, 0x4F, 0xD2, 0x7E, 0x4C, 0x3A, 0xE0, 0x1C },
            ReadPalette(emulator, 0xFF68, 0xFF69, 0));

        emulator.Terminate();
    }

    [Fact]
    public void SkipBootUsesCgbCompatibilityHandoffWithoutMappingFirmware()
    {
        using var rom = CreateRom(CartridgeCompatibility.DmgOnly);
        var emulator = Start(rom, HardwareModel.CgbE, BootRomConfig.Skip());

        Assert.Equal(CartridgeEntryPoint, emulator.Debug.GetCpuState().PC);
        Assert.Equal(0x00, emulator.Debug.PeekByte(0x0000));
        Assert.Equal(
            new byte[] { 0xFF, 0x7F, 0xEF, 0x1B, 0x80, 0x61, 0x00, 0x00 },
            ReadPalette(emulator, 0xFF68, 0xFF69, 0));
        Assert.Equal(
            new byte[] { 0xFF, 0x7F, 0x1F, 0x42, 0xF2, 0x1C, 0x00, 0x00 },
            ReadPalette(emulator, 0xFF6A, 0xFF6B, 0));
        Assert.Equal(
            new byte[]
            {
                0x3C, 0x00, 0x42, 0x00, 0xB9, 0x00, 0xA5, 0x00,
                0xB9, 0x00, 0xA5, 0x00, 0x42, 0x00, 0x3C, 0x00
            },
            Enumerable.Range(0x8190, 16).Select(emulator.Debug.PeekByte));

        emulator.Terminate();
    }

    [Theory]
    [InlineData(HardwareModel.DmgB, 0x100)]
    [InlineData(HardwareModel.Mgb, 0x100)]
    [InlineData(HardwareModel.CgbE, 0x900)]
    [InlineData(HardwareModel.AgbA, 0x900)]
    [InlineData(HardwareModel.Sgb2, 0x100)]
    public void ExternalByteArrayIsPrivatelyOwnedAndMappedForSelectedModel(HardwareModel model, int size)
    {
        using var rom = CreateRom(CartridgeCompatibility.DmgOnly);
        var image = new byte[size];
        image[0] = 0xA5;
        if (size > 0x200)
        {
            image[0x200] = 0xC3;
        }

        var bootRom = BootRomConfig.ExternalBytes(image);
        image[0] = 0x5A;
        if (size > 0x200)
        {
            image[0x200] = 0x3C;
        }

        var emulator = Start(rom, model, bootRom);
        Assert.Equal(0xA5, emulator.Debug.PeekByte(0x0000));
        Assert.Equal(model == HardwareModel.CgbE || model == HardwareModel.AgbA ? 0xC3 : 0x00, emulator.Debug.PeekByte(0x0200));
        emulator.Terminate();
    }

    [Theory]
    [InlineData(HardwareModel.DmgB, 0x100)]
    [InlineData(HardwareModel.Mgb, 0x100)]
    [InlineData(HardwareModel.CgbE, 0x900)]
    [InlineData(HardwareModel.AgbA, 0x900)]
    [InlineData(HardwareModel.Sgb2, 0x100)]
    public void ExternalFileLoadsExactModelSpecificImage(HardwareModel model, int size)
    {
        using var rom = CreateRom(CartridgeCompatibility.DmgOnly);
        var path = WriteBootRom(size, 0x76);

        try
        {
            var emulator = Start(rom, model, BootRomConfig.ExternalFile(path));
            Assert.Equal(0x76, emulator.Debug.PeekByte(0x0000));
            emulator.Terminate();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(HardwareModel.DmgB, 0x900)]
    [InlineData(HardwareModel.Mgb, 0x900)]
    [InlineData(HardwareModel.CgbE, 0x100)]
    [InlineData(HardwareModel.AgbA, 0x100)]
    [InlineData(HardwareModel.Sgb2, 0x900)]
    public void ExternalFirmwareRejectsWrongModelSpecificSize(HardwareModel model, int wrongSize)
    {
        using var rom = CreateRom(CartridgeCompatibility.DmgOnly);
        var emulator = new Emulator();
        var config = CreateConfig(rom, model, BootRomConfig.ExternalBytes(new byte[wrongSize]));

        var error = Assert.Throws<ArgumentException>(() => emulator.Start(config));
        Assert.Contains("must be exactly", error.Message);
    }

    [Fact]
    public void ExternalFileRejectsMissingPath()
    {
        using var rom = CreateRom(CartridgeCompatibility.DmgOnly);
        var missing = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.bin");
        var emulator = new Emulator();

        Assert.Throws<FileNotFoundException>(() =>
            emulator.Start(CreateConfig(rom, HardwareModel.DmgB, BootRomConfig.ExternalFile(missing))));
    }

    [Fact]
    public void BootRomConfigFactoriesExposeOnlyOneImmutableSource()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var externalBytes = BootRomConfig.ExternalBytes(bytes);
        bytes[0] = 9;
        var returned = externalBytes.Bytes;
        returned[1] = 9;

        Assert.Equal(BootRomSource.External, externalBytes.Source);
        Assert.Null(externalBytes.Path);
        Assert.Equal(new byte[] { 1, 2, 3 }, externalBytes.Bytes);

        var externalFile = BootRomConfig.ExternalFile("firmware.bin");
        Assert.Equal("firmware.bin", externalFile.Path);
        Assert.Null(externalFile.Bytes);
        Assert.Throws<ArgumentException>(() => BootRomConfig.ExternalFile(" "));
        Assert.Throws<ArgumentNullException>(() => BootRomConfig.ExternalBytes(null!));
    }

    [Fact]
    public void StartValidatesModelThenImplementationThenCompatibilityBeforeFirmwareIo()
    {
        using var dmgRom = CreateRom(CartridgeCompatibility.DmgOnly);
        using var cgbOnlyRom = CreateRom(CartridgeCompatibility.CgbOnly);
        var missing = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.bin");

        var invalidModel = new Emulator();
        Assert.Throws<ArgumentOutOfRangeException>(() => invalidModel.Start(
            CreateConfig(dmgRom, (HardwareModel)999, BootRomConfig.ExternalFile(missing))));

        var implementedAgb = new Emulator();
        Assert.Throws<FileNotFoundException>(() => implementedAgb.Start(
            CreateConfig(dmgRom, HardwareModel.AgbA, BootRomConfig.ExternalFile(missing))));

        var incompatible = new Emulator();
        var compatibilityError = Assert.Throws<ArgumentException>(() => incompatible.Start(
            CreateConfig(cgbOnlyRom, HardwareModel.DmgB, BootRomConfig.ExternalFile(missing))));
        Assert.Contains("does not support", compatibilityError.Message);
    }

    [Fact]
    public void ExternalImageCanExecuteAndUnmapItself()
    {
        using var rom = CreateRom(CartridgeCompatibility.DmgOnly);
        var image = CreateInstantBootRom(0x100);
        var emulator = Start(rom, HardwareModel.DmgB, BootRomConfig.ExternalBytes(image));

        Assert.True(emulator.Debug.RunUntilProgramCounter(CartridgeEntryPoint, 2));
        Assert.Equal(0x00, emulator.Debug.PeekByte(0x0000));
        emulator.Terminate();
    }

    private static byte[] CreateInstantBootRom(int size)
    {
        var image = new byte[size];
        image[0x00] = 0xC3;
        image[0x01] = 0xFC;
        image[0xFC] = 0x3E;
        image[0xFD] = 0x01;
        image[0xFE] = 0xE0;
        image[0xFF] = 0x50;
        return image;
    }

    private static string WriteBootRom(int size, byte firstByte)
    {
        var image = new byte[size];
        image[0] = firstByte;
        var path = Path.Combine(Path.GetTempPath(), $"gbzemu-boot-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, image);
        return path;
    }

    private static byte[] ReadPalette(Emulator emulator, int indexAddress, int dataAddress, int startIndex)
    {
        var palette = new byte[8];
        for (var offset = 0; offset < palette.Length; offset++)
        {
            emulator.Debug.PokeByte((byte)(startIndex + offset), indexAddress);
            palette[offset] = emulator.Debug.PeekByte(dataAddress);
        }

        return palette;
    }

    private static TestRom CreateRom(CartridgeCompatibility compatibility)
    {
        var rom = TestRom.Create(0x18, 0xFE);
        var bytes = File.ReadAllBytes(rom.Path);
        bytes[0x143] = compatibility switch
        {
            CartridgeCompatibility.CgbCompatible => (byte)0x80,
            CartridgeCompatibility.CgbOnly => (byte)0xC0,
            _ => (byte)0x00
        };
        bytes[0x200] = 0x00;
        File.WriteAllBytes(rom.Path, bytes);
        return rom;
    }

    private static Emulator.Config CreateConfig(TestRom rom, HardwareModel model, BootRomConfig bootRom)
    {
        return new Emulator.Config(model)
        {
            ROMPath = rom.Path,
            SaveLocation = Path.GetTempPath(),
            BootRom = bootRom
        };
    }

    private static Emulator Start(TestRom rom, HardwareModel model, BootRomConfig bootRom)
    {
        var emulator = new Emulator();
        Assert.True(emulator.Start(CreateConfig(rom, model, bootRom)));
        return emulator;
    }
}
