using GBZEmuLibrary;

namespace GBZEmuTests;

/// <summary>
/// Verifies divider and programmable-timer behavior independently of ROM-level conformance tests.
/// </summary>
public sealed class TimerTests
{
    /// <summary>
    /// Verifies that DIV exposes bits 8 through 15 of the shared system counter.
    /// </summary>
    [Fact]
    public void DividerUsesUpperByteOfSystemCounter()
    {
        var timer = CreateTimer();

        timer.Update(51);
        Assert.Equal(0xAB, timer.ReadDivider());

        timer.Update(1);
        Assert.Equal(0xAC, timer.ReadDivider());
    }

    /// <summary>
    /// Verifies that firmware-free CGB startup uses a deterministic counter instead of the DMG boot-ROM phase.
    /// </summary>
    [Fact]
    public void CgbBootSkipUsesDeterministicFallbackCounter()
    {
        var timer = CreateTimer(GBCMode.GBCOnly);

        Assert.Equal(0, timer.ReadDivider());
    }

    /// <summary>
    /// Verifies that writing DIV resets both its visible and hidden counter bits.
    /// </summary>
    [Fact]
    public void DividerWriteResetsWholeSystemCounter()
    {
        var timer = CreateTimer();

        timer.WriteDivider();
        timer.Update(255);
        Assert.Equal(0, timer.ReadDivider());

        timer.Update(1);
        Assert.Equal(1, timer.ReadDivider());
    }

    /// <summary>
    /// Verifies the timer glitch caused when a DIV reset lowers the selected timer input.
    /// </summary>
    [Fact]
    public void DividerWriteClocksTimerOnSelectedFallingEdge()
    {
        var timer = CreateTimer();
        timer.WriteDivider();
        timer.WriteTimer(0x05, MemorySchema.TMC);
        timer.WriteTimer(0x10, MemorySchema.TIMA);
        timer.Update(8);

        timer.WriteDivider();

        Assert.Equal(0x11, timer.ReadTimer(MemorySchema.TIMA));
    }

    /// <summary>
    /// Verifies the timer glitch caused when a TAC write lowers the selected timer input.
    /// </summary>
    [Fact]
    public void TimerControlWriteClocksTimerWhenSelectedSignalFalls()
    {
        var timer = CreateTimer();
        timer.WriteDivider();
        timer.Update(8);
        timer.WriteTimer(0x05, MemorySchema.TMC);
        timer.WriteTimer(0x10, MemorySchema.TIMA);

        timer.WriteTimer(0x06, MemorySchema.TMC);

        Assert.Equal(0x11, timer.ReadTimer(MemorySchema.TIMA));
    }

    /// <summary>
    /// Verifies the documented DMG/CGB difference when TAC disables an active timer input.
    /// </summary>
    [Fact]
    public void DisablingTimerClocksTimerOnDmgOnly()
    {
        var dmgTimer = CreateTimer(GBCMode.NoGBC);
        dmgTimer.WriteDivider();
        dmgTimer.Update(8);
        dmgTimer.WriteTimer(0x05, MemorySchema.TMC);
        dmgTimer.WriteTimer(0x10, MemorySchema.TIMA);

        dmgTimer.WriteTimer(0x01, MemorySchema.TMC);

        Assert.Equal(0x11, dmgTimer.ReadTimer(MemorySchema.TIMA));

        var cgbTimer = CreateTimer(GBCMode.GBCSupport);
        cgbTimer.WriteDivider();
        cgbTimer.Update(8);
        cgbTimer.WriteTimer(0x05, MemorySchema.TMC);
        cgbTimer.WriteTimer(0x10, MemorySchema.TIMA);

        cgbTimer.WriteTimer(0x01, MemorySchema.TMC);

        Assert.Equal(0x10, cgbTimer.ReadTimer(MemorySchema.TIMA));
    }

    /// <summary>
    /// Verifies TIMA's one-M-cycle overflow delay and timer interrupt request.
    /// </summary>
    [Fact]
    public void OverflowReloadsAfterFourClocksAndRequestsInterrupt()
    {
        var messageBus = new MessageBus();
        var timer = CreateTimer(messageBus: messageBus);
        var interruptRequested = false;
        messageBus.OnRequestInterrupt = interrupt => interruptRequested = interrupt == Interrupts.Timer;

        timer.WriteDivider();
        timer.WriteTimer(0x05, MemorySchema.TMC);
        timer.WriteTimer(0xFF, MemorySchema.TIMA);
        timer.WriteTimer(0x42, MemorySchema.TMA);

        timer.Update(16);
        Assert.Equal(0, timer.ReadTimer(MemorySchema.TIMA));
        Assert.False(interruptRequested);

        timer.Update(3);
        Assert.Equal(0, timer.ReadTimer(MemorySchema.TIMA));
        Assert.False(interruptRequested);

        timer.Update(1);
        Assert.Equal(0x42, timer.ReadTimer(MemorySchema.TIMA));
        Assert.True(interruptRequested);
    }

    /// <summary>
    /// Verifies that a TIMA write during the overflow-delay cycle cancels the pending reload.
    /// </summary>
    [Fact]
    public void TimerWriteDuringOverflowDelayCancelsReload()
    {
        var timer = CreateTimer();
        timer.WriteDivider();
        timer.WriteTimer(0x05, MemorySchema.TMC);
        timer.WriteTimer(0xFF, MemorySchema.TIMA);
        timer.WriteTimer(0x42, MemorySchema.TMA);
        timer.Update(16);

        timer.WriteTimer(0x21, MemorySchema.TIMA);
        timer.Update(4);

        Assert.Equal(0x21, timer.ReadTimer(MemorySchema.TIMA));
    }

    /// <summary>
    /// Verifies that TMA overwrites a TIMA write made during the reload cycle.
    /// </summary>
    [Fact]
    public void TimerWriteDuringReloadCycleIsIgnored()
    {
        var timer = CreateTimer();
        timer.WriteDivider();
        timer.WriteTimer(0x05, MemorySchema.TMC);
        timer.WriteTimer(0xFF, MemorySchema.TIMA);
        timer.WriteTimer(0x42, MemorySchema.TMA);
        timer.Update(20);

        timer.WriteTimer(0x21, MemorySchema.TIMA);

        Assert.Equal(0x42, timer.ReadTimer(MemorySchema.TIMA));
    }

    /// <summary>
    /// Verifies that a TMA write during the reload cycle also changes TIMA.
    /// </summary>
    [Fact]
    public void ModuloWriteDuringReloadCycleAlsoUpdatesTimer()
    {
        var timer = CreateTimer();
        timer.WriteDivider();
        timer.WriteTimer(0x05, MemorySchema.TMC);
        timer.WriteTimer(0xFF, MemorySchema.TIMA);
        timer.WriteTimer(0x42, MemorySchema.TMA);
        timer.Update(20);

        timer.WriteTimer(0x21, MemorySchema.TMA);

        Assert.Equal(0x21, timer.ReadTimer(MemorySchema.TIMA));
    }

    /// <summary>
    /// Verifies that a CGB speed switch resets DIV, then runs DIV at the doubled CPU rate while the PPU remains
    /// in the base-speed clock domain.
    /// </summary>
    [Fact]
    public void CgbDoubleSpeedUsesSeparateTimerAndPpuClockDomains()
    {
        // Prepare KEY1, switch speed with STOP, run a 64-iteration delay loop, then signal completion with LD B,B.
        using var rom = TestRom.Create(
            0x3E, 0x01,       // LD A, $01
            0xE0, 0x4D,       // LDH (KEY1), A
            0x10, 0x00,       // STOP and its padding byte
            0x06, 0x40,       // LD B, 64
            0x00,             // loop: NOP
            0x05,             // DEC B
            0x20, 0xFC,       // JR NZ, loop
            0x40);            // LD B, B
        var romBytes = File.ReadAllBytes(rom.Path);
        romBytes[CartridgeSchema.GBC_MODE_LOC] = 0xC0;
        File.WriteAllBytes(rom.Path, romBytes);

        var emulator = new Emulator();
        Assert.True(emulator.Start(new Emulator.Config
        {
            ROMPath = rom.Path,
            SaveLocation = Path.GetTempPath(),
            BootMode = BootMode.GBC | BootMode.Force | BootMode.Skip
        }));
        emulator.Debug.LoadBBExecuted += emulator.Debug.RequestStop;

        emulator.Update();

        var cpuState = emulator.Debug.GetCpuState();
        var ppuState = emulator.Debug.GetPpuState();
        Assert.True(cpuState.DoubleSpeed);
        // 20 clocks before STOP + 1296 clocks after it. DIV resets on STOP and sees all 1296 double-speed clocks;
        // the PPU sees only the 648 base-speed clocks, ending 214 clocks into its current mode.
        Assert.Equal((ulong)1316, cpuState.TotalClockCycles);
        Assert.Equal(5, emulator.Debug.PeekByte(MemorySchema.DIVIDE_REGISTER));
        Assert.Equal(214, ppuState.ModeClockCycles);
        emulator.Terminate();
    }

    /// <summary>
    /// Verifies that TAC's unused bits read back as high.
    /// </summary>
    [Fact]
    public void TimerControlReturnsUnusedBitsHigh()
    {
        var timer = CreateTimer();
        timer.WriteTimer(0x05, MemorySchema.TMC);

        Assert.Equal(0xFD, timer.ReadTimer(MemorySchema.TMC));
    }

    /// <summary>
    /// Creates a boot-skipped timer state for the requested hardware mode.
    /// </summary>
    private static TimerState CreateTimer(GBCMode mode = GBCMode.NoGBC, MessageBus? messageBus = null)
    {
        var timer = new TimerState(messageBus ?? new MessageBus());
        timer.Reset(false, mode);
        return timer;
    }
}
