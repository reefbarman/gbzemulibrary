using GBZEmuLibrary;

namespace GBZEmuTests;

/// <summary>
/// Verifies fatal cartridge geometry validation and non-fatal header diagnostics.
/// </summary>
public sealed class CartridgeInspectionTests
{
    private static readonly byte[] NintendoLogo =
    {
        0xCE, 0xED, 0x66, 0x66, 0xCC, 0x0D, 0x00, 0x0B,
        0x03, 0x73, 0x00, 0x83, 0x00, 0x0C, 0x00, 0x0D,
        0x00, 0x08, 0x11, 0x1F, 0x88, 0x89, 0x00, 0x0E,
        0xDC, 0xCC, 0x6E, 0xE6, 0xDD, 0xDD, 0xD9, 0x99,
        0xBB, 0xBB, 0x67, 0x63, 0x6E, 0x0E, 0xEC, 0xCC,
        0xDD, 0xDC, 0x99, 0x9F, 0xBB, 0xB9, 0x33, 0x3E
    };

    [Theory]
    [InlineData(0x14F)]
    [InlineData(0x7FFF)]
    [InlineData(0x8001)]
    public void InspectRejectsShortOrPartialBankImages(int length)
    {
        var error = Assert.Throws<InvalidDataException>(() => CartridgeInspection.Inspect(new byte[length]));

        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    [Fact]
    public void InspectRejectsImagesBeyondMaximumGeometry()
    {
        var bytes = CreateRomBytes(bankCount: 513, declaredSizeCode: 0x08);

        Assert.Throws<InvalidDataException>(() => CartridgeInspection.Inspect(bytes));
    }

    [Theory]
    [InlineData(0x04)]
    [InlineData(0x14)]
    [InlineData(0xFF)]
    public void InspectRejectsUnsupportedCartridgeTypes(byte cartridgeType)
    {
        var bytes = CreateRomBytes();
        bytes[0x147] = cartridgeType;

        Assert.Throws<NotSupportedException>(() => CartridgeInspection.Inspect(bytes));
    }

    [Theory]
    [InlineData(0x09)]
    [InlineData(0x51)]
    [InlineData(0x55)]
    [InlineData(0xFF)]
    public void InspectRejectsUnsupportedRomSizeCodes(byte romSizeCode)
    {
        var bytes = CreateRomBytes();
        bytes[0x148] = romSizeCode;

        Assert.Throws<InvalidDataException>(() => CartridgeInspection.Inspect(bytes));
    }

    [Fact]
    public void InspectRejectsDeclaredGeometryLargerThanPhysicalImage()
    {
        var bytes = CreateRomBytes(bankCount: 2, declaredSizeCode: 0x01);

        Assert.Throws<InvalidDataException>(() => CartridgeInspection.Inspect(bytes));
    }

    [Theory]
    [InlineData(0x06)]
    [InlineData(0xFF)]
    public void InspectRejectsUnsupportedRamSizeCodes(byte ramSizeCode)
    {
        var bytes = CreateRomBytes();
        bytes[0x149] = ramSizeCode;

        Assert.Throws<InvalidDataException>(() => CartridgeInspection.Inspect(bytes));
    }

    [Theory]
    [InlineData(0x00, 3)]
    [InlineData(0x05, 17)]
    [InlineData(0x01, 129)]
    [InlineData(0x10, 129)]
    public void InspectRejectsPhysicalRomGeometryTheMapperCannotSelect(byte cartridgeType, int bankCount)
    {
        var bytes = CreateRomBytes(bankCount, GetRomSizeCode(bankCount));
        bytes[0x147] = cartridgeType;

        Assert.Throws<InvalidDataException>(() => CartridgeInspection.Inspect(bytes));
    }

    [Theory]
    [InlineData(0x01, 0x04)]
    [InlineData(0x10, 0x04)]
    [InlineData(0x1E, 0x04)]
    [InlineData(0x06, 0x02)]
    public void InspectRejectsRamGeometryTheMapperCannotSelect(byte cartridgeType, byte ramSizeCode)
    {
        var bytes = CreateRomBytes();
        bytes[0x147] = cartridgeType;
        bytes[0x149] = ramSizeCode;

        Assert.Throws<InvalidDataException>(() => CartridgeInspection.Inspect(bytes));
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x01)]
    [InlineData(0x05)]
    [InlineData(0x06)]
    [InlineData(0x0F)]
    [InlineData(0x11)]
    [InlineData(0x19)]
    [InlineData(0x1C)]
    public void InspectRejectsRamDeclarationsForRamlessCartridgeTypes(byte cartridgeType)
    {
        var bytes = CreateRomBytes();
        bytes[0x147] = cartridgeType;
        bytes[0x149] = 0x02;

        Assert.Throws<InvalidDataException>(() => CartridgeInspection.Inspect(bytes));
    }

    [Theory]
    [InlineData(0x08, 0x02, 1)]
    [InlineData(0x09, 0x02, 1)]
    [InlineData(0x02, 0x03, 4)]
    [InlineData(0x03, 0x03, 4)]
    [InlineData(0x10, 0x03, 4)]
    [InlineData(0x12, 0x03, 4)]
    [InlineData(0x13, 0x03, 4)]
    [InlineData(0x1A, 0x04, 16)]
    [InlineData(0x1B, 0x04, 16)]
    [InlineData(0x1D, 0x05, 8)]
    [InlineData(0x1E, 0x05, 8)]
    public void InspectAcceptsRamDeclarationsForRamBearingCartridgeTypes(
        byte cartridgeType,
        byte ramSizeCode,
        int expectedRamBanks)
    {
        var bytes = CreateRomBytes();
        bytes[0x147] = cartridgeType;
        bytes[0x149] = ramSizeCode;

        Assert.Equal(expectedRamBanks, CartridgeInspection.Inspect(bytes).DeclaredRamBanks);
    }

    [Fact]
    public void InspectAllowsMaximumRepresentableMbc5RamGeometry()
    {
        var ordinary = CreateRomBytes();
        ordinary[0x147] = 0x1B;
        ordinary[0x149] = 0x04;
        var rumble = CreateRomBytes();
        rumble[0x147] = 0x1E;
        rumble[0x149] = 0x05;

        Assert.Equal(16, CartridgeInspection.Inspect(ordinary).DeclaredRamBanks);
        Assert.Equal(8, CartridgeInspection.Inspect(rumble).DeclaredRamBanks);
    }

    [Fact]
    public void InspectAllowsUnderDeclaredPhysicalGeometryWithDiagnostic()
    {
        var bytes = CreateRomBytes(bankCount: 12, declaredSizeCode: 0x00);
        bytes[0x147] = 0x11;

        var inspection = CartridgeInspection.Inspect(bytes);

        Assert.Equal(12, inspection.PhysicalRomBanks);
        Assert.Equal(2, inspection.DeclaredRomBanks);
        Assert.Contains(inspection.Diagnostics, diagnostic =>
            diagnostic.Kind == CartridgeDiagnosticKind.PhysicalRomLargerThanDeclared);
    }

    [Fact]
    public void InspectReportsLogoHeaderAndGlobalChecksumMismatchesWithoutRejecting()
    {
        var bytes = CreateRomBytes();
        bytes[0x150] = 0x01;

        var inspection = CartridgeInspection.Inspect(bytes);

        Assert.Contains(inspection.Diagnostics, diagnostic => diagnostic.Kind == CartridgeDiagnosticKind.NintendoLogoMismatch);
        Assert.Contains(inspection.Diagnostics, diagnostic => diagnostic.Kind == CartridgeDiagnosticKind.HeaderChecksumMismatch);
        Assert.Contains(inspection.Diagnostics, diagnostic => diagnostic.Kind == CartridgeDiagnosticKind.GlobalChecksumMismatch);
    }

    [Fact]
    public void InspectAcceptsValidHeaderAndChecksumsWithoutDiagnostics()
    {
        var bytes = CreateRomBytes();
        NintendoLogo.CopyTo(bytes, 0x104);
        FixHeaderChecksum(bytes);
        FixGlobalChecksum(bytes);

        var inspection = CartridgeInspection.Inspect(bytes);

        Assert.Equal(CartridgeCompatibility.DmgOnly, inspection.Compatibility);
        Assert.Equal(2, inspection.PhysicalRomBanks);
        Assert.Equal(2, inspection.DeclaredRomBanks);
        Assert.Equal(0, inspection.DeclaredRamBanks);
        Assert.Empty(inspection.Diagnostics);
    }

    [Fact]
    public void CartridgeHeaderRejectsShortImageWithStructuralError()
    {
        var error = Assert.Throws<InvalidDataException>(() => new CartridgeHeader(new byte[0x143]));

        Assert.Contains("complete cartridge header", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] CreateRomBytes(int bankCount = 2, byte declaredSizeCode = 0x00)
    {
        var bytes = new byte[bankCount * 0x4000];
        bytes[0x147] = 0x00;
        bytes[0x148] = declaredSizeCode;
        bytes[0x149] = 0x00;
        return bytes;
    }

    private static byte GetRomSizeCode(int bankCount)
    {
        if (bankCount < 4)
        {
            return 0x00;
        }

        if (bankCount < 8)
        {
            return 0x01;
        }

        if (bankCount < 16)
        {
            return 0x02;
        }

        if (bankCount < 32)
        {
            return 0x03;
        }

        if (bankCount < 64)
        {
            return 0x04;
        }

        if (bankCount < 128)
        {
            return 0x05;
        }

        return 0x06;
    }

    private static void FixHeaderChecksum(byte[] bytes)
    {
        byte checksum = 0;
        for (var index = 0x134; index <= 0x14C; index++)
        {
            checksum = (byte)(checksum - bytes[index] - 1);
        }

        bytes[0x14D] = checksum;
    }

    private static void FixGlobalChecksum(byte[] bytes)
    {
        var checksum = 0;
        for (var index = 0; index < bytes.Length; index++)
        {
            if (index != 0x14E && index != 0x14F)
            {
                checksum = (checksum + bytes[index]) & 0xFFFF;
            }
        }

        bytes[0x14E] = (byte)(checksum >> 8);
        bytes[0x14F] = (byte)checksum;
    }
}
