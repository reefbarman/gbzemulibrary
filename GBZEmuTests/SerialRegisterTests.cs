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
        var serial = new SerialRegisters(new MessageBus());
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
        var serial = new SerialRegisters(new MessageBus());
        serial.Init(GBCMode.GBCSupport);

        serial.WriteByte(0x00, MemorySchema.SERIAL_CONTROL_REGISTER);
        Assert.Equal(0x7C, serial.ReadByte(MemorySchema.SERIAL_CONTROL_REGISTER));

        serial.WriteByte(0x02, MemorySchema.SERIAL_CONTROL_REGISTER);
        Assert.Equal(0x7E, serial.ReadByte(MemorySchema.SERIAL_CONTROL_REGISTER));
    }

    /// <summary>
    /// Verifies that CGB slow and fast selections use divider bits 8 and 3 respectively.
    /// Raw CPU clocks make these periods naturally halve when the CPU enters double-speed mode.
    /// </summary>
    [Theory]
    [InlineData(0x81, 4096)]
    [InlineData(0x83, 128)]
    public void CgbInternalTransferUsesSelectedClockRate(byte control, int transferClocks)
    {
        var messageBus = new MessageBus();
        var serial = new SerialRegisters(messageBus);
        var interruptRequested = false;
        messageBus.OnRequestInterrupt = interrupt => interruptRequested = interrupt == Interrupts.Serial;
        serial.Init(GBCMode.GBCSupport);
        serial.Reset(usingBootROM: false);
        serial.WriteByte(control, MemorySchema.SERIAL_CONTROL_REGISTER);

        serial.Update(transferClocks - 1);
        Assert.False(interruptRequested);
        Assert.NotEqual(0, serial.ReadByte(MemorySchema.SERIAL_CONTROL_REGISTER) & 0x80);

        serial.Update(1);

        Assert.True(interruptRequested);
        Assert.Equal(0, serial.ReadByte(MemorySchema.SERIAL_CONTROL_REGISTER) & 0x80);
    }

    /// <summary>
    /// Verifies that DMG internal transfers follow falling edges of divider bit 8, shift disconnected input high,
    /// clear SC bit 7, publish the outgoing byte, and request the serial interrupt after eight bits.
    /// </summary>
    [Fact]
    public void DmgInternalTransferUsesResetAlignedDividerEdges()
    {
        var messageBus = new MessageBus();
        var serial = new SerialRegisters(messageBus);
        byte? transferred = null;
        Interrupts? interrupt = null;
        serial.ByteTransferred += value => transferred = value;
        messageBus.OnRequestInterrupt = requested => interrupt = requested;
        serial.Init(GBCMode.NoGBC);
        serial.Reset(usingBootROM: false);
        serial.WriteByte(0x80, MemorySchema.SERIAL_DATA_REGISTER);
        serial.WriteByte(0x81, MemorySchema.SERIAL_CONTROL_REGISTER);

        serial.Update(55);

        Assert.Null(transferred);
        Assert.Equal(0xFF, serial.ReadByte(MemorySchema.SERIAL_CONTROL_REGISTER));
        Assert.Equal(0x80, serial.ReadByte(MemorySchema.SERIAL_DATA_REGISTER));

        serial.Update(1);
        Assert.Equal(0x01, serial.ReadByte(MemorySchema.SERIAL_DATA_REGISTER));

        serial.Update(7 * 512 - 1);
        Assert.Null(transferred);
        Assert.Null(interrupt);

        serial.Update(1);

        Assert.Equal<byte?>((byte)0x80, transferred);
        Assert.Equal<Interrupts?>(Interrupts.Serial, interrupt);
        Assert.Equal(0x7F, serial.ReadByte(MemorySchema.SERIAL_CONTROL_REGISTER));
        Assert.Equal(0xFF, serial.ReadByte(MemorySchema.SERIAL_DATA_REGISTER));
    }
}
