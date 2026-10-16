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

    internal static void AssertSurface(CoverageCapture baseline, CoverageCapture conformed,
        CoverageCapture oracle, bool requireBreakthrough)
    {
        Assert.Equal(oracle.SubmittedSurfaceSignature, conformed.SubmittedSurfaceSignature);
        int breakthroughs = Breakthroughs(baseline, oracle);
        if (requireBreakthrough) Assert.True(breakthroughs > 0, $"No baseline breakthrough: {baseline.Artifact}");
        Assert.Equal(0, Breakthroughs(conformed, oracle));
        int body = Enumerable.Range(0, conformed.Depth.Values.Length).Count(i => Host(conformed.Colour, i));
        int plate = Enumerable.Range(0, conformed.Depth.Values.Length).Count(i => Equipment(conformed.Colour, i));
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

    internal static void AssertNoGhostColour(CoverageCapture actual, CoverageCapture current)
    {
        int mismatches = 0, witnesses = 0, plateWitnesses = 0, protectedWitnesses = 0, quantizedClassChanges = 0;
        var excluded = new System.Collections.Generic.List<object>();
        for (int y = 1; y < CoverageGpuScene.Height - 1; y++)
            for (int x = 1; x < CoverageGpuScene.Width - 1; x++)
            {
                int i = y * CoverageGpuScene.Width + x;
                int kind = Kind(current.Colour, i);
                bool interior = true;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                        interior &= Kind(current.Colour, i + dy * CoverageGpuScene.Width + dx) == kind;
                int ix = Math.Clamp((int)((x + .5f) * current.Depth.Width / CoverageGpuScene.Width + current.Jitter.X), 0, current.Depth.Width - 1);
                int iy = Math.Clamp((int)((y + .5f) * current.Depth.Height / CoverageGpuScene.Height + current.Jitter.Y), 0, current.Depth.Height - 1);
                bool background = Background(current.Depth.Values[iy * current.Depth.Width + ix]);
                var source = current.ReconstructionColour!;
                int sourceKind = SourceKind(source, ix, iy);
                interior &= sourceKind == kind;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int sampleX = Math.Clamp(ix + dx, 0, current.Depth.Width - 1);
                        int sampleY = Math.Clamp(iy + dy, 0, current.Depth.Height - 1);
                        interior &= Background(current.Depth.Values[sampleY * current.Depth.Width + sampleX]) == background;
                        interior &= SourceKind(source, sampleX, sampleY) == sourceKind;
                    }
                if (!interior)
                {
                    if (Kind(actual.Colour, i) != kind)
                        excluded.Add(new { x, y, ix, iy, expected = kind, actual = Kind(actual.Colour, i) });
                    continue;
                }
                witnesses++;
                if (kind == 2) plateWitnesses++;
                if (kind == 1) protectedWitnesses++;
                if (SignificantClassMismatch(actual.Colour, current.Colour, i)) mismatches++;
                else if (Kind(actual.Colour, i) != kind) quantizedClassChanges++;
            }
        System.IO.File.WriteAllText(System.IO.Path.Combine(CoverageArtifacts.DirectoryPath(),
            $"coverage-edge-witnesses-{Guid.NewGuid():N}.json"), System.Text.Json.JsonSerializer.Serialize(new
            { actual.Artifact, Current = current.Artifact, witnesses, plateWitnesses, protectedWitnesses, quantizedClassChanges, Excluded = excluded }));
        Assert.True(witnesses > 100);
        if (!current.Artifact.Contains("-gone")) Assert.True(protectedWitnesses > 100, $"No protected colour interior: {current.Artifact}");
        if (current.Artifact.Contains("-equip") || current.Artifact.Contains("-steady")
            || current.Artifact.Contains("-blended-motion") || current.Artifact.Contains("-replacement")
            || current.Artifact.Contains("-return") || current.Artifact.Contains("-reequip") || current.Artifact.Contains("-reset"))
            Assert.True(plateWitnesses > 100, $"No interior plate colour proof: {current.Artifact}");
        Assert.True(mismatches == 0, $"Ghost colour witnesses {mismatches}: {actual.Artifact}");
    }

    internal static bool SignificantClassMismatch(byte[] actual, byte[] current, int pixel)
    {
        if (Kind(actual, pixel) == Kind(current, pixel)) return false;
        for (int c = 0; c < 3; c++)
            if (Math.Abs(actual[pixel * 4 + c] - current[pixel * 4 + c]) > 1) return true;
        return false;
    }

    internal static void AssertMixedReconstructionFootprint(CoverageCapture capture, int x, int y)
    {
        var source = capture.ReconstructionColour!;
        int ix = Math.Clamp((int)((x + .5f) * source.Width / CoverageGpuScene.Width + capture.Jitter.X), 0, source.Width - 1);
        int iy = Math.Clamp((int)((y + .5f) * source.Height / CoverageGpuScene.Height + capture.Jitter.Y), 0, source.Height - 1);
        int centre = SourceKind(source, ix, iy), different = 0;
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
                if (SourceKind(source, Math.Clamp(ix + dx, 0, source.Width - 1), Math.Clamp(iy + dy, 0, source.Height - 1)) != centre) different++;
        Assert.True(different > 0, $"Previously failing coordinate is not a mixed source footprint: {x},{y}");
    }

    static int SourceKind(CoverageTargetReadback source, int x, int y)
    {
        int i = (y * source.Width + x) * source.Channels;
        return source.Values[i] > source.Values[i + 2] * 1.4f ? 1
            : source.Values[i + 2] > source.Values[i] * 2f ? 2 : 0;
    }

    static int Kind(byte[] colour, int i) => Host(colour, i) ? 1 : Equipment(colour, i) ? 2 : 0;
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
