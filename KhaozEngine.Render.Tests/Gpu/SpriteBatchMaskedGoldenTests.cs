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

    static void AssertRgba(byte[] rgba, int x, int y, byte r, byte g, byte b, byte a, int tolerance = 1)
    {
        int offset = (y * Width + x) * 4;
        byte[] expected = [r, g, b, a];
        for (int channel = 0; channel < 4; channel++)
            Assert.InRange((int)rgba[offset + channel], expected[channel] - tolerance, expected[channel] + tolerance);
    }
}
