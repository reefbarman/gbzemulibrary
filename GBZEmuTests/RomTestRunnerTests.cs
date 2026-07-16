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
        using var rom = TestRom.Create(BuildSerialOutputProgram("Passed"));
        using var runner = new RomTestRunner(rom.Path);

        var output = runner.RunSerialProtocol(2);

        Assert.Equal("Passed", output);
    }

    /// <summary>
    /// Emits both terminal markers in one frame and verifies that "Failed" wins over an earlier "Passed" token.
    /// This prevents diagnostic text or multiple subtest results from producing a false conformance pass.
    /// </summary>
    [Fact]
    public void SerialRunnerRejectsOutputContainingPassedAndFailedMarkers()
    {
        using var rom = TestRom.Create(BuildSerialOutputProgram("PassedFailed"));
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
        using var rom = TestRom.Create(BuildSerialOutputProgram("FF03", signalMooneyeCompletion: true));
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

    /// <summary>
    /// Builds a synthetic ROM routine that sends each character through the internal serial clock and waits for SC
    /// bit 7 to clear before loading the next byte, matching the handshake used by real diagnostic ROMs.
    /// </summary>
    private static byte[] BuildSerialOutputProgram(string output, bool signalMooneyeCompletion = false)
    {
        // Keep executable code beyond the cartridge header fields that TestRom.Create normalizes at 0x0147-0x0149.
        var program = new List<byte> { 0xC3, 0x50, 0x01 }; // JP 0x0150
        while (program.Count < 0x50)
        {
            program.Add(0x00);
        }

        foreach (var character in output)
        {
            program.AddRange(new byte[]
            {
                0x3E, (byte)character, // LD A, character
                0xE0, 0x01,            // LDH (SB), A
                0x3E, 0x81,            // LD A, transfer start with internal clock
                0xE0, 0x02,            // LDH (SC), A
                0xF0, 0x02,            // wait: LDH A, (SC)
                0x87,                  // ADD A, A; SC bit 7 moves into carry
                0x38, 0xFB             // JR C, wait
            });
        }

        if (signalMooneyeCompletion)
        {
            program.Add(0x40); // LD B, B
        }

        program.AddRange(new byte[] { 0x18, 0xFE }); // JR forever
        return program.ToArray();
    }
}
