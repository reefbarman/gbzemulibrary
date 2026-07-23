using GBZEmuLibrary;

namespace GBZEmuTests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ExecutionAllocationCollection
{
    public const string Name = "Execution allocation";
}

/// <summary>
/// Guards warmed integrated CPU, PPU, and DMA execution against steady-state managed allocations.
/// </summary>
[Collection(ExecutionAllocationCollection.Name)]
public sealed class ExecutionAllocationTests
{
    [Fact]
    public void WarmedCpuPpuAndDmaExecutionStaysWithinAllocationBudget()
    {
        using var rom = CreateCgbDmaWorkloadRom();
        var emulator = new Emulator();
        Assert.True(emulator.Start(new Emulator.Config(HardwareModel.CgbE)
        {
            ROMPath = rom.Path,
            SaveLocation = Path.GetTempPath(),
            BootRom = BootRomConfig.Skip()
        }));

        try
        {
            PopulateDmaSources(emulator);
            for (var frame = 0; frame < 16; frame++)
            {
                emulator.Update();
                emulator.GetSoundSamples(out _);
            }

            MeasureAllocatedBytes(emulator, 1);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var first = MeasureAllocatedBytes(emulator, 32);
            var second = MeasureAllocatedBytes(emulator, 32);
            var third = MeasureAllocatedBytes(emulator, 32);
            var allocated = Math.Min(first, Math.Min(second, third));

            Assert.InRange(allocated, 0, 512);
            Assert.True(emulator.Debug.GetCpuState().ExecutedInstructionCount > 0);
            Assert.Equal(0xFF, emulator.Debug.PeekByte(MemorySchema.VIDEO_RAM_START));
            Assert.Equal(16, emulator.Debug.PeekByte(MemorySchema.SPRITE_ATTRIBUTE_TABLE_START));
        }
        finally
        {
            emulator.Terminate();
        }
    }

    private static long MeasureAllocatedBytes(Emulator emulator, int frameCount)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var frame = 0; frame < frameCount; frame++)
        {
            emulator.Update();
            emulator.GetSoundSamples(out _);
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static void PopulateDmaSources(Emulator emulator)
    {
        for (var index = 0; index < 0x10; index++)
        {
            emulator.Debug.PokeByte((byte)(index % 2 == 0 ? 0xFF : 0x00), 0xC100 + index);
        }

        for (var sprite = 0; sprite < 40; sprite++)
        {
            var address = 0xC000 + sprite * 4;
            emulator.Debug.PokeByte((byte)(16 + sprite / 10 * 8), address);
            emulator.Debug.PokeByte((byte)(8 + sprite % 10 * 8), address + 1);
            emulator.Debug.PokeByte(0x00, address + 2);
            emulator.Debug.PokeByte(0x00, address + 3);
        }
    }

    private static TestRom CreateCgbDmaWorkloadRom()
    {
        var rom = TestRom.Create(
            0x3E, 0x93,       // LD A,$93
            0xE0, 0x40,       // LDH (LCDC),A
            0x3E, 0xC1,       // loop: LD A,$C1
            0xE0, 0x51,       // HDMA1 = $C1
            0xAF,             // XOR A
            0xE0, 0x52,       // HDMA2 = $00
            0xE0, 0x53,       // HDMA3 = $00
            0xE0, 0x54,       // HDMA4 = $00
            0x3E, 0x80,       // LD A,$80
            0xE0, 0x55,       // HDMA5: one HBlank block
            0x3E, 0xC0,       // LD A,$C0
            0xE0, 0x46,       // Start OAM DMA from $C000
            0x06, 0x00,       // LD B,$00 (256 iterations)
            0x05,             // delay: DEC B
            0x20, 0xFD,       // JR NZ,delay
            0xC3, 0x04, 0x01);// JP loop
        var bytes = File.ReadAllBytes(rom.Path);
        bytes[CartridgeSchema.GBC_MODE_LOC] = 0xC0;
        File.WriteAllBytes(rom.Path, bytes);
        return rom;
    }
}
