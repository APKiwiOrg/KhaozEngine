using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;
using P = KhaozEngine.Physics.Bepu.ConvexExactPoint;
using Q = KhaozEngine.Physics.Bepu.ConvexQuadratic;

namespace KhaozEngine.Tests.Physics;

public sealed partial class ConvexPlanarPenetrationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(0.001f)]
    public void GenericPlaneExitCertifiesAnInteriorCylinderEdge(float margin)
    {
        using var fixture = new ConvexPlanarFixture(new(0.015625f, 2, 0.015625f), Pose.At(new(0.21875f, 0.75f, 0.0625f)));
        var e = Evaluate(fixture, Request(margin), out _, out _);
        double optimum = 0.25 - System.Math.Sqrt(0.203125 * 0.203125 + 0.046875 * 0.046875) + margin;
        Assert.True(e.LowerDepth <= optimum && e.UpperDepth >= optimum);
        Assert.Contains(e.Contact.QueryFeature, new[] { ConvexShellFeature.Cylinder, ConvexShellFeature.Shoulder });
        Assert.Equal(ConvexTargetFeature.Edge, e.Contact.TargetFeature);
        Assert.True(ShortBodyShellOracle.MemberTranslationEnclosedBy(Request(margin).Shape, Pose.Identity,
            fixture.Oracle, new(true, true, e.Translation, e.LowerDepth, e.UpperDepth, default)));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 0.001f)]
    [InlineData(1, 0)]
    [InlineData(1, 0.001f)]
    [InlineData(2, 0)]
    [InlineData(2, 0.001f)]
    public void GenericPlaneExitCertifiesObliqueConeAndCircularEdges(int regime, float margin)
    {
        var (q, n, v) = regime switch
        {
            0 => (new Vector3(0.125f, 0.25f, 0), new Vector3(2, -1, 0), new Vector3(0.5f, 1, 1)),
            1 => (new Vector3(0.25f, 0.5f, 0), new Vector3(1, -0.25f, 0), new Vector3(0.25f, 1, 1)),
            _ => (new Vector3(0.25f, 1, 0), new Vector3(1, 1, 0), new Vector3(1, -1, 1))
        };
        Vector3 centre = q - n / 256, a = Vector3.UnitX, b = regime == 2 ? Vector3.UnitY : -Vector3.UnitY;
        Vector3[] vertices = [centre-v/8, centre+v/8, centre-v/8+a, centre+v/8+a,
            centre-v/8+b, centre+v/8+b, centre-v/8+a+b, centre+v/8+a+b];
        using var fixture = new ConvexPlanarFixture(default, Pose.Identity, hull: true, vertices: vertices);
        var e = Evaluate(fixture, Request(margin), out _, out _);
        // The bounded upper plane may expose another feature than the exact minimum.
        // Its reported provenance must describe its actual attained support witnesses.
        AssertSupportProvenance(e, fixture.Oracle);
        Assert.True(ShortBodyShellOracle.MemberTranslationEnclosedBy(Request(margin).Shape, Pose.Identity,
            fixture.Oracle, new(true, true, e.Translation, e.LowerDepth, e.UpperDepth, default)));
        Assert.InRange(e.Contact.CertifiedErrorMetres, 0, 0.001f);
    }

    [Fact]
    public void PenetrationRequiresARationalFractionWithoutReplacingARootLoan()
    {
        using var fixture = new ConvexPlanarFixture(new(0.5f, 2, 2), Pose.At(new(0.625f, 0, 0)));
        var scratch = new ConvexQueryScratch(); var work = new ConvexQueryWork();
        Assert.True(work.Primitive());
        var equation = scratch.Polynomials.Import(ConvexPolynomial.From(-0.5, 0, 1));
        Assert.True(ConvexAlgebraicRoots.TryRetain(equation, 0, 1, ref work, scratch, out var root));
        int retained = scratch.Polynomials.Used;
        Assert.False(ConvexPlanarPenetration.TryEvaluate(Request(), fixture.Leaf,
            ConvexCertificateFraction.FromRoot(root, 0, 1), ref work, scratch, out var refused));
        Assert.Equal(default, refused);
        Assert.True(root.IsAlive); Assert.Equal(retained, scratch.Polynomials.Used);
        Assert.False(ConvexPlanarPenetration.TryEvaluate(Request(), fixture.Leaf,
            ConvexCertificateFraction.From(Q.Root(ConvexRational.From(0.5))), ref work, scratch, out refused));
        Assert.Equal(default, refused);
        Assert.True(root.IsAlive); Assert.Equal(retained, scratch.Polynomials.Used);
    }

    [Fact]
    public void GenericEdgeContactsKeepSignedBoundsAndRefuseAnInsufficientHull()
    {
        using var fixture = new ConvexPlanarFixture(new(0.015625f, 2, 0.015625f), Pose.At(new(0.21875f, 0.75f, 0.0625f)));
        foreach (float margin in new[] { 0, 0.001f })
        {
            var owner = new ConvexPlanarDispatch(fixture.Leaf, new(false, 17, 18, 19));
            var work = new ConvexQueryWork(); var scratch = new ConvexQueryScratch();
            Assert.True(owner.TryEvaluate(Request(margin) with { Operation = ConvexQueryOperation.Contacts }, ref work, scratch, out var e));
            Assert.True(owner.TryPublishContact(e, out var contact));
            Assert.True(contact.UpperSeparation < 0); Assert.Equal(new(false, 17, 18, 19), contact.Identity);
            Assert.Equal(ConvexTargetFeature.Edge, e.Local.TargetFeature);
            scratch.BeginCertificates();
            Assert.False(owner.TryPublishContact(e, out var expired)); Assert.Equal(default, expired);
            work = new(); Assert.True(work.Primitive());
            Assert.False(ConvexPlanarPenetration.TryEvaluate(Request(margin), fixture.Leaf, ConvexCertificateFraction.From(0),
                ref work, new(penetrationVertices: 4, penetrationFaces: 4), out var refused));
            Assert.Equal(default, refused); Assert.True(work.Exhausted);
        }
    }

    [Fact]
    public void APlaneCannotBeReboundToACoincidentOriginalMember()
    {
        using var fixture = new ConvexPlanarFixture(new(0.5f, 2, 2), Pose.At(new(0.625f, 0, 0)));
        using var other = new ConvexPlanarFixture(new(0.5f, 2, 2), Pose.At(new(0.625f, 0, 0)));
        var e = Evaluate(fixture, Request(), out var work, out var scratch);
        Assert.True(ConvexPlanarSource.TryCreate(other.Leaf, out var foreign));
        Assert.True(ConvexPenetrationProposal.TryCreate(Request(), e.Initial.Source, default,
            P.From(Vector3.UnitX), ref work, out var proposal));
        Assert.False(ConvexPenetrationPlane.TryVerify(Request(), foreign, default, proposal, ref work, out var unbound));
        Assert.Equal(default, unbound);
        Assert.False(ConvexPenetrationPlane.TryVerify(Request(), e.Initial.Source, P.From(Vector3.UnitX), proposal, ref work, out unbound));
        Assert.Equal(default, unbound);
        Assert.False(ConvexPenetrationPlane.TryVerify(Request(0.001f), e.Initial.Source, default, proposal, ref work, out unbound));
        Assert.Equal(default, unbound);
        Assert.True(ConvexPenetrationPlane.TryCreate(Request(), foreign, default, P.From(Vector3.UnitX), ref work, out var plane));
        Assert.False(ConvexPenetrationCertificate.TryCreate(e.Initial, plane, e.GlobalLowerDepthBounds,
            ref work, scratch, out var refused));
        Assert.Equal(default, refused);
    }

    [Fact]
    public void APlaneCannotBeReusedAtAnotherPoseOrWithAnotherShape()
    {
        using var fixture = new ConvexPlanarFixture(new(0.5f, 2, 2), Pose.At(new(0.625f, 0, 0)));
        var e = Evaluate(fixture, Request(), out var work, out var scratch);
        foreach (bool changedPose in new[] { true, false })
        {
            var request = changedPose ? Request() : Request(0.001f);
            P feet = changedPose ? P.From(new Vector3(0.03125f, 0, 0)) : default;
            Assert.True(ConvexPenetrationPlane.TryCreate(request, e.Initial.Source, feet,
                P.From(Vector3.UnitX), ref work, out var plane));
            Assert.False(ConvexPenetrationCertificate.TryCreate(e.Initial, plane, e.GlobalLowerDepthBounds,
                ref work, scratch, out var refused));
            Assert.Equal(default, refused);
        }
    }

    [Fact]
    public void OracleRejectsATranslationThatStillOverlapsItsOriginalMember()
    {
        using var fixture = new ConvexPlanarFixture(new(0.5f, 2, 2), Pose.At(new(0.625f, 0, 0)));
        var incomplete = new ConvexPenetrationResult(true, true, new(-0.124f, 0, 0), 0.125f, 0.125244140625f, default);
        Assert.False(ShortBodyShellOracle.MemberTranslationEnclosedBy(Shape, Pose.Identity, fixture.Oracle, incomplete));
    }

    [Fact]
    public void PlaneWitnessRetainsTheExactOriginalFractionAndSource()
    {
        using var fixture = new ConvexPlanarFixture(new(0.5f, 2, 2), Pose.At(new(0.625f, 0, 0)));
        var request = Request(0.001f) with { Pose = Pose.At(new(0.1f, 0, 0)), Displacement = new(0.1f, 0, 0), Operation = ConvexQueryOperation.Sweep };
        var work = new ConvexQueryWork(); var scratch = new ConvexQueryScratch();
        Assert.True(work.Primitive());
        var time = ConvexCertificateFraction.From(0.5);
        Assert.True(ConvexPlanarPenetration.TryEvaluate(request, fixture.Leaf, time, ref work, scratch, out var e));
        P feet = P.From(request.Pose.Position) + P.From(request.Displacement) * Q.From(0.5);
        Assert.NotEqual(P.From(request.Pose.Position + request.Displacement * 0.5f), feet);
        Assert.Equal(feet + P.From(e.Translation), e.Attained.Feet);
        Assert.Equal(request, e.Attained.Request);
        Assert.Equal(time.Value, e.Attained.Fraction.Value);
        Assert.Same(e.Initial.Source.Token, e.Attained.Source.Token);
        Assert.True(e.Initial.IsAlive && e.Attained.IsAlive);
        Assert.Equal(1, work.Descendants);
        scratch.BeginCertificates(); Assert.False(e.IsAlive);
        Assert.Equal(0, scratch.Polynomials.Used);
    }
}
