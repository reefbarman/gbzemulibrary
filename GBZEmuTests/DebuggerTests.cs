using GBZEmuLibrary;

namespace GBZEmuTests;

public sealed class DebuggerTests
{
    /// <summary>
    /// Ensures debug reads are rejected before startup and after termination, when internal device state is not
    /// a valid host-facing snapshot. This keeps debugger misuse from exposing stale or uninitialized state.
    /// </summary>
    [Fact]
    public void DebugStateRequiresRunningEmulator()
    {
        var emulator = new Emulator();

        Assert.Throws<InvalidOperationException>(() => emulator.Debug.GetCpuState());

        using var rom = TestRom.Create(0x00);
        Assert.True(emulator.Start(new Emulator.Config(HardwareModel.DmgB)
        {
            ROMPath = rom.Path,
            BootRom = BootRomConfig.Skip()
        }));

        emulator.Terminate();

        Assert.Throws<InvalidOperationException>(() => emulator.Debug.PeekByte(0));
    }

    /// <summary>
    /// Reads deterministic post-boot CPU/PPU snapshots and round-trips work RAM through the debugger's MMU path.
    /// This protects the basic inspection contract that hosts and failure diagnostics rely on.
    /// </summary>
    [Fact]
    public void DebugSnapshotsAndMemoryAccessExposeRunningState()
    {
        using var rom = TestRom.Create(0x00);
        var emulator = EmulatorFactory.Start(rom);

        var cpu = emulator.Debug.GetCpuState();
        var ppu = emulator.Debug.GetPpuState();
        emulator.Debug.PokeByte(0x5A, 0xC000);

        Assert.Equal(0x0100, cpu.PC);
        Assert.Equal(0xFFFE, cpu.SP);
        Assert.Equal(0x91, ppu.LcdControl);
        Assert.Equal(0, ppu.ScanLine);
        Assert.Equal(0x5A, emulator.Debug.PeekByte(0xC000));
        emulator.Terminate();
    }

    /// <summary>
    /// Rejects addresses outside the 16-bit Game Boy address space before they reach MMU lookup code.
    /// Explicit validation gives debugger clients stable argument errors instead of internal routing failures.
    /// </summary>
    [Fact]
    public void DebugMemoryAccessRejectsInvalidAddresses()
    {
        using var rom = TestRom.Create(0x00);
        var emulator = EmulatorFactory.Start(rom);

        Assert.Throws<ArgumentOutOfRangeException>(() => emulator.Debug.PeekByte(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => emulator.Debug.PokeByte(0, 0x10000));
        emulator.Terminate();
    }

    /// <summary>
    /// Verifies that IF exposes only five writable request bits while its unused upper bits read high.
    /// IE implements all eight bits, so its upper bits must remain writable and readable without the IF mask.
    /// </summary>
    [Fact]
    public void InterruptRegistersExposeHardwareReadMasks()
    {
        using var rom = TestRom.Create(0x00);
        var emulator = EmulatorFactory.Start(rom);

        emulator.Debug.PokeByte(1 << (int)Interrupts.Serial, 0xFF0F);
        emulator.Debug.PokeByte(0xA0, 0xFFFF);

        Assert.Equal(0xE8, emulator.Debug.PeekByte(0xFF0F));
        Assert.Equal(0xA0, emulator.Debug.PeekByte(0xFFFF));
        emulator.Terminate();
    }

    /// <summary>
    /// Verifies that firmware-free DMG startup restores deterministic P1, IF, and NR52 values produced by DMG ABC firmware.
    /// </summary>
    [Fact]
    public void DmgSkipRestoresPostBootIoState()
    {
        using var rom = TestRom.Create(0x00);
        var emulator = EmulatorFactory.Start(rom);

        Assert.Equal(0xCF, emulator.Debug.PeekByte(MemorySchema.JOYPAD_REGISTER));
        Assert.Equal(0xE1, emulator.Debug.PeekByte(MemorySchema.INTERRUPT_REQUEST_REGISTER));
        Assert.Equal(0xF1, emulator.Debug.PeekByte(APUSchema.SOUND_ENABLED));
        emulator.Terminate();
    }

    /// <summary>
    /// Verifies that STAT, HDMA5, PPU-memory reads, and PPU-memory writes use end-of-M-cycle boundary sampling.
    /// </summary>
    [Fact]
    public void PpuBoundaryReadsUseEndOfCycleSampling()
    {
        Assert.True(CPU.SamplesPpuStateAtEndOfReadCycle(0xFF41));
        Assert.True(CPU.SamplesPpuStateAtEndOfReadCycle(MemorySchema.DMA_GBC_LENGTH_MODE_START_REGISTER));
        Assert.True(CPU.SamplesPpuStateAtEndOfReadCycle(MemorySchema.VIDEO_RAM_START));
        Assert.True(CPU.SamplesPpuStateAtEndOfReadCycle(MemorySchema.VIDEO_RAM_END - 1));
        Assert.True(CPU.SamplesPpuStateAtEndOfReadCycle(MemorySchema.SPRITE_ATTRIBUTE_TABLE_START));
        Assert.True(CPU.SamplesPpuStateAtEndOfReadCycle(MemorySchema.SPRITE_ATTRIBUTE_TABLE_END - 1));

        Assert.False(CPU.SamplesPpuStateAtEndOfReadCycle(0xFF44));
        Assert.False(CPU.SamplesPpuStateAtEndOfReadCycle(MemorySchema.VIDEO_RAM_START - 1));
        Assert.False(CPU.SamplesPpuStateAtEndOfReadCycle(MemorySchema.SPRITE_ATTRIBUTE_TABLE_END));

        Assert.True(CPU.WritesPpuMemoryAtEndOfCycle(MemorySchema.VIDEO_RAM_START));
        Assert.True(CPU.WritesPpuMemoryAtEndOfCycle(MemorySchema.VIDEO_RAM_END - 1));
        Assert.True(CPU.WritesPpuMemoryAtEndOfCycle(MemorySchema.SPRITE_ATTRIBUTE_TABLE_START));
        Assert.True(CPU.WritesPpuMemoryAtEndOfCycle(MemorySchema.SPRITE_ATTRIBUTE_TABLE_END - 1));
        Assert.False(CPU.WritesPpuMemoryAtEndOfCycle(MemorySchema.VIDEO_RAM_START - 1));
        Assert.False(CPU.WritesPpuMemoryAtEndOfCycle(MemorySchema.SPRITE_ATTRIBUTE_TABLE_END));
    }

    /// <summary>
    /// Verifies only SCY and SCX use end-of-write-cycle visibility; neighboring LCD registers retain normal ordering.
    /// </summary>
    [Fact]
    public void PpuScrollWritesUseEndOfCycleVisibility()
    {
        Assert.True(CPU.WritesPpuScrollAtEndOfCycle(0xFF42));
        Assert.True(CPU.WritesPpuScrollAtEndOfCycle(0xFF43));

        Assert.False(CPU.WritesPpuScrollAtEndOfCycle(0xFF41));
        Assert.False(CPU.WritesPpuScrollAtEndOfCycle(0xFF44));
        Assert.False(CPU.WritesPpuScrollAtEndOfCycle(0xFF45));
    }

    /// <summary>
    /// Starts an internal-clock serial transfer and verifies completion occurs during emulated CPU time rather than
    /// at the SC write, including the debug byte event and serial interrupt request.
    /// </summary>
    [Fact]
    public void InternalClockSerialTransferCompletesAfterClockedDelay()
    {
        using var rom = TestRom.Create(0x18, 0xFE);
        var emulator = EmulatorFactory.Start(rom);
        byte? transferred = null;
        emulator.Debug.SerialByteTransferred += value => transferred = value;

        Assert.Equal(0x7E, emulator.Debug.PeekByte(0xFF02));
        emulator.Debug.PokeByte((byte)'P', 0xFF01);
        emulator.Debug.PokeByte(0x81, 0xFF02);

        Assert.Null(transferred);
        Assert.Equal(0xFF, emulator.Debug.PeekByte(0xFF02));

        emulator.Update();

        Assert.Equal((byte)'P', transferred);
        Assert.Equal(0x7F, emulator.Debug.PeekByte(0xFF02));
        Assert.Equal(0xFF, emulator.Debug.PeekByte(0xFF01));
        Assert.NotEqual(0, emulator.Debug.PeekByte(0xFF0F) & (1 << (int)Interrupts.Serial));
        emulator.Terminate();
    }

    /// <summary>
    /// Enables the serial interrupt, starts an internal-clock transfer, and verifies CPU dispatch reaches vector 58h.
    /// </summary>
    [Fact]
    public void InternalClockSerialTransferDispatchesInterrupt()
    {
        using var rom = TestRom.Create(
            0x3E, 1 << (int)Interrupts.Serial, // LD A, serial interrupt mask
            0xEA, 0xFF, 0xFF,                 // LD (IE), A
            0xAF,                             // XOR A
            0xE0, 0x0F,                       // LDH (IF), A
            0x3E, 0x81,                       // LD A, transfer start with internal clock
            0xE0, 0x02,                       // LDH (SC), A
            0xFB,                             // EI
            0x18, 0xFE);                      // JR forever while transfer runs
        var emulator = EmulatorFactory.Start(rom);

        Assert.True(emulator.Debug.RunUntilProgramCounter(0x0058, 1));

        emulator.Terminate();
    }

    /// <summary>
    /// Starts an external-clock serial transfer and verifies that it remains pending without a link partner.
    /// This prevents the emulator from inventing clock edges that real external-clock hardware requires.
    /// </summary>
    [Fact]
    public void ExternalClockSerialTransferRemainsPending()
    {
        using var rom = TestRom.Create(0x00);
        var emulator = EmulatorFactory.Start(rom);
        byte? transferred = null;
        emulator.Debug.SerialByteTransferred += value => transferred = value;

        emulator.Debug.PokeByte((byte)'P', 0xFF01);
        emulator.Debug.PokeByte(0x80, 0xFF02);

        Assert.Null(transferred);
        Assert.Equal(0xFE, emulator.Debug.PeekByte(0xFF02));
        emulator.Terminate();
    }

    /// <summary>
    /// Stops with an interrupt ready, then verifies dispatch reaches the vector before another opcode executes.
    /// Interrupt entry must consume five M-cycles and push the suppressed instruction's address in hardware order.
    /// </summary>
    [Fact]
    public void InterruptDispatchCompletesBeforeNextInstruction()
    {
        using var rom = TestRom.Create(0xFB, 0x40, 0x00);
        var emulator = EmulatorFactory.Start(rom);
        emulator.Debug.PokeByte(1 << (int)Interrupts.Timer, 0xFFFF);
        emulator.Debug.PokeByte(1 << (int)Interrupts.Timer, 0xFF0F);
        emulator.Debug.LoadBBExecuted += emulator.Debug.RequestStop;

        emulator.Update();

        var stoppedState = emulator.Debug.GetCpuState();
        Assert.Equal(0x0102, stoppedState.PC);
        Assert.True(stoppedState.InterruptsEnabled);
        Assert.NotEqual(0, emulator.Debug.PeekByte(0xFF0F) & (1 << (int)Interrupts.Timer));
        var clocksBeforeDispatch = stoppedState.TotalClockCycles;

        Assert.True(emulator.Debug.RunUntilProgramCounter(0x0050, 1));

        Assert.True(emulator.Debug.IsStopped);
        var dispatchedState = emulator.Debug.GetCpuState();
        Assert.Equal(0x0050, dispatchedState.PC);
        Assert.Equal(0xFFFC, dispatchedState.SP);
        Assert.Equal((ulong)2, dispatchedState.ExecutedInstructionCount);
        Assert.Equal((ulong)20, dispatchedState.TotalClockCycles - clocksBeforeDispatch);
        Assert.Equal(0x02, emulator.Debug.PeekByte(0xFFFC));
        Assert.Equal(0x01, emulator.Debug.PeekByte(0xFFFD));
        Assert.Equal(0, emulator.Debug.PeekByte(0xFF0F) & (1 << (int)Interrupts.Timer));
        emulator.Terminate();
    }

    /// <summary>
    /// Verifies CB-prefixed BIT (HL) uses only its opcode fetches and one memory-read machine cycle.
    /// </summary>
    [Fact]
    public void BitTestAtHlConsumesThreeMachineCycles()
    {
        using var rom = TestRom.Create(
            0x21, 0x00, 0xC0, // LD HL,C000
            0xCB, 0x46,       // BIT 0,(HL)
            0x40);             // LD B,B debugger breakpoint
        var emulator = EmulatorFactory.Start(rom);
        emulator.Debug.PokeByte(0x00, 0xC000);

        Assert.True(emulator.Debug.RunUntilProgramCounter(0x0106, 1));

        var state = emulator.Debug.GetCpuState();
        Assert.Equal((ulong)28, state.TotalClockCycles);
        Assert.Equal(0xB0, state.AF & 0x00F0);
        Assert.Equal(0x00, emulator.Debug.PeekByte(0xC000));
        emulator.Terminate();
    }

    /// <summary>
    /// Verifies that consecutive EI instructions preserve the first instruction's pending enable instead of
    /// restarting the delay. The pending interrupt must preempt the opcode after the second EI.
    /// </summary>
    [Fact]
    public void RepeatedEnableInterruptsDoesNotRestartDelay()
    {
        using var rom = TestRom.Create(0xFB, 0xFB, 0x40, 0x00); // EI; EI; LD B,B; NOP
        var emulator = EmulatorFactory.Start(rom);
        emulator.Debug.PokeByte(1 << (int)Interrupts.Timer, 0xFFFF);
        emulator.Debug.PokeByte(1 << (int)Interrupts.Timer, 0xFF0F);

        Assert.True(emulator.Debug.RunUntilProgramCounter(0x0050, 1));

        Assert.Equal(0x02, emulator.Debug.PeekByte(0xFFFC));
        Assert.Equal(0x01, emulator.Debug.PeekByte(0xFFFD));
        Assert.Equal((ulong)2, emulator.Debug.GetCpuState().ExecutedInstructionCount);
        emulator.Terminate();
    }

    /// <summary>
    /// Verifies that an interrupt enabled by EI immediately after a bugged HALT returns to the HALT opcode.
    /// </summary>
    [Fact]
    public void EnableInterruptsThenHaltPushesSuppressedProgramCounter()
    {
        using var rom = TestRom.Create(0xFB, 0x76, 0x00); // EI; HALT; NOP
        var emulator = EmulatorFactory.Start(rom);
        emulator.Debug.PokeByte(1 << (int)Interrupts.Timer, 0xFFFF);
        emulator.Debug.PokeByte(1 << (int)Interrupts.Timer, 0xFF0F);

        Assert.True(emulator.Debug.RunUntilProgramCounter(0x0050, 1));

        Assert.Equal(0x01, emulator.Debug.PeekByte(0xFFFC));
        Assert.Equal(0x01, emulator.Debug.PeekByte(0xFFFD));
        Assert.Equal((ulong)2, emulator.Debug.GetCpuState().ExecutedInstructionCount);
        emulator.Terminate();
    }

    /// <summary>
    /// Verifies that DI disables IME immediately and cancels an EI that is still waiting for its delayed enable.
    /// The pending interrupt must remain requested rather than preempting the following instruction.
    /// </summary>
    [Fact]
    public void DisableInterruptsCancelsPendingEnable()
    {
        using var rom = TestRom.Create(0xFB, 0xF3, 0x40, 0x00); // EI; DI; LD B,B; NOP
        var emulator = EmulatorFactory.Start(rom);
        emulator.Debug.PokeByte(1 << (int)Interrupts.Timer, 0xFFFF);
        emulator.Debug.PokeByte(1 << (int)Interrupts.Timer, 0xFF0F);
        emulator.Debug.LoadBBExecuted += emulator.Debug.RequestStop;

        emulator.Update();

        var state = emulator.Debug.GetCpuState();
        Assert.True(emulator.Debug.IsStopped);
        Assert.Equal(0x0103, state.PC);
        Assert.False(state.InterruptsEnabled);
        Assert.False(state.InterruptEnablePending);
        Assert.NotEqual(0, emulator.Debug.PeekByte(0xFF0F) & (1 << (int)Interrupts.Timer));
        emulator.Terminate();
    }

    /// <summary>
    /// Places a PC breakpoint after two NOPs and verifies execution stops before fetching the target opcode.
    /// Exact pre-fetch state is required for trustworthy traces and deterministic conformance-test inspection.
    /// </summary>
    [Fact]
    public void ProgramCounterBreakpointStopsBeforeInstruction()
    {
        using var rom = TestRom.Create(0x00, 0x00, 0x00);
        var emulator = EmulatorFactory.Start(rom);
        Assert.True(emulator.Debug.RunUntilProgramCounter(0x0102, 1));
        Assert.True(emulator.Debug.IsStopped);
        Assert.Equal(0x0102, emulator.Debug.GetCpuState().PC);
        Assert.Equal((ulong)2, emulator.Debug.GetCpuState().ExecutedInstructionCount);
        emulator.Terminate();
    }

    /// <summary>
    /// Verifies bounded breakpoint execution reports reached and timed-out addresses without running indefinitely.
    /// </summary>
    [Fact]
    public void RunUntilProgramCounterHonorsFrameBudget()
    {
        using var rom = TestRom.Create(0x00, 0x18, 0xFD); // NOP; JR -3 loops through $0100 and $0101.
        var emulator = EmulatorFactory.Start(rom);

        Assert.Equal((ushort)0x0101, emulator.Debug.RunUntilAnyProgramCounter(new ushort[] { 0x0101, 0x0102 }, 1));
        Assert.False(emulator.Debug.RunUntilProgramCounter(0x0103, 1));
        Assert.False(emulator.Debug.IsStopped);
        Assert.Throws<ArgumentException>(() => emulator.Debug.RunUntilAnyProgramCounter(Array.Empty<ushort>(), 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => emulator.Debug.RunUntilProgramCounter(0x0100, 0));
        emulator.Terminate();
    }

    /// <summary>
    /// Runs long enough to overflow the trace ring and verifies that capacity stays bounded while recent entries
    /// remain readable. This prevents debug tracing from allocating without limit during long ROM runs.
    /// </summary>
    [Fact]
    public void TraceRetainsOnlyLatestEntries()
    {
        using var rom = TestRom.Create(0x00);
        var emulator = EmulatorFactory.Start(rom);
        emulator.Debug.Trace.Enabled = true;

        emulator.Update();

        Assert.Equal(emulator.Debug.Trace.Capacity, emulator.Debug.Trace.Count);
        var entries = emulator.Debug.Trace.GetEntries();
        Assert.Equal(emulator.Debug.Trace.Capacity, entries.Length);
        Assert.Contains("PC:", entries[0]);
        emulator.Terminate();
    }

    /// <summary>
    /// Captures only the configured inclusive instruction-count range and stops before the following opcode.
    /// This keeps targeted traces small and makes range semantics deterministic for long-running ROM diagnostics.
    /// </summary>
    [Fact]
    public void TraceHonorsInstructionRange()
    {
        using var rom = TestRom.Create(0x00, 0x00, 0x00, 0x00, 0x00);
        var emulator = EmulatorFactory.Start(rom);
        emulator.Debug.Trace.Enabled = true;
        emulator.Debug.Trace.StartInstruction = 2;
        emulator.Debug.Trace.StopInstruction = 3;
        emulator.Debug.Trace.BreakProgramCounter = 0x0104;

        emulator.Update();

        var entries = emulator.Debug.Trace.GetEntries();
        Assert.Equal(2, entries.Length);
        Assert.StartsWith("2:", entries[0], StringComparison.Ordinal);
        Assert.StartsWith("3:", entries[1], StringComparison.Ordinal);
        emulator.Terminate();
    }

    /// <summary>
    /// Runs two live emulators concurrently and verifies interrupts, memory, and execution remain instance-local.
    /// </summary>
    [Fact]
    public async Task LiveEmulatorsRunConcurrentlyWithIsolatedHardwareState()
    {
        using var firstRom = TestRom.Create(0x00);
        using var secondRom = TestRom.Create(0x00);
        var first = EmulatorFactory.Start(firstRom);
        var second = EmulatorFactory.Start(secondRom);

        first.Debug.PokeByte(0x11, 0xC000);
        second.Debug.PokeByte(0x22, 0xC000);
        first.Debug.PokeByte(0x10, MemorySchema.JOYPAD_REGISTER);
        first.ButtonDown(JoypadButtons.A);

        Assert.NotEqual(0, first.Debug.PeekByte(0xFF0F) & (1 << (int)Interrupts.Joypad));
        Assert.Equal(0, second.Debug.PeekByte(0xFF0F) & (1 << (int)Interrupts.Joypad));

        var cancellationToken = TestContext.Current.CancellationToken;
        await Task.WhenAll(
            Task.Run(first.Update, cancellationToken),
            Task.Run(second.Update, cancellationToken));

        Assert.Equal(0x11, first.Debug.PeekByte(0xC000));
        Assert.Equal(0x22, second.Debug.PeekByte(0xC000));
        Assert.True(first.Debug.GetCpuState().TotalClockCycles >= Display.CLOCK_CYCLES_PER_FRAME);
        Assert.True(second.Debug.GetCpuState().TotalClockCycles >= Display.CLOCK_CYCLES_PER_FRAME);
        first.Terminate();
        second.Terminate();
    }

    /// <summary>
    /// Starts two live emulators with different firmware images and verifies each MMU retains its own boot overlay.
    /// </summary>
    [Fact]
    public void LiveEmulatorsKeepBootRomSelectionIsolated()
    {
        using var firstRom = TestRom.Create(0x00);
        using var secondRom = TestRom.Create(0x00);
        var firstBootRom = new byte[0x100];
        var secondBootRom = new byte[0x100];
        firstBootRom[0] = 0x11;
        secondBootRom[0] = 0x22;

        var first = new Emulator();
        var second = new Emulator();
        Assert.True(first.Start(new Emulator.Config(HardwareModel.DmgB)
        {
            ROMPath = firstRom.Path,
            BootRom = BootRomConfig.ExternalBytes(firstBootRom)
        }));
        Assert.True(second.Start(new Emulator.Config(HardwareModel.DmgB)
        {
            ROMPath = secondRom.Path,
            BootRom = BootRomConfig.ExternalBytes(secondBootRom)
        }));

        Assert.Equal(0x11, first.Debug.PeekByte(0));
        Assert.Equal(0x22, second.Debug.PeekByte(0));
        first.Terminate();
        second.Terminate();
    }
}
