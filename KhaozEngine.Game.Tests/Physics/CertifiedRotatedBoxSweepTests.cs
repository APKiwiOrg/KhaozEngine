using System;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;
using static KhaozEngine.Tests.Physics.CertifiedGeneralSweepFixture;

namespace KhaozEngine.Tests.Physics;

[Collection("AllocSensitive")]
public sealed class CertifiedRotatedBoxSweepTests
{
    static readonly Quaternion Cyclic = new(0.5f, 0.5f, 0.5f, 0.5f);

    static BepuPhysicsWorld BoxWorld(Vector3? half = null)
    {
        var world = new BepuPhysicsWorld(Vector3.Zero);
        world.AddStatic(new BoxShape(half ?? new(0.5f, 0.25f, 0.125f)), new Pose(Vector3.Zero, Cyclic));
        return world;
    }

    [Fact]
    public void RotatedBoxFaceEntryHasAContainingMetreBracket()
    {
        using var world = BoxWorld();
        var geometries = (System.Collections.Generic.Dictionary<int, InstalledSweepGeometry?>)Field(world, "_installedSweepStatics");
        InstalledSweepGeometry geometry = geometries[0]!;
        var native = Static(world, new StaticHandle(0));
        Sim(world).Statics.GetDescription(native.Handle, out var installed);
        Assert.True(CapsuleSweepGeometry.TryReadLeaf(Sim(world).Shapes, native.Shape, installed.Pose, geometry, 0, out var leaf));
        var scratch = new CapsuleSweepScratch();
        var work = new CapsuleSweepWork();
        CapsuleSweepResult kernel = CapsuleConvexSweep.Sweep(leaf, new(-2, 0, 0), 0.25f, 0.5f,
            new(4, 0, 0), 0.001f, ref work, scratch);
        HitContains(kernel, 1.625, 1.625);
        Assert.False(work.Exhausted);
        Assert.InRange(work.Cells, 1, 256);
        HitContains(Read(world, new(-2, 0, 0), new(4, 0, 0)), 1.625, 1.625);
    }

    [Fact]
    public void RotatedBoxRoundedCornerIsNotInflatedPlanes()
    {
        using var world = BoxWorld();
        Clear(Read(world, new(0.3125f, 0, 0.4375f), new(0, 0.125f, 0)), 0.125f);
    }

    [Fact]
    public void RotatedBoxClosedStartAndStationaryCases()
    {
        using var world = BoxWorld();
        foreach (Vector3 delta in new[] { Vector3.Zero, Vector3.UnitY, -Vector3.UnitX })
        {
            CapsuleSweepResult contact = Read(world, new(-0.375f, 0, 0), delta);
            Assert.Equal(CapsuleSweepStatus.Hit, contact.Status);
            Assert.Equal(0f, contact.ClearThroughDistance);
            Assert.Equal(0f, contact.ImpactDistance);
            Assert.InRange(contact.CertifiedErrorMetres, 0f, 0.001f);
        }
        Vector3 outside = new(MathF.BitDecrement(-0.375f), 0, 0);
        Clear(Read(world, outside, Vector3.Zero), 0);
        Clear(Read(world, outside, Vector3.UnitY), 1);
        HitContains(Read(world, Vector3.Zero, Vector3.Zero), 0, 0);
    }

    [Fact]
    public void RotatedBoxThinCrossingAndBudgetExhaustionPublishNoPrefix()
    {
        using var world = BoxWorld(new(0.5f, 0.25f, 0.015625f));
        HitContains(Read(world, new(-2, 0, 0), new(4, 0, 0)), 1.734375, 1.734375);
    }

    [Fact]
    public void RotatedBoxOperationalMapNeverBecomesAnExactInverse()
    {
        using var scene = SupportFootprintScenes.HiddenCrate(FootprintPartition.Boxes);
        foreach ((float radius, float length) in new[] { (0.4f, 0.59999996f), (0.40000007f, 0.6000001f) })
            Clear(Read(scene.World, new(0, 1.1010001f, 0), new(0, 0.4f, 0), radius, length, useDimensions: true), 0.4f);
        var native = Static(scene.World, new StaticHandle(2));
        var simulation = Sim(scene.World);
        simulation.Statics.GetDescription(native.Handle, out var installed);
        Assert.Equal(new Quaternion(0, 0, 0.5f, 0.8660254f), installed.Pose.Orientation);
        Assert.Equal(new Vector3(0.28660256f, 0.2f, 0), installed.Pose.Position);
        ref BepuPhysics.Collidables.Box source = ref simulation.Shapes.GetShape<BepuPhysics.Collidables.Box>(native.Shape.Index);
        Vector3 half = new(source.HalfWidth, source.HalfHeight, source.HalfLength);
        var q = installed.Pose.Orientation;
        var matrix = InstalledPoseOracle.Represented(q);
        Assert.NotEqual(InstalledPoseOracle.R.One, InstalledPoseOracle.Norm(q));
        Assert.NotEqual(matrix.Inverse(InstalledPoseOracle.V.UnitX), matrix.Transpose.Apply(InstalledPoseOracle.V.UnitX));
        using var isolated = new BepuPhysicsWorld(Vector3.Zero);
        StaticHandle tilted = isolated.AddStatic(new BoxShape(half), new Pose(installed.Pose.Position, q));
        foreach (float cylinderLength in new[] { 0f, 0.6f })
            foreach (Vector3 local in new[] { new Vector3(0, 0, -1), new Vector3(half.X + 0.03125f, 0, -1),
            new Vector3(half.X + 0.03125f, half.Y + 0.03125f, -1) })
            {
                Vector3 start = installed.Pose.Position + Vector3.Transform(local, q);
                Vector3 motion = new(0, 0, 2);
                var expected = CertifiedSweepExactOracle.BoxEntry(isolated, tilted, start, 0.0625f, cylinderLength * 0.5f, motion);
                CapsuleSweepResult result = Read(isolated, start, motion, 0.0625f, cylinderLength);
                HitContains(result, expected.Lower, expected.Upper);
                using IPhysicsQueryLease lease = isolated.AcquireQueryReadLease();
                var shape = new CapsuleShape(0.0625f, cylinderLength);
                var dimensions = new CapsuleQueryDimensions(0.0625f, cylinderLength);
                Pose query = Pose.At(start);
                SameBits(result, isolated.SweepCapsuleCertified(shape, query, motion));
                SameBits(result, isolated.SweepCapsuleCertified(dimensions, query, motion));
                CapsuleSweepResult shapeResult = default, valueResult = default;
                long bytes = AllocationMeasurement.Measure(nameof(RotatedBoxOperationalMapNeverBecomesAnExactInverse), () =>
                {
                    shapeResult = isolated.SweepCapsuleCertified(shape, query, motion);
                    valueResult = isolated.SweepCapsuleCertified(dimensions, query, motion);
                });
                Assert.Equal(0, bytes);
                SameBits(result, shapeResult);
                SameBits(result, valueResult);
                lease.AssertCurrent();
            }
    }

}
