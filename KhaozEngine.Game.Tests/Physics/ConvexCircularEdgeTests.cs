using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Physics;

public sealed class ConvexCircularEdgeTests
{
    static readonly ConvexQueryShape Shape = new(ConvexQueryKind.ConeCylinder, 0.25f, 0.25f, 0.5f, 0.75f);

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void ObliqueCircularEdgeHasAnAttainedKktDistance(bool hull, bool shoulder, bool margin)
    {
        var pose = new Pose(new(0.5f, shoulder ? 0.375f : 1, 0.2f),
            new Quaternion(0, 0, shoulder ? 0.5f : -0.5f, 0.8660254f));
        using var fixture = new ConvexPlanarFixture(new(0.25f, 0.00390625f, 0.00390625f), pose, hull);
        var shape = margin ? Shape.WithMargin(0.001f) : Shape;
        var request = new ConvexQueryRequest(shape, Pose.Identity, Vector3.Zero, ConvexQueryOperation.Distance);
        var work = new ConvexQueryWork();
        Assert.True(ConvexFeatureKernel.TryEvaluate(request, fixture.Leaf, ref work, new(), out var evidence));
        Assert.True(evidence.IsComplete && evidence.HasAttainedWitness && evidence.HasNormalConeCertificate);
        Assert.Equal(shoulder ? ConvexShellFeature.Shoulder : ConvexShellFeature.Rim, evidence.QueryFeature);
        Assert.Equal(ConvexTargetFeature.Edge, evidence.TargetFeature);
        Assert.True(evidence.LowerDistance > 0);
        Assert.InRange(evidence.UpperDistance - evidence.LowerDistance, 0, 0.001f);
        var oracle = ShortBodyShellOracle.Entry(shape, Pose.Identity, fixture.Oracle, Vector3.Zero);
        Assert.True(oracle.IsComplete);
        Assert.True(evidence.LowerDistance <= oracle.Lower && evidence.UpperDistance >= oracle.Upper);
    }
}
