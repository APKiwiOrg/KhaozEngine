using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Physics;
using Xunit;

namespace KhaozEngine.Tests.Physics;

public partial class SupportFootprintTests
{
    [Theory]
    [InlineData(false, 0f, 0.70710677f)]
    [InlineData(true, 0f, 0.70710677f)]
    [InlineData(false, 0.1f, 0.70710677f)]
    [InlineData(true, 0.1f, 0.70710677f)]
    [InlineData(false, 0f, 1f)]
    [InlineData(true, 0f, 1f)]
    [InlineData(false, 0.1f, 1f)]
    [InlineData(true, 0.1f, 1f)]
    public void CurvedTiltedSourceTouchingHorizontalStratumPublishesEnclosedRoles(bool capsule, float z, float cosine)
    {
        using var scene = new SupportFootprintScene();
        var orientation = new Quaternion(0.6f, 0, 0.3741657f, 0.70710677f);
        Vector3 centre = new(0, -0.25f, 0);
        BepuPhysics.Collidables.Compound.GetWorldPose(new(new Vector3(0, 0.5f, 0), Quaternion.Identity),
            new(Vector3.Zero, orientation), out BepuPhysics.RigidPose lift);
        Vector3 position = capsule ? centre : centre - lift.Position;
        StaticHandle owner = scene.Install(capsule ? new CapsuleShape(0.25f, 1) : new CylinderShape(0.25f, 1),
            new(position, orientation));
        var request = Request(z: z, lower: -2, upper: 2, cosine: cosine);
        var source = SupportFootprintCurvedProofOracle.Installed(scene.World, owner)[0];
        Assert.Equal(0, source.Centre.X.Middle);
        Assert.Equal(-0.25, source.Centre.Y.Middle);
        Assert.Equal(0, source.Centre.Z.Middle);
        var expected = SupportFootprintOracle.Curved(source.Tangent, request)!;
        using var lease = scene.World.AcquireQueryReadLease();
        var members = new SupportFootprintMember[256];
        var result = Capability(scene.World).QuerySupportFootprint(lease, request, members, new ulong[1024]);
        _output.WriteLine(TiltedStatistics(scene.World));
        Assert.Equal(CapsuleFeatureStatus.Complete, result.Status);
        var member = Assert.Single(members.Take(result.Members));
        Assert.Equal(owner, member.Static);
        Assert.Equal(2, member.WitnessCount);
        CheckTiltedMember(member, expected, source, request);
    }

    [Theory]
    [InlineData(false, false, 0.70710677f)]
    [InlineData(false, true, 0.70710677f)]
    [InlineData(true, false, 0.70710677f)]
    [InlineData(true, true, 0.70710677f)]
    [InlineData(false, false, 1f)]
    [InlineData(false, true, 1f)]
    [InlineData(true, false, 1f)]
    [InlineData(true, true, 1f)]
    public void CurvedTiltedNearDegenerateInstalledPosesKeepCoverage(bool capsule, bool horizontal, float cosine)
    {
        using var scene = new SupportFootprintScene();
        float angle = horizontal ? MathF.BitDecrement(MathF.PI / 2) : 0.000001f;
        StaticHandle owner = scene.Install(capsule ? new CapsuleShape(0.25f, 1) : new CylinderShape(0.25f, 1),
            new(new(0, -0.25f, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, angle)));
        using var lease = scene.World.AcquireQueryReadLease();
        var members = new SupportFootprintMember[256];
        var request = Request(lower: -2, upper: 2, cosine: cosine);
        var parts = SupportFootprintCurvedProofOracle.Installed(scene.World, owner);
        var expected = parts.Select(p => SupportFootprintOracle.Curved(p.Tangent, request))
            .OfType<FootprintOracleMember>().ToArray();
        Assert.NotEmpty(expected);
        var result = Capability(scene.World).QuerySupportFootprint(lease, request, members, new ulong[1024]);
        _output.WriteLine($"capsule={capsule} horizontal={horizontal} {TiltedStatistics(scene.World)}");
        Assert.Equal(CapsuleFeatureStatus.Complete, result.Status);
        var actual = members.Take(result.Members).ToArray();
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < actual.Length; i++)
            CheckTiltedMember(actual[i], expected[i], parts.Single(p => p.Tangent.ElementId == actual[i].ElementId), request);
    }
}
