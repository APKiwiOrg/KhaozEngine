using System;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Physics;

public sealed class ConvexRoundedVertexSweepTests(ITestOutputHelper output)
{
    static readonly ConvexQueryShape Shape = new(ConvexQueryKind.ConeCylinder, 0.25f, 0.25f, 0.5f, 0.75f, 0.001f);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RoundedCircularVertexUsesTheWholeOriginalPath(bool hull, bool rim)
    {
        float half = 0.00390625f;
        float height = rim ? Shape.UpperY + 0.0005f : Shape.RingY - 0.0005f;
        using var fixture = new ConvexPlanarFixture(new(half),
            Pose.At(new(0.1875f + half, height + (rim ? half : -half), 0.1875f + half)), hull);
        Vector3 delta = new(0.125f, 0, 0.125f);
        var request = new ConvexQueryRequest(Shape, Pose.Identity, delta, ConvexQueryOperation.Sweep);
        var work = new ConvexQueryWork();
        var scratch = new ConvexQueryScratch();
        Assert.True(ConvexFeatureKernel.TryEvaluate(request, fixture.Leaf, ref work, scratch, out var evidence),
            $"cells={work.Cells} features={work.Features} exhausted={work.Exhausted}");
        Assert.Equal(ConvexSweepStatus.Hit, evidence.SweepStatus);
        Assert.Equal(rim ? ConvexShellFeature.Rim : ConvexShellFeature.Shoulder, evidence.QueryFeature);
        Assert.Equal(ConvexTargetFeature.Vertex, evidence.TargetFeature);
        Assert.True(evidence.HasAttainedWitness && evidence.HasNormalConeCertificate);
        Assert.InRange(evidence.CertifiedErrorMetres, 0, 0.001f);
        Assert.True(evidence.UpperDistance - 0.001f < evidence.LowerDistance);
        var oracle = ShortBodyShellOracle.Entry(Shape, Pose.Identity, fixture.Oracle, delta);
        Assert.True(oracle.IsComplete);
        Assert.Equal(ConvexSweepStatus.Hit, oracle.Status);
        Assert.True(evidence.LowerDistance <= oracle.Lower && evidence.UpperDistance >= oracle.Upper,
            $"[{evidence.LowerDistance:R},{evidence.UpperDistance:R}] oracle [{oracle.Lower:R},{oracle.Upper:R}]");
        Assert.Equal(0, scratch.Polynomials.Used);
        output.WriteLine($"{evidence.QueryFeature}/{evidence.TargetFeature}: cells={work.Cells}, features={work.Features}");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RoundedVertexUsesAnArbitraryRepresentedTargetPose(bool hull, bool rim)
    {
        float half = 0.00390625f;
        float height = rim ? Shape.UpperY + 0.0005f : Shape.RingY - 0.0005f;
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.3f);
        using var fixture = new ConvexPlanarFixture(new(half),
            new(new(0.1875f + half, height + (rim ? half : -half), 0.1875f + half), rotation), hull);
        Compare(fixture, Shape, new(0.125f, rim ? 0.0009765625f : -0.0009765625f, 0.09375f),
            rim ? ConvexShellFeature.Rim : ConvexShellFeature.Shoulder);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RoundedVertexRetainsTheFullRepresentedThreeDimensionalPose(bool hull, bool rim)
    {
        float height = rim ? Shape.UpperY + 0.0005f : Shape.RingY - 0.0005f;
        var rotation = Quaternion.CreateFromYawPitchRoll(0.3f, 0.2f, 0.1f);
        using var fixture = new ConvexPlanarFixture(new(0.0000152587890625f),
            new(new(0.1875f, height, 0.1875f), rotation), hull);
        Compare(fixture, Shape, new(0.125f, rim ? 0.0009765625f : -0.0009765625f, 0.09375f),
            rim ? ConvexShellFeature.Rim : ConvexShellFeature.Shoulder);
    }

    [Fact]
    public void RoundedShoulderKeepsItsClosedConeSeam()
    {
        var shape = new ConvexQueryShape(Shape.Kind, 0.1875f, 0.25f, 0.5f, 0.75f, Shape.Margin);
        using var fixture = new ConvexPlanarFixture(new(0.00390625f), Pose.At(new(0.31640625f, 0.40234375f, 0.00390625f)));
        var evidence = Compare(fixture, shape, new(0.5f, -0.375f, 0), ConvexShellFeature.Shoulder);
        double expected = 0.15625 - (double)Shape.Margin;
        Assert.True(evidence.LowerDistance <= expected && evidence.UpperDistance >= expected);
    }

    [Fact]
    public void ZeroPathRetainsANonFaceRoundedBoundaryContact()
    {
        float radius = 0.000244140625f, half = 0.00000762939453125f;
        var shape = new ConvexQueryShape(Shape.Kind, radius, 0, radius, 2 * radius, Shape.Margin);
        float x = (float)(radius + (double)Shape.Margin * 4 / 5);
        float y = (float)(radius - (double)Shape.Margin * 3 / 5);
        using var fixture = new ConvexPlanarFixture(new(half), Pose.At(new(x + half, y - half, half)));
        var evidence = Compare(fixture, shape, Vector3.Zero, ConvexShellFeature.Shoulder);
        Assert.Equal(0, evidence.LowerDistance);
        Assert.Equal(0, evidence.UpperDistance);
    }

    [Fact]
    public void RoundedVertexWorkDenialIsStickyAndRestoresTheSharedScratch()
    {
        using var fixture = new ConvexPlanarFixture(new(0.00390625f), Pose.At(new(0.19140625f, 0.7544063f, 0.19140625f)));
        var request = new ConvexQueryRequest(Shape, Pose.Identity, new(0.125f, 0, 0.125f), ConvexQueryOperation.Sweep);
        var scratch = new ConvexQueryScratch();
        double width = scratch.MaximumFractionWidth;
        var probe = new ConvexQueryWork();
        Assert.True(ConvexFeatureKernel.TryEvaluate(request, fixture.Leaf, ref probe, scratch, out var complete));
        var work = new ConvexQueryWork(maximumCells: probe.Cells - 1);
        Assert.False(ConvexFeatureKernel.TryEvaluate(request, fixture.Leaf, ref work, scratch, out var refused));
        Assert.True(work.Exhausted);
        Assert.Equal(default, refused);
        Assert.Equal(0, scratch.Polynomials.Used);
        Assert.Equal(width, scratch.MaximumFractionWidth);
        Assert.False(ConvexFeatureKernel.TryEvaluate(request, fixture.Leaf, ref work, scratch, out refused));
        Assert.Equal(default, refused);
        work = new();
        Assert.True(ConvexFeatureKernel.TryEvaluate(request, fixture.Leaf, ref work, scratch, out var reused));
        Assert.Equal(complete, reused);
    }

    [Fact]
    public void SquaredInnerRimRootCannotBecomeAnEntryFromInitialOverlap()
    {
        var shape = new ConvexQueryShape(Shape.Kind, 0.000244140625f, 0.25f, 0.5f, 0.75f, Shape.Margin);
        using var fixture = new ConvexPlanarFixture(new(0.00000762939453125f), Pose.At(new(0.0010076294f, 0.49999237f, 0.00000762939453125f)));
        var request = new ConvexQueryRequest(shape, Pose.Identity, new(0.0005f, 0, 0), ConvexQueryOperation.Sweep);
        var a = ConvexExactPoint.From(new(0.001f, 0.5f, 0));
        var work = new ConvexQueryWork();
        Assert.False(ConvexCircularVertexSweep.TryEvaluate(request, fixture.Leaf, a, 0, ref work, new(), out var refused));
        Assert.Equal(default, refused);
        Assert.False(work.Exhausted);
        var oracle = ShortBodyShellOracle.Entry(shape, Pose.Identity, fixture.Oracle, request.Displacement);
        Assert.True(oracle.IsComplete);
        Assert.Equal(ConvexSweepStatus.Hit, oracle.Status);
        Assert.Equal(0, oracle.Lower);
        Assert.Equal(0, oracle.Upper);
    }

    ConvexLeafEvidence Compare(ConvexPlanarFixture fixture, ConvexQueryShape shape, Vector3 delta, ConvexShellFeature feature)
    {
        var request = new ConvexQueryRequest(shape, Pose.Identity, delta, ConvexQueryOperation.Sweep);
        var work = new ConvexQueryWork();
        var scratch = new ConvexQueryScratch();
        Assert.True(ConvexFeatureKernel.TryEvaluate(request, fixture.Leaf, ref work, scratch, out var evidence),
            $"{feature}: cells={work.Cells} features={work.Features} exhausted={work.Exhausted}");
        Assert.Equal(ConvexSweepStatus.Hit, evidence.SweepStatus);
        Assert.Equal(feature, evidence.QueryFeature);
        Assert.Equal(ConvexTargetFeature.Vertex, evidence.TargetFeature);
        Assert.True(evidence.HasAttainedWitness && evidence.HasNormalConeCertificate);
        var oracle = ShortBodyShellOracle.Entry(shape, Pose.Identity, fixture.Oracle, delta);
        Assert.True(oracle.IsComplete, "independent oracle");
        if (delta != Vector3.Zero) Assert.Equal(ConvexSweepStatus.Hit, oracle.Status);
        Assert.True(evidence.LowerDistance <= oracle.Lower && evidence.UpperDistance >= oracle.Upper,
            $"[{evidence.LowerDistance:R},{evidence.UpperDistance:R}] oracle [{oracle.Lower:R},{oracle.Upper:R}]");
        Assert.InRange(evidence.CertifiedErrorMetres, 0, 0.001f);
        Assert.Equal(0, scratch.Polynomials.Used);
        output.WriteLine($"{feature}/{evidence.TargetFeature}: cells={work.Cells}, features={work.Features}");
        return evidence;
    }
}
