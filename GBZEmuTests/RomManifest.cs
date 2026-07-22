using System.Text.Json;
using System.Text.Json.Serialization;
using GBZEmuLibrary;
using EmulatedHardwareModel = GBZEmuLibrary.HardwareModel;

namespace GBZEmuTests;

internal sealed class RomManifest
{
    public List<RomTestCase> Tests { get; set; } = new();

    public static RomManifest Load()
    {
        var manifest = LoadOverrides();
        EnsureUniqueIds(manifest.Tests, "ROM manifest overrides");
        manifest.Tests.RemoveAll(test => IsOriginalSgbOnlyId(test.Id));
        var discovered = DiscoverFixtures().ToList();
        EnsureUniqueIds(discovered, "discovered ROM fixtures");

        foreach (var test in discovered)
        {
            var configured = manifest.Tests.SingleOrDefault(entry => entry.Id == test.Id);
            if (configured == null)
            {
                manifest.Tests.Add(test);
                continue;
            }

            configured.Rom = test.Rom;
        }

        manifest.Tests = manifest.Tests.OrderBy(test => test.Id, StringComparer.Ordinal).ToList();
        return manifest;
    }

    private static RomManifest LoadOverrides()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "RomManifest.json");
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };

        return JsonSerializer.Deserialize<RomManifest>(File.ReadAllText(path), options)
               ?? throw new InvalidOperationException("ROM manifest is empty.");
    }

    /// <summary>
    /// Returns whether the cartridge header requests CGB-compatible or CGB-only execution.
    /// </summary>
    internal static bool IsCgbHeader(int cgbFlag)
    {
        return cgbFlag == 0x80 || cgbFlag == 0xC0;
    }

    internal static bool IsOriginalSgbOnlyId(string id)
    {
        return id.StartsWith("samesuite/sgb/", StringComparison.Ordinal) ||
               id == "mooneye/acceptance/boot_regs-sgb";
    }

    internal static void EnsureUniqueIds(IEnumerable<RomTestCase> tests, string source)
    {
        var duplicates = tests
            .GroupBy(test => test.Id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        if (duplicates.Length > 0)
        {
            throw new InvalidOperationException($"{source} contains duplicate normalized IDs: {string.Join(", ", duplicates)}");
        }
    }

    /// <summary>
    /// Expands reviewed physical fixtures into the bounded concrete-model executions run by xUnit.
    /// </summary>
    internal static IReadOnlyList<RomExecutionCase> CreateExecutionCases(IEnumerable<RomTestCase> fixtures)
    {
        return RomApplicability.CreateExecutionCases(fixtures);
    }

    private static IEnumerable<RomTestCase> DiscoverFixtures()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        foreach (var path in Directory.EnumerateFiles(root, "*.gb*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            if (!relativePath.EndsWith(".gb", StringComparison.OrdinalIgnoreCase) &&
                !relativePath.EndsWith(".gbc", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var test = CreateTest(relativePath);
            if (IsOriginalSgbOnlyId(test.Id))
            {
                continue;
            }

            using (var stream = File.OpenRead(path))
            {
                if (stream.Length > 0x143)
                {
                    stream.Position = 0x143;
                    if (IsCgbHeader(stream.ReadByte()))
                    {
                        test.HardwareModel = EmulatedHardwareModel.CgbE;
                    }
                }
            }

            yield return test;
        }
    }

    private static RomTestCase CreateTest(string relativePath)
    {
        var id = Path.ChangeExtension(relativePath, null)!.Replace(' ', '_');
        var test = new RomTestCase
        {
            Id = id,
            Rom = relativePath,
            MaxFrames = 3600,
            HardwareModel = EmulatedHardwareModel.DmgB,
            Protocol = RomProtocol.Fibonacci
        };

        test.RevisionRequirement = RomApplicability.GetRevisionRequirement(test.Id);

        if (relativePath.StartsWith("blargg/", StringComparison.Ordinal))
        {
            test.Protocol = relativePath.StartsWith("blargg/dmg_sound/", StringComparison.Ordinal) ||
                            relativePath.StartsWith("blargg/cgb_sound/", StringComparison.Ordinal) ||
                            relativePath.StartsWith("blargg/mem_timing-2/", StringComparison.Ordinal) ||
                            relativePath.StartsWith("blargg/oam_bug/", StringComparison.Ordinal) ||
                            relativePath == "blargg/halt_bug.gb"
                ? RomProtocol.BlarggMemory
                : RomProtocol.Serial;
            test.HardwareModel = relativePath.StartsWith("blargg/cgb_sound/", StringComparison.Ordinal)
                ? EmulatedHardwareModel.CgbE
                : EmulatedHardwareModel.DmgB;
        }
        else if (relativePath == "acid2/dmg/dmg-acid2.gb")
        {
            test.Protocol = RomProtocol.Framebuffer;
            test.ReferenceImage = "acid2/dmg/reference-dmg.png";
            test.MaxFrames = 60;
        }
        else if (relativePath == "acid2/cgb/cgb-acid2.gbc")
        {
            test.Protocol = RomProtocol.Framebuffer;
            test.ReferenceImage = "acid2/cgb/reference.png";
            test.HardwareModel = EmulatedHardwareModel.CgbE;
            test.MaxFrames = 60;
        }
        else if (relativePath.StartsWith("samesuite/", StringComparison.Ordinal) &&
                 (relativePath.IndexOf("cgb", StringComparison.OrdinalIgnoreCase) >= 0 ||
                  relativePath.StartsWith("samesuite/dma/", StringComparison.Ordinal) ||
                  relativePath.StartsWith("samesuite/ppu/", StringComparison.Ordinal)))
        {
            test.HardwareModel = EmulatedHardwareModel.CgbE;
        }
        else if (relativePath == "mooneye/acceptance/boot_regs-sgb2.gb")
        {
            test.HardwareModel = EmulatedHardwareModel.Sgb2;
        }
        else if (relativePath.StartsWith("mealybug/roms/", StringComparison.Ordinal))
        {
            test.HardwareModel = EmulatedHardwareModel.CgbE;
            // The RTC fixture contains approximately 20 emulated seconds of deliberate delay loops.
            test.MaxFrames = relativePath == "mealybug/roms/mbc/mbc3_rtc.gb" ? 1800 : 600;

            if (relativePath.StartsWith("mealybug/roms/ppu/", StringComparison.Ordinal) &&
                relativePath != "mealybug/roms/ppu/win_without_bg.gb")
            {
                test.Protocol = RomProtocol.Framebuffer;
                test.ReferenceImage = $"mealybug/expected-cgb-c/{Path.GetFileNameWithoutExtension(relativePath)}.png";
            }
        }

        return test;
    }

}

internal sealed class RomTestCase
{
    public string Id { get; set; } = string.Empty;
    public string Rom { get; set; } = string.Empty;
    public RomProtocol Protocol { get; set; }
    public EmulatedHardwareModel HardwareModel { get; set; }
    public int MaxFrames { get; set; } = 3600;
    public string? ReferenceImage { get; set; }
    public HardwareRevisionRequirement RevisionRequirement { get; set; }

    public string RomPath => FixturePath(Rom);
    public string? ReferenceImagePath => ReferenceImage == null ? null : FixturePath(ReferenceImage);

    /// <summary>
    /// Returns a visible reason only for fixtures requiring a deliberately unsupported hardware revision or boot path.
    /// </summary>
    public string? SkipReason => GetSkipReason(HardwareModel);

    internal string? GetSkipReason(EmulatedHardwareModel executionModel)
    {
        return RomApplicability.GetSkipReason(this, executionModel);
    }


    private static string FixturePath(string relativePath)
    {
        return Path.Combine(AppContext.BaseDirectory, "Fixtures", relativePath.Replace('/', Path.DirectorySeparatorChar));
    }
}

/// <summary>
/// Binds one physical ROM fixture to a stable execution ID and concrete hardware model.
/// </summary>
internal sealed class RomExecutionCase
{
    public RomExecutionCase(
        string executionId,
        RomTestCase fixture,
        EmulatedHardwareModel hardwareModel)
        : this(
            executionId,
            fixture,
            hardwareModel,
            ConformanceStartupCircumstance.SyntheticSkipBoot,
            fixture.ReferenceImage == null
                ? OracleHardwareRevision.RevisionIndependent
                : OracleHardwareRevision.Unreviewed)
    {
    }

    public RomExecutionCase(
        string executionId,
        RomTestCase fixture,
        EmulatedHardwareModel hardwareModel,
        ConformanceStartupCircumstance startupCircumstance,
        OracleHardwareRevision oracleRevision)
    {
        ExecutionId = executionId;
        Fixture = fixture;
        HardwareModel = hardwareModel;
        StartupCircumstance = startupCircumstance;
        OracleRevision = oracleRevision;
    }

    public string ExecutionId { get; }
    public RomTestCase Fixture { get; }
    public EmulatedHardwareModel HardwareModel { get; }
    public ConformanceStartupCircumstance StartupCircumstance { get; }
    public OracleHardwareRevision OracleRevision { get; }
    public string? SkipReason => RomApplicability.GetSkipReason(this);
}

internal enum RomProtocol
{
    Serial,
    BlarggMemory,
    Fibonacci,
    Framebuffer
}
