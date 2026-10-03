using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;
using static KhaozEngine.Tests.Movement.GroundNavigationBakeRoundTripTests;

namespace KhaozEngine.Tests.Movement;

public class WaterCaptureTests
{
    // Four by two cells of 1 m. Centres x -1.5, -0.5, 0.5, 1.5 and z -0.5, 0.5. The probe floor is 5 - 6 = -1.
    private static readonly PhysicsNavBakeOptions Options = new(-2f, -1f, 2f, 1f, 1f, 5f, 6f, 0.8f, 64, 256);
    private static readonly PhysicsNavBakeOptions Wet = Options with { SampleWater = true };
    private static readonly int[] WetCells = [2, 3, 6, 7];

    /// <summary>Water with its surface at 1.5 m over x above zero, dry land elsewhere.</summary>
    internal static MovementMedium Pool(float x, float z, float feetY) =>
        x > 0f ? new MovementMedium(1.5f, feetY < 1.5f) : MovementMedium.Dry;

    private static uint Classify(Vector3 feet) => feet.Y > 1f ? 0x04u : 0x01u;

    [Fact]
    public void SampleWaterIsOffByDefault()
    {
        Assert.False(new PhysicsNavBakeOptions(-2f, -1f, 2f, 1f, 1f, 5f, 6f, 0.8f, 64, 256).SampleWater);
        Assert.True(Wet.SampleWater);
        Assert.NotEqual(Options, Wet);
    }

    [Fact]
    public void MediumWithoutTheOptInCapturesIdenticalOutput()
    {
        using BepuPhysicsWorld world = GroundTraversalProbeTests.FlatWorld();
        int calls = 0;
        var plain = new GroundMoveContext((_, _) => 0f, physics: world);
        var withMedium = new GroundMoveContext((_, _) => 0f, physics: world, medium: (x, z, feetY) =>
        {
            calls++;
            return Pool(x, z, feetY);
        });
        NavBakeProfile[] profiles = [new("player", GroundTraversalProbeTests.Tuning, default)];

        using PhysicsNavBake a = PhysicsNavBake.Capture(plain, Options, Classify);
        using PhysicsNavBake b = PhysicsNavBake.Capture(withMedium, Options, Classify);

        Assert.Equal(0, calls);
        Assert.True(b.Columns.Water.IsEmpty);
        Assert.Equal(a.Columns.SurfaceCount, b.Columns.SurfaceCount);
        for (int z = 0; z < a.Columns.Height; z++)
            for (int x = 0; x < a.Columns.Width; x++)
            {
                ReadOnlySpan<PhysicsNavSurface> ca = a.Columns.GetColumn(x, z), cb = b.Columns.GetColumn(x, z);
                Assert.Equal(ca.Length, cb.Length);
                for (int i = 0; i < ca.Length; i++)
                {
                    Assert.Equal(BitConverter.SingleToUInt32Bits(ca[i].Height), BitConverter.SingleToUInt32Bits(cb[i].Height));
                    Assert.Equal(BitConverter.SingleToUInt32Bits(ca[i].Headroom), BitConverter.SingleToUInt32Bits(cb[i].Headroom));
                    Assert.Equal(ca[i].Areas, cb[i].Areas);
                }
            }
        byte[] plainFile = Write(GroundNavigationBake.Create(a, Sources(), profiles));
        byte[] mediumFile = Write(GroundNavigationBake.Create(b, Sources(), profiles));
        Assert.Equal(plainFile, mediumFile);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void SampleWaterWithoutAMediumIsRefused()
    {
        using BepuPhysicsWorld world = GroundTraversalProbeTests.FlatWorld();
        var context = new GroundMoveContext((_, _) => 0f, physics: world);

        ArgumentException refused = Assert.Throws<ArgumentException>(() => PhysicsNavBake.Capture(context, Wet, Classify));
        Assert.Equal("context", refused.ParamName);
    }

    [Fact]
    public void WetColumnsRecordTheSurfaceAndClassifierAreas()
    {
        using BepuPhysicsWorld world = GroundTraversalProbeTests.FlatWorld();
        var classified = new List<Vector3>();
        using PhysicsNavBake bake = PhysicsNavBake.Capture(Context(world, out _), Wet, feet =>
        {
            classified.Add(feet);
            return Classify(feet);
        });

        PhysicsNavWater[] water = bake.Columns.Water.ToArray();
        Assert.Equal(WetCells, water.Select(w => w.Cell));
        Assert.All(water, w =>
        {
            Assert.Equal(1.5f, w.SurfaceY);
            Assert.Equal(0x04u, w.Areas);
        });
        Vector3[] waterPoints = [.. classified.Where(p => p.Y == 1.5f)];
        Assert.Equal(new Vector3[] { new(0.5f, 1.5f, -0.5f), new(1.5f, 1.5f, -0.5f), new(0.5f, 1.5f, 0.5f), new(1.5f, 1.5f, 0.5f) },
            waterPoints);
        Assert.Equal(8 + 4, classified.Count);
        for (int z = 0; z < 2; z++)
            for (int x = 0; x < 4; x++)
                Assert.Equal(0x01u, bake.Columns.GetColumn(x, z)[0].Areas);
    }

    [Fact]
    public void MediumIsSampledAtTheLowestSurface()
    {
        using BepuPhysicsWorld world = GroundTraversalProbeTests.FlatWorld();
        world.AddStatic(new BoxShape(new Vector3(4f, 0.1f, 4f)), Pose.At(new Vector3(0f, 2.9f, 0f)));
        using PhysicsNavBake bake = PhysicsNavBake.Capture(Context(world, out List<Vector3> sampled), Wet, Classify);

        Assert.Equal(8, sampled.Count);
        for (int z = 0; z < 2; z++)
            for (int x = 0; x < 4; x++)
            {
                ReadOnlySpan<PhysicsNavSurface> column = bake.Columns.GetColumn(x, z);
                Assert.Equal(2, column.Length);
                Assert.InRange(column[1].Height, 2.99f, 3.01f);
                Vector3 sample = sampled[z * 4 + x];
                Assert.Equal(new Vector2(-1.5f + x, -0.5f + z), new Vector2(sample.X, sample.Z));
                Assert.Equal(BitConverter.SingleToUInt32Bits(column[0].Height), BitConverter.SingleToUInt32Bits(sample.Y));
            }
        PhysicsNavWater[] water = bake.Columns.Water.ToArray();
        Assert.Equal(WetCells, water.Select(w => w.Cell));
        Assert.All(water, w => Assert.Equal(1.5f, w.SurfaceY));
    }

    [Fact]
    public void EmptyColumnSamplesTheProbeFloor()
    {
        using var world = new BepuPhysicsWorld();
        // Floor over x in [-2, 1], so the column at x 1.5 has no surface.
        world.AddStatic(new BoxShape(new Vector3(1.5f, 0.1f, 8f)), Pose.At(new Vector3(-0.5f, -0.1f, 0f)));
        using PhysicsNavBake bake = PhysicsNavBake.Capture(Context(world, out List<Vector3> sampled), Wet, Classify);

        Assert.True(bake.Columns.GetColumn(3, 0).IsEmpty);
        Assert.True(bake.Columns.GetColumn(3, 1).IsEmpty);
        Assert.Equal(Wet.ProbeHeight - Wet.ProbeRange, sampled[3].Y);
        Assert.Equal(Wet.ProbeHeight - Wet.ProbeRange, sampled[7].Y);
        PhysicsNavWater[] water = bake.Columns.Water.ToArray();
        Assert.Equal(WetCells, water.Select(w => w.Cell));
        Assert.All(water, w => Assert.Equal(1.5f, w.SurfaceY));
    }

    [Fact]
    public void WaterSectionRoundTrips()
    {
        Baked pool = Bake("pool");

        GroundNavigationBake loaded = LoadOk(pool.File, pool.Expected);

        PhysicsNavWater[] fresh = pool.Bake.GetProfile("player").Footprint.Columns.Water.ToArray();
        PhysicsNavWater[] read = loaded.GetProfile("player").Footprint.Columns.Water.ToArray();
        Assert.Equal(WetCells, fresh.Select(w => w.Cell));
        Assert.Equal(fresh.Select(Bits), read.Select(Bits));
        Assert.Equal(pool.File, Write(loaded));
    }

    private static (int, uint, uint) Bits(PhysicsNavWater water) =>
        (water.Cell, BitConverter.SingleToUInt32Bits(water.SurfaceY), water.Areas);

    private static GroundMoveContext Context(IPhysicsWorld world, out List<Vector3> sampled)
    {
        var samples = new List<Vector3>();
        sampled = samples;
        return new GroundMoveContext((_, _) => 0f, physics: world, medium: (x, z, feetY) =>
        {
            samples.Add(new Vector3(x, feetY, z));
            return Pool(x, z, feetY);
        });
    }
}
