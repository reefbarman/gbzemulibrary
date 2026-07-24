using GBZEmuLibrary;

namespace GBZEmuTests;

/// <summary>
/// Verifies path- and byte-backed emulator startup, ROM ownership, persistence identity, and state compatibility.
/// </summary>
public sealed class EmulatorRomSourceTests
{
    [Fact]
    public void StartRequiresExactlyOneRomSourceBeforeRomOrFirmwareIo()
    {
        var missingRom = Path.Combine(Path.GetTempPath(), $"missing-rom-{Guid.NewGuid():N}.gb");
        var missingBootRom = Path.Combine(Path.GetTempPath(), $"missing-boot-{Guid.NewGuid():N}.bin");
        var noSource = new Emulator();
        var bothSources = new Emulator();
        var whitespacePathAndBytes = new Emulator();

        var noSourceError = Assert.Throws<ArgumentException>(() => noSource.Start(new Emulator.Config(HardwareModel.DmgB)
        {
            BootRom = BootRomConfig.ExternalFile(missingBootRom)
        }));
        var bothSourcesError = Assert.Throws<ArgumentException>(() => bothSources.Start(new Emulator.Config(HardwareModel.DmgB)
        {
            ROMPath = missingRom,
            ROMBytes = CreateRomBytes(),
            ROMIdentity = "patched.gb",
            BootRom = BootRomConfig.ExternalFile(missingBootRom)
        }));
        var whitespacePathError = Assert.Throws<ArgumentException>(() => whitespacePathAndBytes.Start(new Emulator.Config(HardwareModel.DmgB)
        {
            ROMPath = " ",
            ROMBytes = CreateRomBytes(),
            ROMIdentity = "patched.gb",
            BootRom = BootRomConfig.ExternalFile(missingBootRom)
        }));

        Assert.Contains("exactly one ROM source", noSourceError.Message, StringComparison.Ordinal);
        Assert.Contains("exactly one ROM source", bothSourcesError.Message, StringComparison.Ordinal);
        Assert.Contains("exactly one ROM source", whitespacePathError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingPathReturnsFalseAndAllowsRetryWithBytes()
    {
        var missingRom = Path.Combine(Path.GetTempPath(), $"missing-rom-{Guid.NewGuid():N}.gb");
        var saveDirectory = CreateTemporaryDirectory();
        var emulator = new Emulator();
        try
        {
            Assert.False(emulator.Start(new Emulator.Config(HardwareModel.DmgB)
            {
                ROMPath = missingRom,
                SaveLocation = saveDirectory,
                BootRom = BootRomConfig.Skip()
            }));

            Assert.True(emulator.Start(new Emulator.Config(HardwareModel.DmgB)
            {
                ROMBytes = CreateRomBytes(),
                ROMIdentity = "retry.gb",
                SaveLocation = saveDirectory,
                BootRom = BootRomConfig.Skip()
            }));
        }
        finally
        {
            emulator.Terminate();
            Directory.Delete(saveDirectory, recursive: true);
        }
    }

    [Fact]
    public void PathBackedStartupRejectsOversizedFileBeforeLoadingIt()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gbzemu-oversized-{Guid.NewGuid():N}.gb");
        try
        {
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.SetLength(0x800000 + 1L);
            }

            var emulator = new Emulator();
            Assert.Throws<InvalidDataException>(() => emulator.Start(new Emulator.Config(HardwareModel.DmgB)
            {
                ROMPath = path,
                BootRom = BootRomConfig.Skip()
            }));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ByteBackedStartupTakesPrivateRomCopy()
    {
        var bytes = CreateRomBytes();
        bytes[0x150] = 0x42;
        using var started = StartBytes(bytes, "owned.gb");

        bytes[0x150] = 0x99;

        Assert.Equal(0x42, started.Emulator.Debug.PeekByte(0x0150));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("../patched.gb")]
    [InlineData("folder/patched.gb")]
    [InlineData("folder\\patched.gb")]
    [InlineData("patched?.gb")]
    [InlineData("patched.gb.")]
    [InlineData("patched.gb ")]
    [InlineData("CON")]
    [InlineData("NUL.gb")]
    [InlineData("COM1.gb")]
    [InlineData("LPT9")]
    public void ByteBackedStartupRejectsMissingOrUnsafeIdentity(string? identity)
    {
        var emulator = new Emulator();

        Assert.Throws<ArgumentException>(() => emulator.Start(new Emulator.Config(HardwareModel.DmgB)
        {
            ROMBytes = CreateRomBytes(),
            ROMIdentity = identity!,
            BootRom = BootRomConfig.Skip()
        }));
    }

    [Fact]
    public void ByteBackedIdentityAcceptsConfiguredMaximumLength()
    {
        using var started = StartBytes(CreateRomBytes(), new string('a', 240));

        Assert.True(started.Emulator.Debug.GetCpuState().PC >= 0x100);
    }

    [Fact]
    public void ByteBackedIdentityRejectsLengthAboveConfiguredMaximum()
    {
        var emulator = new Emulator();

        Assert.Throws<ArgumentException>(() => emulator.Start(new Emulator.Config(HardwareModel.DmgB)
        {
            ROMBytes = CreateRomBytes(),
            ROMIdentity = new string('a', 241),
            BootRom = BootRomConfig.Skip()
        }));
    }

    [Fact]
    public void ByteBackedStartupUsesExplicitIdentityForBatterySave()
    {
        var saveDirectory = CreateTemporaryDirectory();
        try
        {
            var bytes = CreateRomBytes();
            bytes[0x147] = 0x03;
            bytes[0x149] = 0x02;
            using var started = StartBytes(bytes, "patched-identity", saveDirectory);

            started.Emulator.Debug.PokeByte(0x0A, 0x0000);
            started.Emulator.Debug.PokeByte(0x5A, 0xA000);
            started.Emulator.Terminate();

            var savePath = Path.Combine(saveDirectory, "patched-identity.sav");
            Assert.True(File.Exists(savePath));
            Assert.Equal(0x5A, File.ReadAllBytes(savePath)[0]);
        }
        finally
        {
            Directory.Delete(saveDirectory, recursive: true);
        }
    }

    [Fact]
    public void ByteIdenticalPathAndByteSourcesShareStateIdentity()
    {
        using var rom = TestRom.Create(0x00);
        var bytes = File.ReadAllBytes(rom.Path);
        var pathEmulator = EmulatorFactory.Start(rom);
        var state = pathEmulator.CaptureState();
        pathEmulator.Terminate();

        using var byteEmulator = StartBytes(bytes, "same-content.gb");
        byteEmulator.Emulator.RestoreState(state);
    }

    [Fact]
    public void DifferentByteBackedRomRejectsState()
    {
        var firstBytes = CreateRomBytes();
        using var first = StartBytes(firstBytes, "first.gb");
        var state = first.Emulator.CaptureState();
        first.Emulator.Terminate();

        var secondBytes = (byte[])firstBytes.Clone();
        secondBytes[0x150] ^= 0xFF;
        using var second = StartBytes(secondBytes, "second.gb");

        Assert.Throws<InvalidOperationException>(() => second.Emulator.RestoreState(state));
        second.Emulator.Terminate();
    }

    [Fact]
    public void SuccessfulByteBackedStartRetainsSingleUseLifecycle()
    {
        using var started = StartBytes(CreateRomBytes(), "first.gb");

        Assert.Throws<InvalidOperationException>(() => started.Emulator.Start(new Emulator.Config(HardwareModel.DmgB)
        {
            ROMBytes = CreateRomBytes(),
            ROMIdentity = "second.gb",
            BootRom = BootRomConfig.Skip()
        }));
        started.Emulator.Terminate();
    }

    private static StartedEmulator StartBytes(byte[] bytes, string identity, string? saveDirectory = null)
    {
        var ownsSaveDirectory = saveDirectory == null;
        saveDirectory ??= CreateTemporaryDirectory();
        var emulator = new Emulator();
        Assert.True(emulator.Start(new Emulator.Config(HardwareModel.DmgB)
        {
            ROMBytes = bytes,
            ROMIdentity = identity,
            SaveLocation = saveDirectory,
            BootRom = BootRomConfig.Skip()
        }));
        return new StartedEmulator(emulator, saveDirectory, ownsSaveDirectory);
    }

    private static byte[] CreateRomBytes()
    {
        var bytes = new byte[0x8000];
        bytes[0x100] = 0x00;
        bytes[0x147] = 0x00;
        bytes[0x148] = 0x00;
        bytes[0x149] = 0x00;
        return bytes;
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gbzemu-rom-source-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class StartedEmulator : IDisposable
    {
        private readonly bool _ownsSaveDirectory;

        public StartedEmulator(Emulator emulator, string saveDirectory, bool ownsSaveDirectory)
        {
            Emulator = emulator;
            SaveDirectory = saveDirectory;
            _ownsSaveDirectory = ownsSaveDirectory;
        }

        public Emulator Emulator { get; }
        public string SaveDirectory { get; }

        public void Dispose()
        {
            Emulator.Terminate();
            if (_ownsSaveDirectory && Directory.Exists(SaveDirectory))
            {
                Directory.Delete(SaveDirectory, recursive: true);
            }
        }
    }
}
