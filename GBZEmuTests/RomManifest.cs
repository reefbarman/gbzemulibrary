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
                    if (stream.ReadByte() == 0xC0)
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

        if (relativePath.StartsWith("blargg/", StringComparison.Ordinal))
        {
            test.Protocol = relativePath.StartsWith("blargg/dmg_sound/", StringComparison.Ordinal) ||
                            relativePath.StartsWith("blargg/cgb_sound/", StringComparison.Ordinal) ||
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
}

internal sealed class RomTestCase
{
    public string Id { get; set; } = string.Empty;
    public string Rom { get; set; } = string.Empty;
    public RomProtocol Protocol { get; set; }
    public HardwareMode Hardware { get; set; }
    public int MaxFrames { get; set; } = 3600;
    public string? ReferenceImage { get; set; }

    public string RomPath => FixturePath(Rom);
    public string? ReferenceImagePath => ReferenceImage == null ? null : FixturePath(ReferenceImage);

    public BootMode BootMode => Hardware == HardwareMode.Cgb
        ? BootMode.GBC | BootMode.Skip
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
    Cgb
}
