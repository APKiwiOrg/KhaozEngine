using System;
using System.Numerics;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Physics;

public class SupportFootprintLargeFloorPrerequisiteTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(20f)]
    [InlineData(63.999996185302734375f)]
    [InlineData(64f)]
    [InlineData(64.00000762939453125f)]
    [InlineData(96f)]
    [InlineData(2047.9998779296875f)]
    [InlineData(2048f)]
    public void CompleteFootprintCertifiesTheInteriorOfTheInstalledFloor(float halfWidth)
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        var pose = Pose.At(new(0, -0.125f, 0));
        var floor = world.AddStatic(new BoxShape(new(halfWidth, 0.125f, halfWidth)), pose);
        var query = new FootSupportQuery(new(4, 4), 0, 0.25f, 0.4f, 0.4f, MathF.Cos(MathF.PI / 4));
        var request = FootSupport.DescribeFootprint(query);
        foreach (var proposal in new[] { request.AxisProposal, request.FootProposal })
        {
            Assert.True(world.SweepCapsule(proposal.Probe, proposal.Start, -Vector3.UnitY,
                proposal.Distance, out var hit, QueryFilter.StaticsOnly with { CullBackFaces = true }));
            Assert.Equal(floor, hit.Body);
        }
        using var lease = world.AcquireQueryReadLease();
        var members = new SupportFootprintMember[256];
        var joins = new ulong[1024];
        var result = world.QuerySupportFootprint(lease, request, members, joins, QueryFilter.StaticsOnly);
        var sample = FootSupport.Find(null, world, lease, query);
        var vertex = new Vector3(halfWidth, 0.125f, halfWidth);
        Assert.True(InstalledPoseOperator.TryCreate(new(pose.Position, pose.Orientation), out var transform));
        var vertexStatus = CapsuleFeatureGeometry.TransformVertex(vertex, pose, transform, out _);
        bool pointResolved = RepresentedGeometryTransforms.PosePoint(pose, vertex).IsResolved;
        output.WriteLine($"halfWidth={halfWidth:R} query={result.Status} members={result.Members} " +
            $"support={sample.Status} pointResolved={pointResolved} vertex={vertexStatus}");
        Assert.Equal(CapsuleFeatureStatus.Complete, result.Status);
        Assert.Contains(members[..result.Members], member => member.Static == floor && member.HasDiscMaximum &&
            member.DiscHeightLower <= 0 && member.DiscHeightUpper >= 0);
        Assert.Equal(SupportStatus.Walkable, sample.Status);
        Assert.Equal(0, sample.Height);
    }

    [Fact]
    public void CurvedLeafSizeAdmissionRetainsItsSeparateRangeDomain()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        world.AddStatic(new SphereShape(MathF.BitIncrement(64f)), Pose.Identity);
        var query = new FootSupportQuery(Vector2.Zero, 64, 0.25f, 0.4f, 0.4f, MathF.Cos(MathF.PI / 4));
        using var lease = world.AcquireQueryReadLease();
        var result = world.QuerySupportFootprint(lease, FootSupport.DescribeFootprint(query),
            new SupportFootprintMember[256], new ulong[1024], QueryFilter.StaticsOnly);
        Assert.Equal(CapsuleFeatureStatus.Unsupported, result.Status);
        Assert.Equal(0, result.Members);
    }
}
