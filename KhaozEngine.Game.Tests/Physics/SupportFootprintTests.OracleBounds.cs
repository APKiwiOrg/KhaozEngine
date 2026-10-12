using System;
using KhaozEngine.Physics;
using Xunit;
using R = KhaozEngine.Tests.Physics.InstalledPoseOracle.R;
using V = KhaozEngine.Tests.Physics.InstalledPoseOracle.V;

namespace KhaozEngine.Tests.Physics;

public partial class SupportFootprintTests
{
    [Fact]
    public void RemoteEdgeReferenceIsCorrectlyRounded()
    {
        OracleFace face = Face(0, [new(-0.001f, -0.0001f, -0.1f), new(0, 0, 3), new(-0.001f, -0.0001f, 0.1f)]);
        var member = SupportFootprintOracle.Polygon(face, Request(x: 0.05f))!;
        // Independently evaluated exact binary32 operands, rounded from 70 decimal digits.
        const double rounded = -9.053505736350574e-05;
        Assert.InRange(member.Maximum, Math.BitDecrement(rounded), Math.BitIncrement(rounded));
    }

    [Fact]
    public void ExactReferencesCertifyPlaneRootsAndContactHeight()
    {
        OracleFace face = Face(0, [new(-1, -1, -1), new(1, 1, -1), new(1, 1, 1), new(-1, -1, 1)]);
        var plane = SupportFootprintExactOracle.Plane(face, 0.125, 0);
        Assert.Equal(new R(1, 8), plane.Lower);
        Assert.Equal(plane.Lower, plane.Upper);
        var roots = SupportFootprintExactOracle.EdgeCircleRoots(new V(-R.One, R.Zero, new R(1, 8)),
            new V(new R(2, 1), R.Zero, R.Zero), V.Zero, new R(1, 4));
        Assert.Equal(2, roots.Count);
        var left = (SupportFootprintExactOracle.Bounds)(-R.One) +
            (SupportFootprintExactOracle.Bounds)new R(2, 1) * roots[0];
        var right = (SupportFootprintExactOracle.Bounds)(-R.One) +
            (SupportFootprintExactOracle.Bounds)new R(2, 1) * roots[1];
        Assert.True(left.Lower < R.Zero && right.Lower > R.Zero);
        Assert.True(left.Upper.Square() <= new R(3, 64) && left.Lower.Square() >= new R(3, 64));
        Assert.True(right.Lower.Square() <= new R(3, 64) && right.Upper.Square() >= new R(3, 64));
        var contact = SupportFootprintExactOracle.Contact(face, Request(radius: 0.25f).FootProposal)!.Value;
        // On y=x the probe's lowest contact is (sqrt(2)-1)/4.
        Assert.True((new R(4, 1) * contact.Lower + R.One).Square() <= new R(2, 1));
        Assert.True((new R(4, 1) * contact.Upper + R.One).Square() >= new R(2, 1));
    }
}
