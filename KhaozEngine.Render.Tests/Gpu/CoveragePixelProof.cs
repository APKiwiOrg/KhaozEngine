using System;
using System.Linq;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

internal static class CoveragePixelProof
{
    // A depth value is compared in the actual R32 target. Colour and normals allow one UNorm step.
    internal const float DepthTolerance = .00002f;
    internal const float NormalTolerance = 1f / 255f;
    internal const float OpaqueColourTolerance = 1f / 255f + 1e-6f;

    internal static void AssertSurface(CoverageCapture baseline, CoverageCapture conformed,
        CoverageCapture oracle, bool requireBreakthrough)
    {
        Assert.Equal(oracle.SubmittedSurfaceSignature, conformed.SubmittedSurfaceSignature);
        int breakthroughs = Breakthroughs(baseline, oracle);
        if (requireBreakthrough) Assert.True(breakthroughs > 0, $"No baseline breakthrough: {baseline.Artifact}");
        Assert.Equal(0, Breakthroughs(conformed, oracle));
        // Material witnesses never count depth background, whose clear colour sits one byte from the plate ratio.
        int body = Enumerable.Range(0, conformed.Depth.Values.Length).Count(i => !Background(conformed.Depth.Values[i]) && Host(conformed.Colour, i));
        int plate = Enumerable.Range(0, conformed.Depth.Values.Length).Count(i => !Background(conformed.Depth.Values[i]) && Equipment(conformed.Colour, i));
        Assert.True(body > 100, $"Accepted body witness {body}: {conformed.Artifact}");
        if (requireBreakthrough) Assert.True(plate > 100, $"Accepted plate witness {plate}: {conformed.Artifact}");
        Console.WriteLine($"coverage pixels={conformed.Artifact} baselineBreakthrough={breakthroughs} body={body} plate={plate}");
        Assert.Equal(0, NewHoles(conformed, oracle));
        int protectedWitnesses = 0;
        for (int i = 0; i < oracle.Depth.Values.Length; i++)
            if (Host(baseline.Colour, i) && Host(oracle.Colour, i)
                && MathF.Abs(baseline.Depth.Values[i] - oracle.Depth.Values[i]) <= DepthTolerance)
            {
                Assert.InRange(MathF.Abs(conformed.Depth.Values[i] - baseline.Depth.Values[i]), 0, DepthTolerance);
                protectedWitnesses++;
            }
        Assert.True(protectedWitnesses > 0, $"No retained skin witness: {oracle.Artifact}");
        AssertTargetsEqual(conformed, oracle);
    }

    static bool Host(byte[] colour, int i) => colour[i * 4] > colour[i * 4 + 2] * 1.4f;
    static bool Equipment(byte[] colour, int i) => colour[i * 4 + 2] > colour[i * 4] * 2f;
    internal static int Breakthroughs(CoverageCapture actual, CoverageCapture oracle) =>
        Enumerable.Range(0, oracle.Depth.Values.Length).Count(i => Equipment(oracle.Colour, i) && Host(actual.Colour, i));
    static bool Background(float depth) => MathF.Abs(depth - .02f) < 1e-6f;
    internal static int NewHoles(CoverageCapture actual, CoverageCapture oracle) =>
        Enumerable.Range(0, oracle.Depth.Values.Length).Count(i => !Background(oracle.Depth.Values[i]) && Background(actual.Depth.Values[i]));

    internal static void AssertTargetsEqual(CoverageCapture actual, CoverageCapture expected)
    {
        EqualTarget(actual.Depth, expected.Depth, DepthTolerance, "main depth");
        EqualTarget(actual.Normal, expected.Normal, NormalTolerance, "main normal");
        Assert.Equal(expected.Colour.Length, actual.Colour.Length);
        int worst = 0;
        for (int i = 0; i < actual.Colour.Length; i++) worst = Math.Max(worst, Math.Abs(actual.Colour[i] - expected.Colour[i]));
        Assert.True(worst <= 1, $"Opaque colour mismatch {worst}: {actual.Artifact}");
    }

    internal static void EqualTarget(CoverageTargetReadback actual, CoverageTargetReadback expected,
        float tolerance, string pass)
    {
        Assert.Equal((expected.Width, expected.Height, expected.Channels), (actual.Width, actual.Height, actual.Channels));
        float worst = 0;
        for (int i = 0; i < actual.Values.Length; i++)
        {
            Assert.True(float.IsFinite(actual.Values[i]), $"Nonfinite {pass} at {i}");
            Assert.True(float.IsFinite(expected.Values[i]), $"Nonfinite reference {pass} at {i}");
            worst = MathF.Max(worst, MathF.Abs(actual.Values[i] - expected.Values[i]));
        }
        Assert.True(worst <= tolerance, $"{pass} differs by {worst}, limit {tolerance}");
    }

    internal static void AssertShadows(CoverageCapture actual, CoverageCapture expected, bool staticLight)
    {
        var cascades = actual.Cascades!;
        EqualTarget(cascades, expected.Cascades!, DepthTolerance, "cascades");
        Assert.Equal(4, cascades.Width / cascades.Height);
        for (int cascade = 0; cascade < 4; cascade++) AssertTile(cascades, cascade, 0, 4, "cascade");
        var points = staticLight ? actual.TransientPoints! : actual.Points!;
        var control = staticLight ? expected.TransientPoints! : expected.Points!;
        Assert.NotNull(points);
        Assert.True(points.SourceRow >= 0);
        Assert.True(control.SourceRow >= 0);
        EqualTarget(points, control, DepthTolerance, staticLight ? "static transient" : "dynamic base");
        for (int face = 0; face < 6; face++) AssertTile(points, face, 0, 6, "point face");
    }

    static void AssertTile(CoverageTargetReadback target, int column, int row, int columns, string label)
    {
        int size = target.Width / columns, pixels = 0;
        for (int y = row * size; y < (row + 1) * size; y++)
            for (int x = column * size; x < (column + 1) * size; x++)
                if (target.Values[y * target.Width + x] < .999f) pixels++;
        Assert.True(pixels > 0, $"Empty {label} {column}");
    }

    internal static void AssertOutlines(CoverageCapture actual, CoverageCapture expected)
    {
        Assert.NotNull(actual.FullOutline);
        Assert.NotNull(actual.VisibleOutline);
        EqualTarget(actual.FullOutline, expected.FullOutline!, 0, "full outline");
        EqualTarget(actual.VisibleOutline, expected.VisibleOutline!, NormalTolerance, "visible outline");
        int full = actual.FullOutline.Values.Count(v => v > .5f);
        int visible = Enumerable.Range(0, actual.VisibleOutline.Width * actual.VisibleOutline.Height)
            .Count(i => actual.VisibleOutline.Values[i * actual.VisibleOutline.Channels] > .5f);
        Assert.True(full > 0 && visible > 0 && visible < full, $"Outline witnesses full={full} visible={visible}");
    }
}
