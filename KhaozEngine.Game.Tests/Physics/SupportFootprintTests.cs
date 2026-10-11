using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using Xunit;

namespace KhaozEngine.Tests.Physics;

public class SupportFootprintTests
{
    [Fact]
    public void FootprintCapabilityIsSeparateAndSlotsKeepRawIds()
    {
        Type capability = Required("IPhysicsSupportFootprint");
        Assert.True(capability.IsInterface);
        Assert.Empty(capability.GetInterfaces());
        Assert.Equal(["AssertFootprintCurrent", "QuerySupportFootprint"],
            capability.GetMethods().Select(m => m.Name).Order().ToArray());
        Assert.Equal(["AssertNeighborhoodCurrent", "QuerySupportNeighborhood"],
            typeof(IPhysicsSupportNeighborhood).GetMethods().Select(m => m.Name).Order().ToArray());
        Assert.Equal(["AssertFeatureCurrent", "QueryCapsuleFeature"],
            typeof(IPhysicsCapsuleFeatures).GetMethods().Select(m => m.Name).Order().ToArray());
        Type result = Required("SupportFootprintResult"), member = Required("SupportFootprintMember");
        Assert.Equal(256, result.GetField("MaximumElements")!.GetRawConstantValue());
        Assert.Equal(512, result.GetField("MaximumWitnesses")!.GetRawConstantValue());
        Assert.Equal(4, result.GetMethod("JoinWordsFor")!.Invoke(null, [256]));
        MethodInfo query = capability.GetMethod("QuerySupportFootprint")!;
        Assert.Equal(result, query.ReturnType);
        Assert.Equal(new[] { typeof(IPhysicsQueryLease), Required("SupportFootprintRequest").MakeByRefType(),
            typeof(Span<>).MakeGenericType(member), typeof(Span<ulong>), typeof(QueryFilter) },
            query.GetParameters().Select(p => p.ParameterType).ToArray());
        Assert.True(query.GetParameters()[1].IsIn);
        Assert.True(query.GetParameters()[^1].HasDefaultValue);
        MethodInfo current = capability.GetMethod("AssertFootprintCurrent")!;
        Assert.Equal(typeof(void), current.ReturnType);
        Assert.Equal(new[] { result.MakeByRefType(), typeof(IPhysicsQueryLease) },
            current.GetParameters().Select(p => p.ParameterType).ToArray());
        Type witness = Required("SupportFootprintWitness"), role = Required("SupportWitnessRole");
        Assert.Equal(["Axis", "Footprint"], Enum.GetNames(role));
        Assert.Equal(role, witness.GetProperty("Role")!.PropertyType);
        Assert.Equal(typeof(int), member.GetProperty("ElementId")!.PropertyType);
        Assert.Equal(witness, member.GetProperty("First")!.PropertyType);
        Assert.Equal(witness, member.GetProperty("Second")!.PropertyType);
        Assert.Null(witness.GetProperty("ElementId"));
    }

    static Type Required(string name)
    {
        Type? type = typeof(IPhysicsWorld).Assembly.GetType("KhaozEngine.Physics." + name);
        Assert.NotNull(type);
        return type;
    }

    internal static SupportFootprintRequest Request(float x = 0, float z = 0, float radius = 0.2f,
        float cosine = 0.70710677f, double lower = -0.4, double upper = 0.4)
    {
        SupportFootprintProposal Proposal(float r)
        {
            var probe = new CapsuleShape(r, 0.01f);
            float lowest = (float)upper + 0.001f;
            float centre = lowest + r + 0.01f / 2;
            return new(probe, Pose.At(new(x, centre, z)), (float)(upper - lower) + 2 * 0.001f);
        }
        return new(new(x, z), radius, lower, upper, cosine, 0.0001f, Proposal(0.01f), Proposal(radius));
    }

    internal static List<FootprintOracleMember> Members(SupportFootprintScene scene, SupportFootprintRequest request)
    {
        var members = new List<FootprintOracleMember>();
        foreach (OracleFace face in scene.Faces)
            if (SupportFootprintOracle.Polygon(face, request) is { } member) members.Add(member);
        foreach (OracleTangent curve in scene.Curves)
            if (SupportFootprintOracle.Curved(curve, request) is { } member) members.Add(member);
        return members.OrderBy(m => m.Static.Value).ThenBy(m => m.ElementId).ToList();
    }

    [Fact]
    public void ResultFactoriesKeepCountsVerdictsAndTheOriginalLiveInterval()
    {
        using var scene = new SupportFootprintScene();
        using IPhysicsQueryLease lease = scene.World.AcquireQueryReadLease();
        var contact = new SupportProposalVerdict(SupportProposalStatus.Contact, 0.1, 0.100001);
        SupportFootprintResult result = SupportFootprintResult.Completed(scene.World, lease, 256, 512, contact, default);
        Assert.Equal(256, result.Members);
        Assert.Equal(512, result.Witnesses);
        Assert.Equal(4, result.JoinWordsPerRow);
        Assert.Same(scene.World, result.QueryWorld);
        Assert.Same(lease.SourceWorld, result.SourceWorld);
        Assert.Same(lease, result.Lease);
        Assert.Equal(lease.GeometryGeneration, result.GeometryGeneration);
        Assert.Equal(lease.Origin, result.Origin);
        Assert.Equal(contact, result.AxisVerdict);
        Assert.Equal(default, result.FootVerdict);
        SupportFootprintResult refusal = SupportFootprintResult.Refused(CapsuleFeatureStatus.CapacityExceeded, 257);
        Assert.Equal(257, refusal.RequiredElements);
        Assert.Equal(0, refusal.Members);
        Assert.Equal(0, refusal.Witnesses);
        Assert.Null(refusal.Lease);
        Assert.Equal(CapsuleFeatureStatus.Unresolved, default(SupportFootprintResult).Status);
        Assert.Throws<ArgumentOutOfRangeException>(() => SupportFootprintResult.Completed(scene.World, lease, 257, 0, default, default));
        Assert.Throws<ArgumentOutOfRangeException>(() => SupportFootprintResult.Completed(scene.World, lease, 1, 3, default, default));
        Assert.Throws<ArgumentOutOfRangeException>(() => SupportFootprintResult.Refused(CapsuleFeatureStatus.Complete));
        Assert.Throws<ArgumentOutOfRangeException>(() => SupportFootprintResult.Refused(CapsuleFeatureStatus.Unsupported, 1));
        Assert.Throws<ArgumentException>(() => SupportFootprintResult.Completed(scene.World, lease, 0, 0,
            contact with { LowestLower = double.NaN }, default));
        lease.Dispose();
        Assert.ThrowsAny<InvalidOperationException>(() => SupportFootprintResult.Completed(scene.World, lease, 0, 0, default, default));
    }

    [Theory]
    [InlineData(FootprintPartition.Boxes, false)]
    [InlineData(FootprintPartition.Hulls, false)]
    [InlineData(FootprintPartition.Compound, false)]
    [InlineData(FootprintPartition.Mesh, false)]
    [InlineData(FootprintPartition.Boxes, true)]
    [InlineData(FootprintPartition.Hulls, true)]
    [InlineData(FootprintPartition.Compound, true)]
    [InlineData(FootprintPartition.Mesh, true)]
    public void HiddenCrateOracleUsesInstalledGeometryAndIgnoresOcclusion(FootprintPartition partition, bool reverse)
    {
        using SupportFootprintScene scene = SupportFootprintScenes.HiddenCrate(partition, reverse);
        SupportFootprintRequest request = Request();
        List<FootprintOracleMember> members = Members(scene, request);
        FootprintOracleSupport support = SupportFootprintOracle.Select(members, request);
        Assert.Equal(SupportStatus.Walkable, support.Status);
        Assert.Equal(scene.Crate, support.Static);
        Assert.InRange(support.Height, 0.0999999, 0.1000001);
        Assert.Contains(members, m => m.Normal.Y > 0 && m.Normal.Y < request.CosMaxSlope &&
            Math.Abs(m.Plane - (-0.09641016)) < 0.000001);
        Assert.All(members.Where(m => m.HasDiscMaximum), m => Assert.True(m.WitnessInsideClosedDisc));
    }

    [Theory]
    [InlineData(FootprintPartition.Boxes, false)]
    [InlineData(FootprintPartition.Hulls, false)]
    [InlineData(FootprintPartition.Compound, false)]
    [InlineData(FootprintPartition.Mesh, false)]
    [InlineData(FootprintPartition.Boxes, true)]
    [InlineData(FootprintPartition.Compound, true)]
    public void ClusterCountsAdmittedRawPairsRatherThanStatics(FootprintPartition partition, bool curved)
    {
        using SupportFootprintScene scene = SupportFootprintScenes.Cluster(257, curved, partition);
        List<FootprintOracleMember> members = Members(scene, Request());
        Assert.Equal(257, members.Count);
        Assert.Equal(257, members.Select(m => (m.Static, m.ElementId)).Distinct().Count());
        Assert.Equal(SupportStatus.Refused, SupportFootprintOracle.Select(members, Request()).Status);
    }

    [Fact]
    public void FanAndGridKeepEveryInstalledTriangleAndRawId()
    {
        using SupportFootprintScene fan = SupportFootprintScenes.Fan(32);
        Assert.Equal(32, Members(fan, Request()).Count);
        Assert.Equal(Enumerable.Range(0, 32), fan.Faces.Select(f => f.ElementId));
        using SupportFootprintScene grid = SupportFootprintScenes.Grid(2);
        Assert.Equal(8, Members(grid, Request()).Count);
    }

    [Fact]
    public void PolygonUsesTheFullDiscAfterVolumeAdmissionAndRetainsRingContext()
    {
        // All ramp vertices lie above the band top, while its axis plane is valid support in the band.
        OracleFace ramp = Face(1, [new(0.15f, 0.075f, -0.02f), new(0.18f, 0.09f, -0.02f),
            new(0.18f, 0.09f, 0.02f), new(0.15f, 0.075f, 0.02f)]);
        FootprintOracleMember member = SupportFootprintOracle.Polygon(ramp, Request(upper: 0.01))!;
        Assert.True(member.HasDiscMaximum);
        Assert.InRange(member.Maximum, 0.08999999, 0.09000001);
        Assert.InRange(member.Plane, -1e-7, 1e-7);
        OracleFace ring = Face(2, [new(0.20004, 0, -0.001), new(0.20006, 0, -0.001),
            new(0.20006, 0, 0.001), new(0.20004, 0, 0.001)]);
        FootprintOracleMember context = SupportFootprintOracle.Polygon(ring, Request())!;
        Assert.True(context.Admitted);
        Assert.False(context.HasDiscMaximum);
        Assert.Empty(context.Witnesses);
        OracleFace high = Face(3, [new(0.15, 3.1, -0.1), new(0.25, 3.25, -0.1),
            new(0.25, 3.25, 0.1), new(0.15, 3.1, 0.1)]);
        Assert.Null(SupportFootprintOracle.Polygon(high, Request()));
        Assert.Throws<NotSupportedException>(() => SupportFootprintOracle.Volume(Request(cosine: 0)));
    }

    [Fact]
    public void FullPolygonsJoinAtRemoteVerticesAndContextPlanesStillCap()
    {
        OracleFace flat = Face(1, [new(-1, 0, -1), new(3, 0, -1), new(3, 0, 1), new(-1, 0, 1)]);
        OracleFace ramp = Face(2, [new(0.1, 0.29, -1), new(3, 0, -1), new(3, 0, 1), new(0.1, 0.29, 1)]);
        Assert.True(SupportFootprintOracle.Joined(flat, ramp, 0.3));
        Assert.False(SupportFootprintOracle.Joined(flat, ramp, 0.0001f));
        // These two convex patches meet only at (0,0,3), outside the admission column.
        OracleFace left = Face(3, [new(-0.001, -0.0001, -0.1), new(0, 0, 3), new(-0.001, -0.0001, 0.1)]);
        OracleFace right = Face(4, [new(0, 0, 0.20004), new(0.1, -0.01, 0.20004),
            new(0.1, -0.01, 3), new(0, 0, 3)]);
        SupportFootprintRequest request = Request(x: 0.05f);
        FootprintOracleMember l = SupportFootprintOracle.Polygon(left, request)!;
        FootprintOracleMember context = SupportFootprintOracle.Polygon(right, request)!;
        Assert.False(context.HasDiscMaximum);
        Assert.True(SupportFootprintOracle.Joined(left, right, 0.0001f));
        FootprintOracleSupport support = SupportFootprintOracle.Select([l, context], request);
        Assert.InRange(support.Height, -0.00500001, -0.00499999);
    }

    [Fact]
    public void SphereFlankUsesGeometricFootprintWitnessAndSlopeEqualityIsWalkable()
    {
        using var scene = new SupportFootprintScene();
        scene.Install(new SphereShape(0.25f), Pose.At(new(0, -0.25f, 0)));
        SupportFootprintRequest request = Request(x: 0.35f);
        FootprintOracleMember member = Assert.Single(Members(scene, request));
        FootprintOracleWitness witness = Assert.Single(member.Witnesses);
        Assert.Equal(SupportWitnessRole.Footprint, witness.Role);
        Assert.InRange(witness.Position.X, 0.1499999, 0.1500001);
        Assert.InRange(witness.Position.Y, -0.0500001, -0.0499999);
        FootprintOracleSupport support = SupportFootprintOracle.Select([member], request);
        Assert.Equal(SupportStatus.Walkable, support.Status);
        Assert.InRange(support.Height, -0.2000001, -0.1999999);
        (double lower, double upper) = SupportFootprintOracle.PerPointSupremum(scene.Curves[0], request);
        Assert.InRange(-0.2, lower - 1e-7, upper + 1e-7);
        FootprintOracleMember top = Assert.Single(Members(scene, Request(cosine: 1)));
        Assert.Equal([SupportWitnessRole.Axis, SupportWitnessRole.Footprint], top.Witnesses.Select(w => w.Role));
        Assert.Equal(SupportStatus.Walkable, SupportFootprintOracle.Select([top], Request(cosine: 1)).Status);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 45)]
    [InlineData(false, 90)]
    [InlineData(true, 0)]
    [InlineData(true, 45)]
    [InlineData(true, 90)]
    public void CurvedInstalledUprightTiltedAndLyingPosesHaveAttainedRoles(bool cylinder, float degrees)
    {
        using var scene = new SupportFootprintScene();
        Quaternion orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, degrees * MathF.PI / 180);
        scene.Install(cylinder ? new CylinderShape(0.25f, 0.5f) : new CapsuleShape(0.25f, 0.5f),
            new(new(0, -0.25f, 0), orientation));
        SupportFootprintRequest request = Request();
        List<FootprintOracleMember> members = Members(scene, request);
        Assert.NotEmpty(members);
        Assert.Contains(members, m => m.Witnesses.Count > 0);
        foreach (FootprintOracleMember member in members)
        {
            Assert.InRange(member.Witnesses.Count, 0, 2);
            Assert.True(member.WitnessInsideClosedDisc, $"Outside disc: {member}");
            foreach (FootprintOracleWitness witness in member.Witnesses)
            {
                OracleTangent part = scene.Curves.Single(c => c.ElementId == member.ElementId);
                Assert.InRange(part.Closest(witness.Position).Distance, 0, 1e-7);
                Assert.True(witness.Normal.Y > 0);
                (double lower, double upper) = SupportFootprintOracle.PerPointSupremum(part, request);
                double tangent = witness.Position.Y + (witness.Normal.X * (witness.Position.X - request.Axis.X) +
                    witness.Normal.Z * (witness.Position.Z - request.Axis.Y)) / witness.Normal.Y;
                Assert.True(lower <= upper);
                Assert.True(upper - lower <= 1e-7);
                Assert.True(Math.Min(tangent, witness.Position.Y) <= upper + 1e-12);
            }
        }
    }

    [Fact]
    public void RawCapAdmissionDoesNotMoveItsSurfaceWhenReversingTheNormal()
    {
        var cap = new OracleTangent(new StaticHandle(0), 2, OracleSurface.CylinderBottom,
            new(0, 1.25, 0), new(0, 1, 0), 0.25, 0.25);
        Assert.Null(SupportFootprintOracle.Curved(cap, Request()));
    }

    [Fact]
    public void TiltedSideChoosesTheHighestWalkablePointWhenItsMaximumIsSteep()
    {
        var side = new OracleTangent(new StaticHandle(0), 0, OracleSurface.CylinderSide,
            new(0, 0, 0), new OracleVector(Math.Sqrt(3) / 2, 0.5, 0), 0.25, 0.5);
        SupportFootprintRequest request = Request(z: 0.29f, cosine: 0.8f);
        FootprintOracleMember member = SupportFootprintOracle.Curved(side, request)!;
        FootprintOracleWitness witness = Assert.Single(member.Witnesses);
        Assert.Equal(SupportWitnessRole.Footprint, witness.Role);
        Assert.True(witness.Normal.Y >= request.CosMaxSlope);
        Assert.True(witness.Position.Y < member.Maximum);
        Assert.True(member.WitnessInsideClosedDisc);
        Assert.InRange(side.Closest(witness.Position).Distance, 0, 1e-12);
    }

    [Fact]
    public void ClosedDiscTangencyKeepsAxisAndFootprintRolesWithoutInventingSupport()
    {
        var sphere = new OracleTangent(new StaticHandle(0), 0, OracleSurface.Round,
            new(0, 0, 0), new(0, 1, 0), 0.25, 0);
        FootprintOracleMember member = SupportFootprintOracle.Curved(sphere, Request(x: 0.5f, radius: 0.25f))!;
        FootprintOracleWitness footprint = Assert.Single(member.Witnesses);
        Assert.Equal(SupportWitnessRole.Footprint, footprint.Role);
        Assert.Equal(new OracleVector(0.25, 0, 0), footprint.Position);
        Assert.Equal(0, footprint.Normal.Y);
        Assert.Equal(SupportStatus.None, SupportFootprintOracle.Select([member], Request(x: 0.5f, radius: 0.25f)).Status);
        Assert.Equal((double.NegativeInfinity, double.NegativeInfinity),
            SupportFootprintOracle.PerPointSupremum(sphere, Request(x: 0.5f, radius: 0.25f)));
        FootprintOracleMember axis = SupportFootprintOracle.Curved(sphere, Request(x: 0.25f))!;
        Assert.Equal(SupportWitnessRole.Axis, axis.Witnesses[0].Role);
        Assert.Equal(new OracleVector(0.25, 0, 0), axis.Witnesses[0].Position);
    }

    [Fact]
    public void VolumeTangencyAtOneRemoteVertexCannotAdmitAnEmptyDiscIntersection()
    {
        SupportFootprintRequest request = Request();
        double top = SupportFootprintOracle.Volume(request).Top;
        OracleFace remote = Face(0, [new(2, top, 0), new(3, top + 1, 0), new(2, top + 1, 1)]);
        Assert.Null(SupportFootprintOracle.Polygon(remote, request));
    }

    static OracleFace Face(int owner, OracleVector[] vertices)
    {
        OracleFace face = new(new StaticHandle(owner), 0, vertices);
        return face.Normal.Y < 0 ? new(face.Static, 0, vertices.Reverse().ToArray()) : face;
    }
}
