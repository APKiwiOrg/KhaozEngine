using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;
using static KhaozEngine.Tests.Physics.CertifiedGeneralSweepFixture;

namespace KhaozEngine.Tests.Physics;

public sealed class CertifiedCurvedSweepTests
{
    [Fact]
    public void NonRigidSphereForwardDepartureContainsIndependentEllipsoidEntry()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        Quaternion q = new(0, 0, 0.5f, 0.8660254f);
        var handle = world.AddStatic(new SphereShape(0.25f), new(Vector3.Zero, q));
        var entry = Static(world, handle);
        Sim(world).Statics.GetDescription(entry.Handle, out var installed);
        Assert.True(InstalledPoseOperator.TryCreate(installed.Pose, out var map));
        Assert.False(map.IsExactlyRigid);
        var geometry = ((Dictionary<int, InstalledSweepGeometry?>)Field(world, "_installedSweepStatics"))[handle.Value]!;
        Assert.True(CapsuleSweepGeometry.TryReadLeaf(Sim(world).Shapes, entry.Shape, installed.Pose, geometry, 0, out var leaf));
        Assert.True(CapsuleCurvedCorrespondence.TryForwardError(leaf, out double departure));
        Assert.True(departure > 0);
        // The exact quaternion polynomial has orthogonal XY columns of squared length
        // 1/4 + w*w. Along X its ellipsoid radius is 1/4 times that square root.
        var squared = InstalledPoseOracle.R.From(0.5f).Square() + InstalledPoseOracle.R.From(q.W).Square();
        var scale = InstalledPoseOracle.Root(squared);
        var r = InstalledPoseOracle.R.From(0.25f);
        var distance = InstalledPoseOracle.R.From(1.75f);
        var result = Read(world, new(-2, 0, 0), new(4, 0, 0), length: 0);
        Assert.Equal(CapsuleSweepStatus.Hit, result.Status);
        Assert.NotNull(result.ImpactDistance);
        Assert.True(InstalledPoseOracle.R.From(result.ClearThroughDistance) <= distance - r * scale.Upper);
        Assert.True(InstalledPoseOracle.R.From(result.ImpactDistance.Value) >= distance - r * scale.Lower);
        Assert.InRange(result.CertifiedErrorMetres, result.ImpactDistance.Value - result.ClearThroughDistance, 0.001f);
        SameBits(result, Read(world, new(-2, 0, 0), new(4, 0, 0), length: 0, useDimensions: true));
    }

    public static IEnumerable<object[]> Rows()
    {
        foreach (string family in new[] { "sphere", "capsule", "cylinder" })
            foreach (float radius in new[] { 0.1f, 0.15f, 0.3f })
                foreach (int turn in new[] { 0, 1, 2 })
                    yield return [family, radius, turn];
    }

    internal static PhysicsShape Shape(string family, float radius, float length = 1) => family switch
    {
        "sphere" => new SphereShape(radius),
        "capsule" => new CapsuleShape(radius, length),
        "cylinder" => new CylinderShape(radius, length),
        _ => throw new ArgumentOutOfRangeException(nameof(family))
    };

    // Exact signed permutations preserve the independent closed boundary fixtures. The real
    // rounded quarter turns are separately exercised by the inherited lying-log installations.
    internal static Quaternion Turn(int turn) => turn switch
    {
        0 => Quaternion.Identity,
        1 => new(0.5f, 0.5f, 0.5f, 0.5f),
        _ => new(-0.5f, -0.5f, -0.5f, 0.5f)
    };

    internal static Pose Centred(string family, float length, Quaternion rotation, Vector3 centre = default) =>
        new(centre - (family == "cylinder" ? Vector3.Transform(new(0, length * 0.5f, 0), rotation) : Vector3.Zero), rotation);

    [Theory]
    [MemberData(nameof(Rows))]
    public void CurvedLeavesSweepMatchesIndependentOracle(string family, float radius, int turn)
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        Quaternion rotation = Turn(turn);
        var handle = world.AddStatic(Shape(family, radius), Centred(family, 1, rotation));
        Vector3 axis = Vector3.Transform(Vector3.UnitY, rotation);
        Vector3 across = axis.X == 0 ? Vector3.UnitX : Vector3.UnitZ;
        foreach (Vector3 direction in new[] { across, axis, Vector3.UnitY })
        {
            Vector3 start = -2 * direction, delta = 4 * direction;
            var oracle = CertifiedSweepExactOracle.CurvedEntry(world, handle, start, 0.25f, delta);
            HitContains(Read(world, start, delta, length: 0), oracle.Lower, oracle.Upper);
            SameBits(Read(world, start, delta, length: 0), Read(world, start, delta, length: 0, useDimensions: true));
        }
        Clear(Read(world, 2 * across, across, length: 0), 1);
        HitContains(Read(world, Vector3.Zero, Vector3.Zero, length: 0), 0, 0);
        HitContains(Read(world, Vector3.Zero, across, length: 0), 0, 0);
        HitContains(Read(world, -2 * across, 4 * across, length: 1),
            2 - (double)radius - 0.25, 2 - (double)radius - 0.25);
    }

    [Theory]
    [InlineData("sphere")]
    [InlineData("capsule")]
    [InlineData("cylinder")]
    public void CurvedClosedTangenciesAndImmediateSeparatedNeighbors(string family)
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        world.AddStatic(Shape(family, 0.25f), Centred(family, 1, Quaternion.Identity));
        HitContains(Read(world, new(0.5f, 0, 0), Vector3.Zero, length: 0), 0, 0);
        HitContains(Read(world, new(0.5f, 0, 0), Vector3.UnitX, length: 0), 0, 0);
        Clear(Read(world, new(MathF.BitIncrement(0.5f), 0, 0), Vector3.Zero, length: 0), 0);
        // An isolated double root at t=1/3, with no negative interval.
        HitContains(Read(world, new(-1, 0, 0.5f), new(3, 0, 0), length: 0), 1, 1);
        HitContains(Read(world, new(-1, 0, 0), new(0.5f, 0, 0), length: 0), 0.5, 0.5);
    }

    [Fact]
    public void CylinderRimAndCapFeaturesAreComplete()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        var handle = world.AddStatic(new CylinderShape(0.5f, 1), Pose.At(new(0, -0.5f, 0)));
        // The first enters only the top cap. The second enters only the circular rim's
        // rounded configuration-space surface, outside both the disc and the finite side.
        foreach ((Vector3 start, Vector3 delta) in new[]
        {
            (new Vector3(0, 2, 0), new Vector3(0, -4, 0)),
            (new Vector3(0.65f, 2, 0), new Vector3(0, -4, 0))
        })
        {
            var oracle = CertifiedSweepExactOracle.CurvedEntry(world, handle, start, 0.25f, delta);
            HitContains(Read(world, start, delta, length: 0), oracle.Lower, oracle.Upper);
        }
        // A Pythagorean rim contact is exact, not a polygon vertex proposal.
        HitContains(Read(world, new(0.875f, 1, 0), Vector3.Zero, radius: 0.625f, length: 0), 0, 0);
    }

    [Theory]
    [InlineData("sphere")]
    [InlineData("capsule")]
    [InlineData("cylinder")]
    public void CurvedLeafRigidCancellationRefuses(string family)
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        world.AddStatic(Shape(family, 1024), Centred(family, 1, Quaternion.Identity, new(1024, 0, 0)));
        Vector3 start = new(-0.25001f, 0, 0);
        Assert.True(-(double)start.X > 0.25);
        Assert.Equal(-1024.25f, start.X - 1024);
        Refused(Read(world, start, new(0, 0, 0.125f), length: 0));
        Refused(Read(world, start, new(0, 0, 0.125f), length: 0, useDimensions: true));
    }

    [Fact]
    public void AnUnprovedIsolatedCircularRimDiscardsAnEarlierHitZero()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        Vector3 start = new(0.875f, 1, -1), delta = new(0, 0, 3);
        world.AddStatic(new BoxShape(new(0.125f)), Pose.At(start));
        HitContains(Read(world, start, delta, radius: 0.625f, length: 0), 0, 0);
        world.AddStatic(new CylinderShape(0.5f, 1), Pose.At(new(0, -0.5f, 0)));
        // The only rim event has radial excess 3/8 and axial excess 1/2, so its
        // distance is exactly 5/8 at t=1/3. No dyadic sampling can witness it.
        Assert.Equal(0.625 * 0.625, 0.375 * 0.375 + 0.5 * 0.5);
        Refused(Read(world, start, delta, radius: 0.625f, length: 0));
    }
}
