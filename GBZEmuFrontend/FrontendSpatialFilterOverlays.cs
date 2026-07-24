using Raylib_cs;
using RaylibColor = Raylib_cs.Color;

namespace GBZEmuFrontend;

/// <summary>
/// Generates deterministic output-size pixel-grid and glare overlays without owning GPU resources.
/// </summary>
internal static class FrontendSpatialFilterPixels
{
    /// <summary>
    /// Creates a black-alpha pixel-boundary overlay, or no overlay when disabled or shown at native size.
    /// </summary>
    public static RaylibColor[]? CreateGrid(int sourceWidth, int sourceHeight, int scale, string effectId)
    {
        ValidateDimensions(sourceWidth, sourceHeight, scale);
        var alpha = ResolveAlpha(effectId, subtle: 28, strong: 52);
        if (alpha == 0 || scale == 1)
        {
            return null;
        }

        var width = sourceWidth * scale;
        var height = sourceHeight * scale;
        var pixels = new RaylibColor[width * height];
        for (var y = 0; y < height; y++)
        {
            var horizontalBoundary = y % scale == scale - 1;
            for (var x = 0; x < width; x++)
            {
                if (horizontalBoundary || x % scale == scale - 1)
                {
                    pixels[(y * width) + x] = new RaylibColor((byte)0, (byte)0, (byte)0, alpha);
                }
            }
        }

        return pixels;
    }

    /// <summary>
    /// Creates a flat-screen diagonal glare overlay, or no overlay when disabled.
    /// </summary>
    public static RaylibColor[]? CreateGlare(int sourceWidth, int sourceHeight, int scale, string effectId)
    {
        ValidateDimensions(sourceWidth, sourceHeight, scale);
        var maximumAlpha = ResolveAlpha(effectId, subtle: 18, strong: 34);
        if (maximumAlpha == 0)
        {
            return null;
        }

        var width = sourceWidth * scale;
        var height = sourceHeight * scale;
        var pixels = new RaylibColor[width * height];
        var inverseWidth = 1f / width;
        var inverseHeight = 1f / height;
        for (var y = 0; y < height; y++)
        {
            var normalizedY = (y + 0.5f) * inverseHeight;
            for (var x = 0; x < width; x++)
            {
                var normalizedX = (x + 0.5f) * inverseWidth;
                var diagonal = normalizedX + (normalizedY * 0.7f);
                var highlight = MathF.Max(0, 1 - MathF.Abs(diagonal - 0.48f) / 0.42f);
                highlight *= 1 - (normalizedY * 0.35f);
                var alpha = (byte)MathF.Round(maximumAlpha * highlight);
                pixels[(y * width) + x] = new RaylibColor(byte.MaxValue, byte.MaxValue, byte.MaxValue, alpha);
            }
        }

        return pixels;
    }

    private static byte ResolveAlpha(string effectId, byte subtle, byte strong)
    {
        return effectId switch
        {
            VideoFilterPresetCatalog.OffEffectId => 0,
            VideoFilterPresetCatalog.SubtleEffectId => subtle,
            VideoFilterPresetCatalog.StrongEffectId => strong,
            _ => throw new ArgumentOutOfRangeException(nameof(effectId), effectId, "Unknown spatial-effect level.")
        };
    }

    private static void ValidateDimensions(int sourceWidth, int sourceHeight, int scale)
    {
        if (sourceWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceWidth));
        }

        if (sourceHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceHeight));
        }

        if (scale is < 1 or > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(scale));
        }
    }
}

/// <summary>
/// Owns cached Raylib textures for presentation-only output-size spatial overlays.
/// </summary>
internal sealed class FrontendSpatialFilterOverlays : IDisposable
{
    private Texture2D _gridTexture;
    private Texture2D _glareTexture;
    private bool _gridReady;
    private bool _glareReady;

    public FrontendSpatialFilterOverlays(
        int sourceWidth,
        int sourceHeight,
        int scale,
        string pixelGrid,
        string glare)
    {
        var width = sourceWidth * scale;
        var height = sourceHeight * scale;
        var gridPixels = FrontendSpatialFilterPixels.CreateGrid(sourceWidth, sourceHeight, scale, pixelGrid);
        if (gridPixels != null)
        {
            _gridTexture = CreateTexture(width, height, gridPixels);
            _gridReady = true;
        }

        var glarePixels = FrontendSpatialFilterPixels.CreateGlare(sourceWidth, sourceHeight, scale, glare);
        if (glarePixels != null)
        {
            _glareTexture = CreateTexture(width, height, glarePixels);
            _glareReady = true;
        }
    }

    /// <summary>
    /// Draws cached overlays in grid-then-glare order over the already-scaled source image.
    /// </summary>
    public void Draw()
    {
        if (_gridReady)
        {
            Raylib.DrawTexture(_gridTexture, 0, 0, RaylibColor.White);
        }

        if (_glareReady)
        {
            Raylib.DrawTexture(_glareTexture, 0, 0, RaylibColor.White);
        }
    }

    /// <summary>
    /// Releases each allocated Raylib texture once.
    /// </summary>
    public void Dispose()
    {
        if (_gridReady)
        {
            Raylib.UnloadTexture(_gridTexture);
            _gridReady = false;
        }

        if (_glareReady)
        {
            Raylib.UnloadTexture(_glareTexture);
            _glareReady = false;
        }
    }

    private static Texture2D CreateTexture(int width, int height, RaylibColor[] pixels)
    {
        var image = Raylib.GenImageColor(width, height, RaylibColor.Blank);
        var texture = Raylib.LoadTextureFromImage(image);
        Raylib.UnloadImage(image);
        Raylib.UpdateTexture(texture, pixels);
        Raylib.SetTextureFilter(texture, TextureFilter.Point);
        return texture;
    }
}
