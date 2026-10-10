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
        Assert.Equal([new SupportJoin(0, 1), new SupportJoin(0, 2), new SupportJoin(1, 2)], query.Joins);
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
        Assert.Contains(new SupportJoin(Math.Min(left, right), Math.Max(left, right)), joined.Joins);

        // The same ridge with the right slab 1e-3 further out: both tops still reach the probe, and do not join.
        float angle = 30 * MathF.PI / 180;
        Vector3 half = new(MathF.Cos(angle), MathF.Sin(angle), 0);
        using FootSupportScene gap = new FootSupportScene(SceneVariant.Box)
            .Slab("left", new Vector3(-half.X, 0.5f - half.Y, 0), angle, 1, 2)
            .Slab("right", new Vector3(half.X + 1e-3f, 0.5f - half.Y, 0), -angle, 1, 2);
        Query apart = RidgeQuery(gap, 0.0005f);
        left = TopIndex(apart, gap["left"]);
        right = TopIndex(apart, gap["right"]);
        Assert.DoesNotContain(new SupportJoin(Math.Min(left, right), Math.Max(left, right)), apart.Joins);
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
        Assert.DoesNotContain(new SupportJoin(Math.Min(floor, ramp), Math.Max(floor, ramp)), query.Joins);
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
        // Leaf 1 is curved. It is skipped and never refuses the box leaves beside it.
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
        SupportElement[] elements = ElementSentinels(1), originalElements = (SupportElement[])elements.Clone();
        SupportJoin[] joins = JoinSentinels(8), originalJoins = (SupportJoin[])joins.Clone();
        SupportNeighborhoodResult narrow = scene.Capability(scene.World).QuerySupportNeighborhood(scene.Lease, Probe,
            Corner(), Band, elements, joins);
        AssertRefused(narrow, CapsuleFeatureStatus.CapacityExceeded, 3, 3);
        Assert.Equal(originalElements, elements);
        Assert.Equal(originalJoins, joins);

        elements = ElementSentinels(8);
        originalElements = (SupportElement[])elements.Clone();
        joins = JoinSentinels(2);
        originalJoins = (SupportJoin[])joins.Clone();
        SupportNeighborhoodResult fewJoins = scene.Capability(scene.World).QuerySupportNeighborhood(scene.Lease, Probe,
            Corner(), Band, elements, joins);
        AssertRefused(fewJoins, CapsuleFeatureStatus.CapacityExceeded, 3, 3);
        Assert.Equal(originalElements, elements);
        Assert.Equal(originalJoins, joins);
    }

    [Fact]
    public void ResultIsBoundToItsLeaseAndReceiver()
    {
        using var scene = new Scene();
        StaticHandle box = scene.World.AddStatic(new BoxShape(new Vector3(0.5f)), Pose.Identity);
        using IPhysicsWorldQueryView view = scene.World.CreateQueryViewExcludingStatics([]);
        using IPhysicsWorldQueryView excluded = scene.World.CreateQueryViewExcludingStatics([box]);
        IPhysicsCapsuleFeatures owner = scene.Capability(scene.World), selected = scene.Capability(view);
        IPhysicsCapsuleFeatures other = scene.Capability(excluded);
        Pose pose = Lowest(0, 0.5 + Radius, 0);

        SupportNeighborhoodResult own = scene.Query(scene.World, pose).Result;
        owner.AssertFeatureCurrent(own, scene.Lease);
        SupportNeighborhoodResult viewed = scene.Query(view, pose).Result;
        selected.AssertFeatureCurrent(viewed, scene.Lease);
        Assert.Same(scene.World, viewed.SourceWorld);
        Assert.Same(view, viewed.QueryWorld);
        Assert.Same(scene.Lease, viewed.Lease);

        // Matching source metadata does not authorize another receiver.
        Assert.Throws<InvalidOperationException>(() => owner.AssertFeatureCurrent(viewed, scene.Lease));
        Assert.Throws<InvalidOperationException>(() => other.AssertFeatureCurrent(viewed, scene.Lease));
        Assert.Throws<InvalidOperationException>(() => selected.AssertFeatureCurrent(default(SupportNeighborhoodResult),
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
        Assert.Throws<InvalidOperationException>(() => selected.AssertFeatureCurrent(viewed, next));
        Assert.ThrowsAny<InvalidOperationException>(() => selected.AssertFeatureCurrent(viewed, scene.Lease));
        Assert.ThrowsAny<InvalidOperationException>(() => selected.QuerySupportNeighborhood(scene.Lease, Probe, pose,
            Band, elements, JoinSentinels(4)));
        Assert.Equal(original, elements);
    }

    [Fact]
    public void PoseOutsideTheDomainAndLayerFiltersAreUnsupported()
    {
        using var scene = new Scene();
        scene.World.AddStatic(new BoxShape(new Vector3(0.5f)), Pose.Identity);
        IPhysicsCapsuleFeatures capability = scene.Capability(scene.World);
        Pose upright = Lowest(0, 0.5 + Radius, 0);
        var tilted = new Pose(upright.Position, new Quaternion(0.5f, 0.5f, 0.5f, 0.5f));
        SupportElement[] elements = ElementSentinels(4), original = (SupportElement[])elements.Clone();
        AssertRefused(capability.QuerySupportNeighborhood(scene.Lease, Probe, tilted, Band, elements, JoinSentinels(4)),
            CapsuleFeatureStatus.Unsupported);
        AssertRefused(capability.QuerySupportNeighborhood(scene.Lease, Probe, upright, Band, elements, JoinSentinels(4),
            new QueryFilter(QueryMobility.Statics, 1)), CapsuleFeatureStatus.Unsupported);
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
                // Outward from the chosen face, tilted at random, at a separation inside the band.
                OracleVector tilt = new OracleVector(Signed(random), Signed(random), Signed(random)) * 0.7;
                OracleVector outward = (face.Normal + tilt).Unit;
                if (OracleVector.Dot(outward, face.Normal) < 0.2) outward = face.Normal;
                OracleVector lowest = target + outward * (Radius + Between(random, -5e-5, 5e-5));
                Pose probe = Lowest(lowest.X, lowest.Y, lowest.Z);
                Query query = scene.Query(scene.World, probe);
                Assert.Equal(CapsuleFeatureStatus.Complete, query.Result.Status);
                (OracleVector lower, OracleVector upper) = Segment(probe);
                SupportNeighborhoodOracle.AssertMatches(query.Elements, faces, lower, upper, Radius, Band);
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
    static Pose Lowest(double x, double y, double z) => Pose.At(new Vector3((float)x, (float)(y + Half), (float)z));

    static (OracleVector Lower, OracleVector Upper) Segment(Pose pose)
    {
        OracleVector center = OracleVector.From(pose.Position);
        return (center - new OracleVector(0, Half, 0), center + new OracleVector(0, Half, 0));
    }

    static OracleVector SurfacePoint(Random random, OracleFace face)
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

    static float Signed(Random random) => (float)(random.NextDouble() * 2 - 1);
    static float Between(Random random, double low, double high) => (float)(low + random.NextDouble() * (high - low));

    static void AssertRefused(SupportNeighborhoodResult result, CapsuleFeatureStatus status, int elements = 0,
        int joins = 0)
    {
        Assert.Equal(status, result.Status);
        Assert.Equal(0, result.Elements);
        Assert.Equal(0, result.Joins);
        Assert.Equal(elements, result.RequiredElements);
        Assert.Equal(joins, result.RequiredJoins);
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

    static SupportJoin[] JoinSentinels(int count)
    {
        var joins = new SupportJoin[count];
        for (int i = 0; i < count; i++) joins[i] = new(900 + i, 990 + i);
        return joins;
    }

    readonly record struct Query(SupportNeighborhoodResult Result, SupportElement[] Elements, SupportJoin[] Joins)
    {
        internal static Query Run(IPhysicsWorld world, IPhysicsQueryLease lease, Pose pose,
            QueryFilter filter = default)
        {
            var elements = new SupportElement[SupportNeighborhoodResult.MaximumElements];
            var joins = new SupportJoin[SupportNeighborhoodResult.MaximumJoins];
            IPhysicsCapsuleFeatures capability = Assert.IsAssignableFrom<IPhysicsCapsuleFeatures>(world);
            SupportNeighborhoodResult result = capability.QuerySupportNeighborhood(lease, Probe, pose, Band, elements,
                joins, filter);
            if (result.Status == CapsuleFeatureStatus.Complete) capability.AssertFeatureCurrent(result, lease);
            return new(result, elements[..result.Elements], joins[..result.Joins]);
        }
    }

    sealed class Scene : IDisposable
    {
        readonly List<IPhysicsWorldQueryView> _views = [];
        IPhysicsQueryLease? _lease;
        internal BepuPhysicsWorld World { get; } = new(Vector3.Zero);
        internal IPhysicsQueryLease Lease => _lease ??= World.AcquireQueryReadLease();
        internal IPhysicsCapsuleFeatures Capability(IPhysicsWorld world) =>
            Assert.IsAssignableFrom<IPhysicsCapsuleFeatures>(world);
        /// <summary>A view excluding <paramref name="excluded"/>, released after the scene's lease.</summary>
        internal IPhysicsWorldQueryView View(params StaticHandle[] excluded)
        {
            IPhysicsWorldQueryView view = World.CreateQueryViewExcludingStatics(excluded);
            _views.Add(view);
            return view;
        }

        internal Query Query(IPhysicsWorld world, Pose pose, QueryFilter filter = default) =>
            SupportNeighborhoodTests.Query.Run(world, Lease, pose, filter);

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
