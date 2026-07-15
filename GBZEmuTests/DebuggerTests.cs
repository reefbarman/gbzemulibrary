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
        Assert.True(emulator.Start(new Emulator.Config
        {
            ROMPath = rom.Path,
            BootMode = BootMode.DMG | BootMode.Skip
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
    /// Starts an internal-clock serial transfer and verifies the debug byte event plus immediate completion.
    /// Test ROM protocols depend on this intentional fast path instead of emulating link timing frame by frame.
    /// </summary>
    [Fact]
    public void SerialTransferRaisesByteAndCompletesImmediately()
    {
        using var rom = TestRom.Create(0x00);
        var emulator = EmulatorFactory.Start(rom);
        byte? transferred = null;
        emulator.Debug.SerialByteTransferred += value => transferred = value;

        emulator.Debug.PokeByte((byte)'P', 0xFF01);
        emulator.Debug.PokeByte(0x81, 0xFF02);

        Assert.Equal((byte)'P', transferred);
        Assert.Equal(0, emulator.Debug.PeekByte(0xFF02) & 0x80);
        emulator.Terminate();
    }

    /// <summary>
    /// Starts an external-clock serial transfer and verifies that it remains pending without a link partner.
    /// This prevents the debug fast path from inventing clock edges that real external-clock hardware requires.
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
        Assert.NotEqual(0, emulator.Debug.PeekByte(0xFF02) & 0x80);
        emulator.Terminate();
    }

    /// <summary>
    /// Stops from an instruction callback with an interrupt ready, then resumes at the deferred interrupt
    /// checkpoint. The vector breakpoint proves the interrupt is serviced before the next opcode is fetched.
    /// </summary>
    [Fact]
    public void ResumeCompletesPendingInterruptUpdateBeforeNextInstruction()
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

        emulator.Debug.Trace.BreakProgramCounter = 0x0050;
        emulator.Debug.Resume();
        emulator.Update();

        Assert.True(emulator.Debug.IsStopped);
        Assert.Equal(0x0050, emulator.Debug.GetCpuState().PC);
        Assert.Equal((ulong)2, emulator.Debug.GetCpuState().ExecutedInstructionCount);
        Assert.Equal(0, emulator.Debug.PeekByte(0xFF0F) & (1 << (int)Interrupts.Timer));
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
        emulator.Debug.Trace.BreakProgramCounter = 0x0102;

        emulator.Update();

        Assert.True(emulator.Debug.IsStopped);
        Assert.Equal(0x0102, emulator.Debug.GetCpuState().PC);
        Assert.Equal((ulong)2, emulator.Debug.GetCpuState().ExecutedInstructionCount);
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
    /// Constructs two emulators and verifies the process-global message bus targets only the newest instance.
    /// This protects sequential-instance use while documenting that concurrent live instances are unsupported.
    /// </summary>
    [Fact]
    public void LatestEmulatorOwnsMessageBusCallbacks()
    {
        using var firstRom = TestRom.Create(0x00);
        using var secondRom = TestRom.Create(0x00);
        var first = EmulatorFactory.Start(firstRom);
        var second = EmulatorFactory.Start(secondRom);

        MessageBus.Instance.RequestInterrupt(Interrupts.Timer);

        Assert.Equal(0, first.Debug.PeekByte(0xFF0F) & (1 << (int)Interrupts.Timer));
        Assert.NotEqual(0, second.Debug.PeekByte(0xFF0F) & (1 << (int)Interrupts.Timer));
        first.Terminate();
        second.Terminate();
    }
}
