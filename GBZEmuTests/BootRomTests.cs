using GBZEmuLibrary;

namespace GBZEmuTests;

/// <summary>
/// Verifies the embedded GBZEmu boot ROMs: they run when no host image is supplied, they hand
/// off to the cartridge with the same register and I/O state as the skip-boot profile, and the
/// Skip and host-supplied boot ROM paths keep their existing behavior.
/// </summary>
public sealed class BootRomTests
{
    private const ushort CartridgeEntryPoint = 0x100;
    private const int LongBootFrameBudget = 400;

    /// <summary>
    /// Registers covered by the emulator's skip-boot profile that a completed boot must reproduce.
    /// Timing-dependent registers (DIV, LY) are intentionally absent.
    /// </summary>
    private static readonly int[] SkipProfileRegisters =
    {
        0xFF40, 0xFF42, 0xFF43, 0xFF45, 0xFF47, 0xFF48, 0xFF49, 0xFF4A, 0xFF4B,
        0xFF05, 0xFF06, 0xFF07, 0xFF0F, 0xFFFF,
        0xFF10, 0xFF11, 0xFF12, 0xFF14, 0xFF24, 0xFF25, 0xFF26
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BuiltInBootRomMatchesSkipProfileAtHandoff(bool gbc)
    {
        using var rom = CreateRom(gbc);
        var bootMode = gbc ? BootMode.GBC : BootMode.DMG | BootMode.Force;

        var booted = Start(rom, bootMode);
        Assert.True(booted.Debug.RunUntilProgramCounter(CartridgeEntryPoint, LongBootFrameBudget));

        var skipped = Start(rom, bootMode | BootMode.Skip);

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

        booted.Terminate();
        skipped.Terminate();
    }

    [Fact]
    public void ShortBootModeShortensTheDmgAnimation()
    {
        using var rom = CreateRom(gbc: false);

        var shortBoot = Start(rom, BootMode.DMG | BootMode.Force | BootMode.Short);
        Assert.True(shortBoot.Debug.RunUntilProgramCounter(CartridgeEntryPoint, 40));
        shortBoot.Terminate();

        var longBoot = Start(rom, BootMode.DMG | BootMode.Force);
        Assert.False(longBoot.Debug.RunUntilProgramCounter(CartridgeEntryPoint, 40));
        Assert.True(longBoot.Debug.RunUntilProgramCounter(CartridgeEntryPoint, LongBootFrameBudget));
        longBoot.Terminate();
    }

    /// <summary>
    /// The built-in DMG animation leaves its completed lockup visible long enough to avoid an
    /// abrupt transition from the final scroll position into cartridge execution.
    /// </summary>
    [Fact]
    public void BuiltInDmgBootRomHoldsSettledLogoBeforeHandoff()
    {
        using var rom = CreateRom(gbc: false);
        var emulator = Start(rom, BootMode.DMG | BootMode.Force);
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

    /// <summary>
    /// A Nintendo-licensed DMG cart whose title checksum appears in the boot ROM hash table runs
    /// in GBC compatibility mode, colorized by the palettes the boot ROM wrote.
    /// </summary>
    [Fact]
    public void BuiltInCgbBootRomColorizesKnownDmgCart()
    {
        using var rom = CreateRom(gbc: false);
        var bytes = File.ReadAllBytes(rom.Path);
        bytes[0x134] = 0x88; // title checksum present in the hash table
        bytes[0x14B] = 0x01; // Nintendo old-license code
        File.WriteAllBytes(rom.Path, bytes);

        var emulator = Start(rom, BootMode.GBC);
        Assert.True(emulator.Debug.RunUntilProgramCounter(CartridgeEntryPoint, LongBootFrameBudget));

        // The selected title combination installs black as BG palette 0 color 3
        // instead of retaining the $FFFF power-on default.
        emulator.Debug.PokeByte(0x06, 0xFF68);
        Assert.Equal(0x00, emulator.Debug.PeekByte(0xFF69));

        // BG palette 7 color 3 is the deep-navy fill where the reveal settles ($38C4).
        emulator.Debug.PokeByte(0x3E, 0xFF68);
        Assert.Equal(0xC4, emulator.Debug.PeekByte(0xFF69));
        emulator.Debug.PokeByte(0x3F, 0xFF68);
        Assert.Equal(0x38, emulator.Debug.PeekByte(0xFF69));

        // Cartridge execution starts during vblank, matching the stock CGB firmware's
        // phase closely enough that a first-frame interrupt cannot fire during line 0.
        var ppu = emulator.Debug.GetPpuState();
        Assert.Equal(144, ppu.ScanLine);
        Assert.Equal(1, ppu.Mode);

        // Both tile maps are blank in both VRAM banks, while the boot ROM's tile data
        // remains available just as it does after the stock CGB firmware hand-off.
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

    /// <summary>
    /// Donkey Kong Land's 16-byte title selects the stock combination with separate OBJ0, OBJ1, and BG palettes.
    /// This guards the complete title-specific lookup rather than merely checking that a title hash is recognized.
    /// </summary>
    [Fact]
    public void BuiltInCgbBootRomSelectsDonkeyKongLandPalettes()
    {
        using var rom = CreateRom(gbc: false);
        var bytes = File.ReadAllBytes(rom.Path);
        "DONKEYKONGLAND95"u8.CopyTo(bytes.AsSpan(0x134, 16));
        bytes[0x14B] = 0x01;
        File.WriteAllBytes(rom.Path, bytes);

        var emulator = Start(rom, BootMode.GBC | BootMode.Force);
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
    public void SkipModeBypassesTheBuiltInBootRom()
    {
        using var rom = CreateRom(gbc: false);
        var emulator = Start(rom, BootMode.GBC | BootMode.Skip);

        Assert.Equal(CartridgeEntryPoint, emulator.Debug.GetCpuState().PC);
        Assert.Equal(0x00, emulator.Debug.PeekByte(0x0000)); // cartridge, not boot ROM, is mapped
        emulator.Terminate();
    }

    [Fact]
    public void HostSuppliedBootRomOverridesTheBuiltInImage()
    {
        using var rom = CreateRom(gbc: false);

        // A minimal image that unmaps itself immediately: jp $00FC; ld a, $01; ldh [$FF50], a.
        var hostBootRom = new byte[0x100];
        hostBootRom[0x00] = 0xC3;
        hostBootRom[0x01] = 0xFC;
        hostBootRom[0xFC] = 0x3E;
        hostBootRom[0xFD] = 0x01;
        hostBootRom[0xFE] = 0xE0;
        hostBootRom[0xFF] = 0x50;

        var emulator = new Emulator();
        Assert.True(emulator.Start(new Emulator.Config
        {
            ROMPath = rom.Path,
            SaveLocation = Path.GetTempPath(),
            BootROM = hostBootRom,
            BootMode = BootMode.DMG | BootMode.Force
        }));

        // The built-in animation takes over 100 frames; an instant hand-off proves the host image ran.
        Assert.True(emulator.Debug.RunUntilProgramCounter(CartridgeEntryPoint, 2));
        emulator.Terminate();
    }

    /// <summary>
    /// Config.BootROMPaths slots each file by size, so external DMG and CGB images can be
    /// supplied together and each overrides the matching built-in image.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BootRomPathsFillBothSlotsBySize(bool gbc)
    {
        using var rom = CreateRom(gbc);

        // Instant hand-off images: jp $00FC; ld a, $01; ldh [$FF50], a.
        var dmgPath = WriteInstantBootRom(0x100);
        var gbcPath = WriteInstantBootRom(0x900);

        try
        {
            var emulator = new Emulator();
            Assert.True(emulator.Start(new Emulator.Config
            {
                ROMPath = rom.Path,
                SaveLocation = Path.GetTempPath(),
                BootROMPaths = new[] { dmgPath, gbcPath },
                BootMode = gbc ? BootMode.GBC : BootMode.DMG | BootMode.Force
            }));

            // The built-in animations take over 100 frames; an instant hand-off proves the
            // external image for this hardware type ran.
            Assert.True(emulator.Debug.RunUntilProgramCounter(CartridgeEntryPoint, 2));
            emulator.Terminate();
        }
        finally
        {
            File.Delete(dmgPath);
            File.Delete(gbcPath);
        }
    }

    private static string WriteInstantBootRom(int size)
    {
        var image = new byte[size];
        image[0x00] = 0xC3;
        image[0x01] = 0xFC;
        image[0xFC] = 0x3E;
        image[0xFD] = 0x01;
        image[0xFE] = 0xE0;
        image[0xFF] = 0x50;

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

    private static TestRom CreateRom(bool gbc)
    {
        var rom = TestRom.Create(0x18, 0xFE); // jr @ $0100
        if (gbc)
        {
            var bytes = File.ReadAllBytes(rom.Path);
            bytes[0x143] = 0x80;
            File.WriteAllBytes(rom.Path, bytes);
        }

        return rom;
    }

    private static Emulator Start(TestRom rom, BootMode bootMode)
    {
        var emulator = new Emulator();
        var started = emulator.Start(new Emulator.Config
        {
            ROMPath = rom.Path,
            SaveLocation = Path.GetTempPath(),
            BootMode = bootMode
        });

        Assert.True(started);
        return emulator;
    }
}
