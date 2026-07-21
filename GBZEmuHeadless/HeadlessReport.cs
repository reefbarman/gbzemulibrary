namespace GBZEmuHeadless;

/// <summary>
/// Describes one deterministic headless emulator run and its captured video frames.
/// </summary>
public sealed class HeadlessReport
{
    public int FormatVersion { get; init; } = 2;
    public required string ROMFile { get; init; }
    public required string ROMSHA256 { get; init; }
    public int FramesExecuted { get; init; }
    public int CaptureStartFrame { get; init; }
    public int CaptureEndFrame { get; init; }
    public int CaptureEvery { get; init; }
    public required string HardwareModel { get; init; }
    public required string BootRomSource { get; init; }
    public HeadlessAudioCapture? Audio { get; init; }
    public required IReadOnlyList<HeadlessInputEventReport> InputEvents { get; init; }
    public required IReadOnlyList<HeadlessCapture> Captures { get; init; }
}

/// <summary>
/// Describes an exact, unfiltered capture of the core's reusable stereo amplitude buffer.
/// </summary>
public sealed class HeadlessAudioCapture
{
    public required string File { get; init; }
    public required string Format { get; init; }
    public int SampleRate { get; init; }
    public int SampleFrames { get; init; }
    public required string SHA256 { get; init; }
    public float MinimumAmplitude { get; init; }
    public float MaximumAmplitude { get; init; }
    public int? FirstNonZeroSampleFrame { get; init; }
    public required IReadOnlyList<int> EmulatorFrameSampleCounts { get; init; }
}

/// <summary>
/// Records a framebuffer artifact, its digest, visible color count, and machine state after one frame.
/// </summary>
public sealed class HeadlessCapture
{
    public int Frame { get; init; }
    public required string Image { get; init; }
    public required string FramebufferSHA256 { get; init; }
    public int UniqueColorCount { get; init; }
    public required IReadOnlyList<HeadlessColorCount> DominantColors { get; init; }
    public required string TopRowSHA256 { get; init; }
    public required string RightColumnSHA256 { get; init; }
    public required string CgbBackgroundPaletteSHA256 { get; init; }
    public required string CgbObjectPaletteSHA256 { get; init; }
    public required string VramBank0TileDataSHA256 { get; init; }
    public required string VramBank1TileDataSHA256 { get; init; }
    public required HeadlessCpuState CPU { get; init; }
    public required HeadlessPpuState PPU { get; init; }
}

/// <summary>
/// Records one deterministic joypad transition in a portable report form.
/// </summary>
public sealed class HeadlessInputEventReport
{
    public int Frame { get; init; }
    public required string Button { get; init; }
    public required string State { get; init; }
}

/// <summary>
/// Records one dominant RGB value and the number of framebuffer pixels using it.
/// </summary>
public sealed class HeadlessColorCount
{
    public required string RGB { get; init; }
    public int Pixels { get; init; }
}

/// <summary>
/// Serializable CPU state included with each captured frame.
/// </summary>
public sealed class HeadlessCpuState
{
    public ushort PC { get; init; }
    public ushort SP { get; init; }
    public ushort AF { get; init; }
    public ushort BC { get; init; }
    public ushort DE { get; init; }
    public ushort HL { get; init; }
    public bool InterruptsEnabled { get; init; }
    public bool Halted { get; init; }
    public bool DoubleSpeed { get; init; }
    public ulong TotalClockCycles { get; init; }
    public ulong ExecutedInstructionCount { get; init; }
}

/// <summary>
/// Serializable PPU and raster-register state included with each captured frame.
/// </summary>
public sealed class HeadlessPpuState
{
    public byte ScanLine { get; init; }
    public int Mode { get; init; }
    public int ModeClockCycles { get; init; }
    public byte LCDC { get; init; }
    public byte STAT { get; init; }
    public byte SCY { get; init; }
    public byte SCX { get; init; }
    public byte LYC { get; init; }
    public byte WY { get; init; }
    public byte WX { get; init; }
}
