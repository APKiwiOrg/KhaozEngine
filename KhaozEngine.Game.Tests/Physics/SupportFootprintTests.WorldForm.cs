using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Tests.Locomotion.Contacts;
using Xunit;
using BepuCompound = BepuPhysics.Collidables.Compound;
using RigidPose = BepuPhysics.RigidPose;
using StaticDescription = BepuPhysics.StaticDescription;
using StaticHandle = KhaozEngine.Physics.StaticHandle;

namespace KhaozEngine.Tests.Physics;

public partial class SupportFootprintTests
{
    [Theory]
    [InlineData("flat box")]
    [InlineData("flat hull")]
    [InlineData("flat mesh")]
    [InlineData("rotated")]
    public void SharedMemberFactsMatchRecomputation(string name)
    {
        using var counters = new WorldFormCounters();
        using var scene = name == "rotated"
            ? new WorldFormScene(MixedWorld(), new(Vector2.Zero, 0, 0.2f, 0.4f, 0.4f, 0.7f))
            : new WorldFormScene(CostRows().Single(row => row.Name == name));
        scene.Find();
        var request = FootSupport.DescribeFootprint(scene.Query);
        InstalledSupportWorldFace[] faces = name == "flat mesh"
            ? [((InstalledSupportMeshWorldForm)MeshForm(MeshForms(scene.World), scene.Owners().Single(), 0)).Face!,
                ((InstalledSupportMeshWorldForm)MeshForm(MeshForms(scene.World), scene.Owners().Single(), 1)).Face!]
            : [.. scene.Owners().SelectMany(owner => scene.Form(owner)!.Leaves.ToArray())
                .SelectMany(leaf => leaf.Faces.ToArray())];
        Assert.NotEmpty(faces);
        foreach (InstalledSupportWorldFace face in faces)
        {
            InstalledSupportMemberFacts facts = face.MemberFacts;
            GeometryVector normalBounds = GeometryVectorOperations.Normalize(face.Normal.Bounds);
            AssertMemberFactInterval(normalBounds.X, facts.NormalBounds.X);
            AssertMemberFactInterval(normalBounds.Y, facts.NormalBounds.Y);
            AssertMemberFactInterval(normalBounds.Z, facts.NormalBounds.Z);
            AssertMemberFactOutput(GeometryVectorOperations.Publish(normalBounds), facts.PublishedNormal);
            AssertMemberFactOutput(GeometryVectorOperations.Publish(face.Vertices[0].Bounds), facts.PublishedPlane);
            AssertMemberFactNumber(SupportFootprintProposalGeometry.Root(FeaturePoint.Dot(face.Normal, face.Normal)),
                facts.ProposalNormalLength);
            var polygon = new SupportPolygon();
            polygon.Bind(scene.Owners()[0], face);
            foreach (float shift in new[] { 0f, 0.007f, -0.011f })
            {
                var moved = request with { Axis = request.Axis + new Vector2(shift, -shift) };
                polygon.BeginFootprintOperands(moved);
                var axis = FeaturePoint.Exact(new(moved.Axis.X, 0, moved.Axis.Y));
                SupportQueryDiagnostics.Reset();
                for (int i = 0; i < polygon.Edges.Length; i++)
                {
                    FeatureNumber actual = polygon.FootprintOperands(i, axis).LengthSquared;
                    FeatureNumber expected;
                    using (SupportReferenceEvaluation.Begin())
                        expected = polygon.FootprintOperands(i, axis, share: false).LengthSquared;
                    AssertMemberFactNumber(expected, actual);
                    AssertMemberFactNumber(expected, facts.ProjectedEdgeLengthsSquared[i]);
                }
                Assert.True(SharedMemberFactHits() > 0, $"{name} edge constants were recomputed.");
                SupportQueryDiagnostics.Reset();
                GeometrySign measured = SupportFootprintPolygon.Measure(polygon, moved, out var member);
                long measureHits = SharedMemberFactHits();
                GeometrySign reference;
                SupportFootprintMember expectedMember;
                using (SupportReferenceEvaluation.Begin())
                    reference = SupportFootprintPolygon.Measure(polygon, moved, out expectedMember);
                Assert.Equal(reference, measured);
                Assert.Equal(JsonSerializer.Serialize(expectedMember, new JsonSerializerOptions { IncludeFields = true }),
                    JsonSerializer.Serialize(member, new JsonSerializerOptions { IncludeFields = true }));
                if (measured == GeometrySign.Positive) Assert.True(measureHits >= 3);
                Assert.Equal(measureHits, SharedMemberFactHits());
                SupportQueryDiagnostics.Reset();
                GeometrySign proposal = SupportFootprintProposalGeometry.LowestExhaustive(polygon,
                    moved.FootProposal, out GeometryInterval lowest);
                long proposalHits = SharedMemberFactHits();
                GeometrySign expectedProposal;
                GeometryInterval expectedLowest;
                using (SupportReferenceEvaluation.Begin())
                    expectedProposal = SupportFootprintProposalGeometry.LowestExhaustive(polygon,
                        moved.FootProposal, out expectedLowest);
                Assert.Equal(expectedProposal, proposal);
                AssertMemberFactInterval(expectedLowest, lowest);
                Assert.Equal(proposalHits, SharedMemberFactHits());
            }
        }
    }

    [Theory]
    [InlineData("flat box")]
    [InlineData("flat hull")]
    [InlineData("flat mesh")]
    public void SharedMemberFactsNeverCrossAPoseOrVersion(string name)
    {
        using var counters = new WorldFormCounters();
        using var scene = new WorldFormScene(CostRows().Single(row => row.Name == name));
        SupportSample before = scene.Checked("facts published", out _);
        StaticHandle owner = scene.Owners().Single();
        scene.SetPose(owner, pose => pose with { Position = pose.Position with { Y = pose.Position.Y - 0.125f } });
        SupportQueryDiagnostics.Reset();
        SupportSample moved = scene.Find();
        Assert.Equal(0, SharedMemberFactHits());
        Assert.Equal(0, SupportQueryDiagnostics.Snapshot().WorldFormBuilds);
        scene.Checked("facts pose mismatch", out _);
        Assert.NotEqual(before.Height, moved.Height);
        scene.World.Rebase(new(0, 0.125f, 0));
        scene.Checked("facts version rebuilt", out long builds);
        Assert.True(builds > 0);
        SupportQueryDiagnostics.Reset();
        scene.Find();
        Assert.True(SharedMemberFactHits() > 0);
        Assert.Equal(0, SupportQueryDiagnostics.Snapshot().WorldFormBuilds);
        scene.World.RemoveStatic(owner);
        scene.World.AddStatic(new BoxShape(new(2, 1, 2)), Pose.At(new(0, -1.375f, 0)));
        scene.Checked("facts reinstalled", out builds);
        Assert.Equal(1, builds);
        SupportQueryDiagnostics.Reset();
        scene.Find();
        Assert.True(SharedMemberFactHits() > 0);
    }

    [Fact]
    public void RepresentedHorizontalNormalKeepsEverySlopeQueryIndependent()
    {
        using var counters = new WorldFormCounters();
        var world = new BepuPhysicsWorld(Vector3.Zero);
        world.AddStatic(new BoxShape(new(1, 0.2f, 1)),
            new(new(0, -0.1f, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.37f)));
        using var scene = new WorldFormScene(world, new(Vector2.Zero, 0, 0.2f, 0.4f, 0.4f, 0.7f));
        scene.Find();
        InstalledSupportWorldFace top = scene.Form(scene.Owners().Single())!.Leaves[0].Faces.ToArray()
            .Single(face => face.Orientation == GeometrySign.Positive);
        GeometryVector unadjusted = GeometryVectorOperations.Normalize(top.Normal.Bounds);
        Assert.Equal(GeometryInterval.Exact(1), unadjusted.Y);
        Assert.Equal(GeometryInterval.Exact(0), unadjusted.X);
        Assert.Equal(GeometryInterval.Exact(0), unadjusted.Z);
        var polygon = new SupportPolygon();
        polygon.Bind(scene.Owners().Single(), top);
        foreach (float cosine in new[] { 1f, 0.7f, 1f })
        {
            var request = FootSupport.DescribeFootprint(scene.Query) with { CosMaxSlope = cosine };
            Assert.Equal(GeometrySign.Zero, polygon.SourcePredicates.NormalSlope(polygon, 1));
            SupportQueryDiagnostics.Reset();
            Assert.Equal(GeometrySign.Positive, SupportFootprintPolygon.Measure(polygon, request, out var member));
            long hits = SharedMemberFactHits();
            Assert.True(hits > 0);
            Assert.Equal(BitConverter.DoubleToInt64Bits(cosine == 1 ? 1 : unadjusted.Y.Lower),
                BitConverter.DoubleToInt64Bits(member.UpwardLower));
            using (SupportReferenceEvaluation.Begin())
            {
                Assert.Equal(GeometrySign.Positive, SupportFootprintPolygon.Measure(polygon, request, out var reference));
                Assert.Equal(reference, member);
            }
            Assert.Equal(hits, SharedMemberFactHits());
        }
    }

    [Fact]
    public void RepresentedSlopeAdjustedNormalIsNeverShared()
    {
        using var counters = new WorldFormCounters();
        var world = new BepuPhysicsWorld(Vector3.Zero);
        StaticHandle owner = world.AddStatic(new BoxShape(new(0.9f, 0.2f, 1.1f)),
            new(new(0, -0.1f, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.37f)));
        using var scene = new WorldFormScene(world, new(Vector2.Zero, 0, 0.2f, 0.4f, 0.4f, 0.7f));
        scene.Find();
        InstalledSupportWorldFace top = scene.Form(owner)!.Leaves[0].Faces.ToArray()
            .Single(face => face.Orientation == GeometrySign.Positive);
        const float equalityCosine = 1, interiorCosine = 0.7f;
        GeometryVector unadjusted = GeometryVectorOperations.Normalize(top.Normal.Bounds);
        Assert.True(unadjusted.IsResolved);
        Assert.True(unadjusted.Y.Lower < equalityCosine,
            "The equality query must enter the slope-adjustment branch.");
        Assert.True(unadjusted.Y.Upper >= equalityCosine);
        Assert.True(unadjusted.Y.Lower > interiorCosine);
        AssertMemberFactInterval(unadjusted.Y, top.MemberFacts.NormalBounds.Y);
        _output.WriteLine($"Unadjusted upward [{unadjusted.Y.Lower:R}, {unadjusted.Y.Upper:R}], " +
            $"lower bits {BitConverter.DoubleToInt64Bits(unadjusted.Y.Lower):X16}.");

        // Read the installed pose and extents, then prove exact source slope equality using rational edges.
        var simulation = SimulationOf(world);
        ref var box = ref simulation.Shapes.GetShape<BepuPhysics.Collidables.Box>(HandlesOf(world)[owner.Value].Shape.Index);
        var matrix = InstalledPoseOracle.Represented(scene.StaticPose(owner).Orientation);
        var twice = new InstalledPoseOracle.R(2, 1);
        var edgeX = matrix.X * (InstalledPoseOracle.R.From(box.HalfWidth) * twice);
        var edgeZ = matrix.Z * (InstalledPoseOracle.R.From(box.HalfLength) * twice);
        var normal = InstalledPoseOracle.V.Cross(edgeZ, edgeX);
        Assert.Equal(InstalledPoseOracle.R.Zero, normal.X);
        Assert.Equal(InstalledPoseOracle.R.Zero, normal.Z);
        Assert.True(normal.Y > InstalledPoseOracle.R.Zero);
        Assert.Equal(normal.Y.Square(),
            InstalledPoseOracle.R.From(equalityCosine).Square() * normal.LengthSquared);

        var polygon = new SupportPolygon();
        polygon.Bind(owner, top);
        Assert.True(polygon.HasSource);
        Assert.Same(top.MemberFacts, polygon.SharedMemberFacts);
        Assert.Equal(GeometrySign.Zero, polygon.SourcePredicates.NormalSlope(polygon, equalityCosine));
        var json = new JsonSerializerOptions { IncludeFields = true };
        foreach (float cosine in new[] { equalityCosine, interiorCosine, equalityCosine })
        {
            var request = FootSupport.DescribeFootprint(scene.Query) with { CosMaxSlope = cosine };
            SupportQueryDiagnostics.Reset();
            Assert.Equal(GeometrySign.Positive, SupportFootprintPolygon.Measure(polygon, request, out var member));
            long hits = SharedMemberFactHits();
            Assert.True(hits > 0);
            Assert.Equal(BitConverter.DoubleToInt64Bits(cosine == equalityCosine ? equalityCosine : unadjusted.Y.Lower),
                BitConverter.DoubleToInt64Bits(member.UpwardLower));
            using (SupportReferenceEvaluation.Begin())
            {
                Assert.Equal(GeometrySign.Positive, SupportFootprintPolygon.Measure(polygon, request, out var reference));
                Assert.Equal(reference, member);
                Assert.Equal(JsonSerializer.Serialize(reference, json), JsonSerializer.Serialize(member, json));
            }
            Assert.Equal(hits, SharedMemberFactHits());
        }
        AssertMemberFactInterval(unadjusted.Y, top.MemberFacts.NormalBounds.Y);
    }

    static void AssertMemberFactInterval(GeometryInterval expected, GeometryInterval actual)
    {
        Assert.Equal(expected.IsResolved, actual.IsResolved);
        Assert.Equal(BitConverter.DoubleToInt64Bits(expected.Lower), BitConverter.DoubleToInt64Bits(actual.Lower));
        Assert.Equal(BitConverter.DoubleToInt64Bits(expected.Upper), BitConverter.DoubleToInt64Bits(actual.Upper));
    }

    static void AssertMemberFactNumber(FeatureNumber expected, FeatureNumber actual)
    {
        Assert.Equal(expected.IsResolved, actual.IsResolved);
        Assert.Equal(expected.IsExact, actual.IsExact);
        Assert.Equal(BitConverter.DoubleToInt64Bits(expected.Value), BitConverter.DoubleToInt64Bits(actual.Value));
        AssertMemberFactInterval(expected.Bounds, actual.Bounds);
    }

    static void AssertMemberFactOutput(GeometryVectorOutput expected, GeometryVectorOutput actual)
    {
        Assert.Equal(expected.IsResolved, actual.IsResolved);
        Assert.Equal(BitConverter.SingleToInt32Bits(expected.Value.X), BitConverter.SingleToInt32Bits(actual.Value.X));
        Assert.Equal(BitConverter.SingleToInt32Bits(expected.Value.Y), BitConverter.SingleToInt32Bits(actual.Value.Y));
        Assert.Equal(BitConverter.SingleToInt32Bits(expected.Value.Z), BitConverter.SingleToInt32Bits(actual.Value.Z));
        Assert.Equal(BitConverter.SingleToInt32Bits(expected.Error), BitConverter.SingleToInt32Bits(actual.Error));
    }

    static long SharedMemberFactHits() => SupportQueryDiagnostics.Snapshot().SharedMemberFacts;

    [Fact]
    public void SharedMemberFactsCountersSurviveThreadStateSubtractionAndMerge()
    {
        var prior = SupportQueryDiagnostics.Exchange(SupportQueryDiagnostics.ThreadState.Fresh(true));
        try
        {
            SupportQueryDiagnostics.Record(SupportQueryCounter.SharedMemberFacts, 3);
            SupportQueryCounts first = SupportQueryDiagnostics.Snapshot();
            SupportQueryDiagnostics.Record(SupportQueryCounter.SharedMemberFacts, 2);
            SupportQueryCounts delta = SupportQueryDiagnostics.Snapshot() - first;
            SupportQueryDiagnostics.Reset();
            SupportQueryDiagnostics.Merge(delta, default);
            Assert.Equal(2, SharedMemberFactHits());
            SupportQueryDiagnostics.Merge(first, default);
            Assert.Equal(5, SharedMemberFactHits());
        }
        finally { SupportQueryDiagnostics.Exchange(prior); }
    }

    [Fact]
    public void WorldFormMatchesReferenceOnEveryCostScene()
    {
        SupportCostRow[] rows = CostRows();
        using var counters = new WorldFormCounters();
        using var other = new WorldFormScene(rows.Single(row => row.Name == "stairs hull"));
        long coldBuilds = 0;
        foreach (SupportCostRow row in rows)
        {
            using var scene = new WorldFormScene(row);
            scene.Checked($"{row.Name} cold", out long builds);
            coldBuilds += builds;
            scene.Checked($"{row.Name} warm", out builds);
            Assert.True(builds == 0, $"{row.Name} warm built {builds} world forms.");
            other.Checked($"{row.Name} interleaved other", out _);
            scene.Checked($"{row.Name} interleaved", out builds);
            Assert.Equal(0, builds);
            scene.CheckedFootprint($"{row.Name} raw members");
            SupportQueryDiagnostics.Reset();
            scene.Find();
            long shared = SharedMemberFactHits();
            _output.WriteLine($"SharedMemberFacts {row.Name}: {shared}");
            if (row.Name is "flat box" or "flat hull" or "flat mesh" or "stairs box" or "stairs hull" or "stairs mesh")
                Assert.True(shared > 0, $"{row.Name} did not read stored member facts.");
            SupportQueryDiagnostics.Reset();
            using (SupportReferenceEvaluation.Begin()) scene.Find();
            Assert.Equal(0, SharedMemberFactHits());
            if (row.Name == "stairs box")
            {
                SupportQueryDiagnostics.Reset();
                scene.Find();
                Assert.True(SupportQueryDiagnostics.Snapshot().StairStratum > 0);
            }
            if (row.Name.StartsWith("flat ", StringComparison.Ordinal) ||
                row.Name.StartsWith("stairs ", StringComparison.Ordinal))
                scene.CheckedSubstep($"{row.Name} substep");
        }
        Assert.True(coldBuilds > 0);
    }

    [Fact]
    public void WarmFindBuildsNoWorldForm()
    {
        using var counters = new WorldFormCounters();
        using var scene = new WorldFormScene(CostRows().Single(row => row.Name == "flat box"));
        SupportSample cold = scene.Checked("cold", out long coldBuilds);
        SupportSample warm = scene.Checked("warm", out long warmBuilds);
        Assert.Equal(SupportStatus.Walkable, cold.Status);
        Assert.Equal(1, coldBuilds);
        Assert.Equal(0, warmBuilds);
        Assert.Equal(cold, warm);
    }

    [Fact]
    public void ChangedStaticPoseRebuildsWorldForm()
    {
        using var counters = new WorldFormCounters();
        using var scene = new WorldFormScene(CostRows().Single(row => row.Name == "flat box"));
        SupportSample before = scene.Checked("before", out _);
        scene.World.Rebase(new(0, 0.125f, 0));
        SupportSample after = scene.Checked("after", out long builds);
        Assert.Equal(1, builds);
        Assert.Equal(SupportStatus.Walkable, after.Status);
        Assert.Equal(before.Height - 0.125f, after.Height);
        scene.Checked("after warm", out builds);
        Assert.Equal(0, builds);
    }

    [Fact]
    public void PoseBitMismatchMaterializesWithoutPublishing()
    {
        using var counters = new WorldFormCounters();
        using var scene = new WorldFormScene(CostRows().Single(row => row.Name == "flat box"));
        StaticHandle owner = scene.Owners().Single();
        SupportSample published = scene.Checked("published", out _);
        InstalledSupportWorldForm form = scene.Form(owner)!;
        Assert.NotNull(form);
        int bound = scene.MaterializationPhases();
        Assert.True(bound > 0);

        // A signed zero is equal as a float and different as bits. A writer that skipped the version bump is
        // the only way to present either change under the published key.
        scene.SetPose(owner, pose => pose with { Position = pose.Position with { X = -0f } });
        Assert.Equal(0f, scene.StaticPose(owner).Position.X);
        SupportSample signedZero = scene.Checked("signed zero", out long builds);
        Assert.Equal(0, builds);
        Assert.Equal(published, signedZero);
        Assert.Equal(2 * bound, scene.MaterializationPhases());
        Assert.Same(form, scene.Form(owner));

        scene.SetPose(owner, pose => pose with { Position = pose.Position with { Y = pose.Position.Y - 0.125f } });
        SupportSample moved = scene.Checked("moved", out builds);
        Assert.Equal(0, builds);
        Assert.Equal(published.Height - 0.125f, moved.Height);
        Assert.Equal(2 * bound, scene.MaterializationPhases());
        Assert.Same(form, scene.Form(owner));
    }

    [Fact]
    public void CompoundChildPosesKeyTheWorldForm()
    {
        using var counters = new WorldFormCounters();
        var world = new BepuPhysicsWorld(Vector3.Zero);
        StaticHandle owner = world.AddStatic(new CompoundShape(
        [
            new CompoundChild(new BoxShape(new(2, 1, 2)), Pose.Identity),
            new CompoundChild(new BoxShape(new(0.25f, 0.05f, 0.25f)),
                new Pose(new(0.1f, 1.1f, 0.05f), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.37f))),
        ]), Pose.At(new(0, -1, 0)));
        using var scene = new WorldFormScene(world, new FootSupportQuery(Vector2.Zero, 0, 0.2f, 0.4f, 0.4f,
            MathF.Cos(MathF.PI / 4)));
        SupportSample rotated = scene.Checked("rotated child", out long builds);
        Assert.Equal(1, builds);
        Assert.Equal(SupportStatus.Walkable, rotated.Status);
        Assert.Equal(owner, rotated.Static);
        Assert.True(rotated.FeatureId >= SupportNeighborhoodPolyhedra.FaceStride, $"Feature {rotated.FeatureId}.");
        InstalledSupportWorldForm form = scene.Form(owner)!;
        Assert.NotNull(form);
        int bound = scene.MaterializationPhases();
        Assert.True(bound > 0);

        // Local topology compares child poses as floats, so a signed zero keeps it and only the form's child pose
        // key refuses it, which materializes without publishing.
        scene.SetChildPose(owner, 0, pose => pose with { Position = pose.Position with { X = -0f } });
        Assert.Equal(rotated, scene.Checked("signed zero child", out builds));
        Assert.Equal(0, builds);
        Assert.Equal(2 * bound, scene.MaterializationPhases());
        Assert.Same(form, scene.Form(owner));

        scene.SetChildPose(owner, 1, pose => pose with { Position = pose.Position with { Y = pose.Position.Y - 0.02f } });
        SupportSample lowered = scene.Checked("lowered child", out builds);
        Assert.Equal(1, builds);
        Assert.NotEqual(rotated.Height, lowered.Height);
        Assert.NotSame(form, scene.Form(owner));
    }

    [Fact]
    public void RebuiltWorldMatchesReference()
    {
        using var counters = new WorldFormCounters();
        foreach (SupportCostRow row in CostRows())
        {
            SupportSample first, warm, rebuilt;
            using (var scene = new WorldFormScene(row))
            {
                first = scene.Checked($"{row.Name} first", out _);
                warm = scene.Checked($"{row.Name} first warm", out _);
            }
            using (var scene = new WorldFormScene(row)) rebuilt = scene.Checked($"{row.Name} rebuilt", out _);
            Assert.True(first.ToString() == warm.ToString() && first.ToString() == rebuilt.ToString(),
                $"{row.Name}: first {first}, warm {warm}, rebuilt {rebuilt}.");
        }
    }

    [Fact]
    public void ConcurrentReadersShareOneWorldForm()
    {
        const int Readers = 3;
        using var counters = new WorldFormCounters();
        var query = new FootSupportQuery(new(0.1f, 0.2f), 0, 0.2f, 0.4f, 0.4f, MathF.Cos(MathF.PI / 4));
        using var scene = new WorldFormScene(MixedWorld(), query);
        BepuPhysicsWorld world = scene.World;
        SupportSample expected;
        using (var serial = new WorldFormScene(MixedWorld(), query))
        using (SupportReferenceEvaluation.Begin()) expected = serial.Find();
        Assert.Equal(SupportStatus.Walkable, expected.Status);

        var samples = new SupportSample[Readers];
        var curvedWork = new SupportCurvedWorkObservation[Readers];
        var observedWork = new int[Readers];
        var builds = new long[Readers];
        using (var barrier = new Barrier(Readers))
        {
            BoundedThread[] readers = [.. Enumerable.Range(0, Readers).Select(slot => BoundedThread.Start(() =>
            {
                ContactAllocationDiagnostics.SetObserver((phase, entering) =>
                {
                    // Every reader reaches its first cold lookup together, then builds unobserved like production.
                    if (!entering || phase != ContactAllocationPhase.SupportCapture) return;
                    ContactAllocationDiagnostics.SetObserver(null);
                    BoundedThread.Arrive(barrier);
                });
                using var readerCounters = new WorldFormCounters();
                SupportQueryDiagnostics.SetCurvedWorkObserver(observation =>
                { curvedWork[slot] = observation; observedWork[slot]++; });
                try
                {
                    using IPhysicsQueryLease lease = world.AcquireQueryReadLease();
                    samples[slot] = FootSupport.Find(null, world, lease, query);
                    builds[slot] = SupportQueryDiagnostics.Snapshot().WorldFormBuilds;
                }
                finally
                {
                    ContactAllocationDiagnostics.SetObserver(null);
                    SupportQueryDiagnostics.SetCurvedWorkObserver(null);
                }
            }))];
            foreach (BoundedThread reader in readers) reader.Join();
        }
        Assert.All(samples, sample => Assert.Equal(expected.ToString(), sample.ToString()));
        Assert.All(observedWork, count => Assert.Equal(1, count));
        Assert.All(curvedWork, observation =>
        {
            Assert.Equal(CapsuleFeatureStatus.Complete, observation.FootprintStatus);
            Assert.Equal(SupportCurvedWorkFailure.None, observation.Failure);
            Assert.Equal(observation.Consumed, SupportCurvedWorkTests.Sum(observation));
        });
        StaticHandle[] owners = scene.Owners();
        Assert.Equal(owners.Length, builds.Sum());
        Assert.All(owners, owner => Assert.NotNull(scene.Form(owner)));
        InstalledSupportWorldForm[] forms = [.. owners.Select(owner => scene.Form(owner)!)];
        Assert.Equal(expected.ToString(), scene.Checked("serial after readers", out long serialBuilds).ToString());
        Assert.Equal(0, serialBuilds);
        Assert.All(owners.Zip(forms), pair => Assert.Same(pair.Second, scene.Form(pair.First)));
    }

    [Fact]
    public void ObservedBuildPublishesFirstWins()
    {
        using var counters = new WorldFormCounters();
        using var scene = new WorldFormScene(MixedWorld(),
            new FootSupportQuery(new(0.1f, 0.2f), 0, 0.2f, 0.4f, 0.4f, MathF.Cos(MathF.PI / 4)));
        StaticHandle owner = scene.Owners()[0];
        SupportSample? nested = null;
        InstalledSupportWorldForm? first = null;
        var curvedWork = new List<SupportCurvedWorkObservation>();
        SupportSample outer;
        using (IPhysicsQueryLease lease = scene.World.AcquireQueryReadLease())
        {
            ContactAllocationDiagnostics.SetObserver((phase, entering) =>
            {
                // The outer build of this static's form is running when the same thread misses the same form again.
                if (nested is not null || !entering || phase != ContactAllocationPhase.SupportMaterialization) return;
                nested = default(SupportSample);
                var prior = SupportQueryDiagnostics.Exchange(SupportQueryDiagnostics.ThreadState.Fresh(true) with
                { CurvedWork = curvedWork.Add });
                SupportQueryCounts counts;
                SupportRouteCounts routes;
                try
                {
                    nested = FootSupport.Find(null, scene.World, lease, scene.Query);
                    counts = SupportQueryDiagnostics.Snapshot();
                    routes = SupportQueryDiagnostics.Routes;
                }
                finally { SupportQueryDiagnostics.Exchange(prior); }
                SupportQueryDiagnostics.Merge(counts, routes);
                first = scene.Form(owner);
            });
            SupportQueryDiagnostics.SetCurvedWorkObserver(curvedWork.Add);
            try { outer = FootSupport.Find(null, scene.World, lease, scene.Query); }
            finally
            {
                ContactAllocationDiagnostics.SetObserver(null);
                SupportQueryDiagnostics.SetCurvedWorkObserver(null);
            }
            Assert.Equal(2, curvedWork.Count);
            Assert.All(curvedWork, observation => Assert.Equal(CapsuleFeatureStatus.Complete, observation.FootprintStatus));
        }
        long builds = SupportQueryDiagnostics.Snapshot().WorldFormBuilds;
        SupportSample reference;
        using (SupportReferenceEvaluation.Begin()) reference = scene.Find();
        Assert.Equal(reference.ToString(), nested!.Value.ToString());
        Assert.Equal(reference.ToString(), outer.ToString());
        Assert.Equal(scene.Owners().Length + 1, builds);
        Assert.NotNull(first);
        Assert.Same(first, scene.Form(owner));
        scene.Checked("warm after observed", out long warmBuilds);
        Assert.Equal(0, warmBuilds);
        Assert.Same(first, scene.Form(owner));
    }

    [Theory]
    [InlineData("flat box")]
    [InlineData("flat hull")]
    [InlineData("stairs box")]
    public void WarmFindWithWorldFormAllocatesNothing(string name)
    {
        using var counters = new WorldFormCounters();
        using var scene = new WorldFormScene(CostRows().Single(row => row.Name == name));
        using IPhysicsQueryLease lease = scene.AcquireLease();
        SupportSample cold = scene.Find(lease);
        Assert.True(SupportQueryDiagnostics.Snapshot().WorldFormBuilds > 0);
        // One unmeasured call primes the bind path, as the cost sampler primes every row.
        SupportQueryDiagnostics.Reset();
        Assert.Equal(cold.ToString(), scene.Find(lease).ToString());
        Assert.Equal(0, SupportQueryDiagnostics.Snapshot().WorldFormBuilds);
        SupportSample warm = default;
        long bytes = AllocationMeasurement.Measure($"{nameof(WarmFindWithWorldFormAllocatesNothing)} {name}", () =>
            warm = scene.Find(lease));
        Assert.Equal(0, bytes);
        Assert.Equal(0, SupportQueryDiagnostics.Snapshot().WorldFormBuilds);
        Assert.Equal(cold.ToString(), warm.ToString());
    }

    static SupportCostRow[] CostRows() => [.. SupportGateCostScenes.Rows().Select(row => (SupportCostRow)row[0])];

    // A box, a hull and a compound of two boxes, each under the query's footprint.
    static BepuPhysicsWorld MixedWorld()
    {
        var world = new BepuPhysicsWorld(Vector3.Zero);
        world.AddStatic(new BoxShape(new(2, 1, 2)), Pose.At(new(0, -1, 0)));
        world.AddStatic(new ConvexHullShape(
        [
            new(-0.3f, -0.1f, -0.3f), new(0.3f, -0.1f, -0.3f), new(-0.3f, -0.1f, 0.3f), new(0.3f, -0.1f, 0.3f),
            new(-0.3f, 0.02f, -0.3f), new(0.3f, 0.02f, -0.3f), new(-0.3f, 0.02f, 0.3f), new(0.3f, 0.02f, 0.3f),
        ]), Pose.At(new(0.2f, 0, 0.1f)));
        world.AddStatic(new CompoundShape(
        [
            new CompoundChild(new BoxShape(new(0.1f, 0.03f, 0.1f)), Pose.At(new(-0.1f, 0, 0))),
            new CompoundChild(new BoxShape(new(0.1f, 0.03f, 0.1f)),
                new Pose(new(0.1f, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.3f))),
        ]), Pose.At(new(0.1f, 0.01f, 0.2f)));
        return world;
    }

    /// <summary>Enables this thread's support counters for its lifetime.</summary>
    sealed class WorldFormCounters : IDisposable
    {
        internal WorldFormCounters()
        {
            SupportQueryDiagnostics.Enable(true);
            SupportQueryDiagnostics.Reset();
        }

        public void Dispose() => SupportQueryDiagnostics.Enable(false);
    }

    /// <summary>One world and one Find request, with world form lookups and live pose writers that bypass the
    /// static geometry version.</summary>
    sealed class WorldFormScene : IDisposable
    {
        readonly SupportFootprintScene? _scene;
        readonly IPhysicsWorldQueryView? _view;
        readonly SupportCostRow? _row;
        readonly Func<float, float, float>? _height;
        readonly Func<float, float, Vector3>? _normal;

        internal BepuPhysicsWorld World { get; }
        internal FootSupportQuery Query { get; }

        internal WorldFormScene(SupportCostRow row)
        {
            _row = row;
            if (row.Analytic)
            {
                _height = (x, _) => row.HeightAt(x);
                _normal = (x, _) => row.NormalAt(x);
            }
            _scene = SupportGateCostScenes.Build(row);
            World = _scene.World;
            _view = World.CreateQueryViewExcludingStatics([]);
            Query = row.Query;
        }

        internal WorldFormScene(BepuPhysicsWorld world, FootSupportQuery query)
        {
            World = world;
            Query = query;
        }

        internal IPhysicsWorld Target => _view ?? (IPhysicsWorld)World;

        internal IPhysicsQueryLease AcquireLease() => ((IPhysicsQueryLeaseSource)Target).AcquireQueryReadLease();

        internal SupportSample Find()
        {
            using IPhysicsQueryLease lease = AcquireLease();
            return Find(lease);
        }

        internal SupportSample Find(IPhysicsQueryLease lease) =>
            FootSupport.Find(_height, _normal, Target, lease, Query);

        /// <summary>One Find checked against the reference, with the world forms the optimized run built. The
        /// reference run builds none.</summary>
        internal SupportSample Checked(string context, out long builds)
        {
            long optimized = -1;
            SupportSample sample = SupportFootprintEquivalence.AssertMatchesReference(() =>
            {
                SupportQueryDiagnostics.Reset();
                SupportSample found = Find();
                long built = SupportQueryDiagnostics.Snapshot().WorldFormBuilds;
                if (SupportReferenceEvaluation.Active) Assert.True(built == 0, $"{context}: the reference built {built}.");
                else optimized = built;
                return found;
            }, context);
            builds = optimized;
            return sample;
        }

        /// <summary>The raw complete footprint of this Find's request, checked member by member.</summary>
        internal void CheckedFootprint(string context)
        {
            using IPhysicsQueryLease lease = AcquireLease();
            SupportFootprintEquivalence.AssertMatchesReference((IPhysicsSupportFootprint)Target, lease,
                FootSupport.DescribeFootprint(Query), context);
        }

        /// <summary>One ground substep from the cost row's feet along its direction, checked with every support read.</summary>
        internal void CheckedSubstep(string context)
        {
            SupportCostRow row = _row!;
            MoveTuning tuning = SupportGateCostScenes.Tuning;
            using IPhysicsQueryLease lease = AcquireLease();
            SupportFootprintEquivalence.AssertMatchesReference(() => GroundCore.Step(row.Feet,
                row.Direction * tuning.WalkSpeed * row.Dt, row.Dt, tuning, new(),
                DryQueries.For(_height, _normal, Target, lease, tuning)), context);
        }

        /// <summary>The materialization phase entries of one optimized Find. Binding a published form enters the
        /// phase once per installed static visit, and the reference materialization enters it twice, once for the
        /// leaves and once for their polygons.</summary>
        internal int MaterializationPhases()
        {
            int entries = 0;
            ContactAllocationDiagnostics.SetObserver((phase, entering) =>
            {
                if (entering && phase == ContactAllocationPhase.SupportMaterialization) entries++;
            });
            try { Find(); }
            finally { ContactAllocationDiagnostics.SetObserver(null); }
            return entries;
        }

        internal StaticHandle[] Owners() => [.. HandlesOf(World).Keys.Order().Select(id => new StaticHandle(id))];

        /// <summary>The owner's published world form, read through the installed geometry cache's own lookup.</summary>
        internal InstalledSupportWorldForm? Form(StaticHandle owner)
        {
            var cache = (InstalledSupportGeometryCache)typeof(BepuPhysicsWorld)
                .GetField("_installedSupportGeometry", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(World)!;
            long version = (long)typeof(BepuPhysicsWorld)
                .GetField("_staticGeometryVersion", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(World)!;
            Assert.Equal(CapsuleFeatureStatus.Complete, cache.GetOrCapture(SimulationOf(World), owner,
                HandlesOf(World)[owner.Value].Shape, version, out InstalledSupportGeometry? geometry));
            return geometry!.WorldForm;
        }

        internal RigidPose StaticPose(StaticHandle owner)
        {
            SimulationOf(World).Statics.GetDescription(HandlesOf(World)[owner.Value].Handle, out StaticDescription description);
            return description.Pose;
        }

        internal void SetPose(StaticHandle owner, Func<RigidPose, RigidPose> change)
        {
            BepuPhysics.Simulation simulation = SimulationOf(World);
            BepuPhysics.StaticHandle handle = HandlesOf(World)[owner.Value].Handle;
            simulation.Statics.GetDescription(handle, out StaticDescription description);
            description.Pose = change(description.Pose);
            simulation.Statics.ApplyDescription(handle, description);
        }

        internal void SetChildPose(StaticHandle owner, int child, Func<RigidPose, RigidPose> change)
        {
            ref BepuCompound compound = ref SimulationOf(World).Shapes
                .GetShape<BepuCompound>(HandlesOf(World)[owner.Value].Shape.Index);
            compound.Children[child].LocalPose = change(compound.Children[child].LocalPose);
        }

        public void Dispose()
        {
            _view?.Dispose();
            if (_scene is not null) _scene.Dispose();
            else World.Dispose();
        }
    }
}
