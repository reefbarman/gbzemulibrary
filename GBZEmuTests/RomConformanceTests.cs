namespace GBZEmuTests;

/// <summary>
/// Supplies cached ROM cases and executes their configured conformance oracles for parallel test shards.
/// </summary>
internal static class RomConformanceTestCases
{
    private const int ShardCount = 4;

    private static readonly Lazy<IReadOnlyList<RomTestCase>> Cases =
        new(() => RomManifest.Load().Tests);

    private static readonly Lazy<IReadOnlyDictionary<string, RomTestCase>> CasesById =
        new(() => Cases.Value.ToDictionary(test => test.Id, StringComparer.Ordinal));

    public static IEnumerable<object[]> Shard0() => GetShard(0);
    public static IEnumerable<object[]> Shard1() => GetShard(1);
    public static IEnumerable<object[]> Shard2() => GetShard(2);
    public static IEnumerable<object[]> Shard3() => GetShard(3);

    /// <summary>
    /// Runs one discovered test ROM through its configured oracle.
    /// </summary>
    public static void Run(string testId)
    {
        var test = CasesById.Value[testId];
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

    private static IEnumerable<object[]> GetShard(int shard)
    {
        return Cases.Value
            .Where((_, index) => index % ShardCount == shard)
            .Select(test => new object[] { test.Id });
    }
}

/// <summary>
/// Runs the first interleaved shard of ROM conformance cases.
/// </summary>
public sealed class RomConformanceShard0Tests
{
    /// <summary>
    /// Runs one ROM from this shard through its configured oracle.
    /// </summary>
    [Theory]
    [MemberData(nameof(RomConformanceTestCases.Shard0), MemberType = typeof(RomConformanceTestCases))]
    public void RomPassesConformanceOracle(string testId) => RomConformanceTestCases.Run(testId);
}

/// <summary>
/// Runs the second interleaved shard of ROM conformance cases.
/// </summary>
public sealed class RomConformanceShard1Tests
{
    /// <summary>
    /// Runs one ROM from this shard through its configured oracle.
    /// </summary>
    [Theory]
    [MemberData(nameof(RomConformanceTestCases.Shard1), MemberType = typeof(RomConformanceTestCases))]
    public void RomPassesConformanceOracle(string testId) => RomConformanceTestCases.Run(testId);
}

/// <summary>
/// Runs the third interleaved shard of ROM conformance cases.
/// </summary>
public sealed class RomConformanceShard2Tests
{
    /// <summary>
    /// Runs one ROM from this shard through its configured oracle.
    /// </summary>
    [Theory]
    [MemberData(nameof(RomConformanceTestCases.Shard2), MemberType = typeof(RomConformanceTestCases))]
    public void RomPassesConformanceOracle(string testId) => RomConformanceTestCases.Run(testId);
}

/// <summary>
/// Runs the fourth interleaved shard of ROM conformance cases.
/// </summary>
public sealed class RomConformanceShard3Tests
{
    /// <summary>
    /// Runs one ROM from this shard through its configured oracle.
    /// </summary>
    [Theory]
    [MemberData(nameof(RomConformanceTestCases.Shard3), MemberType = typeof(RomConformanceTestCases))]
    public void RomPassesConformanceOracle(string testId) => RomConformanceTestCases.Run(testId);
}
