using System;
using System.Collections.Generic;
using System.Numerics;
using BepuPhysics.Collidables;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;
using CompoundChild = KhaozEngine.Physics.CompoundChild;
using static KhaozEngine.Tests.Physics.CertifiedGeneralSweepFixture;

namespace KhaozEngine.Tests.Physics;

public sealed class CertifiedCompoundSweepTests
{
    internal static readonly Quaternion Cyclic = new(0.5f, 0.5f, 0.5f, 0.5f);
    internal static readonly BoxShape Box = new(new(0.5f, 0.25f, 0.125f));
    internal static CompoundChild[] Pair() => [new(Box, new(Vector3.Zero, Cyclic)),
        new(CertifiedHullSweepTests.Tetrahedron(), Pose.At(new(2, 0, 0)))];
    internal static InstalledSweepGeometry Geometry(BepuPhysicsWorld world, StaticHandle handle) =>
        ((Dictionary<int, InstalledSweepGeometry?>)Field(world, "_installedSweepStatics"))[handle.Value]!;

    [Fact]
    public void CompoundDescendantsMatchSplitInstalledGeometry()
    {
        using var split = new BepuPhysicsWorld(Vector3.Zero);
        using var compound = new BepuPhysicsWorld(Vector3.Zero);
        using var reversed = new BepuPhysicsWorld(Vector3.Zero);
        var children = Pair();
        foreach (var child in children) split.AddStatic(child.Shape, child.Local);
        compound.AddStatic(new CompoundShape(children), Pose.Identity);
        Array.Reverse(children);
        reversed.AddStatic(new CompoundShape(children), Pose.Identity);
        CapsuleSweepResult expected = Read(split, new(-2, 0, 0), new(8, 0, 0));
        HitContains(expected, 1.625, 1.625);
        SameBits(expected, Read(compound, new(-2, 0, 0), new(8, 0, 0)));
        SameBits(expected, Read(reversed, new(-2, 0, 0), new(8, 0, 0)));
    }

    [Fact]
    public void SupportedHullAndBoxCompoundAtRemoteRootCertifiesCompleteHit()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        world.AddStatic(CertifiedHullSweepTests.Tetrahedron(), Pose.Identity);
        Vector3 start = new(-2, 0.75f, 0.25f), delta = new(4, 0, 0);
        var expected = Read(world, start, delta);
        HitContains(expected, 1.75, 1.75);
        world.AddStatic(new CompoundShape([new(CertifiedHullSweepTests.Tetrahedron(), Pose.Identity),
            new(new BoxShape(new(0.5f)), Pose.At(new(3, 0, 0)))]), Pose.At(new(100, 0, 0)));
        SameBits(expected, Read(world, start, delta));
        HitContains(Read(world, start + new Vector3(100, 0, 0), delta), 1.75, 1.75);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NestedSeamCompoundUsesActualFlattenedChildPoses(bool rigid)
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        using var split = new BepuPhysicsWorld(Vector3.Zero);
        Pose parent = new(new(3, 2, -4), Cyclic);
        Pose local = new(new(0.25f, 0.5f, -0.125f), rigid ? Cyclic : new(0, 0, 0.5f, 0.8660254f));
        var inner = new CompoundShape([new(Box, Pose.Identity),
            new(CertifiedHullSweepTests.Tetrahedron(), Pose.At(new(10, 0, 0)))]);
        var handle = world.AddStatic(new CompoundShape([new(inner, local)]), parent);
        var root = Static(world, handle).Shape;
        ref Compound installed = ref Sim(world).Shapes.GetShape<Compound>(root.Index);
        Assert.Equal(2, installed.Children.Length);
        Assert.Equal(local.Position, installed.Children[0].LocalPose.Position);
        Assert.Equal(local.Orientation, installed.Children[0].LocalPose.Orientation);
        // The installer composes the nested seam pose first, then adds the hull's native centroid once.
        Pose hullLocal = local.Compose(Pose.At(new(10, 0, 0)));
        Vector3 expectedHullOffset = hullLocal.Position + Vector3.Transform(new Vector3(0.5f), hullLocal.Orientation);
        Assert.Equal(expectedHullOffset, installed.Children[1].LocalPose.Position);
        Assert.Equal(hullLocal.Orientation, installed.Children[1].LocalPose.Orientation);
        Pose boxWorld = parent.Compose(local);
        StaticHandle box = split.AddStatic(Box, boxWorld);
        split.AddStatic(CertifiedHullSweepTests.Tetrahedron(), parent.Compose(hullLocal));
        Vector3 start = boxWorld.Position + new Vector3(-2, 0, 0), delta = new(4, 0, 0);
        var oracle = CertifiedSweepExactOracle.BoxEntry(split, box, start, 0.25f, 0, delta);
        var result = Read(world, start, delta, length: 0);
        if (rigid)
        {
            // Two cyclic rotations put the box's 0.25 m Y half extent on world X.
            // The rational oracle's outward conversion may extend beyond this exact root.
            Assert.True(oracle.Lower <= 1.5 && 1.5 <= oracle.Upper);
            HitContains(result, 1.5, 1.5);
        }
        else
        {
            Assert.InRange(oracle.Lower, 1.624999, 1.625001);
            Assert.InRange(oracle.Upper, 1.624999, 1.625001);
            // The non-rigid map's enclosed tangent face reaches depth 48 without a witness.
            // This is a leaf proof refusal, after composition and bounds have succeeded.
            var geometry = Geometry(world, handle);
            Assert.True(InstalledSweepBounds.TryCreate(geometry, new(parent.Position, parent.Orientation), out _, out _));
            Assert.True(CapsuleSweepGeometry.TryReadLeaf(Sim(world).Shapes, root,
                new(parent.Position, parent.Orientation), geometry, 0, out var leaf));
            Assert.False(leaf.Map.IsExactlyRigid);
            var work = new CapsuleSweepWork();
            Refused(CapsuleGeneralSweep.SweepLeaf(leaf, new(0.25f, 0), start, delta, ref work, new CapsuleSweepScratch()));
            Refused(result);
        }
        Assert.NotNull(Geometry(world, handle));
    }

    [Fact]
    public void CompoundHitZeroCannotHideARefusedDescendant()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        var supported = new CompoundShape([new(Box, new(Vector3.Zero, Cyclic)), new(new SphereShape(0.5f), Pose.At(new(100, 0, 0)))]);
        var handle = world.AddStatic(supported, Pose.Identity);
        HitContains(Read(world, Vector3.Zero, Vector3.UnitX), 0, 0);
        var entry = Static(world, handle);
        var geometry = Geometry(world, handle);
        Sim(world).Statics.GetDescription(entry.Handle, out var installed);
        var scratch = new CapsuleSweepScratch();
        var work = new CapsuleSweepWork { Descendants = CapsuleGeneralSweepPolicy.MaximumDescendants - 1 };
        Refused(CapsuleGeneralSweep.Sweep(Sim(world).Shapes, entry.Shape, installed.Pose, geometry,
            new(0.25f, 1), Vector3.Zero, Vector3.UnitX, default, ref work, scratch));
        Assert.Equal(CapsuleGeneralSweepPolicy.MaximumDescendants, work.Descendants);
        Assert.True(work.Exhausted);
        world.RemoveStatic(handle);
        var unsupported = new CompoundShape([new(Box, new(Vector3.Zero, Cyclic)),
            new(OverFaceCapHull(), Pose.At(new(100, 0, 0)))]);
        handle = world.AddStatic(unsupported, Pose.Identity);
        ref Compound root = ref Sim(world).Shapes.GetShape<Compound>(Static(world, handle).Shape.Index);
        Assert.Equal(257, Sim(world).Shapes.GetShape<ConvexHull>(root.Children[1].ShapeIndex.Index).FaceToVertexIndicesStart.Length);
        Refused(Read(world, Vector3.Zero, Vector3.UnitX));
    }

    [Fact]
    public void CompoundCapacityIsAtomicAtSixtyFiveChildren()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        CompoundChild[] children = new CompoundChild[64];
        for (int i = 0; i < children.Length; i++) children[i] = new(Box, new(Vector3.Zero, Cyclic));
        var handle = world.AddStatic(new CompoundShape(children), Pose.Identity);
        HitContains(Read(world, Vector3.Zero, Vector3.Zero), 0, 0);
        var entry = Static(world, handle);
        Sim(world).Statics.GetDescription(entry.Handle, out var installed);
        var scratch = new CapsuleSweepScratch();
        var work = new CapsuleSweepWork { Descendants = CapsuleGeneralSweepPolicy.MaximumDescendants - 64 };
        HitContains(CapsuleGeneralSweep.Sweep(Sim(world).Shapes, entry.Shape, installed.Pose, Geometry(world, handle),
            new(0.25f, 1), Vector3.Zero, Vector3.Zero, default, ref work, scratch), 0, 0);
        Assert.Equal(4096, work.Descendants);
        Assert.False(work.Exhausted);
        // The same whole-query account is passed to a second installed root.
        var other = world.AddStatic(new CompoundShape(Pair()), Pose.Identity);
        var otherEntry = Static(world, other);
        Sim(world).Statics.GetDescription(otherEntry.Handle, out var otherInstalled);
        Refused(CapsuleGeneralSweep.Sweep(Sim(world).Shapes, otherEntry.Shape, otherInstalled.Pose, Geometry(world, other),
            new(0.25f, 1), Vector3.Zero, Vector3.Zero, default, ref work, scratch));
        Assert.True(work.Exhausted);
        Assert.Equal(4096, work.Descendants);
        world.RemoveStatic(handle);
        Array.Resize(ref children, 65);
        children[64] = new(Box, new(Vector3.Zero, Cyclic));
        world.AddStatic(new CompoundShape(children), Pose.Identity);
        Refused(Read(world, Vector3.Zero, Vector3.Zero));
    }

    [Fact]
    public void CompoundMeshChildRemainsAnInstallationRefusal()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        var mesh = new TriangleMeshShape([Vector3.Zero, Vector3.UnitX, Vector3.UnitZ], [0, 1, 2]);
        Assert.Throws<NotSupportedException>(() => world.AddStatic(new CompoundShape([new(mesh, Pose.Identity)]), Pose.Identity));
    }

    [Fact]
    public void CompoundBoundsCannotPruneARequiredRemoteChild()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        var handle = world.AddStatic(new CompoundShape([new(Box, new(Vector3.Zero, Cyclic)),
            new(Box, new(new(100, 0, 0), Cyclic))]), Pose.Identity);
        HitContains(Read(world, new(98, 0, 0), new(4, 0, 0)), 1.625, 1.625);
        var geometry = Geometry(world, handle);
        Assert.True(InstalledSweepBounds.TryCreate(geometry, new(Vector3.Zero, Quaternion.Identity), out var lo, out var hi));
        Assert.True(lo.X <= -0.125f && hi.X >= 100.125f);
        Mutate(world, sim =>
        {
            var native = Static(world, handle);
            int index = sim.Statics[sim.Statics.HandleToIndex[native.Handle.Value]].BroadPhaseIndex;
            sim.BroadPhase.UpdateStaticBounds(index, new(-0.125f, -0.5f, -0.25f), new(0.125f, 0.5f, 0.25f));
        }, handle.Value);
        HitContains(Read(world, new(98, 0, 0), new(4, 0, 0)), 1.625, 1.625);
        Mutate(world, sim =>
        {
            var native = Static(world, handle);
            int index = sim.Statics[sim.Statics.HandleToIndex[native.Handle.Value]].BroadPhaseIndex;
            sim.BroadPhase.UpdateStaticBounds(index, new(float.NaN), new(101));
        }, handle.Value);
        Refused(Read(world, new(98, 0, 0), new(4, 0, 0)));
    }

    [Fact]
    public void CompoundClosedStationaryAndTangentialEventsMatchSplitLeaves()
    {
        foreach (int representation in new[] { 0, 1, 2 })
        {
            using var world = new BepuPhysicsWorld(Vector3.Zero);
            var children = Pair();
            children[1] = children[1] with { Local = Pose.At(new(100, 0, 0)) };
            if (representation == 0) foreach (var child in children) world.AddStatic(child.Shape, child.Local);
            else
            {
                if (representation == 2) Array.Reverse(children);
                world.AddStatic(new CompoundShape(children), Pose.Identity);
            }
            foreach (Vector3 delta in new[] { Vector3.Zero, Vector3.UnitY, -Vector3.UnitX })
                HitContains(Read(world, new(-0.375f, 0, 0), delta), 0, 0);
            Clear(Read(world, new(MathF.BitDecrement(-0.375f), 0, 0), new(0, 0.125f, 0)), 0.125f);
            HitContains(Read(world, new(-1.375f, 0, 0), Vector3.UnitX), 1, 1);
            HitContains(Read(world, Vector3.Zero, Vector3.Zero), 0, 0);
        }
    }

    [Theory]
    [InlineData("box")]
    [InlineData("hull")]
    [InlineData("sphere")]
    [InlineData("capsule")]
    [InlineData("cylinder")]
    public void CompoundChildrenRetainTheirLeafProofAndRigidCancellationRefusal(string family)
    {
        using var split = new BepuPhysicsWorld(Vector3.Zero);
        using var compound = new BepuPhysicsWorld(Vector3.Zero);
        var shape = CancellationShape(family, 0.25f);
        Pose child = CertifiedCurvedSweepTests.Centred(family, 1, Cyclic);
        split.AddStatic(shape, child);
        var handle = compound.AddStatic(new CompoundShape([new(shape, child), new(Box, Pose.At(new(100, 0, 0)))]), Pose.Identity);
        var expected = Read(split, new(-2, 0, 0), new(4, 0, 0), length: 0);
        HitContains(expected, 1.5, 1.5);
        SameBits(expected, Read(compound, new(-2, 0, 0), new(4, 0, 0), length: 0));
        compound.RemoveStatic(handle);
        Pose large = CertifiedCurvedSweepTests.Centred(family, 1, Quaternion.Identity, new(1024, 0, 0));
        compound.AddStatic(new CompoundShape([new(CancellationShape(family, 1024), large),
            new(Box, Pose.At(new(100, 0, 0)))]), Pose.Identity);
        Refused(Read(compound, new(-0.25001f, 0, 0), new(0, 0, 0.125f), length: 0));
    }

    static PhysicsShape CancellationShape(string family, float radius)
    {
        if (family == "box") return new BoxShape(new(radius));
        if (family != "hull") return CertifiedCurvedSweepTests.Shape(family, radius);
        var points = new Vector3[8];
        for (int i = 0; i < points.Length; i++)
            points[i] = new((i & 1) == 0 ? -radius : radius, (i & 2) == 0 ? -radius : radius,
                (i & 4) == 0 ? -radius : radius);
        return new ConvexHullShape(points);
    }

}
