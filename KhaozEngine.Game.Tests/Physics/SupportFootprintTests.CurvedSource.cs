using System;
using System.Numerics;
using BepuPhysics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Physics;

public partial class SupportFootprintTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CurvedPolynomialFallbackRetainsNormalizedSourceIdentity(bool enclosed)
    {
        using var diagnostics = new SourceDiagnosticScope();
        var arithmetic = new SupportCurvedSourceArithmetic();
        FeatureNumber Source(double value) => enclosed ? FeatureNumber.Enclosed(GeometryInterval.Enclose(
            Math.BitDecrement(value), Math.BitIncrement(value))) : FeatureNumber.Exact(value);
        arithmetic.Reset(new(Source(0.6), FeatureNumber.Exact(0), Source(0.8)));
        var square = arithmetic.Add(arithmetic.Square(arithmetic.X), arithmetic.Square(arithmetic.Z));
        var root = arithmetic.Root(square);
        var x = arithmetic.Divide(arithmetic.X, root); var z = arithmetic.Divide(arithmetic.Z, root);
        var identity = arithmetic.Subtract(arithmetic.Add(arithmetic.Square(x), arithmetic.Square(z)), arithmetic.One);
        Assert.Equal(GeometrySign.Zero, arithmetic.Sign(identity));
        AssertSourceFallbacks();
        long before = GC.GetAllocatedBytesForCurrentThread();
        GeometrySign warm = arithmetic.Sign(identity);
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(GeometrySign.Zero, warm);
        Assert.Equal(0, bytes);
    }

    [Fact]
    public void CurvedRadicalFallbackKeepsUnsquaredSignsAndExactTies()
    {
        using var diagnostics = new SourceDiagnosticScope();
        var arithmetic = new SupportCurvedSourceArithmetic();
        arithmetic.Reset(new(FeatureNumber.Exact(0), FeatureNumber.Exact(1), FeatureNumber.Exact(0)));
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
        Assert.True(InstalledPoseOperator.TryCreate(pose, out var map));
        var axis = GeometryVectorOperations.Normalize(map.Y.Bounds);
        var leaf = new SupportCurvedLeaf(0, SupportCurvedShape.Cylinder, FeaturePoint.Exact(pose.Position).Bounds,
            axis, 0.25, half, default, default, default, map.Y);
        var kernel = new SupportFootprintCurved();
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
        long before = GC.GetAllocatedBytesForCurrentThread();
        GeometrySign warm = kernel.Measure(default, leaf, 0, request, out _);
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(GeometrySign.Positive, warm);
        Assert.Equal(0, bytes);

    }

}
