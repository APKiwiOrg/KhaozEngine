using System;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;
using Xunit.Abstractions;
using Q = KhaozEngine.Physics.Bepu.ConvexQuadratic;

namespace KhaozEngine.Tests.Physics;

public sealed class ConvexIntervalCertificateTests(ITestOutputHelper output)
{
    static ConvexQueryShape Shape(float margin = 0) => new(ConvexQueryKind.ConeCylinder, 0.25f, 0.25f, 0.5f, 0.75f, margin);
    static ConvexQueryRequest Request(float margin = 0) => new(Shape(margin), Pose.Identity, new(0.125f, 0, 0), ConvexQueryOperation.Sweep);

    [Theory]
    [InlineData(0f)]
    [InlineData(0.001f)]
    public void IntervalEndpointsProveEveryTranslatedFraction(float margin)
    {
        using var fixture = new ConvexPlanarFixture(new(0.125f), new(new(1, 0.625f, 0),
            Quaternion.CreateFromAxisAngle(Vector3.Normalize(new(1, 2, 3)), 0.7f)));
        var request = Request(margin);
        var work = new ConvexQueryWork();
        Assert.True(work.Primitive());
        var scratch = new ConvexQueryScratch();
        Assert.True(ConvexIntervalCertificate.TryCreate(request, fixture.Leaf, Vector3.UnitX, 0, 1, ref work, scratch, out var plane));
        Assert.True(plane.FromGap.Lower > 0 && plane.ToGap.Lower > 0);
        Assert.True(plane.UniformError >= 0);
        // Affinity is the proof, these points exercise the certificate's evaluation API.
        foreach (double fraction in new[] { 0d, 0.125, 0.5, 0.875, 1 })
        {
            Assert.True(plane.TryGap(fraction, ref work, scratch, out var gap));
            Assert.True(gap.Lower > 0);
        }
        int features = work.Features;
        Assert.True(plane.TryRestrict(0.25, 0.75, ref work, scratch, out var restricted));
        Assert.True(restricted.FromGap.Lower > 0);
        Assert.True(work.Features - features < fixture.Leaf.Geometry.VertexCount);
        Assert.False(ConvexIntervalCertificate.TryCreate(request with { Displacement = Vector3.UnitX }, fixture.Leaf,
            Vector3.UnitX, 0, 1, ref work, scratch, out var failed));
        Assert.Equal(default, failed);
        output.WriteLine($"interval cells={work.Cells} features={work.Features} arena={scratch.Polynomials.Used}");
    }

    [Fact]
    public void MarginOverlapRetainsThePositiveBasePair()
    {
        using var fixture = new ConvexPlanarFixture(new(0.125f), Pose.At(new(0.3755f, 0.625f, 0)));
        var request = Request(0.001f) with { Displacement = Vector3.Zero };
        var work = new ConvexQueryWork();
        Assert.True(work.Primitive());
        var scratch = new ConvexQueryScratch();
        Assert.True(ConvexPlanarSource.TryCreate(fixture.Leaf, out var source));
        Assert.True(ConvexFeatureKernel.TryDistanceCertificate(request, source, ConvexCertificateFraction.From(0), ref work, scratch, out var pair));
        Assert.True(pair.BaseSquaredDistance.Sign > 0);
        Assert.NotEqual(0, pair.TowardTarget.X.Sign);
        Assert.True(pair.TryCompareSquaredMargin(0.001f, ref work, scratch, out int sign));
        Assert.Equal(-1, sign);
        Assert.True(ConvexDistanceEncoding.TryCreate(pair, out var evidence));
        Assert.Equal(0, evidence.UpperDistance);
        Assert.Equal(Vector3.Zero, evidence.Normal);
        Assert.Equal(1, work.Descendants);
    }

    [Fact]
    public void ExactMarginTieIsNotAnIntervalZero()
    {
        using var fixture = new ConvexPlanarFixture(new(0.125f), Pose.At(new(0.375f, 0.625f, 0)));
        foreach (float margin in new[] { 0f, 0.001f })
        {
            var request = Request(margin) with { Pose = Pose.At(new(-margin, 0, 0)), Displacement = Vector3.Zero };
            var work = new ConvexQueryWork();
            Assert.True(work.Primitive());
            var scratch = new ConvexQueryScratch();
            Assert.True(ConvexPlanarSource.TryCreate(fixture.Leaf, out var source));
            Assert.True(ConvexFeatureKernel.TryDistanceCertificate(request, source, ConvexCertificateFraction.From(0), ref work, scratch, out var pair));
            Assert.True(pair.TryCompareSquaredMargin(margin, ref work, scratch, out int sign));
            Assert.Equal(0, sign);
            Assert.False(ConvexIntervalCertificate.TryCreate(request, fixture.Leaf, Vector3.UnitX, 0, 1, ref work, scratch, out _));
            if (margin == 0) continue;
            Assert.True(pair.TryCompareSquaredMargin(MathF.BitDecrement(margin), ref work, scratch, out sign));
            Assert.Equal(1, sign);
            Assert.True(pair.TryCompareSquaredMargin(MathF.BitIncrement(margin), ref work, scratch, out sign));
            Assert.Equal(-1, sign);
        }
    }

    [Fact]
    public void FractionEvaluationKeepsTheOriginalFeetBits()
    {
        using var fixture = new ConvexPlanarFixture(new(0.125f), Pose.At(new(0.5f, 0.625f, 0)));
        float minusZero = BitConverter.Int32BitsToSingle(unchecked((int)0x80000000));
        var request = Request() with { Pose = Pose.At(new(minusZero, 0, 0)), Displacement = new(MathF.BitIncrement(0.125f), 0, 0) };
        var work = new ConvexQueryWork();
        Assert.True(work.Primitive());
        var scratch = new ConvexQueryScratch();
        Assert.True(ConvexPlanarSource.TryCreate(fixture.Leaf, out var source));
        var fraction = ConvexCertificateFraction.From(0.3);
        Assert.True(ConvexFeatureKernel.TryDistanceCertificate(request, source, fraction, ref work, scratch, out var pair));
        Assert.Equal(unchecked((int)0x80000000), BitConverter.SingleToInt32Bits(pair.Request.Pose.Position.X));
        Q exactGap = Q.From(0.125) - Q.From(request.Displacement.X) * Q.From(0.3);
        Assert.Equal(0, (pair.BaseSquaredDistance - exactGap * exactGap).Sign);
        Assert.Equal(0, (ConvexCertificateFraction.From(minusZero).Value - ConvexCertificateFraction.From(0).Value).Sign);
        Assert.Equal(0, (pair.Fraction.Value - fraction.Value).Sign);
        Assert.Equal(1, work.Descendants);
    }

    [Fact]
    public void InnerSourceCannotCertifyOriginalTargetClear()
    {
        using var fixture = new ConvexPlanarFixture(new(0.125f, 0.125f, 0.125f), Pose.At(new(0.375f, 0.625f, 0)));
        Assert.True(ConvexPlanarSource.TryCreate(fixture.Leaf, out var source));
        int far = 0;
        for (int i = 1; i < source.VertexCount; i++)
            if ((source.Vertex(i).X - source.Vertex(far).X).Sign > 0) far = i;
        Assert.True(source.TrySimplex([far], out var inner));
        var request = Request() with { Displacement = Vector3.Zero };
        var work = new ConvexQueryWork();
        Assert.True(work.Primitive());
        var scratch = new ConvexQueryScratch();
        Assert.True(ConvexFeatureKernel.TryDistanceCertificate(request, inner, ConvexCertificateFraction.From(0), ref work, scratch, out var pair));
        Assert.True(pair.BaseSquaredDistance.Sign > 0);
        Assert.False(ConvexDistanceEncoding.TryCreate(pair, out _));
        Assert.False(ConvexIntervalCertificate.TryCreate(request, fixture.Leaf, Vector3.UnitX, 0, 1, ref work, scratch, out _));
        Assert.Equal(far, inner.OriginalVertex(0));
    }

    [Fact]
    public void NestedCertificatesCannotResetPrimitiveWork()
    {
        using var fixture = new ConvexPlanarFixture(new(0.125f), Pose.At(new(1, 0.625f, 0)));
        Assert.True(ConvexPlanarSource.TryCreate(fixture.Leaf, out var source));
        var request = Request();
        var probe = new ConvexQueryWork();
        Assert.True(probe.Primitive());
        Assert.True(ConvexFeatureKernel.TryDistanceCertificate(request, source, ConvexCertificateFraction.From(0), ref probe, new(), out _));
        var work = new ConvexQueryWork(maximumCells: probe.Cells, maximumFeatures: probe.Features);
        Assert.True(work.Primitive());
        var scratch = new ConvexQueryScratch();
        Assert.True(ConvexFeatureKernel.TryDistanceCertificate(request, source, ConvexCertificateFraction.From(0), ref work, scratch, out var first));
        Assert.False(ConvexFeatureKernel.TryDistanceCertificate(request, source, ConvexCertificateFraction.From(0.5), ref work, scratch, out _));
        Assert.True(work.Exhausted);
        Assert.Equal(1, work.Descendants);
        Assert.Equal(probe.Cells, work.Cells);
        Assert.Equal(probe.Features, work.Features);
        Assert.Equal(0, scratch.Polynomials.Used);
        Assert.False(first.TryCompareSquaredMargin(0, ref work, scratch, out _));
        output.WriteLine($"nested cells={work.Cells} features={work.Features} descendants={work.Descendants}");
    }
    [Theory]
    [InlineData(0f)]
    [InlineData(0.001f)]
    public void RetentionCoversAllSixStaticFeatures(float margin)
    {
        foreach (var (half, point, feature) in new[]
        {
            (new Vector3(2, 0.03125f, 2), new Vector3(0, 0.125f, 0), ConvexShellFeature.Apex),
            (new Vector3(0.00390625f), new Vector3(0.1875f, 0.3125f, 0), ConvexShellFeature.LowerCone),
            (new Vector3(0.03125f), new Vector3(0.5f, 0.4375f, 0), ConvexShellFeature.Shoulder),
            (new Vector3(0.03125f, 2, 2), new Vector3(0.5f, 0.625f, 0), ConvexShellFeature.Cylinder),
            (new Vector3(2, 0.03125f, 2), new Vector3(0, 1, 0), ConvexShellFeature.HeadDisc),
            (new Vector3(0.03125f), new Vector3(0.5f, 1, 0), ConvexShellFeature.Rim)
        })
        {
            using var fixture = new ConvexPlanarFixture(half, Pose.At(point));
            var request = Request(margin) with { Displacement = Vector3.Zero, Operation = ConvexQueryOperation.Distance };
            Assert.True(ConvexPlanarSource.TryCreate(fixture.Leaf, out var source));
            var work = new ConvexQueryWork();
            Assert.True(work.Primitive());
            var scratch = new ConvexQueryScratch();
            Assert.True(ConvexFeatureKernel.TryDistanceCertificate(request, source, ConvexCertificateFraction.From(0), ref work, scratch, out var pair));
            Assert.Equal(feature, pair.QueryFeature);
            Assert.True(ConvexDistanceEncoding.TryCreate(pair, out var encoded));
            var legacy = new ConvexQueryWork();
            Assert.True(ConvexFeatureKernel.TryEvaluate(request, fixture.Leaf, ref legacy, new(), out var previous));
            Assert.Equal(previous, encoded);
            output.WriteLine($"retained {feature} margin={margin:R} cells={work.Cells} features={work.Features}");
        }
    }

    [Fact]
    public void RetainedCircularRootSupportsExactSignsAndBoundTime()
    {
        using var fixture = new ConvexPlanarFixture(new(0.25f, 0.0001f, 0.0001f),
            new(new(0.5f, 1, 0.2f), new Quaternion(0, 0, -0.5f, 0.8660254f)));
        var request = Request() with { Displacement = Vector3.Zero };
        Assert.True(ConvexPlanarSource.TryCreate(fixture.Leaf, out var source));
        var work = new ConvexQueryWork();
        Assert.True(work.Primitive());
        var scratch = new ConvexQueryScratch();
        Assert.True(ConvexFeatureKernel.TryDistanceCertificate(request, source, ConvexCertificateFraction.From(0), ref work, scratch, out var pair));
        Assert.True(pair.IsCircular);
        Assert.True(pair.TryCompareSquaredMargin(0.001f, ref work, scratch, out int sign));
        Assert.Equal(1, sign);
        Assert.True(pair.TryBarycentricFraction(0, 1, ref work, scratch, out var time));
        Assert.True(time.TryCompare(ConvexCertificateFraction.From(0), ref work, scratch, out sign));
        Assert.Equal(1, sign);
        Assert.True(time.TryCompare(time, ref work, scratch, out sign));
        Assert.Equal(0, sign);
        Assert.True(time.TryCompare(ConvexCertificateFraction.From(1), ref work, scratch, out sign));
        Assert.Equal(-1, sign);
        int arena = scratch.Polynomials.Used;
        Assert.True(arena > 0);
        Assert.True(ConvexFeatureKernel.TryDistanceCertificate(request, source, time, ref work, scratch, out var atRoot));
        Assert.True(atRoot.TryCompareSquaredMargin(0, ref work, scratch, out sign));
        Assert.Equal(1, sign);
        Assert.Equal(arena, scratch.Polynomials.Used);
        output.WriteLine($"circular cells={work.Cells} features={work.Features} retained={arena} high={scratch.Polynomials.HighWater}");
    }

    [Fact]
    public void RetainedRootRecognizesExactEqualityAndRestoresTemporaryCoefficients()
    {
        var work = new ConvexQueryWork();
        Assert.True(work.Primitive());
        var scratch = new ConvexQueryScratch();
        scratch.BeginCertificates();
        var equation = scratch.Polynomials.Import(ConvexPolynomial.From(-0.5, 0, 1));
        Assert.True(ConvexAlgebraicRoots.TryRetain(equation, 0, 1, ref work, scratch, out var root));
        int checkpoint = scratch.Polynomials.Used;
        Assert.True(root.TrySign(equation, ref work, scratch, out int sign));
        Assert.Equal(0, sign);
        Assert.Equal(checkpoint, scratch.Polynomials.Used);
        var time = ConvexCertificateFraction.FromRoot(root, 0, 1);
        Assert.True(time.TryCompare(ConvexCertificateFraction.From(Q.Root(ConvexRational.From(0.5))), ref work, scratch, out sign));
        Assert.Equal(0, sign);
        Assert.Equal(checkpoint, scratch.Polynomials.Used);
        Assert.True(time.TryRefine(0.00001, ref work, scratch, out var bounds));
        Assert.True(bounds.Upper - bounds.Lower <= 0.00001);
        Assert.Equal(checkpoint, scratch.Polynomials.Used);
        var other = new ConvexQueryScratch();
        Assert.False(time.TryCompare(time, ref work, other, out _));
        var constant = ConvexCertificateFraction.FromRoot(root, 0.5, 0);
        Assert.False(constant.TryRefine(0.001, ref work, other, out _));
    }

    [Fact]
    public void OriginalPredicatesAtRootTimeUseTheRetainedWitness()
    {
        using var fixture = new ConvexPlanarFixture(new(0.25f, 0.0001f, 0.0001f),
            new(new(0.5f, 1, 0.2f), new Quaternion(0, 0, -0.5f, 0.8660254f)));
        Assert.True(ConvexPlanarSource.TryCreate(fixture.Leaf, out var source));
        var request = Request() with { Displacement = Vector3.Zero };
        var scratch = new ConvexQueryScratch();
        var work = new ConvexQueryWork();
        Assert.True(work.Primitive());
        Assert.True(ConvexFeatureKernel.TryDistanceCertificate(request, source, ConvexCertificateFraction.From(0), ref work, scratch, out var pair));
        Assert.True(pair.TryBarycentricFraction(1, 0, ref work, scratch, out var time));
        var shifted = Request() with { Pose = Pose.At(new(-0.125f, 0, 0)) };
        Assert.True(ConvexFeatureKernel.TryDistanceCertificate(shifted, source, time, ref work, scratch, out var atRoot));
        Assert.True(atRoot.IsCircular);
        Assert.Equal(ConvexTargetFeature.Face, atRoot.TargetFeature);
        Assert.InRange(atRoot.LocalFeatureId, 0, source.FaceCount - 1);
        Assert.True(atRoot.TryCompareSquaredMargin(0, ref work, scratch, out int sign));
        Assert.Equal(1, sign);
        Assert.True(ConvexDistanceEncoding.TryCreate(atRoot, out var encoded));
        var oracle = ShortBodyShellOracle.Entry(Shape(), Pose.Identity, fixture.Oracle, Vector3.Zero);
        Assert.True(oracle.IsComplete);
        Assert.True(encoded.LowerDistance <= oracle.Lower && encoded.UpperDistance >= oracle.Upper);
        int retained = scratch.Polynomials.Used;
        using var foreign = new ConvexPlanarFixture(new(0.125f), Pose.At(new(2, 0.625f, 0)));
        Assert.True(ConvexPlanarSource.TryCreate(foreign.Leaf, out var wrong));
        Assert.False(ConvexFeatureKernel.TryDistanceCertificate(shifted, wrong, time, ref work, scratch, out _));
        Assert.Equal(retained, scratch.Polynomials.Used);
        Assert.Equal(1, work.Descendants);
        output.WriteLine($"root revalidation cells={work.Cells} features={work.Features} retained={retained}");
    }

    [Fact]
    public void SimplexRanksRetainOriginalCoordinatesAndCannotPublish()
    {
        using var fixture = new ConvexPlanarFixture(new(0.125f), Pose.At(new(1, 0.625f, 0)));
        Assert.True(ConvexPlanarSource.TryCreate(fixture.Leaf, out var source));
        foreach (int[] indices in new int[][] { [0], [0, 1], [0, 1, 2], [0, 1, 2, 4] })
        {
            Assert.True(source.TrySimplex(indices, out var simplex));
            Assert.Equal(indices.Length - 1, simplex.Rank);
            for (int i = 0; i < indices.Length; i++)
            {
                Assert.Equal(indices[i], simplex.OriginalVertex(i));
                Assert.Equal(source.Vertex(indices[i]), simplex.Vertex(i));
            }
            var work = new ConvexQueryWork();
            Assert.True(work.Primitive());
            Assert.True(ConvexFeatureKernel.TryDistanceCertificate(Request(), simplex, ConvexCertificateFraction.From(0), ref work, new(), out var pair));
            Assert.True(pair.BaseSquaredDistance.Sign > 0);
            Assert.False(ConvexDistanceEncoding.TryCreate(pair, out _));
        }
        Assert.False(source.TrySimplex([0, 0], out _));
        Assert.False(source.TrySimplex([0, 1, 2, 3, 4], out _));
        var untrusted = ConvexPlanarSource.FromRepresented(fixture.Leaf);
        var account = new ConvexQueryWork();
        Assert.True(account.Primitive());
        Assert.False(ConvexFeatureKernel.TryDistanceCertificate(Request(), untrusted, ConvexCertificateFraction.From(0), ref account, new(), out _));
    }

    [Fact]
    public void CircularRefusalAndReplacementRestoreTheArenaAndInvalidateOldTimes()
    {
        using var fixture = new ConvexPlanarFixture(new(0.25f, 0.0001f, 0.0001f),
            new(new(0.5f, 1, 0.2f), new Quaternion(0, 0, -0.5f, 0.8660254f)));
        Assert.True(ConvexPlanarSource.TryCreate(fixture.Leaf, out var source));
        var request = Request() with { Displacement = Vector3.Zero };
        var refused = new ConvexQueryScratch(maximumCoefficients: 0);
        var limited = new ConvexQueryWork();
        Assert.True(limited.Primitive());
        Assert.False(ConvexFeatureKernel.TryDistanceCertificate(request, source, ConvexCertificateFraction.From(0), ref limited, refused, out _));
        Assert.Equal(0, refused.Polynomials.Used);
        Assert.True(limited.Exhausted);
        var scratch = new ConvexQueryScratch();
        var work = new ConvexQueryWork();
        Assert.True(work.Primitive());
        Assert.True(ConvexFeatureKernel.TryDistanceCertificate(request, source, ConvexCertificateFraction.From(0), ref work, scratch, out var pair));
        Assert.True(pair.TryBarycentricFraction(0, 1, ref work, scratch, out var time));
        Assert.True(source.TrySimplex([0], out var point));
        Assert.True(ConvexFeatureKernel.TryDistanceCertificate(request, point, ConvexCertificateFraction.From(0), ref work, scratch, out _));
        Assert.Equal(0, scratch.Polynomials.Used);
        Assert.False(time.TryCompare(time, ref work, scratch, out _));
        Assert.False(pair.TryCompareSquaredMargin(0, ref work, scratch, out _));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.001f)]
    public void UniformForwardErrorCoversTranslatedWorldCoordinates(float margin)
    {
        using var fixture = new ConvexPlanarFixture(new(0.125f), Pose.At(new(100.502f, 0.625f, 0)));
        var request = Request(margin) with { Pose = Pose.At(new(100, 0, 0)) };
        var work = new ConvexQueryWork();
        Assert.True(work.Primitive());
        Assert.True(ConvexIntervalCertificate.TryCreate(request, fixture.Leaf, Vector3.UnitX, 0, 1, ref work, new(), out var plane));
        // At the last fraction, the exact represented gap before margin is over 0.00199 m.
        // Forward operation error must be bounded over the path, without losing millimetre evidence.
        Assert.True(plane.ToGap.Lower > 0);
        Assert.InRange(plane.UniformError, 0, 0.001f);
        output.WriteLine($"world margin={margin:R} error={plane.UniformError:R} endpoint=[{plane.ToGap.Lower:R},{plane.ToGap.Upper:R}]");
    }

}
