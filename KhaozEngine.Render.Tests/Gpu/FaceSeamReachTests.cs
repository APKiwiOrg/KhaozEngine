using System;
using System.IO;
using System.IO.Compression;
using System.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

/// <summary>
/// The cube-face seam measurement on retained captures, headless, so both of its claims hold on every leg and
/// not only where the GPU case runs.
/// <para>
/// Each fixture is the red channel of the unshadowed capture followed by the shadowed one, 192 by 192, as the
/// GPU case renders them. <c>warp-shipped</c> is the shipped Soft filter on the Direct3D 11 WARP rasterizer, the
/// capture whose row of point probes read a -8.7 mm crossing bump and failed the old form of the case.
/// <c>metal-clamped-face</c> is the same scene on Metal with <c>clamped-face-mutant.patch</c> applied, which
/// projects every blocker-search and filter tap onto the receiver's own face and clamps it inside that face's
/// cell: the seam the GPU case exists to catch.
/// </para>
/// </summary>
public sealed class FaceSeamReachTests(ITestOutputHelper output)
{
    const int Size = 192;

    /// <summary>The fixture's iso camera after the GPU case frames (3.6, 0, 5.4) at a half extent of 1.8, as
    /// recorded with both captures. Orthographic, so world x and z map straight onto picture x and y.</summary>
    static Vector2 Camera(Vector3 world) => new(-96f + 53.333336f * world.X, -185.00826f + 52.03858f * world.Z);

    static FaceSeamReach.Measurement Replay(string name)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "point-shadow-seam", name + ".red.gz");
        byte[] red;
        using (var gz = new GZipStream(File.OpenRead(path), CompressionMode.Decompress))
        using (var all = new MemoryStream())
        {
            gz.CopyTo(all);
            red = all.ToArray();
        }
        Assert.Equal(2 * Size * Size, red.Length);
        return FaceSeamReach.Measure(Size, Size, Camera, (x, y) =>
        {
            int lit = red[y * Size + x];
            Assert.True(lit > 24, $"the unshadowed capture reads {lit} at pixel ({x}, {y})");
            return red[Size * Size + y * Size + x] / (float)lit;
        });
    }

    [Fact]
    public void TheShippedFilterOnWarpShowsNoStepAtTheCrossing()
    {
        FaceSeamReach.Measurement seam = Replay("warp-shipped");
        output.WriteLine(seam.Describe());
        Assert.True(Math.Abs(seam.Bump) <= FaceSeamReach.Bound,
            $"the retained WARP capture of the shipped filter reads {seam.Describe()}");
    }

    [Fact]
    public void AKernelClampedInsideItsOwnFaceFailsTheBound()
    {
        FaceSeamReach.Measurement seam = Replay("metal-clamped-face");
        output.WriteLine(seam.Describe());
        Assert.True(Math.Abs(seam.Bump) > FaceSeamReach.Bound,
            $"the clamped-face control reads {seam.Describe()}, inside the bound, so the GPU case could not tell "
            + "a seamed filter from a clean one");
    }

    /// <summary>A seam three stations wide on a drifting edge reads at its full height, and the rest of the edge
    /// reads flat, because the line it is measured against never saw it.</summary>
    [Fact]
    public void TheTrendIsFittedAwayFromTheCrossing()
    {
        var reach = new float[FaceSeamReach.Stations];
        for (int s = 0; s < reach.Length; s++)
            reach[s] = 0.9f - 0.002f * s + (s is >= FaceSeamReach.FirstCrossing
                and < FaceSeamReach.FirstCrossing + FaceSeamReach.CrossingCount ? 0.02f : 0f);

        FaceSeamReach.Measurement seam = FaceSeamReach.Crossing(reach);

        Assert.Equal(0.02, seam.Bump, 5);
        Assert.True(seam.WorstElsewhere < 1e-5f, $"the stations away from the seam read {seam.Describe()}");
    }
}
