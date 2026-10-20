using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;
using static KhaozEngine.Tests.Physics.CertifiedGeneralSweepFixture;

namespace KhaozEngine.Tests.Physics;

public sealed class CertifiedOperationalPruningTests
{
    static readonly Quaternion Cyclic = new(0.5f, 0.5f, 0.5f, 0.5f);

    public static IEnumerable<object[]> CompoundRoutes()
    {
        foreach (string family in new[] { "sphere", "capsule", "cylinder", "box", "hull" })
            foreach (bool view in new[] { false, true })
                foreach (bool dimensions in new[] { false, true })
                    yield return [family, view, dimensions];
    }

    public static IEnumerable<object[]> RootRoutes()
    {
        foreach (string family in new[] { "sphere", "capsule", "cylinder", "box", "hull", "mesh" })
            foreach (bool view in new[] { false, true })
                foreach (bool dimensions in new[] { false, true })
                    yield return [family, view, dimensions];
    }

    [Theory]
    [MemberData(nameof(CompoundRoutes))]
    public void RootChildCancellationCannotPruneOperationalContact(string family, bool view, bool dimensions)
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        var child = new CompoundChild(Shape(family), new(new(-1024, 0, 0), family == "box" ? Cyclic : Quaternion.Identity));
        var handle = world.AddStatic(new CompoundShape([child,
            new(new BoxShape(new(0.25f)), new(new(-1028, 0, 0), Cyclic))]), Pose.At(new(1024, 0, 0)));
        Vector3 centre = new(1.25001f, 0, 0), delta = new(0, 0, 0.125f);
        var geometry = CertifiedCompoundSweepTests.Geometry(world, handle);
        Assert.True(CapsuleSweepGeometry.TryReadLeaf(Sim(world).Shapes, Static(world, handle).Shape,
            new(new(1024, 0, 0), Quaternion.Identity), geometry, 0, out var leaf));
        Assert.True(CapsuleSweepSourceSample.TryCreate(leaf, centre, delta, 0, out var sample));
        // The represented forward gap is positive, but the actual root subtraction and
        // child addition lose it. The local surface is exactly one metre from its origin.
        Assert.True((double)centre.X - 1 > 0.25);
        Assert.Equal(1.25f, family == "box" ? sample.Centre.Z : sample.Centre.X);
        Refused(Direct(leaf, centre, delta));
        Refused(Read(world, centre, delta, length: 0, useDimensions: dimensions,
            exclusions: view ? [Decoy(world)] : null));
    }

    [Theory]
    [MemberData(nameof(RootRoutes))]
    public void OriginalPathCancellationCannotPruneOperationalContact(string family, bool view, bool dimensions)
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        float rootX = family == "mesh" ? -0.25001f : -1.25001f;
        var handle = world.AddStatic(Shape(family), new(new(rootX, 0, 0), family == "box" ? Cyclic : Quaternion.Identity));
        Vector3 centre = new(100_000_000, 0, 0), delta = -centre;
        var geometry = CertifiedCompoundSweepTests.Geometry(world, handle);
        Assert.NotNull(geometry);
        // At the final sample, the rounded root-relative start plus displacement is zero.
        // The forward capsule stays strictly to the right of the installed surface.
        Assert.Equal(0f, (rootX - centre.X) - delta.X);
        Assert.True(rootX + (family == "mesh" ? 0d : 1d) < -0.25);
        Refused(Read(world, centre, delta, length: 0, useDimensions: dimensions,
            exclusions: view ? [Decoy(world)] : null));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void InnerMeshPruningMustRetainRawAndRecenteredOperationalContact(bool view, bool dimensions)
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        var handle = world.AddStatic(new TriangleMeshShape([
            new(0, -2, -2), new(0, 2, -2), new(0, 0, 2),
            new(1, 100, -2), new(1, 104, -2), new(1, 102, 2)], [0, 1, 2, 3, 4, 5]), Pose.At(new(-0.25001f, 0, 0)));
        var mesh = CertifiedCompoundSweepTests.Geometry(world, handle).Mesh!;
        // The far triangle admits the root through the original aperture, but its Y
        // excludes it internally. The near triangle's forward X excludes it internally.
        Assert.True(mesh.RootBounds.Max.X > 0);
        Assert.NotNull(mesh.Triangle(0).SweepCompanion);
        Vector3 centre = new(100_000_000, 0, 0);
        Refused(Read(world, centre, -centre, length: 0, useDimensions: dimensions,
            exclusions: view ? [Decoy(world)] : null));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void UnboundedOperationalEnclosureRetainsCandidatesForLeafRefusal(bool view, bool dimensions)
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        world.AddStatic(new SphereShape(1), Pose.At(new(-1.25001f, 0, 0)));
        Vector3 centre = new(float.MaxValue / 32, 0, 0);
        // The original aperture remains finite, but the operational spacing account
        // reaches the binary32 range boundary. It cannot authorize any exclusion.
        Assert.True(CapsuleSweepAperture.TryCreate(centre, 0.25f, 0, -centre, out _, out _));
        Assert.Equal(0f, (-1.25001f - centre.X) + centre.X);
        Refused(Read(world, centre, -centre, length: 0, useDimensions: dimensions,
            exclusions: view ? [Decoy(world)] : null));
    }

    static CapsuleSweepResult Direct(in InstalledSweepLeaf leaf, Vector3 centre, Vector3 delta)
    {
        var work = new CapsuleSweepWork();
        return CapsuleGeneralSweep.SweepLeaf(leaf, new(0.25f, 0), centre, delta, ref work, new CapsuleSweepScratch());
    }

    static StaticHandle Decoy(BepuPhysicsWorld world) =>
        world.AddStatic(new BoxShape(new(0.25f)), Pose.At(new(0, 10, 0)));

    static PhysicsShape Shape(string family) => family switch
    {
        "sphere" => new SphereShape(1),
        "capsule" => new CapsuleShape(1, 0.000001f),
        "cylinder" => new CylinderShape(1, 0.000001f),
        "box" => new BoxShape(new(1)),
        "hull" => new ConvexHullShape([new(-1, -1, -1), new(-1, -1, 1), new(-1, 1, -1), new(-1, 1, 1),
            new(1, -1, -1), new(1, -1, 1), new(1, 1, -1), new(1, 1, 1)]),
        "mesh" => new TriangleMeshShape([new(0, -2, -2), new(0, 2, -2), new(0, 0, 2)], [0, 1, 2]),
        _ => throw new ArgumentOutOfRangeException(nameof(family)),
    };
}
