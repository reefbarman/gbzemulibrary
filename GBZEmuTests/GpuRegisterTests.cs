using GBZEmuLibrary;

namespace GBZEmuTests;

/// <summary>
/// Verifies PPU register masks and interrupt-edge behavior independently of scanline rendering.
/// </summary>
public sealed class GpuRegisterTests
{
    /// <summary>
    /// Verifies that STAT preserves read-only status bits, accepts interrupt-enable bits, and reads unused bit 7 high.
    /// </summary>
    [Fact]
    public void LcdStatusRegisterAppliesHardwareReadAndWriteMasks()
    {
        var gpu = new GPU(new MessageBus());
        gpu.Reset(false);

        gpu.WriteByte(0x00, 0xFF41);
        Assert.Equal(0x85, gpu.ReadByte(0xFF41));

        gpu.WriteByte(0xFF, 0xFF41);
        Assert.Equal(0xFD, gpu.ReadByte(0xFF41));
    }

    /// <summary>
    /// Verifies that a persistent LY=LYC condition requests one LCD interrupt after the comparison clock starts,
    /// then does not retrigger while the condition remains high.
    /// </summary>
    [Fact]
    public void LycCoincidenceInterruptRequiresANewRisingEdge()
    {
        var (gpu, getInterruptRequests) = CreateInterruptCountingGpu();

        gpu.Update(4);
        gpu.WriteByte(0x40, 0xFF41);
        Assert.Equal(0, getInterruptRequests());

        gpu.WriteByte(0x80, 0xFF40);
        Assert.Equal(0, getInterruptRequests());

        gpu.WriteByte(0x01, 0xFF45);
        gpu.WriteByte(0x00, 0xFF45);
        Assert.Equal(1, getInterruptRequests());
        Assert.NotEqual(0, gpu.ReadByte(0xFF41) & 0x04);

        gpu.WriteByte(0x00, 0xFF40);
        gpu.Update(4);
        gpu.Update(4);
        gpu.WriteByte(0x80, 0xFF40);

        Assert.Equal(1, getInterruptRequests());

        gpu.WriteByte(0x00, 0xFF41);
        gpu.WriteByte(0x40, 0xFF41);

        Assert.Equal(2, getInterruptRequests());
    }

    /// <summary>
    /// Verifies that LCD-off retains the last coincidence result, ignores LYC changes while the comparison clock
    /// is stopped, and recomputes the result as soon as the LCD is enabled again.
    /// </summary>
    [Fact]
    public void LycComparisonPausesWhileLcdIsDisabled()
    {
        var (gpu, getInterruptRequests) = CreateInterruptCountingGpu();

        gpu.Update(4);
        gpu.WriteByte(0x40, 0xFF41);
        Assert.Equal(0, getInterruptRequests());
        gpu.WriteByte(0x00, 0xFF41);
        gpu.WriteByte(0x01, 0xFF45);

        Assert.NotEqual(0, gpu.ReadByte(0xFF41) & 0x04);

        gpu.WriteByte(0x80, 0xFF40);
        Assert.Equal(0, gpu.ReadByte(0xFF41) & 0x04);

        gpu.WriteByte(0x40, 0xFF41);
        gpu.WriteByte(0x00, 0xFF40);
        gpu.WriteByte(0x00, 0xFF45);

        Assert.Equal(0, gpu.ReadByte(0xFF41) & 0x04);

        gpu.WriteByte(0x80, 0xFF40);

        Assert.NotEqual(0, gpu.ReadByte(0xFF41) & 0x04);
        Assert.Equal(1, getInterruptRequests());
    }

    /// <summary>
    /// Verifies that enabling another active STAT source does not request a second interrupt while the shared
    /// source line is already held high by LY=LYC.
    /// </summary>
    [Fact]
    public void ActiveStatSourceBlocksAnotherEnabledSource()
    {
        var (gpu, getInterruptRequests) = CreateInterruptCountingGpu();

        gpu.WriteByte(0x80, 0xFF40);
        gpu.WriteByte(0x40, 0xFF41);
        Assert.Equal(1, getInterruptRequests());

        gpu.WriteByte(0x48, 0xFF41);
        Assert.Equal(1, getInterruptRequests());

        gpu.WriteByte(0x08, 0xFF41);
        gpu.WriteByte(0x48, 0xFF41);
        Assert.Equal(2, getInterruptRequests());
    }

    /// <summary>
    /// Verifies that consecutive enabled mode sources share one uninterrupted STAT signal across a scanline
    /// boundary, then generate a new interrupt only after mode 3 lowers the line.
    /// </summary>
    [Fact]
    public void ConsecutiveModeSourcesShareOneInterruptLine()
    {
        var (gpu, getInterruptRequests) = CreateInterruptCountingGpu();

        gpu.Update(4);
        Assert.Equal(0, gpu.ReadByte(0xFF41) & 0x03);
        gpu.WriteByte(0x28, 0xFF41);
        gpu.WriteByte(0x80, 0xFF40);
        gpu.Update(204);

        Assert.Equal(1, getInterruptRequests());
        Assert.Equal(2, gpu.ReadByte(0xFF41) & 0x03);

        gpu.Update(80);
        gpu.Update(172);

        Assert.Equal(2, getInterruptRequests());
        Assert.Equal(0, gpu.ReadByte(0xFF41) & 0x03);
    }

    /// <summary>
    /// Verifies the DMG-only mode-2 STAT source pulse when line 144 enters VBlank without exposing mode 2 in STAT.
    /// </summary>
    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public void VBlankStartPulsesMode2SourceOnlyOnDmg(bool gbcMode, int expectedInterruptRequests)
    {
        var (gpu, getInterruptRequests) = CreateInterruptCountingGpu(gbcMode);

        gpu.Update(4);
        gpu.WriteByte(0x80, 0xFF40);
        for (var line = 1; line <= 143; line++)
        {
            gpu.Update(204);
            gpu.Update(80);
            gpu.Update(172);
        }

        gpu.WriteByte(0x20, 0xFF41);
        gpu.Update(204);

        Assert.Equal(144, gpu.ReadByte(0xFF44));
        Assert.Equal(1, gpu.ReadByte(0xFF41) & 0x03);
        Assert.Equal(0, getInterruptRequests());

        gpu.Update(4);

        Assert.Equal(expectedInterruptRequests, getInterruptRequests());
    }

    /// <summary>
    /// Verifies that CPU OAM reads are blocked during modes 2 and 3, then become visible again in HBlank.
    /// </summary>
    [Fact]
    public void OamReadsAreBlockedOnlyWhilePpuUsesOam()
    {
        var gpu = new GPU(new MessageBus());
        gpu.Reset(false);
        gpu.WriteByte(0x5A, 0xFE00);

        Assert.Equal(0x5A, gpu.ReadByte(0xFE00));

        gpu.Update(4);
        gpu.WriteByte(0x80, 0xFF40);
        gpu.Update(204);

        Assert.Equal(2, gpu.ReadByte(0xFF41) & 0x03);
        Assert.Equal(0xFF, gpu.ReadByte(0xFE00));

        gpu.Update(80);

        Assert.Equal(3, gpu.ReadByte(0xFF41) & 0x03);
        Assert.Equal(0xFF, gpu.ReadByte(0xFE00));

        gpu.Update(172);

        Assert.Equal(0, gpu.ReadByte(0xFF41) & 0x03);
        Assert.Equal(0x5A, gpu.ReadByte(0xFE00));
    }

    private static (GPU Gpu, Func<int> GetInterruptRequests) CreateInterruptCountingGpu(bool gbcMode = false)
    {
        var messageBus = new MessageBus();
        var gpu = new GPU(messageBus);
        var interruptRequests = 0;
        messageBus.OnRequestInterrupt = interrupt =>
        {
            if (interrupt == Interrupts.LCD)
            {
                interruptRequests++;
            }
        };

        gpu.Reset(gbcMode);
        return (gpu, () => interruptRequests);
    }
}
