using GBZEmuLibrary;

namespace GBZEmuTests;

/// <summary>
/// Characterizes the pre-T-cycle CPU and subsystem ordering that Phase 5 replaces in bounded steps.
/// </summary>
public sealed class TimingCharacterizationTests
{
    /// <summary>
    /// Locks the Batch 5.1 T1-through-T4 subsystem order for one ordinary opcode-fetch machine cycle.
    /// </summary>
    [Fact]
    public void MachineCycleInterleavesFourRawClockSubsystemGroups()
    {
        using var rom = TestRom.Create(0x00, 0x40);
        var emulator = EmulatorFactory.Start(rom);
        var observer = new RecordingTimingObserver();
        emulator.SetTimingObserver(observer);

        Assert.True(emulator.Debug.RunUntilProgramCounter(0x0101, 1));

        Assert.Equal(TimingEventKind.MachineCycleStarted, observer.Events[0].Kind);
        var rawClockEvents = new[]
        {
            TimingEventKind.SystemUpdateStarted,
            TimingEventKind.TimerUpdateCompleted,
            TimingEventKind.SerialUpdateCompleted,
            TimingEventKind.DmaUpdateCompleted,
            TimingEventKind.CartridgeUpdateCompleted,
            TimingEventKind.GpuUpdateCompleted,
            TimingEventKind.ApuUpdateCompleted,
            TimingEventKind.SystemUpdateCompleted
        };
        for (var rawClock = 0; rawClock < InstructionSchema.FOUR_CYCLES; rawClock++)
        {
            var groupStart = 1 + rawClock * rawClockEvents.Length;
            Assert.Equal(rawClockEvents, observer.Events
                .Skip(groupStart)
                .Take(rawClockEvents.Length)
                .Select(timingEvent => timingEvent.Kind));
            Assert.Equal(rawClock + 1, observer.Events[groupStart].Value);
            Assert.Equal(rawClock + 1, observer.Events[groupStart + rawClockEvents.Length - 1].Value);
            Assert.All(
                observer.Events.Skip(groupStart).Take(rawClockEvents.Length),
                timingEvent => Assert.Equal(1, timingEvent.Clocks));
        }

        Assert.Equal(TimingEventKind.MachineCycleCompleted, observer.Events[^2].Kind);
        Assert.Equal(InstructionSchema.FOUR_CYCLES, observer.Events[^2].Clocks);
        Assert.Equal(TimingEventKind.CpuReadObserved, observer.Events[^1].Kind);
        emulator.Terminate();
    }

    /// <summary>
    /// Locks the exact raw-clock DIV-APU edge before timer completion, serial, and DMA for that same clock.
    /// </summary>
    [Fact]
    public void ApuDividerEdgeOccursInsideCurrentRawClockGroup()
    {
        const int nopsBeforeFallingEdge = 1292;
        var program = new byte[nopsBeforeFallingEdge + 2];
        program[program.Length - 1] = 0x40;
        using var rom = TestRom.Create(program);
        var emulator = EmulatorFactory.Start(rom);

        Assert.True(emulator.Debug.RunUntilProgramCounter((ushort)(0x0100 + nopsBeforeFallingEdge), 1));
        var observer = new RecordingTimingObserver();
        emulator.SetTimingObserver(observer);

        Assert.True(emulator.Debug.RunUntilProgramCounter((ushort)(0x0101 + nopsBeforeFallingEdge), 1));

        var edge = FindEvent(observer.Events, TimingEventKind.ApuFrameSequencerClocked);
        var groupStart = FindPreviousEvent(observer.Events, TimingEventKind.SystemUpdateStarted, edge);
        var timerCompleted = FindEvent(observer.Events, TimingEventKind.TimerUpdateCompleted, edge + 1);
        var serialCompleted = FindEvent(observer.Events, TimingEventKind.SerialUpdateCompleted, timerCompleted + 1);
        var dmaCompleted = FindEvent(observer.Events, TimingEventKind.DmaUpdateCompleted, serialCompleted + 1);
        Assert.True(groupStart < edge);
        Assert.True(edge < timerCompleted);
        Assert.True(timerCompleted < serialCompleted);
        Assert.True(serialCompleted < dmaCompleted);
        Assert.Equal(1, observer.Events[edge].Clocks);
        emulator.Terminate();
    }

    /// <summary>
    /// Proves that ordinary and PPU-sensitive reads both complete at the canonical T4 transaction boundary.
    /// </summary>
    [Fact]
    public void OrdinaryAndPpuSensitiveReadsCompleteAtT4()
    {
        var ordinary = ObserveInstruction(new byte[] { 0xFA, 0x44, 0xFF, 0x40 }, 0x0103);
        var ppuSensitive = ObserveInstruction(new byte[] { 0xFA, 0x41, 0xFF, 0x40 }, 0x0103);

        AssertObservedAfterMachineCycle(ordinary, TimingEventKind.CpuReadObserved, 0xFF44);
        AssertObservedAfterMachineCycle(ppuSensitive, TimingEventKind.CpuReadObserved, 0xFF41);
    }

    /// <summary>
    /// Proves that ordinary and scroll writes both become visible at canonical T4 completion.
    /// </summary>
    [Fact]
    public void OrdinaryAndScrollWritesCompleteAtT4()
    {
        var ordinary = ObserveInstruction(
            new byte[] { 0x3E, 0x5A, 0xE0, 0x04, 0x40 },
            0x0104);
        var scroll = ObserveInstruction(
            new byte[] { 0x3E, 0x5A, 0xE0, 0x43, 0x40 },
            0x0104);

        AssertWriteObservedAtT4(ordinary, 0xFF04);
        AssertWriteObservedAtT4(scroll, 0xFF43);
    }

    /// <summary>
    /// Locks raw timer/serial/DMA clocks and divided cartridge/PPU/APU clocks at both CGB CPU speeds.
    /// </summary>
    [Theory]
    [InlineData(false, 4)]
    [InlineData(true, 2)]
    public void CgbSystemUpdatePreservesRawAndBaseClockDomains(bool doubleSpeed, int expectedBaseClocks)
    {
        var program = doubleSpeed
            ? new byte[]
            {
                0x3E, 0x01, // LD A,1
                0xE0, 0x4D, // LDH (KEY1),A
                0x10, 0x00, // STOP
                0x00,       // observed NOP
                0x40
            }
            : new byte[] { 0x00, 0x40 };
        using var rom = CreateCgbRom(program);
        var emulator = StartCgb(rom);
        var observedInstruction = doubleSpeed ? (ushort)0x0106 : (ushort)0x0100;
        var breakpoint = (ushort)(observedInstruction + 1);
        if (doubleSpeed)
        {
            Assert.True(emulator.Debug.RunUntilProgramCounter(observedInstruction, 1));
            Assert.True(emulator.Debug.GetCpuState().DoubleSpeed);
        }

        var observer = new RecordingTimingObserver();
        emulator.SetTimingObserver(observer);
        Assert.True(emulator.Debug.RunUntilProgramCounter(breakpoint, 1));

        Assert.Equal(
            InstructionSchema.FOUR_CYCLES,
            SumClocks(observer.Events, TimingEventKind.TimerUpdateCompleted));
        Assert.Equal(
            InstructionSchema.FOUR_CYCLES,
            SumClocks(observer.Events, TimingEventKind.SerialUpdateCompleted));
        Assert.Equal(
            InstructionSchema.FOUR_CYCLES,
            SumClocks(observer.Events, TimingEventKind.DmaUpdateCompleted));
        Assert.Equal(expectedBaseClocks, SumClocks(observer.Events, TimingEventKind.CartridgeUpdateCompleted));
        Assert.Equal(expectedBaseClocks, SumClocks(observer.Events, TimingEventKind.GpuUpdateCompleted));
        Assert.Equal(expectedBaseClocks, SumClocks(observer.Events, TimingEventKind.ApuUpdateCompleted));
        emulator.Terminate();
    }

    /// <summary>
    /// Locks CGB fast serial progression to raw clocks while base consumers remain speed-divided.
    /// </summary>
    [Theory]
    [InlineData(false, 16)]
    [InlineData(true, 8)]
    public void CgbSerialBitIntervalUsesRawClocksAtBothCpuSpeeds(bool doubleSpeed, int expectedBaseClocks)
    {
        var program = doubleSpeed
            ? new byte[]
            {
                0x3E, 0x01, // LD A,1
                0xE0, 0x4D, // LDH (KEY1),A
                0x10, 0x00, // STOP
                0x40,       // one-time debugger stop
                0x18, 0xFE  // JR forever
            }
            : new byte[] { 0x40, 0x18, 0xFE };
        using var rom = CreateCgbRom(program);
        var emulator = StartCgb(rom);
        emulator.Debug.LoadBBExecuted += emulator.Debug.RequestStop;
        emulator.Update();
        emulator.Debug.LoadBBExecuted -= emulator.Debug.RequestStop;
        Assert.True(emulator.Debug.IsStopped);
        Assert.Equal(doubleSpeed, emulator.Debug.GetCpuState().DoubleSpeed);
        emulator.Debug.PokeByte(0x80, MemorySchema.SERIAL_DATA_REGISTER);
        emulator.Debug.PokeByte(0x83, MemorySchema.SERIAL_CONTROL_REGISTER);

        var observer = new RecordingTimingObserver(timingEvent =>
        {
            if (timingEvent.Kind == TimingEventKind.SerialBitShifted && timingEvent.Value == 2)
            {
                emulator.Debug.RequestStop();
            }
        });
        emulator.SetTimingObserver(observer);
        emulator.Debug.Resume();
        emulator.Update();

        var firstShift = FindEvent(observer.Events, TimingEventKind.SerialBitShifted);
        var secondShift = FindEvent(observer.Events, TimingEventKind.SerialBitShifted, firstShift + 1);
        Assert.Equal(1, observer.Events[firstShift].Value);
        Assert.Equal(2, observer.Events[secondShift].Value);
        Assert.Equal(
            16,
            SumClocksBetween(observer.Events, TimingEventKind.TimerUpdateCompleted, firstShift, secondShift));
        Assert.Equal(
            expectedBaseClocks,
            SumClocksBetween(observer.Events, TimingEventKind.GpuUpdateCompleted, firstShift, secondShift));
        emulator.Terminate();
    }

    /// <summary>
    /// Verifies every previously characterized device read completes after its canonical T4 machine cycle.
    /// </summary>
    [Theory]
    [InlineData(0xFF41)] // STAT
    [InlineData(0xFF55)] // HDMA5
    [InlineData(0x8000)] // VRAM
    [InlineData(0xFE00)] // OAM
    [InlineData(0xFF42)] // SCY
    [InlineData(0xFF43)] // SCX
    [InlineData(0xFF69)] // CGB palette data
    [InlineData(0xFF04)] // DIV
    [InlineData(0xFF05)] // TIMA
    [InlineData(0xFF06)] // TMA
    [InlineData(0xFF07)] // TAC
    [InlineData(0xFF01)] // SB
    [InlineData(0xFF02)] // SC
    [InlineData(0xFF46)] // OAM DMA
    [InlineData(0xFF0F)] // IF through a guest CPU read
    [InlineData(0xFFFF)] // IE through a guest CPU read
    public void CpuReadsCompleteAtCanonicalT4Boundary(int address)
    {
        var events = ObserveCgbInstruction(
            new byte[] { 0xFA, (byte)address, (byte)(address >> 8), 0x40 },
            0x0103);

        AssertObservedAfterMachineCycle(events, TimingEventKind.CpuReadObserved, address);
    }

    /// <summary>
    /// Verifies stable timer and serial data is latched before T4 while the CPU receives it at T4 completion.
    /// </summary>
    [Theory]
    [InlineData(0xFF05, 0x33, 0x5A, 0x33, 0xFF)] // TIMA
    [InlineData(0xFF07, 0x00, 0x05, 0xF8, 0x07)] // TAC with unused bits high
    [InlineData(0xFF01, 0x3C, 0xA5, 0x3C, 0xFF)] // SB
    [InlineData(0xFF02, 0x00, 0x83, 0x7C, 0x83)] // CGB SC with unused bits high
    public void TimerAndSerialReadsRetainPreCompletionDeviceData(
        int address,
        byte initialValue,
        byte writtenValue,
        byte expectedValue,
        byte writableMask)
    {
        using var rom = CreateCgbRom(0xFA, (byte)address, (byte)(address >> 8), 0x40);
        var emulator = StartCgb(rom);
        emulator.Debug.PokeByte(initialValue, address);
        var machineCycles = 0;
        var observer = new RecordingTimingObserver(timingEvent =>
        {
            if (timingEvent.Kind == TimingEventKind.MachineCycleStarted && ++machineCycles == 4)
            {
                emulator.Debug.PokeByte(writtenValue, address);
            }
        });
        emulator.SetTimingObserver(observer);

        Assert.True(emulator.Debug.RunUntilProgramCounter(0x0103, 1));

        Assert.Equal(expectedValue, (byte)(emulator.Debug.GetCpuState().AF >> 8));
        Assert.Equal(
            (byte)(writtenValue & writableMask),
            (byte)(emulator.Debug.PeekByte(address) & writableMask));
        AssertObservedAfterMachineCycle(observer.Events, TimingEventKind.CpuReadObserved, address);
        emulator.Terminate();
    }

    /// <summary>
    /// Verifies representative mapped CPU writes become visible only at canonical T4 completion.
    /// </summary>
    [Theory]
    [InlineData(0x2000)] // mapper control
    [InlineData(0xFF41)] // STAT
    [InlineData(0xFF55)] // HDMA5
    [InlineData(0x8000)] // VRAM
    [InlineData(0xFE00)] // OAM
    [InlineData(0xFF42)] // SCY
    [InlineData(0xFF43)] // SCX
    [InlineData(0xFF69)] // CGB palette data
    [InlineData(0xFF04)] // DIV
    [InlineData(0xFF05)] // TIMA
    [InlineData(0xFF06)] // TMA
    [InlineData(0xFF07)] // TAC
    [InlineData(0xFF01)] // SB
    [InlineData(0xFF02)] // SC
    [InlineData(0xFF46)] // OAM DMA
    [InlineData(0xFF0F)] // IF
    [InlineData(0xFF50)] // boot unmap
    [InlineData(0xFF80)] // HRAM
    [InlineData(0xFFFF)] // IE
    public void CpuWritesCompleteAtCanonicalT4Boundary(int address)
    {
        var events = ObserveCgbInstruction(
            new byte[] { 0xEA, (byte)address, (byte)(address >> 8), 0x40 },
            0x0103);

        AssertWriteObservedAtT4(events, address);
    }

    /// <summary>
    /// Locks representative canonical T4 read/write order and instruction M-cycle counts.
    /// </summary>
    [Fact]
    public void RepresentativeInstructionsUseCurrentBusAndMachineCycleOrder()
    {
        AssertInstructionTrace(
            new byte[] { 0x00, 0x40 },
            0x0100,
            0x0101,
            "C", "R0100");
        AssertInstructionTrace(
            new byte[] { 0xCB, 0x00, 0x40 },
            0x0100,
            0x0102,
            "C", "R0100", "C", "R0101");
        AssertInstructionTrace(
            new byte[] { 0x3E, 0x5A, 0x40 },
            0x0100,
            0x0102,
            "C", "R0100", "C", "R0101");
        AssertInstructionTrace(
            new byte[] { 0x21, 0x00, 0xC0, 0x7E, 0x40 },
            0x0103,
            0x0104,
            "C", "R0103", "C", "RC000");
        AssertInstructionTrace(
            new byte[] { 0x21, 0x00, 0xC0, 0x70, 0x40 },
            0x0103,
            0x0104,
            "C", "R0103", "C", "WC000");
        AssertInstructionTrace(
            new byte[] { 0x21, 0x00, 0xC0, 0x34, 0x40 },
            0x0103,
            0x0104,
            "C", "R0103", "C", "RC000", "C", "WC000");
        AssertInstructionTrace(
            new byte[] { 0x20, 0x02, 0x40 },
            0x0100,
            0x0102,
            "C", "R0100", "C", "R0101");
        AssertInstructionTrace(
            new byte[] { 0x28, 0x02, 0x40, 0x40, 0x40 },
            0x0100,
            0x0104,
            "C", "R0100", "C", "R0101", "C");
        AssertInstructionTrace(
            new byte[] { 0xC5, 0x40 },
            0x0100,
            0x0101,
            "C", "R0100", "C", "C", "WFFFD", "C", "WFFFC");
        AssertInstructionTrace(
            new byte[] { 0xC1, 0x40 },
            0x0100,
            0x0101,
            "C", "R0100", "C", "RFFFE", "C", "RFFFF");
        AssertInstructionTrace(
            new byte[] { 0xCD, 0x05, 0x01, 0x40, 0x40, 0x40 },
            0x0100,
            0x0105,
            "C", "R0100", "C", "R0101", "C", "R0102", "C", "C", "WFFFD", "C", "WFFFC");
        AssertInstructionTrace(
            new byte[] { 0xC9, 0x40, 0x40, 0x40, 0x40, 0x40 },
            0x0100,
            0x0105,
            "C", "R0100", "C", "RFFFE", "C", "RFFFF", "C");
        AssertInstructionTrace(
            new byte[] { 0xD9, 0x40, 0x40, 0x40, 0x40, 0x40 },
            0x0100,
            0x0105,
            "C", "R0100", "C", "RFFFE", "C", "RFFFF", "C");
        AssertInstructionTrace(
            new byte[] { 0xC7 },
            0x0100,
            0x0000,
            "C", "R0100", "C", "C", "WFFFD", "C", "WFFFC");
        AssertInstructionTrace(
            new byte[] { 0x08, 0x00, 0xC0, 0x40 },
            0x0100,
            0x0103,
            "C", "R0100", "C", "R0101", "C", "R0102", "C", "WC000", "C", "WC001");
        AssertInstructionTrace(
            new byte[] { 0x21, 0x00, 0xC0, 0xCB, 0x06, 0x40 },
            0x0103,
            0x0105,
            "C", "R0103", "C", "R0104", "C", "RC000", "C", "WC000");
    }

    /// <summary>
    /// Locks the HALT bug's suppressed PC increment and repeated opcode fetch when IME is clear with a pending request.
    /// </summary>
    [Fact]
    public void HaltBugRepeatsThePostHaltOpcodeFetch()
    {
        using var rom = TestRom.Create(0x76, 0x00, 0x40); // HALT; NOP; LD B,B
        var emulator = EmulatorFactory.Start(rom);
        var timerMask = (byte)(1 << (int)Interrupts.Timer);
        emulator.Debug.PokeByte(timerMask, MemorySchema.INTERRUPT_ENABLE_REGISTER_START);
        emulator.Debug.PokeByte(timerMask, MemorySchema.INTERRUPT_REQUEST_REGISTER);
        var observer = new RecordingTimingObserver();
        emulator.SetTimingObserver(observer);

        Assert.True(emulator.Debug.RunUntilProgramCounter(0x0102, 1));

        Assert.Equal(
            new[] { "C", "R0100", "C", "R0101", "C", "R0101" },
            GetInstructionTrace(observer.Events));
        emulator.Terminate();
    }

    /// <summary>
    /// Verifies an IME-clear HALT wake consumes the opcode latched at T1 of the elapsed wake cycle without refetching.
    /// </summary>
    [Fact]
    public void HaltWakeExecutesBufferedT1OpcodeWithoutSecondFetch()
    {
        using var rom = TestRom.Create(
            0xC3, 0x80, 0xFF, // JP FF80
            0x40);
        var emulator = EmulatorFactory.Start(rom);
        emulator.Debug.PokeByte(0x76, 0xFF80); // HALT
        emulator.Debug.PokeByte(0x04, 0xFF81); // INC B
        emulator.Debug.PokeByte(0x40, 0xFF82); // LD B,B debugger breakpoint
        emulator.Debug.PokeByte(0x10, MemorySchema.JOYPAD_REGISTER);
        emulator.Debug.PokeByte(1 << (int)Interrupts.Joypad, MemorySchema.INTERRUPT_ENABLE_REGISTER_START);
        Assert.True(emulator.Debug.RunUntilProgramCounter(0xFF80, 1));

        var haltFetched = false;
        var wakeFetchT1Observed = false;
        var observer = new RecordingTimingObserver(timingEvent =>
        {
            if (timingEvent.Kind == TimingEventKind.CpuReadObserved && timingEvent.Address == 0xFF80)
            {
                haltFetched = true;
            }
            else if (haltFetched &&
                     !wakeFetchT1Observed &&
                     timingEvent.Kind == TimingEventKind.SystemUpdateStarted &&
                     timingEvent.Value == 1)
            {
                wakeFetchT1Observed = true;
                emulator.Debug.PokeByte(0x05, 0xFF81); // DEC B after T1 has sampled INC B.
                emulator.ButtonDown(JoypadButtons.A);
            }
        });
        emulator.SetTimingObserver(observer);
        Assert.True(emulator.Debug.RunUntilProgramCounter(0xFF82, 1));

        Assert.True(wakeFetchT1Observed);
        Assert.Equal(0x01, emulator.Debug.GetCpuState().BC >> 8);
        Assert.Equal(
            1,
            observer.Events.Count(timingEvent =>
                timingEvent.Kind == TimingEventKind.CpuReadObserved &&
                timingEvent.Address == 0xFF81));
        emulator.Terminate();
    }

    /// <summary>
    /// Verifies an IME-enabled HALT wake suppresses its elapsed fetch and reuses that cycle as interrupt M1.
    /// </summary>
    [Fact]
    public void HaltWakeWithImeSuppressesFetchedOpcodeAndReusesDispatchM1()
    {
        using var rom = TestRom.Create(0xC3, 0x80, 0xFF, 0x40); // JP FF80
        var emulator = EmulatorFactory.Start(rom);
        emulator.Debug.PokeByte(0xFB, 0xFF80); // EI
        emulator.Debug.PokeByte(0x00, 0xFF81); // NOP: completes EI delay
        emulator.Debug.PokeByte(0x76, 0xFF82); // HALT
        emulator.Debug.PokeByte(0x04, 0xFF83); // INC B: must be suppressed
        emulator.Debug.PokeByte(0x40, 0x0060); // debugger breakpoint at Joypad vector
        emulator.Debug.PokeByte(0x10, MemorySchema.JOYPAD_REGISTER);
        emulator.Debug.PokeByte(1 << (int)Interrupts.Joypad, MemorySchema.INTERRUPT_ENABLE_REGISTER_START);
        Assert.True(emulator.Debug.RunUntilProgramCounter(0xFF80, 1));

        var haltFetched = false;
        var wakeRequested = false;
        var observer = new RecordingTimingObserver(timingEvent =>
        {
            if (timingEvent.Kind == TimingEventKind.CpuReadObserved && timingEvent.Address == 0xFF82)
            {
                haltFetched = true;
            }
            else if (haltFetched &&
                     !wakeRequested &&
                     timingEvent.Kind == TimingEventKind.SystemUpdateStarted &&
                     timingEvent.Value == 1)
            {
                wakeRequested = true;
                emulator.ButtonDown(JoypadButtons.A);
            }
        });
        emulator.SetTimingObserver(observer);
        Assert.True(emulator.Debug.RunUntilProgramCounter(0x0060, 1));

        Assert.True(wakeRequested);
        Assert.Equal(0x00, emulator.Debug.GetCpuState().BC >> 8);
        Assert.Equal(0xFF83, emulator.Debug.PeekByte(0xFFFC) | emulator.Debug.PeekByte(0xFFFD) << 8);
        Assert.Equal(
            1,
            observer.Events.Count(timingEvent =>
                timingEvent.Kind == TimingEventKind.CpuReadObserved &&
                timingEvent.Address == 0xFF83));
        Assert.Equal(
            5,
            observer.Events.Count(timingEvent => timingEvent.Kind == TimingEventKind.InterruptDispatchCycle));
        emulator.Terminate();
    }

    /// <summary>
    /// Locks the fixed MMU owner table, including overlapping device ranges and fallback storage.
    /// </summary>
    [Theory]
    [InlineData(0x0000, (int)MemoryAddressOwner.Cartridge)]
    [InlineData(0x7FFF, (int)MemoryAddressOwner.Cartridge)]
    [InlineData(0x8000, (int)MemoryAddressOwner.Gpu)]
    [InlineData(0x9FFF, (int)MemoryAddressOwner.Gpu)]
    [InlineData(0xA000, (int)MemoryAddressOwner.Cartridge)]
    [InlineData(0xBFFF, (int)MemoryAddressOwner.Cartridge)]
    [InlineData(0xC000, (int)MemoryAddressOwner.WorkRam)]
    [InlineData(0xFDFF, (int)MemoryAddressOwner.WorkRam)]
    [InlineData(0xFE00, (int)MemoryAddressOwner.Gpu)]
    [InlineData(0xFE9F, (int)MemoryAddressOwner.Gpu)]
    [InlineData(0xFEA0, (int)MemoryAddressOwner.MainMemory)]
    [InlineData(0xFF00, (int)MemoryAddressOwner.Joypad)]
    [InlineData(0xFF01, (int)MemoryAddressOwner.Serial)]
    [InlineData(0xFF02, (int)MemoryAddressOwner.Serial)]
    [InlineData(0xFF03, (int)MemoryAddressOwner.UnmappedIo)]
    [InlineData(0xFF04, (int)MemoryAddressOwner.Divider)]
    [InlineData(0xFF05, (int)MemoryAddressOwner.Timer)]
    [InlineData(0xFF07, (int)MemoryAddressOwner.Timer)]
    [InlineData(0xFF08, (int)MemoryAddressOwner.UnmappedIo)]
    [InlineData(0xFF0E, (int)MemoryAddressOwner.UnmappedIo)]
    [InlineData(0xFF0F, (int)MemoryAddressOwner.MainMemory)]
    [InlineData(0xFF10, (int)MemoryAddressOwner.Apu)]
    [InlineData(0xFF3F, (int)MemoryAddressOwner.Apu)]
    [InlineData(0xFF40, (int)MemoryAddressOwner.Gpu)]
    [InlineData(0xFF46, (int)MemoryAddressOwner.Dma)]
    [InlineData(0xFF4C, (int)MemoryAddressOwner.Compatibility)]
    [InlineData(0xFF4D, (int)MemoryAddressOwner.MainMemory)]
    [InlineData(0xFF4F, (int)MemoryAddressOwner.Gpu)]
    [InlineData(0xFF50, (int)MemoryAddressOwner.MainMemory)]
    [InlineData(0xFF51, (int)MemoryAddressOwner.Dma)]
    [InlineData(0xFF55, (int)MemoryAddressOwner.Dma)]
    [InlineData(0xFF68, (int)MemoryAddressOwner.Gpu)]
    [InlineData(0xFF6C, (int)MemoryAddressOwner.Compatibility)]
    [InlineData(0xFF70, (int)MemoryAddressOwner.WorkRam)]
    [InlineData(0xFF76, (int)MemoryAddressOwner.Apu)]
    [InlineData(0xFF7F, (int)MemoryAddressOwner.MainMemory)]
    [InlineData(0xFF80, (int)MemoryAddressOwner.MainMemory)]
    [InlineData(0xFFFF, (int)MemoryAddressOwner.MainMemory)]
    public void MmuUsesCurrentFixedAddressOwnerTable(int address, int expectedOwner)
    {
        using var rom = CreateCgbRom(0x00);
        var emulator = StartCgb(rom);

        Assert.Equal((MemoryAddressOwner)expectedOwner, emulator.GetAddressOwnerForTesting(address));
        emulator.Terminate();
    }

    /// <summary>
    /// Locks the MMU's current OAM-DMA source-bus contention matrix through HRAM-executed CPU reads.
    /// </summary>
    [Theory]
    [InlineData(HardwareModel.DmgB, 0x80, 0x8000, true)]
    [InlineData(HardwareModel.DmgB, 0x80, 0xC000, false)]
    [InlineData(HardwareModel.DmgB, 0xC0, 0xC000, true)]
    [InlineData(HardwareModel.DmgB, 0xC0, 0x8000, false)]
    [InlineData(HardwareModel.CgbE, 0x00, 0x0000, true)]
    [InlineData(HardwareModel.CgbE, 0x00, 0x8000, false)]
    [InlineData(HardwareModel.CgbE, 0x80, 0x8000, true)]
    [InlineData(HardwareModel.CgbE, 0x80, 0xC000, false)]
    [InlineData(HardwareModel.CgbE, 0xC0, 0xC000, true)]
    [InlineData(HardwareModel.CgbE, 0xC0, 0x8000, false)]
    [InlineData(HardwareModel.CgbE, 0xC0, 0xFE00, true)]
    [InlineData(HardwareModel.CgbE, 0xC0, 0xFF90, false)]
    [InlineData(HardwareModel.CgbE, 0xC0, 0xFF46, false)]
    public void OamDmaContentionUsesCurrentHardwareBusMatrix(
        HardwareModel hardwareModel,
        byte sourceHigh,
        int targetAddress,
        bool expectedBlocked)
    {
        var timingEvent = ObserveOamDmaRead(hardwareModel, sourceHigh, targetAddress);

        Assert.Equal(expectedBlocked, timingEvent.BlockedByOamDma);
        if (targetAddress >= MemorySchema.SPRITE_ATTRIBUTE_TABLE_START &&
            targetAddress < MemorySchema.SPRITE_ATTRIBUTE_TABLE_END)
        {
            Assert.Equal(0xFF, timingEvent.Value);
        }
    }

    /// <summary>
    /// Locks GDMA's immediate copy inside HDMA5 T4 completion without adding emulated clocks.
    /// </summary>
    [Fact]
    public void GeneralPurposeDmaCopiesSynchronouslyAtControlWriteCompletion()
    {
        var program = new byte[]
        {
            0x3E, 0xC0, 0xE0, 0x51, // HDMA1 = C0
            0x3E, 0x00, 0xE0, 0x52, // HDMA2 = 00
            0x3E, 0x00, 0xE0, 0x53, // HDMA3 = 00
            0x3E, 0x00, 0xE0, 0x54, // HDMA4 = 00
            0x3E, 0x00, 0xE0, 0x55, // HDMA5 = one immediate block
            0x40
        };
        using var rom = CreateCgbRom(program);
        var emulator = StartCgb(rom);
        emulator.Debug.PokeByte(0x6B, 0xC000);
        var observer = new RecordingTimingObserver();
        emulator.SetTimingObserver(observer);

        Assert.True(emulator.Debug.RunUntilProgramCounter(0x0114, 1));

        var copied = FindEvent(observer.Events, TimingEventKind.GeneralPurposeDmaBlockCopied);
        var writeObserved = FindEventAtAddress(
            observer.Events,
            TimingEventKind.CpuWriteObserved,
            MemorySchema.DMA_GBC_LENGTH_MODE_START_REGISTER);
        var t3Started = FindPreviousEvent(
            observer.Events,
            TimingEventKind.SystemUpdateStarted,
            copied);
        var t4Started = FindEvent(observer.Events, TimingEventKind.SystemUpdateStarted, copied + 1);
        var t4Completed = FindPreviousEvent(
            observer.Events,
            TimingEventKind.SystemUpdateCompleted,
            writeObserved);
        Assert.Equal(3, observer.Events[t3Started].Value);
        Assert.True(t3Started < copied);
        Assert.True(copied < t4Started);
        Assert.Equal(4, observer.Events[t4Started].Value);
        Assert.True(t4Started < t4Completed);
        Assert.True(t4Completed < writeObserved);
        Assert.Equal(0x6B, emulator.Debug.PeekByte(MemorySchema.VIDEO_RAM_START));
        emulator.Terminate();
    }

    /// <summary>
    /// Records the known Phase 7 defect that HDMA requested mid-instruction stalls only at the next process boundary.
    /// </summary>
    [Fact]
    public void HBlankDmaRequestedDuringCallAllowsCurrentInstructionToFinish()
    {
        using var rom = CreateCgbRom(0xCD, 0x00, 0x01); // CALL $0100 forever
        var emulator = StartCgb(rom);
        emulator.Debug.PokeByte(0x00, 0xFF40);
        emulator.Debug.PokeByte(0x80, 0xFF40);
        emulator.Debug.PokeByte(0x5A, 0xC000);
        emulator.Debug.PokeByte(0xC0, MemorySchema.DMA_GBC_SOURCE_HIGH_REGISTER);
        emulator.Debug.PokeByte(0x00, MemorySchema.DMA_GBC_SOURCE_LOW_REGISTER);
        emulator.Debug.PokeByte(0x00, MemorySchema.DMA_GBC_DESTINATION_HIGH_REGISTER);
        emulator.Debug.PokeByte(0x00, MemorySchema.DMA_GBC_DESTINATION_LOW_REGISTER);
        var gpuClocks = 0;
        var hBlankDmaArmed = false;
        var observer = new RecordingTimingObserver(timingEvent =>
        {
            if (timingEvent.Kind == TimingEventKind.GpuUpdateCompleted)
            {
                gpuClocks += timingEvent.Clocks;
                if (!hBlankDmaArmed && gpuClocks >= 84)
                {
                    hBlankDmaArmed = true;
                    emulator.Debug.PokeByte(0x80, MemorySchema.DMA_GBC_LENGTH_MODE_START_REGISTER);
                }
            }
            else if (timingEvent.Kind == TimingEventKind.HBlankDmaWindowOpened)
            {
                emulator.Debug.RequestStop();
            }
        });
        emulator.SetTimingObserver(observer);

        emulator.Update();

        Assert.True(hBlankDmaArmed);
        var window = FindEvent(observer.Events, TimingEventKind.HBlankDmaWindowOpened);
        var nextMachineCycle = FindEvent(observer.Events, TimingEventKind.MachineCycleStarted, window + 1);
        Assert.True(window < nextMachineCycle);
        Assert.Equal(0x00, emulator.Debug.PeekByte(MemorySchema.VIDEO_RAM_START));
        Assert.True(emulator.Debug.IsStopped);
        emulator.Terminate();
    }

    /// <summary>
    /// Locks the early HDMA request, later mode-0 notification, and delayed block completion order.
    /// </summary>
    [Fact]
    public void HBlankDmaUsesEarlyWindowThenModeZeroThenDelayedBlockCopy()
    {
        const int lcdEnableModeZeroClocks = 81;
        const int modeThreeClocks = 172;
        const int hBlankClocks = 196;
        const int modeTwoStartDelayClocks = 8;
        const int modeTwoClocks = 80;
        var memory = new byte[MemorySchema.MAX_RAM_SIZE];
        var messageBus = new MessageBus
        {
            OnReadCgbDmaSourceByte = address => memory[address],
            OnWriteCgbDmaDestinationByte = (data, address) => memory[address] = data,
            OnCanStartHBlankDmaImmediately = () => false,
            OnIsCpuHalted = () => false,
            OnGetCpuSpeedFactor = () => 1
        };
        var gpu = new GPU(messageBus);
        var dma = new DMAController(messageBus);
        gpu.Reset(true);
        dma.Init(GBCMode.GBCSupport);
        gpu.Update(InstructionSchema.FOUR_CYCLES);
        gpu.WriteByte(0x80, 0xFF40);
        gpu.Update(
            lcdEnableModeZeroClocks +
            modeThreeClocks +
            hBlankClocks +
            modeTwoStartDelayClocks +
            modeTwoClocks);
        Assert.Equal(3, gpu.GetDebugState().Mode);

        memory[0xC000] = 0x5A;
        var observer = new RecordingTimingObserver();
        messageBus.SetTimingObserver(observer);
        dma.WriteByte(0xC0, MemorySchema.DMA_GBC_SOURCE_HIGH_REGISTER);
        dma.WriteByte(0x00, MemorySchema.DMA_GBC_SOURCE_LOW_REGISTER);
        dma.WriteByte(0x00, MemorySchema.DMA_GBC_DESTINATION_HIGH_REGISTER);
        dma.WriteByte(0x00, MemorySchema.DMA_GBC_DESTINATION_LOW_REGISTER);
        dma.WriteByte(0x80, MemorySchema.DMA_GBC_LENGTH_MODE_START_REGISTER);
        Assert.DoesNotContain(
            observer.Events,
            timingEvent => timingEvent.Kind == TimingEventKind.HBlankDmaBlockCopied);

        for (var batch = 0; batch < 64 && memory[MemorySchema.VIDEO_RAM_START] != 0x5A; batch++)
        {
            // Emulator.UpdateSystems currently advances DMA before the PPU for each whole four-clock batch.
            dma.Update(InstructionSchema.FOUR_CYCLES);
            gpu.Update(InstructionSchema.FOUR_CYCLES);
        }

        var window = FindEvent(observer.Events, TimingEventKind.HBlankDmaWindowOpened);
        var hBlank = FindEvent(observer.Events, TimingEventKind.HBlankStarted, window + 1);
        var copied = FindEvent(observer.Events, TimingEventKind.HBlankDmaBlockCopied, hBlank + 1);
        Assert.True(window < hBlank);
        Assert.True(hBlank < copied);
        Assert.Equal(0x5A, memory[MemorySchema.VIDEO_RAM_START]);
    }

    /// <summary>
    /// Locks the five semantic interrupt-dispatch cycles independently from their generic clock implementation.
    /// </summary>
    [Fact]
    public void InterruptDispatchReportsFiveNamedCycles()
    {
        using var rom = TestRom.Create(0xFB, 0x40, 0x00);
        var emulator = EmulatorFactory.Start(rom);
        var timerMask = (byte)(1 << (int)Interrupts.Timer);
        emulator.Debug.PokeByte(timerMask, MemorySchema.INTERRUPT_ENABLE_REGISTER_START);
        emulator.Debug.PokeByte(timerMask, MemorySchema.INTERRUPT_REQUEST_REGISTER);
        emulator.Debug.LoadBBExecuted += emulator.Debug.RequestStop;
        emulator.Update();

        var observer = new RecordingTimingObserver();
        emulator.SetTimingObserver(observer);
        Assert.True(emulator.Debug.RunUntilProgramCounter(0x0050, 1));

        Assert.Equal(
            new[]
            {
                InterruptDispatchCycle.First,
                InterruptDispatchCycle.Internal,
                InterruptDispatchCycle.HighStackWrite,
                InterruptDispatchCycle.LowStackWrite,
                InterruptDispatchCycle.Final
            },
            observer.Events
                .Where(timingEvent => timingEvent.Kind == TimingEventKind.InterruptDispatchCycle)
                .Select(timingEvent => (InterruptDispatchCycle)timingEvent.Value));
        emulator.Terminate();
    }

    /// <summary>
    /// Verifies the return PC remains visible through M4 and transitions to the vector at the named M5 boundary.
    /// </summary>
    [Fact]
    public void InterruptVectorTransitionOccursAtFinalNamedCycle()
    {
        using var rom = TestRom.Create(0xFB, 0x40, 0x00);
        var emulator = EmulatorFactory.Start(rom);
        var timerMask = (byte)(1 << (int)Interrupts.Timer);
        emulator.Debug.PokeByte(timerMask, MemorySchema.INTERRUPT_ENABLE_REGISTER_START);
        emulator.Debug.PokeByte(timerMask, MemorySchema.INTERRUPT_REQUEST_REGISTER);
        emulator.Debug.LoadBBExecuted += emulator.Debug.RequestStop;
        emulator.Update();

        ushort? lowWritePc = null;
        ushort? finalCyclePc = null;
        var observer = new RecordingTimingObserver(timingEvent =>
        {
            if (timingEvent.Kind != TimingEventKind.InterruptDispatchCycle)
            {
                return;
            }

            switch ((InterruptDispatchCycle)timingEvent.Value)
            {
                case InterruptDispatchCycle.LowStackWrite:
                    lowWritePc = emulator.Debug.GetCpuState().PC;
                    break;
                case InterruptDispatchCycle.Final:
                    finalCyclePc = emulator.Debug.GetCpuState().PC;
                    break;
            }
        });
        emulator.SetTimingObserver(observer);
        Assert.True(emulator.Debug.RunUntilProgramCounter(0x0050, 1));

        Assert.True(lowWritePc.HasValue);
        Assert.True(finalCyclePc.HasValue);
        Assert.Equal(0x0102, lowWritePc.Value);
        Assert.Equal(0x0050, finalCyclePc.Value);
        emulator.Terminate();
    }

    /// <summary>
    /// Verifies acknowledgement precedes an early-latched low stack write to LCDC.
    /// </summary>
    [Fact]
    public void InterruptAcknowledgementPrecedesLowWriteTransactionStart()
    {
        using var rom = TestRom.Create(0x31, 0x42, 0xFF, 0xFB, 0x40, 0x00); // LD SP,FF42; EI; LD B,B
        var emulator = EmulatorFactory.Start(rom);
        var timerMask = (byte)(1 << (int)Interrupts.Timer);
        emulator.Debug.PokeByte(0x80, MemorySchema.GPU_REGISTERS_START);
        emulator.Debug.PokeByte(timerMask, MemorySchema.INTERRUPT_ENABLE_REGISTER_START);
        emulator.Debug.PokeByte(timerMask, MemorySchema.INTERRUPT_REQUEST_REGISTER);
        emulator.Debug.LoadBBExecuted += emulator.Debug.RequestStop;
        emulator.Update();

        byte? lcdcAtAcknowledgement = null;
        var observer = new RecordingTimingObserver(timingEvent =>
        {
            if (timingEvent.Kind == TimingEventKind.InterruptAcknowledged)
            {
                lcdcAtAcknowledgement = emulator.Debug.PeekByte(MemorySchema.GPU_REGISTERS_START);
            }
        });
        emulator.SetTimingObserver(observer);
        Assert.True(emulator.Debug.RunUntilProgramCounter(0x0050, 1));

        Assert.True(lcdcAtAcknowledgement.HasValue);
        Assert.Equal(0x80, lcdcAtAcknowledgement.Value);
        Assert.Equal(0x05, emulator.Debug.PeekByte(MemorySchema.GPU_REGISTERS_START));
        emulator.Terminate();
    }

    /// <summary>
    /// Locks interrupt selection after the high write and acknowledgement before the low write's T-state group.
    /// </summary>
    [Fact]
    public void InterruptSelectionAndAcknowledgementPreserveWriteBoundaries()
    {
        using var rom = TestRom.Create(0xFB, 0x40, 0x00);
        var emulator = EmulatorFactory.Start(rom);
        emulator.Debug.PokeByte(1 << (int)Interrupts.Timer, MemorySchema.INTERRUPT_ENABLE_REGISTER_START);
        emulator.Debug.PokeByte(1 << (int)Interrupts.Timer, MemorySchema.INTERRUPT_REQUEST_REGISTER);
        emulator.Debug.LoadBBExecuted += emulator.Debug.RequestStop;
        emulator.Update();

        var observer = new RecordingTimingObserver();
        emulator.SetTimingObserver(observer);
        Assert.True(emulator.Debug.RunUntilProgramCounter(0x0050, 1));

        Assert.Equal(
            5,
            observer.Events.Count(timingEvent => timingEvent.Kind == TimingEventKind.MachineCycleStarted));
        var highWrite = FindEventAtAddress(observer.Events, TimingEventKind.CpuWriteObserved, 0xFFFD);
        var selected = FindEvent(observer.Events, TimingEventKind.InterruptSelected);
        var acknowledged = FindEvent(observer.Events, TimingEventKind.InterruptAcknowledged);
        var lowCycle = FindEvent(observer.Events, TimingEventKind.MachineCycleStarted, acknowledged + 1);
        var lowWrite = FindEventAtAddress(observer.Events, TimingEventKind.CpuWriteObserved, 0xFFFC);
        var finalCycle = FindEvent(observer.Events, TimingEventKind.MachineCycleStarted, lowWrite + 1);

        Assert.True(highWrite < selected);
        Assert.True(selected < acknowledged);
        Assert.True(acknowledged < lowCycle);
        Assert.True(lowCycle < lowWrite);
        Assert.True(lowWrite < finalCycle);
        Assert.Equal((byte)Interrupts.Timer, observer.Events[selected].Value);
        Assert.Equal((byte)Interrupts.Timer, observer.Events[acknowledged].Value);
        emulator.Terminate();
    }

    /// <summary>
    /// Locks the current rule that the upper stack write may replace IE before interrupt priority is selected.
    /// </summary>
    [Fact]
    public void InterruptPriorityIsResampledAfterUpperStackWriteOverwritesIe()
    {
        using var rom = TestRom.Create(0x31, 0x00, 0x00, 0xFB, 0x40, 0x00); // LD SP,0; EI; LD B,B
        var emulator = EmulatorFactory.Start(rom);
        emulator.Debug.PokeByte(
            (1 << (int)Interrupts.VBlank) | (1 << (int)Interrupts.Timer),
            MemorySchema.INTERRUPT_REQUEST_REGISTER);
        emulator.Debug.PokeByte(1 << (int)Interrupts.Timer, MemorySchema.INTERRUPT_ENABLE_REGISTER_START);
        emulator.Debug.LoadBBExecuted += emulator.Debug.RequestStop;
        emulator.Update();

        var observer = new RecordingTimingObserver();
        emulator.SetTimingObserver(observer);
        Assert.True(emulator.Debug.RunUntilProgramCounter(0x0040, 1));

        var upperWrite = FindEventAtAddress(observer.Events, TimingEventKind.CpuWriteObserved, 0xFFFF);
        var selected = FindEvent(observer.Events, TimingEventKind.InterruptSelected, upperWrite + 1);
        Assert.Equal((byte)Interrupts.VBlank, observer.Events[selected].Value);
        Assert.Equal(0x01, emulator.Debug.PeekByte(MemorySchema.INTERRUPT_ENABLE_REGISTER_START));
        emulator.Terminate();
    }

    /// <summary>
    /// Locks dispatch cancellation when the upper stack write removes every enabled pending request.
    /// </summary>
    [Fact]
    public void InterruptDispatchCanBeCancelledByUpperStackWriteOverwritingIe()
    {
        using var rom = TestRom.Create(0x31, 0x00, 0x00, 0xFB, 0x40, 0x00); // LD SP,0; EI; LD B,B
        var emulator = EmulatorFactory.Start(rom);
        emulator.Debug.PokeByte(1 << (int)Interrupts.Timer, MemorySchema.INTERRUPT_REQUEST_REGISTER);
        emulator.Debug.PokeByte(1 << (int)Interrupts.Timer, MemorySchema.INTERRUPT_ENABLE_REGISTER_START);
        emulator.Debug.LoadBBExecuted += emulator.Debug.RequestStop;
        emulator.Update();

        var observer = new RecordingTimingObserver();
        emulator.SetTimingObserver(observer);
        Assert.True(emulator.Debug.RunUntilProgramCounter(0x0000, 1));

        var selected = FindEvent(observer.Events, TimingEventKind.InterruptSelected);
        Assert.Equal(byte.MaxValue, observer.Events[selected].Value);
        Assert.DoesNotContain(
            observer.Events,
            timingEvent => timingEvent.Kind == TimingEventKind.InterruptAcknowledged);
        emulator.Terminate();
    }

    /// <summary>
    /// Locks a same-source IF request made at acknowledgement remaining set through the low stack write's clocks.
    /// </summary>
    [Fact]
    public void InterruptRequestRaisedAtAcknowledgementSurvivesTrailingClocks()
    {
        using var rom = TestRom.Create(0xFB, 0x40, 0x00);
        var emulator = EmulatorFactory.Start(rom);
        var timerMask = (byte)(1 << (int)Interrupts.Timer);
        emulator.Debug.PokeByte(timerMask, MemorySchema.INTERRUPT_ENABLE_REGISTER_START);
        emulator.Debug.PokeByte(timerMask, MemorySchema.INTERRUPT_REQUEST_REGISTER);
        emulator.Debug.LoadBBExecuted += emulator.Debug.RequestStop;
        emulator.Update();

        var observer = new RecordingTimingObserver(timingEvent =>
        {
            if (timingEvent.Kind == TimingEventKind.InterruptAcknowledged)
            {
                emulator.Debug.PokeByte(timerMask, MemorySchema.INTERRUPT_REQUEST_REGISTER);
            }
        });
        emulator.SetTimingObserver(observer);
        Assert.True(emulator.Debug.RunUntilProgramCounter(0x0050, 1));

        var acknowledged = FindEvent(observer.Events, TimingEventKind.InterruptAcknowledged);
        var lowCycle = FindEvent(observer.Events, TimingEventKind.MachineCycleStarted, acknowledged + 1);
        var lowWrite = FindEventAtAddress(observer.Events, TimingEventKind.CpuWriteObserved, 0xFFFC);
        Assert.True(acknowledged < lowCycle);
        Assert.True(lowCycle < lowWrite);
        Assert.NotEqual(0, emulator.Debug.PeekByte(MemorySchema.INTERRUPT_REQUEST_REGISTER) & timerMask);
        emulator.Terminate();
    }

    /// <summary>
    /// Proves that the test-only observer does not change serialized machine state.
    /// </summary>
    [Fact]
    public void TimingObserverDoesNotChangeExecutedOrSerializedState()
    {
        using var rom = TestRom.Create(0x00, 0x00, 0x00, 0x40);
        var observed = EmulatorFactory.Start(rom);
        observed.SetTimingObserver(new RecordingTimingObserver());
        var unobserved = EmulatorFactory.Start(rom);

        Assert.True(observed.Debug.RunUntilProgramCounter(0x0103, 1));
        Assert.True(unobserved.Debug.RunUntilProgramCounter(0x0103, 1));

        Assert.Equal(unobserved.CaptureState().ToArray(), observed.CaptureState().ToArray());
        observed.Terminate();
        unobserved.Terminate();
    }

    private static IReadOnlyList<TimingEvent> ObserveInstruction(byte[] program, ushort breakpoint)
    {
        using var rom = TestRom.Create(program);
        var emulator = EmulatorFactory.Start(rom);
        var observer = new RecordingTimingObserver();
        emulator.SetTimingObserver(observer);

        Assert.True(emulator.Debug.RunUntilProgramCounter(breakpoint, 1));

        emulator.Terminate();
        return observer.Events;
    }

    private static IReadOnlyList<TimingEvent> ObserveCgbInstruction(byte[] program, ushort breakpoint)
    {
        using var rom = CreateCgbRom(program);
        var emulator = StartCgb(rom);
        var observer = new RecordingTimingObserver();
        emulator.SetTimingObserver(observer);

        Assert.True(emulator.Debug.RunUntilProgramCounter(breakpoint, 1));

        emulator.Terminate();
        return observer.Events;
    }

    private static void AssertInstructionTrace(
        byte[] program,
        ushort instructionAddress,
        ushort breakpoint,
        params string[] expectedTrace)
    {
        using var rom = TestRom.Create(program);
        var emulator = EmulatorFactory.Start(rom);
        if (instructionAddress != 0x0100)
        {
            Assert.True(emulator.Debug.RunUntilProgramCounter(instructionAddress, 1));
        }

        if (program[0] == 0xC9 || program[0] == 0xD9)
        {
            emulator.Debug.PokeByte((byte)breakpoint, 0xFFFE);
            emulator.Debug.PokeByte((byte)(breakpoint >> 8), 0xFFFF);
        }

        var observer = new RecordingTimingObserver();
        emulator.SetTimingObserver(observer);
        Assert.True(emulator.Debug.RunUntilProgramCounter(breakpoint, 1));

        Assert.Equal(expectedTrace, GetInstructionTrace(observer.Events));
        emulator.Terminate();
    }

    private static string[] GetInstructionTrace(IReadOnlyList<TimingEvent> events)
    {
        var trace = new List<string>();
        foreach (var timingEvent in events)
        {
            switch (timingEvent.Kind)
            {
                case TimingEventKind.CpuReadObserved:
                    trace.Add($"R{timingEvent.Address:X4}");
                    break;
                case TimingEventKind.CpuWriteObserved:
                    trace.Add($"W{timingEvent.Address:X4}");
                    break;
                case TimingEventKind.MachineCycleStarted:
                    trace.Add("C");
                    break;
            }
        }

        return trace.ToArray();
    }

    private static TestRom CreateCgbRom(params byte[] program)
    {
        var rom = TestRom.Create(program);
        var bytes = File.ReadAllBytes(rom.Path);
        bytes[CartridgeSchema.GBC_MODE_LOC] = 0xC0;
        File.WriteAllBytes(rom.Path, bytes);
        return rom;
    }

    private static Emulator StartCgb(TestRom rom)
    {
        var emulator = new Emulator();
        Assert.True(emulator.Start(new Emulator.Config(HardwareModel.CgbE)
        {
            ROMPath = rom.Path,
            SaveLocation = Path.GetTempPath(),
            BootRom = BootRomConfig.Skip()
        }));
        return emulator;
    }

    private static TimingEvent ObserveOamDmaRead(
        HardwareModel hardwareModel,
        byte sourceHigh,
        int targetAddress)
    {
        using var rom = hardwareModel == HardwareModel.CgbE
            ? CreateCgbRom(0xC3, 0x80, 0xFF)
            : TestRom.Create(0xC3, 0x80, 0xFF);

        var emulator = new Emulator();
        Assert.True(emulator.Start(new Emulator.Config(hardwareModel)
        {
            ROMPath = rom.Path,
            SaveLocation = Path.GetTempPath(),
            BootRom = BootRomConfig.Skip()
        }));

        var routine = new byte[]
        {
            0x3E, sourceHigh,                         // LD A, source high
            0xE0, 0x46,                               // LDH (DMA), A
            0x00,                                     // NOP: complete startup
            0xFA, (byte)targetAddress, (byte)(targetAddress >> 8), // LD A, (target)
            0x40                                      // debugger breakpoint
        };
        for (var index = 0; index < routine.Length; index++)
        {
            emulator.Debug.PokeByte(routine[index], 0xFF80 + index);
        }

        Assert.True(emulator.Debug.RunUntilProgramCounter(0xFF80, 1));
        var observer = new RecordingTimingObserver();
        emulator.SetTimingObserver(observer);
        Assert.True(emulator.Debug.RunUntilProgramCounter(0xFF88, 1));

        var observation = observer.Events[FindEventAtAddress(
            observer.Events,
            TimingEventKind.CpuReadObserved,
            targetAddress)];
        emulator.Terminate();
        return observation;
    }


    private static void AssertWriteObservedAtT4(
        IReadOnlyList<TimingEvent> events,
        int address)
    {
        var observationIndex = FindEventAtAddress(events, TimingEventKind.CpuWriteObserved, address);
        var t4Started = FindPreviousEvent(events, TimingEventKind.SystemUpdateStarted, observationIndex);
        var t4Completed = FindPreviousEvent(events, TimingEventKind.SystemUpdateCompleted, observationIndex);
        var completionIndex = FindEvent(events, TimingEventKind.MachineCycleCompleted, observationIndex + 1);

        Assert.Equal(4, events[t4Started].Value);
        Assert.True(t4Started < t4Completed);
        Assert.True(t4Completed < observationIndex);
        Assert.True(observationIndex < completionIndex);
    }

    private static void AssertObservedAfterMachineCycle(
        IReadOnlyList<TimingEvent> events,
        TimingEventKind eventKind,
        int address)
    {
        var observationIndex = FindEventAtAddress(events, eventKind, address);
        Assert.True(observationIndex > 0);
        Assert.Equal(TimingEventKind.MachineCycleCompleted, events[observationIndex - 1].Kind);
    }

    private static int FindEventAtAddress(
        IReadOnlyList<TimingEvent> events,
        TimingEventKind eventKind,
        int address)
    {
        for (var index = 0; index < events.Count; index++)
        {
            if (events[index].Kind == eventKind && events[index].Address == address)
            {
                return index;
            }
        }

        throw new Xunit.Sdk.XunitException($"Timing event {eventKind} at {address:X4} was not observed.");
    }

    private static int SumClocks(IReadOnlyList<TimingEvent> events, TimingEventKind eventKind)
    {
        var clocks = 0;
        for (var index = 0; index < events.Count; index++)
        {
            if (events[index].Kind == eventKind)
            {
                clocks += events[index].Clocks;
            }
        }

        return clocks;
    }

    private static int SumClocksBetween(
        IReadOnlyList<TimingEvent> events,
        TimingEventKind eventKind,
        int startIndex,
        int endIndex)
    {
        var clocks = 0;
        for (var index = startIndex + 1; index < endIndex; index++)
        {
            if (events[index].Kind == eventKind)
            {
                clocks += events[index].Clocks;
            }
        }

        return clocks;
    }

    private static int FindPreviousEvent(
        IReadOnlyList<TimingEvent> events,
        TimingEventKind eventKind,
        int startIndex)
    {
        for (var index = startIndex - 1; index >= 0; index--)
        {
            if (events[index].Kind == eventKind)
            {
                return index;
            }
        }

        throw new Xunit.Sdk.XunitException($"Timing event {eventKind} was not observed before index {startIndex}.");
    }

    private static int FindEvent(
        IReadOnlyList<TimingEvent> events,
        TimingEventKind eventKind,
        int startIndex = 0)
    {
        for (var index = startIndex; index < events.Count; index++)
        {
            if (events[index].Kind == eventKind)
            {
                return index;
            }
        }

        throw new Xunit.Sdk.XunitException($"Timing event {eventKind} was not observed.");
    }

    private sealed class RecordingTimingObserver : ITimingObserver
    {
        private readonly Action<TimingEvent>? _onObserved;

        public RecordingTimingObserver(Action<TimingEvent>? onObserved = null)
        {
            _onObserved = onObserved;
        }

        public List<TimingEvent> Events { get; } = new List<TimingEvent>();

        public void Observe(in TimingEvent timingEvent)
        {
            Events.Add(timingEvent);
            _onObserved?.Invoke(timingEvent);
        }
    }
}
