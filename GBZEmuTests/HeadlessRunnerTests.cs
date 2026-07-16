using System.Text;
using System.Text.Json;
using GBZEmuHeadless;
using GBZEmuLibrary;

namespace GBZEmuTests;

/// <summary>
/// Verifies deterministic headless option parsing, frame selection, image output, and reports.
/// </summary>
public sealed class HeadlessRunnerTests
{
    /// <summary>
    /// Verifies the CLI contract for capture ranges, cadence, boot mode, and deterministic input events.
    /// </summary>
    [Fact]
    public void OptionsParseCaptureAndInputSettings()
    {
        using var rom = TestRom.Create(0x00);
        var output = Path.Combine(Path.GetTempPath(), $"gbzemu-headless-options-{Guid.NewGuid():N}");

        var options = HeadlessOptions.Parse([
            rom.Path,
            "--frames", "20",
            "--capture-frames", "5-15",
            "--capture-every", "5",
            "--output", output,
            "--skip-bios",
            "--dmg",
            "--input", "3:Start:down",
            "--input", "4:Start:up"
        ]);

        Assert.Equal(20, options.Frames);
        Assert.Equal(5, options.CaptureStartFrame);
        Assert.Equal(15, options.CaptureEndFrame);
        Assert.Equal(5, options.CaptureEvery);
        Assert.True(options.SkipBootROM);
        Assert.True(options.ForceDMG);
        Assert.Equal(Path.GetFullPath(output), options.OutputDirectory);
        Assert.Equal([
            new HeadlessInputEvent(3, JoypadButtons.Start, true),
            new HeadlessInputEvent(4, JoypadButtons.Start, false)
        ], options.InputEvents);
    }

    /// <summary>
    /// Verifies that a fixed-frame run writes selected PPM images and a self-consistent JSON report.
    /// </summary>
    [Fact]
    public void RunnerWritesDeterministicCapturesAndReport()
    {
        using var rom = TestRom.Create(0x00, 0x18, 0xFD);
        var output = Path.Combine(Path.GetTempPath(), $"gbzemu-headless-run-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(output);
            var staleCapture = Path.Combine(output, "frame-999999.ppm");
            File.WriteAllText(staleCapture, "stale");

            var options = HeadlessOptions.Parse([
                rom.Path,
                "--frames", "4",
                "--capture-frames", "2-4",
                "--capture-every", "2",
                "--output", output,
                "--skip-bios",
                "--dmg"
            ]);

            var reportPath = new HeadlessRunner().Run(options);
            var report = JsonSerializer.Deserialize<HeadlessReport>(File.ReadAllText(reportPath));

            Assert.NotNull(report);
            Assert.Equal(1, report.FormatVersion);
            Assert.Equal(Path.GetFileName(rom.Path), report.ROMFile);
            Assert.Equal(64, report.ROMSHA256.Length);
            Assert.Equal(4, report.FramesExecuted);
            Assert.Equal(2, report.CaptureStartFrame);
            Assert.Equal(4, report.CaptureEndFrame);
            Assert.Equal(2, report.CaptureEvery);
            Assert.Equal("DMG, Skip, Force", report.BootMode);
            Assert.Empty(report.InputEvents);
            Assert.Equal([2, 4], report.Captures.Select(capture => capture.Frame));
            Assert.False(File.Exists(staleCapture));

            foreach (var capture in report.Captures)
            {
                Assert.Equal(64, capture.FramebufferSHA256.Length);
                Assert.InRange(capture.UniqueColorCount, 1, Display.HORIZONTAL_RESOLUTION * Display.VERTICAL_RESOLUTION);
                Assert.NotEmpty(capture.DominantColors);
                Assert.InRange(capture.DominantColors.Count, 1, Math.Min(16, capture.UniqueColorCount));
                Assert.InRange(capture.DominantColors.Sum(color => color.Pixels), 1, Display.HORIZONTAL_RESOLUTION * Display.VERTICAL_RESOLUTION);
                Assert.Matches("^#[0-9A-F]{6}$", capture.DominantColors[0].RGB);
                Assert.Equal(64, capture.TopRowSHA256.Length);
                Assert.Equal(64, capture.RightColumnSHA256.Length);
                Assert.Equal(64, capture.CgbBackgroundPaletteSHA256.Length);
                Assert.Equal(64, capture.CgbObjectPaletteSHA256.Length);
                Assert.Equal(64, capture.VramBank0TileDataSHA256.Length);
                Assert.Equal(64, capture.VramBank1TileDataSHA256.Length);
                Assert.True(capture.CPU.TotalClockCycles > 0);
                Assert.InRange(capture.PPU.Mode, 0, 3);

                var imagePath = Path.Combine(output, capture.Image);
                var image = File.ReadAllBytes(imagePath);
                var header = Encoding.ASCII.GetBytes($"P6\n{Display.HORIZONTAL_RESOLUTION} {Display.VERTICAL_RESOLUTION}\n255\n");
                Assert.True(image.AsSpan(0, header.Length).SequenceEqual(header));
                Assert.Equal(header.Length + Display.HORIZONTAL_RESOLUTION * Display.VERTICAL_RESOLUTION * 3, image.Length);
            }
        }
        finally
        {
            if (Directory.Exists(output))
            {
                Directory.Delete(output, true);
            }
        }
    }

    /// <summary>
    /// Verifies that invalid capture and input frames fail before emulation starts.
    /// </summary>
    [Theory]
    [InlineData("--capture-frames", "3-6")]
    [InlineData("--input", "6:A:down")]
    public void OptionsRejectFramesPastRunBudget(string option, string value)
    {
        using var rom = TestRom.Create(0x00);
        var exception = Assert.Throws<ArgumentException>(() => HeadlessOptions.Parse([
            rom.Path,
            "--frames", "5",
            option, value
        ]));

        Assert.Contains("frames", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
