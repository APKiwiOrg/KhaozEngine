using System;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;
using Xunit.Abstractions;
using P = KhaozEngine.Physics.Bepu.ConvexExactPoint;
using Q = KhaozEngine.Physics.Bepu.ConvexQuadratic;

namespace KhaozEngine.Tests.Physics;

public sealed partial class ConvexPlanarPenetrationTests(ITestOutputHelper output)
{
    static readonly ConvexQueryShape Shape = new(ConvexQueryKind.ConeCylinder, 0.25f, 0, 0.5f, 1);
    static ConvexQueryRequest Request(float margin = 0) => new(Shape.WithMargin(margin), Pose.Identity,
        Vector3.Zero, ConvexQueryOperation.Penetration);

    ConvexPenetrationCertificate Evaluate(ConvexPlanarFixture fixture, ConvexQueryRequest request,
        out ConvexQueryWork work, out ConvexQueryScratch scratch)
    {
        work = new(); scratch = new();
        Assert.True(work.Primitive());
        Assert.True(ConvexPlanarPenetration.TryEvaluate(request, fixture.Leaf, ConvexCertificateFraction.From(0),
            ref work, scratch, out var certificate), $"cells={work.Cells}, features={work.Features}, exhausted={work.Exhausted}");
        Assert.True(certificate.IsAlive);
        Assert.Equal(1, work.Descendants);
        output.WriteLine($"cells={work.Cells}, features={work.Features}, vertices={scratch.Penetration.VertexCount}, faces={scratch.Penetration.FaceCount}");
        return certificate;
    }

    [Fact]
    public void PlanarPenetrationHasMinimumMemberTranslation()
    {
        foreach (float margin in new[] { 0, 0.001f })
            foreach (var (half, position, optimum) in new[]
            {
                (new Vector3(0.5f, 2, 2), new Vector3(0.5f + 0.125f, 0, 0), 0.125f),
                (new Vector3(0.0625f), new Vector3(0, 0.75f, 0), 0.3125f),
                (new Vector3(2), new Vector3(0, 0.5f, 0), 2.25f),
                (new Vector3(2, 0.125f, 2), new Vector3(0, 0.9375f, 0), 0.1875f)
            })
            {
                using var fixture = new ConvexPlanarFixture(half, Pose.At(position));
                var e = Evaluate(fixture, Request(margin), out _, out _);
                Assert.True(e.Initial.State == ConvexInitialState.Overlapping);
                Assert.InRange(optimum + margin, e.LowerDepth, e.UpperDepth);
                Assert.InRange(e.UpperDepth - e.LowerDepth, 0, 0.001f);
                Assert.True(ShortBodyShellOracle.MemberTranslationEnclosedBy(Request(margin).Shape, Pose.Identity,
                    fixture.Oracle, new(true, true, e.Translation, e.LowerDepth, e.UpperDepth, default)));
            }
    }

    [Fact]
    public void LocalStationaryExitCannotBeatTheGlobalLowerProof()
    {
        using var fixture = new ConvexPlanarFixture(new(0.5f, 2, 2), Pose.At(new(0.625f, 0, 0)));
        var e = Evaluate(fixture, Request(), out var work, out var scratch);
        Assert.True(e.UpperDepth < 0.126f);
        Assert.True(ConvexPenetrationPlane.TryCreate(Request(), e.Initial.Source, default, P.From(-Vector3.UnitX), ref work, out var plane));
        Assert.False(ConvexPenetrationCertificate.TryCreate(e.Initial, plane, e.GlobalLowerDepthBounds, ref work, scratch, out var rejected));
        Assert.Equal(default, rejected);
        // The right exit is an attained stationary exit too, but is not the member minimum.
        float wrongDepth = 1.375f + 1f / 4096;
        var wrong = new ConvexPenetrationResult(true, true, new(wrongDepth, 0, 0), 1.375f, wrongDepth, default);
        Assert.False(ShortBodyShellOracle.MemberTranslationEnclosedBy(Shape, Pose.Identity, fixture.Oracle, wrong));
    }

    [Fact]
    public void EncodedTranslationClearsItsOriginalMember()
    {
        using var original = new ConvexPlanarFixture(new(0.5f, 2, 2), Pose.At(new(0.625f, 0, 0)));
        using var other = new ConvexPlanarFixture(new(0.5f, 2, 2), Pose.At(new(-0.625f, 0, 0)));
        var e = Evaluate(original, Request(), out _, out _);
        var result = new ConvexPenetrationResult(true, true, e.Translation, e.LowerDepth, e.UpperDepth, default);
        Assert.True(ShortBodyShellOracle.MemberTranslationEnclosedBy(Shape, Pose.Identity, original.Oracle, result));
        var moved = Pose.At(e.Translation);
        Assert.True(ShortBodyShellOracle.Entry(Shape, moved, original.Oracle, Vector3.Zero).Lower >= 0);
        var work = new ConvexQueryWork(); Assert.True(work.Primitive());
        Assert.True(ConvexPlanarContacts.TryClassify(Request() with { Pose = moved }, other.Leaf,
            ConvexCertificateFraction.From(0), ref work, new(), out var blocked));
        Assert.Equal(ConvexInitialState.Overlapping, blocked.State);
        using var shifted = new ConvexPlanarFixture(new(0.5f, 2, 2), Pose.At(new(100.625f, 0, 0)));
        var request = Request(0.001f) with { Pose = Pose.At(new(100, 0, 0)) };
        var world = Evaluate(shifted, request, out _, out _);
        Pose destination = Pose.At(request.Pose.Position + world.Translation);
        Assert.True(ShortBodyShellOracle.Entry(request.Shape, destination, shifted.Oracle, Vector3.Zero).Lower > 0,
            $"encoded destination {destination.Position}");
    }

    [Fact]
    public void InitialOverlapNormalHasAttainedSupportWitnesses()
    {
        // Dyadic face normals exercise the cone and the closed rim cone without a rounded tilt.
        foreach (var (normal, point) in new[]
        {
            (new Vector3(2, -1, 0), new Vector3(0.125f, 0.25f, 0)),
            (new Vector3(1, 1, 0), new Vector3(0.25f, 1, 0))
        })
        {
            Vector3 u = new(-normal.Y, normal.X, 0), v = Vector3.UnitZ;
            Vector3 centre = point - normal / 128;
            Vector3[] vertices = [centre-u-v, centre+u-v, centre-u+v, centre+u+v,
                centre-u-v+normal, centre+u-v+normal, centre-u+v+normal, centre+u+v+normal];
            using var fixture = new ConvexPlanarFixture(default, Pose.Identity, hull: true, vertices: vertices);
            var e = Evaluate(fixture, Request(), out var work, out _);
            Assert.True(P.Dot(e.Direction, e.Direction).Sign > 0);
            Assert.True(ConvexPlanarFeatures.Member(Shape, e.Query));
            Assert.True(ConvexPlanarFeatures.NormalCone(Shape, e.Query, e.Direction));
            Assert.True(ConvexPlanarFeatures.SupportsTarget(e.Attained.Source, e.Attained.Feet, e.Target,
                e.Direction, ref work));
            // SS-R12 witnesses optimize the support functional, not a second distance problem.
            Assert.True(P.Dot(e.Target - e.Query, e.Direction).Sign >= 0);
        }
    }

    [Fact]
    public void PlanarContactsKeepSignedBoundsAndRawConstraints()
    {
        using var fixture = new ConvexPlanarFixture(new(0.5f, 2, 2), Pose.At(new(0.625f, 0, 0)));
        foreach (var operation in new[] { ConvexQueryOperation.Contacts, ConvexQueryOperation.Penetration })
        {
            var a = new ConvexPlanarDispatch(fixture.Leaf, new(false, 11, 2, 3));
            var b = new ConvexPlanarDispatch(fixture.Leaf, new(true, 12, 4, 5));
            var work = new ConvexQueryWork();
            Assert.True(a.TryEvaluate(Request() with { Operation = operation }, ref work, new(), out var evidence));
            Assert.True(a.TryPublishContact(evidence, out var contact));
            Assert.True(contact.UpperSeparation < 0);
            Assert.Equal(new(false, 11, 2, 3), contact.Identity);
            Assert.False(b.TryPublishContact(evidence, out var refused)); Assert.Equal(default, refused);
            Assert.True(a.TryPublishPenetration(evidence, out var penetration));
            Assert.Equal(contact.LowerSeparation, -penetration.UpperDepth);
            Assert.Equal(contact.UpperSeparation, -penetration.LowerDepth);
        }
    }

    [Fact]
    public void PenetrationDenialClearsEveryOutput()
    {
        using var fixture = new ConvexPlanarFixture(new(0.5f, 2, 2), Pose.At(new(0.625f, 0, 0)));
        Evaluate(fixture, Request(), out var probe, out _);
        foreach (bool cells in new[] { true, false })
        {
            var work = cells ? new ConvexQueryWork(maximumCells: probe.Cells - 1) :
                new ConvexQueryWork(maximumFeatures: probe.Features - 1);
            Assert.True(work.Primitive());
            var scratch = new ConvexQueryScratch();
            Assert.False(ConvexPlanarPenetration.TryEvaluate(Request(), fixture.Leaf, ConvexCertificateFraction.From(0),
                ref work, scratch, out var refused));
            Assert.Equal(default, refused); Assert.True(work.Exhausted);
            Assert.False(ConvexPlanarPenetration.TryEvaluate(Request(), fixture.Leaf, ConvexCertificateFraction.From(0),
                ref work, scratch, out refused));
            Assert.Equal(default, refused); Assert.Equal(0, scratch.Polynomials.Used);
        }
        var limited = new ConvexQueryScratch(penetrationVertices: 4, penetrationFaces: 4);
        var fresh = new ConvexQueryWork(); Assert.True(fresh.Primitive());
        Assert.False(ConvexPlanarPenetration.TryEvaluate(Request(), fixture.Leaf, ConvexCertificateFraction.From(0),
            ref fresh, limited, out var denied));
        Assert.Equal(default, denied); Assert.True(fresh.Exhausted);
        var arena = new ConvexQueryScratch(maximumCoefficients: 0);
        fresh = new(); Assert.True(fresh.Primitive());
        Assert.False(ConvexPlanarPenetration.TryEvaluate(Request(), fixture.Leaf, ConvexCertificateFraction.From(0),
            ref fresh, arena, out denied));
        Assert.Equal(default, denied); Assert.True(fresh.Exhausted);
    }

    [Fact]
    public void OracleRejectsAValidPenetrationReusedAtAnotherPose()
    {
        using var target = new ConvexPlanarFixture(new(0.5f, 2, 2), Pose.At(new(0.5f, 0, 0)));
        float depth = 0.125f + 1f / 4096;
        var result = new ConvexPenetrationResult(true, true, new(-depth, 0, 0), 0.125f, depth, default);
        Assert.True(ShortBodyShellOracle.MemberTranslationEnclosedBy(Shape, Pose.At(new(-0.125f, 0, 0)), target.Oracle, result));
        Assert.False(ShortBodyShellOracle.MemberTranslationEnclosedBy(Shape, Pose.At(new(-0.0625f, 0, 0)), target.Oracle, result));
    }

    [Fact]
    public void VertexExitAndMarginOnlyOverlapHaveGlobalDepthBounds()
    {
        using var vertex = new ConvexPlanarFixture(new(0.015625f), Pose.At(new(0.21875f, 0.75f, 0.0625f)));
        var e = Evaluate(vertex, Request(), out _, out _);
        double optimum = 0.25 - Math.Sqrt(0.203125 * 0.203125 + 0.046875 * 0.046875);
        Assert.True(e.LowerDepth <= optimum && e.UpperDepth >= optimum);
        AssertSupportProvenance(e, vertex.Oracle);
        using var rounded = new ConvexPlanarFixture(new(0.5f, 2, 2), Pose.At(new(0.75048828125f, 0, 0)));
        var grown = Evaluate(rounded, Request(0.001f), out _, out _);
        Assert.True(grown.LowerDepth <= 0.001 - 1.0 / 2048 && grown.UpperDepth >= 0.001 - 1.0 / 2048);
    }

    [Fact]
    public void ContactsPreserveSeparatedTouchingAndExpiredLoans()
    {
        foreach (float x in new[] { 0.75f, 0.875f })
        {
            using var fixture = new ConvexPlanarFixture(new(0.5f, 2, 2), Pose.At(new(x, 0, 0)));
            var owner = new ConvexPlanarDispatch(fixture.Leaf, new(false, 7, 8, 9));
            var scratch = new ConvexQueryScratch(); var work = new ConvexQueryWork();
            Assert.True(owner.TryEvaluate(Request() with { Operation = ConvexQueryOperation.Contacts }, ref work, scratch, out var evidence));
            Assert.True(owner.TryPublishContact(evidence, out var contact));
            Assert.True(contact.LowerSeparation <= x - 0.75f && contact.UpperSeparation >= x - 0.75f);
            Assert.NotEqual(Vector3.Zero, contact.Normal);
            scratch.BeginCertificates();
            Assert.False(owner.TryPublishContact(evidence, out var refused)); Assert.Equal(default, refused);
        }
    }
}
