using GBZEmuLibrary;

namespace GBZEmuTests;

public sealed class JoypadTests
{
    /// <summary>
    /// Verifies that P1 exposes only its writable selection bits and returns unused bits and unselected inputs high.
    /// </summary>
    [Fact]
    public void UnselectedButtonGroupsReadHigh()
    {
        var joypad = new Joypad();

        joypad.WriteByte(0xFF, MemorySchema.JOYPAD_REGISTER);

        Assert.Equal(0xFF, joypad.ReadByte(MemorySchema.JOYPAD_REGISTER));
    }

    /// <summary>
    /// Selects each P1 input group and verifies its active-low button state is mapped onto the low nibble.
    /// </summary>
    [Fact]
    public void SelectedButtonGroupReturnsActiveLowInputs()
    {
        var joypad = new Joypad();
        joypad.ButtonDown(JoypadButtons.Right);
        joypad.ButtonDown(JoypadButtons.A);

        joypad.WriteByte(0x20, MemorySchema.JOYPAD_REGISTER);
        Assert.Equal(0xEE, joypad.ReadByte(MemorySchema.JOYPAD_REGISTER));

        joypad.WriteByte(0x10, MemorySchema.JOYPAD_REGISTER);
        Assert.Equal(0xDE, joypad.ReadByte(MemorySchema.JOYPAD_REGISTER));
    }

    /// <summary>
    /// Selects both P1 groups and verifies shared input lines are low when either mapped button is pressed.
    /// </summary>
    [Fact]
    public void BothSelectedButtonGroupsCombineActiveLowInputs()
    {
        var joypad = new Joypad();
        joypad.ButtonDown(JoypadButtons.Left);
        joypad.ButtonDown(JoypadButtons.A);

        joypad.WriteByte(0x00, MemorySchema.JOYPAD_REGISTER);

        Assert.Equal(0xCC, joypad.ReadByte(MemorySchema.JOYPAD_REGISTER));
    }
}
