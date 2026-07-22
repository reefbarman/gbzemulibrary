using GBZEmuLibrary;

namespace GBZEmuTests;

/// <summary>
/// Verifies that batched GPU updates are exactly equivalent to advancing the same PPU one dot at a time.
/// </summary>
public sealed class GpuUpdateEquivalenceTests
{
    private const int HBlankClocks = 196;
    private const int VBlankEntryHBlankClocks = 200;
    private const int Mode2StartDelayClocks = 8;
    private const int LcdEnableMode0Clocks = 81;
    private const int SearchingSpritesAttributesClocks = 80;
    private const int TransferringDataToLcdDriverClocks = 172;
    private const int ScanlineClocks = 456;
    private const byte WindowXOffset = 7;

    /// <summary>
    /// Enumerates timing-sensitive states that must not depend on the host's GPU update batch size.
    /// </summary>
    public static TheoryData<EquivalenceScenario> Scenarios => new()
    {
        EquivalenceScenario.LcdDisabled,
        EquivalenceScenario.DmgModeTransitions,
        EquivalenceScenario.CgbModeTransitions,
        EquivalenceScenario.CgbDoubleSpeedTransitions,
        EquivalenceScenario.CgbDmgCompatibilityRendering,
        EquivalenceScenario.FineScroll,
        EquivalenceScenario.WindowStartup,
        EquivalenceScenario.ObjectFetchPenalties,
        EquivalenceScenario.Line143IntoVBlank,
        EquivalenceScenario.Line153EarlyLyReset,
        EquivalenceScenario.HBlankCallbackDisablesLcd
    };

    /// <summary>
    /// Compares private mutable state, framebuffer output, and ordered callback traces after batched and dot updates.
    /// </summary>
    [Theory]
    [MemberData(nameof(Scenarios))]
    public void BatchedUpdateMatchesRepeatedSingleDotUpdates(EquivalenceScenario scenario)
    {
        var batched = CreateHarness(scenario);
        var dotted = CreateHarness(scenario);
        var cycles = PrepareScenario(batched, scenario);
        Assert.Equal(cycles, PrepareScenario(dotted, scenario));

        batched.Trace.Clear();
        dotted.Trace.Clear();

        batched.Gpu.Update(cycles);
        for (var dot = 0; dot < cycles; dot++)
        {
            dotted.Gpu.Update(1);
        }

        Assert.Equal(dotted.Trace, batched.Trace);
        Assert.Equal(StateSerialization.Write(dotted.Gpu), StateSerialization.Write(batched.Gpu));
        AssertFramebuffersEqual(dotted.Gpu.GetScreenData(), batched.Gpu.GetScreenData());
        AssertScenarioWasExercised(batched, scenario);
    }

    private static GpuHarness CreateHarness(EquivalenceScenario scenario)
    {
        return new GpuHarness(
            scenario == EquivalenceScenario.HBlankCallbackDisablesLcd,
            scenario == EquivalenceScenario.CgbDoubleSpeedTransitions ? 2 : 1);
    }

    private static int PrepareScenario(GpuHarness harness, EquivalenceScenario scenario)
    {
        switch (scenario)
        {
            case EquivalenceScenario.LcdDisabled:
                harness.Gpu.Reset(GBCMode.NoGBC, usingBootROM: false);
                return ScanlineClocks * 3;
            case EquivalenceScenario.DmgModeTransitions:
                PrepareVisibleDisplay(harness.Gpu, GBCMode.NoGBC, 0x91);
                return ScanlineClocks * 3 + 100;
            case EquivalenceScenario.CgbModeTransitions:
            case EquivalenceScenario.CgbDoubleSpeedTransitions:
                PrepareVisibleDisplay(harness.Gpu, GBCMode.GBCSupport, 0x91);
                return ScanlineClocks * 3 + 100;
            case EquivalenceScenario.CgbDmgCompatibilityRendering:
                PrepareVisibleDisplay(harness.Gpu, GBCMode.GBCCompatibility, 0x91);
                return ScanlineClocks * 145;
            case EquivalenceScenario.FineScroll:
                harness.Gpu.Reset(GBCMode.GBCSupport, usingBootROM: false);
                ConfigureCgbPalette(harness.Gpu, MemorySchema.GPU_GBC_BG_PALETTE_INDEX_REGISTER,
                    MemorySchema.GPU_GBC_BG_PALETTE_DATA_REGISTER, 0x001F);
                harness.Gpu.WriteByte(7, 0xFF43);
                PrepareVisibleDisplay(harness.Gpu, 0x91);
                return ScanlineClocks * 145;
            case EquivalenceScenario.WindowStartup:
                harness.Gpu.Reset(GBCMode.NoGBC, usingBootROM: false);
                harness.Gpu.WriteByte(0, 0xFF4A);
                harness.Gpu.WriteByte(WindowXOffset, 0xFF4B);
                PrepareVisibleDisplay(harness.Gpu, 0xB1);
                return ScanlineClocks * 145;
            case EquivalenceScenario.ObjectFetchPenalties:
                harness.Gpu.Reset(GBCMode.GBCSupport, usingBootROM: false);
                ConfigureCgbPalette(harness.Gpu, MemorySchema.GPU_GBC_BG_PALETTE_INDEX_REGISTER,
                    MemorySchema.GPU_GBC_BG_PALETTE_DATA_REGISTER, 0x001F);
                ConfigureCgbPalette(harness.Gpu, MemorySchema.GPU_GBC_SPRITE_PALETTE_INDEX_REGISTER,
                    MemorySchema.GPU_GBC_SPRITE_PALETTE_DATA_REGISTER, 0x03E0);
                WriteSprite(harness.Gpu, 0, 17, 8);
                WriteSprite(harness.Gpu, 1, 17, 24);
                PrepareVisibleDisplay(harness.Gpu, 0x93);
                return ScanlineClocks * 145;
            case EquivalenceScenario.Line143IntoVBlank:
                PrepareVisibleDisplay(harness.Gpu, GBCMode.NoGBC, 0x91);
                AdvanceToLineOneMode3(harness.Gpu);
                harness.Gpu.Update(142 * ScanlineClocks);
                return TransferringDataToLcdDriverClocks + VBlankEntryHBlankClocks + 4;
            case EquivalenceScenario.Line153EarlyLyReset:
                PrepareVisibleDisplay(harness.Gpu, GBCMode.GBCSupport, 0x91);
                harness.Gpu.Update(
                    LcdEnableMode0Clocks +
                    TransferringDataToLcdDriverClocks +
                    HBlankClocks +
                    VBlankEntryHBlankClocks - HBlankClocks +
                    152 * ScanlineClocks);
                Assert.Equal(153, harness.Gpu.GetDebugState().ScanLine);
                harness.Gpu.WriteByte(0, 0xFF45);
                harness.Gpu.WriteByte(0x40, 0xFF41);
                return 4;
            case EquivalenceScenario.HBlankCallbackDisablesLcd:
                PrepareVisibleDisplay(harness.Gpu, GBCMode.NoGBC, 0x91);
                return LcdEnableMode0Clocks + TransferringDataToLcdDriverClocks + ScanlineClocks;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
        }
    }

    private static void PrepareVisibleDisplay(GPU gpu, GBCMode mode, byte lcdControl)
    {
        gpu.Reset(mode, usingBootROM: false);
        PrepareVisibleDisplay(gpu, lcdControl);
    }

    private static void PrepareVisibleDisplay(GPU gpu, byte lcdControl)
    {
        for (var row = 0; row < 8; row++)
        {
            gpu.WriteByte(0xFF, MemorySchema.TILE_DATA_UNSIGNED_START + row * 2);
            gpu.WriteByte(0x00, MemorySchema.TILE_DATA_UNSIGNED_START + row * 2 + 1);
        }

        gpu.WriteByte(0xE4, 0xFF47);
        gpu.Update(4);
        gpu.WriteByte(0x78, 0xFF41);
        gpu.WriteByte(lcdControl, 0xFF40);
    }

    private static void ConfigureCgbPalette(GPU gpu, int indexAddress, int dataAddress, ushort colorOne)
    {
        gpu.WriteByte(0x80, indexAddress);
        gpu.WriteByte(0, dataAddress);
        gpu.WriteByte(0, dataAddress);
        gpu.WriteByte((byte)colorOne, dataAddress);
        gpu.WriteByte((byte)(colorOne >> 8), dataAddress);
    }

    private static void WriteSprite(GPU gpu, int index, byte y, byte x)
    {
        var address = MemorySchema.SPRITE_ATTRIBUTE_TABLE_START + index * 4;
        gpu.WriteByte(y, address);
        gpu.WriteByte(x, address + 1);
        gpu.WriteByte(0, address + 2);
        gpu.WriteByte(0, address + 3);
    }

    private static void AdvanceToLineOneMode3(GPU gpu)
    {
        gpu.Update(
            LcdEnableMode0Clocks +
            TransferringDataToLcdDriverClocks +
            HBlankClocks +
            Mode2StartDelayClocks +
            SearchingSpritesAttributesClocks);
    }

    private static void AssertFramebuffersEqual(Color[,] expected, Color[,] actual)
    {
        for (var y = 0; y < Display.VERTICAL_RESOLUTION; y++)
        {
            for (var x = 0; x < Display.HORIZONTAL_RESOLUTION; x++)
            {
                var expectedPixel = expected[x, y];
                var actualPixel = actual[x, y];
                Assert.Equal(
                    (
                        expectedPixel.R,
                        expectedPixel.G,
                        expectedPixel.B,
                        expectedPixel.Index,
                        expectedPixel.SgbIndex,
                        expectedPixel.BGPriority),
                    (
                        actualPixel.R,
                        actualPixel.G,
                        actualPixel.B,
                        actualPixel.Index,
                        actualPixel.SgbIndex,
                        actualPixel.BGPriority));
            }
        }
    }

    private static void AssertScenarioWasExercised(GpuHarness harness, EquivalenceScenario scenario)
    {
        var state = harness.Gpu.GetDebugState();
        switch (scenario)
        {
            case EquivalenceScenario.LcdDisabled:
                Assert.Equal(0, state.ScanLine);
                Assert.Equal(0, state.Mode);
                Assert.Equal(0, state.ModeClockCycles);
                Assert.Empty(harness.Trace);
                break;
            case EquivalenceScenario.DmgModeTransitions:
            case EquivalenceScenario.CgbModeTransitions:
            case EquivalenceScenario.CgbDoubleSpeedTransitions:
                Assert.True(harness.Trace.Count(item => item.Kind == CallbackKind.HBlank) >= 3);
                Assert.Contains(harness.Trace, item => item.Kind == CallbackKind.Interrupt && item.Interrupt == Interrupts.LCD);
                break;
            case EquivalenceScenario.CgbDmgCompatibilityRendering:
            case EquivalenceScenario.FineScroll:
            case EquivalenceScenario.WindowStartup:
            case EquivalenceScenario.ObjectFetchPenalties:
                Assert.Contains(harness.Trace, item => item.Kind == CallbackKind.VBlank);
                Assert.Contains(harness.Trace, item => item.Kind == CallbackKind.HBlankDmaWindow);
                Assert.NotEqual(
                    (byte.MaxValue, byte.MaxValue, byte.MaxValue),
                    (harness.Gpu.GetScreenData()[0, 0].R, harness.Gpu.GetScreenData()[0, 0].G, harness.Gpu.GetScreenData()[0, 0].B));
                break;
            case EquivalenceScenario.Line143IntoVBlank:
                var vBlankCallback = harness.Trace.FindIndex(item => item.Kind == CallbackKind.VBlank);
                var vBlankInterrupt = harness.Trace.FindIndex(item =>
                    item.Kind == CallbackKind.Interrupt && item.Interrupt == Interrupts.VBlank);
                Assert.True(vBlankCallback >= 0);
                Assert.True(vBlankInterrupt > vBlankCallback);
                Assert.Equal(144, state.ScanLine);
                Assert.Equal(1, state.Mode);
                break;
            case EquivalenceScenario.Line153EarlyLyReset:
                Assert.Equal(0, state.ScanLine);
                Assert.Equal(1, state.Mode);
                Assert.Equal(4, state.ModeClockCycles);
                Assert.Contains(harness.Trace, item => item.Kind == CallbackKind.Interrupt && item.Interrupt == Interrupts.LCD);
                break;
            case EquivalenceScenario.HBlankCallbackDisablesLcd:
                Assert.Equal(1, harness.Trace.Count(item => item.Kind == CallbackKind.HBlank));
                Assert.Equal(0, state.LcdControl & 0x80);
                Assert.Equal(0, state.ScanLine);
                Assert.Equal(0, state.Mode);
                Assert.Equal(0, state.ModeClockCycles);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
        }
    }

    public enum EquivalenceScenario
    {
        LcdDisabled,
        DmgModeTransitions,
        CgbModeTransitions,
        CgbDoubleSpeedTransitions,
        CgbDmgCompatibilityRendering,
        FineScroll,
        WindowStartup,
        ObjectFetchPenalties,
        Line143IntoVBlank,
        Line153EarlyLyReset,
        HBlankCallbackDisablesLcd
    }

    private enum CallbackKind
    {
        Interrupt,
        HBlankDmaWindow,
        HBlank,
        VBlank
    }

    private readonly record struct CallbackEvent(
        CallbackKind Kind,
        Interrupts? Interrupt,
        byte ScanLine,
        int Mode,
        int ModeClockCycles,
        byte LcdStatus);

    private sealed class GpuHarness
    {
        public GpuHarness(bool disableLcdOnHBlank, int cpuSpeedFactor)
        {
            var messageBus = new MessageBus
            {
                OnGetCpuSpeedFactor = () => cpuSpeedFactor
            };
            Gpu = new GPU(messageBus);
            messageBus.OnRequestInterrupt = interrupt => Record(CallbackKind.Interrupt, interrupt);
            messageBus.OnHBlankDmaWindow = () => Record(CallbackKind.HBlankDmaWindow);
            messageBus.OnVBlank = () => Record(CallbackKind.VBlank);
            messageBus.OnHBlank = () =>
            {
                Record(CallbackKind.HBlank);
                if (disableLcdOnHBlank)
                {
                    Gpu.WriteByte(0, 0xFF40);
                }
            };
        }

        public GPU Gpu { get; }
        public List<CallbackEvent> Trace { get; } = new();

        private void Record(CallbackKind kind, Interrupts? interrupt = null)
        {
            var state = Gpu.GetDebugState();
            Trace.Add(new CallbackEvent(
                kind,
                interrupt,
                state.ScanLine,
                state.Mode,
                state.ModeClockCycles,
                state.LcdStatus));
        }
    }
}
