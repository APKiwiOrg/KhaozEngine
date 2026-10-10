using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Tests.Locomotion.Contacts;
using Xunit;

namespace KhaozEngine.Tests.Physics;

// Every probe pose is computed from the installed geometry in binary64. Expectations come from that geometry or
// from the binary64 oracle, never from a query result.
public class SupportNeighborhoodTests
{
    // SupportCertification.ContactBand, which this project cannot reference.
    const float Band = 0.0001f;
    static readonly CapsuleShape Probe = new(0.2f, 0.01f);
    static double Radius => Probe.Radius;
    static double Half => Probe.Length * 0.5;

    [Fact]
    public void BoxTopFaceIsTheOnlyMemberAboveItsCentre()
    {
        using var scene = new Scene();
        StaticHandle box = scene.World.AddStatic(new BoxShape(new Vector3(0.5f)), Pose.Identity);
        Query query = scene.Query(scene.World, Lowest(0, 0.5 + Radius, 0));

        Assert.Equal(CapsuleFeatureStatus.Complete, query.Result.Status);
        SupportElement top = Assert.Single(query.Elements);
        Assert.Empty(query.Joins);
        Assert.Equal(box, top.Static);
        Assert.Equal(SupportElementKind.Polygon, top.Kind);
        Assert.Equal(SupportNeighborhoodOracle.BoxTop, top.ElementId);
        Assert.True((top.Normal - Vector3.UnitY).Length() <= top.NormalError, "The top normal is +Y.");
        Assert.True(Math.Abs(top.Witness.Y - 0.5) <= top.PositionErrorMetres, "The witness lies on the top.");
        Assert.True(top.SeparationLower <= Band);
    }

    [Fact]
    public void BoxCornerPublishesTopAndBothSides()
    {
        using var scene = new Scene();
        StaticHandle box = scene.World.AddStatic(new BoxShape(new Vector3(0.5f)), Pose.Identity);
        Query query = scene.Query(scene.World, Corner());

        Assert.Equal(CapsuleFeatureStatus.Complete, query.Result.Status);
        Assert.Equal([SupportNeighborhoodOracle.BoxMinusX, SupportNeighborhoodOracle.BoxTop,
            SupportNeighborhoodOracle.BoxMinusZ], Array.ConvertAll(query.Elements, e => e.ElementId));
        Assert.All(query.Elements, e => Assert.Equal(box, e.Static));
        // The corner is convex: the top joins each side, and the two sides join each other.
        Assert.Equal([new Join(0, 1), new Join(0, 2), new Join(1, 2)], query.Joins);
    }

    [Fact]
    public void NosingPoseIncludesTreadAndRiser()
    {
        // The #1342 box pose: the foot probe's rim reaches 2.1e-7 past the riser at x 0.4.
        using FootSupportScene stairs = FootSupportScenes.Stairs(SceneVariant.Box, 0.40f, 0.25f, 2);
        const float axis = 0.20000021f;
        double riser = 0.40f, inside = riser - axis;
        Assert.InRange(Radius - inside, 2e-7, 2.2e-7);
        // The rim touches the nosing edge (riser, 0.5) from above.
        Pose pose = Lowest(axis, 0.5 + Math.Sqrt(Radius * Radius - inside * inside), 0);
        using var view = stairs.World.CreateQueryViewExcludingStatics([]);
        using IPhysicsQueryLease lease = ((IPhysicsQueryLeaseSource)view).AcquireQueryReadLease();
        Query query = Query.Run(view, lease, pose);

        Assert.Equal(CapsuleFeatureStatus.Complete, query.Result.Status);
        StaticHandle step = stairs["step2"];
        Assert.Contains(query.Elements, e => e.Static == step && e.ElementId == SupportNeighborhoodOracle.BoxTop);
        Assert.Contains(query.Elements, e => e.Static == step && e.ElementId == SupportNeighborhoodOracle.BoxMinusX);
    }

    [Fact]
    public void TwoBoxRidgeIsJoinedAcrossStatics()
    {
        using FootSupportScene ridge = FootSupportScenes.TwoStaticRidge(SceneVariant.Box, 30);
        Query joined = RidgeQuery(ridge, 0);
        int left = TopIndex(joined, ridge["left"]), right = TopIndex(joined, ridge["right"]);
        Assert.Contains(new Join(Math.Min(left, right), Math.Max(left, right)), joined.Joins);

        // The same ridge with the right slab 1e-3 further out: both tops still reach the probe, and do not join.
        float angle = 30 * MathF.PI / 180;
        Vector3 half = new(MathF.Cos(angle), MathF.Sin(angle), 0);
        using FootSupportScene gap = new FootSupportScene(SceneVariant.Box)
            .Slab("left", new Vector3(-half.X, 0.5f - half.Y, 0), angle, 1, 2)
            .Slab("right", new Vector3(half.X + 1e-3f, 0.5f - half.Y, 0), -angle, 1, 2);
        Query apart = RidgeQuery(gap, 0.0005f);
        left = TopIndex(apart, gap["left"]);
        right = TopIndex(apart, gap["right"]);
        Assert.DoesNotContain(new Join(Math.Min(left, right), Math.Max(left, right)), apart.Joins);
    }

    [Fact]
    public void YawedRidgeGapIsNotJoined()
    {
        // The 30 degree two slab ridge yawed 45 degrees about Y. Yaw leaves the ridge line's rounded directions
        // only nearly parallel, which is where a whole-edge enclosure would bridge a crack.
        Query shared = YawedRidgeQuery(0, out StaticHandle left, out StaticHandle right);
        int a = TopIndex(shared, left), b = TopIndex(shared, right);
        Assert.Contains(new Join(Math.Min(a, b), Math.Max(a, b)), shared.Joins);

        Query apart = YawedRidgeQuery(1e-3f, out left, out right);
        a = TopIndex(apart, left);
        b = TopIndex(apart, right);
        Assert.DoesNotContain(new Join(Math.Min(a, b), Math.Max(a, b)), apart.Joins);
    }

    [Fact]
    public void SunkRampIsNotJoined()
    {
        // A ramp rising 0.5 over 2, sunk 0.05 into a floor whose top is y 0. Its top meets the floor at x 0.2.
        const float rise = 0.5f, sink = 0.05f;
        float angle = MathF.Atan2(rise, 2);
        using FootSupportScene scene = new FootSupportScene(SceneVariant.Box)
            .Flat("floor", -2, 2, -2, 2, 0)
            .Slab("ramp", new Vector3(1, rise / 2 - sink, 0), angle, MathF.Sqrt(4 + rise * rise) / 2, 2);
        // The probe sits in the valley, tangent to both planes: radius above the floor, radius from the ramp.
        double a = angle, meet = sink / Math.Tan(a);
        Pose pose = Lowest(meet - Radius * Math.Tan(a / 2), Radius, 0);
        using var view = scene.World.CreateQueryViewExcludingStatics([]);
        using IPhysicsQueryLease lease = ((IPhysicsQueryLeaseSource)view).AcquireQueryReadLease();
        Query query = Query.Run(view, lease, pose);

        Assert.Equal(CapsuleFeatureStatus.Complete, query.Result.Status);
        int floor = TopIndex(query, scene["floor"]), ramp = TopIndex(query, scene["ramp"]);
        Assert.DoesNotContain(new Join(Math.Min(floor, ramp), Math.Max(floor, ramp)), query.Joins);
    }

    [Fact]
    public void SlabCrossingTheFloorPlaneIsNotJoined()
    {
        // A floor whose top is y 0 over x at most 0, and a slab falling 20 degrees to +X whose top plane passes
        // through the floor's edge line. Starting at that edge the pair is a convex crease and joins. Moved 0.15 up
        // its own slope, the slab's top crosses the floor's plane at the same line: the floor still lies below the
        // slab's plane, and only the slab's part above the floor's plane separates them.
        Assert.True(SlabJoined(0), "The slab starting at the floor's edge joins the floor.");
        Assert.False(SlabJoined(0.15f), "The slab crossing the floor's plane does not join it.");
    }

    [Fact]
    public void YawedBoxElementsMatchTheOracle()
    {
        using var scene = new Scene();
        var half = new Vector3(0.6f, 0.3f, 0.4f);
        var pose = new Pose(new Vector3(1.25f, 0.5f, -0.75f), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.6f));
        StaticHandle box = scene.World.AddStatic(new BoxShape(half), pose);
        List<OracleFace> faces = SupportNeighborhoodOracle.Box(box, half, pose);
        // The top -X -Z corner, approached along the box's own outward diagonal.
        OracleVector vertex = OracleVector.Rotate(pose.Orientation, new OracleVector(-half.X, half.Y, -half.Z)) +
            OracleVector.From(pose.Position);
        OracleVector outward = OracleVector.Rotate(pose.Orientation, new OracleVector(-1, 1, -1)).Unit;
        OracleVector lowest = vertex + outward * Radius;
        Pose probe = Lowest(lowest.X, lowest.Y, lowest.Z);
        Query query = scene.Query(scene.World, probe);

        Assert.Equal(CapsuleFeatureStatus.Complete, query.Result.Status);
        (OracleVector lower, OracleVector upper) = Segment(probe);
        Assert.Equal(3, SupportNeighborhoodOracle.AssertMatches(query.Elements, faces, lower, upper, Radius, Band));
        Assert.Equal(3, query.Elements.Length);
        Assert.Equal(3, query.Joins.Length);
    }

    [Fact]
    public void CompoundLeavesContributeIndependently()
    {
        using var scene = new Scene();
        var cube = new BoxShape(new Vector3(0.5f));
        // Leaf 1 is a sphere out of the probe's band. It publishes nothing and never refuses the box leaves beside it.
        StaticHandle compound = scene.World.AddStatic(new CompoundShape(
        [
            new(cube, Pose.At(new Vector3(-0.5f, 0, 0))),
            new(new SphereShape(0.5f), Pose.At(new Vector3(0, 0, 0.6f))),
            new(cube, Pose.At(new Vector3(0.5f, 0, 0))),
        ]), Pose.Identity);
        Query query = scene.Query(scene.World, Lowest(0, 0.5 + Radius, 0));

        Assert.Equal(CapsuleFeatureStatus.Complete, query.Result.Status);
        Assert.All(query.Elements, e => Assert.Equal(compound, e.Static));
        int stride = SupportNeighborhoodPolyhedraStride;
        Assert.Contains(query.Elements, e => e.ElementId == 0 * stride + SupportNeighborhoodOracle.BoxTop);
        Assert.Contains(query.Elements, e => e.ElementId == 2 * stride + SupportNeighborhoodOracle.BoxTop);
        Assert.DoesNotContain(query.Elements, e => e.ElementId / stride == 1);
    }

    [Fact]
    public void NeighborhoodHonoursViewExclusions()
    {
        using var scene = new Scene();
        var slab = new BoxShape(new Vector3(0.5f));
        StaticHandle left = scene.World.AddStatic(slab, Pose.At(new Vector3(-0.5f, 0, 0)));
        StaticHandle right = scene.World.AddStatic(slab, Pose.At(new Vector3(0.5f, 0, 0)));
        Pose seam = Lowest(0, 0.5 + Radius, 0);

        Query both = scene.Query(scene.World, seam);
        Assert.Contains(both.Elements, e => e.Static == left);
        Assert.Contains(both.Elements, e => e.Static == right);

        IPhysicsWorldQueryView withoutLeft = scene.View(left);
        Query visible = scene.Query(withoutLeft, seam);
        Assert.Equal(CapsuleFeatureStatus.Complete, visible.Result.Status);
        Assert.NotEmpty(visible.Elements);
        Assert.All(visible.Elements, e => Assert.Equal(right, e.Static));

        IPhysicsWorldQueryView neither = scene.View(left, right);
        Query none = scene.Query(neither, seam);
        Assert.Equal(CapsuleFeatureStatus.Complete, none.Result.Status);
        Assert.Empty(none.Elements);
    }

    [Fact]
    public void DynamicBodiesAreNotMembers()
    {
        using var scene = new Scene();
        StaticHandle floor = scene.World.AddStatic(new BoxShape(new Vector3(4, 0.5f, 4)), Pose.At(new Vector3(0, -0.5f, 0)));
        // A dynamic box whose -X face stands exactly one probe radius from the probe, resting on the floor.
        scene.World.AddDynamic(new BoxShape(new Vector3(0.5f)), Pose.At(new Vector3(0.7f, 0.5f, 0)),
            new DynamicBodyDescription(1f) { SleepThreshold = 0f });
        Pose pose = Lowest(0, Radius, 0);

        foreach (QueryFilter filter in (QueryFilter[])[QueryFilter.All, QueryFilter.StaticsOnly])
        {
            Query query = scene.Query(scene.World, pose, filter);
            Assert.Equal(CapsuleFeatureStatus.Complete, query.Result.Status);
            SupportElement top = Assert.Single(query.Elements);
            Assert.Equal(floor, top.Static);
            Assert.Equal(SupportNeighborhoodOracle.BoxTop, top.ElementId);
        }
    }

    [Fact]
    public void CapacityRefusesAtomically()
    {
        using var scene = new Scene();
        scene.World.AddStatic(new BoxShape(new Vector3(0.5f)), Pose.Identity);
        IPhysicsSupportNeighborhood capability = scene.Capability(scene.World);
        // The corner has three members, so its join matrix is three rows of one word.
        SupportElement[] elements = ElementSentinels(1), originalElements = (SupportElement[])elements.Clone();
        ulong[] joins = JoinSentinels(8), originalJoins = (ulong[])joins.Clone();
        AssertRefused(capability.QuerySupportNeighborhood(scene.Lease, Probe, Corner(), Band, elements, joins),
            CapsuleFeatureStatus.CapacityExceeded, 3);
        Assert.Equal(originalElements, elements);
        Assert.Equal(originalJoins, joins);

        // Exactly the matrix for the member count fits.
        elements = ElementSentinels(3);
        joins = JoinSentinels(3);
        SupportNeighborhoodResult exact = capability.QuerySupportNeighborhood(scene.Lease, Probe, Corner(), Band,
            elements, joins);
        Assert.Equal(CapsuleFeatureStatus.Complete, exact.Status);
        Assert.Equal(3, exact.Elements);
        Assert.Equal(1, exact.JoinWordsPerRow);
        Assert.Equal([0b110UL, 0b101UL, 0b011UL], joins);
    }

    // The join span must hold the matrix for the whole element span, elements.Length * JoinWordsFor(elements.Length)
    // words, whatever the member count turns out to be. A shorter span is argument misuse and throws before anything
    // is written, on the owner and on a view.
    [Fact]
    public void JoinSpanShorterThanDocumentedThrows()
    {
        using var scene = new Scene();
        scene.World.AddStatic(new BoxShape(new Vector3(0.5f)), Pose.Identity);
        foreach (IPhysicsWorld receiver in new IPhysicsWorld[] { scene.World, scene.View() })
        {
            IPhysicsSupportNeighborhood capability = scene.Capability(receiver);
            foreach ((int elementCount, int joinCount) in new[] { (8, 7), (3, 2), (65, 129) })
            {
                SupportElement[] elements = ElementSentinels(elementCount);
                SupportElement[] originalElements = (SupportElement[])elements.Clone();
                ulong[] joins = JoinSentinels(joinCount), originalJoins = (ulong[])joins.Clone();
                Assert.Throws<ArgumentException>(() => capability.QuerySupportNeighborhood(scene.Lease, Probe,
                    Corner(), Band, elements, joins));
                Assert.Equal(originalElements, elements);
                Assert.Equal(originalJoins, joins);
            }
            // The documented size for the element span is enough.
            Assert.Equal(CapsuleFeatureStatus.Complete, capability.QuerySupportNeighborhood(scene.Lease, Probe,
                Corner(), Band, ElementSentinels(65), JoinSentinels(130)).Status);
        }
    }

    [Fact]
    public void ResultIsBoundToItsLeaseAndReceiver()
    {
        using var scene = new Scene();
        StaticHandle box = scene.World.AddStatic(new BoxShape(new Vector3(0.5f)), Pose.Identity);
        using IPhysicsWorldQueryView view = scene.World.CreateQueryViewExcludingStatics([]);
        using IPhysicsWorldQueryView excluded = scene.World.CreateQueryViewExcludingStatics([box]);
        IPhysicsSupportNeighborhood owner = scene.Capability(scene.World), selected = scene.Capability(view);
        IPhysicsSupportNeighborhood other = scene.Capability(excluded);
        Pose pose = Lowest(0, 0.5 + Radius, 0);

        SupportNeighborhoodResult own = scene.Query(scene.World, pose).Result;
        owner.AssertNeighborhoodCurrent(own, scene.Lease);
        SupportNeighborhoodResult viewed = scene.Query(view, pose).Result;
        selected.AssertNeighborhoodCurrent(viewed, scene.Lease);
        Assert.Same(scene.World, viewed.SourceWorld);
        Assert.Same(view, viewed.QueryWorld);
        Assert.Same(scene.Lease, viewed.Lease);

        // Matching source metadata does not authorize another receiver.
        Assert.Throws<InvalidOperationException>(() => owner.AssertNeighborhoodCurrent(viewed, scene.Lease));
        Assert.Throws<InvalidOperationException>(() => other.AssertNeighborhoodCurrent(viewed, scene.Lease));
        Assert.Throws<InvalidOperationException>(() => selected.AssertNeighborhoodCurrent(default(SupportNeighborhoodResult),
            scene.Lease));

        // A lease from another owner is refused before anything is written.
        using var foreignWorld = new BepuPhysicsWorld(Vector3.Zero);
        using IPhysicsQueryLease foreign = foreignWorld.AcquireQueryReadLease();
        SupportElement[] elements = ElementSentinels(4), original = (SupportElement[])elements.Clone();
        Assert.ThrowsAny<ArgumentException>(() => selected.QuerySupportNeighborhood(foreign, Probe, pose, Band,
            elements, JoinSentinels(4)));
        Assert.Equal(original, elements);

        // A later same-generation lease cannot revive the result, and an expired lease refuses both calls.
        scene.Lease.Dispose();
        using IPhysicsQueryLease next = ((IPhysicsQueryLeaseSource)view).AcquireQueryReadLease();
        Assert.Equal(viewed.GeometryGeneration, next.GeometryGeneration);
        Assert.Equal(viewed.Origin, next.Origin);
        Assert.Throws<InvalidOperationException>(() => selected.AssertNeighborhoodCurrent(viewed, next));
        Assert.ThrowsAny<InvalidOperationException>(() => selected.AssertNeighborhoodCurrent(viewed, scene.Lease));
        Assert.ThrowsAny<InvalidOperationException>(() => selected.QuerySupportNeighborhood(scene.Lease, Probe, pose,
            Band, elements, JoinSentinels(4)));
        Assert.Equal(original, elements);
    }

    [Fact]
    public void PosesOutsideTheDomainAreUnsupportedAndLayerFiltersThrow()
    {
        using var scene = new Scene();
        scene.World.AddStatic(new BoxShape(new Vector3(0.5f)), Pose.Identity);
        IPhysicsSupportNeighborhood capability = scene.Capability(scene.World);
        Pose upright = Lowest(0, 0.5 + Radius, 0);
        var tilted = new Pose(upright.Position, new Quaternion(0.5f, 0.5f, 0.5f, 0.5f));
        SupportElement[] elements = ElementSentinels(4), original = (SupportElement[])elements.Clone();
        AssertRefused(capability.QuerySupportNeighborhood(scene.Lease, Probe, tilted, Band, elements, JoinSentinels(4)),
            CapsuleFeatureStatus.Unsupported);
        Assert.Throws<ArgumentException>(() => capability.QuerySupportNeighborhood(scene.Lease, Probe, upright, Band,
            elements, JoinSentinels(4), new QueryFilter(QueryMobility.Statics, 1)));
        AssertRefused(capability.QuerySupportNeighborhood(scene.Lease, Probe, upright, 0.02f, elements,
            JoinSentinels(4)), CapsuleFeatureStatus.Unsupported);
        Assert.Equal(original, elements);
    }

    [Fact]
    public void PolyhedronMembershipMatchesBruteForceOracle()
    {
        var random = new Random(1438);
        int poses = 0;
        for (int solid = 0; solid < 50; solid++)
        {
            using var scene = new Scene();
            Quaternion rotation = Quaternion.Normalize(new Quaternion(Signed(random), Signed(random), Signed(random),
                Signed(random)));
            var position = new Vector3(Signed(random), Signed(random), Signed(random));
            List<OracleFace> faces;
            if (solid % 2 == 0)
            {
                var half = new Vector3(Between(random, 0.15, 0.8), Between(random, 0.15, 0.8), Between(random, 0.15, 0.8));
                var pose = new Pose(position, rotation);
                faces = SupportNeighborhoodOracle.Box(scene.World.AddStatic(new BoxShape(half), pose), half, pose);
            }
            else
            {
                var points = new Vector3[10];
                float scale = Between(random, 0.3, 0.8);
                for (int i = 0; i < points.Length; i++)
                {
                    OracleVector direction = new OracleVector(Signed(random), Signed(random), Signed(random)).Unit;
                    points[i] = new Vector3((float)direction.X, (float)direction.Y, (float)direction.Z) * scale;
                }
                StaticHandle hull = scene.World.AddStatic(new ConvexHullShape(points), new Pose(position, rotation));
                faces = SupportNeighborhoodOracle.InstalledHull(scene.World, hull);
            }
            for (int sample = 0; sample < 10; sample++, poses++)
            {
                OracleFace face = faces[random.Next(faces.Count)];
                OracleVector target = SurfacePoint(random, face);
                // Outward from the chosen face, tilted at random, offset from the target by a gap from one band
                // inside to three bands out, so both edges of the band are crossed.
                OracleVector tilt = new OracleVector(Signed(random), Signed(random), Signed(random)) * 0.7;
                OracleVector outward = (face.Normal + tilt).Unit;
                if (OracleVector.Dot(outward, face.Normal) < 0.2) outward = face.Normal;
                double gap = Between(random, -Band, 3 * Band);
                OracleVector lowest = target + outward * (Radius + gap);
                Pose probe = Lowest(lowest.X, lowest.Y, lowest.Z);
                Query query = scene.Query(scene.World, probe);
                Assert.Equal(CapsuleFeatureStatus.Complete, query.Result.Status);
                (OracleVector lower, OracleVector upper) = Segment(probe);
                SupportNeighborhoodOracle.AssertMatches(query.Elements, faces, lower, upper, Radius, Band);
                // The face is no farther than the target, so a gap certainly inside the band makes it a member.
                if (gap <= Band - SupportNeighborhoodOracle.Geometry)
                    Assert.True(SupportNeighborhoodOracle.IndexOf(query.Elements, face) >= 0,
                        $"Pose {poses}: the face the probe was placed against is a member.");
            }
        }
        Assert.Equal(500, poses);
    }

    // SupportNeighborhoodPolyhedra.FaceStride, the element id stride of a polyhedron leaf.
    const int SupportNeighborhoodPolyhedraStride = 256;

    static Query RidgeQuery(FootSupportScene ridge, float x)
    {
        // The probe's lowest point is one radius straight above the ridge line, which both tops reach at an edge.
        using IPhysicsWorldQueryView view = ridge.World.CreateQueryViewExcludingStatics([]);
        using IPhysicsQueryLease lease = ((IPhysicsQueryLeaseSource)view).AcquireQueryReadLease();
        Query query = Query.Run(view, lease, Lowest(x, 0.5 + Math.Sqrt(Radius * Radius - (double)x * x), 0));
        Assert.Equal(CapsuleFeatureStatus.Complete, query.Result.Status);
        return query;
    }

    // Two slabs 2 by 4 by 0.2 whose tops rise 30 degrees to a ridge at height 0.5, the right slab moved out by
    // gap, then yawed 45 degrees about Y. The probe's lowest point is one radius above the middle of the crack.
    static Query YawedRidgeQuery(float gap, out StaticHandle left, out StaticHandle right)
    {
        float angle = 30 * MathF.PI / 180;
        Quaternion yaw = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 4);
        var half = new Vector3(1, 0.1f, 2);
        Pose Slab(float side, float offset)
        {
            Quaternion tilt = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, side * angle);
            Vector3 top = new(side * -MathF.Cos(angle) + offset, 0.5f - MathF.Sin(angle), 0);
            Vector3 centre = top + Vector3.Transform(new Vector3(0, -half.Y, 0), tilt);
            return new Pose(Vector3.Transform(centre, yaw), Quaternion.Concatenate(tilt, yaw));
        }
        using var scene = new Scene();
        left = scene.World.AddStatic(new BoxShape(half), Slab(1, 0));
        right = scene.World.AddStatic(new BoxShape(half), Slab(-1, gap));
        var middle = Vector3.Transform(new Vector3(gap / 2, 0, 0), yaw);
        double rise = Math.Sqrt(Radius * Radius - (double)(gap / 2) * (gap / 2));
        Query query = scene.Query(scene.World, Lowest(middle.X, 0.5 + rise, middle.Z));
        Assert.Equal(CapsuleFeatureStatus.Complete, query.Result.Status);
        return query;
    }

    // Whether the floor top and the slab top join, with the slab moved up its slope by shift. The probe rests on the
    // slab one radius along its normal from the floor's edge line, which is one radius from both tops.
    static bool SlabJoined(float shift)
    {
        float angle = 20 * MathF.PI / 180;
        const float halfLength = 1;
        var down = new Vector3(MathF.Cos(angle), -MathF.Sin(angle), 0);
        using FootSupportScene scene = new FootSupportScene(SceneVariant.Box)
            .Flat("floor", -2, 0, -2, 2, 0)
            .Slab("slab", down * (halfLength - shift), -angle, halfLength, 2);
        using IPhysicsWorldQueryView view = scene.World.CreateQueryViewExcludingStatics([]);
        using IPhysicsQueryLease lease = ((IPhysicsQueryLeaseSource)view).AcquireQueryReadLease();
        double a = angle;
        Query query = Query.Run(view, lease, Lowest(Radius * Math.Sin(a), Radius * Math.Cos(a), 0));
        Assert.Equal(CapsuleFeatureStatus.Complete, query.Result.Status);
        int floor = TopIndex(query, scene["floor"]), slab = TopIndex(query, scene["slab"]);
        return Array.IndexOf(query.Joins, new Join(Math.Min(floor, slab), Math.Max(floor, slab))) >= 0;
    }

    static int TopIndex(Query query, StaticHandle owner)
    {
        int index = Array.FindIndex(query.Elements, e => e.Static == owner && e.ElementId == SupportNeighborhoodOracle.BoxTop);
        Assert.True(index >= 0, $"The top of static {owner.Value} is a member.");
        return index;
    }

    // The probe touches the unit box's top -X -Z corner from outside along the diagonal.
    static Pose Corner()
    {
        double offset = Radius / Math.Sqrt(3);
        return Lowest(-0.5 - offset, 0.5 + offset, -0.5 - offset);
    }

    /// <summary>The upright probe pose whose lowest axis point is (x, y, z), rounded to float.</summary>
    internal static Pose Lowest(double x, double y, double z) =>
        Pose.At(new Vector3((float)x, (float)(y + Half), (float)z));

    internal static (OracleVector Lower, OracleVector Upper) Segment(Pose pose)
    {
        OracleVector center = OracleVector.From(pose.Position);
        return (center - new OracleVector(0, Half, 0), center + new OracleVector(0, Half, 0));
    }

    internal static OracleVector SurfacePoint(Random random, OracleFace face)
    {
        OracleVector[] v = face.Vertices;
        switch (random.Next(3))
        {
            case 0:
                return v[random.Next(v.Length)];
            case 1:
                int edge = random.Next(v.Length);
                return v[edge] + (v[(edge + 1) % v.Length] - v[edge]) * random.NextDouble();
            default:
                OracleVector sum = default;
                double total = 0;
                foreach (OracleVector vertex in v)
                {
                    double weight = random.NextDouble() + 0.01;
                    sum += vertex * weight;
                    total += weight;
                }
                return sum * (1 / total);
        }
    }

    internal static float Signed(Random random) => (float)(random.NextDouble() * 2 - 1);
    internal static float Between(Random random, double low, double high) => (float)(low + random.NextDouble() * (high - low));

    static void AssertRefused(SupportNeighborhoodResult result, CapsuleFeatureStatus status, int elements = 0)
    {
        Assert.Equal(status, result.Status);
        Assert.Equal(0, result.Elements);
        Assert.Equal(0, result.JoinWordsPerRow);
        Assert.Equal(elements, result.RequiredElements);
        Assert.Null(result.QueryWorld);
        Assert.Null(result.SourceWorld);
        Assert.Null(result.Lease);
    }

    static SupportElement[] ElementSentinels(int count)
    {
        var elements = new SupportElement[count];
        for (int i = 0; i < count; i++)
            elements[i] = new(new StaticHandle(900 + i), SupportElementKind.Tangent, 77, new Vector3(7, -11, 13), 0.125f,
                new Vector3(3, 5, 7), 0.25f, -9, 9);
        return elements;
    }

    static ulong[] JoinSentinels(int count)
    {
        var joins = new ulong[count];
        for (int i = 0; i < count; i++) joins[i] = 0xA5A5_0000_0000_0000UL + (ulong)i;
        return joins;
    }

    /// <summary>One join decoded from the matrix, <c>First</c> less than <c>Second</c>.</summary>
    internal readonly record struct Join(int First, int Second);

    internal readonly record struct Query(SupportNeighborhoodResult Result, SupportElement[] Elements, Join[] Joins)
    {
        const int Capacity = SupportNeighborhoodResult.MaximumElements;

        internal static Query Run(IPhysicsWorld world, IPhysicsQueryLease lease, Pose pose,
            QueryFilter filter = default, CapsuleShape? probe = null)
        {
            var elements = new SupportElement[Capacity];
            ulong[] joins = JoinSentinels(Capacity * SupportNeighborhoodResult.JoinWordsFor(Capacity));
            ulong[] original = (ulong[])joins.Clone();
            IPhysicsSupportNeighborhood capability = Assert.IsAssignableFrom<IPhysicsSupportNeighborhood>(world);
            SupportNeighborhoodResult result = capability.QuerySupportNeighborhood(lease, probe ?? Probe, pose, Band, elements,
                joins, filter);
            if (result.Status != CapsuleFeatureStatus.Complete) return new(result, [], []);
            capability.AssertNeighborhoodCurrent(result, lease);
            int count = result.Elements, stride = result.JoinWordsPerRow;
            Assert.Equal((count + 63) / 64, stride);
            var pairs = new List<Join>();
            for (int i = 0; i < count; i++)
            {
                Assert.False(Joined(joins, stride, i, i), "An element is never joined to itself.");
                for (int j = i + 1; j < count; j++)
                {
                    Assert.Equal(Joined(joins, stride, i, j), Joined(joins, stride, j, i));
                    if (Joined(joins, stride, i, j)) pairs.Add(new(i, j));
                }
                // No bit past the element count is set.
                for (int j = count; j < stride * 64; j++) Assert.False(Joined(joins, stride, i, j));
            }
            Assert.Equal(original.AsSpan(count * stride).ToArray(), joins.AsSpan(count * stride).ToArray());
            return new(result, elements[..count], [.. pairs]);
        }

        static bool Joined(ulong[] joins, int stride, int row, int column) =>
            (joins[row * stride + column / 64] >> (column % 64) & 1) != 0;
    }

    internal sealed class Scene : IDisposable
    {
        readonly List<IPhysicsWorldQueryView> _views = [];
        IPhysicsQueryLease? _lease;
        internal BepuPhysicsWorld World { get; } = new(Vector3.Zero);
        internal IPhysicsQueryLease Lease => _lease ??= World.AcquireQueryReadLease();
        internal IPhysicsSupportNeighborhood Capability(IPhysicsWorld world) =>
            Assert.IsAssignableFrom<IPhysicsSupportNeighborhood>(world);
        /// <summary>A view excluding <paramref name="excluded"/>, released after the scene's lease.</summary>
        internal IPhysicsWorldQueryView View(params StaticHandle[] excluded)
        {
            IPhysicsWorldQueryView view = World.CreateQueryViewExcludingStatics(excluded);
            _views.Add(view);
            return view;
        }

        internal Query Query(IPhysicsWorld world, Pose pose, QueryFilter filter = default,
            CapsuleShape? probe = null) => SupportNeighborhoodTests.Query.Run(world, Lease, pose, filter, probe);

        public void Dispose()
        {
            try { _lease?.Dispose(); }
            finally
            {
                try { foreach (IPhysicsWorldQueryView view in _views) view.Dispose(); }
                finally { World.Dispose(); }
            }
        }
    }
}
