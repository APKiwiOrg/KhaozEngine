using System;
using System.Numerics;
using KhaozEngine.Physics;
using Xunit;

namespace KhaozEngine.Tests.Physics;

public partial class SupportFootprintTests
{
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
        scene.Install(capsule ? new CapsuleShape(0.25f, 1) : new CylinderShape(0.25f, 1),
            new(new(0, -0.25f, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, angle)));
        using var lease = scene.World.AcquireQueryReadLease();
        var members = new SupportFootprintMember[256];
        var request = Request(lower: -2, upper: 2, cosine: cosine);
        var result = Capability(scene.World).QuerySupportFootprint(lease, request, members, new ulong[1024]);
        _output.WriteLine($"capsule={capsule} horizontal={horizontal} {TiltedStatistics(scene.World)}");
        Assert.Equal(CapsuleFeatureStatus.Complete, result.Status);
    }
}
