namespace GBZEmuTests;

public sealed class FixtureIntegrityTests
{
    /// <summary>
    /// Compares discovered ROMs with the reviewed inventory and rejects duplicate IDs or missing fixture files.
    /// This prevents accidental fixture loss or silent suite expansion from changing conformance coverage.
    /// </summary>
    [Fact]
    public void FixtureInventoryMatchesExpectedIds()
    {
        var expectedIds = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "ExpectedRomIds.txt"))
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToArray();
        var manifest = RomManifest.Load();
        var discoveredIds = manifest.Tests.Select(test => test.Id).ToArray();

        AssertNoDuplicates("ExpectedRomIds.txt", expectedIds);
        AssertNoDuplicates("ROM manifest", discoveredIds);
        AssertSetEqual("fixture inventory", expectedIds, discoveredIds);

        var missingFiles = manifest.Tests
            .Where(test => !File.Exists(test.RomPath))
            .Select(test => test.Id)
            .ToArray();
        Assert.True(missingFiles.Length == 0,
            $"Manifest entries have no ROM fixture: {string.Join(", ", missingFiles)}");
    }

    /// <summary>
    /// Verifies the second Blargg memory-timing suite uses its documented A000 status/signature protocol.
    /// </summary>
    [Fact]
    public void MemoryTimingTwoFixturesUseBlarggMemoryProtocol()
    {
        var tests = RomManifest.Load().Tests
            .Where(test => test.Id.StartsWith("blargg/mem_timing-2/", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(tests);
        Assert.All(tests, test => Assert.Equal(RomProtocol.BlarggMemory, test.Protocol));
    }

    /// <summary>
    /// Supplies two fixture paths that normalize to the same test ID and verifies discovery rejects the collision.
    /// This prevents one ROM from silently replacing another before the locked inventory comparison runs.
    /// </summary>
    [Fact]
    public void ManifestRejectsDuplicateNormalizedFixtureIds()
    {
        var tests = new[]
        {
            new RomTestCase { Id = "suite/test_case", Rom = "suite/test case.gb" },
            new RomTestCase { Id = "suite/test_case", Rom = "suite/test_case.gbc" }
        };

        var exception = Assert.Throws<InvalidOperationException>(
            () => RomManifest.EnsureUniqueIds(tests, "test fixtures"));

        Assert.Contains("suite/test_case", exception.Message, StringComparison.Ordinal);
    }

    private static void AssertNoDuplicates(string source, IEnumerable<string> ids)
    {
        var duplicates = ids
            .GroupBy(id => id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        Assert.True(duplicates.Length == 0,
            $"{source} contains duplicate IDs: {string.Join(", ", duplicates)}");
    }

    private static void AssertSetEqual(string source, IEnumerable<string> expected, IEnumerable<string> actual)
    {
        var expectedSet = expected.ToHashSet(StringComparer.Ordinal);
        var actualSet = actual.ToHashSet(StringComparer.Ordinal);
        var missing = expectedSet.Except(actualSet).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        var unexpected = actualSet.Except(expectedSet).OrderBy(id => id, StringComparer.Ordinal).ToArray();

        Assert.True(missing.Length == 0 && unexpected.Length == 0,
            $"Unexpected {source} change. Missing: [{string.Join(", ", missing)}]. Unexpected: [{string.Join(", ", unexpected)}].");
    }
}
