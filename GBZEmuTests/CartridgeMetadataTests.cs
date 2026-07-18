using GBZEmuLibrary;

namespace GBZEmuTests;

public sealed class CartridgeMetadataTests
{
    [Theory]
    [InlineData(0x00, CartridgeCompatibility.DmgOnly)]
    [InlineData(0x80, CartridgeCompatibility.CgbCompatible)]
    [InlineData(0xC0, CartridgeCompatibility.CgbOnly)]
    [InlineData(0xFF, CartridgeCompatibility.DmgOnly)]
    public void Read_ClassifiesDefinedCgbHeaderValues(int flag, CartridgeCompatibility expected)
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

    [Theory]
    [InlineData(BootRomMetadata.DmgImageSize, true, BootRomSystem.Dmg)]
    [InlineData(BootRomMetadata.CgbImageSize, true, BootRomSystem.Cgb)]
    [InlineData(0, false, BootRomSystem.Dmg)]
    [InlineData(1024, false, BootRomSystem.Dmg)]
    public void BootRomMetadata_ClassifiesExactImageSizes(
        long length,
        bool expectedResult,
        BootRomSystem expectedSystem)
    {
        var result = BootRomMetadata.TryGetSystem(length, out var system);

        Assert.Equal(expectedResult, result);
        if (expectedResult)
        {
            Assert.Equal(expectedSystem, system);
        }
    }
}
