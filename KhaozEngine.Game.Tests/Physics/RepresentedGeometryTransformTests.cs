using System;
using System.Numerics;
using BepuUtilities;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Physics;

// Supplied matrix arithmetic only. These facts do not prove that a matrix represents an installed shape.
public class RepresentedGeometryTransformTests
{
    [Fact]
    public void IdentityAndTranslationEncloseTheExactRepresentedSum()
    {
        Matrix3x3 matrix = Matrix3x3.Identity;
        Vector3 local = new(0.125f, -2f, 4f), translation = new(54f, 1.4825f, -97.75f);
        GeometryVector value = RepresentedGeometryTransforms.Point(matrix, translation, local);
        Contains(value, 54.125, (double)translation.Y - 2d, -93.75);
        ContainsBackend(value, matrix, translation, local);
    }

    [Fact]
    public void RowBasisQuarterTurnDoesNotAccidentallyUseTheTranspose()
    {
        Matrix3x3 matrix = QuarterTurn();
        Vector3 local = new(2, 3, 4);
        GeometryVector value = RepresentedGeometryTransforms.Point(matrix, Vector3.Zero, local);
        Contains(value, -3, 2, 4);
        ContainsBackend(value, matrix, Vector3.Zero, local);
    }

    [Fact]
    public void EnclosureContainsBothRealAndSingleRoundedCancellation()
    {
        var matrix = new Matrix3x3 { X = Vector3.UnitX, Y = new Vector3(1, 1, 0), Z = new Vector3(1, 0, 1) };
        Vector3 local = new(16777216f, 1f, -16777216f);
        GeometryVector value = RepresentedGeometryTransforms.Point(matrix, Vector3.Zero, local);
        Contains(value, 1d, 1d, -16777216d);
        ContainsBackend(value, matrix, Vector3.Zero, local);
        // This deliberately exceeds the feature domain. A finite enclosure is not domain acceptance.
        Assert.True(value.X.Upper - value.X.Lower > 0.00025);
    }

    [Fact]
    public void SingleRoundingRetainsTheUnroundedValueAsWellAsItsEncoding()
    {
        GeometryInterval value = GeometryInterval.Exact(16777217d).EncloseSingleRounding();
        Assert.True(value.IsResolved);
        Assert.True(value.Lower <= 16777216d && value.Upper >= 16777217d);
    }

    [Fact]
    public void LargeFrameRoundingIsIncludedRatherThanHiddenAsZeroError()
    {
        Matrix3x3 matrix = Matrix3x3.Identity;
        Vector3 local = new(0.125f, 0.0425f, 0.125f), translation = new(2048f, 100f, -2048f);
        GeometryVector value = RepresentedGeometryTransforms.Point(matrix, translation, local);
        Contains(value, (double)translation.X + local.X, (double)translation.Y + local.Y,
            (double)translation.Z + local.Z);
        ContainsBackend(value, matrix, translation, local);
        Assert.True(value.X.Upper > value.X.Lower);
    }

    [Fact]
    public void NonfiniteMatrixPointOrTranslationRefusesTheWholeVector()
    {
        Matrix3x3 matrix = Matrix3x3.Identity;
        matrix.X.X = float.NaN;
        Assert.False(RepresentedGeometryTransforms.Point(matrix, Vector3.Zero, Vector3.One).IsResolved);
        Assert.False(RepresentedGeometryTransforms.Point(Matrix3x3.Identity, new Vector3(float.PositiveInfinity),
            Vector3.One).IsResolved);
        Assert.False(RepresentedGeometryTransforms.Point(Matrix3x3.Identity, Vector3.Zero,
            new Vector3(float.NaN)).IsResolved);
    }

    [Fact]
    public void SinglePrecisionOverflowCannotReturnAResolvedTransform()
    {
        Assert.False(RepresentedGeometryTransforms.Point(Matrix3x3.Identity, new Vector3(float.MaxValue),
            new Vector3(float.MaxValue)).IsResolved);
        Assert.False(GeometryInterval.Exact(double.MaxValue).EncloseSingleRounding().IsResolved);
    }

    [Fact]
    public void DirectionTransformationHasNoTranslationTerm()
    {
        GeometryVector value = RepresentedGeometryTransforms.Direction(QuarterTurn(), new Vector3(2, 3, 4));
        Contains(value, -3, 2, 4);
        Assert.False(default(GeometryVector).IsResolved);
    }

    static Matrix3x3 QuarterTurn() => new() { X = Vector3.UnitY, Y = -Vector3.UnitX, Z = Vector3.UnitZ };

    static void ContainsBackend(GeometryVector value, Matrix3x3 matrix, Vector3 translation, Vector3 local)
    {
        Matrix3x3.Transform(local, matrix, out Vector3 transformed);
        Vector3 actual = transformed + translation;
        Contains(value, actual.X, actual.Y, actual.Z);
    }

    static void Contains(GeometryVector value, double x, double y, double z)
    {
        Assert.True(value.IsResolved);
        Assert.True(value.X.Lower <= x && value.X.Upper >= x);
        Assert.True(value.Y.Lower <= y && value.Y.Upper >= y);
        Assert.True(value.Z.Lower <= z && value.Z.Upper >= z);
    }
}
