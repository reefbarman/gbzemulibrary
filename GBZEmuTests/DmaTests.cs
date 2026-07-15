using GBZEmuLibrary;

namespace GBZEmuTests;

/// <summary>
/// Verifies CGB VRAM DMA block sizing, address masking, and HBlank progression independently of PPU timing.
/// </summary>
public sealed class DmaTests
{
    /// <summary>
    /// Verifies that GDMA copies the encoded number of 16-byte blocks, ignores the low address nibbles,
    /// and reports completion through HDMA5.
    /// </summary>
    [Fact]
    public void GeneralPurposeDmaCopiesCompleteBlocks()
    {
        var memory = new byte[MemorySchema.MAX_RAM_SIZE];
        using var fixture = new DmaFixture(memory);
        var dma = fixture.Controller;
        for (var index = 0; index < 0x20; index++)
        {
            memory[0xC120 + index] = (byte)(0x40 + index);
        }

        dma.WriteByte(0xC1, MemorySchema.DMA_GBC_SOURCE_HIGH_REGISTER);
        dma.WriteByte(0x2F, MemorySchema.DMA_GBC_SOURCE_LOW_REGISTER);
        dma.WriteByte(0xE1, MemorySchema.DMA_GBC_DESTINATION_HIGH_REGISTER);
        dma.WriteByte(0x3F, MemorySchema.DMA_GBC_DESTINATION_LOW_REGISTER);
        dma.WriteByte(0x01, MemorySchema.DMA_GBC_LENGTH_MODE_START_REGISTER);

        Assert.Equal(memory[0xC120..0xC140], memory[0x8130..0x8150]);
        Assert.Equal(0xFF, dma.ReadByte(MemorySchema.DMA_GBC_LENGTH_MODE_START_REGISTER));
    }

    /// <summary>
    /// Verifies through the MMU that GDMA writes to the VRAM bank selected by VBK, as CGB games require when
    /// uploading tile attributes independently from bank-zero tile IDs.
    /// </summary>
    [Fact]
    public void GeneralPurposeDmaWritesToSelectedVramBank()
    {
        using var rom = TestRom.Create(0x00);
        var romBytes = File.ReadAllBytes(rom.Path);
        romBytes[CartridgeSchema.GBC_MODE_LOC] = 0x80;
        File.WriteAllBytes(rom.Path, romBytes);

        var emulator = new Emulator();
        Assert.True(emulator.Start(new Emulator.Config
        {
            ROMPath = rom.Path,
            SaveLocation = Path.GetTempPath(),
            BootMode = BootMode.GBC | BootMode.Force | BootMode.Skip
        }));

        for (var index = 0; index < 0x10; index++)
        {
            emulator.Debug.PokeByte((byte)(0xA0 + index), 0xC000 + index);
        }

        emulator.Debug.PokeByte(0x01, MemorySchema.GPU_VRAM_BANK_REGISTER);
        emulator.Debug.PokeByte(0xC0, MemorySchema.DMA_GBC_SOURCE_HIGH_REGISTER);
        emulator.Debug.PokeByte(0x00, MemorySchema.DMA_GBC_SOURCE_LOW_REGISTER);
        emulator.Debug.PokeByte(0x00, MemorySchema.DMA_GBC_DESTINATION_HIGH_REGISTER);
        emulator.Debug.PokeByte(0x00, MemorySchema.DMA_GBC_DESTINATION_LOW_REGISTER);
        emulator.Debug.PokeByte(0x00, MemorySchema.DMA_GBC_LENGTH_MODE_START_REGISTER);

        for (var index = 0; index < 0x10; index++)
        {
            Assert.Equal((byte)(0xA0 + index), emulator.Debug.PeekByte(0x8000 + index));
        }

        emulator.Debug.PokeByte(0x00, MemorySchema.GPU_VRAM_BANK_REGISTER);
        for (var index = 0; index < 0x10; index++)
        {
            Assert.Equal(0x00, emulator.Debug.PeekByte(0x8000 + index));
        }

        Assert.Equal(0xFF, emulator.Debug.PeekByte(MemorySchema.DMA_GBC_LENGTH_MODE_START_REGISTER));
        emulator.Terminate();
    }

    /// <summary>
    /// Verifies that HDMA copies one 16-byte block per HBlank and exposes the remaining block count in HDMA5.
    /// </summary>
    [Fact]
    public void HBlankDmaCopiesOneBlockPerHBlank()
    {
        var memory = new byte[MemorySchema.MAX_RAM_SIZE];
        using var fixture = new DmaFixture(memory);
        var dma = fixture.Controller;
        for (var index = 0; index < 0x20; index++)
        {
            memory[0xD000 + index] = (byte)(0x80 + index);
        }

        dma.WriteByte(0xD0, MemorySchema.DMA_GBC_SOURCE_HIGH_REGISTER);
        dma.WriteByte(0x00, MemorySchema.DMA_GBC_SOURCE_LOW_REGISTER);
        dma.WriteByte(0x02, MemorySchema.DMA_GBC_DESTINATION_HIGH_REGISTER);
        dma.WriteByte(0x00, MemorySchema.DMA_GBC_DESTINATION_LOW_REGISTER);
        dma.WriteByte(0x81, MemorySchema.DMA_GBC_LENGTH_MODE_START_REGISTER);

        Assert.Equal(0x01, dma.ReadByte(MemorySchema.DMA_GBC_LENGTH_MODE_START_REGISTER));
        Assert.Equal(0x00, dma.ReadByte(MemorySchema.DMA_GBC_LENGTH_MODE_START_REGISTER) & 0x80);
        Assert.All(memory[0x8200..0x8220], value => Assert.Equal(0x00, value));

        MessageBus.Instance.HBlankStarted();

        Assert.Equal(memory[0xD000..0xD010], memory[0x8200..0x8210]);
        Assert.All(memory[0x8210..0x8220], value => Assert.Equal(0x00, value));
        Assert.Equal(0x00, dma.ReadByte(MemorySchema.DMA_GBC_LENGTH_MODE_START_REGISTER));

        MessageBus.Instance.HBlankStarted();

        Assert.Equal(memory[0xD000..0xD020], memory[0x8200..0x8220]);
        Assert.Equal(0xFF, dma.ReadByte(MemorySchema.DMA_GBC_LENGTH_MODE_START_REGISTER));
    }

    /// <summary>
    /// Verifies that clearing HDMA5 bit 7 cancels an active transfer without copying another block and marks the
    /// remaining-block value as inactive.
    /// </summary>
    [Fact]
    public void HBlankDmaCanBeCancelled()
    {
        var memory = new byte[MemorySchema.MAX_RAM_SIZE];
        using var fixture = new DmaFixture(memory);
        var dma = fixture.Controller;
        for (var index = 0; index < 0x20; index++)
        {
            memory[0xC000 + index] = (byte)(0x20 + index);
        }

        dma.WriteByte(0xC0, MemorySchema.DMA_GBC_SOURCE_HIGH_REGISTER);
        dma.WriteByte(0x00, MemorySchema.DMA_GBC_SOURCE_LOW_REGISTER);
        dma.WriteByte(0x00, MemorySchema.DMA_GBC_DESTINATION_HIGH_REGISTER);
        dma.WriteByte(0x00, MemorySchema.DMA_GBC_DESTINATION_LOW_REGISTER);
        dma.WriteByte(0x81, MemorySchema.DMA_GBC_LENGTH_MODE_START_REGISTER);
        MessageBus.Instance.HBlankStarted();

        dma.WriteByte(0x00, MemorySchema.DMA_GBC_LENGTH_MODE_START_REGISTER);
        MessageBus.Instance.HBlankStarted();

        Assert.Equal(memory[0xC000..0xC010], memory[0x8000..0x8010]);
        Assert.All(memory[0x8010..0x8020], value => Assert.Equal(0x00, value));
        Assert.Equal(0x80, dma.ReadByte(MemorySchema.DMA_GBC_LENGTH_MODE_START_REGISTER));
    }

    /// <summary>
    /// Connects a DMA controller to deterministic memory and restores the process-global message-bus callbacks afterward.
    /// </summary>
    private sealed class DmaFixture : IDisposable
    {
        private readonly Func<int, byte> _previousReadByte;
        private readonly Action<byte, int> _previousWriteByte;
        private readonly Action _previousHBlank;

        public DmaFixture(byte[] memory)
        {
            _previousReadByte = MessageBus.Instance.OnReadByte;
            _previousWriteByte = MessageBus.Instance.OnWriteByte;
            _previousHBlank = MessageBus.Instance.OnHBlank;
            MessageBus.Instance.OnReadByte = address => memory[address];
            MessageBus.Instance.OnWriteByte = (data, address) => memory[address] = data;
            Controller = new DMAController();
        }

        public DMAController Controller { get; }

        public void Dispose()
        {
            MessageBus.Instance.OnReadByte = _previousReadByte;
            MessageBus.Instance.OnWriteByte = _previousWriteByte;
            MessageBus.Instance.OnHBlank = _previousHBlank;
        }
    }
}
