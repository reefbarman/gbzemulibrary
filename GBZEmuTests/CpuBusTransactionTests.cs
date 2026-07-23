using System.Reflection;
using GBZEmuLibrary;

namespace GBZEmuTests;

/// <summary>
/// Verifies the structural CPU/MMU transaction seam while Batch 5.2 retains compatibility access ordering.
/// </summary>
public sealed class CpuBusTransactionTests
{
    /// <summary>
    /// Verifies transaction metadata is fixed-size value state without references or policy objects.
    /// </summary>
    [Fact]
    public void TransactionContainsOnlyValueTypeMetadata()
    {
        var transaction = new CpuBusTransaction(
            CpuMachineCycleKind.MemoryWrite,
            0xC123,
            0x5A,
            true,
            0x6B);

        Assert.Equal(CpuMachineCycleKind.MemoryWrite, transaction.Kind);
        Assert.Equal(0xC123, transaction.Address);
        Assert.Equal(0x5A, transaction.WriteData);
        Assert.True(transaction.OamDmaBlockedAtT1);
        Assert.Equal(0x6B, transaction.OamDmaBusValueAtT1);
        Assert.False(transaction.WriteDataLatchedBeforeCompletion);
        Assert.All(
            typeof(CpuBusTransaction).GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => Assert.True(field.FieldType.IsValueType));
    }

    /// <summary>
    /// Verifies a CPU read retains the OAM-DMA ownership and bus value captured when its transaction began.
    /// </summary>
    [Fact]
    public void BlockedReadRetainsT1OamDmaValueThroughCompletion()
    {
        var fixture = new MmuFixture();
        fixture.Mmu.WriteByteUntimed(0x5A, 0xC000);
        fixture.StartOamDma(0xC0);

        var transaction = fixture.Mmu.BeginCpuRead(0xC000, CpuMachineCycleKind.MemoryRead);
        fixture.Mmu.WriteByteUntimed(0x6B, 0xC000);
        fixture.Mmu.AdvanceDmaRawClock();

        Assert.True(transaction.OamDmaBlockedAtT1);
        Assert.Equal(0x5A, transaction.OamDmaBusValueAtT1);
        Assert.False(transaction.ReadDataLatchedBeforeCompletion);
        Assert.Equal(0x5A, fixture.Mmu.CompleteCpuRead(in transaction));
    }

    /// <summary>
    /// Verifies ordinary memory drives stable read data before the CPU transaction completes at T4.
    /// </summary>
    [Fact]
    public void OrdinaryReadRetainsDeviceDataThroughCompletion()
    {
        var fixture = new MmuFixture();
        fixture.Mmu.WriteByteUntimed(0x5A, 0xC000);

        var transaction = fixture.Mmu.BeginCpuRead(0xC000, CpuMachineCycleKind.MemoryRead);
        fixture.Mmu.WriteByteUntimed(0x6B, 0xC000);

        Assert.True(transaction.ReadDataLatchedBeforeCompletion);
        Assert.Equal(0x5A, transaction.ReadDataBeforeCompletion);
        Assert.Equal(0x5A, fixture.Mmu.CompleteCpuRead(in transaction));
    }

    /// <summary>
    /// Verifies PPU-sensitive memory resolves its data and access state when the CPU transaction completes.
    /// </summary>
    [Fact]
    public void PpuSensitiveReadSamplesDeviceDataAtCompletion()
    {
        var fixture = new MmuFixture();
        fixture.Mmu.WriteByteUntimed(0x5A, MemorySchema.VIDEO_RAM_START);

        var transaction = fixture.Mmu.BeginCpuRead(
            MemorySchema.VIDEO_RAM_START,
            CpuMachineCycleKind.MemoryRead);
        fixture.Mmu.WriteByteUntimed(0x6B, MemorySchema.VIDEO_RAM_START);

        Assert.False(transaction.ReadDataLatchedBeforeCompletion);
        Assert.Equal(0x6B, fixture.Mmu.CompleteCpuRead(in transaction));
    }

    /// <summary>
    /// Verifies CGB palette data and its PPU accessibility are resolved when the CPU transaction completes.
    /// </summary>
    [Fact]
    public void CgbPaletteReadSamplesDeviceDataAtCompletion()
    {
        var fixture = new MmuFixture(GBCMode.GBCSupport, HardwareModel.CgbE);
        fixture.Mmu.WriteByteUntimed(0x00, MemorySchema.GPU_GBC_BG_PALETTE_INDEX_REGISTER);
        fixture.Mmu.WriteByteUntimed(0x5A, MemorySchema.GPU_GBC_BG_PALETTE_DATA_REGISTER);

        var transaction = fixture.Mmu.BeginCpuRead(
            MemorySchema.GPU_GBC_BG_PALETTE_DATA_REGISTER,
            CpuMachineCycleKind.MemoryRead);
        fixture.Mmu.WriteByteUntimed(0x6B, MemorySchema.GPU_GBC_BG_PALETTE_DATA_REGISTER);

        Assert.False(transaction.ReadDataLatchedBeforeCompletion);
        Assert.Equal(0x6B, fixture.Mmu.CompleteCpuRead(in transaction));
    }

    /// <summary>
    /// Verifies a natural OAM-DMA source switch after an unblocked write begins does not retroactively cancel it.
    /// </summary>
    [Fact]
    public void UnblockedWriteRetainsT1OwnershipThroughCompletion()
    {
        var fixture = new MmuFixture();
        fixture.StartOamDma(0xC0);
        var transaction = fixture.Mmu.BeginCpuWrite(0x7C, MemorySchema.VIDEO_RAM_START);

        fixture.StartOamDma(0x80);
        fixture.AdvanceMachineCycles(2);
        fixture.CompleteCpuWrite(in transaction);

        Assert.False(transaction.OamDmaBlockedAtT1);
        Assert.True(fixture.Mmu.IsCpuAccessBlockedByOamDma(MemorySchema.VIDEO_RAM_START));
        Assert.Equal(0x7C, fixture.Mmu.ReadByteUntimed(MemorySchema.VIDEO_RAM_START));
    }

    /// <summary>
    /// Verifies a natural OAM-DMA source switch after a blocked write begins does not retroactively permit it.
    /// </summary>
    [Fact]
    public void BlockedWriteRetainsT1OwnershipThroughCompletion()
    {
        var fixture = new MmuFixture();
        fixture.StartOamDma(0x80);
        var transaction = fixture.Mmu.BeginCpuWrite(0x7C, MemorySchema.VIDEO_RAM_START);

        fixture.StartOamDma(0xC0);
        fixture.AdvanceMachineCycles(2);
        fixture.CompleteCpuWrite(in transaction);

        Assert.True(transaction.OamDmaBlockedAtT1);
        Assert.False(fixture.Mmu.IsCpuAccessBlockedByOamDma(MemorySchema.VIDEO_RAM_START));
        Assert.Equal(0x00, fixture.Mmu.ReadByteUntimed(MemorySchema.VIDEO_RAM_START));
    }

    /// <summary>
    /// Verifies timer and APU devices consume their write input at T1 while CPU completion remains a later boundary.
    /// </summary>
    [Theory]
    [InlineData(MemorySchema.DIVIDE_REGISTER, 0x00)]
    [InlineData(MemorySchema.TIMA, 0x5A)]
    [InlineData(APUSchema.VIN_VOL_CONTROL, 0x5A)]
    public void ClockedDeviceWritesLatchBeforeCpuCompletion(int address, byte expectedValue)
    {
        var fixture = new MmuFixture();
        if (address >= MemorySchema.APU_REGISTERS_START)
        {
            fixture.Mmu.WriteByteUntimed(0x80, APUSchema.SOUND_ENABLED);
        }
        else if (address == MemorySchema.DIVIDE_REGISTER)
        {
            fixture.AdvanceMachineCycles(64);
        }

        var transaction = fixture.Mmu.BeginCpuWrite(0x5A, address);

        Assert.True(transaction.WriteDataLatchedBeforeCompletion);
        Assert.Equal(expectedValue, fixture.Mmu.ReadByteUntimed(address));

        fixture.CompleteCpuWrite(in transaction);

        Assert.Equal(expectedValue, fixture.Mmu.ReadByteUntimed(address));
    }

    /// <summary>
    /// Verifies LCDC latches through its device owner before T4 while ordinary memory waits for CPU completion.
    /// </summary>
    [Fact]
    public void LcdControlLatchesBeforeOrdinaryWriteCompletion()
    {
        var fixture = new MmuFixture();
        var lcdTransaction = fixture.Mmu.BeginCpuWrite(0x80, MemorySchema.GPU_REGISTERS_START);

        Assert.True(lcdTransaction.WriteDataLatchedBeforeCompletion);
        Assert.Equal(0x80, fixture.Mmu.ReadByteUntimed(MemorySchema.GPU_REGISTERS_START));

        fixture.CompleteCpuWrite(in lcdTransaction);

        Assert.Equal(0x80, fixture.Mmu.ReadByteUntimed(MemorySchema.GPU_REGISTERS_START));

        var transaction = fixture.Mmu.BeginCpuWrite(0x5A, 0xC000);

        Assert.False(transaction.WriteDataLatchedBeforeCompletion);
        Assert.Equal(0x00, fixture.Mmu.ReadByteUntimed(0xC000));

        fixture.CompleteCpuWrite(in transaction);

        Assert.Equal(0x5A, fixture.Mmu.ReadByteUntimed(0xC000));
    }

    /// <summary>
    /// Verifies an OAM-DMA start write does not schedule its startup pipeline before CPU completion.
    /// </summary>
    [Fact]
    public void OamDmaGuestStartBeginsOnlyAfterT4Completion()
    {
        var fixture = new MmuFixture();
        var transaction = fixture.Mmu.BeginCpuWrite(0xC0, MemorySchema.DMA_REGISTER);

        Assert.Equal(0x00, fixture.Mmu.ReadByteUntimed(MemorySchema.DMA_REGISTER));
        Assert.False(fixture.Mmu.DmaController.IsOamDmaActive);

        fixture.CompleteCpuWrite(in transaction);

        Assert.Equal(0xC0, fixture.Mmu.ReadByteUntimed(MemorySchema.DMA_REGISTER));
        Assert.False(fixture.Mmu.DmaController.IsOamDmaActive);
        fixture.AdvanceMachineCycles(1);
        Assert.True(fixture.Mmu.DmaController.IsOamDmaActive);
    }

    /// <summary>
    /// Verifies an active HBlank-DMA transfer remains active until its CPU cancellation write completes.
    /// </summary>
    [Fact]
    public void HBlankDmaGuestCancellationWaitsForT4Completion()
    {
        var fixture = new MmuFixture(GBCMode.GBCSupport, HardwareModel.CgbE);
        fixture.Mmu.WriteByteUntimed(0xC0, MemorySchema.DMA_GBC_SOURCE_HIGH_REGISTER);
        fixture.Mmu.WriteByteUntimed(0x00, MemorySchema.DMA_GBC_SOURCE_LOW_REGISTER);
        fixture.Mmu.WriteByteUntimed(0x00, MemorySchema.DMA_GBC_DESTINATION_HIGH_REGISTER);
        fixture.Mmu.WriteByteUntimed(0x00, MemorySchema.DMA_GBC_DESTINATION_LOW_REGISTER);
        fixture.Mmu.WriteByteUntimed(0x81, MemorySchema.DMA_GBC_LENGTH_MODE_START_REGISTER);
        var transaction = fixture.Mmu.BeginCpuWrite(0x00, MemorySchema.DMA_GBC_LENGTH_MODE_START_REGISTER);

        Assert.Equal(0x01, fixture.Mmu.ReadByteUntimed(MemorySchema.DMA_GBC_LENGTH_MODE_START_REGISTER));

        fixture.CompleteCpuWrite(in transaction);

        Assert.Equal(0x80, fixture.Mmu.ReadByteUntimed(MemorySchema.DMA_GBC_LENGTH_MODE_START_REGISTER));
        Assert.False(fixture.Mmu.IsCpuStalledByHBlankDma);
    }

    /// <summary>
    /// Verifies immediate HDMA scheduled at CPU completion reserves only its remaining raw clocks at each CPU speed.
    /// </summary>
    [Theory]
    [InlineData(1, 36)]
    [InlineData(2, 68)]
    public void ImmediateHBlankDmaCpuCompletionUsesRemainingRawClocks(int speedFactor, int expectedRawClocks)
    {
        var fixture = new MmuFixture(
            GBCMode.GBCSupport,
            HardwareModel.CgbE,
            startHBlankDmaImmediately: true,
            speedFactor: speedFactor);
        fixture.Mmu.WriteByteUntimed(0x6B, 0xC000);
        fixture.Mmu.WriteByteUntimed(0xC0, MemorySchema.DMA_GBC_SOURCE_HIGH_REGISTER);
        fixture.Mmu.WriteByteUntimed(0x00, MemorySchema.DMA_GBC_SOURCE_LOW_REGISTER);
        fixture.Mmu.WriteByteUntimed(0x00, MemorySchema.DMA_GBC_DESTINATION_HIGH_REGISTER);
        fixture.Mmu.WriteByteUntimed(0x00, MemorySchema.DMA_GBC_DESTINATION_LOW_REGISTER);
        var transaction = fixture.Mmu.BeginCpuWrite(0x80, MemorySchema.DMA_GBC_LENGTH_MODE_START_REGISTER);

        fixture.Mmu.LatchCpuWriteAtT4(in transaction);
        fixture.Mmu.AdvanceDmaRawClock();
        fixture.Mmu.CompleteCpuWrite(in transaction);
        for (var clock = 1; clock < expectedRawClocks; clock++)
        {
            fixture.Mmu.AdvanceDmaRawClock();
        }

        Assert.True(fixture.Mmu.IsCpuStalledByHBlankDma);
        Assert.Equal(0x00, fixture.Mmu.ReadByteUntimed(MemorySchema.VIDEO_RAM_START));

        fixture.Mmu.AdvanceDmaRawClock();

        Assert.False(fixture.Mmu.IsCpuStalledByHBlankDma);
        Assert.Equal(0x6B, fixture.Mmu.ReadByteUntimed(MemorySchema.VIDEO_RAM_START));
    }

    /// <summary>
    /// Verifies the boot-ROM overlay remains active until an FF50 CPU transaction completes.
    /// </summary>
    [Fact]
    public void BootRomUnmapWaitsForT4Completion()
    {
        using var rom = TestRom.Create(0x00);
        var bootImage = new byte[0x100];
        bootImage[0] = 0xA5;
        using var fixture = new MmuFixture(
            romPath: rom.Path,
            bootRomConfig: BootRomConfig.ExternalBytes(bootImage));
        var transaction = fixture.Mmu.BeginCpuWrite(0x01, MemorySchema.BOOT_ROM_DISABLE_REGISTER);

        Assert.True(fixture.Mmu.InBootROM);
        Assert.Equal(0xA5, fixture.Mmu.ReadByteUntimed(0x0000));

        fixture.CompleteCpuWrite(in transaction);

        Assert.False(fixture.Mmu.InBootROM);
        Assert.Equal(0x00, fixture.Mmu.ReadByteUntimed(0x0000));
    }

    /// <summary>
    /// Verifies an MBC1 bank selection retains the old ROM window until its CPU transaction completes.
    /// </summary>
    [Fact]
    public void MapperBankChangeWaitsForT4Completion()
    {
        using var rom = CreateMbc1Rom();
        using var fixture = new MmuFixture(romPath: rom.Path);
        var transaction = fixture.Mmu.BeginCpuWrite(0x02, 0x2000);

        Assert.Equal(0x01, fixture.Mmu.ReadByteUntimed(0x4000));

        fixture.CompleteCpuWrite(in transaction);

        Assert.Equal(0x02, fixture.Mmu.ReadByteUntimed(0x4000));
    }

    private static TestRom CreateMbc1Rom()
    {
        var rom = TestRom.Create(0x00);
        var bytes = new byte[4 * CartridgeSchema.ROM_BANK_SIZE];
        for (var bank = 0; bank < 4; bank++)
        {
            bytes[bank * CartridgeSchema.ROM_BANK_SIZE] = (byte)bank;
        }

        bytes[CartridgeSchema.MBC_MODE_LOC] = 0x01;
        bytes[CartridgeSchema.ROM_BANK_NUM_LOC] = 0x01;
        File.WriteAllBytes(rom.Path, bytes);
        return rom;
    }

    /// <summary>
    /// Builds the production MMU graph needed to exercise initiator ownership without exposing a public test API.
    /// </summary>
    private sealed class MmuFixture : IDisposable
    {
        private readonly Cartridge _cartridge;

        internal MmuFixture(
            GBCMode mode = GBCMode.NoGBC,
            HardwareModel hardwareModel = HardwareModel.DmgB,
            bool startHBlankDmaImmediately = false,
            int speedFactor = 1,
            string? romPath = null,
            BootRomConfig? bootRomConfig = null)
        {
            var bootRom = new BootROM();
            if (bootRomConfig != null)
            {
                bootRom.Load(hardwareModel, bootRomConfig);
            }

            var messageBus = new MessageBus();
            _cartridge = new Cartridge(bootRom);
            if (romPath != null)
            {
                Assert.True(_cartridge.LoadFile(romPath, Path.GetTempPath()));
            }

            var gpu = new GPU(messageBus);
            var sgb = new SgbSystem(gpu);
            var timerState = new TimerState(messageBus);
            var timer = new GBZEmuLibrary.Timer(timerState);
            var divider = new DivideRegister(timerState);
            var joypad = new Joypad(messageBus, sgb);
            var apu = new APU();
            var serial = new SerialRegisters(messageBus);
            Mmu = new MMU(
                _cartridge,
                gpu,
                timer,
                divider,
                joypad,
                apu,
                serial,
                bootRom,
                new CheatCollection(),
                messageBus);
            Mmu.Init(mode, hardwareModel);
            gpu.Reset(mode, usingBootROM: bootRomConfig != null);
            Mmu.Reset(usingBootROM: bootRomConfig != null);
            messageBus.OnCanStartHBlankDmaImmediately = () => startHBlankDmaImmediately;
            messageBus.OnGetCpuSpeedFactor = () => speedFactor;
        }

        internal MMU Mmu { get; }

        internal void CompleteCpuWrite(in CpuBusTransaction transaction)
        {
            Mmu.LatchCpuWriteAtT4(in transaction);
            Mmu.CompleteCpuWrite(in transaction);
        }

        public void Dispose()
        {
            _cartridge.Terminate();
        }

        internal void StartOamDma(byte sourceHigh)
        {
            Mmu.WriteByteUntimed(sourceHigh, MemorySchema.DMA_REGISTER);
            AdvanceMachineCycles(2);
        }

        internal void AdvanceMachineCycles(int count)
        {
            for (var cycle = 0; cycle < count; cycle++)
            {
                for (var rawClock = 0; rawClock < InstructionSchema.FOUR_CYCLES; rawClock++)
                {
                    Mmu.AdvanceDmaRawClock();
                }
            }
        }
    }
}
