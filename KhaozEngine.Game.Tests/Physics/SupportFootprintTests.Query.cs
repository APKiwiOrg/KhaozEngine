using System;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Collections.Generic;
using System.Threading;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Physics;

public partial class SupportFootprintTests
{
    [Theory]
    [InlineData(FootprintPartition.Boxes)]
    [InlineData(FootprintPartition.Hulls)]
    [InlineData(FootprintPartition.Compound)]
    [InlineData(FootprintPartition.Mesh)]
    public void CompleteColumnFindsCrateAcrossPartitions(FootprintPartition partition)
    {
        foreach (bool reverse in new[] { false, true })
            foreach (float slope in new[] { 50f, 60f, 70f })
            {
                using var scene = SupportFootprintScenes.HiddenCrate(partition, reverse, slope);
                scene.Install(new BoxShape(new(0.025f, 0.025f, 0.05f)), Pose.At(new(-0.08f, 0.025f, 0)));
                SupportFootprintRequest request = Request();
                using IPhysicsQueryLease lease = scene.World.AcquireQueryReadLease();
                var members = new SupportFootprintMember[256];
                var joins = new ulong[1024];
                SupportFootprintResult result = Capability(scene.World).QuerySupportFootprint(lease, request, members, joins);
                CheckQueryOracle(scene, request, result, members, joins);
                Assert.Contains(members.Take(result.Members), m => m.Static == scene.Crate && m.HasDiscMaximum &&
                    m.Normal.Y > request.CosMaxSlope && m.DiscHeightLower <= 0.1f && m.DiscHeightUpper >= 0.1f);
                Assert.Equal(SupportProposalStatus.Contact, result.AxisVerdict.Status);
                Assert.Equal(SupportProposalStatus.Contact, result.FootVerdict.Status);
                Assert.Contains(members.Take(result.Members), m => (m.ProposalMask & 1) != 0);
                Assert.Contains(members.Take(result.Members), m => (m.ProposalMask & 2) != 0);
            }
    }

    [Fact]
    public void RemoteWholePolygonJoinsAndRingCapsMatchOracle()
    {
        using var scene = new SupportFootprintScene();
        scene.Install(new TriangleMeshShape([new(-0.001f, -0.0001f, -0.1f), new(0, 0, 3),
            new(-0.001f, -0.0001f, 0.1f)], [0, 1, 2]), Pose.Identity);
        scene.Install(new TriangleMeshShape([new(0, 0, 0.20004f), new(0.1f, -0.01f, 0.20004f),
            new(0, 0, 3)], [0, 1, 2]), Pose.Identity);
        // A remote face with an in-band axis plane is excluded by its actual column height.
        scene.Install(new TriangleMeshShape([new(0.15f, 3.1f, -0.1f), new(0.25f, 3.25f, -0.1f),
            new(0.25f, 3.25f, 0.1f)], [0, 1, 2]), Pose.Identity);
        SupportFootprintRequest request = Request(x: 0.05f);
        using IPhysicsQueryLease lease = scene.World.AcquireQueryReadLease();
        var members = new SupportFootprintMember[256];
        var joins = new ulong[1024];
        SupportFootprintResult result = Capability(scene.World).QuerySupportFootprint(lease, request, members, joins);
        CheckQueryOracle(scene, request, result, members, joins);
        Assert.Contains(members.Take(result.Members), m => !m.HasDiscMaximum && m.WitnessCount == 0);
        int left = Array.FindIndex(members, 0, result.Members, m => m.Static.Value == 0 && m.ElementId == 0);
        OracleFace remoteFace = scene.Faces.Single(f => f.Static.Value == 1 &&
            f.Vertices.Contains(new OracleVector(0, 0, 3)) && f.Vertices.Count(v => v.X == 0) == 2);
        int remote = Array.FindIndex(members, 0, result.Members, m => m.Static == remoteFace.Static && m.ElementId == remoteFace.ElementId);
        Assert.True(left >= 0 && remote >= 0);
        Assert.True((joins[left * result.JoinWordsPerRow + remote / 64] & (1UL << (remote % 64))) != 0);
    }

    [Theory]
    [InlineData(96)]
    [InlineData(256)]
    [InlineData(257)]
    [InlineData(300)]
    public void RawCapacityPublishesAtomically(int count)
    {
        using var scene = SupportFootprintScenes.Fan(count);
        using IPhysicsQueryLease lease = scene.World.AcquireQueryReadLease();
        SupportFootprintMember[] members = MemberSentinels(256), before = (SupportFootprintMember[])members.Clone();
        ulong[] joins = Enumerable.Repeat(0xDEADBEEFUL, 1024).ToArray(), beforeJoins = (ulong[])joins.Clone();
        SupportQueryDiagnostics.Enable(true);
        try
        {
            SupportQueryDiagnostics.Reset();
            SupportFootprintResult result = Capability(scene.World).QuerySupportFootprint(lease, Request(), members, joins);
            if (count <= 256)
            {
                Assert.Equal(CapsuleFeatureStatus.Complete, result.Status);
                Assert.Equal(count, result.Members);
                Assert.Equal(SupportFootprintResult.JoinWordsFor(count), result.JoinWordsPerRow);
                Assert.Equal(count, members.Take(result.Members).Select(m => (m.Static, m.ElementId)).Distinct().Count());
            }
            else
            {
                Assert.Equal(CapsuleFeatureStatus.CapacityExceeded, result.Status);
                Assert.True(result.RequiredElements >= 257);
                if (count == 257) Assert.Equal(257, result.RequiredElements);
                Assert.Equal(before, members);
                Assert.Equal(beforeJoins, joins);
                Assert.Equal(1, SupportQueryDiagnostics.Snapshot().CapacityExceeded);
            }
            Assert.Equal(2, SupportQueryDiagnostics.Snapshot().Proposals);
            Assert.Equal(1, SupportQueryDiagnostics.Snapshot().Volumes);
        }
        finally { SupportQueryDiagnostics.Enable(false); }
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(10f)]
    public void CurvedCompoundLeafPublishesBesidePolygon(float curveX)
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        world.AddStatic(new CompoundShape([new(new BoxShape(new(2, 1, 2)), Pose.At(new(0, -1, 0))),
            new(new SphereShape(0.1f), Pose.At(new(curveX, 0, 0)))]), Pose.Identity);
        using IPhysicsQueryLease lease = world.AcquireQueryReadLease();
        SupportFootprintMember[] members = MemberSentinels(256);
        ulong[] joins = Enumerable.Repeat(77UL, 1024).ToArray();
        SupportFootprintResult result = Capability(world).QuerySupportFootprint(lease, Request(), members, joins);
        Assert.Equal(CapsuleFeatureStatus.Complete, result.Status);
        Assert.Equal(curveX == 0 ? 2 : 1, result.Members);
        Assert.Equal(SupportElementKind.Polygon, members[0].Kind);
        if (curveX == 0)
        {
            Assert.Equal(SupportElementKind.Tangent, members[1].Kind);
            Assert.Equal(SupportNeighborhoodOracle.TangentLeafStride, members[1].ElementId);
            Assert.Equal(2, members[1].WitnessCount);
        }
    }

    [Fact]
    public void FootprintAuthenticatesBeforeCallingForeignLease()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        using var view = world.CreateQueryViewExcludingStatics([]);
        var foreign = new ForeignFootprintLease();
        foreach (IPhysicsWorld receiver in new IPhysicsWorld[] { world, view })
        {
            var members = MemberSentinels(1);
            var before = (SupportFootprintMember[])members.Clone();
            var joins = new[] { 99UL };
            Assert.Throws<ArgumentException>(() => Capability(receiver).QuerySupportFootprint(foreign, Request(), members, joins));
            Assert.Equal(before, members);
            Assert.Equal(99UL, joins[0]);
        }
        Assert.Equal(0, foreign.Callbacks);
    }

    [Fact]
    public void FootprintSelectionLifecycleAndShortSpansAreAtomic()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        StaticHandle floor = world.AddStatic(new BoxShape(new(2, 1, 2)), Pose.At(new(0, -1, 0)));
        using var view = world.CreateQueryViewExcludingStatics([]);
        using var excluded = world.CreateQueryViewExcludingStatics([floor]);
        using IPhysicsQueryLease lease = world.AcquireQueryReadLease();
        var members = new SupportFootprintMember[256];
        var joins = new ulong[1024];
        IPhysicsSupportFootprint selected = Capability(view);
        SupportFootprintResult result = selected.QuerySupportFootprint(lease, Request(), members, joins);
        Assert.Equal(1, result.Members);
        Assert.Same(view, result.QueryWorld);
        Assert.Same(world, result.SourceWorld);
        selected.AssertFootprintCurrent(result, lease);
        Assert.Throws<InvalidOperationException>(() => Capability(world).AssertFootprintCurrent(result, lease));
        Assert.Throws<InvalidOperationException>(() => Capability(excluded).AssertFootprintCurrent(result, lease));
        SupportFootprintResult empty = Capability(excluded).QuerySupportFootprint(lease, Request(), members, joins);
        Assert.Equal(0, empty.Members);
        Assert.Equal(SupportProposalStatus.Miss, empty.FootVerdict.Status);
        Exception? threadFailure = null;
        var thread = new Thread(() =>
        {
            try { selected.QuerySupportFootprint(lease, Request(), members, joins); }
            catch (Exception error) { threadFailure = error; }
        });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        Assert.IsAssignableFrom<InvalidOperationException>(threadFailure);
        Assert.Throws<ArgumentException>(() => selected.QuerySupportFootprint(lease, Request(), new SupportFootprintMember[65], new ulong[129]));
        SupportFootprintMember[] shortMembers = MemberSentinels(0);
        Assert.Equal(CapsuleFeatureStatus.CapacityExceeded, selected.QuerySupportFootprint(lease, Request(), shortMembers, []).Status);
        lease.Dispose();
        using (IPhysicsQueryLease next = world.AcquireQueryReadLease())
        {
            Assert.Equal(result.GeometryGeneration, next.GeometryGeneration);
            Assert.Throws<InvalidOperationException>(() => selected.AssertFootprintCurrent(result, next));
            Assert.ThrowsAny<InvalidOperationException>(() => selected.QuerySupportFootprint(lease, Request(), members, joins));
        }
        world.RemoveStatic(floor);
        world.AddStatic(new BoxShape(new(2, 1, 2)), Pose.At(new(0, -0.9f, 0)));
        world.Rebase(new(10, 0, 20));
        using (IPhysicsQueryLease next = world.AcquireQueryReadLease())
        {
            SupportFootprintResult moved = selected.QuerySupportFootprint(next, Request(x: -10, z: -20), members, joins);
            Assert.Equal(CapsuleFeatureStatus.Complete, moved.Status);
            Assert.Equal(new Vector3(10, 0, 20), moved.Origin);
            Assert.InRange(members[0].DiscHeightLower, 0.099999, 0.100001);
        }
        view.Dispose();
        using IPhysicsQueryLease final = world.AcquireQueryReadLease();
        Assert.Throws<ObjectDisposedException>(() => selected.QuerySupportFootprint(final, Request(), members, joins));
    }

    static IPhysicsSupportFootprint Capability(IPhysicsWorld world) => Assert.IsAssignableFrom<IPhysicsSupportFootprint>(world);

    // The idle footprint collector of the world's only query scratch. Every operation restores its collector
    // before returning, so this holds between queries even while a lease is open.
    internal static SupportFootprintCollector FootprintScratch(BepuPhysicsWorld world) =>
        Assert.Single(ConcurrentReadGateTests.Gate(world).ScratchPool.Constructed()).Footprints.Idle!;

    static SupportFootprintMember[] MemberSentinels(int count) => Enumerable.Repeat(new SupportFootprintMember(
        new StaticHandle(987), SupportElementKind.Polygon, 654, Vector3.One, 0, Vector3.UnitY, 0,
        1, true, 321, 321, 3, 0, default, default), count).ToArray();

    static void CheckQueryOracle(SupportFootprintScene scene, SupportFootprintRequest request,
        SupportFootprintResult result, SupportFootprintMember[] members, ulong[] joins)
    {
        if (result.Status != CapsuleFeatureStatus.Complete)
        {
            object scratch = FootprintScratch(scene.World);
            var polygons = (List<SupportPolygon>)scratch.GetType().GetField("_polygons", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scratch)!;
            string diagnostics = string.Join("\n", polygons.Select(p => $"{p.Static}/{p.ElementId} admission={SupportFootprintPolygon.Measure(p, request, out _)} lowest={SupportFootprintProposalGeometry.Lowest(p, request.FootProposal, out var h)} [{h.Lower:R}, {h.Upper:R}]"));
            Assert.Fail($"{result.Status}\n{diagnostics}");
        }
        var oracle = Members(scene, request);
        Assert.Equal(oracle.Count, result.Members);
        for (int i = 0; i < result.Members; i++)
        {
            Assert.Equal(oracle[i].Static, members[i].Static);
            Assert.Equal(oracle[i].ElementId, members[i].ElementId);
            if (oracle[i].Normal.Y > 0 && oracle[i].HasDiscMaximum) CheckMaximum(members[i], oracle[i]);
            else Assert.False(members[i].HasDiscMaximum);
            if (oracle[i].PlaneBounds is { } plane) CheckPublishedPlane(members[i], request, plane);
            for (int j = 0; j < result.Members; j++)
                Assert.Equal(i != j && SupportFootprintOracle.Joined(oracle[i].Face!, oracle[j].Face!, request.ContactBand),
                    (joins[i * result.JoinWordsPerRow + j / 64] & (1UL << (j % 64))) != 0);
        }
        foreach (int index in new[] { 0, 1 })
        {
            var verdict = index == 0 ? result.AxisVerdict : result.FootVerdict;
            if (verdict.Status != SupportProposalStatus.Contact) continue;
            var proposal = index == 0 ? request.AxisProposal : request.FootProposal;
            var references = Enumerable.Range(0, result.Members)
                .Where(i => (members[i].ProposalMask & (1 << index)) != 0)
                .Select(i => SupportFootprintExactOracle.Contact(oracle[i].Face!, proposal))
                .Where(r => r is not null).Select(r => r!.Value).ToArray();
            Assert.NotEmpty(references);
            var reference = new SupportFootprintExactOracle.Bounds(
                references.Select(r => r.Lower).Aggregate((a, b) => a > b ? a : b),
                references.Select(r => r.Upper).Aggregate((a, b) => a > b ? a : b));
            ContainsReference(GeometryInterval.Enclose(verdict.LowestLower, verdict.LowestUpper), reference);
        }
    }

    sealed class ForeignFootprintLease : IPhysicsQueryLease
    {
        internal int Callbacks;
        public IPhysicsWorld SourceWorld { get { Callbacks++; throw new InvalidOperationException(); } }
        public Vector3 Origin { get { Callbacks++; throw new InvalidOperationException(); } }
        public long GeometryGeneration { get { Callbacks++; throw new InvalidOperationException(); } }
        public void AssertCurrent() { Callbacks++; throw new InvalidOperationException(); }
        public void Dispose() => throw new InvalidOperationException();
    }
}
