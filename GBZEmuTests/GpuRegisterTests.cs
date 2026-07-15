using GBZEmuLibrary;

namespace GBZEmuTests;

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
}
