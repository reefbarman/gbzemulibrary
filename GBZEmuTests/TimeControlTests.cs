using System.Security.Cryptography;
using GBZEmuLibrary;

namespace GBZEmuTests;

public sealed class TimeControlTests
{
    [Fact]
    public void SaveStateRoundTripRestoresAndResumesDeterministically()
    {
        using var rom = CreateCounterRom();
        var emulator = EmulatorFactory.Start(rom);

        emulator.Update();
        emulator.GetSoundSamples(out _);
        var savedCpu = emulator.Debug.GetCpuState();
        var savedPpu = emulator.Debug.GetPpuState();
        var savedCounter = emulator.Debug.PeekByte(0xC000);
        var savedScreenHash = HashScreen(emulator.GetScreenData());
        Assert.True(emulator.TryGetDmgShadeData(out var shades));
        var savedShadeHash = HashShades(shades);
        var serialized = emulator.CaptureState().ToArray();

        emulator.AdvanceFrames(3);
        shades[0, 0] = (byte)((shades[0, 0] + 1) & 0x03);
        Assert.NotEqual(savedCpu.ExecutedInstructionCount, emulator.Debug.GetCpuState().ExecutedInstructionCount);

        var state = EmulatorState.FromArray(serialized);
        emulator.RestoreState(state);

        Assert.Equal(EmulatorState.CurrentFormatVersion, state.FormatVersion);
        Assert.Equal(serialized.Length, state.SerializedLength);
        AssertCpuEqual(savedCpu, emulator.Debug.GetCpuState());
        Assert.Equal(savedPpu.ScanLine, emulator.Debug.GetPpuState().ScanLine);
        Assert.Equal(savedPpu.ModeClockCycles, emulator.Debug.GetPpuState().ModeClockCycles);
        Assert.Equal(savedCounter, emulator.Debug.PeekByte(0xC000));
        Assert.Equal(savedScreenHash, HashScreen(emulator.GetScreenData()));
        Assert.True(emulator.TryGetDmgShadeData(out var restoredShades));
        Assert.Same(shades, restoredShades);
        Assert.Equal(savedShadeHash, HashShades(restoredShades));

        emulator.Update();
        var firstReplayCpu = emulator.Debug.GetCpuState();
        var firstReplayCounter = emulator.Debug.PeekByte(0xC000);
        var firstReplayScreenHash = HashScreen(emulator.GetScreenData());
        var firstReplayAudio = emulator.GetSoundSamples(out var firstReplayAudioFrames);
        var firstReplayAudioHash = HashBytes(firstReplayAudio, firstReplayAudioFrames * 2);

        emulator.RestoreState(state);
        emulator.Update();

        AssertCpuEqual(firstReplayCpu, emulator.Debug.GetCpuState());
        Assert.Equal(firstReplayCounter, emulator.Debug.PeekByte(0xC000));
        Assert.Equal(firstReplayScreenHash, HashScreen(emulator.GetScreenData()));
        var secondReplayAudio = emulator.GetSoundSamples(out var secondReplayAudioFrames);
        Assert.Equal(firstReplayAudioFrames, secondReplayAudioFrames);
        Assert.Equal(firstReplayAudioHash, HashBytes(secondReplayAudio, secondReplayAudioFrames * 2));
        emulator.Terminate();
    }

    /// <summary>
    /// Verifies a buffered IME-clear HALT-wake opcode survives state capture without being re-read on replay.
    /// </summary>
    [Fact]
    public void SaveStateRoundTripsBufferedHaltWakeOpcode()
    {
        using var rom = TestRom.Create(0xC3, 0x80, 0xFF, 0x40); // JP FF80
        var emulator = EmulatorFactory.Start(rom);
        emulator.Debug.PokeByte(0x76, 0xFF80); // HALT
        emulator.Debug.PokeByte(0x04, 0xFF81); // INC B
        emulator.Debug.PokeByte(0x40, 0xFF82); // LD B,B debugger breakpoint
        emulator.Debug.PokeByte(0x10, MemorySchema.JOYPAD_REGISTER);
        emulator.Debug.PokeByte(1 << (int)Interrupts.Joypad, MemorySchema.INTERRUPT_ENABLE_REGISTER_START);
        Assert.True(emulator.Debug.RunUntilProgramCounter(0xFF80, 1));

        var haltFetched = false;
        var wakeRequested = false;
        emulator.SetTimingObserver(new StopOnBufferedWakeObserver(emulator, () => haltFetched, () =>
        {
            wakeRequested = true;
            emulator.ButtonDown(JoypadButtons.A);
        }, () => haltFetched = true));
        Assert.True(emulator.Debug.RunUntilProgramCounter(0xFF82, 1));

        Assert.True(wakeRequested);
        Assert.True(emulator.Debug.IsStopped);
        Assert.Equal(0xFF81, emulator.Debug.GetCpuState().PC);
        var state = emulator.CaptureState();
        emulator.Debug.PokeByte(0x05, 0xFF81); // DEC B if replay incorrectly re-reads memory.

        emulator.Debug.Resume();
        Assert.True(emulator.Debug.RunUntilProgramCounter(0xFF82, 1));
        Assert.Equal(0x01, emulator.Debug.GetCpuState().BC >> 8);

        emulator.RestoreState(state);
        emulator.Debug.PokeByte(0x05, 0xFF81);
        emulator.Debug.Resume();
        Assert.True(emulator.Debug.RunUntilProgramCounter(0xFF82, 1));
        Assert.Equal(0x01, emulator.Debug.GetCpuState().BC >> 8);
        emulator.Terminate();
    }

    [Fact]
    public void SaveStateRestoresIntoAnotherInstanceOfSameRom()
    {
        using var rom = CreateCounterRom();
        var first = EmulatorFactory.Start(rom);
        first.AdvanceFrames(2);
        var state = first.CaptureState();
        var expectedCpu = first.Debug.GetCpuState();
        var expectedCounter = first.Debug.PeekByte(0xC000);
        first.Terminate();

        var second = EmulatorFactory.Start(rom);
        second.RestoreState(state);

        AssertCpuEqual(expectedCpu, second.Debug.GetCpuState());
        Assert.Equal(expectedCounter, second.Debug.PeekByte(0xC000));
        second.Terminate();
    }

    [Fact]
    public void SaveStateRestoresCgbBankedVideoAndWorkRam()
    {
        using var rom = CreateCounterRom();
        var bytes = File.ReadAllBytes(rom.Path);
        bytes[0x143] = 0x80;
        File.WriteAllBytes(rom.Path, bytes);
        var emulator = new Emulator();
        Assert.True(emulator.Start(new Emulator.Config(HardwareModel.CgbE)
        {
            ROMPath = rom.Path,
            SaveLocation = Path.GetTempPath(),
            BootRom = BootRomConfig.Skip()
        }));

        emulator.Debug.PokeByte(0x01, MemorySchema.GPU_VRAM_BANK_REGISTER);
        emulator.Debug.PokeByte(0x5A, MemorySchema.VIDEO_RAM_START);
        emulator.Debug.PokeByte(0x02, MemorySchema.SWITCHABLE_WORK_RAM_REGISTER);
        emulator.Debug.PokeByte(0xA5, MemorySchema.WORK_RAM_SWITCHABLE_START);
        var state = emulator.CaptureState();

        emulator.Debug.PokeByte(0x00, MemorySchema.GPU_VRAM_BANK_REGISTER);
        emulator.Debug.PokeByte(0x03, MemorySchema.SWITCHABLE_WORK_RAM_REGISTER);
        emulator.RestoreState(state);

        Assert.Equal(0x01, emulator.Debug.PeekByte(MemorySchema.GPU_VRAM_BANK_REGISTER));
        Assert.Equal(0x5A, emulator.Debug.PeekByte(MemorySchema.VIDEO_RAM_START));
        Assert.Equal(0xFA, emulator.Debug.PeekByte(MemorySchema.SWITCHABLE_WORK_RAM_REGISTER));
        Assert.Equal(0xA5, emulator.Debug.PeekByte(MemorySchema.WORK_RAM_SWITCHABLE_START));
        emulator.Terminate();
    }

    [Fact]
    public void RestoreRejectsDifferentRomAndCorruptPayload()
    {
        using var firstRom = TestRom.Create(0x00, 0x18, 0xFD);
        using var secondRom = TestRom.Create(0x04, 0x18, 0xFD);
        var first = EmulatorFactory.Start(firstRom);
        var second = EmulatorFactory.Start(secondRom);
        var state = first.CaptureState();

        Assert.Throws<InvalidOperationException>(() => second.RestoreState(state));

        var corrupt = state.ToArray();
        corrupt[corrupt.Length / 2] ^= 0xFF;
        Assert.Throws<InvalidDataException>(() => EmulatorState.FromArray(corrupt));

        first.Terminate();
        second.Terminate();
    }

    [Fact]
    public void SaveStateIdentityIncludesConcreteHardwareModel()
    {
        using var rom = CreateCounterRom();
        var dmg = Start(rom, HardwareModel.DmgB, BootRomConfig.Skip());
        var mgb = Start(rom, HardwareModel.Mgb, BootRomConfig.Skip());
        var sgb2 = Start(rom, HardwareModel.Sgb2, BootRomConfig.Skip());
        var state = dmg.CaptureState();

        Assert.Throws<InvalidOperationException>(() => mgb.RestoreState(state));
        Assert.Throws<InvalidOperationException>(() => sgb2.RestoreState(state));
        dmg.Terminate();
        mgb.Terminate();
        sgb2.Terminate();
    }

    [Fact]
    public void AgbSaveStateRestoresCompatibilityRegistersAndReplay()
    {
        using var rom = CreateCounterRom();
        var emulator = Start(rom, HardwareModel.AgbA, BootRomConfig.Skip());
        emulator.Update();
        var state = emulator.CaptureState();
        var savedCpu = emulator.Debug.GetCpuState();
        var savedCounter = emulator.Debug.PeekByte(0xC000);

        emulator.Debug.PokeByte(0x00, MemorySchema.OBJECT_PRIORITY_REGISTER);
        Assert.Equal(0xFE, emulator.Debug.PeekByte(MemorySchema.OBJECT_PRIORITY_REGISTER));
        emulator.Update();

        emulator.RestoreState(state);

        Assert.Equal(0x04, emulator.Debug.PeekByte(MemorySchema.CPU_MODE_SELECT_REGISTER));
        Assert.Equal(0xFF, emulator.Debug.PeekByte(MemorySchema.OBJECT_PRIORITY_REGISTER));
        AssertCpuEqual(savedCpu, emulator.Debug.GetCpuState());
        Assert.Equal(savedCounter, emulator.Debug.PeekByte(0xC000));

        emulator.Update();
        var replayCpu = emulator.Debug.GetCpuState();
        var replayCounter = emulator.Debug.PeekByte(0xC000);
        emulator.RestoreState(state);
        emulator.Update();

        AssertCpuEqual(replayCpu, emulator.Debug.GetCpuState());
        Assert.Equal(replayCounter, emulator.Debug.PeekByte(0xC000));
        emulator.Terminate();
    }

    [Fact]
    public void SaveStateIdentitySeparatesCgbEAndAgbA()
    {
        using var rom = CreateCounterRom();
        var cgb = Start(rom, HardwareModel.CgbE, BootRomConfig.Skip());
        var agb = Start(rom, HardwareModel.AgbA, BootRomConfig.Skip());
        var cgbState = cgb.CaptureState();
        var agbState = agb.CaptureState();

        Assert.Throws<InvalidOperationException>(() => agb.RestoreState(cgbState));
        Assert.Throws<InvalidOperationException>(() => cgb.RestoreState(agbState));
        cgb.Terminate();
        agb.Terminate();
    }

    [Fact]
    public void SaveStateIdentitySeparatesSkipAndFirmwareBoot()
    {
        using var rom = CreateCounterRom();
        var skipped = Start(rom, HardwareModel.DmgB, BootRomConfig.Skip());
        var booted = Start(rom, HardwareModel.DmgB, BootRomConfig.BuiltIn());
        var state = skipped.CaptureState();

        Assert.Throws<InvalidOperationException>(() => booted.RestoreState(state));
        skipped.Terminate();
        booted.Terminate();
    }

    [Theory]
    [InlineData(HardwareModel.DmgB, "dmg_boot.bin")]
    [InlineData(HardwareModel.Mgb, "mgb_boot.bin")]
    [InlineData(HardwareModel.AgbA, "agb_boot.bin")]
    public void SaveStateIdentityMatchesByteIdenticalBuiltInAndExternalFirmware(
        HardwareModel model,
        string resourceName)
    {
        using var rom = CreateCounterRom();
        var builtInBytes = ReadBuiltInFirmware(resourceName);
        var builtIn = Start(rom, model, BootRomConfig.BuiltIn());
        var external = Start(rom, model, BootRomConfig.ExternalBytes(builtInBytes));
        var state = builtIn.CaptureState();

        external.RestoreState(state);
        builtIn.Terminate();
        external.Terminate();
    }

    [Fact]
    public void MgbSaveStateIdentitySeparatesSkipAndFirmwareBoot()
    {
        using var rom = CreateCounterRom();
        var skipped = Start(rom, HardwareModel.Mgb, BootRomConfig.Skip());
        var booted = Start(rom, HardwareModel.Mgb, BootRomConfig.BuiltIn());
        var state = skipped.CaptureState();

        Assert.Throws<InvalidOperationException>(() => booted.RestoreState(state));
        skipped.Terminate();
        booted.Terminate();
    }

    [Fact]
    public void SaveStateIdentityRejectsDifferentActiveFirmware()
    {
        using var rom = CreateCounterRom();
        var firstImage = new byte[0x100];
        var secondImage = new byte[0x100];
        firstImage[0] = 0x01;
        secondImage[0] = 0x02;
        var first = Start(rom, HardwareModel.DmgB, BootRomConfig.ExternalBytes(firstImage));
        var second = Start(rom, HardwareModel.DmgB, BootRomConfig.ExternalBytes(secondImage));
        var state = first.CaptureState();

        Assert.Throws<InvalidOperationException>(() => second.RestoreState(state));
        first.Terminate();
        second.Terminate();
    }

    /// <summary>
    /// Verifies public v4 states resume double-speed CPU clocks and base-speed PPU clocks deterministically.
    /// </summary>
    [Fact]
    public void SaveStateRestoresDoubleSpeedClockDomainsDeterministically()
    {
        using var rom = TestRom.Create(
            0x3E, 0x01,       // LD A,$01
            0xE0, 0x4D,       // LDH (KEY1),A
            0x10, 0x00,       // STOP; padding
            0x00,             // loop: NOP
            0x18, 0xFD);      // JR loop
        var bytes = File.ReadAllBytes(rom.Path);
        bytes[CartridgeSchema.GBC_MODE_LOC] = 0xC0;
        File.WriteAllBytes(rom.Path, bytes);
        var emulator = Start(rom, HardwareModel.CgbE, BootRomConfig.Skip());
        Assert.True(emulator.Debug.RunUntilProgramCounter(0x0106, 1));
        Assert.True(emulator.Debug.GetCpuState().DoubleSpeed);
        var checkpoint = emulator.CaptureState();

        var expected = ReplayMachineCycles(emulator, checkpoint, 12);
        var actual = ReplayMachineCycles(emulator, checkpoint, 12);

        Assert.Equal(expected, actual);
        Assert.True(emulator.Debug.GetCpuState().DoubleSpeed);
        emulator.Terminate();
    }

    /// <summary>
    /// Verifies pending TIMA reload and the normal-speed DIV-APU phase survive the public v4 state envelope.
    /// </summary>
    [Fact]
    public void SaveStateRestoresTimerReloadAndDivApuPhaseDeterministically()
    {
        using var rom = TestRom.Create();
        var emulator = EmulatorFactory.Start(rom);
        emulator.Debug.PokeByte(0x00, MemorySchema.DIVIDE_REGISTER);
        emulator.Debug.PokeByte(0x42, MemorySchema.TMA);
        emulator.Debug.PokeByte(0xFF, MemorySchema.TIMA);
        emulator.Debug.PokeByte(0x05, MemorySchema.TMC);
        RunMachineCycles(emulator, 4);
        Assert.Equal(0x00, emulator.Debug.PeekByte(MemorySchema.TIMA));
        var pendingReload = emulator.CaptureState();

        var expectedReload = ReplayMachineCycles(emulator, pendingReload, 1);
        var actualReload = ReplayMachineCycles(emulator, pendingReload, 1);

        Assert.Equal(expectedReload, actualReload);
        Assert.Equal(0x42, emulator.Debug.PeekByte(MemorySchema.TIMA));
        Assert.NotEqual(0, emulator.Debug.PeekByte(MemorySchema.INTERRUPT_REQUEST_REGISTER) & (1 << (int)Interrupts.Timer));

        emulator.Debug.PokeByte(0x00, MemorySchema.TMC);
        emulator.Debug.PokeByte(0x00, MemorySchema.DIVIDE_REGISTER);
        RunMachineCycles(emulator, 2047);
        var pendingApuEdge = emulator.CaptureState();

        var expectedApu = ReplayMachineCycles(emulator, pendingApuEdge, 1);
        var actualApu = ReplayMachineCycles(emulator, pendingApuEdge, 1);

        Assert.Equal(expectedApu, actualApu);
        emulator.Terminate();
    }

    /// <summary>
    /// Verifies OAM-DMA startup/transfer and an active HDMA countdown resume identically through public v4 states.
    /// </summary>
    [Fact]
    public void SaveStateRestoresDmaPhasesDeterministically()
    {
        using var dmgRom = TestRom.Create(0xC3, 0x80, 0xFF); // JP FF80
        var dmg = EmulatorFactory.Start(dmgRom);
        for (var index = 0; index < 0x20; index++)
        {
            dmg.Debug.PokeByte(0x00, 0xFF80 + index);
            dmg.Debug.PokeByte((byte)(0x60 + index), 0xC000 + index);
        }

        Assert.True(dmg.Debug.RunUntilProgramCounter(0xFF80, 1));
        dmg.Debug.PokeByte(0xC0, MemorySchema.DMA_REGISTER);
        var startup = dmg.CaptureState();
        Assert.Equal(
            ReplayMachineCycles(dmg, startup, 1),
            ReplayMachineCycles(dmg, startup, 1));
        Assert.Equal(0x00, dmg.Debug.PeekByte(MemorySchema.SPRITE_ATTRIBUTE_TABLE_START));

        RunMachineCycles(dmg, 1);
        var transfer = dmg.CaptureState();
        Assert.Equal(
            ReplayMachineCycles(dmg, transfer, 3),
            ReplayMachineCycles(dmg, transfer, 3));
        Assert.Equal(0x62, dmg.Debug.PeekByte(MemorySchema.SPRITE_ATTRIBUTE_TABLE_START + 2));
        dmg.Terminate();

        using var cgbRom = TestRom.Create(0x00, 0x18, 0xFD);
        var cgbBytes = File.ReadAllBytes(cgbRom.Path);
        cgbBytes[CartridgeSchema.GBC_MODE_LOC] = 0xC0;
        File.WriteAllBytes(cgbRom.Path, cgbBytes);
        var cgb = Start(cgbRom, HardwareModel.CgbE, BootRomConfig.Skip());
        cgb.Debug.PokeByte(0x00, MemorySchema.GPU_REGISTERS_START); // LCD off permits immediate HDMA.
        cgb.Debug.PokeByte(0x7C, 0xC000);
        cgb.Debug.PokeByte(0xC0, MemorySchema.DMA_GBC_SOURCE_HIGH_REGISTER);
        cgb.Debug.PokeByte(0x00, MemorySchema.DMA_GBC_SOURCE_LOW_REGISTER);
        cgb.Debug.PokeByte(0x00, MemorySchema.DMA_GBC_DESTINATION_HIGH_REGISTER);
        cgb.Debug.PokeByte(0x00, MemorySchema.DMA_GBC_DESTINATION_LOW_REGISTER);
        cgb.Debug.PokeByte(0x80, MemorySchema.DMA_GBC_LENGTH_MODE_START_REGISTER);
        RunMachineCycles(cgb, 4);
        var countdown = cgb.CaptureState();

        Assert.Equal(
            ReplayMachineCycles(cgb, countdown, 6),
            ReplayMachineCycles(cgb, countdown, 6));
        Assert.Equal(0x7C, cgb.Debug.PeekByte(MemorySchema.VIDEO_RAM_START));
        Assert.Equal(0xFF, cgb.Debug.PeekByte(MemorySchema.DMA_GBC_LENGTH_MODE_START_REGISTER));
        cgb.Terminate();
    }

    /// <summary>
    /// Verifies representative synthetic Mode 3 fetch/render transaction state resumes exactly from a v4 state.
    /// </summary>
    [Fact]
    public void SaveStateRestoresMode3TransactionBoundaryDeterministically()
    {
        using var rom = TestRom.Create(0x00, 0x18, 0xFD);
        var emulator = EmulatorFactory.Start(rom);
        emulator.Debug.PokeByte(0x00, MemorySchema.GPU_REGISTERS_START);
        RunMachineCycles(emulator, 1);
        emulator.Debug.PokeByte(0x91, MemorySchema.GPU_REGISTERS_START);
        RunMachineCycles(emulator, 21);
        var ppu = emulator.Debug.GetPpuState();

        Assert.Equal(3, ppu.Mode);
        var checkpoint = emulator.CaptureState();

        Assert.Equal(
            ReplayMachineCycles(emulator, checkpoint, 20),
            ReplayMachineCycles(emulator, checkpoint, 20));
        emulator.Terminate();
    }

    [Fact]
    public void SaveStateFormatUsesClockPhaseVersionFour()
    {
        Assert.Equal(4, EmulatorState.CurrentFormatVersion);
    }

    [Fact]
    public void OldSaveStateVersionIsRejectedWithoutMigration()
    {
        using var rom = CreateCounterRom();
        var emulator = EmulatorFactory.Start(rom);
        var bytes = emulator.CaptureState().ToArray();
        BitConverter.GetBytes(3).CopyTo(bytes, 8);
        using (var sha256 = SHA256.Create())
        {
            var checksum = sha256.ComputeHash(bytes, 0, bytes.Length - 32);
            checksum.CopyTo(bytes, bytes.Length - checksum.Length);
        }

        Assert.Throws<NotSupportedException>(() => EmulatorState.FromArray(bytes));
        emulator.Terminate();
    }

    [Fact]
    public void RewindBufferRestoresEarlierBoundedCheckpoints()
    {
        using var rom = CreateCounterRom();
        var emulator = EmulatorFactory.Start(rom);
        var rewind = new RewindBuffer(3);

        rewind.Capture(emulator);
        emulator.Update();
        rewind.Capture(emulator);
        var oneFrameCounter = emulator.Debug.PeekByte(0xC000);
        emulator.Update();
        rewind.Capture(emulator);
        emulator.Update();
        rewind.Capture(emulator);

        Assert.Equal(3, rewind.Count);
        Assert.True(rewind.TryRewind(emulator));
        Assert.True(rewind.TryRewind(emulator));
        Assert.Equal(oneFrameCounter, emulator.Debug.PeekByte(0xC000));
        Assert.False(rewind.TryRewind(emulator));

        rewind.Clear();
        Assert.Equal(0, rewind.Count);
        emulator.Terminate();
    }

    [Fact]
    public void RestoreRewindsFileBackedCartridgeRam()
    {
        using var rom = TestRom.Create(0x18, 0xFE);
        var bytes = File.ReadAllBytes(rom.Path);
        bytes[0x147] = 0x03; // MBC1 + RAM + battery
        bytes[0x149] = 0x02; // 8 KiB RAM
        File.WriteAllBytes(rom.Path, bytes);
        var savePath = rom.Path + ".sav";

        try
        {
            var emulator = EmulatorFactory.Start(rom);
            emulator.Debug.PokeByte(0x0A, 0x0000);
            emulator.Debug.PokeByte(0x12, 0xA000);
            var state = emulator.CaptureState();
            emulator.Debug.PokeByte(0x34, 0xA000);

            emulator.RestoreState(state);
            Assert.Equal(0x12, emulator.Debug.PeekByte(0xA000));
            emulator.Terminate();

            var reloaded = EmulatorFactory.Start(rom);
            reloaded.Debug.PokeByte(0x0A, 0x0000);
            Assert.Equal(0x12, reloaded.Debug.PeekByte(0xA000));
            reloaded.Terminate();
        }
        finally
        {
            File.Delete(savePath);
        }
    }

    [Fact]
    public void FastForwardMatchesRepeatedFramesAndDrainsAudio()
    {
        using var regularRom = CreateCounterRom();
        using var fastRom = CreateCounterRom();
        var regular = EmulatorFactory.Start(regularRom);
        var fast = EmulatorFactory.Start(fastRom);

        for (var frame = 0; frame < 5; frame++)
        {
            regular.Update();
            regular.GetSoundSamples(out _);
        }

        Assert.Equal(5, fast.FastForward(5));
        AssertCpuEqual(regular.Debug.GetCpuState(), fast.Debug.GetCpuState());
        Assert.Equal(regular.Debug.PeekByte(0xC000), fast.Debug.PeekByte(0xC000));
        Assert.Equal(HashScreen(regular.GetScreenData()), HashScreen(fast.GetScreenData()));

        fast.GetSoundSamples(out var remainingAudioFrames);
        Assert.Equal(0, remainingAudioFrames);
        Assert.Throws<ArgumentOutOfRangeException>(() => fast.AdvanceFrames(-1));

        regular.Terminate();
        fast.Terminate();
    }

    [Fact]
    public void StateOperationsRequireRunningEmulator()
    {
        var emulator = new Emulator();
        Assert.Throws<InvalidOperationException>(() => emulator.CaptureState());

        using var rom = CreateCounterRom();
        emulator = EmulatorFactory.Start(rom);
        var state = emulator.CaptureState();
        emulator.Terminate();

        Assert.Throws<InvalidOperationException>(() => emulator.RestoreState(state));
    }

    private static byte[] ReplayMachineCycles(Emulator emulator, EmulatorState checkpoint, int machineCycles)
    {
        emulator.RestoreState(checkpoint);
        RunMachineCycles(emulator, machineCycles);
        return emulator.CaptureState().ToArray();
    }

    private static void RunMachineCycles(Emulator emulator, int machineCycles)
    {
        var observer = new StopAfterMachineCyclesObserver(emulator, machineCycles);
        emulator.SetTimingObserver(observer);
        try
        {
            emulator.Debug.Trace.BreakProgramCounter = null;
            emulator.Debug.Trace.BreakProgramCounters = null;
            emulator.Debug.Resume();
            emulator.Update();
        }
        finally
        {
            emulator.SetTimingObserver(null);
        }

        Assert.True(emulator.Debug.IsStopped);
        Assert.True(observer.ObservedMachineCycles >= machineCycles);
    }

    private static Emulator Start(TestRom rom, HardwareModel model, BootRomConfig bootRom)
    {
        var emulator = new Emulator();
        Assert.True(emulator.Start(new Emulator.Config(model)
        {
            ROMPath = rom.Path,
            SaveLocation = Path.GetTempPath(),
            BootRom = bootRom
        }));
        return emulator;
    }

    private static byte[] ReadBuiltInFirmware(string name)
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "Resources",
            name);

        if (File.Exists(path))
        {
            return File.ReadAllBytes(path);
        }

        var repositoryPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "GBZEmuLibrary", "Resources", name));
        return File.ReadAllBytes(repositoryPath);
    }

    private static TestRom CreateCounterRom()
    {
        return TestRom.Create(
            0x21, 0x00, 0xC0, // LD HL, C000
            0x34,             // INC (HL)
            0x18, 0xFD);      // JR back to INC
    }

    private sealed class StopAfterMachineCyclesObserver : ITimingObserver
    {
        private readonly Emulator _emulator;
        private readonly int _targetMachineCycles;

        public StopAfterMachineCyclesObserver(Emulator emulator, int targetMachineCycles)
        {
            _emulator = emulator;
            _targetMachineCycles = targetMachineCycles;
        }

        public int ObservedMachineCycles { get; private set; }

        public void Observe(in TimingEvent timingEvent)
        {
            if (timingEvent.Kind != TimingEventKind.MachineCycleCompleted)
            {
                return;
            }

            ObservedMachineCycles++;
            if (ObservedMachineCycles >= _targetMachineCycles)
            {
                _emulator.Debug.RequestStop();
            }
        }
    }

    private sealed class StopOnBufferedWakeObserver : ITimingObserver
    {
        private readonly Emulator _emulator;
        private readonly Func<bool> _haltFetched;
        private readonly Action _requestWake;
        private readonly Action _markHaltFetched;
        private bool _wakeRequested;

        public StopOnBufferedWakeObserver(
            Emulator emulator,
            Func<bool> haltFetched,
            Action requestWake,
            Action markHaltFetched)
        {
            _emulator = emulator;
            _haltFetched = haltFetched;
            _requestWake = requestWake;
            _markHaltFetched = markHaltFetched;
        }

        public void Observe(in TimingEvent timingEvent)
        {
            if (timingEvent.Kind == TimingEventKind.CpuReadObserved && timingEvent.Address == 0xFF80)
            {
                _markHaltFetched();
                return;
            }

            if (!_wakeRequested &&
                _haltFetched() &&
                timingEvent.Kind == TimingEventKind.SystemUpdateStarted &&
                timingEvent.Value == 1)
            {
                _wakeRequested = true;
                _requestWake();
                return;
            }

            if (_wakeRequested &&
                timingEvent.Kind == TimingEventKind.CpuReadObserved &&
                timingEvent.Address == 0xFF81)
            {
                _emulator.Debug.RequestStop();
            }
        }
    }

    private static void AssertCpuEqual(CpuDebugState expected, CpuDebugState actual)
    {
        Assert.Equal(expected.PC, actual.PC);
        Assert.Equal(expected.SP, actual.SP);
        Assert.Equal(expected.AF, actual.AF);
        Assert.Equal(expected.BC, actual.BC);
        Assert.Equal(expected.DE, actual.DE);
        Assert.Equal(expected.HL, actual.HL);
        Assert.Equal(expected.InterruptsEnabled, actual.InterruptsEnabled);
        Assert.Equal(expected.InterruptEnablePending, actual.InterruptEnablePending);
        Assert.Equal(expected.Halted, actual.Halted);
        Assert.Equal(expected.DoubleSpeed, actual.DoubleSpeed);
        Assert.Equal(expected.TotalClockCycles, actual.TotalClockCycles);
        Assert.Equal(expected.ExecutedInstructionCount, actual.ExecutedInstructionCount);
    }

    private static ulong HashScreen(Color[,] screen)
    {
        const ulong offsetBasis = 14695981039346656037;
        const ulong prime = 1099511628211;
        var hash = offsetBasis;

        for (var y = 0; y < Display.VERTICAL_RESOLUTION; y++)
        {
            for (var x = 0; x < Display.HORIZONTAL_RESOLUTION; x++)
            {
                var color = screen[x, y];
                hash = (hash ^ color.R) * prime;
                hash = (hash ^ color.G) * prime;
                hash = (hash ^ color.B) * prime;
            }
        }

        return hash;
    }

    private static ulong HashShades(byte[,] shades)
    {
        const ulong offsetBasis = 14695981039346656037;
        const ulong prime = 1099511628211;
        var hash = offsetBasis;

        for (var y = 0; y < Display.VERTICAL_RESOLUTION; y++)
        {
            for (var x = 0; x < Display.HORIZONTAL_RESOLUTION; x++)
            {
                hash = (hash ^ shades[x, y]) * prime;
            }
        }

        return hash;
    }

    private static ulong HashBytes(float[] data, int length)
    {
        const ulong offsetBasis = 14695981039346656037;
        const ulong prime = 1099511628211;
        var hash = offsetBasis;
        for (var index = 0; index < length; index++)
        {
            var bits = unchecked((uint)BitConverter.SingleToInt32Bits(data[index]));
            hash = (hash ^ (byte)bits) * prime;
            hash = (hash ^ (byte)(bits >> 8)) * prime;
            hash = (hash ^ (byte)(bits >> 16)) * prime;
            hash = (hash ^ (byte)(bits >> 24)) * prime;
        }

        return hash;
    }
}
