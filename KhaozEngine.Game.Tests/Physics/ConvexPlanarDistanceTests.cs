using System;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Physics;

public sealed class ConvexPlanarDistanceTests(ITestOutputHelper output)
{
    static readonly ConvexQueryShape Dyadic = new(ConvexQueryKind.ConeCylinder, 0.25f, 0.25f, 0.5f, 0.75f);

    ConvexLeafEvidence Check(ConvexQueryShape shape, ConvexPlanarFixture fixture)
    {
        var request = new ConvexQueryRequest(shape, Pose.Identity, Vector3.Zero, ConvexQueryOperation.Distance);
        var work = new ConvexQueryWork();
        var scratch = new ConvexQueryScratch();
        var oracle = ShortBodyShellOracle.Entry(shape, Pose.Identity, fixture.Oracle, Vector3.Zero);
        Assert.True(oracle.IsComplete);
        Assert.True(ConvexFeatureKernel.TryEvaluate(request, fixture.Leaf, ref work, scratch, out var evidence));
        Assert.True(evidence.IsComplete && evidence.HasAttainedWitness && evidence.HasNormalConeCertificate);
        Assert.True(evidence.LowerDistance <= oracle.Lower && evidence.UpperDistance >= oracle.Upper,
            $"Certificate [{evidence.LowerDistance:R}, {evidence.UpperDistance:R}], oracle [{oracle.Lower:R}, {oracle.Upper:R}]");
        Assert.InRange((double)evidence.UpperDistance - evidence.LowerDistance, 0, 0.001f);
        Assert.True(evidence.LocalFeatureId >= 0);
        output.WriteLine($"{evidence.QueryFeature} / {evidence.TargetFeature}: cells={work.CellHighWater} features={work.FeatureHighWater}");
        return evidence;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConeSideHeadDiscAndRimsHaveAttainedDistanceWitnesses(bool hull)
    {
        foreach (var shape in ConvexQueryFixture.Bodies())
        {
            float middle = 0.5f * (shape.RingY + shape.UpperY);
            foreach (var (half, point, feature) in new[]
            {
                (new Vector3(2, 0.03125f, 2), new Vector3(0, 0.125f, 0), ConvexShellFeature.Apex),
                (new Vector3(0.00390625f), new Vector3(shape.Radius * 0.5f + (shape.RingY - shape.LowerY) * 0.25f, shape.LowerY + (shape.RingY - shape.LowerY) * 0.5f - shape.Radius * 0.25f, 0), ConvexShellFeature.LowerCone),
                (new Vector3(0.03125f), new Vector3(shape.Radius + 0.25f, shape.RingY - 0.0625f, 0), ConvexShellFeature.Shoulder),
                (new Vector3(0.03125f, 2, 2), new Vector3(shape.Radius + 0.25f, middle, 0), ConvexShellFeature.Cylinder),
                (new Vector3(2, 0.03125f, 2), new Vector3(0, shape.UpperY + 0.25f, 0), ConvexShellFeature.HeadDisc),
                (new Vector3(0.03125f), new Vector3(shape.Radius + 0.25f, shape.UpperY + 0.25f, 0), ConvexShellFeature.Rim)
            })
            {
                using var fixture = new ConvexPlanarFixture(half, Pose.At(point), hull);
                Assert.Equal(feature, Check(shape, fixture).QueryFeature);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OffAxisVerticesHaveAttainedContinuousCircleWitnesses(bool hull)
    {
        foreach (var shape in ConvexQueryFixture.Bodies())
        {
            float h = shape.RingY - shape.LowerY;
            foreach (var center in new[]
            {
                new Vector3(0.01f, 0.125f, 0.01f),
                new Vector3(0.6f * (shape.Radius * 0.5f + h * 0.25f), shape.LowerY + h * 0.5f - shape.Radius * 0.25f,
                    0.8f * (shape.Radius * 0.5f + h * 0.25f)),
                new Vector3(shape.Radius + 0.25f, shape.RingY - 0.0625f, 0.25f),
                new Vector3(shape.Radius + 0.25f, (shape.RingY + shape.UpperY) * 0.5f, 0.25f),
                new Vector3(0.0625f, shape.UpperY + 0.25f, 0.0625f),
                new Vector3(shape.Radius + 0.25f, shape.UpperY + 0.25f, 0.25f)
            })
            {
                using var fixture = new ConvexPlanarFixture(new(0.00390625f), Pose.At(center), hull);
                Check(shape, fixture);
            }
        }
    }

    [Fact]
    public void MarginIsTheExactExistingMinkowskiSkin()
    {
        var grown = new ConvexQueryShape(Dyadic.Kind, Dyadic.Radius, Dyadic.LowerY, Dyadic.RingY, Dyadic.UpperY, 0.001f);
        using var fixture = new ConvexPlanarFixture(new(0.125f), Pose.At(new(0.5f, 0.625f, 0)));
        var a = Check(Dyadic, fixture);
        var b = Check(grown, fixture);
        Assert.True(b.UpperDistance < a.LowerDistance);
        Assert.True(b.QueryWitness.X > a.QueryWitness.X);
        using var touching = new ConvexPlanarFixture(new(0.125f), Pose.At(new(0.3755f, 0.625f, 0)));
        Assert.Equal(0, Check(grown, touching).UpperDistance);
    }

    [Fact]
    public void UncertainAndInvalidInputsHaveNoUsableEvidence()
    {
        using var fixture = new ConvexPlanarFixture(new(0.125f), Pose.At(new(1, 0.625f, 0)));
        var request = new ConvexQueryRequest(Dyadic, Pose.Identity, Vector3.Zero, ConvexQueryOperation.Distance);
        var uncertain = new InstalledSweepLeaf(fixture.Leaf.Geometry, fixture.Leaf.Pose, default);
        var work = new ConvexQueryWork();
        Assert.False(ConvexFeatureKernel.TryEvaluate(request, uncertain, ref work, new(), out var evidence));
        Assert.Equal(default, evidence);
        Assert.False(evidence.IsComplete);
        request = request with { Pose = new(Vector3.Zero, new Quaternion(0, 0, 0.5f, 0.8660254f)) };
        Assert.False(ConvexFeatureKernel.TryEvaluate(request, fixture.Leaf, ref work, new(), out evidence));
        Assert.Equal(default, evidence);
    }

    [Fact]
    public void CenteredHullWrapperUsesItsInstalledLeafPose()
    {
        using var fixture = new ConvexPlanarFixture(new(0.125f), Pose.At(new(0.5f, 0.625f, 0.25f)),
            hull: true, vertexOffset: new(0.125f, 0.25f, 0.0625f));
        Assert.NotEqual(Vector3.Zero, fixture.Leaf.Geometry.LocalPose.Position);
        Check(Dyadic, fixture);
    }

    [Fact]
    public void ObliqueCircularEdgeRefusesWithoutApproximatingTheQuartic()
    {
        using var fixture = new ConvexPlanarFixture(new(0.25f, 0.0001f, 0.0001f),
            new(new(0.5f, 1, 0.2f), new Quaternion(0, 0, -0.5f, 0.8660254f)));
        Assert.True(InstalledPoseOperator.TryCreateSupport(fixture.Leaf.Pose, out _));
        var request = new ConvexQueryRequest(Dyadic, Pose.Identity, Vector3.Zero, ConvexQueryOperation.Distance);
        var work = new ConvexQueryWork();
        Assert.False(ConvexFeatureKernel.TryEvaluate(request, fixture.Leaf, ref work, new(), out var evidence));
        Assert.Equal(default, evidence);
        Assert.False(evidence.IsComplete);
        // The independent oracle also refuses this non-Cartesian snapshot. Its AABB is not truth.
        Assert.Equal(default, ShortBodyShellOracle.Entry(Dyadic, Pose.Identity, fixture.Oracle, Vector3.Zero));
    }

    [Fact]
    public void OracleDoesNotClipDistanceAtAnInitializationBound()
    {
        using var fixture = new ConvexPlanarFixture(new(0.125f), Pose.Identity);
        var oracle = ShortBodyShellOracle.Entry(Dyadic, Pose.At(new(1_000_000)), fixture.Oracle, Vector3.Zero);
        Assert.True(oracle.IsComplete);
        Assert.True(oracle.Lower > 1_048_576);
        Assert.True(oracle.Upper >= oracle.Lower);
    }

    [Fact]
    public void CircleSupportIsNeverReplacedByInscribedVertices()
    {
        // A narrow blocker lies inside the true circle but beyond a missing polygon sector.
        // The chord endpoints rotate the 3:4:5 radius by cos=99/101, sin=20/101.
        using var fixture = new ConvexPlanarFixture(new(0.00001f, 0.03125f, 0.00001f),
            Pose.At(new(0.14995f, 0.625f, 0.19995f)));
        var chordNormal = new InstalledPoseOracle.V(new(3, 5), new(0, 1), new(4, 5));
        foreach (var vertex in fixture.Oracle.Vertices)
            Assert.True(InstalledPoseOracle.V.Dot(vertex, chordNormal) > new InstalledPoseOracle.R(99, 404));
        var evidence = Check(Dyadic, fixture);
        Assert.Equal(0, evidence.UpperDistance);
    }

    [Fact]
    public void SharpShoulderNormalBelongsToTheNormalCone()
    {
        using var fixture = new ConvexPlanarFixture(new(0.03125f), Pose.At(new(0.5f, 0.4375f, 0)));
        var evidence = Check(Dyadic, fixture);
        Assert.Equal(ConvexShellFeature.Shoulder, evidence.QueryFeature);
        Assert.True(ShortBodyShellOracle.NormalConeContains(Dyadic, evidence.QueryWitness, -evidence.Normal));
        Assert.False(ShortBodyShellOracle.NormalConeContains(Dyadic, new(0.25f, 0.5f, 0), new(1, 0.25f, 0)));
        Assert.False(ShortBodyShellOracle.NormalConeContains(Dyadic, new(0.25f, 0.5f, 0), new(1, -2, 0)));
    }

    [Fact]
    public void DyadicTangencyAndAdjacentFloatsDoNotBecomeClearProxyMisses()
    {
        foreach (float x in new[] { MathF.BitDecrement(0.5f), 0.5f, MathF.BitIncrement(0.5f) })
        {
            using var fixture = new ConvexPlanarFixture(new(0.25f, 0.0625f, 0.0625f), Pose.At(new(x, 0.625f, 0)));
            var evidence = Check(Dyadic, fixture);
            if (x <= 0.5f) Assert.Equal(0, evidence.UpperDistance);
            else Assert.True(evidence.LowerDistance > 0);
        }
    }

    [Fact]
    public void LaterOperationsAndExhaustedDistanceRefuseAtomically()
    {
        using var fixture = new ConvexPlanarFixture(new(0.125f), Pose.At(new(1, 0.625f, 0)));
        foreach (var operation in new[] { ConvexQueryOperation.Sweep, ConvexQueryOperation.Contacts, ConvexQueryOperation.Penetration })
        {
            var request = new ConvexQueryRequest(Dyadic, Pose.Identity, Vector3.Zero, operation);
            var work = new ConvexQueryWork();
            Assert.False(ConvexFeatureKernel.TryEvaluate(request, fixture.Leaf, ref work, new(), out var evidence));
            Assert.Equal(default, evidence);
            Assert.False(evidence.IsComplete);
        }
        foreach (var workLimit in new[] { new ConvexQueryWork(maximumCells: 0), new ConvexQueryWork(maximumFeatures: 0), new ConvexQueryWork(maximumDescendants: 0) })
        {
            var work = workLimit;
            var request = new ConvexQueryRequest(Dyadic, Pose.Identity, Vector3.Zero, ConvexQueryOperation.Distance);
            Assert.False(ConvexFeatureKernel.TryEvaluate(request, fixture.Leaf, ref work, new(), out var evidence));
            Assert.Equal(default, evidence);
            Assert.True(work.Exhausted);
        }
    }

    [Fact]
    public void CoincidentDispatchedLeavesRetainIdentityAndRejectForeignEvidence()
    {
        using var a = new ConvexPlanarFixture(new(0.125f), Pose.At(new(1, 0.625f, 0)));
        using var b = new ConvexPlanarFixture(new(0.125f), Pose.At(new(1, 0.625f, 0)));
        var first = new ConvexPlanarDispatch(a.Leaf, new(false, 17, 2, 3));
        var second = new ConvexPlanarDispatch(b.Leaf, new(false, 17, 4, 3));
        var sameGeometry = new ConvexPlanarDispatch(a.Leaf, new(true, 19, 2, 3));
        var request = new ConvexQueryRequest(Dyadic, Pose.Identity, Vector3.Zero, ConvexQueryOperation.Distance);
        var work = new ConvexQueryWork();
        Assert.True(first.TryEvaluate(request, ref work, new(), out var evidence));
        Assert.True(first.TryPublish(evidence, out var result));
        Assert.Equal(new(false, 17, 2, 3), result.Identity);
        Assert.False(second.TryPublish(evidence, out var refused));
        Assert.Equal(default, refused);
        Assert.False(sameGeometry.TryPublish(evidence, out refused));
        Assert.Equal(default, refused);
        Assert.True(second.TryEvaluate(request, ref work, new(), out var other));
        Assert.True(second.TryPublish(other, out var otherResult));
        Assert.Equal(result.QueryWitness, otherResult.QueryWitness);
        Assert.NotEqual(result.Identity, otherResult.Identity);
    }
}
