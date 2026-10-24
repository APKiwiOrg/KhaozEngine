using System;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;
using Q = KhaozEngine.Physics.Bepu.ConvexQuadratic;
using P = KhaozEngine.Physics.Bepu.ConvexExactPoint;

namespace KhaozEngine.Tests.Physics;

public sealed partial class ConvexTemporalSimplexTests
{
    static ConvexPlanarFixture RoundedFixture() => new(default, Pose.Identity, hull: true,
        vertices: [new(0.1875f, 0.75f, 0.25f), new(0.21875f, 0.75f, 0.25f),
            new(0.1875f, 0.78125f, 0.25f), new(0.1875f, 0.75f, 0.28125f)]);
    static ConvexQueryRequest RoundedRequest(float offsetScale = 1) => new(
        new(ConvexQueryKind.ConeCylinder, 0.3125f, 0.25f, 0.5f, 0.75f, 0.001f),
        Pose.At(new Vector3(-0.001f, -0.002f, 0.002f) * offsetScale),
        new(8f / 512, 15f / 512, -31f / 512), ConvexQueryOperation.Sweep);

    [Fact]
    public void RootDefinedWeightsSurviveRefinementAndRetainTheirOriginalLabels()
    {
        using var fixture = RoundedFixture();
        var request = RoundedRequest();
        var impact = Impact(request, fixture, out var work, out var scratch);
        var attained = impact.Attainment;
        Assert.NotNull(attained);
        Assert.True(attained.IsAlive);
        Assert.True(attained.Pair.IsCircular);
        Assert.NotNull(impact.Time.Root);
        ConvexRational sumOffset = default, sumScale = default;
        for (int i = 0; i < attained.Weights.Length; i++)
        {
            var weight = attained.Weights[i];
            Assert.Same(impact.Time.Root, weight.Root);
            Assert.True(weight.TryCompare(ConvexCertificateFraction.From(0), ref work, scratch, out int lo) && lo >= 0);
            Assert.True(weight.TryCompare(ConvexCertificateFraction.From(1), ref work, scratch, out int hi) && hi <= 0);
            sumOffset += weight.Offset; sumScale += weight.Scale;
            Assert.True(attained.Vertices[i].Matches(request, impact.Original));
        }
        Assert.Equal(0, (sumOffset - ConvexRational.One).Sign);
        Assert.True(sumScale.IsZero);
        var first = attained.Weights[0];
        Assert.True(impact.Time.TryRefine(1e-12, ref work, scratch, out _));
        Assert.Equal(first.Offset, attained.Weights[0].Offset);
        Assert.Equal(first.Scale, attained.Weights[0].Scale);
        Assert.True(impact.IsAlive);
        Assert.False(impact.Time.TryCompare(ConvexCertificateFraction.FromRoot(
            OtherRoot(), 0, 1), ref work, scratch, out _));
        scratch.BeginCertificates();
        Assert.False(attained.IsAlive);
        Assert.False(impact.Time.IsValid);
    }

    static ConvexAlgebraicRoots.Retained OtherRoot()
    {
        var scratch = new ConvexQueryScratch(); var work = new ConvexQueryWork();
        Assert.True(work.Primitive());
        var equation = scratch.Polynomials.Create(2);
        equation.Set(0, ConvexRational.From(-0.5)); equation.Set(1, ConvexRational.One);
        Assert.True(ConvexAlgebraicRoots.TryRetain(equation, 0, 1, ref work, scratch, out var root));
        return root;
    }

    [Fact]
    public void CircularStrictOverlapUsesADyadicOriginalPenetrationEndpoint()
    {
        using var fixture = RoundedFixture();
        var request = RoundedRequest(0.5f);
        var oracle = ShortBodyShellOracle.AttainedSweepVertex(request.Shape, request.Pose, fixture.Oracle,
            request.Displacement, 512, 25, 0.001f);
        Assert.True(oracle.IsComplete);
        var impact = Impact(request, fixture, out var work, out var scratch);
        Assert.True(impact.Attainment!.Pair.IsCircular);
        Assert.True(impact.Pair.TryCompareSquaredMargin(0.001f, ref work, scratch, out int sign) && sign < 0);
        Assert.NotNull(impact.Time.Root);
        Assert.Null(impact.NormalTime.Root);
        Assert.True(impact.NormalTime.Value.Rational);
        Assert.True(impact.NormalTime.TryCompare(impact.Time, ref work, scratch, out int order) && order >= 0);
        Assert.True(impact.Penetration.IsAlive);
        Assert.True(impact.Attainment.IsAlive);
        Assert.True(impact.TryValidate(request, fixture.Leaf, ref work, scratch));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.001f)]
    public void StationaryIntervalsRetainAnAttainedOriginalEndpoint(float margin)
    {
        using var fixture = new ConvexPlanarFixture(new(0.0625f), Pose.At(new(0.3125f, 0.625f, 0)));
        var request = Request(margin) with { Displacement = Vector3.Zero };
        var impact = Impact(request, fixture, out var work, out var scratch, 0.25, 0.75);
        Assert.True(impact.TryValidate(request, fixture.Leaf, ref work, scratch));
        Assert.True(impact.Pair.TryCompareSquaredMargin(margin, ref work, scratch, out int sign) && sign <= 0);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.001f)]
    public void NonzeroPathsCanHaveAStationaryContactInterval(float margin)
    {
        using var fixture = new ConvexPlanarFixture(new(0.5f, 0.03125f, 0.5f), Pose.At(new(0, 0.78125f, 0)));
        var request = Request(margin);
        var impact = Impact(request, fixture, out var work, out var scratch, 0.25, 0.75);
        Assert.True(impact.TryValidate(request, fixture.Leaf, ref work, scratch));
        Assert.Equal(impact.Pair.Query, impact.Pair.Target);
        Assert.True(impact.Contact.Normal.Y < -0.99f);
    }

    [Theory]
    [InlineData(false, 0f)]
    [InlineData(true, 0f)]
    [InlineData(false, 0.001f)]
    [InlineData(true, 0.001f)]
    public void ArbitraryRepresentedPosesUseOriginalBoxAndHullGeometry(bool hull, float margin)
    {
        Quaternion orientation = Quaternion.Normalize(new(0.13f, -0.27f, 0.19f, 0.92f));
        float h = 0.015625f;
        using var fixture = new ConvexPlanarFixture(new(h), new(new(0, 0.625f, 0), orientation), hull,
            vertices: [new(-h, -h, -h), new(h, -h, -h), new(0, h, -h), new(0, 0, h)]);
        var request = Request(margin) with { Pose = Pose.At(new(-0.125f, 0, 0)), Displacement = new(0.125f, 0, 0) };
        var impact = Impact(request, fixture, out var work, out var scratch);
        Assert.True(impact.TryValidate(request, fixture.Leaf, ref work, scratch));
        Assert.True(impact.Attainment!.IsAlive);
        Assert.True(impact.Penetration.IsAlive);
        Assert.True(impact.Time.TryCompare(ConvexCertificateFraction.From(1), ref work, scratch, out int endpoint) && endpoint == 0);
        var oracle = ShortBodyShellOracle.Entry(request.Shape, Pose.Identity, fixture.Oracle, Vector3.Zero);
        Assert.True(oracle.IsComplete && oracle.Upper == 0);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.001f)]
    public void TemporalTranslationKeepsTheBoundOnOriginalInstalledVertices(float margin)
    {
        using var fixture = new ConvexPlanarFixture(new(0.0625f), Pose.At(new(0.3125f, 0.625f, 0)));
        // At t=1 the feet are exactly zero. The installed box is inside the 2048 m bound,
        // while its labelled endpoint coordinates include the 4096 m translation.
        var request = Request(margin) with { Pose = Pose.At(new(4096, 0, 0)), Displacement = new(-4096, 0, 0) };
        var impact = Impact(request, fixture, out var work, out var scratch, 1, 1);
        Assert.True(impact.Time.TryCompare(ConvexCertificateFraction.From(1), ref work, scratch, out int at) && at == 0);
        Assert.True(impact.TryValidate(request, fixture.Leaf, ref work, scratch));
    }

    [Fact]
    public void TemporalTranslationCannotHideAnOutOfDomainInstalledVertex()
    {
        using var fixture = new ConvexPlanarFixture(new(0.0625f), Pose.At(new(2048, 0.625f, 0)));
        var request = Request() with { Pose = Pose.At(new(2048, 0, 0)), Displacement = Vector3.Zero };
        var work = new ConvexQueryWork(); Assert.True(work.Primitive());
        Assert.False(ConvexTemporalSimplex.TryImpact(request, fixture.Leaf, 0, 1, ref work, new(), out var refused));
        Assert.Equal(default, refused);
    }

    [Fact]
    public void LoweredTemporalBudgetsRefuseAtomicallyUsingTheSameRunProbe()
    {
        using var fixture = RoundedFixture();
        var request = RoundedRequest();
        var probe = new ConvexQueryWork(); Assert.True(probe.Primitive());
        Assert.True(ConvexTemporalSimplex.TryImpact(request, fixture.Leaf, 0, 1, ref probe, new(), out var impact));
        Assert.True(impact.IsAlive);
        foreach (var limit in new[] { new ConvexQueryWork(maximumCells: probe.Cells - 1),
            new ConvexQueryWork(maximumFeatures: probe.Features - 1) })
        {
            var work = limit; var scratch = new ConvexQueryScratch();
            Assert.True(work.Primitive());
            Assert.False(ConvexTemporalSimplex.TryImpact(request, fixture.Leaf, 0, 1, ref work, scratch, out var refused));
            Assert.Equal(default, refused);
            Assert.True(work.Exhausted);
        }
        var coefficients = new ConvexQueryScratch(maximumCoefficients: 0);
        var account = new ConvexQueryWork(); Assert.True(account.Primitive());
        Assert.False(ConvexTemporalSimplex.TryImpact(request, fixture.Leaf, 0, 1, ref account, coefficients, out var missing));
        Assert.Equal(default, missing);
    }

    [Fact]
    public void AnInnerPositivePairCannotSupplyTheOriginalMembersNormal()
    {
        using var fixture = new ConvexPlanarFixture(new(0.0625f), Pose.At(new(0.25f, 0.625f, 0)));
        var request = Request(0.001f) with { Pose = Pose.At(new(0, -0.06298828125f, 0)), Displacement = Vector3.Zero };
        Assert.True(ConvexPlanarSource.TryCreate(fixture.Leaf, out var original));
        var vertices = new ConvexTemporalVertex[3]; int count = 0;
        for (int i = 0; i < original.VertexCount && count < 3; i++)
            if ((original.Vertex(i).Y - Q.From(0.6875)).Sign == 0)
                vertices[count++] = ConvexTemporalVertex.Create(request, original, i, 0);
        Assert.Equal(3, count);
        Assert.True(ConvexTemporalSimplex.TryCreate(request, original, vertices, out var simplex));
        var work = new ConvexQueryWork(); var scratch = new ConvexQueryScratch(); Assert.True(work.Primitive());
        Assert.True(ConvexFeatureKernel.TryDistanceCertificate(request, simplex.Source, ConvexCertificateFraction.From(0),
            ref work, scratch, out var inner));
        Assert.True(inner.TryCompareSquaredMargin(0.001f, ref work, scratch, out int gap) && gap < 0);
        Assert.Equal(0, inner.TowardTarget.X.Sign);
        Assert.True(inner.TowardTarget.Y.Sign > 0);
        Assert.True(simplex.TryTime(inner, ref work, scratch, out var time));
        Assert.True(ConvexImpactCertificate.TryCreate(request, original, simplex, inner, time, 0, 1,
            ref work, scratch, out var impact));
        Assert.True(impact.IsAlive && impact.Penetration.IsAlive);
        Assert.True(impact.Contact.Normal.X < -0.99f);
        Assert.True(MathF.Abs(impact.Contact.Normal.Y) < 0.01f);
        Assert.True(impact.TryValidate(request, fixture.Leaf, ref work, scratch));
    }

    [Fact]
    public void CoordinatorEndpointLoanAvoidsReenumeratingItsStaticCertificate()
    {
        using var fixture = new ConvexPlanarFixture(new(0.0625f), Pose.At(new(0.375f, 0.625f, 0)));
        var request = Request();
        var plain = new ConvexQueryWork(); Assert.True(plain.Primitive());
        Assert.True(ConvexTemporalSimplex.TryImpact(request, fixture.Leaf, 0, 1, ref plain, new(), out _));
        var work = new ConvexQueryWork(); var scratch = new ConvexQueryScratch(); Assert.True(work.Primitive());
        Assert.True(ConvexPlanarSource.TryCreate(fixture.Leaf, out var source));
        Assert.True(ConvexFeatureKernel.TryDistanceCertificate(request, source, ConvexCertificateFraction.From(1),
            ref work, scratch, out var endpoint));
        int before = work.Cells;
        Assert.True(ConvexTemporalSimplex.TryImpact(request, fixture.Leaf, 0, 1, endpoint,
            ref work, scratch, out var impact));
        Assert.True(impact.IsAlive && endpoint.IsAlive);
        Assert.True(work.Cells - before < plain.Cells);
        Assert.Equal(1, work.Descendants);
        var foreign = new ConvexQueryScratch();
        Assert.False(ConvexTemporalSimplex.TryImpact(request, fixture.Leaf, 0, 1, endpoint,
            ref work, foreign, out var refused));
        Assert.Equal(default, refused);
    }

    [Fact]
    public void SquaredCircularRootsCannotDiscardTheOriginalRadicalSigns()
    {
        using var fixture = RoundedFixture();
        var request = RoundedRequest();
        var impact = Impact(request, fixture, out var work, out var scratch);
        Q m = Q.From(0.001f), radial = Q.From(0.625) + Q.From(3) * m / Q.From(5);
        Q vertical = Q.From(4) * m / Q.From(5);
        Assert.True(impact.Pair.Circular!.TryCompareSquared(radial * radial + vertical * vertical,
            ref work, scratch, out int wrongBranch));
        Assert.Equal(-1, wrongBranch);
        Assert.True(ConvexPlanarSource.TryCreate(fixture.Leaf, out var source));
        var fresh = new ConvexQueryScratch(); var freshWork = new ConvexQueryWork(); Assert.True(freshWork.Primitive());
        var context = new ConvexDistanceContext(request with { Shape = Shape(0.001f) }, source,
            ConvexCertificateFraction.From(0), fresh, false);
        P point = new(Q.From(0.1875), Q.From(1), Q.From(0.25));
        P speed = new(Q.From(1), -Q.From(27) / Q.From(20), default);
        Assert.False(ConvexCircularCertificate.TryCreate(context, point - speed * Q.From(0.5), speed,
            Q.From(0.25), Q.From(0.75), GeometryInterval.Enclose(0.49, 0.51), 0, ref freshWork, out var rejected));
        Assert.Null(rejected);
        Assert.False(freshWork.Exhausted);
    }
}
