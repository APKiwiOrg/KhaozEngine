using System;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Physics;

public sealed class ConvexGeneralPlanarTests
{
    static readonly ConvexQueryShape Shape = new(ConvexQueryKind.ConeCylinder, 0.25f, 0.25f, 0.5f, 0.75f);

    static ConvexLeafEvidence Check(ConvexQueryShape shape, ConvexPlanarFixture fixture)
    {
        var request = new ConvexQueryRequest(shape, Pose.Identity, Vector3.Zero, ConvexQueryOperation.Distance);
        var work = new ConvexQueryWork();
        Assert.True(ConvexFeatureKernel.TryEvaluate(request, fixture.Leaf, ref work, new(), out var evidence));
        var oracle = ShortBodyShellOracle.Entry(shape, Pose.Identity, fixture.Oracle, Vector3.Zero);
        Assert.True(oracle.IsComplete);
        Assert.True(evidence.HasAttainedWitness && evidence.HasNormalConeCertificate);
        Assert.True(evidence.LowerDistance <= oracle.Lower && evidence.UpperDistance >= oracle.Upper,
            $"{evidence.QueryFeature}/{evidence.TargetFeature}: [{evidence.LowerDistance:R},{evidence.UpperDistance:R}] oracle [{oracle.Lower:R},{oracle.Upper:R}]");
        return evidence;
    }

    [Theory]
    [InlineData(false, 0.7f)]
    [InlineData(true, 0.7f)]
    [InlineData(false, -1.1f)]
    [InlineData(true, -1.1f)]
    public void ObliqueRepresentedBoxesAndNonBoxHullsMatchIndependentDistance(bool hull, float angle)
    {
        Quaternion rotation = Quaternion.CreateFromAxisAngle(Vector3.Normalize(new(1, 2, 3)), angle);
        float h = 0.00390625f;
        Vector3[] tetrahedron = [new(-h, -h, -h), new(h, -h, -h), new(0, h, -h), new(0, 0, h)];
        foreach (var shape in ConvexQueryFixture.Bodies())
        {
            foreach (var center in new[]
            {
                new Vector3(0.01f, 0.125f, 0.01f),
                new Vector3(shape.Radius * 0.5f + (shape.RingY - shape.LowerY) * 0.25f,
                    shape.LowerY + (shape.RingY - shape.LowerY) * 0.5f - shape.Radius * 0.25f, 0.01f),
                new Vector3(shape.Radius + 0.25f, shape.RingY - 0.0625f, 0.25f),
                new Vector3(shape.Radius + 0.25f, (shape.RingY + shape.UpperY) * 0.5f, 0.25f),
                new Vector3(0.0625f, shape.UpperY + 0.25f, 0.0625f),
                new Vector3(shape.Radius + 0.25f, shape.UpperY + 0.25f, 0.25f)
            })
            {
                using var fixture = new ConvexPlanarFixture(new(h), new(center, rotation), hull, vertices: tetrahedron);
                Check(shape, fixture);
            }
        }
    }

    [Fact]
    public void ClosedDegenerateExposedFeatureIntersectionsAreAttained()
    {
        using (var axisEdge = new ConvexPlanarFixture(new(0.00390625f, 0.5f, 0.00390625f), Pose.At(new(0.5f, 0.5f, 0.2f))))
        {
            var evidence = Check(Shape, axisEdge);
            Assert.InRange(evidence.QueryWitness.Y, Shape.RingY, Shape.UpperY);
            Assert.Equal(ConvexTargetFeature.Edge, evidence.TargetFeature);
        }
        foreach (var (half, position) in new[]
        {
            (new Vector3(0.5f, 0.125f, 0.125f), new Vector3(0, 0.375f, 0.125f)),
            (new Vector3(0.125f), new Vector3(0.375f, 0.875f, 0)),
            (new Vector3(0.125f), new Vector3(0.375f, 0.375f, 0))
        })
        {
            using var fixture = new ConvexPlanarFixture(half, Pose.At(position));
            Assert.Equal(0, Check(Shape, fixture).UpperDistance);
        }
    }

    [Fact]
    public void FaceSectionOverlapDoesNotRequireAnAxisSampleOrSupportProjection()
    {
        var duck = new ConvexQueryShape(ConvexQueryKind.ConeCylinder, 0.2f, 0.4f, 0.45f, 0.5f);
        Quaternion rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -MathF.PI / 4);
        using var fixture = new ConvexPlanarFixture(new(1, 0.00390625f, 1), new(new(0, 0.475f, 0), rotation));
        Assert.Equal(0, Check(duck, fixture).UpperDistance);
    }

    [Fact]
    public void CircularRootWorkSharesTheStickyPrimitiveAccount()
    {
        using var fixture = new ConvexPlanarFixture(new(0.25f, 0.00390625f, 0.00390625f),
            new(new(0.5f, 1, 0.2f), new Quaternion(0, 0, -0.5f, 0.8660254f)));
        var request = new ConvexQueryRequest(Shape, Pose.Identity, Vector3.Zero, ConvexQueryOperation.Distance);
        var scratch = new ConvexQueryScratch();
        var probe = new ConvexQueryWork();
        Assert.True(ConvexFeatureKernel.TryEvaluate(request, fixture.Leaf, ref probe, scratch, out _));
        foreach (var limit in new[] { new ConvexQueryWork(maximumCells: probe.Cells - 1), new ConvexQueryWork(maximumFeatures: probe.Features - 1) })
        {
            var work = limit;
            Assert.False(ConvexFeatureKernel.TryEvaluate(request, fixture.Leaf, ref work, scratch, out var evidence));
            Assert.Equal(default, evidence);
            Assert.True(work.Exhausted);
        }
        var fresh = new ConvexQueryWork();
        Assert.True(ConvexFeatureKernel.TryEvaluate(request, fixture.Leaf, ref fresh, scratch, out _));
    }
}
