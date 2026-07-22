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
    /// Compares committed-but-excluded ROMs with the reviewed exact original-SGB exclusion inventory.
    /// This prevents broad discovery filters from silently suppressing additional physical fixtures.
    /// </summary>
    [Fact]
    public void ExcludedFixtureInventoryMatchesExpectedIds()
    {
        var expectedIds = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "ExpectedRomExcludedIds.txt"))
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToArray();
        var fixturesRoot = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        var committedIds = Directory.EnumerateFiles(fixturesRoot, "*.gb*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".gb", StringComparison.OrdinalIgnoreCase) ||
                           path.EndsWith(".gbc", StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.ChangeExtension(Path.GetRelativePath(fixturesRoot, path), null)!
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(' ', '_'))
            .ToArray();
        var activeIds = RomManifest.Load().Tests
            .Select(fixture => fixture.Id)
            .ToHashSet(StringComparer.Ordinal);
        var excludedIds = committedIds
            .Where(id => !activeIds.Contains(id))
            .ToArray();

        AssertNoDuplicates("ExpectedRomExcludedIds.txt", expectedIds);
        AssertNoDuplicates("committed ROM fixtures", committedIds);
        AssertSetEqual("excluded fixture inventory", expectedIds, excludedIds);
    }

    /// <summary>
    /// Compares generated model-specific execution IDs with the reviewed execution inventory.
    /// This prevents applicability rules from silently adding or removing concrete-model rows.
    /// </summary>
    [Fact]
    public void ExecutionInventoryMatchesExpectedIds()
    {
        var expectedIds = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "ExpectedRomExecutionIds.txt"))
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToArray();
        var executionIds = RomManifest.CreateExecutionCases(RomManifest.Load().Tests)
            .Select(execution => execution.ExecutionId)
            .ToArray();

        AssertNoDuplicates("ExpectedRomExecutionIds.txt", expectedIds);
        AssertNoDuplicates("ROM execution cases", executionIds);
        AssertSetEqual("execution inventory", expectedIds, executionIds);
    }

    /// <summary>
    /// Compares unresolved applicability and oracle keys with the reviewed unresolved inventory.
    /// This keeps unknown model pairs visible without turning them into executions or xUnit skips.
    /// </summary>
    [Fact]
    public void UnresolvedInventoryMatchesExpectedIds()
    {
        var expectedIds = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "ExpectedRomUnresolvedIds.txt"))
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToArray();
        var fixtures = RomManifest.Load().Tests;
        var unresolvedIds = RomApplicability.GetUnresolvedDecisions(fixtures)
            .Select(decision => decision.DecisionKey)
            .Concat(RomApplicability.GetOracleDecisions(fixtures)
                .Where(decision => decision.Disposition == OracleApplicabilityDisposition.Unresolved)
                .Select(decision => decision.DecisionKey))
            .ToArray();

        AssertNoDuplicates("ExpectedRomUnresolvedIds.txt", expectedIds);
        AssertNoDuplicates("unresolved applicability decisions", unresolvedIds);
        AssertSetEqual("unresolved decision inventory", expectedIds, unresolvedIds);
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
    /// Verifies CGB-compatible SameSuite APU fixtures are not forced onto DMG hardware and only exact original-SGB fixtures remain excluded.
    /// </summary>
    [Fact]
    public void CgbCompatibleApuFixturesUseCgbHardware()
    {
        var tests = RomManifest.Load().Tests.ToDictionary(test => test.Id, StringComparer.Ordinal);

        Assert.Equal(HardwareModel.CgbE, tests["samesuite/apu/channel_2/channel_2_nrx2_glitch"].HardwareModel);
        Assert.Equal(HardwareModel.CgbE, tests["samesuite/apu/channel_2/channel_2_nrx2_speed_change"].HardwareModel);
        Assert.DoesNotContain("samesuite/sgb/command_mlt_req", tests.Keys);
        Assert.Contains("mooneye/acceptance/boot_div-S", tests.Keys);
        Assert.Contains("mooneye/acceptance/boot_div2-S", tests.Keys);
        Assert.Contains("mooneye/acceptance/boot_hwio-S", tests.Keys);
        Assert.DoesNotContain("mooneye/acceptance/boot_regs-sgb", tests.Keys);
        Assert.Equal(HardwareModel.Sgb2, tests["mooneye/acceptance/boot_regs-sgb2"].HardwareModel);
        Assert.Contains("DMG-CPU-0", tests["mooneye/acceptance/boot_regs-dmg0"].SkipReason);
        Assert.Null(tests["mooneye/acceptance/boot_regs-mgb"].SkipReason);
        Assert.Contains("official DMG ABC/MGB firmware PPU handoff phase", tests["mooneye/acceptance/boot_hwio-dmgABCmgb"].SkipReason);
    }

    /// <summary>
    /// Applies Mooneye's S and GS model groups literally while preserving the matching-firmware startup circumstance.
    /// </summary>
    [Fact]
    public void MooneyeSgbGroupsProduceReviewedSgb2Executions()
    {
        var fixtures = RomManifest.Load().Tests;
        var executions = RomManifest.CreateExecutionCases(fixtures);
        var byFixtureId = executions.ToLookup(execution => execution.Fixture.Id, StringComparer.Ordinal);
        var sgbGroupIds = fixtures
            .Where(fixture => RomApplicability.IsMooneyeSgbGroupFixture(fixture.Id))
            .Select(fixture => fixture.Id)
            .ToArray();
        var gsIds = fixtures
            .Where(fixture => RomApplicability.IsMooneyeGsFixture(fixture.Id))
            .Select(fixture => fixture.Id)
            .ToArray();

        Assert.Equal(3, sgbGroupIds.Length);
        foreach (var fixtureId in sgbGroupIds)
        {
            var execution = Assert.Single(byFixtureId[fixtureId]);
            Assert.Equal($"{fixtureId}@Sgb2", execution.ExecutionId);
            Assert.Equal(HardwareModel.Sgb2, execution.HardwareModel);
            Assert.Equal(ConformanceStartupCircumstance.MatchingFirmwareRequired, execution.StartupCircumstance);
            Assert.Contains("matching Sgb2 firmware post-boot state", execution.SkipReason, StringComparison.Ordinal);
        }

        Assert.Equal(9, gsIds.Length);
        foreach (var fixtureId in gsIds)
        {
            AssertSetEqual(
                $"{fixtureId} GS executions",
                new[] { fixtureId, $"{fixtureId}@Mgb", $"{fixtureId}@Sgb2" },
                byFixtureId[fixtureId].Select(execution => execution.ExecutionId));
            Assert.Equal(HardwareModel.DmgB, byFixtureId[fixtureId].Single(execution => execution.ExecutionId == fixtureId).HardwareModel);
            Assert.Equal(HardwareModel.Mgb, byFixtureId[fixtureId].Single(execution => execution.ExecutionId.EndsWith("@Mgb", StringComparison.Ordinal)).HardwareModel);
            Assert.Equal(HardwareModel.Sgb2, byFixtureId[fixtureId].Single(execution => execution.ExecutionId.EndsWith("@Sgb2", StringComparison.Ordinal)).HardwareModel);
        }

        Assert.DoesNotContain("mooneye/acceptance/boot_regs-sgb", fixtures.Select(fixture => fixture.Id));
        Assert.DoesNotContain(executions, execution => execution.Fixture.Id == "mooneye/acceptance/boot_regs-sgb");
    }

    /// <summary>
    /// Expands untagged Mooneye acceptance fixtures across all five canonical models without replacing stable primary IDs.
    /// </summary>
    [Fact]
    public void UntaggedMooneyeAcceptanceFixturesUseCanonicalModelFanOut()
    {
        var fixtures = RomManifest.Load().Tests;
        var executions = RomManifest.CreateExecutionCases(fixtures);
        var byFixtureId = executions.ToLookup(execution => execution.Fixture.Id, StringComparer.Ordinal);
        var untagged = fixtures
            .Where(fixture => RomApplicability.IsUntaggedMooneyeAcceptance(fixture.Id))
            .ToArray();
        var canonicalModels = new[]
        {
            HardwareModel.DmgB,
            HardwareModel.Mgb,
            HardwareModel.CgbE,
            HardwareModel.Sgb2,
            HardwareModel.AgbA
        };

        Assert.Equal(53, untagged.Length);
        foreach (var fixture in untagged)
        {
            var fixtureExecutions = byFixtureId[fixture.Id].ToArray();
            Assert.Equal(5, fixtureExecutions.Length);
            Assert.Equal(canonicalModels, fixtureExecutions
                .Select(execution => execution.HardwareModel)
                .OrderBy(model => Array.IndexOf(canonicalModels, model)));
            Assert.Equal(fixture.HardwareModel, fixtureExecutions.Single(execution => execution.ExecutionId == fixture.Id).HardwareModel);
            Assert.DoesNotContain($"{fixture.Id}@{fixture.HardwareModel}", fixtureExecutions.Select(execution => execution.ExecutionId));
        }
    }

    /// <summary>
    /// Keeps model-specific startup fixtures bounded while preserving their existing visible skip behavior.
    /// </summary>
    [Fact]
    public void ModelSpecificMooneyeExecutionsRemainBounded()
    {
        var executions = RomManifest.CreateExecutionCases(RomManifest.Load().Tests);
        var byExecutionId = executions.ToDictionary(execution => execution.ExecutionId, StringComparer.Ordinal);

        Assert.DoesNotContain("mooneye/acceptance/boot_regs-mgb", byExecutionId.Keys);
        Assert.Equal(HardwareModel.Mgb, byExecutionId["mooneye/acceptance/boot_regs-mgb@Mgb"].HardwareModel);
        Assert.Null(byExecutionId["mooneye/acceptance/boot_regs-mgb@Mgb"].SkipReason);
        Assert.Equal(HardwareModel.DmgB, byExecutionId["mooneye/acceptance/boot_div-dmgABCmgb"].HardwareModel);
        Assert.Equal(HardwareModel.Mgb, byExecutionId["mooneye/acceptance/boot_div-dmgABCmgb@Mgb"].HardwareModel);
        Assert.Contains(
            "official DMG ABC/MGB firmware PPU handoff phase",
            byExecutionId["mooneye/acceptance/boot_hwio-dmgABCmgb"].SkipReason);
        Assert.Contains(
            "official DMG ABC/MGB firmware PPU handoff phase",
            byExecutionId["mooneye/acceptance/boot_hwio-dmgABCmgb@Mgb"].SkipReason);
    }

    /// <summary>
    /// Prevents Mooneye emulator-only mapper fixtures from inheriting acceptance-suite model expansion.
    /// </summary>
    [Fact]
    public void MooneyeMapperFixturesRemainSingleRun()
    {
        var fixtures = RomManifest.Load().Tests
            .Where(fixture => fixture.Id.StartsWith("mooneye/emulator-only/", StringComparison.Ordinal))
            .ToArray();
        var executions = RomManifest.CreateExecutionCases(RomManifest.Load().Tests)
            .ToLookup(execution => execution.Fixture.Id, StringComparer.Ordinal);

        Assert.Equal(28, fixtures.Length);
        foreach (var fixture in fixtures)
        {
            var execution = Assert.Single(executions[fixture.Id]);
            Assert.Equal(fixture.Id, execution.ExecutionId);
            Assert.Equal(fixture.HardwareModel, execution.HardwareModel);
        }
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
        Assert.Equal("CPU CGB-0/B/C", HardwareRevisionRequirement.Cgb0BC.DisplayName());
        Assert.DoesNotContain("CGB-A", HardwareRevisionRequirement.Cgb0BC.DisplayName(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Adds only the reviewed pre-CGB and all-model SameSuite executions while preserving stable primary IDs.
    /// </summary>
    [Fact]
    public void SameSuiteCanonicalModelExecutionsMatchReviewedMatrix()
    {
        var fixtures = RomManifest.Load().Tests;
        var executions = RomManifest.CreateExecutionCases(fixtures);
        var byFixtureId = executions.ToLookup(execution => execution.Fixture.Id, StringComparer.Ordinal);
        var preCgbFixtures = fixtures
            .Where(fixture => RomApplicability.IsSameSuitePreCgbFixture(fixture.Id))
            .ToArray();
        var allModelFixture = Assert.Single(fixtures, fixture => RomApplicability.IsSameSuiteAllModelFixture(fixture.Id));

        Assert.Equal(2, preCgbFixtures.Length);
        foreach (var fixture in preCgbFixtures)
        {
            Assert.Equal(HardwareModel.CgbE, fixture.HardwareModel);
            AssertSetEqual(
                $"{fixture.Id} pre-CGB executions",
                new[]
                {
                    fixture.Id,
                    $"{fixture.Id}@DmgB",
                    $"{fixture.Id}@Mgb",
                    $"{fixture.Id}@Sgb2"
                },
                byFixtureId[fixture.Id].Select(execution => execution.ExecutionId));
            Assert.All(byFixtureId[fixture.Id], execution => Assert.Null(execution.SkipReason));
        }

        Assert.Equal(HardwareModel.DmgB, allModelFixture.HardwareModel);
        AssertSetEqual(
            $"{allModelFixture.Id} canonical executions",
            new[]
            {
                allModelFixture.Id,
                $"{allModelFixture.Id}@Mgb",
                $"{allModelFixture.Id}@CgbE",
                $"{allModelFixture.Id}@Sgb2",
                $"{allModelFixture.Id}@AgbA"
            },
            byFixtureId[allModelFixture.Id].Select(execution => execution.ExecutionId));
        Assert.All(byFixtureId[allModelFixture.Id], execution => Assert.Null(execution.SkipReason));
    }

    /// <summary>
    /// Keeps unresolved SameSuite model pairs queryable without generating executions or xUnit skips.
    /// </summary>
    [Fact]
    public void SameSuiteUnresolvedDecisionsRemainOutsideExecutionMatrix()
    {
        var fixtures = RomManifest.Load().Tests;
        var decisions = RomApplicability.GetUnresolvedDecisions(fixtures);
        var executionIds = RomManifest.CreateExecutionCases(fixtures)
            .Select(execution => execution.ExecutionId)
            .ToHashSet(StringComparer.Ordinal);
        var sgbDecisionIds = decisions
            .Where(decision => decision.EvidenceKey == "samesuite-sgb2-expected-arrays-unresolved")
            .Select(decision => decision.DecisionKey)
            .ToArray();
        var agbDecisions = decisions
            .Where(decision => decision.EvidenceKey == "samesuite-agb-applicability-unresolved")
            .ToArray();

        AssertNoDuplicates("SameSuite unresolved decisions", decisions.Select(decision => decision.DecisionKey));
        Assert.Equal(new[]
        {
            "samesuite/sgb/command_mlt_req@Sgb2",
            "samesuite/sgb/command_mlt_req_1_incrementing@Sgb2"
        }, sgbDecisionIds);
        Assert.Equal(67, agbDecisions.Length);
        Assert.All(decisions, decision =>
        {
            Assert.False(string.IsNullOrWhiteSpace(decision.EvidenceKey));
            Assert.False(string.IsNullOrWhiteSpace(decision.Reason));
            Assert.DoesNotContain(decision.DecisionKey, executionIds);
        });
        Assert.DoesNotContain(agbDecisions, decision =>
            decision.FixtureId == "samesuite/apu/channel_1/channel_1_freq_change_timing-A");
        Assert.DoesNotContain(agbDecisions, decision =>
            decision.FixtureId == "samesuite/interrupt/ei_delay_halt");
    }

    /// <summary>
    /// Requires every active framebuffer assertion to name a reviewed oracle revision compatible with its execution model.
    /// </summary>
    [Fact]
    public void ActiveFramebufferExecutionsUseCompatibleReviewedOracles()
    {
        var executions = RomManifest.CreateExecutionCases(RomManifest.Load().Tests)
            .Where(execution => execution.Fixture.Protocol == RomProtocol.Framebuffer)
            .ToArray();

        Assert.Equal(2, executions.Length);
        Assert.All(executions, execution =>
        {
            Assert.NotEqual(OracleHardwareRevision.Unreviewed, execution.OracleRevision);
            Assert.True(
                execution.OracleRevision == OracleHardwareRevision.DmgB && execution.HardwareModel == HardwareModel.DmgB ||
                execution.OracleRevision == OracleHardwareRevision.CgbE && execution.HardwareModel == HardwareModel.CgbE,
                $"Framebuffer execution {execution.ExecutionId} uses {execution.OracleRevision} evidence on {execution.HardwareModel}.");
        });
    }

    /// <summary>
    /// Keeps CPU CGB-C framebuffer evidence separate from canonical CPU CGB-E execution rows.
    /// </summary>
    [Fact]
    public void MealybugFramebufferOraclesDoNotGateCgbEExecutions()
    {
        var fixtures = RomManifest.Load().Tests;
        var decisions = RomApplicability.GetOracleDecisions(fixtures);
        var executions = RomManifest.CreateExecutionCases(fixtures)
            .Select(execution => execution.ExecutionId)
            .ToHashSet(StringComparer.Ordinal);
        var mealybugPpuFixtures = fixtures
            .Where(fixture => fixture.Id.StartsWith("mealybug/roms/ppu/", StringComparison.Ordinal))
            .ToArray();
        var imageFixtures = mealybugPpuFixtures
            .Where(fixture => fixture.ReferenceImage != null)
            .ToArray();
        var knownInapplicable = decisions
            .Where(decision => decision.Disposition == OracleApplicabilityDisposition.KnownInapplicable)
            .Select(decision => decision.FixtureId)
            .ToArray();
        var unresolved = decisions
            .Where(decision => decision.Disposition == OracleApplicabilityDisposition.Unresolved)
            .ToArray();

        Assert.Equal(32, mealybugPpuFixtures.Length);
        Assert.Equal(31, imageFixtures.Length);
        Assert.Equal(31, decisions.Count);
        Assert.Equal(new[]
        {
            "mealybug/roms/ppu/m3_scy_change",
            "mealybug/roms/ppu/m3_scy_change2"
        }, knownInapplicable);
        Assert.Equal(29, unresolved.Length);
        Assert.All(unresolved, decision => Assert.EndsWith("@CgbE", decision.DecisionKey, StringComparison.Ordinal));
        Assert.All(decisions, decision =>
        {
            Assert.Equal(HardwareModel.CgbE, decision.HardwareModel);
            Assert.Equal(OracleHardwareRevision.CgbC, decision.OracleRevision);
            Assert.False(string.IsNullOrWhiteSpace(decision.EvidenceKey));
            Assert.False(string.IsNullOrWhiteSpace(decision.Reason));
            Assert.DoesNotContain(decision.FixtureId, executions);
        });
        Assert.Contains("mealybug/roms/ppu/win_without_bg", executions);
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
