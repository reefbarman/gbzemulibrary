namespace GBZEmuTests;

public sealed class FixtureIntegrityTests
{
    /// <summary>
    /// Compares discovered ROMs with the reviewed inventory and rejects duplicate IDs, missing fixture files, and
    /// orphaned known failures. This prevents accidental fixture loss or silent suite expansion from skewing totals.
    /// </summary>
    [Fact]
    public void FixtureInventoryMatchesExpectedIdsAndKnownFailures()
    {
        var expectedIds = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "ExpectedRomIds.txt"))
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToArray();
        var manifest = RomManifest.Load();
        var discoveredIds = manifest.Tests.Select(test => test.Id).ToArray();
        var knownFailures = KnownFailureRegistry.Load().Failures;
        var knownFailureIds = knownFailures.Select(failure => failure.Id).ToArray();

        AssertNoDuplicates("ExpectedRomIds.txt", expectedIds);
        AssertNoDuplicates("ROM manifest", discoveredIds);
        AssertNoDuplicates("KnownFailures.json", knownFailureIds);
        AssertSetEqual("fixture inventory", expectedIds, discoveredIds);

        var discoveredSet = discoveredIds.ToHashSet(StringComparer.Ordinal);
        var orphanedFailures = knownFailureIds
            .Where(id => !discoveredSet.Contains(id))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        Assert.True(orphanedFailures.Length == 0,
            $"KnownFailures.json contains IDs outside the fixture inventory: {string.Join(", ", orphanedFailures)}");

        var unclassifiedFailures = knownFailures
            .Where(failure => string.IsNullOrWhiteSpace(failure.RootCause) ||
                              failure.RootCause.Contains("diagnosis is required", StringComparison.OrdinalIgnoreCase))
            .Select(failure => failure.Id)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        Assert.True(unclassifiedFailures.Length == 0,
            $"Known failures require a specific root-cause category: {string.Join(", ", unclassifiedFailures)}");

        var missingFiles = manifest.Tests
            .Where(test => !File.Exists(test.RomPath))
            .Select(test => test.Id)
            .ToArray();
        Assert.True(missingFiles.Length == 0,
            $"Manifest entries have no ROM fixture: {string.Join(", ", missingFiles)}");
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
