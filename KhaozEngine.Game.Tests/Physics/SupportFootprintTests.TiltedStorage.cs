using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Tests.Locomotion.Contacts;
using Xunit;

namespace KhaozEngine.Tests.Physics;

public partial class SupportFootprintTests
{
    [Theory]
    [InlineData("cylinder")]
    [InlineData("capsule")]
    [InlineData("equality")]
    public void CurvedTiltedWarmWitnessesReuseOwnedScratch(string shape)
    {
        using var scene = new SupportFootprintScene();
        bool equality = shape == "equality";
        StaticHandle owner = scene.Install(shape == "cylinder" ? new CylinderShape(0.3f, 2) :
            equality ? new CapsuleShape(0.25f, 1) : new CapsuleShape(0.3f, 2),
            new(new(0, equality ? 0 : -0.25f, 0), equality ? new Quaternion(0, 0, 0.5f, MathF.Sqrt(3) / 2) :
                Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 6)));
        var request = equality ? Request(z: 0.29f, cosine: 0.8f) : Request(z: 0.1f, lower: -2, upper: 2);
        using var lease = scene.World.AcquireQueryReadLease();
        var members = new SupportFootprintMember[256]; var joins = new ulong[1024];
        var capability = Capability(scene.World);
        using var region = SupportAllocationRegion.Start();
        long coldBefore = GC.GetAllocatedBytesForCurrentThread();
        var cold = capability.QuerySupportFootprint(lease, request, members, joins);
        long coldBytes = GC.GetAllocatedBytesForCurrentThread() - coldBefore;
        var coldMembers = (SupportFootprintMember[])members.Clone();
        Assert.Equal(CapsuleFeatureStatus.Complete, cold.Status);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var warm = capability.QuerySupportFootprint(lease, request, members, joins);
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        region.Dispose();
        foreach (var source in SupportFootprintCurvedProofOracle.Installed(scene.World, owner))
        {
            var expected = SupportFootprintOracle.Curved(source.Tangent, request);
            if (expected is not null)
                CheckTiltedMember(Assert.Single(coldMembers.Take(cold.Members), m => m.ElementId == expected.ElementId), expected, source, request);
        }
        Assert.Equal(CapsuleFeatureStatus.Complete, warm.Status);
        Assert.Equal(0, bytes);
        _output.WriteLine($"{shape} cold-query-bytes={coldBytes} warm-bytes={bytes} {TiltedStatistics(scene.World)}");
    }
}
