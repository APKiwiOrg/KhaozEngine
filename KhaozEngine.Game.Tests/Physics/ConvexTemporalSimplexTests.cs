using System;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;
using Xunit.Abstractions;
using P = KhaozEngine.Physics.Bepu.ConvexExactPoint;
using Q = KhaozEngine.Physics.Bepu.ConvexQuadratic;

namespace KhaozEngine.Tests.Physics;

public sealed partial class ConvexTemporalSimplexTests(ITestOutputHelper output)
{
    static ConvexQueryShape Shape(float margin = 0) => new(ConvexQueryKind.ConeCylinder, 0.25f, 0.25f, 0.5f, 0.75f, margin);
    static ConvexQueryRequest Request(float margin = 0) => new(Shape(margin), Pose.Identity, new(0.125f, 0, 0), ConvexQueryOperation.Sweep);

    static ConvexImpactCertificate Impact(ConvexQueryRequest request, ConvexPlanarFixture fixture,
        out ConvexQueryWork work, out ConvexQueryScratch scratch, double from = 0, double to = 1)
    {
        work = new(); scratch = new();
        Assert.True(work.Primitive());
        bool success = ConvexTemporalSimplex.TryImpact(request, fixture.Leaf, from, to, ref work, scratch, out var impact);
        Assert.True(success, $"cells={work.Cells} features={work.Features} exhausted={work.Exhausted}");
        Assert.True(impact.IsAlive);
        Assert.True(impact.Contact.IsComplete && impact.Contact.HasNormalConeCertificate);
        Assert.NotEqual(Vector3.Zero, impact.Contact.Normal);
        Assert.Equal(1, work.Descendants);
        Assert.True(impact.Time.TryCompare(ConvexCertificateFraction.From(from), ref work, scratch, out int lower) && lower >= 0);
        Assert.True(impact.Time.TryCompare(ConvexCertificateFraction.From(to), ref work, scratch, out int upper) && upper <= 0);
        return impact;
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.001f)]
    public void TemporalBarycentricsRecoverAnOriginalPathEvent(float margin)
    {
        using var fixture = new ConvexPlanarFixture(new(0.0625f), Pose.At(new(0.375f, 0.625f, 0)));
        var request = Request(margin);
        var impact = Impact(request, fixture, out var work, out var scratch);
        Assert.True(impact.TryValidate(request, fixture.Leaf, ref work, scratch));
        Assert.True(impact.Pair.TryCompareSquaredMargin(margin, ref work, scratch, out int sign) && sign <= 0);
        if (margin == 0) Assert.Equal(impact.Pair.Query, impact.Pair.Target);
        output.WriteLine($"box margin={margin} cells={work.Cells} features={work.Features}");

        Assert.True(ConvexPlanarSource.TryCreate(fixture.Leaf, out var source));
        // Independent affine coordinates exercise every rank. The expected weights are 1/count.
        int[] ids = [0, 1, 2, 4];
        for (int count = 1; count <= 4; count++)
        {
            var vertices = new ConvexTemporalVertex[count];
            P point = default;
            Q expectedTime = default;
            for (int i = 0; i < count; i++)
            {
                vertices[i] = ConvexTemporalVertex.Create(request, source, ids[i], i / 4d);
                point += vertices[i].Point * (Q.From(1) / Q.From(count));
                expectedTime += Q.From(i / 4d) / Q.From(count);
            }
            Assert.True(ConvexTemporalSimplex.TryCreate(request, source, vertices, out var simplex));
            Assert.Equal(count - 1, simplex.Rank);
            Assert.True(simplex.TryCoordinates(point, ref work, out var weights, out var time));
            Assert.Equal(0, (time.Value - expectedTime).Sign);
            foreach (Q weight in weights) Assert.Equal(0, (weight - Q.From(1) / Q.From(count)).Sign);
        }
    }

    [Fact]
    public void InnerSimplexMissCannotPublishClear()
    {
        using var fixture = new ConvexPlanarFixture(new(0.0625f), Pose.At(new(2, 0.625f, 0)));
        var request = Request();
        var work = new ConvexQueryWork();
        Assert.True(work.Primitive());
        Assert.False(ConvexTemporalSimplex.TryImpact(request, fixture.Leaf, 0, 1, ref work, new(), out var impact));
        Assert.Equal(default, impact);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.001f)]
    public void TangentBetweenDyadicSamplesIsAnAttainedHit(float margin)
    {
        // The original vertex touches the cylinder only at t=1/3. Neither endpoint touches.
        Vector3 p = new(0.25f, 0.625f, 0);
        using var fixture = new ConvexPlanarFixture(default, Pose.Identity, hull: true,
            vertices: [p, p + new Vector3(0.03125f, 0, 0), p + new Vector3(0.03125f, 0.03125f, 0.03125f), p + new Vector3(0.03125f, -0.03125f, 0.03125f)]);
        var request = Request(margin) with { Pose = Pose.At(new(-margin, 0, -0.0625f)), Displacement = new(0, 0, 0.1875f) };
        var impact = Impact(request, fixture, out var work, out var scratch);
        Assert.Equal(0, (impact.Time.Value - Q.From(1) / Q.From(3)).Sign);
        Assert.True(impact.Pair.TryCompareSquaredMargin(margin, ref work, scratch, out int sign));
        Assert.Equal(0, sign);
        Assert.True(impact.Pair.SquaredDistanceBounds.Upper <= (Q.From(margin) * Q.From(margin)).Bounds.Upper);
        output.WriteLine($"tangent margin={margin} cells={work.Cells} features={work.Features}");
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.001f)]
    public void ClosedApexAndRimSeamsKeepTheirOriginalDomains(float margin)
    {
        foreach (var (feature, point, normal) in new[]
        {
            (ConvexShellFeature.Apex, new Vector3(0, 0.25f, 0), -Vector3.UnitY),
            (ConvexShellFeature.LowerCone, new Vector3(0.125f, 0.375f, 0), new Vector3(1, -1, 0)),
            (ConvexShellFeature.Shoulder, new Vector3(0.25f, 0.5f, 0), new Vector3(2, -1, 0)),
            (ConvexShellFeature.Cylinder, new Vector3(0.25f, 0.625f, 0), Vector3.UnitX),
            (ConvexShellFeature.HeadDisc, new Vector3(0, 0.75f, 0), Vector3.UnitY),
            (ConvexShellFeature.Rim, new Vector3(0.25f, 0.75f, 0), new Vector3(1, 1, 0))
        })
            foreach (var stratum in new[] { ConvexTargetFeature.Face, ConvexTargetFeature.Edge, ConvexTargetFeature.Vertex })
            {
                Vector3 u = new Vector3(normal.Y, -normal.X, 0) / 64, v = Vector3.UnitZ / 64, d = normal / 64;
                Vector3[] vertices = stratum switch
                {
                    ConvexTargetFeature.Face => [point - u - v, point + u - v, point - u + v, point + u + v,
                        point - u - v + d, point + u - v + d, point - u + v + d, point + u + v + d],
                    ConvexTargetFeature.Edge => [point - v, point + v, point + d - u - v, point + d + u - v, point + d - u + v, point + d + u + v],
                    _ => [point, point + d + u, point + d - u + v, point + d - u - v]
                };
                using var fixture = new ConvexPlanarFixture(default, Pose.Identity, hull: true, vertices: vertices);
                var request = Request(margin) with { Pose = Pose.At(normal / -8), Displacement = normal / 8 };
                var oracle = ShortBodyShellOracle.AttainedSweepPoint(request.Shape, request.Pose, fixture.Oracle,
                    request.Displacement, point, 1, 1);
                Assert.True(oracle.IsComplete && oracle.Status == ConvexSweepStatus.Hit);
                var impact = Impact(request, fixture, out var work, out var scratch);
                // The primal event and the original-member MTV can use different support
                // features inside the admitted penetration bracket. Check the attained stratum.
                Assert.False(impact.Pair.IsCircular);
                Assert.Equal(impact.Pair.Query, impact.Pair.Target);
                Assert.Equal(feature, ConvexPlanarFeatures.Classify(request.Shape, impact.Pair.Query));
                if (margin == 0) Assert.Equal(feature, impact.Contact.QueryFeature);
                else Assert.True(impact.Penetration.IsAlive);
                Assert.True(impact.TryValidate(request, fixture.Leaf, ref work, scratch));
                output.WriteLine($"seam m={margin} {feature}/{stratum} cells={work.Cells} features={work.Features}");
            }
    }

    [Fact]
    public void RoundedCircularDoubleRootRetainsItsUnsquaredSigns()
    {
        // A line tangent to the rounded rim, with two nonzero radial components. Its repeated
        // margin root must retain the original (positive) radial and distance-difference signs.
        float m = 0.001f;
        var shape = new ConvexQueryShape(ConvexQueryKind.ConeCylinder, 0.3125f, 0.25f, 0.5f, 0.75f, m);
        Vector3 p = new(0.1875f, 0.75f, 0.25f);
        using var fixture = new ConvexPlanarFixture(default, Pose.Identity, hull: true,
            vertices: [p, p + Vector3.UnitX / 32, p + Vector3.UnitY / 32, p + Vector3.UnitZ / 32]);
        // The attained offset is m*(9,20,12)/25. The tangent (8,15,-31) is
        // perpendicular to that normal, and the attained fraction is 1024*m/25.
        var request = new ConvexQueryRequest(shape, Pose.At(new(-m, -2 * m, 2 * m)),
            new(8f / 512, 15f / 512, -31f / 512), ConvexQueryOperation.Sweep);
        var oracle = ShortBodyShellOracle.AttainedSweepVertex(shape, request.Pose, fixture.Oracle,
            request.Displacement, 1024, 25, m);
        Assert.True(oracle.IsComplete);
        Assert.Equal(ConvexSweepStatus.Hit, oracle.Status);
        var negative = request with { Displacement = -request.Displacement };
        var negativeOracle = ShortBodyShellOracle.Entry(shape, negative.Pose, fixture.Oracle, negative.Displacement);
        Assert.True(negativeOracle.IsComplete);
        Assert.Equal(ConvexSweepStatus.Clear, negativeOracle.Status);
        var negativeWork = new ConvexQueryWork();
        Assert.True(negativeWork.Primitive());
        Assert.False(ConvexTemporalSimplex.TryImpact(negative, fixture.Leaf, 0, 1, ref negativeWork, new(), out var miss));
        Assert.Equal(default, miss);
        var impact = Impact(request, fixture, out var work, out var scratch);
        Assert.True(impact.Pair.TryCompareSquaredMargin(m, ref work, scratch, out int sign));
        Assert.Equal(0, sign);
        Assert.True(impact.Time.TryCompare(ConvexCertificateFraction.From(Q.From(1024) * Q.From(m) / Q.From(25)),
            ref work, scratch, out int timeSign));
        Assert.Equal(0, timeSign);
        Assert.True(impact.OriginalPair.IsAlive && impact.OriginalPair.Source.IsOriginal);
        Assert.True(impact.OriginalPair.TryCompareSquaredMargin(m, ref work, scratch, out int originalSign));
        Assert.Equal(0, originalSign);
        Assert.Equal(0, impact.Contact.UpperDistance);
        Assert.True(impact.TryValidate(request, fixture.Leaf, ref work, scratch));
    }

    [Fact]
    public void TemporalWitnessCannotBorrowAnotherLeafNormal()
    {
        using var a = new ConvexPlanarFixture(new(0.0625f), Pose.At(new(0.375f, 0.625f, 0)));
        using var b = new ConvexPlanarFixture(new(0.0625f), Pose.At(new(0.375f, 0.625f, 0)));
        var request = Request();
        var impact = Impact(request, a, out var work, out var scratch);
        Assert.False(impact.TryValidate(request, b.Leaf, ref work, scratch));
        Assert.False(impact.TryValidate(request with { Displacement = new(0.25f, 0, 0) }, a.Leaf, ref work, scratch));
        Assert.False(impact.TryValidate(request, a.Leaf, ref work, new()));
        scratch.BeginCertificates();
        Assert.False(impact.IsAlive);
        var denied = new ConvexQueryWork(maximumCells: 0);
        Assert.True(denied.Primitive());
        Assert.False(ConvexTemporalSimplex.TryImpact(request, a.Leaf, 0, 1, ref denied, new(), out var refused));
        Assert.Equal(default, refused);
        Assert.True(denied.Exhausted);
    }
}
