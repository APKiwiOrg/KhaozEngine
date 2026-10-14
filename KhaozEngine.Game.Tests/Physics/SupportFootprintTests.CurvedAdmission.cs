using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Physics;
using Xunit;

namespace KhaozEngine.Tests.Physics;

public partial class SupportFootprintTests
{
    [Theory]
    [InlineData("sphere")]
    [InlineData("capsule-upright")]
    [InlineData("capsule-horizontal")]
    public void CurvedRawSurfaceExcludesAColumnWhollyInsideTheSolid(string shape)
    {
        using var scene = new SupportFootprintScene();
        scene.Install(shape == "sphere" ? new SphereShape(1) : new CapsuleShape(1, 1),
            new(Vector3.Zero, shape.EndsWith("horizontal", StringComparison.Ordinal) ?
                new Quaternion(0.5f, 0.5f, 0.5f, 0.5f) : Quaternion.Identity));
        var request = Request(lower: 0.15, upper: 0.15, cosine: 1);
        Assert.Empty(Members(scene, request));
        using var lease = scene.World.AcquireQueryReadLease();
        var result = Capability(scene.World).QuerySupportFootprint(lease, request, new SupportFootprintMember[256], new ulong[1024]);
        Assert.Equal(CapsuleFeatureStatus.Complete, result.Status);
        Assert.Equal(0, result.Members);
        Assert.Equal(SupportProposalStatus.InvalidStart, result.FootVerdict.Status);
    }

    [Fact]
    public void CurvedRingOnlyMemberRetainsNoFootprintWitness()
    {
        using var scene = new SupportFootprintScene();
        scene.Install(new SphereShape(0.25f), Pose.At(new(0, -0.25f, 0)));
        var request = Request(x: 0.45005f);
        var expected = Assert.Single(Members(scene, request));
        Assert.False(expected.HasDiscMaximum);
        using var lease = scene.World.AcquireQueryReadLease();
        var members = new SupportFootprintMember[256];
        var result = Capability(scene.World).QuerySupportFootprint(lease, request, members, new ulong[1024]);
        Assert.Equal(CapsuleFeatureStatus.Complete, result.Status);
        Assert.Equal(1, result.Members);
        Assert.False(members[0].HasDiscMaximum);
        Assert.Equal(0, members[0].WitnessCount);
        Assert.Equal(0, members[0].ProposalMask);
    }

    [Theory]
    [InlineData("sphere")]
    [InlineData("capsule")]
    [InlineData("cylinder")]
    public void CurvedCrestAtSlopeEqualityKeepsAnExactWalkableNormal(string shape)
    {
        using var scene = new SupportFootprintScene();
        scene.Install(shape == "sphere" ? new SphereShape(0.25f) : shape == "capsule" ?
            new CapsuleShape(0.25f, 1) : new CylinderShape(0.25f, 1),
            new(new(0, -0.25f, shape == "cylinder" ? -0.5f : 0), new(0.5f, 0.5f, 0.5f, 0.5f)));
        using var lease = scene.World.AcquireQueryReadLease();
        var members = new SupportFootprintMember[256];
        var result = Capability(scene.World).QuerySupportFootprint(lease, Request(cosine: 1), members, new ulong[1024]);
        Assert.Equal(CapsuleFeatureStatus.Complete, result.Status);
        var side = members.Take(result.Members).Single(m => m.ElementId == 0);
        Assert.Equal(2, side.WitnessCount);
        Assert.Equal(Vector3.UnitY, side.First.Normal);
        Assert.Equal(Vector3.UnitY, side.Second.Normal);
        Assert.Equal(0, side.First.NormalError);
        Assert.Equal(0, side.Second.NormalError);
    }
}
