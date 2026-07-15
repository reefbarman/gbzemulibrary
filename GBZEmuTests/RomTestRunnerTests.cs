namespace GBZEmuTests;

public sealed class RomTestRunnerTests
{
    /// <summary>
    /// Emits the standard serial "Passed" marker from a synthetic ROM and verifies the runner terminates with
    /// that output. This checks the protocol used by serial-reporting Blargg fixtures without a large ROM dependency.
    /// </summary>
    [Fact]
    public void SerialRunnerDetectsPassedOutput()
    {
        using var rom = TestRom.Create(
            0x3E, (byte)'P', 0xEA, 0x01, 0xFF, 0x3E, 0x81, 0xEA, 0x02, 0xFF,
            0x3E, (byte)'a', 0xEA, 0x01, 0xFF, 0x3E, 0x81, 0xEA, 0x02, 0xFF,
            0x3E, (byte)'s', 0xEA, 0x01, 0xFF, 0x3E, 0x81, 0xEA, 0x02, 0xFF,
            0x3E, (byte)'s', 0xEA, 0x01, 0xFF, 0x3E, 0x81, 0xEA, 0x02, 0xFF,
            0x3E, (byte)'e', 0xEA, 0x01, 0xFF, 0x3E, 0x81, 0xEA, 0x02, 0xFF,
            0x3E, (byte)'d', 0xEA, 0x01, 0xFF, 0x3E, 0x81, 0xEA, 0x02, 0xFF,
            0x18, 0xFE);
        using var runner = new RomTestRunner(rom.Path);

        var output = runner.RunSerialProtocol(1);

        Assert.Equal("Passed", output);
    }

    /// <summary>
    /// Emits both terminal markers in one frame and verifies that "Failed" wins over an earlier "Passed" token.
    /// This prevents diagnostic text or multiple subtest results from producing a false conformance pass.
    /// </summary>
    [Fact]
    public void SerialRunnerRejectsOutputContainingPassedAndFailedMarkers()
    {
        using var rom = TestRom.Create(
            0x21, 0x00, 0x02,
            0x06, 12,
            0x2A,
            0xEA, 0x01, 0xFF,
            0x3E, 0x81,
            0xEA, 0x02, 0xFF,
            0x05,
            0x20, 0xF4,
            0x18, 0xFE);
        var bytes = File.ReadAllBytes(rom.Path);
        "PassedFailed"u8.CopyTo(bytes.AsSpan(0x0200));
        File.WriteAllBytes(rom.Path, bytes);
        using var runner = new RomTestRunner(rom.Path);

        var exception = Assert.Throws<InvalidOperationException>(() => runner.RunSerialProtocol(1));

        Assert.Contains("Failed", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Emits a serial diagnostic before an invalid Mooneye fingerprint and verifies the failure preserves that text.
    /// Mooneye and SameSuite use serial output to identify the exact subtest that failed before their LD B,B marker.
    /// </summary>
    [Fact]
    public void MooneyeRunnerIncludesSerialFailureDiagnostic()
    {
        using var rom = TestRom.Create(
            0x21, 0x00, 0x02,
            0x06, 4,
            0x2A,
            0xEA, 0x01, 0xFF,
            0x3E, 0x81,
            0xEA, 0x02, 0xFF,
            0x05,
            0x20, 0xF4,
            0x40,
            0x18, 0xFE);
        var bytes = File.ReadAllBytes(rom.Path);
        "FF03"u8.CopyTo(bytes.AsSpan(0x0200));
        File.WriteAllBytes(rom.Path, bytes);
        using var runner = new RomTestRunner(rom.Path);

        var exception = Assert.Throws<InvalidOperationException>(() => runner.RunMooneyeProtocol(1));

        Assert.Contains("Serial output: FF03", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Builds the Mooneye Fibonacci register fingerprint and executes LD B,B as its completion signal.
    /// This validates both the protocol oracle and exact-state capture at the test ROM's breakpoint convention.
    /// </summary>
    [Fact]
    public void MooneyeRunnerDetectsRegisterFingerprint()
    {
        using var rom = TestRom.Create(
            0x06, 3,
            0x0E, 5,
            0x16, 8,
            0x1E, 13,
            0x26, 21,
            0x2E, 34,
            0x40,
            0x18, 0xFE);
        using var runner = new RomTestRunner(rom.Path);

        var state = runner.RunMooneyeProtocol(1);

        Assert.Equal(0x0305, state.BC);
        Assert.Equal(0x080D, state.DE);
        Assert.Equal(0x1522, state.HL);
    }
}
