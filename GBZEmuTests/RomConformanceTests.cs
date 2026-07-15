namespace GBZEmuTests;

public sealed class RomConformanceTests
{
    public static IEnumerable<object[]> TestCases()
    {
        return RomManifest.Load().Tests.Select(test => new object[] { test.Id });
    }

    /// <summary>
    /// Runs each discovered test ROM through its configured oracle so Test Explorer reports its actual pass or failure.
    /// </summary>
    [Theory]
    [MemberData(nameof(TestCases))]
    public void RomPassesConformanceOracle(string testId)
    {
        var test = RomManifest.Load().Tests.Single(entry => entry.Id == testId);
        Run(test);
    }

    private static void Run(RomTestCase test)
    {
        Assert.True(File.Exists(test.RomPath), $"Missing ROM fixture: {test.RomPath}");
        using var runner = new RomTestRunner(test.RomPath, test.BootMode);

        switch (test.Protocol)
        {
            case RomProtocol.Serial:
                var serialOutput = runner.RunSerialProtocol(test.MaxFrames);
                if (serialOutput.Contains("Failed", StringComparison.Ordinal) ||
                    !serialOutput.Contains("Passed", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Serial result: {serialOutput.Trim()}");
                }

                break;
            case RomProtocol.BlarggMemory:
                var result = runner.RunBlarggMemoryProtocol(test.MaxFrames);
                Assert.True(result.Passed, $"Blargg status {result.Status}: {result.Message}");
                break;
            case RomProtocol.Fibonacci:
                runner.RunMooneyeProtocol(test.MaxFrames);
                break;
            case RomProtocol.Framebuffer:
                Assert.NotNull(test.ReferenceImagePath);
                Assert.True(File.Exists(test.ReferenceImagePath), $"Missing reference image: {test.ReferenceImagePath}");
                runner.RunToLoadBB(test.MaxFrames);
                var difference = FramebufferComparer.Compare(runner.Emulator.GetScreenData(), test.ReferenceImagePath, test.Hardware);
                if (difference != null)
                {
                    throw new InvalidOperationException($"Framebuffer mismatch: {difference}");
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(test.Protocol));
        }
    }
}
