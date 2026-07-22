using GBZEmuLibrary;

namespace GBZEmuTests;

/// <summary>
/// Verifies the color-family KEY0 and OPRI boot-time latch boundary.
/// </summary>
public sealed class CompatibilityModeRegisterTests
{
    [Theory]
    [InlineData(HardwareModel.DmgB)]
    [InlineData(HardwareModel.Mgb)]
    [InlineData(HardwareModel.Sgb2)]
    public void MonochromeHardwareExposesOpenBusValues(HardwareModel hardwareModel)
    {
        var registers = new CompatibilityModeRegisters();
        registers.Init(hardwareModel, useDmgObjectPriority: false);

        registers.WriteByte(0x04, MemorySchema.CPU_MODE_SELECT_REGISTER);
        registers.WriteByte(0x01, MemorySchema.OBJECT_PRIORITY_REGISTER);

        Assert.Equal(0xFF, registers.ReadByte(MemorySchema.CPU_MODE_SELECT_REGISTER));
        Assert.Equal(0xFF, registers.ReadByte(MemorySchema.OBJECT_PRIORITY_REGISTER));
        Assert.False(registers.UsesDmgObjectPriority);
    }

    [Theory]
    [InlineData(HardwareModel.CgbE)]
    [InlineData(HardwareModel.AgbA)]
    public void ColorFamilyBootWritesAreMaskedAndLockEffectivePriority(HardwareModel hardwareModel)
    {
        var registers = new CompatibilityModeRegisters();
        registers.Init(hardwareModel, useDmgObjectPriority: false);

        registers.WriteByte(0x04, MemorySchema.CPU_MODE_SELECT_REGISTER);
        registers.WriteByte(0x03, MemorySchema.OBJECT_PRIORITY_REGISTER);

        Assert.Equal(0x04, registers.ReadByte(MemorySchema.CPU_MODE_SELECT_REGISTER));
        Assert.Equal(0xFF, registers.ReadByte(MemorySchema.OBJECT_PRIORITY_REGISTER));
        Assert.True(registers.UsesDmgObjectPriority);

        registers.Lock();
        registers.WriteByte(0x80, MemorySchema.CPU_MODE_SELECT_REGISTER);
        registers.WriteByte(0x00, MemorySchema.OBJECT_PRIORITY_REGISTER);

        Assert.Equal(0x04, registers.ReadByte(MemorySchema.CPU_MODE_SELECT_REGISTER));
        Assert.Equal(0xFE, registers.ReadByte(MemorySchema.OBJECT_PRIORITY_REGISTER));
        Assert.True(registers.UsesDmgObjectPriority);
    }
}
