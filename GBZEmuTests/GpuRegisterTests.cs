using GBZEmuLibrary;

namespace GBZEmuTests;

/// <summary>
/// Verifies PPU register masks and interrupt-edge behavior independently of scanline rendering.
/// </summary>
public sealed class GpuRegisterTests
{
    /// <summary>
    /// Verifies that STAT preserves read-only status bits, accepts interrupt-enable bits, and reads unused bit 7 high.
    /// </summary>
    [Fact]
    public void LcdStatusRegisterAppliesHardwareReadAndWriteMasks()
    {
        var gpu = new GPU(new MessageBus());
        gpu.Reset(false);

        gpu.WriteByte(0x00, 0xFF41);
        Assert.Equal(0x85, gpu.ReadByte(0xFF41));

        gpu.WriteByte(0xFF, 0xFF41);
        Assert.Equal(0xFD, gpu.ReadByte(0xFF41));
    }

    /// <summary>
    /// Verifies that a persistent LY=LYC condition requests one LCD interrupt after the comparison clock starts,
    /// then does not retrigger while the condition remains high.
    /// </summary>
    [Fact]
    public void LycCoincidenceInterruptRequiresANewRisingEdge()
    {
        var (gpu, getInterruptRequests) = CreateInterruptCountingGpu();

        gpu.Update(4);
        gpu.WriteByte(0x40, 0xFF41);
        Assert.Equal(0, getInterruptRequests());

        gpu.WriteByte(0x80, 0xFF40);
        Assert.Equal(0, getInterruptRequests());

        gpu.WriteByte(0x01, 0xFF45);
        gpu.WriteByte(0x00, 0xFF45);
        Assert.Equal(1, getInterruptRequests());
        Assert.NotEqual(0, gpu.ReadByte(0xFF41) & 0x04);

        gpu.WriteByte(0x00, 0xFF40);
        gpu.Update(4);
        gpu.Update(4);
        gpu.WriteByte(0x80, 0xFF40);

        Assert.Equal(1, getInterruptRequests());

        gpu.WriteByte(0x00, 0xFF41);
        gpu.WriteByte(0x40, 0xFF41);

        Assert.Equal(2, getInterruptRequests());
    }

    /// <summary>
    /// Verifies that LCD-off retains the last coincidence result, ignores LYC changes while the comparison clock
    /// is stopped, and recomputes the result as soon as the LCD is enabled again.
    /// </summary>
    [Fact]
    public void LycComparisonPausesWhileLcdIsDisabled()
    {
        var (gpu, getInterruptRequests) = CreateInterruptCountingGpu();

        gpu.Update(4);
        gpu.WriteByte(0x40, 0xFF41);
        Assert.Equal(0, getInterruptRequests());
        gpu.WriteByte(0x00, 0xFF41);
        gpu.WriteByte(0x01, 0xFF45);

        Assert.NotEqual(0, gpu.ReadByte(0xFF41) & 0x04);

        gpu.WriteByte(0x80, 0xFF40);
        Assert.Equal(0, gpu.ReadByte(0xFF41) & 0x04);

        gpu.WriteByte(0x40, 0xFF41);
        gpu.WriteByte(0x00, 0xFF40);
        gpu.WriteByte(0x00, 0xFF45);

        Assert.Equal(0, gpu.ReadByte(0xFF41) & 0x04);

        gpu.WriteByte(0x80, 0xFF40);

        Assert.NotEqual(0, gpu.ReadByte(0xFF41) & 0x04);
        Assert.Equal(1, getInterruptRequests());
    }

    /// <summary>
    /// Verifies that enabling another active STAT source does not request a second interrupt while the shared
    /// source line is already held high by LY=LYC.
    /// </summary>
    [Fact]
    public void ActiveStatSourceBlocksAnotherEnabledSource()
    {
        var (gpu, getInterruptRequests) = CreateInterruptCountingGpu();

        gpu.WriteByte(0x80, 0xFF40);
        gpu.WriteByte(0x40, 0xFF41);
        Assert.Equal(1, getInterruptRequests());

        gpu.WriteByte(0x48, 0xFF41);
        Assert.Equal(1, getInterruptRequests());

        gpu.WriteByte(0x08, 0xFF41);
        gpu.WriteByte(0x48, 0xFF41);
        Assert.Equal(2, getInterruptRequests());
    }

    /// <summary>
    /// Verifies that consecutive enabled mode sources share one uninterrupted STAT signal across a scanline
    /// boundary, then generate a new interrupt only after mode 3 lowers the line.
    /// </summary>
    [Fact]
    public void ConsecutiveModeSourcesShareOneInterruptLine()
    {
        var (gpu, getInterruptRequests) = CreateInterruptCountingGpu();

        gpu.Update(4);
        Assert.Equal(0, gpu.ReadByte(0xFF41) & 0x03);
        gpu.WriteByte(0x28, 0xFF41);
        gpu.WriteByte(0x80, 0xFF40);
        gpu.Update(204);

        Assert.Equal(1, getInterruptRequests());
        Assert.Equal(2, gpu.ReadByte(0xFF41) & 0x03);

        gpu.Update(80);
        gpu.Update(172);

        Assert.Equal(2, getInterruptRequests());
        Assert.Equal(0, gpu.ReadByte(0xFF41) & 0x03);
    }

    /// <summary>
    /// Verifies LY resets to 0 four dots into line 153, raises the LYC coincidence edge there, and remains in
    /// VBlank until the physical line finishes.
    /// </summary>
    [Fact]
    public void Line153ResetsLyEarlyBeforeLineZeroMode2()
    {
        var (gpu, getInterruptRequests) = CreateInterruptCountingGpu(gbcMode: true);
        gpu.Update(4);
        gpu.WriteByte(0x80, 0xFF40);

        while (gpu.ReadByte(0xFF44) != 153)
        {
            gpu.Update(4);
        }

        gpu.WriteByte(0x00, 0xFF45);
        gpu.WriteByte(0x40, 0xFF41);
        var interruptsBeforeReset = getInterruptRequests();

        gpu.Update(3);
        Assert.Equal(153, gpu.ReadByte(0xFF44));
        Assert.Equal(1, gpu.ReadByte(0xFF41) & 0x03);

        gpu.Update(1);
        Assert.Equal(0, gpu.ReadByte(0xFF44));
        Assert.Equal(1, gpu.ReadByte(0xFF41) & 0x03);
        Assert.NotEqual(0, gpu.ReadByte(0xFF41) & 0x04);
        Assert.Equal(interruptsBeforeReset + 1, getInterruptRequests());

        gpu.Update(451);
        Assert.Equal(1, gpu.ReadByte(0xFF41) & 0x03);
        gpu.Update(1);
        Assert.Equal(2, gpu.ReadByte(0xFF41) & 0x03);
        Assert.Equal(0, gpu.ReadByte(0xFF44));
    }

    /// <summary>
    /// Verifies the DMG-only mode-2 STAT source pulse when line 144 enters VBlank without exposing mode 2 in STAT.
    /// </summary>
    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public void VBlankStartPulsesMode2SourceOnlyOnDmg(bool gbcMode, int expectedInterruptRequests)
    {
        var (gpu, getInterruptRequests) = CreateInterruptCountingGpu(gbcMode);

        gpu.Update(4);
        gpu.WriteByte(0x80, 0xFF40);
        for (var line = 1; line <= 143; line++)
        {
            gpu.Update(204);
            gpu.Update(80);
            gpu.Update(172);
        }

        gpu.WriteByte(0x20, 0xFF41);
        gpu.Update(204);

        Assert.Equal(144, gpu.ReadByte(0xFF44));
        Assert.Equal(1, gpu.ReadByte(0xFF41) & 0x03);
        Assert.Equal(0, getInterruptRequests());

        gpu.Update(4);

        Assert.Equal(expectedInterruptRequests, getInterruptRequests());
    }

    /// <summary>
    /// Verifies that CPU OAM reads are blocked during modes 2 and 3, then become visible again in HBlank.
    /// </summary>
    [Fact]
    public void OamReadsAreBlockedOnlyWhilePpuUsesOam()
    {
        var gpu = new GPU(new MessageBus());
        gpu.Reset(false);
        gpu.WriteByte(0x5A, 0xFE00);

        Assert.Equal(0x5A, gpu.ReadByte(0xFE00));

        gpu.Update(4);
        gpu.WriteByte(0x80, 0xFF40);
        gpu.Update(204);

        Assert.Equal(2, gpu.ReadByte(0xFF41) & 0x03);
        Assert.Equal(0xFF, gpu.ReadByte(0xFE00));

        gpu.Update(80);

        Assert.Equal(3, gpu.ReadByte(0xFF41) & 0x03);
        Assert.Equal(0xFF, gpu.ReadByte(0xFE00));

        gpu.Update(172);

        Assert.Equal(0, gpu.ReadByte(0xFF41) & 0x03);
        Assert.Equal(0x5A, gpu.ReadByte(0xFE00));
    }

    /// <summary>
    /// Verifies that mode 3 blocks CGB palette RAM reads and writes while write auto-increment still advances.
    /// </summary>
    [Theory]
    [InlineData(0xFF68, 0xFF69)]
    [InlineData(0xFF6A, 0xFF6B)]
    public void CgbPaletteDataIsBlockedDuringPixelTransfer(int indexAddress, int dataAddress)
    {
        var gpu = new GPU(new MessageBus());
        gpu.Reset(true);
        gpu.WriteByte(0x80, indexAddress);
        gpu.WriteByte(0x12, dataAddress);
        gpu.WriteByte(0x80, indexAddress);

        gpu.Update(4);
        gpu.WriteByte(0x80, 0xFF40);
        gpu.Update(HBLANK_CLOCKS);
        gpu.Update(80);

        Assert.Equal(3, gpu.ReadByte(0xFF41) & 0x03);
        Assert.Equal(0xFF, gpu.ReadByte(dataAddress));

        gpu.WriteByte(0x34, dataAddress);

        Assert.Equal(0x81, gpu.ReadByte(indexAddress));
        gpu.WriteByte(0x00, 0xFF40);
        gpu.WriteByte(0x00, indexAddress);
        Assert.Equal(0x12, gpu.ReadByte(dataAddress));
    }

    /// <summary>
    /// Verifies that CGB LCDC.0 controls background priority without disabling background pixel output.
    /// </summary>
    [Fact]
    public void CgbBackgroundRendersWhenMasterPriorityIsDisabled()
    {
        var gpu = new GPU(new MessageBus());
        gpu.Reset(true);
        for (var row = 0; row < 8; row++)
        {
            gpu.WriteByte(0x00, MemorySchema.TILE_DATA_UNSIGNED_START + row * 2);
            gpu.WriteByte(0x80, MemorySchema.TILE_DATA_UNSIGNED_START + row * 2 + 1);
        }

        gpu.WriteByte(0x00, MemorySchema.GPU_GBC_BG_PALETTE_INDEX_REGISTER);
        gpu.WriteByte(0x00, MemorySchema.GPU_GBC_BG_PALETTE_DATA_REGISTER);
        gpu.WriteByte(0x01, MemorySchema.GPU_GBC_BG_PALETTE_INDEX_REGISTER);
        gpu.WriteByte(0x00, MemorySchema.GPU_GBC_BG_PALETTE_DATA_REGISTER);
        gpu.WriteByte(0x04, MemorySchema.GPU_GBC_BG_PALETTE_INDEX_REGISTER);
        gpu.WriteByte(0x1F, MemorySchema.GPU_GBC_BG_PALETTE_DATA_REGISTER);
        gpu.WriteByte(0x05, MemorySchema.GPU_GBC_BG_PALETTE_INDEX_REGISTER);
        gpu.WriteByte(0x00, MemorySchema.GPU_GBC_BG_PALETTE_DATA_REGISTER);

        gpu.Update(4);
        gpu.WriteByte(0x90, 0xFF40);
        gpu.Update(HBLANK_CLOCKS);
        gpu.Update(80);
        gpu.Update(TRANSFERRING_DATA_TO_LCD_DRIVER_CLOCKS);

        Assert.Equal(0, gpu.GetScreenData()[0, 1].R);
        AdvanceToVBlank(gpu);

        var pixel = gpu.GetScreenData()[0, 1];
        Assert.Equal(255, pixel.R);
        Assert.Equal(0, pixel.G);
        Assert.Equal(0, pixel.B);
    }

    /// <summary>
    /// CGB DMG-compatibility mode maps a tile's two-bit color number through BGP before selecting
    /// one of the four RGB555 colors installed by the boot ROM.
    /// </summary>
    [Fact]
    public void CgbCompatibilityBackgroundUsesDmgPaletteRegisterMapping()
    {
        var gpu = new GPU(new MessageBus());
        gpu.Reset(GBCMode.GBCCompatibility, usingBootROM: false);

        var palette = new byte[]
        {
            0x1F, 0x00,
            0x00, 0x00,
            0x00, 0x00,
            0x00, 0x7C
        };
        gpu.WriteByte(0x80, MemorySchema.GPU_GBC_BG_PALETTE_INDEX_REGISTER);
        foreach (var value in palette)
        {
            gpu.WriteByte(value, MemorySchema.GPU_GBC_BG_PALETTE_DATA_REGISTER);
        }

        // Tile color 0 maps to palette color 3, reversing red to blue.
        gpu.WriteByte(0x03, 0xFF47);
        gpu.Update(4);
        gpu.WriteByte(0x91, 0xFF40);
        AdvanceToVBlank(gpu);

        var pixel = gpu.GetScreenData()[0, 1];
        Assert.Equal((0, 0, 255), (pixel.R, pixel.G, pixel.B));
        Assert.Equal(0, pixel.Index);
    }

    /// <summary>
    /// Verifies that disabling the CGB LCD immediately blanks the host-visible framebuffer.
    /// </summary>
    [Fact]
    public void DisablingCgbLcdClearsPublishedFramebuffer()
    {
        var gpu = new GPU(new MessageBus());
        gpu.Reset(true);
        gpu.WriteByte(0x80, MemorySchema.TILE_DATA_UNSIGNED_START);
        gpu.WriteByte(0x1F, MemorySchema.GPU_GBC_BG_PALETTE_DATA_REGISTER);
        gpu.WriteByte(0x80, MemorySchema.GPU_GBC_BG_PALETTE_INDEX_REGISTER);
        gpu.WriteByte(0x1F, MemorySchema.GPU_GBC_BG_PALETTE_DATA_REGISTER);
        gpu.WriteByte(0x80, 0xFF40);
        gpu.Update(HBLANK_CLOCKS);
        gpu.Update(80);
        gpu.Update(TRANSFERRING_DATA_TO_LCD_DRIVER_CLOCKS);
        AdvanceToVBlank(gpu);

        gpu.WriteByte(0x00, 0xFF40);

        var blank = gpu.GetScreenData()[0, 1];
        Assert.Equal((255, 255, 255), (blank.R, blank.G, blank.B));


    }

    /// <summary>
    /// Verifies that the low three SCX bits are latched for the complete scanline before pixel transfer.
    /// </summary>
    [Fact]
    public void DmgLowScrollBitsDoNotChangeDuringPixelTransfer()
    {
        var gpu = new GPU(new MessageBus());
        gpu.Reset(false);
        for (var row = 0; row < 8; row++)
        {
            gpu.WriteByte(0xAA, MemorySchema.TILE_DATA_UNSIGNED_START + row * 2);
            gpu.WriteByte(0x00, MemorySchema.TILE_DATA_UNSIGNED_START + row * 2 + 1);
        }

        gpu.WriteByte(0xE4, 0xFF47);
        gpu.Update(4);
        gpu.WriteByte(0x91, 0xFF40);
        gpu.Update(HBLANK_CLOCKS);
        gpu.Update(80);
        gpu.Update(92);

        gpu.WriteByte(0x01, 0xFF43);
        gpu.Update(80);
        AdvanceToVBlank(gpu);

        var screen = gpu.GetScreenData();
        Assert.Equal(Display.DefaultPalette[1].R, screen[120, 1].R);
        Assert.Equal(Display.DefaultPalette[0].R, screen[121, 1].R);
    }

    /// <summary>
    /// Verifies that the high SCX bits written during mode 3 affect only pixels emitted after the write.
    /// </summary>
    [Fact]
    public void DmgScrollWriteDuringPixelTransferDoesNotShiftEarlierPixels()
    {
        var gpu = new GPU(new MessageBus());
        gpu.Reset(false);
        for (var row = 0; row < 8; row++)
        {
            gpu.WriteByte(0xFF, MemorySchema.TILE_DATA_UNSIGNED_START + row * 2);
            gpu.WriteByte(0x00, MemorySchema.TILE_DATA_UNSIGNED_START + row * 2 + 1);
            gpu.WriteByte(0x00, MemorySchema.TILE_DATA_UNSIGNED_START + 16 + row * 2);
            gpu.WriteByte(0xFF, MemorySchema.TILE_DATA_UNSIGNED_START + 16 + row * 2 + 1);
        }

        for (var tile = 0; tile < 32; tile++)
        {
            gpu.WriteByte((byte)(tile & 1), MemorySchema.BACKGROUND_LAYOUT_0_START + tile);
        }

        gpu.WriteByte(0xE4, 0xFF47);
        gpu.Update(4);
        gpu.WriteByte(0x91, 0xFF40);
        gpu.Update(HBLANK_CLOCKS);
        gpu.Update(80);
        gpu.Update(92);

        gpu.WriteByte(0x08, 0xFF43);
        gpu.Update(80);
        AdvanceToVBlank(gpu);

        var screen = gpu.GetScreenData();
        Assert.Equal(Display.DefaultPalette[2].R, screen[40, 1].R);
        Assert.Equal(Display.DefaultPalette[2].R, screen[112, 1].R);
        Assert.Equal(Display.DefaultPalette[1].R, screen[120, 1].R);
    }

    /// <summary>
    /// Verifies a late SCX write cannot alter background pixels already fetched ahead of LCD output.
    /// </summary>
    [Fact]
    public void DmgLateScrollWritePreservesFetchedBackgroundPixels()
    {
        var gpu = new GPU(new MessageBus());
        gpu.Reset(false);
        for (var row = 0; row < 8; row++)
        {
            gpu.WriteByte(0xFF, MemorySchema.TILE_DATA_UNSIGNED_START + row * 2);
            gpu.WriteByte(0x00, MemorySchema.TILE_DATA_UNSIGNED_START + row * 2 + 1);
            gpu.WriteByte(0x00, MemorySchema.TILE_DATA_UNSIGNED_START + 16 + row * 2);
            gpu.WriteByte(0xFF, MemorySchema.TILE_DATA_UNSIGNED_START + 16 + row * 2 + 1);
        }

        for (var tile = 0; tile < 32; tile++)
        {
            gpu.WriteByte((byte)(tile & 1), MemorySchema.BACKGROUND_LAYOUT_0_START + tile);
        }

        gpu.WriteByte(0xE4, 0xFF47);
        gpu.Update(4);
        gpu.WriteByte(0x91, 0xFF40);
        gpu.Update(HBLANK_CLOCKS);
        gpu.Update(80);
        gpu.Update(140);

        gpu.WriteByte(0x08, 0xFF43);
        gpu.Update(40);
        AdvanceToVBlank(gpu);

        var screen = gpu.GetScreenData();
        Assert.Equal(Display.DefaultPalette[2].R, screen[143, 1].R);
        Assert.Equal(Display.DefaultPalette[1].R, screen[152, 1].R);
    }

    /// <summary>
    /// Verifies that a DMG palette write during mode 3 affects only pixels emitted after the write.
    /// </summary>
    [Fact]
    public void DmgPaletteWriteDuringPixelTransferDoesNotRecolorEarlierPixels()
    {
        var gpu = new GPU(new MessageBus());
        gpu.Reset(false);
        for (var row = 0; row < 8; row++)
        {
            gpu.WriteByte(0xFF, MemorySchema.TILE_DATA_UNSIGNED_START + row * 2);
            gpu.WriteByte(0x00, MemorySchema.TILE_DATA_UNSIGNED_START + row * 2 + 1);
        }

        gpu.WriteByte(0xE4, 0xFF47);
        gpu.Update(4);
        gpu.WriteByte(0x91, 0xFF40);
        gpu.Update(HBLANK_CLOCKS);
        gpu.Update(80);
        gpu.Update(92);

        gpu.WriteByte(0xFC, 0xFF47);
        gpu.Update(80);
        AdvanceToVBlank(gpu);

        var screen = gpu.GetScreenData();
        Assert.Equal(Display.DefaultPalette[1].R, screen[40, 1].R);
        Assert.Equal(Display.DefaultPalette[3].R, screen[120, 1].R);
    }

    /// <summary>
    /// Verifies that HBlank DMA cannot retroactively change the scanline whose pixel transfer just completed.
    /// </summary>
    [Fact]
    public void CompletedScanLineIsRenderedBeforeHBlankCallback()
    {
        var messageBus = new MessageBus();
        var gpu = new GPU(messageBus);
        gpu.Reset(false);
        for (var row = 0; row < 8; row++)
        {
            gpu.WriteByte(0x00, MemorySchema.TILE_DATA_UNSIGNED_START + row * 2);
            gpu.WriteByte(0x80, MemorySchema.TILE_DATA_UNSIGNED_START + row * 2 + 1);
        }

        gpu.WriteByte(0xE4, 0xFF47);
        messageBus.OnHBlank = () => gpu.WriteByte(0x00, MemorySchema.TILE_DATA_UNSIGNED_START + 3);

        gpu.Update(4);
        gpu.WriteByte(0x91, 0xFF40);
        gpu.Update(HBLANK_CLOCKS);
        gpu.Update(80);
        gpu.Update(TRANSFERRING_DATA_TO_LCD_DRIVER_CLOCKS);

        Assert.Equal(0, gpu.GetScreenData()[0, 1].R);
        AdvanceToVBlank(gpu);

        var pixel = gpu.GetScreenData()[0, 1];
        Assert.Equal(Display.DefaultPalette[2].R, pixel.R);
        Assert.Equal(Display.DefaultPalette[2].G, pixel.G);
        Assert.Equal(Display.DefaultPalette[2].B, pixel.B);
        Assert.Equal(0x00, gpu.ReadByte(MemorySchema.TILE_DATA_UNSIGNED_START + 3));
    }

    /// <summary>
    /// Verifies that one large update consumes every reachable mode transition, renders the completed line,
    /// and starts HBlank exactly once.
    /// </summary>
    [Fact]
    public void UpdateConsumesMultipleModeTransitions()
    {
        var messageBus = new MessageBus();
        var gpu = new GPU(messageBus);
        var hBlankCount = 0;
        messageBus.OnHBlank = () => hBlankCount++;
        gpu.Reset(false);
        for (var row = 0; row < 8; row++)
        {
            gpu.WriteByte(0xFF, MemorySchema.TILE_DATA_UNSIGNED_START + row * 2);
            gpu.WriteByte(0x00, MemorySchema.TILE_DATA_UNSIGNED_START + row * 2 + 1);
        }

        gpu.WriteByte(0xE4, 0xFF47);
        gpu.Update(4);
        gpu.WriteByte(0x91, 0xFF40);
        gpu.Update(HBLANK_CLOCKS + 80 + TRANSFERRING_DATA_TO_LCD_DRIVER_CLOCKS);

        Assert.Equal(1, gpu.ReadByte(0xFF44));
        Assert.Equal(0, gpu.ReadByte(0xFF41) & 0x03);
        Assert.Equal(1, hBlankCount);

        AdvanceToVBlank(gpu);
        Assert.Equal(Display.DefaultPalette[1].R, gpu.GetScreenData()[159, 1].R);
    }

    /// <summary>
    /// Verifies fine SCX delays the first visible pixel and extends mode 3 by its low three bits while shortening
    /// HBlank by the same amount. This is the timing measured by Mooneye hblank_ly_scx_timing-GS.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void FineScrollExtendsMode3AndPreservesScanlineLength(byte fineScroll)
    {
        var gpu = CreateGpuAtMode3(Array.Empty<byte>(), scrollX: fineScroll);

        gpu.Update(TRANSFERRING_DATA_TO_LCD_DRIVER_CLOCKS + fineScroll - 1);
        Assert.Equal(3, gpu.ReadByte(0xFF41) & 0x03);

        gpu.Update(1);
        Assert.Equal(0, gpu.ReadByte(0xFF41) & 0x03);

        gpu.Update(HBLANK_CLOCKS - fineScroll - 1);
        Assert.Equal(1, gpu.ReadByte(0xFF44));

        gpu.Update(1);
        Assert.Equal(2, gpu.ReadByte(0xFF44));
        Assert.Equal(2, gpu.ReadByte(0xFF41) & 0x03);
    }

    /// <summary>
    /// Verifies sprite fetches extend mode 3 by six dots each and share one alignment wait at the same X coordinate.
    /// </summary>
    [Theory]
    [InlineData(new byte[] { 0 }, 8)]
    [InlineData(new byte[] { 0, 0 }, 16)]
    [InlineData(new byte[] { 0, 1 }, 16)]
    [InlineData(new byte[] { 8, 0 }, 20)]
    [InlineData(new byte[] { 168 }, 0)]
    public void SpriteFetchesExtendMode3ByPosition(byte[] spriteXCoordinates, int expectedPenalty)
    {
        var gpu = CreateGpuAtMode3(spriteXCoordinates);

        gpu.Update(TRANSFERRING_DATA_TO_LCD_DRIVER_CLOCKS + expectedPenalty - 1);
        Assert.Equal(3, gpu.ReadByte(0xFF41) & 0x03);

        gpu.Update(1);
        Assert.Equal(0, gpu.ReadByte(0xFF41) & 0x03);
    }

    /// <summary>
    /// Verifies that an object at OAM X=0 pays the full initial fetch wait regardless of SCX alignment.
    /// </summary>
    [Fact]
    public void SpriteAtZeroUsesFullFetchWaitWhenBackgroundIsScrolled()
    {
        var gpu = CreateGpuAtMode3(new byte[] { 0 }, scrollX: 7);
        const int expectedPenalty = 15;

        gpu.Update(TRANSFERRING_DATA_TO_LCD_DRIVER_CLOCKS + expectedPenalty - 1);
        Assert.Equal(3, gpu.ReadByte(0xFF41) & 0x03);

        gpu.Update(1);
        Assert.Equal(0, gpu.ReadByte(0xFF41) & 0x03);
    }

    /// <summary>
    /// Verifies that LCDC.1 gates sprite fetch timing on DMG while CGB hardware continues fetching objects.
    /// </summary>
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 8)]
    public void SpriteFetchTimingHonorsHardwareModeWhenObjectsAreDisabled(bool gbcMode, int expectedPenalty)
    {
        var gpu = CreateGpuAtMode3(new byte[] { 0 }, gbcMode, objectsEnabled: false);

        gpu.Update(TRANSFERRING_DATA_TO_LCD_DRIVER_CLOCKS + expectedPenalty - 1);
        Assert.Equal(3, gpu.ReadByte(0xFF41) & 0x03);

        gpu.Update(1);
        Assert.Equal(0, gpu.ReadByte(0xFF41) & 0x03);
    }

    /// <summary>
    /// Verifies that the first ten Y-matching OAM entries consume the scanline sprite limit even when clipped.
    /// </summary>
    [Fact]
    public void SpriteFetchTimingHonorsTenSpriteScanlineLimit()
    {
        var gpu = CreateGpuAtMode3(new byte[] { 168, 168, 168, 168, 168, 168, 168, 168, 168, 168, 0 });

        gpu.Update(TRANSFERRING_DATA_TO_LCD_DRIVER_CLOCKS);

        Assert.Equal(0, gpu.ReadByte(0xFF41) & 0x03);
    }

    /// <summary>
    /// Verifies that extending sprite transfer shortens HBlank so each visible scanline remains exactly 456 dots.
    /// </summary>
    [Fact]
    public void SpriteFetchPenaltyPreservesScanlineLength()
    {
        var gpu = CreateGpuAtMode3(new byte[] { 0 });
        const int spritePenalty = 8;

        gpu.Update(TRANSFERRING_DATA_TO_LCD_DRIVER_CLOCKS + spritePenalty);
        gpu.Update(HBLANK_CLOCKS - spritePenalty - 1);

        Assert.Equal(1, gpu.ReadByte(0xFF44));
        Assert.Equal(0, gpu.ReadByte(0xFF41) & 0x03);

        gpu.Update(1);

        Assert.Equal(2, gpu.ReadByte(0xFF44));
        Assert.Equal(2, gpu.ReadByte(0xFF41) & 0x03);
    }

    private const int HBLANK_CLOCKS = 204;
    private const int TRANSFERRING_DATA_TO_LCD_DRIVER_CLOCKS = 172;

    private static void AdvanceToVBlank(GPU gpu)
    {
        while (gpu.ReadByte(0xFF44) < Display.VERTICAL_RESOLUTION)
        {
            gpu.Update(4);
        }
    }

    private static GPU CreateGpuAtMode3(
        byte[] spriteXCoordinates,
        bool gbcMode = false,
        bool objectsEnabled = true,
        byte scrollX = 0)
    {
        var gpu = new GPU(new MessageBus());
        gpu.Reset(gbcMode);
        gpu.WriteByte(scrollX, 0xFF43);

        for (var index = 0; index < spriteXCoordinates.Length; index++)
        {
            var spriteAddress = MemorySchema.SPRITE_ATTRIBUTE_TABLE_START + index * 4;
            gpu.WriteByte(17, spriteAddress);
            gpu.WriteByte(spriteXCoordinates[index], spriteAddress + 1);
        }

        gpu.Update(4);
        gpu.WriteByte((byte)(objectsEnabled ? 0x82 : 0x80), 0xFF40);
        gpu.Update(HBLANK_CLOCKS);
        gpu.Update(80);

        Assert.Equal(3, gpu.ReadByte(0xFF41) & 0x03);
        return gpu;
    }

    private static (GPU Gpu, Func<int> GetInterruptRequests) CreateInterruptCountingGpu(bool gbcMode = false)
    {
        var messageBus = new MessageBus();
        var gpu = new GPU(messageBus);
        var interruptRequests = 0;
        messageBus.OnRequestInterrupt = interrupt =>
        {
            if (interrupt == Interrupts.LCD)
            {
                interruptRequests++;
            }
        };

        gpu.Reset(gbcMode);
        return (gpu, () => interruptRequests);
    }
}
