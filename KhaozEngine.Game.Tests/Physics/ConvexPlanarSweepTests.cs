using System;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Physics;

public sealed class ConvexPlanarSweepTests(ITestOutputHelper output)
{
    static readonly ConvexQueryShape Shape = new(ConvexQueryKind.ConeCylinder, 0.25f, 0.25f, 0.5f, 0.75f);

    ConvexLeafEvidence Evaluate(ConvexPlanarFixture fixture, Vector3 delta, Pose pose = default,
        ConvexQueryShape? shape = null)
    {
        if (pose == default) pose = Pose.Identity;
        var request = new ConvexQueryRequest(shape ?? Shape, pose, delta, ConvexQueryOperation.Sweep);
        var work = new ConvexQueryWork();
        Assert.True(ConvexFeatureKernel.TryEvaluate(request, fixture.Leaf, ref work, new(), out var evidence),
            $"cells={work.Cells} features={work.Features} exhausted={work.Exhausted}");
        Assert.True(evidence.IsComplete);
        if (delta != Vector3.Zero)
        {
            var oracle = ShortBodyShellOracle.Entry(shape ?? Shape, pose, fixture.Oracle, delta);
            Assert.True(oracle.IsComplete, "independent sweep oracle");
            Assert.Equal(evidence.SweepStatus, oracle.Status);
            if (oracle.Status == ConvexSweepStatus.Hit)
                Assert.True(evidence.LowerDistance <= oracle.Lower && evidence.UpperDistance >= oracle.Upper,
                    $"[{evidence.LowerDistance:R},{evidence.UpperDistance:R}] oracle [{oracle.Lower:R},{oracle.Upper:R}]");
        }
        output.WriteLine($"{evidence.QueryFeature}/{evidence.TargetFeature}: cells={work.Cells}, features={work.Features}");
        return evidence;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OriginalPathFindsFiniteFaceBeforeTheEndpoint(bool hull)
    {
        using var fixture = new ConvexPlanarFixture(new(0.03125f, 1, 1), Pose.At(new(0.78125f, 0.625f, 0)), hull);
        var evidence = Evaluate(fixture, Vector3.UnitX);
        Assert.True(evidence.HasAttainedWitness && evidence.HasNormalConeCertificate);
        Assert.True(evidence.LowerDistance <= 0.5 && evidence.UpperDistance >= 0.5);
        Assert.InRange((double)evidence.UpperDistance - evidence.LowerDistance, 0, 0.001f);
        Assert.Equal(-Vector3.UnitX, evidence.Normal);
        Assert.Equal(0.75f, evidence.TargetWitness.X);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClosedDoubleRootIsContact(bool hull)
    {
        using var fixture = new ConvexPlanarFixture(new(0.03125f, 0.0625f, 0.03125f),
            Pose.At(new(0.28125f, 0.625f, 0.03125f)), hull);
        var evidence = Evaluate(fixture, Vector3.UnitZ, Pose.At(new(0, 0, -0.5f)));
        Assert.True(evidence.HasAttainedWitness && evidence.HasNormalConeCertificate);
        Assert.True(evidence.LowerDistance <= 0.5 && evidence.UpperDistance >= 0.5);
        Assert.Equal(-Vector3.UnitX, evidence.Normal);
    }

    [Fact]
    public void BothAdjacentFloatsDistinguishTangentHitAndWholePathClear()
    {
        foreach (float x in new[] { MathF.BitDecrement(0.25f), MathF.BitIncrement(0.25f) })
        {
            // Keep the near face exact. A symmetric centre/half-width sum can erase this ulp.
            Vector3[] points = new Vector3[8];
            for (int i = 0; i < 8; i++) points[i] = new((i & 1) == 0 ? x : 0.3125f,
                (i & 2) == 0 ? 0.5625f : 0.6875f, (i & 4) == 0 ? 0 : 0.0625f);
            using var fixture = new ConvexPlanarFixture(default, Pose.Identity, hull: true, vertices: points);
            var evidence = Evaluate(fixture, Vector3.UnitZ, Pose.At(new(0, 0, -0.5f)));
            if (x < 0.25f)
            {
                Assert.True(evidence.HasAttainedWitness);
                Assert.True(evidence.UpperDistance < 0.5f);
            }
            else Assert.False(evidence.HasAttainedWitness);
        }
    }

    [Fact]
    public void ZeroPathAndInitialBoundaryNeverBecomeFalseClear()
    {
        using var touching = new ConvexPlanarFixture(new(0.125f), Pose.At(new(0.375f, 0.625f, 0)));
        foreach (Vector3 delta in new[] { Vector3.Zero, Vector3.UnitX, -Vector3.UnitX })
        {
            var evidence = Evaluate(touching, delta);
            Assert.True(evidence.HasAttainedWitness);
            Assert.Equal(0, evidence.UpperDistance);
        }
        using var separated = new ConvexPlanarFixture(new(0.125f), Pose.At(new(1, 0.625f, 0)));
        Assert.False(Evaluate(separated, Vector3.Zero).HasAttainedWitness);
        using var overlap = new ConvexPlanarFixture(new(0.125f), Pose.At(new(0.2f, 0.625f, 0)));
        var request = new ConvexQueryRequest(Shape, Pose.Identity, Vector3.UnitX, ConvexQueryOperation.Sweep);
        var work = new ConvexQueryWork();
        Assert.False(ConvexFeatureKernel.TryEvaluate(request, overlap.Leaf, ref work, new(), out var refused));
        Assert.Equal(default, refused);
    }

    [Fact]
    public void ThinBlockerBetweenProposalVerticesCannotBeMissed()
    {
        using var fixture = new ConvexPlanarFixture(new(0.00001f, 0.03125f, 0.00001f),
            Pose.At(new(0.14995f, 0.625f, 0.19995f)));
        var evidence = Evaluate(fixture, new(0.5f, 0, 0), Pose.At(new(-0.5f, 0, 0)));
        Assert.True(evidence.HasAttainedWitness);
        Assert.True(evidence.UpperDistance < 0.5f);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void InteriorEdgeContactsDoNotRequireAnEndpointInTheShell(int row)
    {
        Vector3 half = row == 0 ? new(0.03125f, 2, 0.03125f) : new(0.03125f, 0.03125f, 1);
        Vector3 center = row == 0 ? new(0.5f, 0.625f, 0.25f) : row == 1 ? new(0.28125f, 0.34375f, 0) : new(0.1875f, 1, 0);
        using var fixture = new ConvexPlanarFixture(half, Pose.At(center));
        var evidence = Evaluate(fixture, row == 0 ? Vector3.UnitX : row == 1 ? new(0.25f, 0, 0) : Vector3.UnitY);
        Assert.Equal(ConvexTargetFeature.Edge, evidence.TargetFeature);
        Assert.Equal(row == 0 ? ConvexShellFeature.Cylinder : row == 1 ? ConvexShellFeature.LowerCone : ConvexShellFeature.HeadDisc, evidence.QueryFeature);
        if (row != 0)
        {
            double expected = row == 1 ? 0.125 : 0.21875;
            Assert.True(evidence.LowerDistance <= expected && evidence.UpperDistance >= expected);
        }
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.001f)]
    public void SixExposedFaceRegimesUseTheRequestedMargin(float margin)
    {
        var shape = new ConvexQueryShape(Shape.Kind, Shape.Radius, Shape.LowerY, Shape.RingY, Shape.UpperY, margin);
        var rows = new (Vector3 Normal, Vector3 Point, ConvexShellFeature Feature)[]
        {
            (new(0, -1, 0), new(0, 0.25f, 0), ConvexShellFeature.Apex),
            (new(1, -1, 0), new(0.125f, 0.375f, 0), ConvexShellFeature.LowerCone),
            (new(2, -1, 0), new(0.25f, 0.5f, 0), ConvexShellFeature.Shoulder),
            (new(1, 0, 0), new(0.25f, 0.625f, 0), ConvexShellFeature.Cylinder),
            (new(0, 1, 0), new(0, 0.75f, 0), ConvexShellFeature.HeadDisc),
            (new(1, 1, 0), new(0.25f, 0.75f, 0), ConvexShellFeature.Rim)
        };
        foreach (var row in rows)
        {
            Vector3 center = row.Point + row.Normal * 0.25f;
            Vector3 across = new Vector3(-row.Normal.Y, row.Normal.X, 0) * 0.5f;
            Vector3[] vertices = new Vector3[8];
            for (int i = 0; i < 8; i++) vertices[i] = center + ((i & 1) == 0 ? -across : across) +
                new Vector3(0, 0, (i & 2) == 0 ? -1 : 1) + ((i & 4) == 0 ? Vector3.Zero : row.Normal * 0.125f);
            using var fixture = new ConvexPlanarFixture(default, Pose.Identity, hull: true, vertices: vertices);
            var evidence = Evaluate(fixture, row.Normal * 0.5f, shape: shape);
            Assert.Equal(row.Feature, evidence.QueryFeature);
            Assert.Equal(ConvexTargetFeature.Face, evidence.TargetFeature);
            if (margin == 0) Assert.True(evidence.LowerDistance <= row.Normal.Length() * 0.25 && evidence.UpperDistance >= row.Normal.Length() * 0.25);
            else Assert.True(evidence.UpperDistance < row.Normal.Length() * 0.25);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DuckAndCowUseTheirActualFullWidthAndHeadHeight(bool hull)
    {
        foreach (var body in ConvexQueryFixture.Bodies())
        {
            foreach (float margin in new[] { 0f, 0.001f })
            {
                var shape = new ConvexQueryShape(body.Kind, body.Radius, body.LowerY, body.RingY, body.UpperY, margin);
                using var wall = new ConvexPlanarFixture(new(0.03125f, 2, 2), Pose.At(new(body.Radius + 0.53125f, body.RingY, 0)), hull);
                Assert.Equal(ConvexShellFeature.Cylinder, Evaluate(wall, Vector3.UnitX, shape: shape).QueryFeature);
                using var roof = new ConvexPlanarFixture(new(2, 0.03125f, 2), Pose.At(new(0, body.UpperY + 0.53125f, 0)), hull);
                Assert.Equal(ConvexShellFeature.HeadDisc, Evaluate(roof, Vector3.UnitY, shape: shape).QueryFeature);
                using var floor = new ConvexPlanarFixture(new(2, 0.03125f, 2), Pose.At(new(0, body.LowerY - 0.53125f, 0)), hull);
                Assert.Equal(ConvexShellFeature.Apex, Evaluate(floor, -Vector3.UnitY, shape: shape).QueryFeature);
            }
        }
    }

    [Fact]
    public void WorkAndUnfinishedOperationsRefuseAtomically()
    {
        using var fixture = new ConvexPlanarFixture(new(0.125f), Pose.At(new(0.75f, 0.625f, 0)));
        var request = new ConvexQueryRequest(Shape, Pose.Identity, Vector3.UnitX, ConvexQueryOperation.Sweep);
        var work = new ConvexQueryWork(maximumCells: 0);
        var scratch = new ConvexQueryScratch();
        Assert.False(ConvexFeatureKernel.TryEvaluate(request, fixture.Leaf, ref work, scratch, out var evidence));
        Assert.True(work.Exhausted);
        Assert.Equal(default, evidence);
        Assert.Equal(0, scratch.Polynomials.Used);
        Assert.False(ConvexFeatureKernel.TryEvaluate(request, fixture.Leaf, ref work, scratch, out evidence));
        Assert.Equal(default, evidence);
        foreach (var operation in new[] { ConvexQueryOperation.Contacts, ConvexQueryOperation.Penetration })
        {
            work = new();
            Assert.False(ConvexFeatureKernel.TryEvaluate(request with { Operation = operation }, fixture.Leaf, ref work, scratch, out evidence));
            Assert.Equal(default, evidence);
        }
    }

    [Fact]
    public void LoweredRootStorageRestoresScratchAndRefusalIsSticky()
    {
        using var fixture = new ConvexPlanarFixture(new(0.125f), Pose.At(new(0.75f, 0.625f, 0)));
        var request = new ConvexQueryRequest(Shape, Pose.Identity, Vector3.UnitX, ConvexQueryOperation.Sweep);
        var scratch = new ConvexQueryScratch(maximumCoefficients: 0);
        var work = new ConvexQueryWork();
        Assert.False(ConvexFeatureKernel.TryEvaluate(request, fixture.Leaf, ref work, scratch, out var evidence));
        Assert.Equal(default, evidence);
        Assert.True(work.Exhausted);
        Assert.Equal(0, scratch.Polynomials.Used);
        Assert.False(ConvexFeatureKernel.TryEvaluate(request with { Displacement = -Vector3.UnitX }, fixture.Leaf, ref work, scratch, out evidence));
        Assert.Equal(default, evidence);
        work = new();
        Assert.True(ConvexFeatureKernel.TryEvaluate(request with { Displacement = -Vector3.UnitX }, fixture.Leaf, ref work, scratch, out evidence));
        Assert.Equal(ConvexSweepStatus.Clear, evidence.SweepStatus);
        Assert.Equal(0, scratch.Polynomials.Used);
    }

    [Fact]
    public void PendingMarginConeEdgeHasNoUsablePartialEvidence()
    {
        using var fixture = new ConvexPlanarFixture(new(0.03125f, 0.03125f, 1), Pose.At(new(0.28125f, 0.34375f, 0)));
        var grown = new ConvexQueryShape(Shape.Kind, Shape.Radius, Shape.LowerY, Shape.RingY, Shape.UpperY, 0.001f);
        var delta = new Vector3(0.25f, 0, 0);
        var oracle = ShortBodyShellOracle.Entry(grown, Pose.Identity, fixture.Oracle, delta);
        Assert.True(oracle.IsComplete);
        Assert.Equal(ConvexSweepStatus.Hit, oracle.Status);
        var request = new ConvexQueryRequest(grown, Pose.Identity, delta, ConvexQueryOperation.Sweep);
        var work = new ConvexQueryWork();
        Assert.False(ConvexFeatureKernel.TryEvaluate(request, fixture.Leaf, ref work, new(), out var evidence));
        Assert.Equal(default, evidence);
    }
}
