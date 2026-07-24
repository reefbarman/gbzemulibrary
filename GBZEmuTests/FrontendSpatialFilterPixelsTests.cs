using GBZEmuFrontend;

namespace GBZEmuTests;

/// <summary>
/// Verifies deterministic spatial-filter overlay pixels without creating Raylib window resources.
/// </summary>
public sealed class FrontendSpatialFilterPixelsTests
{
    /// <summary>
    /// Verifies disabled and native-size pixel grids do not allocate an overlay.
    /// </summary>
    [Theory]
    [InlineData(4, "off")]
    [InlineData(1, "subtle")]
    [InlineData(1, "strong")]
    public void GridIsOmittedWhenDisabledOrAtNativeScale(int scale, string effectId)
    {
        Assert.Null(FrontendSpatialFilterPixels.CreateGrid(160, 144, scale, effectId));
    }

    /// <summary>
    /// Verifies grid pixels align to the final output subpixel row and column of each source cell.
    /// </summary>
    [Fact]
    public void GridAlignsToScaledPixelCellBoundaries()
    {
        const int sourceWidth = 2;
        const int sourceHeight = 2;
        const int scale = 4;
        var pixels = FrontendSpatialFilterPixels.CreateGrid(sourceWidth, sourceHeight, scale, "subtle");

        Assert.NotNull(pixels);
        Assert.Equal(8 * 8, pixels.Length);
        Assert.Equal(0, pixels[0].A);
        Assert.Equal(28, pixels[3].A);
        Assert.Equal(28, pixels[3 * 8].A);
        Assert.Equal(28, pixels[(7 * 8) + 7].A);
    }

    /// <summary>
    /// Verifies strong grid boundaries are darker than subtle boundaries.
    /// </summary>
    [Fact]
    public void StrongGridUsesGreaterBoundaryAlpha()
    {
        var subtle = FrontendSpatialFilterPixels.CreateGrid(1, 1, 4, "subtle")!;
        var strong = FrontendSpatialFilterPixels.CreateGrid(1, 1, 4, "strong")!;

        Assert.Equal(28, subtle[3].A);
        Assert.Equal(52, strong[3].A);
    }

    /// <summary>
    /// Verifies glare is omitted when off and generated at the exact scaled output dimensions otherwise.
    /// </summary>
    [Fact]
    public void GlareUsesScaledOutputDimensions()
    {
        Assert.Null(FrontendSpatialFilterPixels.CreateGlare(160, 144, 4, "off"));

        var pixels = FrontendSpatialFilterPixels.CreateGlare(3, 2, 5, "subtle");

        Assert.NotNull(pixels);
        Assert.Equal(15 * 10, pixels.Length);
        Assert.Contains(pixels, pixel => pixel.A > 0);
    }

    /// <summary>
    /// Verifies strong glare has a brighter peak while preserving transparent low-lobe pixels.
    /// </summary>
    [Fact]
    public void StrongGlareHasGreaterPeakAlpha()
    {
        var subtle = FrontendSpatialFilterPixels.CreateGlare(20, 18, 2, "subtle")!;
        var strong = FrontendSpatialFilterPixels.CreateGlare(20, 18, 2, "strong")!;

        Assert.True(strong.Max(pixel => pixel.A) > subtle.Max(pixel => pixel.A));
        Assert.True(strong.Min(pixel => pixel.A) < strong.Max(pixel => pixel.A));
    }

    /// <summary>
    /// Verifies invalid effect IDs and dimensions are rejected before resource creation.
    /// </summary>
    [Fact]
    public void InvalidSpatialConfigurationIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => FrontendSpatialFilterPixels.CreateGrid(160, 144, 4, "unknown"));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => FrontendSpatialFilterPixels.CreateGlare(0, 144, 4, "subtle"));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => FrontendSpatialFilterPixels.CreateGrid(160, 144, 11, "subtle"));
    }
}
