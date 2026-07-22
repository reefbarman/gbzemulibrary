using GBZEmuLibrary;
using EmulatedHardwareModel = GBZEmuLibrary.HardwareModel;

namespace GBZEmuTests;

/// <summary>
/// Owns reviewed fixture applicability, concrete-model execution expansion, and visible exclusion reasons.
/// </summary>
internal static class RomApplicability
{
    private static readonly HashSet<string> MgbOnlyExecutionIds = new(StringComparer.Ordinal)
    {
        "mooneye/acceptance/boot_regs-mgb"
    };

    private static readonly HashSet<string> AgbOnlyExecutionIds = new(StringComparer.Ordinal)
    {
        "samesuite/apu/channel_1/channel_1_freq_change_timing-A"
    };

    private static readonly HashSet<string> DmgAndMgbExecutionIds = new(StringComparer.Ordinal)
    {
        "mooneye/acceptance/boot_div-dmgABCmgb",
        "mooneye/acceptance/boot_hwio-dmgABCmgb",
        "mooneye/acceptance/serial/boot_sclk_align-dmgABCmgb"
    };

    private static readonly HashSet<string> SameSuitePreCgbExecutionIds = new(StringComparer.Ordinal)
    {
        "samesuite/apu/div_write_trigger",
        "samesuite/apu/div_write_trigger_10"
    };

    private const string SameSuiteAllModelExecutionId = "samesuite/interrupt/ei_delay_halt";

    private static readonly HashSet<string> SameSuiteSgbFixtureIds = new(StringComparer.Ordinal)
    {
        "samesuite/sgb/command_mlt_req",
        "samesuite/sgb/command_mlt_req_1_incrementing"
    };

    private static readonly HashSet<string> MooneyeSgbGroupExecutionIds = new(StringComparer.Ordinal)
    {
        "mooneye/acceptance/boot_div-S",
        "mooneye/acceptance/boot_div2-S",
        "mooneye/acceptance/boot_hwio-S"
    };

    private static readonly HashSet<string> MooneyeGsExecutionIds = new(StringComparer.Ordinal)
    {
        "mooneye/acceptance/bits/unused_hwio-GS",
        "mooneye/acceptance/di_timing-GS",
        "mooneye/acceptance/halt_ime1_timing2-GS",
        "mooneye/acceptance/oam_dma/sources-GS",
        "mooneye/acceptance/ppu/hblank_ly_scx_timing-GS",
        "mooneye/acceptance/ppu/intr_1_2_timing-GS",
        "mooneye/acceptance/ppu/lcdon_timing-GS",
        "mooneye/acceptance/ppu/lcdon_write_timing-GS",
        "mooneye/acceptance/ppu/vblank_stat_intr-GS"
    };

    private static readonly HashSet<string> MooneyeModelSpecificExecutionIds = new(StringComparer.Ordinal)
    {
        "mooneye/acceptance/boot_div-dmg0",
        "mooneye/acceptance/boot_div-dmgABCmgb",
        "mooneye/acceptance/boot_hwio-dmg0",
        "mooneye/acceptance/boot_hwio-dmgABCmgb",
        "mooneye/acceptance/boot_regs-dmg0",
        "mooneye/acceptance/boot_regs-dmgABC",
        "mooneye/acceptance/boot_regs-mgb",
        "mooneye/acceptance/boot_regs-sgb2",
        "mooneye/acceptance/serial/boot_sclk_align-dmgABCmgb"
    };

    private static readonly EmulatedHardwareModel[] CanonicalModels =
    {
        EmulatedHardwareModel.DmgB,
        EmulatedHardwareModel.Mgb,
        EmulatedHardwareModel.CgbE,
        EmulatedHardwareModel.Sgb2,
        EmulatedHardwareModel.AgbA
    };

    private static readonly HashSet<string> KnownInapplicableCgbCOracleIds = new(StringComparer.Ordinal)
    {
        "mealybug/roms/ppu/m3_scy_change",
        "mealybug/roms/ppu/m3_scy_change2"
    };

    /// <summary>
    /// Expands physical fixtures into the reviewed concrete-model executions run by xUnit.
    /// </summary>
    internal static IReadOnlyList<RomExecutionCase> CreateExecutionCases(IEnumerable<RomTestCase> fixtures)
    {
        var executions = new List<RomExecutionCase>();
        foreach (var fixture in fixtures)
        {
            if (!MgbOnlyExecutionIds.Contains(fixture.Id) &&
                !AgbOnlyExecutionIds.Contains(fixture.Id) &&
                !MooneyeSgbGroupExecutionIds.Contains(fixture.Id) &&
                !HasUnresolvedFramebufferOracle(fixture, fixture.HardwareModel))
            {
                executions.Add(CreateExecution(fixture.Id, fixture, fixture.HardwareModel));
            }

            if (MgbOnlyExecutionIds.Contains(fixture.Id) ||
                DmgAndMgbExecutionIds.Contains(fixture.Id) ||
                MooneyeGsExecutionIds.Contains(fixture.Id))
            {
                executions.Add(CreateExecution(
                    $"{fixture.Id}@Mgb",
                    fixture,
                    EmulatedHardwareModel.Mgb));
            }

            if (AgbOnlyExecutionIds.Contains(fixture.Id))
            {
                executions.Add(CreateExecution(
                    $"{fixture.Id}@AgbA",
                    fixture,
                    EmulatedHardwareModel.AgbA));
            }

            if (MooneyeSgbGroupExecutionIds.Contains(fixture.Id) ||
                MooneyeGsExecutionIds.Contains(fixture.Id))
            {
                executions.Add(CreateExecution(
                    $"{fixture.Id}@Sgb2",
                    fixture,
                    EmulatedHardwareModel.Sgb2));
            }

            if (IsUntaggedMooneyeAcceptance(fixture.Id) || fixture.Id == SameSuiteAllModelExecutionId)
            {
                AddCanonicalModelExecutions(executions, fixture);
            }

            if (SameSuitePreCgbExecutionIds.Contains(fixture.Id))
            {
                AddExecutionForModel(executions, fixture, EmulatedHardwareModel.DmgB);
                AddExecutionForModel(executions, fixture, EmulatedHardwareModel.Mgb);
                AddExecutionForModel(executions, fixture, EmulatedHardwareModel.Sgb2);
            }
        }

        var duplicates = executions
            .GroupBy(execution => execution.ExecutionId, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        if (duplicates.Length > 0)
        {
            throw new InvalidOperationException(
                $"ROM execution cases contain duplicate IDs: {string.Join(", ", duplicates)}");
        }

        return executions.OrderBy(execution => execution.ExecutionId, StringComparer.Ordinal).ToArray();
    }

    internal static bool IsMooneyeSgbGroupFixture(string fixtureId)
    {
        return MooneyeSgbGroupExecutionIds.Contains(fixtureId);
    }

    internal static bool IsMooneyeGsFixture(string fixtureId)
    {
        return MooneyeGsExecutionIds.Contains(fixtureId);
    }

    internal static bool IsUntaggedMooneyeAcceptance(string fixtureId)
    {
        return fixtureId.StartsWith("mooneye/acceptance/", StringComparison.Ordinal) &&
               !MooneyeSgbGroupExecutionIds.Contains(fixtureId) &&
               !MooneyeGsExecutionIds.Contains(fixtureId) &&
               !MooneyeModelSpecificExecutionIds.Contains(fixtureId);
    }

    internal static bool IsSameSuitePreCgbFixture(string fixtureId)
    {
        return SameSuitePreCgbExecutionIds.Contains(fixtureId);
    }

    internal static bool IsSameSuiteAllModelFixture(string fixtureId)
    {
        return fixtureId == SameSuiteAllModelExecutionId;
    }

    /// <summary>
    /// Returns reviewed model pairs that lack sufficient evidence to become an execution or visible skip.
    /// </summary>
    internal static IReadOnlyList<RomApplicabilityDecision> GetUnresolvedDecisions(IEnumerable<RomTestCase> fixtures)
    {
        var decisions = fixtures
            .Where(IsUntaggedSameSuiteAgbCandidate)
            .Select(fixture => new RomApplicabilityDecision(
                fixture.Id,
                EmulatedHardwareModel.AgbA,
                "samesuite-agb-applicability-unresolved",
                "Pinned SameSuite evidence does not establish AGB-A applicability for this untagged CGB fixture."))
            .ToList();

        decisions.AddRange(SameSuiteSgbFixtureIds.Select(fixtureId => new RomApplicabilityDecision(
            fixtureId,
            EmulatedHardwareModel.Sgb2,
            "samesuite-sgb2-expected-arrays-unresolved",
            "The committed expected arrays target original SGB mode and are not established as SGB2 evidence.")));
        decisions.AddRange(fixtures
            .Where(fixture => fixture.Id.StartsWith("mealybug/roms/ppu/", StringComparison.Ordinal))
            .Select(fixture => new RomApplicabilityDecision(
                fixture.Id,
                EmulatedHardwareModel.AgbA,
                "mealybug-agb-ppu-applicability-unresolved",
                "Pinned Mealybug evidence does not establish AGB-A applicability for this PPU fixture.")));
        decisions.AddRange(fixtures
            .Where(fixture => fixture.Id.StartsWith("mealybug/roms/dma/", StringComparison.Ordinal))
            .Select(fixture => new RomApplicabilityDecision(
                fixture.Id,
                EmulatedHardwareModel.AgbA,
                "mealybug-agb-hdma-applicability-unresolved",
                "No reviewed pinned-source evidence currently establishes this HDMA result fixture on AGB-A.")));

        return decisions
            .OrderBy(decision => decision.DecisionKey, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Returns the reviewed target-model decisions for framebuffer oracles that cannot currently gate execution.
    /// </summary>
    internal static IReadOnlyList<RomOracleDecision> GetOracleDecisions(IEnumerable<RomTestCase> fixtures)
    {
        return fixtures
            .Where(fixture => IsMealybugCgbCFramebuffer(fixture))
            .Select(fixture => new RomOracleDecision(
                fixture.Id,
                EmulatedHardwareModel.CgbE,
                OracleHardwareRevision.CgbC,
                KnownInapplicableCgbCOracleIds.Contains(fixture.Id)
                    ? OracleApplicabilityDisposition.KnownInapplicable
                    : OracleApplicabilityDisposition.Unresolved,
                KnownInapplicableCgbCOracleIds.Contains(fixture.Id)
                    ? "mealybug-scy-cgb-d-and-later"
                    : "mealybug-no-cgb-e-framebuffer-oracle",
                KnownInapplicableCgbCOracleIds.Contains(fixture.Id)
                    ? "CPU CGB-C SCY fetch behavior differs on CPU CGB-D and later; no CPU CGB-E framebuffer oracle is committed."
                    : "No committed CPU CGB-E framebuffer oracle or evidence that the CPU CGB-C image is exact on CPU CGB-E."))
            .OrderBy(decision => decision.FixtureId, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Returns the exact hardware revision set encoded by a reviewed fixture ID.
    /// </summary>
    internal static HardwareRevisionRequirement GetRevisionRequirement(string testId)
    {
        if (!testId.StartsWith("samesuite/apu/", StringComparison.Ordinal))
        {
            return HardwareRevisionRequirement.Any;
        }

        if (testId.EndsWith("-cgb0BC", StringComparison.Ordinal))
        {
            return HardwareRevisionRequirement.Cgb0BC;
        }

        if (testId.EndsWith("-cgb0B", StringComparison.Ordinal))
        {
            return HardwareRevisionRequirement.Cgb0B;
        }

        if (testId.EndsWith("-cgbDE", StringComparison.Ordinal))
        {
            return HardwareRevisionRequirement.CgbDE;
        }

        if (testId.EndsWith("freq_change_timing-A", StringComparison.Ordinal))
        {
            return HardwareRevisionRequirement.AgbA;
        }

        if (testId.EndsWith("-cgb0", StringComparison.Ordinal))
        {
            return HardwareRevisionRequirement.Cgb0;
        }

        if (testId.EndsWith("-cgbB", StringComparison.Ordinal))
        {
            return HardwareRevisionRequirement.CgbB;
        }

        return HardwareRevisionRequirement.Any;
    }

    /// <summary>
    /// Returns a visible reason only for a deliberately unsupported revision or startup circumstance.
    /// </summary>
    internal static string? GetSkipReason(RomTestCase fixture, EmulatedHardwareModel executionModel)
    {
        return GetSkipReason(new RomExecutionCase(fixture.Id, fixture, executionModel));
    }

    internal static string? GetSkipReason(RomExecutionCase execution)
    {
        if (execution.Fixture.Id.EndsWith("-dmg0", StringComparison.Ordinal))
        {
            return "Requires DMG-CPU-0 startup state; GBZEmu targets DMG-B.";
        }

        if (execution.StartupCircumstance == ConformanceStartupCircumstance.MatchingFirmwareRequired)
        {
            return $"Requires matching {execution.HardwareModel} firmware post-boot state; the committed conformance runner uses synthetic skip boot.";
        }

        if (execution.Fixture.Id == "mooneye/acceptance/boot_hwio-dmgABCmgb")
        {
            return "Requires the official DMG ABC/MGB firmware PPU handoff phase; GBZEmu's replacement firmware and synthetic skip-boot profile do not reproduce that phase.";
        }

        return execution.HardwareModel == EmulatedHardwareModel.CgbE &&
               !execution.Fixture.RevisionRequirement.SupportsCgbE()
            ? $"Requires {execution.Fixture.RevisionRequirement.DisplayName()}; GBZEmu targets CPU CGB-E."
            : null;
    }

    private static bool HasUnresolvedFramebufferOracle(
        RomTestCase fixture,
        EmulatedHardwareModel hardwareModel)
    {
        return hardwareModel == EmulatedHardwareModel.CgbE && IsMealybugCgbCFramebuffer(fixture);
    }

    private static bool IsUntaggedSameSuiteAgbCandidate(RomTestCase fixture)
    {
        if (fixture.RevisionRequirement != HardwareRevisionRequirement.Any)
        {
            return false;
        }

        return fixture.Id.StartsWith("samesuite/apu/", StringComparison.Ordinal) ||
               fixture.Id.StartsWith("samesuite/dma/", StringComparison.Ordinal) ||
               fixture.Id == "samesuite/ppu/blocking_bgpi_increase";
    }

    private static bool IsMealybugCgbCFramebuffer(RomTestCase fixture)
    {
        return fixture.Id.StartsWith("mealybug/roms/ppu/", StringComparison.Ordinal) &&
               fixture.ReferenceImage != null;
    }

    private static void AddCanonicalModelExecutions(
        ICollection<RomExecutionCase> executions,
        RomTestCase fixture)
    {
        foreach (var hardwareModel in CanonicalModels)
        {
            AddExecutionForModel(executions, fixture, hardwareModel);
        }
    }

    private static void AddExecutionForModel(
        ICollection<RomExecutionCase> executions,
        RomTestCase fixture,
        EmulatedHardwareModel hardwareModel)
    {
        if (hardwareModel == fixture.HardwareModel)
        {
            return;
        }

        executions.Add(CreateExecution(
            $"{fixture.Id}@{hardwareModel}",
            fixture,
            hardwareModel));
    }

    private static RomExecutionCase CreateExecution(
        string executionId,
        RomTestCase fixture,
        EmulatedHardwareModel hardwareModel)
    {
        return new RomExecutionCase(
            executionId,
            fixture,
            hardwareModel,
            MooneyeSgbGroupExecutionIds.Contains(fixture.Id)
                ? ConformanceStartupCircumstance.MatchingFirmwareRequired
                : ConformanceStartupCircumstance.SyntheticSkipBoot,
            GetOracleRevision(fixture));
    }

    private static OracleHardwareRevision GetOracleRevision(RomTestCase fixture)
    {
        return fixture.Id switch
        {
            "acid2/dmg/dmg-acid2" => OracleHardwareRevision.DmgB,
            "acid2/cgb/cgb-acid2" => OracleHardwareRevision.CgbE,
            _ when fixture.ReferenceImage == null => OracleHardwareRevision.RevisionIndependent,
            _ => OracleHardwareRevision.Unreviewed
        };
    }
}

/// <summary>
/// Records a reviewed model pair that remains outside the concrete execution matrix.
/// </summary>
internal sealed class RomApplicabilityDecision
{
    public RomApplicabilityDecision(
        string fixtureId,
        EmulatedHardwareModel hardwareModel,
        string evidenceKey,
        string reason)
    {
        FixtureId = fixtureId;
        HardwareModel = hardwareModel;
        EvidenceKey = evidenceKey;
        Reason = reason;
    }

    public string FixtureId { get; }
    public EmulatedHardwareModel HardwareModel { get; }
    public string EvidenceKey { get; }
    public string Reason { get; }
    public string DecisionKey => $"{FixtureId}@{HardwareModel}";
}

/// <summary>
/// Records why a hardware-revision-specific framebuffer oracle does not produce a target-model execution.
/// </summary>
internal sealed class RomOracleDecision
{
    public RomOracleDecision(
        string fixtureId,
        EmulatedHardwareModel hardwareModel,
        OracleHardwareRevision oracleRevision,
        OracleApplicabilityDisposition disposition,
        string evidenceKey,
        string reason)
    {
        FixtureId = fixtureId;
        HardwareModel = hardwareModel;
        OracleRevision = oracleRevision;
        Disposition = disposition;
        EvidenceKey = evidenceKey;
        Reason = reason;
    }

    public string FixtureId { get; }
    public EmulatedHardwareModel HardwareModel { get; }
    public OracleHardwareRevision OracleRevision { get; }
    public OracleApplicabilityDisposition Disposition { get; }
    public string EvidenceKey { get; }
    public string Reason { get; }
    public string DecisionKey => $"{FixtureId}@{HardwareModel}";
}

internal enum OracleApplicabilityDisposition
{
    KnownInapplicable,
    Unresolved
}

/// <summary>
/// Describes the startup contract exercised by a conformance execution.
/// </summary>
internal enum ConformanceStartupCircumstance
{
    SyntheticSkipBoot,
    MatchingFirmwareRequired
}

/// <summary>
/// Identifies the hardware revision that produced a conformance oracle.
/// </summary>
internal enum OracleHardwareRevision
{
    Unreviewed,
    RevisionIndependent,
    DmgB,
    Mgb,
    CgbC,
    CgbE,
    Sgb2,
    AgbA
}

internal enum HardwareRevisionRequirement
{
    Any,
    Cgb0,
    CgbB,
    Cgb0B,
    Cgb0BC,
    CgbDE,
    AgbA
}

internal static class HardwareRevisionRequirementExtensions
{
    public static bool SupportsCgbE(this HardwareRevisionRequirement requirement)
    {
        return requirement == HardwareRevisionRequirement.Any ||
               requirement == HardwareRevisionRequirement.CgbDE;
    }

    public static string DisplayName(this HardwareRevisionRequirement requirement)
    {
        return requirement switch
        {
            HardwareRevisionRequirement.Cgb0 => "CPU CGB-0",
            HardwareRevisionRequirement.CgbB => "CPU CGB-B",
            HardwareRevisionRequirement.Cgb0B => "CPU CGB-0/B",
            HardwareRevisionRequirement.Cgb0BC => "CPU CGB-0/B/C",
            HardwareRevisionRequirement.CgbDE => "CPU CGB-D/E",
            HardwareRevisionRequirement.AgbA => "CPU AGB-A",
            _ => "any supported revision"
        };
    }
}
