using GBZEmuLibrary;

namespace GBZEmuTests;

public sealed class SerialRegisterTests
{
    /// <summary>
    /// Verifies that DMG SC stores only transfer-start and clock-select while unused bits read high.
    /// </summary>
    [Fact]
    public void DmgControlRegisterAppliesHardwareMask()
    {
        var serial = new SerialRegisters();
        serial.Init(GBCMode.NoGBC);

        serial.WriteByte(0x02, MemorySchema.SERIAL_CONTROL_REGISTER);

        Assert.Equal(0x7E, serial.ReadByte(MemorySchema.SERIAL_CONTROL_REGISTER));
    }

    /// <summary>
    /// Verifies that CGB SC preserves fast-clock bit 1 while its remaining unused bits read high.
    /// </summary>
    [Fact]
    public void CgbControlRegisterPreservesFastClockSelection()
    {
        var serial = new SerialRegisters();
        serial.Init(GBCMode.GBCSupport);

        serial.WriteByte(0x00, MemorySchema.SERIAL_CONTROL_REGISTER);
        Assert.Equal(0x7C, serial.ReadByte(MemorySchema.SERIAL_CONTROL_REGISTER));

        serial.WriteByte(0x02, MemorySchema.SERIAL_CONTROL_REGISTER);
        Assert.Equal(0x7E, serial.ReadByte(MemorySchema.SERIAL_CONTROL_REGISTER));
    }
}
