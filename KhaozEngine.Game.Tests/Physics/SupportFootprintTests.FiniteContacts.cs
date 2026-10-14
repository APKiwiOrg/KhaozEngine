using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Physics;
using Xunit;

namespace KhaozEngine.Tests.Physics;

public partial class SupportFootprintTests
{
    [Theory]
    [InlineData(0.15f, false)]
    [InlineData(0.3f, false)]
    [InlineData(0.075f, false)]
    [InlineData(0.3f, true)]
    public void FiniteTopEdgeCertifiesProposalContact(float drop, bool lowerFloor)
    {
        using var scene = new SupportFootprintScene();
        StaticHandle edge = scene.Install(new TriangleMeshShape(
            [new(0.15f, 0, -0.5f), new(0.3f, -drop, -0.5f),
                new(0.15f, 0, 0.5f), new(0.3f, -drop, 0.5f)], [0, 1, 2, 1, 3, 2]), Pose.Identity);
        if (lowerFloor)
        {
            scene.Install(new TriangleMeshShape([new(0.1f, -0.1f, -0.5f), new(0.15f, 0, -0.5f),
                new(0.1f, -0.1f, 0.5f), new(0.15f, 0, 0.5f)], [0, 1, 2, 1, 3, 2]), Pose.Identity);
            scene.Install(new TriangleMeshShape([new(-1, -0.2f, -1), new(1, -0.2f, -1),
                new(-1, -0.2f, 1), new(1, -0.2f, 1)], [0, 1, 2, 1, 3, 2]), Pose.Identity);
        }
        var request = Request(upper: 0.125);
        using var lease = scene.World.AcquireQueryReadLease();
        var members = new SupportFootprintMember[256];
        var joins = new ulong[1024];
        var result = Capability(scene.World).QuerySupportFootprint(lease, request, members, joins);

        CheckQueryOracle(scene, request, result, members, joins);
        Assert.Equal(lowerFloor ? SupportProposalStatus.Contact : SupportProposalStatus.Miss, result.AxisVerdict.Status);
        Assert.Equal(SupportProposalStatus.Contact, result.FootVerdict.Status);
        // The lower sphere touches the finite edge at x 0.15, y 0, independently of the extended plane.
        double radius = request.Radius, x = (double)0.15f;
        double lowest = Math.Sqrt(radius * radius - x * x) - radius;
        Assert.True(result.FootVerdict.LowestLower <= lowest);
        Assert.True(result.FootVerdict.LowestUpper >= lowest);
        Assert.True(result.FootVerdict.LowestLower > request.BandLower);
        Assert.True(result.FootVerdict.LowestUpper < request.BandUpper);
        Assert.Contains(members.Take(result.Members), m => m.Static == edge && (m.ProposalMask & 2) != 0 &&
            m.HasDiscMaximum && m.DiscHeightLower <= 0 && m.DiscHeightUpper >= 0);
    }
}
