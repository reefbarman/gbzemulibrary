using GBZEmuLibrary;

namespace GBZEmuTests;

/// <summary>
/// Verifies CGB work-RAM bank selection and the SVBK register's hardware-visible bits.
/// </summary>
public sealed class WorkRamTests
{
    [Theory]
    [InlineData(0x00, 0xF8)]
    [InlineData(0x01, 0xF9)]
    [InlineData(0x05, 0xFD)]
    [InlineData(0x07, 0xFF)]
    [InlineData(0xFF, 0xFF)]
    public void CgbSvbkReadsSelectedBankWithUnusedBitsHigh(byte written, byte expected)
    {
        var workRam = new WorkRAM();
        workRam.Init(GBCMode.GBCSupport);

        workRam.WriteByte(written, MemorySchema.SWITCHABLE_WORK_RAM_REGISTER);

        Assert.Equal(expected, workRam.ReadByte(MemorySchema.SWITCHABLE_WORK_RAM_REGISTER));
    }

    [Fact]
    public void DmgSvbkReadsAsFF()
    {
        var workRam = new WorkRAM();
        workRam.Init(GBCMode.NoGBC);
        workRam.WriteByte(0x05, MemorySchema.SWITCHABLE_WORK_RAM_REGISTER);

        Assert.Equal(0xFF, workRam.ReadByte(MemorySchema.SWITCHABLE_WORK_RAM_REGISTER));
    }

    [Fact]
    public void CgbBankZeroStillSelectsBankOne()
    {
        var workRam = new WorkRAM();
        workRam.Init(GBCMode.GBCSupport);
        workRam.WriteByte(0x01, MemorySchema.SWITCHABLE_WORK_RAM_REGISTER);
        workRam.WriteByte(0xA5, MemorySchema.WORK_RAM_END);

        workRam.WriteByte(0x00, MemorySchema.SWITCHABLE_WORK_RAM_REGISTER);

        Assert.Equal(0xA5, workRam.ReadByte(MemorySchema.WORK_RAM_END));
    }
}
