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
    /// Verifies only the two defined CGB cartridge-header values select CGB hardware for ROM execution.
    /// </summary>
    [Theory]
    [InlineData(0x00, false)]
    [InlineData(0x80, true)]
    [InlineData(0xC0, true)]
    [InlineData(0xFF, false)]
    public void CgbHeaderClassificationUsesDefinedValues(int cgbFlag, bool expected)
    {
        Assert.Equal(expected, RomManifest.IsCgbHeader(cgbFlag));
    }

    /// <summary>
    /// Verifies CGB-compatible SameSuite APU fixtures are not forced onto DMG hardware.
    /// </summary>
    [Fact]
    public void CgbCompatibleApuFixturesUseCgbHardware()
    {
        var tests = RomManifest.Load().Tests.ToDictionary(test => test.Id, StringComparer.Ordinal);

        Assert.Equal(HardwareMode.Cgb, tests["samesuite/apu/channel_2/channel_2_nrx2_glitch"].Hardware);
        Assert.Equal(HardwareMode.Cgb, tests["samesuite/apu/channel_2/channel_2_nrx2_speed_change"].Hardware);
        Assert.Equal(HardwareMode.Sgb, tests["samesuite/sgb/command_mlt_req"].Hardware);
        Assert.Equal(HardwareMode.Sgb, tests["mooneye/acceptance/boot_div-S"].Hardware);
        Assert.Equal(HardwareMode.Sgb, tests["mooneye/acceptance/boot_div2-S"].Hardware);
        Assert.Equal(HardwareMode.Sgb, tests["mooneye/acceptance/boot_hwio-S"].Hardware);
        Assert.Equal(HardwareMode.Sgb, tests["mooneye/acceptance/boot_regs-sgb"].Hardware);
        Assert.Equal(HardwareMode.Sgb2, tests["mooneye/acceptance/boot_regs-sgb2"].Hardware);
        Assert.Contains("DMG-CPU-0", tests["mooneye/acceptance/boot_regs-dmg0"].SkipReason);
        Assert.Contains("MGB startup", tests["mooneye/acceptance/boot_regs-mgb"].SkipReason);
        Assert.Contains("original SGB/SGB2 boot-ROM DIV phase", tests["mooneye/acceptance/boot_div-S"].SkipReason);
    }

    /// <summary>
    /// Keeps revision-specific SameSuite exclusions explicit while retaining CGB-D/E coverage for the selected CGB-E target.
    /// </summary>
    [Fact]
    public void SameSuiteApuRevisionSkipsMatchDmgBAndCgbETargets()
    {
        var tests = RomManifest.Load().Tests
            .Where(test => test.Id.StartsWith("samesuite/apu/", StringComparison.Ordinal))
            .ToDictionary(test => test.Id, StringComparer.Ordinal);
        var skipped = tests.Values
            .Where(test => test.SkipReason != null)
            .Select(test => test.Id)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        var expected = new[]
        {
            "samesuite/apu/channel_1/channel_1_extra_length_clocking-cgb0B",
            "samesuite/apu/channel_1/channel_1_freq_change_timing-A",
            "samesuite/apu/channel_1/channel_1_freq_change_timing-cgb0BC",
            "samesuite/apu/channel_2/channel_2_extra_length_clocking-cgb0B",
            "samesuite/apu/channel_3/channel_3_extra_length_clocking-cgb0",
            "samesuite/apu/channel_3/channel_3_extra_length_clocking-cgbB",
            "samesuite/apu/channel_4/channel_4_extra_length_clocking-cgb0B"
        };

        Assert.Equal(expected, skipped);
        Assert.Null(tests["samesuite/apu/channel_1/channel_1_freq_change_timing-cgbDE"].SkipReason);
        Assert.All(skipped, id => Assert.Contains("GBZEmu targets CPU CGB-E", tests[id].SkipReason, StringComparison.Ordinal));
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
