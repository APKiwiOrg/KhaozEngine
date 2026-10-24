using System;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;
using Xunit.Abstractions;
using P = KhaozEngine.Physics.Bepu.ConvexExactPoint;
using Q = KhaozEngine.Physics.Bepu.ConvexQuadratic;

namespace KhaozEngine.Tests.Physics;

public sealed class ConvexInitialStateTests(ITestOutputHelper output)
{
    static readonly ConvexQueryShape Shape = new(ConvexQueryKind.ConeCylinder, 0.25f, 0.25f, 0.5f, 0.75f);
    static ConvexQueryRequest Request(float margin = 0) => new(Shape.WithMargin(margin), Pose.Identity, Vector3.Zero, ConvexQueryOperation.Sweep);

    static (ConvexShellFeature Feature, Vector3 Point, Vector3 Outward)[] Rows() =>
    [
        (ConvexShellFeature.Apex, new(0, 0.25f, 0), -Vector3.UnitY),
        (ConvexShellFeature.LowerCone, new(0.125f, 0.375f, 0), new(1, -1, 0)),
        (ConvexShellFeature.Shoulder, new(0.25f, 0.5f, 0), new(2, -1, 0)),
        (ConvexShellFeature.Cylinder, new(0.25f, 0.625f, 0), Vector3.UnitX),
        (ConvexShellFeature.HeadDisc, new(0, 0.75f, 0), Vector3.UnitY),
        (ConvexShellFeature.Rim, new(0.25f, 0.75f, 0), new(1, 1, 0))
    ];

    // All coordinates are dyadic. The near stratum contains p and is supported by n.
    static ConvexPlanarFixture TouchFixture(Vector3 p, Vector3 n, ConvexTargetFeature stratum)
    {
        Vector3 u = new Vector3(n.Y, -n.X, 0) / 64, d = n / 64;
        Vector3 v = stratum == ConvexTargetFeature.Edge && n == Vector3.UnitY ? Vector3.UnitZ : Vector3.UnitZ / 64;
        Vector3[] vertices = stratum switch
        {
            ConvexTargetFeature.Face => [p - u - v, p + u - v, p - u + v, p + u + v,
                p - u - v + d, p + u - v + d, p - u + v + d, p + u + v + d],
            ConvexTargetFeature.Edge => [p - v, p + v, p + d - u - v, p + d + u - v, p + d - u + v, p + d + u + v],
            _ => [p, p + d + u, p + d - u + v, p + d - u - v]
        };
        return new(default, Pose.Identity, hull: true, vertices: vertices);
    }

    static ConvexInitialEvidence Classify(ConvexQueryRequest request, ConvexPlanarFixture fixture,
        out ConvexQueryWork work, out ConvexQueryScratch scratch, double fraction = 0)
    {
        work = new(); scratch = new();
        Assert.True(work.Primitive());
        Assert.True(ConvexPlanarContacts.TryClassify(request, fixture.Leaf, ConvexCertificateFraction.From(fraction),
            ref work, scratch, out var evidence), $"leaf={fixture.Leaf.Pose} cells={work.Cells} features={work.Features} exhausted={work.Exhausted}");
        Assert.Equal(1, work.Descendants);
        Assert.True(evidence.IsAlive);
        return evidence;
    }

    static Vector3 Encode(P p) => new((float)p.X.Bounds.Lower, (float)p.Y.Bounds.Lower, (float)p.Z.Bounds.Lower);
    static BigInteger[] Terms(P p)
    {
        var result = new BigInteger[18];
        int i = 0;
        foreach (Q q in new[] { p.X, p.Y, p.Z })
        {
            result[i++] = q.A.Numerator; result[i++] = q.A.Denominator;
            result[i++] = q.B.Numerator; result[i++] = q.B.Denominator;
            result[i++] = q.D.Numerator; result[i++] = q.D.Denominator;
        }
        return result;
    }

    static void CheckTouch(ConvexInitialEvidence e, ConvexQueryRequest request, ConvexPlanarFixture fixture)
    {
        Assert.Equal(ConvexInitialState.Touching, e.State);
        Assert.True(e.HasCommonPoint);
        Assert.True(e.Contact.IsComplete && e.Contact.HasNormalConeCertificate);
        Assert.True(e.DirectionSquared.Sign > 0);
        // The mathematical normal is Direction/sqrt(DirectionSquared), not the rounded tuple.
        Assert.Equal(0, (P.Dot(e.Direction, e.Direction) - e.DirectionSquared).Sign);
        var n = e.UnitNormalBounds;
        Assert.True(n.IsResolved);
        var norm = n.X.Square().Add(n.Y.Square()).Add(n.Z.Square());
        Assert.True(norm.Lower <= 1 && norm.Upper >= 1);
        Assert.True(float.IsFinite(e.Contact.Normal.X) && float.IsFinite(e.Contact.Normal.Y) && float.IsFinite(e.Contact.Normal.Z));
        Assert.NotEqual(Vector3.Zero, e.Contact.Normal);
        Assert.True(Vector3.Dot(e.Contact.Normal, Encode(e.Direction)) < 0);
        Assert.Equal(0, e.Contact.LowerDistance);
        Assert.Equal(0, e.Contact.UpperDistance);
        if (request.Shape.Margin == 0)
        {
            Assert.True(ShortBodyShellOracle.InitialConesContain(Shape, request.Pose, fixture.Oracle,
                Terms(e.CommonPoint), Terms(e.Direction)));
        }
    }

    [Fact]
    public void InitialTouchKeepsEveryIncidentClosedCone()
    {
        foreach (var row in Rows())
            foreach (var stratum in new[] { ConvexTargetFeature.Face, ConvexTargetFeature.Edge, ConvexTargetFeature.Vertex })
            {
                using var fixture = TouchFixture(row.Point, row.Outward, stratum);
                Assert.True(ShortBodyShellOracle.NormalConeContains(Shape, row.Point, row.Outward));
                Assert.True(ShortBodyShellOracle.TargetSupports(fixture.Oracle, row.Point, row.Outward));
                var e = Classify(Request(), fixture, out var work, out _);
                CheckTouch(e, Request(), fixture);
                Assert.Equal(row.Feature, e.Contact.QueryFeature);
                Assert.Equal(stratum, e.Contact.TargetFeature);
                output.WriteLine($"touch {row.Feature}/{stratum} cells={work.Cells} features={work.Features}");
            }

        foreach (var (p, vertices, normals) in new[]
        {
            (new Vector3(0.25f, 0.5f, 0), new[] { new Vector3(0, 0, 0), new Vector3(0.125f, 0, 0), new Vector3(0, -0.125f, 0), new Vector3(0.125f, -0.125f, 0.125f) },
                new[] { Vector3.UnitX, new Vector3(1, -1, 0) }),
            (new Vector3(0.25f, 0.75f, 0), new[] { Vector3.Zero, new Vector3(0.125f, 0, 0), new Vector3(0, 0.125f, 0), new Vector3(0.125f, 0.125f, 0.125f) },
                new[] { Vector3.UnitX, Vector3.UnitY })
        })
        {
            using var fixture = new ConvexPlanarFixture(default, Pose.At(p), hull: true, vertices: vertices);
            var e = Classify(Request(), fixture, out var work, out var scratch);
            foreach (Vector3 normal in normals)
            {
                Assert.True(ShortBodyShellOracle.NormalConeContains(Shape, p, normal));
                Assert.True(ShortBodyShellOracle.TargetSupports(fixture.Oracle, p, normal));
                Assert.True(ConvexPlanarContacts.TryTouchingConstraint(e, P.From(normal), ref work, scratch, out var contact));
                Assert.True(Vector3.Dot(contact.Normal, normal) < 0);
            }
            Assert.False(ConvexPlanarContacts.TryTouchingConstraint(e, P.From(-Vector3.UnitX), ref work, scratch, out var refused));
            Assert.Equal(default, refused);
            Assert.False(ConvexPlanarContacts.TryTouchingConstraint(e, default, ref work, scratch, out refused));
            Assert.Equal(default, refused);
        }
    }

    [Fact]
    public void ZeroDistanceDoesNotInventAnOverlapNormal()
    {
        using var fixture = new ConvexPlanarFixture(new(0.0625f), Pose.At(new(0, 0.625f, 0)));
        var e = Classify(Request(), fixture, out var work, out var scratch);
        Assert.Equal(ConvexInitialState.Overlapping, e.State);
        Assert.Equal(0, e.Pair.BaseSquaredDistance.Sign);
        Assert.Equal(default, e.Contact);
        Assert.Equal(default, e.Direction);
        Assert.False(ConvexPlanarContacts.TryTouchingConstraint(e, P.From(Vector3.UnitX), ref work, scratch, out var refused));
        Assert.Equal(default, refused);
    }

    [Fact]
    public void StrictRationalCommonPointProvesOverlap()
    {
        foreach (var (half, pose) in new[]
        {
            (new Vector3(0.0625f), Pose.At(new(0, 0.625f, 0))),
            (new Vector3(2), Pose.At(new(0, 0.5f, 0))),
            (new Vector3(0.25f, 0.015625f, 0.25f), new Pose(new(0.125f, 0.5f, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -0.7f))),
            (new Vector3(0.125f), Pose.At(new(0.25f, 0.625f, 0))),
            (new Vector3(0.125f), Pose.At(new(0.25f, 0.75f, 0)))
        })
        {
            using var fixture = new ConvexPlanarFixture(half, pose);
            var e = Classify(Request(), fixture, out var work, out _);
            Assert.Equal(ConvexInitialState.Overlapping, e.State);
            Assert.True(e.HasCommonPoint);
            Assert.True(e.CommonPoint.X.Rational && e.CommonPoint.Y.Rational && e.CommonPoint.Z.Rational);
            Assert.True(ShortBodyShellOracle.StrictCommonPoint(Shape, Pose.Identity, fixture.Oracle,
                e.CommonPoint.X.A.Numerator, e.CommonPoint.X.A.Denominator,
                e.CommonPoint.Y.A.Numerator, e.CommonPoint.Y.A.Denominator,
                e.CommonPoint.Z.A.Numerator, e.CommonPoint.Z.A.Denominator));
            Assert.Equal(default, e.Contact);
            output.WriteLine($"overlap cells={work.Cells} features={work.Features}");
        }
    }

    [Fact]
    public void RoundedTouchUsesTheExactAdmittedMargin()
    {
        using var fixture = new ConvexPlanarFixture(new(0.125f), Pose.At(new(0.375f, 0.625f, 0)));
        float m = 0.001f;
        foreach (float offset in new[] { MathF.BitDecrement(m), m, MathF.BitIncrement(m) })
        {
            var request = Request(m) with { Pose = Pose.At(new(-offset, 0, 0)) };
            var e = Classify(request, fixture, out var work, out var scratch);
            var expected = offset < m ? ConvexInitialState.Overlapping : offset == m ? ConvexInitialState.Touching : ConvexInitialState.Separated;
            Assert.Equal(expected, e.State);
            Assert.True(e.Pair.TryCompareSquaredMargin(m, ref work, scratch, out int sign));
            Assert.Equal(offset.CompareTo(m), sign);
            if (offset == m) CheckTouch(e, request, fixture);
        }
        foreach (var row in Rows())
            foreach (var stratum in new[] { ConvexTargetFeature.Face, ConvexTargetFeature.Edge, ConvexTargetFeature.Vertex })
            {
                using var touching = TouchFixture(row.Point, row.Outward, stratum);
                var e = Classify(Request(m), touching, out _, out _);
                Assert.Equal(ConvexInitialState.Overlapping, e.State);
                Assert.Equal(default, e.Contact);
            }
    }

    [Fact]
    public void InitialRelationIgnoresMotionDirection()
    {
        foreach (float x in new[] { MathF.BitDecrement(0.375f), 0.375f, MathF.BitIncrement(0.375f) })
        {
            using var fixture = new ConvexPlanarFixture(new(0.125f), Pose.At(new(x, 0.625f, 0)));
            foreach (Vector3 motion in new[] { Vector3.Zero, Vector3.UnitX, -Vector3.UnitX })
            {
                var request = Request() with { Displacement = motion };
                var e = Classify(request, fixture, out _, out _);
                Assert.Equal(x < 0.375f ? ConvexInitialState.Overlapping : x == 0.375f ? ConvexInitialState.Touching : ConvexInitialState.Separated, e.State);
                if (x == 0.375f) CheckTouch(e, request, fixture);
            }
        }
        using var shifted = new ConvexPlanarFixture(new(0.125f), Pose.At(new(0.5f, 0.625f, 0)));
        var atHalf = Classify(Request() with { Displacement = new(0.25f, 0, 0) }, shifted, out _, out _, 0.5);
        Assert.Equal(ConvexInitialState.Touching, atHalf.State);
        Assert.Equal(0, (atHalf.CommonPoint.X - Q.From(0.375f)).Sign);
    }

    [Fact]
    public void InitialRelationKeepsCoincidentSourceTickets()
    {
        using var fixture = new ConvexPlanarFixture(new(0.125f), Pose.At(new(0.375f, 0.625f, 0)));
        var a = new ConvexPlanarDispatch(fixture.Leaf, new(false, 7, 2, 3));
        var b = new ConvexPlanarDispatch(fixture.Leaf, new(true, 8, 4, 5));
        var work = new ConvexQueryWork(); Assert.True(work.Primitive());
        var scratch = new ConvexQueryScratch();
        Assert.True(a.TryClassifyInitial(Request(), ConvexCertificateFraction.From(0), ref work, scratch, out var ticket));
        Assert.True(a.TryPublishInitial(ticket, out var relation, out var identity));
        Assert.Equal(new ConvexContactIdentity(false, 7, 2, 3), identity);
        Assert.Equal(ConvexInitialState.Touching, relation.State);
        Assert.False(b.TryPublishInitial(ticket, out relation, out identity));
        Assert.Equal(default, relation); Assert.Equal(default, identity);
        Assert.True(b.TryClassifyInitial(Request(), ConvexCertificateFraction.From(0), ref work, scratch, out var other));
        Assert.True(b.TryPublishInitial(other, out _, out identity));
        Assert.Equal(new ConvexContactIdentity(true, 8, 4, 5), identity);
        Assert.False(a.TryPublishInitial(ticket, out _, out _));
        Assert.Equal(1, work.Descendants);
    }

    [Fact]
    public void StrictSignsOnEveryFiniteStratumAreIndependentOfTheClassificationFlag()
    {
        foreach (var row in Rows())
            foreach (var stratum in new[] { ConvexTargetFeature.Face, ConvexTargetFeature.Edge, ConvexTargetFeature.Vertex })
            {
                using var fixture = TouchFixture(row.Point, row.Outward, stratum);
                foreach (float margin in new[] { 0f, 0.001f })
                {
                    var separated = Request(margin) with { Pose = Pose.At(-row.Outward / 128) };
                    var e = Classify(separated, fixture, out _, out _);
                    Assert.Equal(ConvexInitialState.Separated, e.State);
                    Assert.True(e.Contact.LowerDistance > 0);
                    // The original exact support plane has a positive gap larger than m.
                    var support = ShortBodyShellOracle.SupportBounds(separated.Shape, row.Outward);
                    double attained = Vector3.Dot(row.Point - separated.Pose.Position, row.Outward);
                    Assert.True(attained > support.Upper);
                }
                var overlap = Request() with { Pose = Pose.At(row.Outward / 128) };
                var inside = Classify(overlap, fixture, out _, out _);
                Assert.Equal(ConvexInitialState.Overlapping, inside.State);
                P c = inside.CommonPoint;
                Assert.True(ShortBodyShellOracle.StrictCommonPoint(Shape, overlap.Pose, fixture.Oracle,
                    c.X.A.Numerator, c.X.A.Denominator, c.Y.A.Numerator, c.Y.A.Denominator, c.Z.A.Numerator, c.Z.A.Denominator));
            }
    }

    [Fact]
    public void ApexAndSeamNormalsCanLieBetweenTheIncidentTargetFaceNormals()
    {
        foreach (var (point, vertices) in new[]
        {
            (new Vector3(0, 0.25f, 0), new[] { Vector3.Zero, new Vector3(0.125f, 0.0625f, 0), new Vector3(0, -0.125f, 0.0625f), new Vector3(0, -0.125f, -0.0625f) }),
            (new Vector3(0.25f, 0.5f, 0), new[] { Vector3.Zero, new Vector3(0.0625f, 0.125f, 0), new Vector3(-0.0625f, -0.25f, 0), new Vector3(0.125f, 0, 0.125f) })
        })
        {
            using var fixture = new ConvexPlanarFixture(default, Pose.At(point), hull: true, vertices: vertices);
            var e = Classify(Request(), fixture, out var work, out _);
            CheckTouch(e, Request(), fixture);
            Assert.True(e.Direction.X.Sign > 0 && e.Direction.Y.Sign < 0);
            Assert.Equal(0, e.Direction.Z.Sign);
            output.WriteLine($"cone intersection cells={work.Cells} features={work.Features}");
        }
    }

    [Fact]
    public void StrictOverlapCanRequireAnInwardTwoVertexCombination()
    {
        using var fixture = new ConvexPlanarFixture(default, Pose.At(new(0.25f, 0.5f, 0)), hull: true,
            vertices: [Vector3.Zero, new(0.0625f, 0.25f, 0), new(-0.0625f, -0.09375f, 0), new(0.5f, 0.25f, 0.25f)]);
        var e = Classify(Request(), fixture, out var work, out _);
        Assert.Equal(ConvexInitialState.Overlapping, e.State);
        P c = e.CommonPoint;
        Assert.True(ShortBodyShellOracle.StrictCommonPoint(Shape, Pose.Identity, fixture.Oracle,
            c.X.A.Numerator, c.X.A.Denominator, c.Y.A.Numerator, c.Y.A.Denominator, c.Z.A.Numerator, c.Z.A.Denominator));
        output.WriteLine($"inward combination cells={work.Cells} features={work.Features}");
    }

    [Fact]
    public void CircularRelationsKeepTheRetainedRootAndScratchOwner()
    {
        using var fixture = new ConvexPlanarFixture(new(0.25f, 0.0001f, 0.0001f),
            new(new(0.5f, 1, 0.2f), new Quaternion(0, 0, -0.5f, 0.8660254f)));
        var e = Classify(Request(0.001f), fixture, out var work, out var scratch);
        Assert.Equal(ConvexInitialState.Separated, e.State);
        Assert.True(e.Pair.IsCircular);
        Assert.True(e.Pair.TryBarycentricFraction(0, 1, ref work, scratch, out var time));
        int retained = scratch.Polynomials.Used;
        Assert.True(retained > 0);
        Assert.True(ConvexPlanarContacts.TryClassify(Request(0.001f), fixture.Leaf, time, ref work, scratch, out var atRoot));
        Assert.Equal(ConvexInitialState.Separated, atRoot.State);
        Assert.Same(e.Pair.Circular!.Root, atRoot.Pair.Circular!.Root);
        Assert.Equal(retained, scratch.Polynomials.Used);
        Assert.False(ConvexPlanarContacts.TryClassify(Request(0.001f), fixture.Leaf, time, ref work, new(), out var refused));
        Assert.Equal(default, refused);
        Assert.True(atRoot.IsAlive);
        Assert.Equal(1, work.Descendants);
        output.WriteLine($"root cells={work.Cells} features={work.Features} retained={retained} high={scratch.Polynomials.HighWater}");
    }

    [Fact]
    public void TouchingConstraintsRefuseForeignScratchAndExpiredEvidence()
    {
        using var fixture = new ConvexPlanarFixture(new(0.125f), Pose.At(new(0.375f, 0.625f, 0)));
        var e = Classify(Request(), fixture, out var work, out var scratch);
        Assert.False(ConvexPlanarContacts.TryTouchingConstraint(e, P.From(Vector3.UnitX), ref work, new(), out var refused));
        Assert.Equal(default, refused);
        Assert.True(e.IsAlive);
        scratch.BeginCertificates();
        Assert.False(ConvexPlanarContacts.TryTouchingConstraint(e, P.From(Vector3.UnitX), ref work, scratch, out refused));
        Assert.Equal(default, refused);
    }

    [Fact]
    public void RefusalsKeepEvidenceDefaultAndChargeTheAdmittedPrimitive()
    {
        using var fixture = TouchFixture(new(0.25f, 0.75f, 0), new(1, 1, 0), ConvexTargetFeature.Vertex);
        var probe = Classify(Request(), fixture, out var measured, out _);
        foreach (var limit in new[] { new ConvexQueryWork(maximumCells: measured.Cells - 1), new ConvexQueryWork(maximumFeatures: measured.Features - 1) })
        {
            var work = limit; Assert.True(work.Primitive());
            var scratch = new ConvexQueryScratch();
            Assert.False(ConvexPlanarContacts.TryClassify(Request(), fixture.Leaf, ConvexCertificateFraction.From(0), ref work, scratch, out var e));
            Assert.Equal(default, e); Assert.True(work.Exhausted);
            Assert.Equal(1, work.Descendants);
            Assert.Equal(0, scratch.Polynomials.Used);
        }
        var unadmitted = new ConvexQueryWork();
        Assert.False(ConvexPlanarContacts.TryClassify(Request(), fixture.Leaf, ConvexCertificateFraction.From(0), ref unadmitted, new(), out var refused));
        Assert.Equal(default, refused);
        output.WriteLine($"charged cells={measured.Cells} features={measured.Features} alive={probe.IsAlive}");
    }
}
