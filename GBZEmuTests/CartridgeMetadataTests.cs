using GBZEmuLibrary;

namespace GBZEmuTests;

public sealed class CartridgeMetadataTests
{
    [Theory]
    [InlineData(0x00, CartridgeCompatibility.DmgOnly)]
    [InlineData(0x7F, CartridgeCompatibility.DmgOnly)]
    [InlineData(0x80, CartridgeCompatibility.CgbCompatible)]
    [InlineData(0x81, CartridgeCompatibility.CgbCompatible)]
    [InlineData(0xC0, CartridgeCompatibility.CgbOnly)]
    [InlineData(0xFF, CartridgeCompatibility.CgbCompatible)]
    public void Read_ClassifiesCgbHeaderBitSeven(int flag, CartridgeCompatibility expected)
    {
        var rom = new byte[0x144];
        rom[0x143] = (byte)flag;

        var metadata = CartridgeMetadata.Read(rom);

        Assert.Equal(expected, metadata.Compatibility);
    }

    [Fact]
    public void Read_PathReadsOnlyRequiredHeaderData()
    {
        var path = Path.GetTempFileName();
        try
        {
            var rom = new byte[0x144];
            rom[0x143] = 0xC0;
            File.WriteAllBytes(path, rom);

            var metadata = CartridgeMetadata.Read(path);

            Assert.Equal(CartridgeCompatibility.CgbOnly, metadata.Compatibility);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Read_RejectsShortRom()
    {
        Assert.Throws<InvalidDataException>(() => CartridgeMetadata.Read(new byte[0x143]));
    }

    [Fact]
    public void HardwareModelMetadata_ReportsImplementedModelsInStableOrder()
    {
        Assert.Equal(
            new[] { HardwareModel.DmgB, HardwareModel.Mgb, HardwareModel.CgbE, HardwareModel.Sgb2, HardwareModel.AgbA },
            HardwareModelMetadata.ImplementedModels);
        Assert.True(HardwareModelMetadata.IsImplemented(HardwareModel.DmgB));
        Assert.True(HardwareModelMetadata.IsImplemented(HardwareModel.Mgb));
        Assert.True(HardwareModelMetadata.IsImplemented(HardwareModel.AgbA));
        Assert.False(HardwareModelMetadata.IsImplemented((HardwareModel)999));
    }

    [Theory]
    [InlineData(HardwareModel.DmgB, CartridgeCompatibility.DmgOnly, true)]
    [InlineData(HardwareModel.DmgB, CartridgeCompatibility.CgbCompatible, true)]
    [InlineData(HardwareModel.DmgB, CartridgeCompatibility.CgbOnly, false)]
    [InlineData(HardwareModel.Mgb, CartridgeCompatibility.DmgOnly, true)]
    [InlineData(HardwareModel.Mgb, CartridgeCompatibility.CgbOnly, false)]
    [InlineData(HardwareModel.CgbE, CartridgeCompatibility.DmgOnly, true)]
    [InlineData(HardwareModel.CgbE, CartridgeCompatibility.CgbCompatible, true)]
    [InlineData(HardwareModel.CgbE, CartridgeCompatibility.CgbOnly, true)]
    [InlineData(HardwareModel.Sgb2, CartridgeCompatibility.DmgOnly, true)]
    [InlineData(HardwareModel.Sgb2, CartridgeCompatibility.CgbCompatible, true)]
    [InlineData(HardwareModel.Sgb2, CartridgeCompatibility.CgbOnly, false)]
    [InlineData(HardwareModel.AgbA, CartridgeCompatibility.DmgOnly, true)]
    [InlineData(HardwareModel.AgbA, CartridgeCompatibility.CgbCompatible, true)]
    [InlineData(HardwareModel.AgbA, CartridgeCompatibility.CgbOnly, true)]
    public void HardwareModelMetadata_UsesCanonicalCartridgeMatrix(
        HardwareModel model,
        CartridgeCompatibility compatibility,
        bool expected)
    {
        Assert.Equal(expected, HardwareModelMetadata.SupportsCartridge(model, compatibility));
    }
}
