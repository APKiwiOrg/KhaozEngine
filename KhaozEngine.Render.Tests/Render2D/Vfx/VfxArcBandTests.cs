using System;
using System.Numerics;
using KhaozEngine.Render2D.Vfx;
using Xunit;

namespace KhaozEngine.Tests.Render2D.Vfx;

/// <summary>
/// Headless coverage for the anti-aliased arc band bake (no GPU, no shipped asset). Everything here is in
/// pixels of the target image, and pixel (x, y) samples its own centre (x + 0.5, y + 0.5), so a centre placed
/// on a half-pixel puts the band's edges exactly on pixel centres.
/// </summary>
public class VfxArcBandTests
{
    const int Size = 240;
    static readonly Vector2 Centre = new(120.5f, 120.5f);
    const float Inner = 40f;
    const float Outer = 60f;
    const float Mid = (Inner + Outer) * 0.5f;

    static byte Alpha(byte[] rgba, int width, int x, int y) => rgba[(y * width + x) * 4 + 3];

    // The pixel whose centre sits at polar (radius, angle) around Centre. Only exact for the angles the tests
    // pick, which is why they keep to the axes and to distances measured along them.
    static (int X, int Y) At(float radius, float angle) =>
        ((int)MathF.Round(Centre.X + MathF.Cos(angle) * radius - 0.5f),
         (int)MathF.Round(Centre.Y + MathF.Sin(angle) * radius - 0.5f));

    static byte AlphaAt(byte[] px, float radius, float angle)
    {
        var (x, y) = At(radius, angle);
        return Alpha(px, Size, x, y);
    }

    static byte[] Bake(float start, float sweep, float feather = 1f, bool roundCaps = false) =>
        VfxTextures.BakeArcBandPixels(Size, Size, Centre, Inner, Outer, start, sweep, feather, roundCaps);

    [Fact]
    public void BufferIsTightlyPackedRgba()
    {
        byte[] px = Bake(0f, 1f);
        Assert.Equal(Size * Size * 4, px.Length);
        for (int i = 0; i < px.Length; i += 4)
        {
            Assert.Equal(255, px[i]);
            Assert.Equal(255, px[i + 1]);
            Assert.Equal(255, px[i + 2]);
        }
    }

    [Fact]
    public void MidRadiusAtMidAngleIsFullyCovered()
    {
        byte[] px = Bake(0.4f, 1.2f);
        Assert.Equal(255, AlphaAt(px, Mid, 0.4f + 0.6f));
    }

    [Fact]
    public void InsideTheInnerRadiusAndOutsideTheOuterAreEmpty()
    {
        byte[] px = Bake(0.4f, 1.2f);
        float midAngle = 0.4f + 0.6f;
        Assert.Equal(0, AlphaAt(px, Inner - 8f, midAngle));
        Assert.Equal(0, AlphaAt(px, Outer + 8f, midAngle));
        Assert.Equal(0, Alpha(px, Size, (int)Centre.X, (int)Centre.Y));
    }

    [Fact]
    public void OutsideTheSweepIsEmpty()
    {
        // A quarter turn from angle 0: the band exists along +x and +y, and not along -x.
        byte[] px = Bake(0f, MathF.PI * 0.5f);
        Assert.Equal(255, AlphaAt(px, Mid, MathF.PI * 0.25f));
        Assert.Equal(0, AlphaAt(px, Mid, MathF.PI));
    }

    [Fact]
    public void APixelExactlyOnAnEdgeIsHalfCovered()
    {
        byte[] px = Bake(0f, MathF.PI * 0.5f);
        // Angle 0 is inside the sweep, so the pixel whose centre sits at exactly Outer (and at exactly Inner)
        // along +x is on the radial edge: half coverage, give or take the byte rounding.
        Assert.InRange(Alpha(px, Size, (int)(Centre.X + Outer - 0.5f), (int)(Centre.Y - 0.5f)), 120, 136);
        Assert.InRange(Alpha(px, Size, (int)(Centre.X + Inner - 0.5f), (int)(Centre.Y - 0.5f)), 120, 136);
    }

    [Fact]
    public void AlphaIsMonotoneAcrossTheFeather()
    {
        const float feather = 6f;
        byte[] px = Bake(0f, MathF.PI * 0.5f, feather);
        int y = (int)(Centre.Y - 0.5f);
        byte previous = 255;
        // Walk out through the feather at the outer edge: coverage must never rise.
        for (float r = Outer - feather; r <= Outer + feather; r += 1f)
        {
            byte a = Alpha(px, Size, (int)MathF.Round(Centre.X + r - 0.5f), y);
            Assert.True(a <= previous, $"alpha rose from {previous} to {a} at radius {r}");
            previous = a;
        }
        Assert.Equal(0, previous);
    }

    [Fact]
    public void BothSweepSignsDescribeTheSameSector()
    {
        byte[] forward = Bake(0.3f, 1.1f);
        byte[] backward = Bake(0.3f + 1.1f, -1.1f);
        // The two calls describe one sector from either end, so the same pixels are lit. The alphas are only
        // near-equal because the caller's own start angle round-trips through a float add and subtract.
        for (int i = 3; i < forward.Length; i += 4)
        {
            Assert.Equal(forward[i] > 0, backward[i] > 0);
            Assert.InRange(Math.Abs(forward[i] - backward[i]), 0, 2);
        }
    }

    [Fact]
    public void AFullTurnHasNoCaps()
    {
        byte[] px = Bake(0f, MathF.Tau);
        for (int i = 0; i < 32; i++)
        {
            float angle = i * MathF.Tau / 32f;
            Assert.Equal(255, AlphaAt(px, Mid, angle));
        }
    }

    [Fact]
    public void RoundCapsExtendBeyondTheFlatEnd()
    {
        const float sweep = MathF.PI * 0.5f;
        byte[] flat = Bake(0f, sweep, 1f, roundCaps: false);
        byte[] round = Bake(0f, sweep, 1f, roundCaps: true);

        // Just past the start cap, along -y at the mid radius: the flat end is done, the round one is not.
        float halfThick = (Outer - Inner) * 0.5f;
        float overshoot = halfThick * 0.5f;
        int x = (int)(Centre.X + Mid - 0.5f);
        int y = (int)MathF.Round(Centre.Y - overshoot - 0.5f);
        Assert.Equal(0, Alpha(flat, Size, x, y));
        Assert.Equal(255, Alpha(round, Size, x, y));

        // And the round cap stops at about half the thickness beyond it.
        int beyond = (int)MathF.Round(Centre.Y - (halfThick + 3f) - 0.5f);
        Assert.Equal(0, Alpha(round, Size, x, beyond));
    }

    [Fact]
    public void TheTightImageHoldsEveryLitPixel()
    {
        const float inner = 140f, outer = 150f, start = 0.35f, sweep = 0.8f, feather = 1.5f;
        foreach (bool roundCaps in new[] { false, true })
        {
            var (w, h, centre) = VfxTextures.ArcBandImageSize(inner, outer, start, sweep, feather, roundCaps);
            byte[] px = VfxTextures.BakeArcBandPixels(w, h, centre, inner, outer, start, sweep, feather, roundCaps);

            for (int x = 0; x < w; x++)
            {
                Assert.Equal(0, Alpha(px, w, x, 0));
                Assert.Equal(0, Alpha(px, w, x, h - 1));
            }
            for (int y = 0; y < h; y++)
            {
                Assert.Equal(0, Alpha(px, w, 0, y));
                Assert.Equal(0, Alpha(px, w, w - 1, y));
            }

            // And it is not so tight that it clipped the band: the middle of the sector is still solid.
            float midAngle = start + sweep * 0.5f;
            float midR = (inner + outer) * 0.5f;
            int mx = (int)MathF.Round(centre.X + MathF.Cos(midAngle) * midR - 0.5f);
            int my = (int)MathF.Round(centre.Y + MathF.Sin(midAngle) * midR - 0.5f);
            Assert.Equal(255, Alpha(px, w, mx, my));
        }
    }

    [Fact]
    public void AShallowArcGetsAFarSmallerImageThanItsCircle()
    {
        var (w, h, centre) = VfxTextures.ArcBandImageSize(140f, 150f, 0f, 0.8f, 1f, roundCaps: false);
        Assert.True(w < 150, $"width {w} should be far under the 300 of the full circle");
        Assert.True(h < 120, $"height {h} should be far under the 300 of the full circle");
        // The centre of curvature is off the left edge of a shallow arc drawn around angle 0.
        Assert.True(centre.X < 0f, $"centre of curvature X {centre.X} should sit outside the image");
    }

    [Fact]
    public void AFullTurnImageIsTheWholeCircle()
    {
        var (w, h, centre) = VfxTextures.ArcBandImageSize(40f, 60f, 0f, MathF.Tau, 1f, roundCaps: false);
        Assert.Equal(w, h);
        Assert.InRange(w, 121, 124);
        Assert.InRange(centre.X, 60.5f, 62f);
        Assert.Equal(centre.X, centre.Y);
    }

    [Fact]
    public void DegenerateArgumentsStillProduceABuffer()
    {
        byte[] swapped = VfxTextures.BakeArcBandPixels(Size, Size, Centre, Outer, Inner, 0f, 1f);
        byte[] ordered = Bake(0f, 1f);
        Assert.Equal(ordered, swapped);

        byte[] tiny = VfxTextures.BakeArcBandPixels(0, 0, Centre, Inner, Outer, 0f, 1f);
        Assert.Equal(4, tiny.Length);
    }

    [Fact]
    public void ZeroFeatherIsAHardEdge()
    {
        byte[] px = Bake(0f, MathF.PI * 0.5f, feather: 0f);
        int y = (int)(Centre.Y - 0.5f);
        Assert.Equal(255, Alpha(px, Size, (int)(Centre.X + Mid - 0.5f), y));
        Assert.Equal(0, Alpha(px, Size, (int)(Centre.X + Outer + 1f - 0.5f), y));
    }
}
