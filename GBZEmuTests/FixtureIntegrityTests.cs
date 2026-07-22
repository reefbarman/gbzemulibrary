using GBZEmuLibrary;

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

        Assert.Equal(HardwareModel.CgbE, tests["samesuite/apu/channel_2/channel_2_nrx2_glitch"].HardwareModel);
        Assert.Equal(HardwareModel.CgbE, tests["samesuite/apu/channel_2/channel_2_nrx2_speed_change"].HardwareModel);
        Assert.DoesNotContain("samesuite/sgb/command_mlt_req", tests.Keys);
        Assert.DoesNotContain("mooneye/acceptance/boot_div-S", tests.Keys);
        Assert.DoesNotContain("mooneye/acceptance/boot_div2-S", tests.Keys);
        Assert.DoesNotContain("mooneye/acceptance/boot_hwio-S", tests.Keys);
        Assert.DoesNotContain("mooneye/acceptance/boot_regs-sgb", tests.Keys);
        Assert.Equal(HardwareModel.Sgb2, tests["mooneye/acceptance/boot_regs-sgb2"].HardwareModel);
        Assert.Contains("DMG-CPU-0", tests["mooneye/acceptance/boot_regs-dmg0"].SkipReason);
        Assert.Null(tests["mooneye/acceptance/boot_regs-mgb"].SkipReason);
        Assert.Contains("official DMG ABC/MGB firmware PPU handoff phase", tests["mooneye/acceptance/boot_hwio-dmgABCmgb"].SkipReason);
    }

    /// <summary>
    /// Keeps physical fixture inventory separate from the bounded model-specific execution matrix.
    /// </summary>
    [Fact]
    public void MgbExecutionVariantsPreservePhysicalFixtureInventory()
    {
        var fixtures = RomManifest.Load().Tests;
        var executions = RomManifest.CreateExecutionCases(fixtures);
        var byExecutionId = executions.ToDictionary(execution => execution.ExecutionId, StringComparer.Ordinal);
        var expectedMgbIds = new[]
        {
            "mooneye/acceptance/bits/unused_hwio-GS@Mgb",
            "mooneye/acceptance/boot_div-dmgABCmgb@Mgb",
            "mooneye/acceptance/boot_hwio-dmgABCmgb@Mgb",
            "mooneye/acceptance/boot_regs-mgb@Mgb",
            "mooneye/acceptance/ppu/hblank_ly_scx_timing-GS@Mgb",
            "mooneye/acceptance/ppu/lcdon_timing-GS@Mgb",
            "mooneye/acceptance/ppu/lcdon_write_timing-GS@Mgb",
            "mooneye/acceptance/serial/boot_sclk_align-dmgABCmgb@Mgb"
        };

        Assert.Equal(fixtures.Count + expectedMgbIds.Length - 1, executions.Count);
        Assert.Equal(expectedMgbIds, executions
            .Where(execution => execution.ExecutionId.EndsWith("@Mgb", StringComparison.Ordinal))
            .Select(execution => execution.ExecutionId));
        Assert.DoesNotContain("mooneye/acceptance/boot_regs-mgb", byExecutionId.Keys);

        foreach (var executionId in expectedMgbIds)
        {
            var execution = byExecutionId[executionId];
            Assert.Equal(HardwareModel.Mgb, execution.HardwareModel);
            Assert.Equal(executionId[..^"@Mgb".Length], execution.Fixture.Id);
        }

        foreach (var fixtureId in expectedMgbIds
                     .Select(id => id[..^"@Mgb".Length])
                     .Where(id => id != "mooneye/acceptance/boot_regs-mgb"))
        {
            Assert.Equal(HardwareModel.DmgB, byExecutionId[fixtureId].HardwareModel);
        }

        Assert.Null(byExecutionId["mooneye/acceptance/boot_regs-mgb@Mgb"].SkipReason);
        Assert.Contains(
            "official DMG ABC/MGB firmware PPU handoff phase",
            byExecutionId["mooneye/acceptance/boot_hwio-dmgABCmgb"].SkipReason);
        Assert.Contains(
            "official DMG ABC/MGB firmware PPU handoff phase",
            byExecutionId["mooneye/acceptance/boot_hwio-dmgABCmgb@Mgb"].SkipReason);
    }

    /// <summary>
    /// Keeps revision-specific SameSuite exclusions explicit while activating only the reviewed AGB-A timing execution.
    /// </summary>
    [Fact]
    public void SameSuiteApuRevisionExecutionsMatchSelectedTargets()
    {
        const string agbFixtureId = "samesuite/apu/channel_1/channel_1_freq_change_timing-A";
        const string agbExecutionId = agbFixtureId + "@AgbA";
        var fixtures = RomManifest.Load().Tests;
        var tests = fixtures
            .Where(test => test.Id.StartsWith("samesuite/apu/", StringComparison.Ordinal))
            .ToDictionary(test => test.Id, StringComparer.Ordinal);
        var executions = RomManifest.CreateExecutionCases(fixtures)
            .ToDictionary(execution => execution.ExecutionId, StringComparer.Ordinal);
        var skipped = executions.Values
            .Where(execution => execution.Fixture.Id.StartsWith("samesuite/apu/", StringComparison.Ordinal) &&
                                execution.SkipReason != null)
            .Select(execution => execution.ExecutionId)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        var expected = new[]
        {
            "samesuite/apu/channel_1/channel_1_extra_length_clocking-cgb0B",
            "samesuite/apu/channel_1/channel_1_freq_change_timing-cgb0BC",
            "samesuite/apu/channel_2/channel_2_extra_length_clocking-cgb0B",
            "samesuite/apu/channel_3/channel_3_extra_length_clocking-cgb0",
            "samesuite/apu/channel_3/channel_3_extra_length_clocking-cgbB",
            "samesuite/apu/channel_4/channel_4_extra_length_clocking-cgb0B"
        };

        Assert.Equal(expected, skipped);
        Assert.DoesNotContain(agbFixtureId, executions.Keys);
        Assert.Equal(HardwareRevisionRequirement.AgbA, tests[agbFixtureId].RevisionRequirement);
        Assert.Equal(HardwareModel.AgbA, executions[agbExecutionId].HardwareModel);
        Assert.Null(executions[agbExecutionId].SkipReason);
        Assert.Null(executions["samesuite/apu/channel_1/channel_1_freq_change_timing-cgbDE"].SkipReason);
        Assert.All(skipped, id => Assert.Contains("GBZEmu targets CPU CGB-E", executions[id].SkipReason, StringComparison.Ordinal));
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

    /// <summary>
    /// Rejects generated execution IDs that collide with a physical fixture ID.
    /// </summary>
    [Fact]
    public void ExecutionExpansionRejectsGeneratedIdCollisions()
    {
        var fixtures = new[]
        {
            new RomTestCase
            {
                Id = "mooneye/acceptance/boot_regs-mgb",
                HardwareModel = HardwareModel.DmgB
            },
            new RomTestCase
            {
                Id = "mooneye/acceptance/boot_regs-mgb@Mgb",
                HardwareModel = HardwareModel.DmgB
            }
        };

        var exception = Assert.Throws<InvalidOperationException>(
            () => RomManifest.CreateExecutionCases(fixtures));

        Assert.Contains("boot_regs-mgb@Mgb", exception.Message, StringComparison.Ordinal);
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
