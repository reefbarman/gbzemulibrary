using GBZEmuLibrary;

namespace GBZEmuTests;

/// <summary>
/// Verifies CGB VRAM DMA block sizing, address masking, and HBlank progression independently of PPU timing.
/// </summary>
public sealed class DmaTests
{
    /// <summary>
    /// Verifies fresh OAM DMA waits two machine cycles, copies one byte per following cycle, and releases OAM after
    /// exactly 160 copied bytes.
    /// </summary>
    [Fact]
    public void OamDmaUsesClockedStartupAndTransfer()
    {
        var memory = new byte[MemorySchema.MAX_RAM_SIZE];
        var fixture = new DmaFixture(memory);
        for (var index = 0; index < 0xA0; index++)
        {
            memory[0xC000 + index] = (byte)(0x20 + index);
        }

        fixture.Controller.WriteByte(0xC0, MemorySchema.DMA_REGISTER);
        Assert.Equal(0xC0, fixture.Controller.ReadByte(MemorySchema.DMA_REGISTER));
        Assert.False(fixture.Controller.IsOamDmaActive);

        fixture.UpdateMachineCycle();
        Assert.False(fixture.Controller.IsOamDmaActive);
        fixture.UpdateMachineCycle();
        Assert.True(fixture.Controller.IsOamDmaActive);
        Assert.Equal(0x00, memory[MemorySchema.SPRITE_ATTRIBUTE_TABLE_START]);
        Assert.Equal(0x20, fixture.Controller.OamDmaBusValue);

        fixture.UpdateMachineCycle();
        Assert.Equal(0x20, memory[MemorySchema.SPRITE_ATTRIBUTE_TABLE_START]);
        Assert.Equal(0x21, fixture.Controller.OamDmaBusValue);
        for (var index = 1; index < 0xA0; index++)
        {
            fixture.UpdateMachineCycle();
        }

        Assert.False(fixture.Controller.IsOamDmaActive);
        Assert.Equal(memory[0xC000..0xC0A0], memory[0xFE00..0xFEA0]);
    }

    /// <summary>
    /// Verifies OAM DMA advances only on T1 while T2 through T4 preserve its startup and transfer state.
    /// </summary>
    [Fact]
    public void OamDmaAdvancesOncePerFourRawClocks()
    {
        var memory = new byte[MemorySchema.MAX_RAM_SIZE];
        var fixture = new DmaFixture(memory);
        memory[0xC000] = 0x5A;
        fixture.Controller.WriteByte(0xC0, MemorySchema.DMA_REGISTER);

        fixture.AdvanceRawClock();
        Assert.False(fixture.Controller.IsOamDmaActive);
        fixture.AdvanceRawClock(3);
        Assert.False(fixture.Controller.IsOamDmaActive);

        fixture.AdvanceRawClock();
        Assert.True(fixture.Controller.IsOamDmaActive);
        Assert.Equal(0x00, memory[MemorySchema.SPRITE_ATTRIBUTE_TABLE_START]);
        fixture.AdvanceRawClock(3);
        Assert.Equal(0x00, memory[MemorySchema.SPRITE_ATTRIBUTE_TABLE_START]);

        fixture.AdvanceRawClock();
        Assert.Equal(0x5A, memory[MemorySchema.SPRITE_ATTRIBUTE_TABLE_START]);
    }

    /// <summary>
    /// Verifies restarting OAM DMA leaves the previous transfer active for both startup cycles before the new source
    /// begins again at OAM byte zero.
    /// </summary>
    [Fact]
    public void OamDmaRestartKeepsPreviousTransferActiveDuringDelay()
    {
        var memory = new byte[MemorySchema.MAX_RAM_SIZE];
        var fixture = new DmaFixture(memory);
        memory[0xC000] = 0x11;
        memory[0xC001] = 0x12;
        memory[0xC002] = 0x13;
        memory[0xD000] = 0x44;

        fixture.Controller.WriteByte(0xC0, MemorySchema.DMA_REGISTER);
        fixture.UpdateMachineCycle(3);
        Assert.Equal(0x11, memory[0xFE00]);

        fixture.Controller.WriteByte(0xD0, MemorySchema.DMA_REGISTER);
        fixture.UpdateMachineCycle();
        Assert.Equal(0x12, memory[0xFE01]);
        fixture.UpdateMachineCycle();
        Assert.Equal(0x13, memory[0xFE02]);
        Assert.True(fixture.Controller.IsOamDmaActive);

        fixture.UpdateMachineCycle();
        Assert.Equal(0x44, memory[0xFE00]);
        Assert.Equal(0xD0, fixture.Controller.ReadByte(MemorySchema.DMA_REGISTER));
    }

    /// <summary>
    /// Verifies CGB invalid high-memory source values select the external-RAM bus instead of CPU-visible I/O or OAM.
    /// </summary>
    [Fact]
    public void CgbOamDmaHighSourceUsesExternalRamDecoder()
    {
        var memory = new byte[MemorySchema.MAX_RAM_SIZE];
        var fixture = new DmaFixture(memory, mode: GBCMode.GBCOnly);
        memory[0xBE00] = 0x5A;

        fixture.Controller.WriteByte(0xFE, MemorySchema.DMA_REGISTER);
        fixture.UpdateMachineCycle(3);

        Assert.Equal(0x5A, memory[0xFE00]);
    }

    /// <summary>
    /// Verifies OAM and CGB DMA dispatch exclusively through their dedicated privileged memory ports.
    /// </summary>
    [Fact]
    public void DmaEnginesUseDedicatedInitiatorPorts()
    {
        var memory = new byte[MemorySchema.MAX_RAM_SIZE];
        memory[0xC000] = 0x5A;
        var cgbReads = 0;
        var cgbWrites = 0;
        var oamReads = 0;
        var oamWrites = 0;
        var messageBus = new MessageBus
        {
            OnReadCgbDmaSourceByte = address =>
            {
                cgbReads++;
                return memory[address];
            },
            OnWriteCgbDmaDestinationByte = (data, address) =>
            {
                cgbWrites++;
                memory[address] = data;
            },
            OnReadOamDmaSourceByte = address =>
            {
                oamReads++;
                return memory[address];
            },
            OnWriteOamDmaByte = (data, address) =>
            {
                oamWrites++;
                memory[address] = data;
            },
            OnCanStartHBlankDmaImmediately = () => false,
            OnIsCpuHalted = () => false,
            OnGetCpuSpeedFactor = () => 1
        };
        var dma = new DMAController(messageBus);
        dma.Init(GBCMode.GBCSupport);
        dma.WriteByte(0xC0, MemorySchema.DMA_GBC_SOURCE_HIGH_REGISTER);
        dma.WriteByte(0x00, MemorySchema.DMA_GBC_SOURCE_LOW_REGISTER);
        dma.WriteByte(0x00, MemorySchema.DMA_GBC_DESTINATION_HIGH_REGISTER);
        dma.WriteByte(0x00, MemorySchema.DMA_GBC_DESTINATION_LOW_REGISTER);
        dma.WriteByte(0x00, MemorySchema.DMA_GBC_LENGTH_MODE_START_REGISTER);

        Assert.Equal(16, cgbReads);
        Assert.Equal(16, cgbWrites);
        Assert.Equal(0, oamReads);
        Assert.Equal(0, oamWrites);

        dma.WriteByte(0xC0, MemorySchema.DMA_REGISTER);
        dma.Update(InstructionSchema.FOUR_CYCLES * 3);

        Assert.Equal(16, cgbReads);
        Assert.Equal(16, cgbWrites);
        Assert.Equal(2, oamReads);
        Assert.Equal(1, oamWrites);
    }

    /// <summary>
    /// Verifies that GDMA copies the encoded number of 16-byte blocks, ignores the low address nibbles,
    /// and reports completion through HDMA5.
    /// </summary>
    [Fact]
    public void GeneralPurposeDmaCopiesCompleteBlocks()
    {
        var memory = new byte[MemorySchema.MAX_RAM_SIZE];
        var fixture = new DmaFixture(memory);
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
    /// Verifies that completed DMA blocks advance the internal source and destination used by a later HDMA5 start,
    /// while the address registers themselves remain write-only.
    /// </summary>
    [Fact]
    public void ConsecutiveGeneralPurposeDmaStartsContinueFromAdvancedAddresses()
    {
        var memory = new byte[MemorySchema.MAX_RAM_SIZE];
        var fixture = new DmaFixture(memory);
        var dma = fixture.Controller;
        for (var index = 0; index < 0x20; index++)
        {
            memory[0xC000 + index] = (byte)(0x20 + index);
        }

        dma.WriteByte(0xC0, MemorySchema.DMA_GBC_SOURCE_HIGH_REGISTER);
        dma.WriteByte(0x00, MemorySchema.DMA_GBC_SOURCE_LOW_REGISTER);
        dma.WriteByte(0x00, MemorySchema.DMA_GBC_DESTINATION_HIGH_REGISTER);
        dma.WriteByte(0x00, MemorySchema.DMA_GBC_DESTINATION_LOW_REGISTER);
        dma.WriteByte(0x00, MemorySchema.DMA_GBC_LENGTH_MODE_START_REGISTER);
        dma.WriteByte(0x00, MemorySchema.DMA_GBC_LENGTH_MODE_START_REGISTER);

        Assert.Equal(memory[0xC000..0xC020], memory[0x8000..0x8020]);
        Assert.Equal(0xFF, dma.ReadByte(MemorySchema.DMA_GBC_SOURCE_HIGH_REGISTER));
        Assert.Equal(0xFF, dma.ReadByte(MemorySchema.DMA_GBC_SOURCE_LOW_REGISTER));
        Assert.Equal(0xFF, dma.ReadByte(MemorySchema.DMA_GBC_DESTINATION_HIGH_REGISTER));
        Assert.Equal(0xFF, dma.ReadByte(MemorySchema.DMA_GBC_DESTINATION_LOW_REGISTER));
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
        Assert.True(emulator.Start(new Emulator.Config(HardwareModel.CgbE)
        {
            ROMPath = rom.Path,
            SaveLocation = Path.GetTempPath(),
            BootRom = BootRomConfig.Skip()
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
    /// Verifies that HDMA reserves eight transfer cycles plus its release cycle per HBlank block, then updates HDMA5.
    /// </summary>
    [Fact]
    public void HBlankDmaCopiesOneBlockPerHBlank()
    {
        var memory = new byte[MemorySchema.MAX_RAM_SIZE];
        var fixture = new DmaFixture(memory);
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

        fixture.HBlankDmaWindowOpened();

        Assert.True(dma.IsCpuStalledByHBlankDma);
        fixture.UpdateMachineCycle(7);
        Assert.All(memory[0x8200..0x8220], value => Assert.Equal(0x00, value));
        fixture.UpdateMachineCycle(2);

        Assert.Equal(memory[0xD000..0xD010], memory[0x8200..0x8210]);
        Assert.All(memory[0x8210..0x8220], value => Assert.Equal(0x00, value));
        Assert.Equal(0x00, dma.ReadByte(MemorySchema.DMA_GBC_LENGTH_MODE_START_REGISTER));
        Assert.False(dma.IsCpuStalledByHBlankDma);

        fixture.HBlankDmaWindowOpened();
        fixture.UpdateMachineCycle(9);

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
        var fixture = new DmaFixture(memory);
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
        fixture.HBlankDmaWindowOpened();
        fixture.UpdateMachineCycle(9);

        dma.WriteByte(0x00, MemorySchema.DMA_GBC_LENGTH_MODE_START_REGISTER);
        fixture.HBlankDmaWindowOpened();

        Assert.Equal(memory[0xC000..0xC010], memory[0x8000..0x8010]);
        Assert.All(memory[0x8010..0x8020], value => Assert.Equal(0x00, value));
        Assert.Equal(0x80, dma.ReadByte(MemorySchema.DMA_GBC_LENGTH_MODE_START_REGISTER));
    }

    /// <summary>
    /// Verifies that starting HBlank DMA with the LCD off or already in HBlank schedules its first block immediately.
    /// </summary>
    [Fact]
    public void HBlankDmaStartCanCopyFirstBlockImmediately()
    {
        var memory = new byte[MemorySchema.MAX_RAM_SIZE];
        var fixture = new DmaFixture(memory, startHBlankDmaImmediately: true);
        var dma = fixture.Controller;
        for (var index = 0; index < 0x20; index++)
        {
            memory[0xC000 + index] = (byte)(0x60 + index);
        }

        dma.WriteByte(0xC0, MemorySchema.DMA_GBC_SOURCE_HIGH_REGISTER);
        dma.WriteByte(0x00, MemorySchema.DMA_GBC_SOURCE_LOW_REGISTER);
        dma.WriteByte(0x00, MemorySchema.DMA_GBC_DESTINATION_HIGH_REGISTER);
        dma.WriteByte(0x00, MemorySchema.DMA_GBC_DESTINATION_LOW_REGISTER);
        dma.WriteByte(0x83, MemorySchema.DMA_GBC_LENGTH_MODE_START_REGISTER);

        Assert.True(dma.IsCpuStalledByHBlankDma);
        Assert.All(memory[0x8000..0x8020], value => Assert.Equal(0x00, value));
        fixture.UpdateMachineCycle(10);

        Assert.Equal(memory[0xC000..0xC010], memory[0x8000..0x8010]);
        Assert.All(memory[0x8010..0x8020], value => Assert.Equal(0x00, value));
        Assert.Equal(0x02, dma.ReadByte(MemorySchema.DMA_GBC_LENGTH_MODE_START_REGISTER));
    }

    /// <summary>
    /// Verifies that cancelling HBlank DMA exposes the cancel write's length bits with the inactive flag set.
    /// </summary>
    [Fact]
    public void HBlankDmaCancellationReloadsVisibleLengthBits()
    {
        var memory = new byte[MemorySchema.MAX_RAM_SIZE];
        var fixture = new DmaFixture(memory, startHBlankDmaImmediately: true);
        var dma = fixture.Controller;

        dma.WriteByte(0xC0, MemorySchema.DMA_GBC_SOURCE_HIGH_REGISTER);
        dma.WriteByte(0x00, MemorySchema.DMA_GBC_SOURCE_LOW_REGISTER);
        dma.WriteByte(0x00, MemorySchema.DMA_GBC_DESTINATION_HIGH_REGISTER);
        dma.WriteByte(0x00, MemorySchema.DMA_GBC_DESTINATION_LOW_REGISTER);
        dma.WriteByte(0x83, MemorySchema.DMA_GBC_LENGTH_MODE_START_REGISTER);
        dma.WriteByte(0x00, MemorySchema.DMA_GBC_LENGTH_MODE_START_REGISTER);

        Assert.Equal(0x80, dma.ReadByte(MemorySchema.DMA_GBC_LENGTH_MODE_START_REGISTER));
        Assert.False(dma.IsCpuStalledByHBlankDma);
    }

    /// <summary>
    /// Verifies an immediate HBlank-DMA block completes after its exact forty-raw-clock reservation.
    /// </summary>
    [Fact]
    public void ImmediateHBlankDmaCountsEveryRawClock()
    {
        var memory = new byte[MemorySchema.MAX_RAM_SIZE];
        var fixture = new DmaFixture(memory, startHBlankDmaImmediately: true);
        memory[0xC000] = 0x6B;
        fixture.Controller.WriteByte(0xC0, MemorySchema.DMA_GBC_SOURCE_HIGH_REGISTER);
        fixture.Controller.WriteByte(0x00, MemorySchema.DMA_GBC_SOURCE_LOW_REGISTER);
        fixture.Controller.WriteByte(0x00, MemorySchema.DMA_GBC_DESTINATION_HIGH_REGISTER);
        fixture.Controller.WriteByte(0x00, MemorySchema.DMA_GBC_DESTINATION_LOW_REGISTER);
        fixture.Controller.WriteByte(0x80, MemorySchema.DMA_GBC_LENGTH_MODE_START_REGISTER);

        fixture.AdvanceRawClock(39);
        Assert.True(fixture.Controller.IsCpuStalledByHBlankDma);
        Assert.Equal(0x00, memory[MemorySchema.VIDEO_RAM_START]);

        fixture.AdvanceRawClock();
        Assert.False(fixture.Controller.IsCpuStalledByHBlankDma);
        Assert.Equal(0x6B, memory[MemorySchema.VIDEO_RAM_START]);
    }

    /// <summary>
    /// Verifies partial OAM/HDMA raw-clock phase survives direct v4 machine-state serialization.
    /// </summary>
    [Fact]
    public void PartialDmaRawClockPhaseRoundTripsThroughStateSerialization()
    {
        var originalMemory = new byte[MemorySchema.MAX_RAM_SIZE];
        var restoredMemory = new byte[MemorySchema.MAX_RAM_SIZE];
        originalMemory[0xC000] = 0x7C;
        restoredMemory[0xC000] = 0x7C;
        var original = new DmaFixture(originalMemory, startHBlankDmaImmediately: true);
        var restored = new DmaFixture(restoredMemory, startHBlankDmaImmediately: true);
        original.Controller.WriteByte(0xC0, MemorySchema.DMA_GBC_SOURCE_HIGH_REGISTER);
        original.Controller.WriteByte(0x00, MemorySchema.DMA_GBC_SOURCE_LOW_REGISTER);
        original.Controller.WriteByte(0x00, MemorySchema.DMA_GBC_DESTINATION_HIGH_REGISTER);
        original.Controller.WriteByte(0x00, MemorySchema.DMA_GBC_DESTINATION_LOW_REGISTER);
        original.Controller.WriteByte(0x80, MemorySchema.DMA_GBC_LENGTH_MODE_START_REGISTER);
        original.AdvanceRawClock(17);

        var serialized = StateSerialization.Write(original.Controller);
        StateSerialization.Read(serialized, restored.Controller);

        original.AdvanceRawClock(22);
        restored.AdvanceRawClock(22);
        Assert.True(original.Controller.IsCpuStalledByHBlankDma);
        Assert.True(restored.Controller.IsCpuStalledByHBlankDma);
        original.AdvanceRawClock();
        restored.AdvanceRawClock();
        Assert.Equal(originalMemory[MemorySchema.VIDEO_RAM_START], restoredMemory[MemorySchema.VIDEO_RAM_START]);
        Assert.Equal(0x7C, restoredMemory[MemorySchema.VIDEO_RAM_START]);
    }

    /// <summary>
    /// Verifies that HBlank blocks are skipped while HALT owns the CPU and begin at the next HBlank after wake.
    /// </summary>
    [Fact]
    public void HBlankDmaDoesNotRunDuringCpuHalt()
    {
        var memory = new byte[MemorySchema.MAX_RAM_SIZE];
        var fixture = new DmaFixture(memory, cpuHalted: true);
        var dma = fixture.Controller;
        memory[0xC000] = 0x5A;

        dma.WriteByte(0xC0, MemorySchema.DMA_GBC_SOURCE_HIGH_REGISTER);
        dma.WriteByte(0x00, MemorySchema.DMA_GBC_SOURCE_LOW_REGISTER);
        dma.WriteByte(0x00, MemorySchema.DMA_GBC_DESTINATION_HIGH_REGISTER);
        dma.WriteByte(0x00, MemorySchema.DMA_GBC_DESTINATION_LOW_REGISTER);
        dma.WriteByte(0x80, MemorySchema.DMA_GBC_LENGTH_MODE_START_REGISTER);

        fixture.HBlankDmaWindowOpened();
        fixture.UpdateMachineCycle(8);

        Assert.False(dma.IsCpuStalledByHBlankDma);
        Assert.Equal(0x00, memory[0x8000]);
        Assert.Equal(0x00, dma.ReadByte(MemorySchema.DMA_GBC_LENGTH_MODE_START_REGISTER));

        fixture.CpuHalted = false;
        fixture.HBlankDmaWindowOpened();
        fixture.UpdateMachineCycle(9);

        Assert.Equal(0x5A, memory[0x8000]);
        Assert.Equal(0xFF, dma.ReadByte(MemorySchema.DMA_GBC_LENGTH_MODE_START_REGISTER));
    }

    /// <summary>
    /// Verifies that double-speed CGB execution consumes twice as many raw CPU machine cycles for one HDMA block.
    /// </summary>
    [Fact]
    public void HBlankDmaPreservesDotDurationAtDoubleSpeed()
    {
        var memory = new byte[MemorySchema.MAX_RAM_SIZE];
        var fixture = new DmaFixture(memory, startHBlankDmaImmediately: true, speedFactor: 2);
        var dma = fixture.Controller;
        memory[0xC000] = 0x6B;

        dma.WriteByte(0xC0, MemorySchema.DMA_GBC_SOURCE_HIGH_REGISTER);
        dma.WriteByte(0x00, MemorySchema.DMA_GBC_SOURCE_LOW_REGISTER);
        dma.WriteByte(0x00, MemorySchema.DMA_GBC_DESTINATION_HIGH_REGISTER);
        dma.WriteByte(0x00, MemorySchema.DMA_GBC_DESTINATION_LOW_REGISTER);
        dma.WriteByte(0x80, MemorySchema.DMA_GBC_LENGTH_MODE_START_REGISTER);

        fixture.UpdateMachineCycle(17);
        Assert.True(dma.IsCpuStalledByHBlankDma);
        Assert.Equal(0x00, memory[0x8000]);

        fixture.UpdateMachineCycle();
        Assert.False(dma.IsCpuStalledByHBlankDma);
        Assert.Equal(0x6B, memory[0x8000]);
    }

    /// <summary>
    /// Connects a DMA controller to deterministic memory through its own instance-scoped bus.
    /// </summary>
    private sealed class DmaFixture
    {
        private readonly MessageBus _messageBus = new MessageBus();

        public DmaFixture(
            byte[] memory,
            bool startHBlankDmaImmediately = false,
            GBCMode mode = GBCMode.NoGBC,
            bool cpuHalted = false,
            int speedFactor = 1)
        {
            _messageBus.OnReadCgbDmaSourceByte = address => memory[address];
            _messageBus.OnWriteCgbDmaDestinationByte = (data, address) => memory[address] = data;
            _messageBus.OnReadOamDmaSourceByte = address => memory[address];
            _messageBus.OnWriteOamDmaByte = (data, address) => memory[address] = data;
            _messageBus.OnCanStartHBlankDmaImmediately = () => startHBlankDmaImmediately;
            _messageBus.OnIsCpuHalted = () => CpuHalted;
            _messageBus.OnGetCpuSpeedFactor = () => speedFactor;
            CpuHalted = cpuHalted;
            Controller = new DMAController(_messageBus);
            Controller.Init(mode);
        }

        public DMAController Controller { get; }
        public bool CpuHalted { get; set; }

        public void HBlankDmaWindowOpened()
        {
            _messageBus.HBlankDmaWindowOpened();
        }

        public void UpdateMachineCycle(int count = 1)
        {
            Controller.Update(InstructionSchema.FOUR_CYCLES * count);
        }

        public void AdvanceRawClock(int count = 1)
        {
            for (var clock = 0; clock < count; clock++)
            {
                Controller.AdvanceRawClock();
            }
        }
    }
}
