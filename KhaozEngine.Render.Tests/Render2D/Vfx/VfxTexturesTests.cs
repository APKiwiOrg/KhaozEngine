using KhaozEngine.Render2D.Vfx;
using Xunit;

namespace KhaozEngine.Tests.Render2D.Vfx;

/// <summary>Headless coverage for the CPU-baked VFX textures (no GPU, no shipped asset).</summary>
public class VfxTexturesTests
{
    static (byte R, byte G, byte B, byte A) Pixel(byte[] rgba, int size, int x, int y)
    {
        int i = (y * size + x) * 4;
        return (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]);
    }

    [Fact]
    public void BakeGlowPixels_HasExpectedBufferSize()
    {
        const int size = 16;
        byte[] px = VfxTextures.BakeGlowPixels(size);
        Assert.Equal(size * size * 4, px.Length);
    }

    [Fact]
    public void BakeGlowPixels_IsOpaqueWhiteAtCentreAndTransparentAtCorners()
    {
        const int size = 16;
        byte[] px = VfxTextures.BakeGlowPixels(size, falloff: 2f);

        var centre = Pixel(px, size, size / 2, size / 2);
        Assert.True(centre.A > 200, $"centre alpha {centre.A} should be near opaque");

        var corner = Pixel(px, size, 0, 0);
        Assert.Equal(0, corner.A);
    }

    [Fact]
    public void BakeGlowPixels_RgbIsWhiteEverywhere()
    {
        const int size = 16;
        byte[] px = VfxTextures.BakeGlowPixels(size);
        for (int i = 0; i < px.Length; i += 4)
        {
            Assert.Equal(255, px[i]);
            Assert.Equal(255, px[i + 1]);
            Assert.Equal(255, px[i + 2]);
        }
    }

    [Fact]
    public void BakeGlowPixels_AlphaFallsOffFromCentre()
    {
        const int size = 32;
        byte[] px = VfxTextures.BakeGlowPixels(size, falloff: 2f);
        int c = size / 2;
        byte aCentre = Pixel(px, size, c, c).A;
        byte aQuarter = Pixel(px, size, c + size / 4, c).A;
        byte aEdge = Pixel(px, size, size - 1, c).A;
        Assert.True(aCentre > aQuarter, $"centre {aCentre} > quarter {aQuarter}");
        Assert.True(aQuarter > aEdge, $"quarter {aQuarter} > edge {aEdge}");
    }

    [Fact]
    public void BakeRingPixels_IsTransparentAtCentreAndHollow()
    {
        const int size = 32;
        byte[] px = VfxTextures.BakeRingPixels(size, innerRadius01: 0.5f, thickness01: 0.25f);
        int c = size / 2;
        Assert.Equal(0, Pixel(px, size, c, c).A);             // hollow centre
        // A pixel on the ring band (mid radius ~0.625 of half-extent) should be lit.
        int band = c + (int)(0.625f * (size / 2));
        Assert.True(Pixel(px, size, band, c).A > 0, "ring band should be lit");
    }
}
