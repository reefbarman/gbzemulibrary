using GBZEmuLibrary;

namespace GBZEmuTests;

/// <summary>
/// Verifies factual CGB/AGB compatibility-palette selection independently of firmware execution.
/// </summary>
public sealed class CompatibilityPaletteSelectorTests
{
    [Fact]
    public void UnlicensedCartridgeUsesDefaultCompatibilityPalettes()
    {
        var palettes = CompatibilityPaletteSelector.Select(CreateHeader(new byte[] { 0x88 }));

        Assert.Equal(new byte[] { 0xFF, 0x7F, 0x1F, 0x42, 0xF2, 0x1C, 0x00, 0x00 }, palettes.ObjectPalette0);
        Assert.Equal(new byte[] { 0xFF, 0x7F, 0x1F, 0x42, 0xF2, 0x1C, 0x00, 0x00 }, palettes.ObjectPalette1);
        Assert.Equal(new byte[] { 0xFF, 0x7F, 0xEF, 0x1B, 0x80, 0x61, 0x00, 0x00 }, palettes.BackgroundPalette);
    }

    [Fact]
    public void DonkeyKongLandUsesDocumentedTitlePaletteCombination()
    {
        var palettes = CompatibilityPaletteSelector.Select(CreateHeader("DONKEYKONGLAND95"u8.ToArray(), oldLicensee: 0x01));

        Assert.Equal(new byte[] { 0x1F, 0x23, 0x5F, 0x03, 0xF2, 0x00, 0x09, 0x00 }, palettes.ObjectPalette0);
        Assert.Equal(new byte[] { 0xFF, 0x7F, 0x1F, 0x42, 0xF2, 0x1C, 0x00, 0x00 }, palettes.ObjectPalette1);
        Assert.Equal(new byte[] { 0xFF, 0x4F, 0xD2, 0x7E, 0x4C, 0x3A, 0xE0, 0x1C }, palettes.BackgroundPalette);
    }

    [Fact]
    public void DuplicateChecksumRequiresDocumentedFourthTitleByte()
    {
        var matching = CompatibilityPaletteSelector.Select(CreateHeaderWithChecksum(0xB3, (byte)'B'));
        var other = CompatibilityPaletteSelector.Select(CreateHeaderWithChecksum(0xB3, (byte)'C'));
        var fallback = CompatibilityPaletteSelector.Select(CreateHeader(Array.Empty<byte>()));

        Assert.NotEqual(fallback.BackgroundPalette, matching.BackgroundPalette);
        Assert.Equal(fallback.ObjectPalette0, other.ObjectPalette0);
        Assert.Equal(fallback.ObjectPalette1, other.ObjectPalette1);
        Assert.Equal(fallback.BackgroundPalette, other.BackgroundPalette);
    }

    [Fact]
    public void SelectorMatchesEveryAuthoredFirmwareTableEntry()
    {
        const int mappingTable = 0x006A;
        const int duplicateLetters = 0x00C8;
        const int checksumTable = 0x06C7;
        const int paletteCombinations = 0x0725;
        const int paletteBytes = 0x07BE;
        const int firstDuplicate = 65;
        const int entryCount = 94;
        var bootRom = new BootROM();
        bootRom.Load(HardwareModel.CgbE, BootRomConfig.BuiltIn());
        var image = bootRom.Bytes;

        for (var sourceIndex = 0; sourceIndex < entryCount; sourceIndex++)
        {
            var checksum = image[checksumTable + sourceIndex];
            var fourthTitleByte = sourceIndex >= firstDuplicate
                ? image[duplicateLetters + sourceIndex - firstDuplicate]
                : (byte)0;
            var matchedIndex = FindFirmwareMatch(image, checksum, fourthTitleByte, checksumTable, duplicateLetters, firstDuplicate, entryCount);
            var combination = image[mappingTable + matchedIndex] & 0x7F;
            var combinationOffset = paletteCombinations + (combination * 3);
            var selected = CompatibilityPaletteSelector.Select(CreateHeaderWithChecksum(checksum, fourthTitleByte));

            Assert.Equal(ReadFirmwarePalette(image, paletteBytes, image[combinationOffset]), selected.ObjectPalette0);
            Assert.Equal(ReadFirmwarePalette(image, paletteBytes, image[combinationOffset + 1]), selected.ObjectPalette1);
            Assert.Equal(ReadFirmwarePalette(image, paletteBytes, image[combinationOffset + 2]), selected.BackgroundPalette);
        }
    }

    [Fact]
    public void SelectedPaletteArraysArePrivatelyOwned()
    {
        var first = CompatibilityPaletteSelector.Select(CreateHeader(Array.Empty<byte>()));
        first.BackgroundPalette[0] = 0;
        var second = CompatibilityPaletteSelector.Select(CreateHeader(Array.Empty<byte>()));

        Assert.Equal(0xFF, second.BackgroundPalette[0]);
    }

    private static int FindFirmwareMatch(
        byte[] image,
        byte checksum,
        byte fourthTitleByte,
        int checksumTable,
        int duplicateLetters,
        int firstDuplicate,
        int entryCount)
    {
        for (var index = 0; index < entryCount; index++)
        {
            if (image[checksumTable + index] == checksum &&
                (index < firstDuplicate || image[duplicateLetters + index - firstDuplicate] == fourthTitleByte))
            {
                return index;
            }
        }

        throw new InvalidOperationException("The authored firmware table did not contain its own checksum entry.");
    }

    private static byte[] ReadFirmwarePalette(byte[] image, int paletteBytes, int sourceOffset)
    {
        return image.AsSpan(paletteBytes + sourceOffset, 8).ToArray();
    }

    private static CartridgeHeader CreateHeaderWithChecksum(byte checksum, byte fourthTitleByte)
    {
        var title = new byte[16];
        title[3] = fourthTitleByte;
        title[0] = (byte)(checksum - fourthTitleByte);
        return CreateHeader(title, oldLicensee: 0x01);
    }

    private static CartridgeHeader CreateHeader(byte[] title, byte oldLicensee = 0)
    {
        var rom = new byte[0x8000];
        Array.Copy(title, 0, rom, 0x134, Math.Min(title.Length, 16));
        rom[0x147] = 0x00;
        rom[0x148] = 0x00;
        rom[0x149] = 0x00;
        rom[0x14B] = oldLicensee;
        return new CartridgeHeader(rom);
    }
}
