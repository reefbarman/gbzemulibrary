using System.Text.Json;

namespace GBZEmuTests;

internal sealed class KnownFailureRegistry
{
    private static readonly object BaselineLock = new();

    public List<KnownFailure> Failures { get; set; } = new();

    public static KnownFailureRegistry Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "KnownFailures.json");
        return JsonSerializer.Deserialize<KnownFailureRegistry>(File.ReadAllText(path), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? new KnownFailureRegistry();
    }

    public static bool IsBaselineUpdateEnabled =>
        string.Equals(Environment.GetEnvironmentVariable("GBZEMU_UPDATE_BASELINE"), "1", StringComparison.Ordinal);

    public static void RecordBaseline(string id, string? failure)
    {
        var path = Environment.GetEnvironmentVariable("GBZEMU_BASELINE_PATH");
        if (string.IsNullOrEmpty(path))
        {
            throw new InvalidOperationException("GBZEMU_BASELINE_PATH must be set while updating the baseline.");
        }

        lock (BaselineLock)
        {
            KnownFailureRegistry registry;
            if (File.Exists(path))
            {
                registry = JsonSerializer.Deserialize<KnownFailureRegistry>(File.ReadAllText(path), SerializerOptions())
                           ?? new KnownFailureRegistry();
            }
            else
            {
                registry = new KnownFailureRegistry();
            }

            registry.Failures.RemoveAll(entry => entry.Id == id);
            if (failure != null)
            {
                registry.Failures.Add(new KnownFailure
                {
                    Id = id,
                    FailureSignature = NormalizeSignature(failure),
                    RootCause = ClassifyRootCause(id)
                });
            }

            registry.Failures = registry.Failures.OrderBy(entry => entry.Id, StringComparer.Ordinal).ToList();
            File.WriteAllText(path, JsonSerializer.Serialize(registry, SerializerOptions()));
        }
    }

    private static string NormalizeSignature(string failure)
    {
        if (failure.StartsWith("Failed to load test ROM:", StringComparison.Ordinal))
        {
            return "Failed to load test ROM";
        }

        if (failure.StartsWith("Framebuffer mismatch:", StringComparison.Ordinal))
        {
            return "Framebuffer mismatch";
        }

        var firstLine = failure.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrEmpty(firstLine))
        {
            return failure;
        }

        return firstLine.Length <= 160 ? firstLine : firstLine.Substring(0, 160);
    }

    private static string ClassifyRootCause(string id)
    {
        if (id.StartsWith("acid2/", StringComparison.Ordinal) ||
            id.StartsWith("mealybug/roms/ppu/", StringComparison.Ordinal))
        {
            return "The scanline renderer does not model mid-scanline PPU register changes and pixel-pipeline timing required by this framebuffer oracle.";
        }

        if (id.StartsWith("mealybug/roms/dma/", StringComparison.Ordinal) ||
            id.Contains("/oam_dma", StringComparison.Ordinal))
        {
            return "DMA bus blocking, restart, and transfer timing are not modeled at the hardware cycle granularity required by this test.";
        }

        if (id.StartsWith("mealybug/roms/mbc/", StringComparison.Ordinal))
        {
            return "MBC3 RTC registers, latching, and persistence are not implemented.";
        }

        if (id.StartsWith("samesuite/sgb/", StringComparison.Ordinal))
        {
            return "Super Game Boy command transport and multiplayer controller behavior are not implemented.";
        }

        if (id.StartsWith("samesuite/apu/", StringComparison.Ordinal) ||
            id.StartsWith("blargg/dmg_sound/", StringComparison.Ordinal) ||
            id.StartsWith("blargg/cgb_sound/", StringComparison.Ordinal))
        {
            return "APU register, power, frame-sequencer, wave RAM, or channel edge behavior is not cycle/revision accurate for this case.";
        }

        if (id.StartsWith("samesuite/dma/", StringComparison.Ordinal))
        {
            return "CGB GDMA/HDMA register masking, transfer, or LCD-mode timing is incomplete.";
        }

        if (id.StartsWith("samesuite/ppu/", StringComparison.Ordinal) ||
            id.Contains("/acceptance/ppu/", StringComparison.Ordinal))
        {
            return "PPU mode timing and CGB register access blocking are modeled per scanline rather than at the dot granularity required by this test.";
        }

        if (id.StartsWith("blargg/oam_bug/", StringComparison.Ordinal))
        {
            return "DMG OAM corruption behavior and its instruction/scanline timing window are not implemented.";
        }

        if (id == "blargg/halt_bug" ||
            id.StartsWith("samesuite/interrupt/", StringComparison.Ordinal) ||
            id.Contains("/acceptance/halt_", StringComparison.Ordinal) ||
            id.Contains("/acceptance/ei_", StringComparison.Ordinal) ||
            id.Contains("/acceptance/di_", StringComparison.Ordinal) ||
            id.Contains("/acceptance/intr_", StringComparison.Ordinal) ||
            id.Contains("/acceptance/reti_", StringComparison.Ordinal) ||
            id.Contains("/acceptance/rapid_di_ei", StringComparison.Ordinal) ||
            id.Contains("/acceptance/interrupts/", StringComparison.Ordinal))
        {
            return "HALT bug, delayed IME transitions, or interrupt-service sequencing is incomplete.";
        }

        if (id.Contains("/acceptance/timer/", StringComparison.Ordinal) ||
            id.Contains("/acceptance/div_", StringComparison.Ordinal))
        {
            return "Timer divider-edge, TIMA reload, and register-write timing is not modeled with hardware-accurate edge semantics.";
        }

        if (id.Contains("/acceptance/serial/", StringComparison.Ordinal))
        {
            return "The debug serial device completes immediately and does not model serial clock alignment or interrupt timing.";
        }

        if (id.Contains("/acceptance/boot_", StringComparison.Ordinal))
        {
            return "This hardware-revision boot-state test is run without its matching boot ROM and revision-specific initial state.";
        }

        if (id.Contains("/emulator-only/mbc1/", StringComparison.Ordinal))
        {
            return "MBC1 bank masking, multicart wiring, or external-RAM banking differs from the tested cartridge geometry.";
        }

        if (id.Contains("/emulator-only/mbc2/", StringComparison.Ordinal))
        {
            return "MBC2 internal 4-bit RAM and address-bit-gated banking behavior are incomplete.";
        }

        if (id.Contains("/emulator-only/mbc5/", StringComparison.Ordinal))
        {
            return "MBC5 banking is incomplete and the cartridge loader has a fixed 2 MiB ROM buffer for large test images.";
        }

        if (id == "blargg/interrupt_time/interrupt_time")
        {
            return "Interrupt dispatch and service timing differs from hardware at machine-cycle granularity, so the ROM never reaches its serial completion report.";
        }

        if (id.StartsWith("blargg/instr_timing/", StringComparison.Ordinal) ||
            id.StartsWith("blargg/mem_timing", StringComparison.Ordinal) ||
            id.Contains("/acceptance/", StringComparison.Ordinal))
        {
            return "CPU instruction, memory-access, or interrupt timing differs from hardware at machine-cycle granularity.";
        }

        return "The test reaches an explicit failure result; targeted subsystem diagnosis is required before changing emulator behavior.";
    }

    private static JsonSerializerOptions SerializerOptions()
    {
        return new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };
    }
}

internal sealed class KnownFailure
{
    public string Id { get; set; } = string.Empty;
    public string FailureSignature { get; set; } = string.Empty;
    public string RootCause { get; set; } = string.Empty;
}
