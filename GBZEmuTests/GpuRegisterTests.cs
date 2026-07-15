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
        var gpu = new GPU();
        gpu.Reset(false);

        gpu.WriteByte(0x00, 0xFF41);
        Assert.Equal(0x85, gpu.ReadByte(0xFF41));

        gpu.WriteByte(0xFF, 0xFF41);
        Assert.Equal(0xFD, gpu.ReadByte(0xFF41));
    }

    /// <summary>
    /// Verifies that a persistent LY=LYC condition requests one LCD interrupt on its rising edge rather than
    /// retriggering every update while the LCD is disabled and LY remains zero.
    /// </summary>
    [Fact]
    public void LycCoincidenceInterruptRequiresANewRisingEdge()
    {
        var gpu = new GPU();
        var interruptRequests = 0;
        var previousRequestInterrupt = MessageBus.Instance.OnRequestInterrupt;
        MessageBus.Instance.OnRequestInterrupt = interrupt =>
        {
            if (interrupt == Interrupts.LCD)
            {
                interruptRequests++;
            }
        };

        try
        {
            gpu.Reset(false);

            gpu.WriteByte(0x01, 0xFF45);
            gpu.WriteByte(0x40, 0xFF41);
            gpu.WriteByte(0x00, 0xFF45);

            Assert.Equal(1, interruptRequests);
            Assert.NotEqual(0, gpu.ReadByte(0xFF41) & 0x04);

            gpu.Update(4);
            gpu.Update(4);

            Assert.Equal(1, interruptRequests);

            gpu.WriteByte(0x01, 0xFF45);
            gpu.WriteByte(0x00, 0xFF45);

            Assert.Equal(2, interruptRequests);
        }
        finally
        {
            MessageBus.Instance.OnRequestInterrupt = previousRequestInterrupt;
        }
    }
}
