using System;
using System.Numerics;
using BepuPhysics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Tests.Locomotion.Contacts;
using Xunit;
using B = KhaozEngine.Tests.Physics.SupportFootprintExactOracle.Bounds;
using P = KhaozEngine.Tests.Physics.SupportFootprintExactOracle.Point;
using R = KhaozEngine.Tests.Physics.InstalledPoseOracle.R;
using V = KhaozEngine.Tests.Physics.InstalledPoseOracle.V;

namespace KhaozEngine.Tests.Physics;

public partial class SupportFootprintTests
{
    [Fact]
    public void CurvedPolynomialFallbackRetainsNormalizedSourceIdentity()
    {
        using var diagnostics = new SourceDiagnosticScope();
        var work = NewCurvedWork();
        var arithmetic = new SupportCurvedSourceArithmetic(work);
        arithmetic.Reset(new(0.6f, 0, 0.8f));
        var square = arithmetic.Add(arithmetic.Square(arithmetic.X), arithmetic.Square(arithmetic.Z));
        var root = arithmetic.Root(square);
        var x = arithmetic.Divide(arithmetic.X, root); var z = arithmetic.Divide(arithmetic.Z, root);
        var identity = arithmetic.Subtract(arithmetic.Add(arithmetic.Square(x), arithmetic.Square(z)), arithmetic.One);
        Assert.Equal(GeometrySign.Zero, arithmetic.Sign(identity));
        AssertSourceFallbacks();
        GeometrySign warm = default;
        long bytes = AllocationMeasurement.Measure(nameof(CurvedPolynomialFallbackRetainsNormalizedSourceIdentity), () =>
            warm = arithmetic.Sign(identity));
        Assert.Equal(GeometrySign.Zero, warm);
        Assert.Equal(0, bytes);
    }

    // The generic coefficient-box owner still cancels equal monomials over a whole box. No support source reaches it.
    [Fact]
    public void SourcePolynomialBoxCancelsTheClearedNormalizedIdentity()
    {
        var work = new SupportSourcePolynomialWorkspace();
        GeometryInterval Box(double value) => GeometryInterval.Enclose(Math.BitDecrement(value), Math.BitIncrement(value));
        GeometrySign Identity()
        {
            work.Reset();
            var x = work.Coefficient(Box(0.6));
            var z = work.Coefficient(Box(0.8));
            work.CompleteMap();
            var squared = work.Add(work.Multiply(x, x), work.Multiply(z, z));
            // (x/r)^2 + (z/r)^2 - 1 over the positive r^2 = x^2 + z^2, cleared of its denominator.
            return work.Sign(work.Add(work.Add(work.Multiply(x, x), work.Multiply(z, z)), squared, -1));
        }
        Assert.Equal(GeometrySign.Zero, Identity());
        GeometrySign warm = default;
        long bytes = AllocationMeasurement.Measure(nameof(SourcePolynomialBoxCancelsTheClearedNormalizedIdentity), () =>
            warm = Identity());
        Assert.Equal(GeometrySign.Zero, warm);
        Assert.Equal(0, bytes);
    }

    [Fact]
    public void CurvedRadicalFallbackKeepsUnsquaredSignsAndExactTies()
    {
        using var diagnostics = new SourceDiagnosticScope();
        var work = NewCurvedWork();
        var arithmetic = new SupportCurvedSourceArithmetic(work);
        arithmetic.Reset(Vector3.UnitY);
        var two = arithmetic.Root(arithmetic.Constant(2)); var eight = arithmetic.Root(arithmetic.Constant(8));
        var tied = arithmetic.Subtract(arithmetic.Multiply(two, eight), arithmetic.Constant(4));
        Assert.Equal(GeometrySign.Zero, arithmetic.Sign(tied));
        Assert.Equal(GeometrySign.Positive, arithmetic.Sign(arithmetic.Add(two, arithmetic.Constant(1))));
        Assert.Equal(GeometrySign.Negative, arithmetic.Sign(arithmetic.Subtract(arithmetic.Constant(1), two)));
        Assert.Equal(GeometrySign.Negative, arithmetic.Sign(arithmetic.Negate(eight)));
        AssertSourceFallbacks();
    }
    [Theory]
    [InlineData(0.1f, 0.7f, -0.75f, 1f, 1.25f)]
    [InlineData(0.2692306339740753f, 0.6538458466529846f, -0.3125f, 0.75f, 0.8125f)]
    public void CurvedFiniteAxisOnCoreBoundingCircleKeepsBothRoles(float y, float w, float x, float z, float half)
    {
        var pose = new RigidPose(new(0, -0.25f, 0), new Quaternion(0.5f, y, 0.5f, w));
        var leaf = RepresentedLeaf(SupportCurvedShape.Cylinder, pose, 0.25, half);
        var kernel = new SupportFootprintCurved(NewCurvedWork());
        var request = Request(x: x, z: z, lower: -2, upper: 2);
        GeometrySign result = kernel.Measure(default, leaf, 0, request, out var member);
        Assert.Equal(GeometrySign.Positive, result);
        Assert.Equal(2, member.WitnessCount);
        Assert.Equal(SupportWitnessRole.Axis, member.First.Role);
        Assert.Equal(SupportWitnessRole.Footprint, member.Second.Role);
        Assert.Equal(0, member.DiscHeightLower);
        Assert.Equal(0, member.DiscHeightUpper);
        Assert.Equal(Vector3.UnitY, member.Second.Normal);
        Assert.Equal(0, member.Second.NormalError);
        GeometrySign warm = default;
        long bytes = AllocationMeasurement.Measure(nameof(CurvedFiniteAxisOnCoreBoundingCircleKeepsBothRoles), () =>
            warm = kernel.Measure(default, leaf, 0, request, out _));
        Assert.Equal(GeometrySign.Positive, warm);
        Assert.Equal(0, bytes);

    }

    [Theory]
    [InlineData(-0.3125f, 0.75f)]
    [InlineData(0.3125f, -0.75f)]
    [InlineData(-0.15625f, 0.375f)]
    public void CurvedCapsuleEnclosingCoreLineKeepsBothRoles(float x, float z)
    {
        var pose = new RigidPose(new(0, -0.25f, 0),
            new Quaternion(0.5f, 0.2692306339740753f, 0.5f, 0.6538458466529846f));
        CheckCurvedCoreLine(SupportCurvedShape.Capsule, pose, 0.8125f, 0, x, z);
    }

    [Theory]
    [InlineData(0.1875f, 0.078125f)]
    [InlineData(-0.1875f, -0.078125f)]
    public void CurvedCapsuleCrestOnDiscBoundaryKeepsSignedAxisAndFootprint(float x, float z)
    {
        var pose = new RigidPose(new(0, -0.25f, 0),
            new Quaternion(0.5f, 0.2692306339740753f, 0.5f, 0.6538458466529846f));
        CheckCurvedCoreLine(SupportCurvedShape.Capsule, pose, 0.8125f, 0, x, z, 0.203125f);
    }

    [Theory]
    [InlineData("sphere", 0f)]
    [InlineData("capsule-upright", 0f)]
    [InlineData("capsule-horizontal", 0f)]
    [InlineData("capsule-horizontal", 0.8125f)]
    [InlineData("capsule-horizontal", -0.8125f)]
    [InlineData("cylinder-cap", 0f)]
    [InlineData("cylinder-side", 0f)]
    public void CurvedExactZeroCoreDistanceKeepsAttainedRoles(string stratum, float z)
    {
        SupportCurvedShape shape = stratum == "sphere" ? SupportCurvedShape.Sphere :
            stratum.StartsWith("capsule", StringComparison.Ordinal) ? SupportCurvedShape.Capsule : SupportCurvedShape.Cylinder;
        Quaternion orientation = stratum == "capsule-horizontal" ? new(0.5f, 0.5f, 0.5f, 0.5f) : Quaternion.Identity;
        CheckCurvedCoreLine(shape, new(new(0, -0.25f, 0), orientation), stratum == "sphere" ? 0 : 0.8125f,
            stratum == "cylinder-cap" ? SupportNeighborhoodCurved.TopCap : 0, 0, z,
            stratum == "cylinder-side" ? 0.25f : 0.2f);
    }

    static void CheckCurvedCoreLine(SupportCurvedShape shape, RigidPose pose, float half, int part,
        float x, float z, float footRadius = 0.2f)
    {
        var leaf = RepresentedLeaf(shape, pose, 0.25, half);
        var request = Request(x: x, z: z, radius: footRadius, lower: -2, upper: 2);
        V source = InstalledPoseOracle.Represented(pose.Orientation).Y;
        B norm = B.Root(source.LengthSquared);
        var exactAxis = new P((B)source.X / norm, (B)source.Y / norm, (B)source.Z / norm);
        P centre = P.Exact(V.From(pose.Position));
        OracleSurface surface = shape != SupportCurvedShape.Cylinder ? OracleSurface.Round :
            part == SupportNeighborhoodCurved.Side ? OracleSurface.CylinderSide : OracleSurface.CylinderTop;
        var exactPart = new SupportFootprintCurvedProofOracle.InstalledPart(new(default, part, surface,
            centre.Middle, exactAxis.Middle, 0.25, half)
        { ExactAxis = exactAxis }, centre, exactAxis,
            R.From(0.25), R.From(half));
        var exact = SupportFootprintClosedFormOracle.Measure(exactPart, request);
        var kernel = new SupportFootprintCurved(NewCurvedWork());
        Assert.Equal(GeometrySign.Positive, kernel.Measure(default, leaf, part, request, out var member));
        GeometrySign warm = default;
        SupportFootprintMember repeated = default;
        long bytes = AllocationMeasurement.Measure(nameof(CheckCurvedCoreLine), () =>
            warm = kernel.Measure(default, leaf, part, request, out repeated));
        Assert.Equal(exact.Witnesses.Count, member.WitnessCount);
        Assert.Equal(SupportWitnessRole.Footprint, member.WitnessCount == 2 ? member.Second.Role : member.First.Role);
        if (surface != OracleSurface.CylinderSide)
        {
            Assert.Equal(2, member.WitnessCount);
            Assert.Equal(SupportWitnessRole.Axis, member.First.Role);
        }
        Assert.True(exact.Maximum.HasValue);
        ContainsCurvedBounds(member.DiscHeightLower, member.DiscHeightUpper, exact.Maximum.Value);
        for (int i = 0; i < member.WitnessCount; i++)
        {
            var witness = i == 0 ? member.First : member.Second;
            var reference = exact.Witnesses[i];
            Assert.Equal(reference.Role, witness.Role);
            ContainsCurvedVector(witness.Position, witness.PositionErrorMetres, reference.Position);
            ContainsCurvedVector(witness.Normal, witness.NormalError, reference.Normal);
            if (reference.Contribution is { } contribution) ContainsCurvedContribution(witness, request, contribution);
        }
        Assert.Equal(GeometrySign.Positive, warm);
        Assert.Equal(member, repeated);
        Assert.Equal(0, bytes);
    }

    // A synthetic leaf at the represented support column of an admitted pose.
    static SupportCurvedLeaf RepresentedLeaf(SupportCurvedShape shape, RigidPose pose, double radius, double half)
    {
        Assert.True(InstalledPoseOperator.TryCreateSupport(pose, out var map));
        Vector3 column = InstalledSupportPoseTests.Point(map.Y);
        return new SupportCurvedLeaf(0, shape, FeaturePoint.Exact(pose.Position).Bounds,
            GeometryVectorOperations.Normalize(FeaturePoint.Exact(column).Bounds), radius, half, default, default,
            default, column);
    }
}
