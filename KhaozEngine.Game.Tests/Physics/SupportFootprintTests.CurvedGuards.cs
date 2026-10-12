using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Physics;

public partial class SupportFootprintTests
{
    [Theory]
    [InlineData("sphere")]
    [InlineData("capsule-upright")]
    [InlineData("capsule-horizontal")]
    [InlineData("cylinder-upright")]
    [InlineData("cylinder-horizontal")]
    [InlineData("cylinder-yaw")]
    public void CurvedProposalTopAtBandEqualityIsSourceCertified(string shape)
    {
        using var diagnostics = new SourceDiagnosticScope();
        using var scene = new SupportFootprintScene();
        bool cylinder = shape.StartsWith("cylinder", StringComparison.Ordinal);
        PhysicsShape geometry = cylinder ? new CylinderShape(0.25f, 1) :
            shape == "sphere" ? new SphereShape(0.25f) : new CapsuleShape(0.25f, 1);
        Pose pose = shape.EndsWith("horizontal", StringComparison.Ordinal) ?
            new(new(0, -0.25f, cylinder ? -0.5f : 0), new(0.5f, 0.5f, 0.5f, 0.5f)) :
            shape.EndsWith("yaw", StringComparison.Ordinal) ? new(new(0.3f, -0.25f, -0.4f), new(0.5f, 0.1f, 0.5f, 0.7f)) :
            Pose.At(new(0, cylinder ? -1 : shape == "sphere" ? -0.25f : -0.75f, 0));
        StaticHandle owner = scene.Install(geometry, pose);
        var centre = SupportFootprintCurvedProofOracle.Installed(scene.World, owner)[0].Centre.Middle;
        var request = Request(x: (float)centre.X, z: (float)centre.Z, upper: 0, cosine: 1);
        using var lease = scene.World.AcquireQueryReadLease();
        var members = new SupportFootprintMember[256]; var joins = new ulong[1024];
        var capability = Capability(scene.World);
        var cold = capability.QuerySupportFootprint(lease, request, members, joins);
        Assert.Equal(CapsuleFeatureStatus.Complete, cold.Status);
        Assert.Equal(SupportProposalStatus.Contact, cold.FootVerdict.Status);
        Assert.Equal(0, cold.FootVerdict.LowestLower);
        Assert.Equal(0, cold.FootVerdict.LowestUpper);
        Assert.Equal(0, cold.AxisVerdict.LowestLower);
        Assert.Equal(0, cold.AxisVerdict.LowestUpper);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var warm = capability.QuerySupportFootprint(lease, request, members, joins);
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(CapsuleFeatureStatus.Complete, warm.Status);
        Assert.Equal(0, bytes);
        if (shape != "cylinder-upright") AssertSourceFallbacks();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TiltedFiniteStrataRefuseAtomicallyUntilTask6b(bool capsule)
    {
        using var scene = new SupportFootprintScene();
        scene.Install(capsule ? new CapsuleShape(0.25f, 1) : new CylinderShape(0.25f, 1),
            new(new(0, -0.25f, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 6)));
        using var lease = scene.World.AcquireQueryReadLease();
        var members = MemberSentinels(256); var before = (SupportFootprintMember[])members.Clone();
        var joins = Enumerable.Repeat(73UL, 1024).ToArray();
        var result = Capability(scene.World).QuerySupportFootprint(lease, Request(), members, joins);
        Assert.Equal(CapsuleFeatureStatus.Unsupported, result.Status);
        Assert.Equal(before, members);
        Assert.All(joins, word => Assert.Equal(73UL, word));
    }
}
