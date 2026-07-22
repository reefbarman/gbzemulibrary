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
        var serialized = emulator.CaptureState().ToArray();

        emulator.AdvanceFrames(3);
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

    [Fact]
    public void OldSaveStateVersionIsRejectedWithoutMigration()
    {
        using var rom = CreateCounterRom();
        var emulator = EmulatorFactory.Start(rom);
        var bytes = emulator.CaptureState().ToArray();
        BitConverter.GetBytes(EmulatorState.CurrentFormatVersion - 1).CopyTo(bytes, 8);
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
