using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class SpriteBatchMaskedGoldenTests
{
    const int Width = 32, Height = 16;
    static readonly Vector4 FullUv = new(0, 0, 1, 1);

    [GpuFact]
    public void ZeroAndOneCoveragePreserveBackgroundAndSource()
    {
        byte[] rgba = Render2DSnapshot.Capture(Width, Height, new Color(0f, 0f, 1f, 1f), ctx =>
        {
            var source = ctx.CreateTexture([255, 0, 0, 255], 1, 1);
            var maskA = ctx.CreateTexture([255, 0, 0, 0, 0, 255, 0, 255], 2, 1);
            var maskB = ctx.CreateTexture([0, 0, 255, 0, 255, 0, 0, 255], 2, 1);
            ctx.Batch.Begin(SamplerMode.Point);
            ctx.Batch.DrawMasked(source, new Vector4(0, 0, Width, 8), FullUv, maskA,
                FullUv, Color.White, SamplerMode.Point);
            ctx.Batch.DrawMasked(source, new Vector4(0, 8, Width, 8), FullUv, maskB,
                FullUv, Color.White, SamplerMode.Point);
            ctx.Batch.End();
        });

        AssertRgba(rgba, 8, 4, 0, 0, 255, 255);
        AssertRgba(rgba, 24, 4, 255, 0, 0, 255);
        AssertRgba(rgba, 8, 12, 0, 0, 255, 255);
        AssertRgba(rgba, 24, 12, 255, 0, 0, 255);
    }

    [GpuFact]
    public void HalfCoveredEdgeUsesCoverageOnce()
    {
        byte[] rgba = Render2DSnapshot.Capture(Width, Height, Color.Transparent, ctx =>
        {
            var source = ctx.CreateTexture([255, 255, 255, 255], 1, 1);
            var maskA = ctx.CreateTexture([255, 0, 0, 0, 0, 255, 0, 255], 2, 1);
            var maskB = ctx.CreateTexture([0, 0, 255, 0, 255, 0, 0, 255], 2, 1);
            // The midpoint between two alpha texels is exactly half covered without 8-bit quantization.
            var midpoint = new Vector4(0.5f, 0.5f, 0.5f, 0.5f);
            ctx.Batch.Begin(SamplerMode.Point);
            ctx.Batch.DrawMasked(source, new Vector4(0, 0, Width, 8), FullUv, maskA,
                midpoint, Color.White);
            ctx.Batch.DrawMasked(source, new Vector4(0, 8, Width, 8), FullUv, maskB,
                midpoint, Color.White);
            ctx.Batch.End();
        });

        AssertRgba(rgba, 16, 4, 128, 128, 128, 64);
        AssertRgba(rgba, 16, 12, 128, 128, 128, 64);
    }

    [GpuFact]
    public void MaskMultipliesSourceAndTintAlpha()
    {
        byte[] rgba = Render2DSnapshot.Capture(Width, Height, Color.Transparent, ctx =>
        {
            var source = ctx.CreateTexture([255, 255, 255, 128], 1, 1);
            var mask = ctx.CreateTexture([255, 0, 0, 0, 0, 255, 0, 255], 2, 1);
            ctx.Batch.Begin(SamplerMode.Point);
            ctx.Batch.DrawMasked(source, new Vector4(0, 0, Width, Height), FullUv, mask,
                new Vector4(0.5f, 0.5f, 0.5f, 0.5f), new Color(1f, 0.5f, 0f, 0.5f));
            ctx.Batch.End();
        });

        AssertRgba(rgba, 16, 8, 32, 16, 0, 4);
    }

    [GpuFact]
    public void VerticalGradientComposesBeforeMask()
    {
        byte[] rgba = Render2DSnapshot.Capture(Width, Height, Color.Transparent, ctx =>
        {
            var source = ctx.CreateTexture([255, 255, 255, 255], 1, 1);
            var mask = ctx.CreateTexture([255, 0, 0, 0, 0, 255, 0, 255], 2, 1);
            ctx.Batch.Begin(SamplerMode.Point);
            // Pixel row 8 is exactly halfway down this gradient.
            ctx.Batch.DrawMasked(source, new Vector4(0, 0.5f, Width, Height), FullUv, mask,
                new Vector4(0.5f, 0.5f, 0.5f, 0.5f),
                new Color(1f, 0f, 0f, 1f), new Color(0f, 0f, 1f, 1f));
            ctx.Batch.End();
        });

        AssertRgba(rgba, 16, 8, 64, 0, 64, 64);
        for (int y = 1; y < Height; y++)
        {
            int previous = ((y - 1) * Width + 16) * 4;
            int current = (y * Width + 16) * 4;
            Assert.True(rgba[current] <= rgba[previous], $"Red increased at row {y}.");
            Assert.True(rgba[current + 2] >= rgba[previous + 2], $"Blue decreased at row {y}.");
            Assert.InRange((int)rgba[current + 3], 63, 65);
        }
    }

    [GpuFact]
    public void PointSourceStillUsesLinearMaskByDefault()
    {
        byte[] rgba = Render2DSnapshot.Capture(Width, Height, Color.Transparent, ctx =>
        {
            var source = ctx.CreateTexture([255, 0, 0, 255, 0, 0, 255, 255], 2, 1);
            var mask = ctx.CreateTexture([255, 255, 255, 0, 255, 255, 255, 255], 2, 1);
            var nearestRed = new Vector4(0.375f, 0.5f, 0.375f, 0.5f);
            var halfCoverage = new Vector4(0.5f, 0.5f, 0.5f, 0.5f);
            ctx.Batch.Begin(SamplerMode.Point);
            ctx.Batch.DrawMasked(source, new Vector4(0, 0, Width, 4), nearestRed,
                mask, halfCoverage, Color.White);
            ctx.Batch.DrawMasked(source, new Vector4(0, 4, Width, 4), nearestRed,
                mask, halfCoverage, Color.White, SamplerMode.Linear);
            ctx.Batch.DrawMasked(source, new Vector4(0, 8, Width, 4), nearestRed,
                mask, new Vector4(0.375f, 0.5f, 0.375f, 0.5f), Color.White, SamplerMode.Point);
            ctx.Batch.DrawMasked(source, new Vector4(0, 12, Width, 4), nearestRed,
                mask, new Vector4(0.625f, 0.5f, 0.625f, 0.5f), Color.White, SamplerMode.Point);
            ctx.Batch.End();
        });

        AssertRgba(rgba, 16, 2, 128, 0, 0, 64);
        AssertRgba(rgba, 16, 6, 128, 0, 0, 64);
        AssertRgba(rgba, 16, 10, 0, 0, 0, 0);
        AssertRgba(rgba, 16, 14, 255, 0, 0, 255);
    }

    [GpuFact]
    public void HalfCoverageOverOpaqueBackgroundUsesExistingBlend()
    {
        byte[] rgba = Render2DSnapshot.Capture(Width, Height, new Color(0f, 0f, 1f, 1f), ctx =>
        {
            var source = ctx.CreateTexture([255, 255, 255, 255], 1, 1);
            var mask = ctx.CreateTexture([255, 0, 0, 0, 0, 255, 0, 255], 2, 1);
            ctx.Batch.Begin(SamplerMode.Point);
            ctx.Batch.DrawMasked(source, new Vector4(0, 0, Width, Height), FullUv, mask,
                new Vector4(0.5f, 0.5f, 0.5f, 0.5f), Color.White);
            ctx.Batch.End();
        });

        AssertRgba(rgba, 16, 8, 128, 128, 255, 191);
    }

    [GpuFact]
    public void AdditiveCoverageUsesExistingFactors()
    {
        byte[] rgba = Render2DSnapshot.Capture(Width, Height, new Color(0.25f, 0f, 0f, 0.25f), ctx =>
        {
            var source = ctx.CreateTexture([255, 255, 255, 255], 1, 1);
            var mask = ctx.CreateTexture([255, 0, 0, 0, 0, 255, 0, 255], 2, 1);
            ctx.Batch.Begin(SamplerMode.Point);
            ctx.Batch.BlendMode = BlendMode.Additive;
            ctx.Batch.DrawMasked(source, new Vector4(0, 0, Width, Height), FullUv, mask,
                new Vector4(0.5f, 0.5f, 0.5f, 0.5f), Color.White);
            ctx.Batch.End();
        });

        AssertRgba(rgba, 16, 8, 191, 128, 128, 128);
    }

    static void AssertRgba(byte[] rgba, int x, int y, byte r, byte g, byte b, byte a, int tolerance = 1)
    {
        int offset = (y * Width + x) * 4;
        byte[] expected = [r, g, b, a];
        for (int channel = 0; channel < 4; channel++)
            Assert.InRange((int)rgba[offset + channel], expected[channel] - tolerance, expected[channel] + tolerance);
    }
}
