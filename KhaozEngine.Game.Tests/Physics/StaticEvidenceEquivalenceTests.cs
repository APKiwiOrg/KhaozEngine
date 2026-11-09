using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Physics;

public class StaticEvidenceEquivalenceTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(64, false)]
    [InlineData(64, true)]
    public void SpatialCaptureMatchesFullWalkStatusPrefixOrderAndEveryCoordinateBit(int origin, bool exclude)
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        var excluded = new HashSet<StaticHandle>();
        var random = new Random(1753);
        for (int i = 0; i < 40; i++)
        {
            PhysicsShape shape = (i % 6) switch
            {
                0 => StaticEvidenceSpatialTests.Strip(32),
                1 => new BoxShape(new(1, 3, 1)),
                2 => new SphereShape(0.5f),
                3 => new CapsuleShape(0.25f, 1),
                4 => new CylinderShape(0.5f, 1),
                _ => new CompoundShape([new(new BoxShape(Vector3.One), Pose.Identity),
                    new(new SphereShape(0.25f), Pose.At(new(3, 0, 0)))])
            };
            Vector3 position = new(random.Next(-4, 5), random.Next(-1, 2), random.Next(-4, 5));
            Quaternion rotation = i % 3 == 0 ? new(0.5f, 0.5f, 0.5f, 0.5f) : Quaternion.Identity;
            var handle = world.AddStatic(shape, new(position, rotation));
            if (i % 3 == 0 && exclude) excluded.Add(handle);
        }
        world.Rebase(new(origin, 0, origin));
        using var view = world.CreateQueryViewExcludingStatics([.. excluded]);
        using var lease = world.AcquireQueryReadLease();
        var complete = Capture(world, world, lease, new(-200), new(200), 4096, int.MaxValue, [], true);
        Assert.Equal(PhysicsEvidenceStatus.Complete, complete.Status);
        Assert.NotEmpty(complete.Bits);
        for (int sample = 0; sample < 48; sample++)
        {
            Vector3 centre = new(random.Next(-5, 6) - origin, 0, random.Next(-5, 6) - origin);
            Vector3 extent = sample % 4 == 0 ? Vector3.Zero : new(0.5f + sample % 5, 1, 0.5f + sample % 3);
            foreach (int limit in new[] { 1, 7, 4096 })
                foreach (int stop in new[] { 1, int.MaxValue })
                    Compare(world, (IPhysicsStaticEvidence)view, lease, centre - extent, centre + extent,
                        limit, stop, excluded);
        }
    }

    [Fact]
    public void ExcludedFacesStillConsumeTheHistoricalFeatureBudget()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        var ground = world.AddStatic(StaticEvidenceSpatialTests.Strip(32), Pose.Identity);
        using var view = world.CreateQueryViewExcludingStatics([ground]);
        using var lease = world.AcquireQueryReadLease();
        var actual = Capture((IPhysicsStaticEvidence)view, world, lease, new(-1), new(100), 1, int.MaxValue, [], false);
        Assert.Equal(PhysicsEvidenceStatus.CapacityExceeded, actual.Status);
        Assert.Empty(actual.Bits);
        Compare(world, (IPhysicsStaticEvidence)view, lease, new(-1), new(100), 1, int.MaxValue, [ground]);
    }

    [Fact]
    public void PublicationsRemovalHandleReuseAndUnpublishedChangesCannotReuseOldEvidence()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        var original = world.AddStatic(StaticEvidenceSpatialTests.Strip(32), Pose.Identity);
        using var view = world.CreateQueryViewExcludingStatics([original]);
        Check();
        world.RemoveStatic(original);
        var replacement = world.AddStatic(new BoxShape(Vector3.One), Pose.Identity);
        Check();
        world.Rebase(new(32, 0, 32));
        Check();
        world.AdvanceUnhandledGenerationForTests();
        Check();
        var capture = world.InstalledSweepStaticsForTests[replacement.Value];
        world.ReplaceStaticSweepCaptureForTests(replacement, null);
        using (var lease = world.AcquireQueryReadLease())
        {
            var actual = Capture((IPhysicsStaticEvidence)view, world, lease, new(-100), new(100), 64,
                int.MaxValue, [], false);
            Assert.Equal(PhysicsEvidenceStatus.Unsupported, actual.Status);
            Compare(world, (IPhysicsStaticEvidence)view, lease, new(-100), new(100), 64, int.MaxValue, [original]);
        }
        world.ReplaceStaticSweepCaptureForTests(replacement, capture);
        void Check()
        {
            using var lease = world.AcquireQueryReadLease();
            Compare(world, (IPhysicsStaticEvidence)view, lease, new(-100), new(100), 4096, int.MaxValue, [original]);
            var selected = Capture((IPhysicsStaticEvidence)view, world, lease, new(-100), new(100), 4096,
                int.MaxValue, [], false);
            if (world.StaticHandlesForTests.ContainsKey(original.Value)) Assert.Empty(selected.Bits);
            else Assert.NotEmpty(selected.Bits);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnresolvedVerticesOutsideTheApertureRetainTheirRefusalEvenWhenExcluded(bool exclude)
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        // 512 + 2^-48 needs 58 binary digits, so its exact represented coordinate cannot fit binary64.
        // The operand is a normal binary32 value. This does not rely on cancellation or subnormal modes.
        float tiny = MathF.ScaleB(1, -48);
        var mesh = new TriangleMeshShape([new(tiny, 0, 0), new(1, 0, 0), new(0, 0, 1),
            new(10, 0, 0), new(11, 0, 0), new(10, 0, 1)], [0, 1, 2, 3, 4, 5]);
        var handle = world.AddStatic(mesh, Pose.At(new(512, 0, 0)));
        HashSet<StaticHandle> excluded = exclude ? [handle] : [];
        using var view = world.CreateQueryViewExcludingStatics([.. excluded]);
        using var lease = world.AcquireQueryReadLease();
        Vector3 min = new(522.25f, -1, 0.25f), max = new(522.75f, 1, 0.75f);
        var expected = Capture(world, world, lease, min, max, 32, int.MaxValue, excluded, true);
        Assert.Equal(PhysicsEvidenceStatus.Unresolved, expected.Status);
        Compare(world, (IPhysicsStaticEvidence)view, lease, min, max, 32, int.MaxValue, excluded);
    }

    [Fact]
    public void InwardOwnerAncestorFallsBackWithoutChangingFullWalkEvidence()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        for (int i = 0; i < 4; i++) world.AddStatic(new BoxShape(Vector3.One), Pose.At(new(i * 10, 0, 0)));
        var simulation = world.SimulationForTests;
        StaticHandle target = default;
        Vector3 position = default;
        foreach (var entry in world.StaticHandlesForTests)
        {
            int broad = simulation.Statics[simulation.Statics.HandleToIndex[entry.Value.Handle.Value]].BroadPhaseIndex;
            int node = simulation.BroadPhase.StaticTree.Leaves[broad].NodeIndex;
            if (simulation.BroadPhase.StaticTree.Metanodes[node].Parent < 0) continue;
            target = new(entry.Key);
            position = simulation.Statics[entry.Value.Handle].Pose.Position;
            break;
        }
        Assert.NotEqual(default, target);
        var native = world.StaticHandlesForTests[target.Value].Handle;
        world.MutateForTests(sim =>
        {
            ref var tree = ref sim.BroadPhase.StaticTree;
            int broad = sim.Statics[sim.Statics.HandleToIndex[native.Value]].BroadPhaseIndex;
            int node = tree.Leaves[broad].NodeIndex;
            var link = tree.Metanodes[node];
            ref var child = ref (link.IndexInParent == 0 ? ref tree.Nodes[link.Parent].A : ref tree.Nodes[link.Parent].B);
            child.Min = new(100); child.Max = new(101);
        }, target.Value);
        using var lease = world.AcquireQueryReadLease();
        var min = position - new Vector3(2); var max = position + new Vector3(2);
        var expected = Capture(world, world, lease, min, max, 64, int.MaxValue, [], true);
        Assert.Equal(PhysicsEvidenceStatus.Complete, expected.Status);
        Assert.NotEmpty(expected.Bits);
        Compare(world, world, lease, min, max, 64, int.MaxValue, []);
    }

    [Fact]
    public void CallbackLocalPrimitiveVerticesCannotAlterTheNextCapture()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        world.AddStatic(new BoxShape(Vector3.One), Pose.Identity);
        using var lease = world.AcquireQueryReadLease();
        Assert.Equal(PhysicsEvidenceStatus.Complete, world.CaptureStaticEvidence(lease, new(-2), new(2), 32, Change));
        Compare(world, world, lease, new(-2), new(2), 32, int.MaxValue, []);
        static bool Change(in PhysicsEvidenceFace face, ReadOnlySpan<PhysicsEvidencePoint> points)
        {
            if (!points.IsEmpty) MemoryMarshal.GetReference(points) = new(123, 456, 789);
            return true;
        }
    }

    [Fact]
    public void NestedCaptureCannotOverwriteTheOuterCandidateOrTriangleScratch()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        world.AddStatic(StaticEvidenceSpatialTests.Strip(32), Pose.Identity);
        using var lease = world.AcquireQueryReadLease();
        var expected = Capture(world, world, lease, new(-1), new(100), 100, int.MaxValue, [], true);
        int faces = 0;
        var actual = world.CaptureStaticEvidence(lease, new(-1), new(100), 100, Visit);
        Assert.Equal(expected.Status, actual);
        Assert.Equal(32, faces);
        bool Visit(in PhysicsEvidenceFace face, ReadOnlySpan<PhysicsEvidencePoint> points)
        {
            var nested = Capture(world, world, lease, new(-1), new(100), 100, int.MaxValue, [], false);
            Assert.Equal(expected.Bits, nested.Bits);
            Assert.Equal(faces++, face.Feature);
            return true;
        }
    }

    static void Compare(BepuPhysicsWorld world, IPhysicsStaticEvidence selected, IPhysicsQueryLease lease,
        Vector3 min, Vector3 max, int limit, int stop, HashSet<StaticHandle> excluded)
    {
        var expected = Capture(world, world, lease, min, max, limit, stop, excluded, true);
        var actual = Capture(selected, world, lease, min, max, limit, stop, [], false);
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.Bits, actual.Bits);
    }

    static (PhysicsEvidenceStatus Status, long[] Bits) Capture(IPhysicsStaticEvidence selected,
        BepuPhysicsWorld world, IPhysicsQueryLease lease, Vector3 min, Vector3 max, int limit, int stop,
        HashSet<StaticHandle> excluded, bool reference)
    {
        var bits = new List<long>();
        int faces = 0;
        var status = reference ? world.CaptureStaticEvidenceFullWalkForTests(lease, min, max, limit, Visit)
            : selected.CaptureStaticEvidence(lease, min, max, limit, Visit);
        return (status, bits.ToArray());
        bool Visit(in PhysicsEvidenceFace face, ReadOnlySpan<PhysicsEvidencePoint> points)
        {
            if (excluded.Contains(face.Static)) return true;
            bits.AddRange([face.Static.Value, face.Leaf, face.Feature, (long)face.Kind, points.Length]);
            Add(face.Pose.Position.X); Add(face.Pose.Position.Y); Add(face.Pose.Position.Z);
            Add(face.Pose.Orientation.X); Add(face.Pose.Orientation.Y); Add(face.Pose.Orientation.Z); Add(face.Pose.Orientation.W);
            Add(face.Radius); Add(face.HalfLength);
            foreach (var point in points)
            {
                bits.Add(BitConverter.DoubleToInt64Bits(point.X));
                bits.Add(BitConverter.DoubleToInt64Bits(point.Y));
                bits.Add(BitConverter.DoubleToInt64Bits(point.Z));
            }
            return ++faces < stop;
        }
        void Add(float value) => bits.Add(BitConverter.SingleToInt32Bits(value));
    }
}
