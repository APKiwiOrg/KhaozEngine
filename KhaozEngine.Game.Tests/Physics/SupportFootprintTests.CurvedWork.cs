using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Tests.Locomotion.Contacts;
using Xunit;
using StaticHandle = KhaozEngine.Physics.StaticHandle;

namespace KhaozEngine.Tests.Physics;

public partial class SupportFootprintTests
{
    static SupportCurvedWork NewCurvedWork()
    {
        var work = new SupportCurvedWork();
        work.Begin();
        return work;
    }

    [Fact]
    public void CurvedWorkRefusesBeforePublishingEitherSpan()
    {
        using var scene = new SupportFootprintScene();
        StaticHandle owner = scene.Install(new CylinderShape(0.3f, 2),
            new(new(0, -0.25f, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 6)));
        var request = Request(z: 0.1f, lower: -2, upper: 2, cosine: 0.4f);
        using var lease = scene.World.AcquireQueryReadLease();
        var collector = new SupportFootprintCollector(32);
        Assert.True(collector.Begin(request));
        var simulation = (BepuPhysics.Simulation)ObjectField(scene.World, "_sim");
        var candidates = collector.Candidates(simulation,
            (System.Collections.Generic.Dictionary<int, int>)ObjectField(scene.World, "_reverseHandles"), null);
        var candidate = Assert.Single(candidates);
        simulation.Statics.GetDescription(candidate.Installed, out var description);
        var cache = new InstalledSupportGeometryCache();
        CapsuleFeatureStatus status = collector.Collect(simulation, owner, description, cache, 0);
        Assert.Equal(CapsuleFeatureStatus.Unsupported, status);
        var members = MemberSentinels(260);
        var memberSentinels = (SupportFootprintMember[])members.Clone();
        var joins = Enumerable.Repeat(73UL, 1300).ToArray();
        var joinSentinels = (ulong[])joins.Clone();
        var refused = collector.Publish(scene.World, lease, members, joins, false);
        Assert.Equal(CapsuleFeatureStatus.Unsupported, refused.Status);
        Assert.Equal(0, refused.Members);
        Assert.Equal(0, refused.Witnesses);
        Assert.Equal(memberSentinels, members);
        Assert.Equal(joinSentinels, joins);
        var exhausted = collector.CurvedWork;
        Assert.Equal(32, exhausted.Consumed);
        Assert.Equal(SupportCurvedWorkFailure.WorkLimit, exhausted.Failure);
        collector.BeginBelowBandPass();
        Assert.Equal(CapsuleFeatureStatus.Unsupported, collector.Collect(simulation, owner, description, cache, 0));
        Assert.Equal(exhausted, collector.CurvedWork);
        Assert.True(collector.Begin(request));
        Assert.Equal(0, collector.CurvedWork.Consumed);
        // Install only a provisioned collector, without changing the world's captured geometry or query path.
        scene.World.QuerySupportFootprint(lease, request, members, joins);
        var scratch = Assert.Single(ConcurrentReadGateTests.Gate(scene.World).ScratchPool.Constructed());
        scratch.Footprints.Idle = collector;
        var query = new FootSupportQuery(request.Axis, 0, request.Radius, 2, 2, request.CosMaxSlope);
        SupportCurvedWorkObservation observed = default;
        var prior = SupportQueryDiagnostics.Exchange(SupportQueryDiagnostics.ThreadState.Fresh(true) with
        { CurvedWork = o => observed = o });
        try { Assert.Equal(SupportStatus.Refused, FootSupport.Find(null, scene.World, lease, query).Status); }
        finally { SupportQueryDiagnostics.Exchange(prior); }
        Assert.Equal(CapsuleFeatureStatus.Unsupported, observed.FootprintStatus);
        Assert.Equal(SupportCurvedWorkFailure.WorkLimit, observed.Failure);
        Assert.Equal(32, observed.Consumed);
        Assert.Equal(SupportCurvedWorkFailure.WorkLimit, collector.CurvedWork.Failure);
        scratch.Footprints.Idle = new SupportFootprintCollector();
        Assert.Equal(CapsuleFeatureStatus.Complete,
            scene.World.QuerySupportFootprint(lease, request, members, joins).Status);
    }

    [Fact]
    public void CertifiedCurvedStrataFitTheCumulativeQueryCeiling()
    {
        string[] strata = ["sphere-apex", "sphere-flank", "sphere-axis-tangent", "sphere-disc-tangent",
            "capsule-zero", "capsule-upright", "capsule-horizontal", "capsule-horizontal-yaw", "capsule-end-tangent",
            "capsule-inverted", "capsule-end", "capsule-seam", "capsule-core-line", "capsule-axis-tangent",
            "capsule-compound", "cylinder-upright", "cylinder-horizontal", "cylinder-horizontal-yaw",
            "cylinder-negative-horizontal", "cylinder-ring", "cylinder-finite-end", "cylinder-finite-end-yaw",
            "cylinder-axis-tangent", "cylinder-disc-tangent", "cylinder-cap-tangent", "cylinder-inverted",
            "cylinder-compound", "cylinder-inverted-compound", "hidden-sphere", "hidden-capsule-zero",
            "hidden-capsule-upright", "hidden-capsule-horizontal", "hidden-cylinder-upright", "hidden-cylinder-horizontal",
            "tilted-side", "tilted-cap", "tilted-round", "slope-below", "slope-boundary", "slope-above", "below-band", "maximum-raw"];
        long[] totals = new long[7];
        long maximum = 0;
        string maximumRow = "";
        foreach (string row in strata)
        {
            using var scene = new SupportFootprintScene();
            var request = Request();
            if (row == "maximum-raw")
                for (int i = 0; i < 256; i++) scene.Install(new SphereShape(0.25f), Pose.At(new(0, -0.25f, 0)));
            else if (row == "below-band")
            {
                scene.Install(new SphereShape(0.25f), Pose.At(new(0, -0.2505f, 0)));
                request = Request(lower: 0);
            }
            else if (row.StartsWith("tilted-", StringComparison.Ordinal) || row.StartsWith("slope-", StringComparison.Ordinal))
            {
                float angle = row == "slope-below" ? MathF.BitDecrement(MathF.PI / 4) :
                    row == "slope-above" ? MathF.BitIncrement(MathF.PI / 4) :
                    row == "slope-boundary" ? MathF.PI / 4 : MathF.PI / 6;
                PhysicsShape shape = row == "tilted-round" ? new CapsuleShape(0.3f, 2) : new CylinderShape(0.3f, 2);
                scene.Install(shape, new(new(0, -0.25f, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, angle)));
                request = Request(z: 0.1f, lower: -2, upper: 2, cosine: row.StartsWith("slope-", StringComparison.Ordinal) ? 0.70710677f : 0.4f);
                if (row == "tilted-cap") request = request with { Axis = new(0.35f, 0.1f) };
            }
            else
            {
                var fixture = CurvedQueryRow(row);
                scene.Install(fixture.Shape, fixture.Pose);
                request = fixture.Request;
            }
            using var lease = scene.World.AcquireQueryReadLease();
            var observations = new System.Collections.Generic.List<SupportCurvedWorkObservation>();
            var prior = SupportQueryDiagnostics.Exchange(SupportQueryDiagnostics.ThreadState.Fresh(true) with { CurvedWork = observations.Add });
            try
            {
                var result = SupportFootprintEquivalence.AssertMatchesReference(scene.World, lease, request, row);
                Assert.Equal(CapsuleFeatureStatus.Complete, result.Status);
                Assert.Equal(2, observations.Count);
                if (row == "maximum-raw")
                {
                    Assert.Equal(256, result.Members);
                    Assert.Equal(512, result.Witnesses);
                }
                if (row == "sphere-apex")
                {
                    Assert.Equal(SupportProposalStatus.Contact, result.AxisVerdict.Status);
                    Assert.Equal(SupportProposalStatus.Contact, result.FootVerdict.Status);
                }
                if (row == "below-band")
                {
                    Assert.True((bool)ObjectField(FootprintScratch(scene.World), "_belowBandPass"));
                    Assert.Equal(0, result.Members);
                }
                foreach (var observation in observations)
                {
                    Assert.Equal(CapsuleFeatureStatus.Complete, observation.FootprintStatus);
                    Assert.Equal(SupportCurvedWorkFailure.None, observation.Failure);
                    Assert.InRange(observation.Consumed, 1, 1048576);
                    Assert.Equal(observation.Consumed, SupportCurvedWorkTests.Sum(observation));
                    totals[0] += observation.New; totals[1] += observation.Sign; totals[2] += observation.Split;
                    totals[3] += observation.Compile; totals[4] += observation.RootCells;
                    totals[5] += observation.Refine; totals[6] += observation.AdmissionCells;
                    if (observation.Consumed > maximum) { maximum = observation.Consumed; maximumRow = row; }
                    _output.WriteLine($"curved-work {row} {observation}");
                }
            }
            finally { SupportQueryDiagnostics.Exchange(prior); }
        }
        _output.WriteLine($"curved-work totals New={totals[0]} Sign={totals[1]} Split={totals[2]} Compile={totals[3]} RootCell={totals[4]} Refine={totals[5]} AdmissionCell={totals[6]} maximum={maximum} row={maximumRow}");
    }

    [Fact]
    public void CurvedWorkObserverRestoresNestedScopesAndKeepsCallbackQueriesIndependent()
    {
        using var scene = new SupportFootprintScene();
        scene.Install(new SphereShape(0.25f), Pose.At(new(0, -0.25f, 0)));
        using var lease = scene.World.AcquireQueryReadLease();
        var members = new SupportFootprintMember[256];
        var joins = new ulong[1024];
        var request = Request();
        int outerCalls = 0, innerCalls = 0;
        SupportCurvedWorkObservation outerWork = default, innerWork = default;
        Action<SupportCurvedWorkObservation> inner = o => { innerCalls++; innerWork = o; };
        Action<SupportCurvedWorkObservation> outer = o =>
        {
            outerCalls++;
            outerWork = o;
            var enclosing = SupportQueryDiagnostics.Exchange(SupportQueryDiagnostics.ThreadState.Fresh(true) with { CurvedWork = inner });
            try
            {
                Assert.Equal(CapsuleFeatureStatus.Complete,
                    scene.World.QuerySupportFootprint(lease, request, new SupportFootprintMember[256], new ulong[1024]).Status);
            }
            finally { SupportQueryDiagnostics.Exchange(enclosing); }
        };
        var prior = SupportQueryDiagnostics.Exchange(SupportQueryDiagnostics.ThreadState.Fresh(true));
        try
        {
            SupportQueryDiagnostics.SetCurvedWorkObserver(outer);
            SupportQueryDiagnostics.Reset();
            Assert.Equal(CapsuleFeatureStatus.Complete, scene.World.QuerySupportFootprint(lease, request, members, joins).Status);
            Assert.Equal(1, outerCalls);
            Assert.Equal(1, innerCalls);
            Assert.Equal(outerWork, innerWork);
            Assert.True(outerWork.Consumed > 0);
            Assert.Equal(outerWork, FootprintScratch(scene.World).CurvedWork with { FootprintStatus = outerWork.FootprintStatus });
            SupportQueryDiagnostics.Enable(false);
            scene.World.QuerySupportFootprint(lease, request, members, joins);
            Assert.Equal(1, outerCalls);
            SupportQueryDiagnostics.Enable(true);
            SupportQueryDiagnostics.SetCurvedWorkObserver(null);
            scene.World.QuerySupportFootprint(lease, request, members, joins);
            Assert.Equal(1, outerCalls);
            SupportQueryDiagnostics.SetCurvedWorkObserver(inner);
            scene.World.QuerySupportFootprint(lease, request, members, joins);
            long bytes = AllocationMeasurement.Measure(nameof(CurvedWorkObserverRestoresNestedScopesAndKeepsCallbackQueriesIndependent), () =>
                scene.World.QuerySupportFootprint(lease, request, members, joins));
            Assert.Equal(0, bytes);
            Assert.Equal(3, innerCalls);
            _output.WriteLine($"curved-work observer warm allocation={bytes}");
        }
        finally { SupportQueryDiagnostics.Exchange(prior); }
    }

    [Fact]
    public void CurvedWorkConcurrentObserversAndOwnersStayIndependent()
    {
        using var scene = new SupportFootprintScene();
        scene.Install(new SphereShape(0.25f), Pose.At(new(0, -0.25f, 0)));
        using var barrier = new System.Threading.Barrier(2);
        SupportCurvedWorkObservation[] observations = new SupportCurvedWorkObservation[2];
        var threads = Enumerable.Range(0, 2).Select(index => BoundedThread.Start(() =>
        {
            var prior = SupportQueryDiagnostics.Exchange(SupportQueryDiagnostics.ThreadState.Fresh(true));
            try
            {
                int calls = 0;
                SupportQueryDiagnostics.SetCurvedWorkObserver(o => { calls++; observations[index] = o; });
                using var lease = scene.World.AcquireQueryReadLease();
                // Each concurrent reader provisions its own scratch before the synchronized query.
                scene.World.QuerySupportFootprint(lease, Request(), new SupportFootprintMember[256], new ulong[1024]);
                calls = 0;
                BoundedThread.Arrive(barrier);
                var result = scene.World.QuerySupportFootprint(lease, Request(), new SupportFootprintMember[256], new ulong[1024]);
                Assert.Equal(CapsuleFeatureStatus.Complete, result.Status);
                Assert.Equal(1, calls);
            }
            finally { SupportQueryDiagnostics.Exchange(prior); }
        })).ToArray();
        foreach (var thread in threads) thread.Join();
        Assert.Equal(observations[0], observations[1]);
        Assert.True(observations[0].Consumed > 0);
        Assert.Equal(SupportCurvedWorkFailure.None, observations[0].Failure);
        Assert.Equal(2, ConcurrentReadGateTests.Gate(scene.World).ScratchPool.Constructed().Count());
    }


    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void CurvedWorkStorageFailureDiscardsARetainedPrefix(int failure)
    {
        var reason = (SupportCurvedWorkFailure)failure;
        using var scene = new SupportFootprintScene();
        StaticHandle owner = scene.Install(new SphereShape(0.25f), Pose.At(new(0, -0.25f, 0)));
        using var lease = scene.World.AcquireQueryReadLease();
        var collector = new SupportFootprintCollector();
        Assert.True(collector.Begin(Request()));
        var simulation = (BepuPhysics.Simulation)ObjectField(scene.World, "_sim");
        var candidates = collector.Candidates(simulation,
            (System.Collections.Generic.Dictionary<int, int>)ObjectField(scene.World, "_reverseHandles"), null);
        simulation.Statics.GetDescription(Assert.Single(candidates).Installed, out var description);
        Assert.Equal(CapsuleFeatureStatus.Complete,
            collector.Collect(simulation, owner, description, new InstalledSupportGeometryCache(), 0));
        Assert.Equal(1, (int)ObjectField(collector, "_count"));
        var work = (SupportCurvedWork)ObjectField(collector, "_curvedWork");
        work.Fail(reason);
        var members = MemberSentinels(260);
        var before = (SupportFootprintMember[])members.Clone();
        var joins = Enumerable.Repeat(73UL, 1300).ToArray();
        var result = collector.Publish(scene.World, lease, members, joins, false);
        Assert.Equal(CapsuleFeatureStatus.Unsupported, result.Status);
        Assert.Equal(0, result.Members);
        Assert.Equal(0, result.Witnesses);
        Assert.Equal(before, members);
        Assert.All(joins, word => Assert.Equal(73UL, word));
        Assert.Equal(reason, collector.CurvedWork.Failure);
    }

}
