using System.Text.Json;
using System.Text.Json.Serialization;
using GBZEmuLibrary;

namespace GBZEmuTests;

internal sealed class RomManifest
{
    public List<RomTestCase> Tests { get; set; } = new();

    public static RomManifest Load()
    {
        var manifest = LoadOverrides();
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
            using (var stream = File.OpenRead(path))
            {
                if (stream.Length > 0x143)
                {
                    stream.Position = 0x143;
                    if (IsCgbHeader(stream.ReadByte()))
                    {
                        test.Hardware = HardwareMode.Cgb;
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
            Hardware = HardwareMode.Dmg,
            Protocol = RomProtocol.Fibonacci
        };

        test.RevisionRequirement = GetRevisionRequirement(test.Id);

        if (relativePath.StartsWith("blargg/", StringComparison.Ordinal))
        {
            test.Protocol = relativePath.StartsWith("blargg/dmg_sound/", StringComparison.Ordinal) ||
                            relativePath.StartsWith("blargg/cgb_sound/", StringComparison.Ordinal) ||
                            relativePath.StartsWith("blargg/mem_timing-2/", StringComparison.Ordinal) ||
                            relativePath.StartsWith("blargg/oam_bug/", StringComparison.Ordinal) ||
                            relativePath == "blargg/halt_bug.gb"
                ? RomProtocol.BlarggMemory
                : RomProtocol.Serial;
            test.Hardware = relativePath.StartsWith("blargg/cgb_sound/", StringComparison.Ordinal)
                ? HardwareMode.Cgb
                : HardwareMode.Dmg;
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
            test.Hardware = HardwareMode.Cgb;
            test.MaxFrames = 60;
        }
        else if (relativePath.StartsWith("samesuite/", StringComparison.Ordinal) &&
                 (relativePath.IndexOf("cgb", StringComparison.OrdinalIgnoreCase) >= 0 ||
                  relativePath.StartsWith("samesuite/dma/", StringComparison.Ordinal) ||
                  relativePath.StartsWith("samesuite/ppu/", StringComparison.Ordinal)))
        {
            test.Hardware = HardwareMode.Cgb;
        }
        else if (relativePath.StartsWith("samesuite/sgb/", StringComparison.Ordinal))
        {
            test.Hardware = HardwareMode.Sgb;
        }
        else if (id.StartsWith("mooneye/acceptance/boot_", StringComparison.Ordinal) &&
                 id.EndsWith("-S", StringComparison.Ordinal))
        {
            test.Hardware = HardwareMode.Sgb;
        }
        else if (relativePath == "mooneye/acceptance/boot_regs-sgb.gb")
        {
            test.Hardware = HardwareMode.Sgb;
        }
        else if (relativePath == "mooneye/acceptance/boot_regs-sgb2.gb")
        {
            test.Hardware = HardwareMode.Sgb2;
        }
        else if (relativePath.StartsWith("mealybug/roms/", StringComparison.Ordinal))
        {
            test.Hardware = HardwareMode.Cgb;
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

    private static HardwareRevisionRequirement GetRevisionRequirement(string testId)
    {
        if (!testId.StartsWith("samesuite/apu/", StringComparison.Ordinal))
        {
            return HardwareRevisionRequirement.Any;
        }

        if (testId.EndsWith("-cgb0BC", StringComparison.Ordinal))
        {
            return HardwareRevisionRequirement.Cgb0ThroughC;
        }

        if (testId.EndsWith("-cgb0B", StringComparison.Ordinal))
        {
            return HardwareRevisionRequirement.Cgb0ThroughB;
        }

        if (testId.EndsWith("-cgbDE", StringComparison.Ordinal))
        {
            return HardwareRevisionRequirement.CgbDThroughE;
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
}

internal sealed class RomTestCase
{
    public string Id { get; set; } = string.Empty;
    public string Rom { get; set; } = string.Empty;
    public RomProtocol Protocol { get; set; }
    public HardwareMode Hardware { get; set; }
    public int MaxFrames { get; set; } = 3600;
    public string? ReferenceImage { get; set; }
    public HardwareRevisionRequirement RevisionRequirement { get; set; }

    public string RomPath => FixturePath(Rom);
    public string? ReferenceImagePath => ReferenceImage == null ? null : FixturePath(ReferenceImage);

    /// <summary>
    /// Returns a visible reason only for fixtures requiring a deliberately unsupported hardware revision or boot path.
    /// </summary>
    public string? SkipReason
    {
        get
        {
            if (Id.EndsWith("-dmg0", StringComparison.Ordinal))
            {
                return "Requires DMG-CPU-0 startup state; GBZEmu targets DMG-B.";
            }

            if (Id == "mooneye/acceptance/boot_regs-mgb")
            {
                return "Requires MGB startup state; GBZEmu does not model MGB hardware.";
            }

            if (Id == "mooneye/acceptance/boot_hwio-dmgABCmgb")
            {
                return "Requires the original DMG ABC/MGB boot-ROM I/O handoff phase; GBZEmu's redistributable replacement firmware and skip-boot profile do not reproduce proprietary firmware timing.";
            }

            if (Id == "mooneye/acceptance/boot_div-S" ||
                Id == "mooneye/acceptance/boot_div2-S")
            {
                return "Requires cartridge-dependent original SGB/SGB2 boot-ROM DIV phase; GBZEmu's redistributable replacement firmware and skip-boot profile do not reproduce proprietary firmware duration.";
            }

            return Hardware == HardwareMode.Cgb && !RevisionRequirement.SupportsCgbE()
                ? $"Requires {RevisionRequirement.DisplayName()}; GBZEmu targets CPU CGB-E."
                : null;
        }
    }

    public BootMode BootMode => Hardware == HardwareMode.Cgb
        ? BootMode.GBC | BootMode.Skip
        : Hardware == HardwareMode.Sgb2
            ? BootMode.SGB2 | BootMode.Skip
        : Hardware == HardwareMode.Sgb
            ? BootMode.SGB | BootMode.Skip
            : BootMode.DMG | BootMode.Force | BootMode.Skip;

    private static string FixturePath(string relativePath)
    {
        return Path.Combine(AppContext.BaseDirectory, "Fixtures", relativePath.Replace('/', Path.DirectorySeparatorChar));
    }
}

internal enum RomProtocol
{
    Serial,
    BlarggMemory,
    Fibonacci,
    Framebuffer
}

internal enum HardwareMode
{
    Dmg,
    Cgb,
    Sgb,
    Sgb2
}

internal enum HardwareRevisionRequirement
{
    Any,
    Cgb0,
    CgbB,
    Cgb0ThroughB,
    Cgb0ThroughC,
    CgbDThroughE,
    AgbA
}

internal static class HardwareRevisionRequirementExtensions
{
    public static bool SupportsCgbE(this HardwareRevisionRequirement requirement)
    {
        return requirement == HardwareRevisionRequirement.Any ||
               requirement == HardwareRevisionRequirement.CgbDThroughE;
    }

    public static string DisplayName(this HardwareRevisionRequirement requirement)
    {
        return requirement switch
        {
            HardwareRevisionRequirement.Cgb0 => "CPU CGB-0",
            HardwareRevisionRequirement.CgbB => "CPU CGB-B",
            HardwareRevisionRequirement.Cgb0ThroughB => "CPU CGB-0/B",
            HardwareRevisionRequirement.Cgb0ThroughC => "CPU CGB-0/A/B/C",
            HardwareRevisionRequirement.CgbDThroughE => "CPU CGB-D/E",
            HardwareRevisionRequirement.AgbA => "CPU AGB-A",
            _ => "any supported revision"
        };
    }
}
