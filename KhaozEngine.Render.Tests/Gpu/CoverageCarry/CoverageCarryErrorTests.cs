using System;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class CoverageCarryErrorTests
{
    [Fact]
    public void Vector_errors_retain_maxima_and_report_undefined_zero_reference_relative_errors()
    {
        var expected = new ModelVertex[1];
        expected[0].Position = new(3, 4, 0);
        expected[0].Normal = Vector3.UnitY;
        expected[0].Tangent = new(0, 0, 0, -1);
        uint[] actual = CoverageCarryReferenceTests.Words(expected);
        actual[2] = BitConverter.SingleToUInt32Bits(5);
        actual[3] = BitConverter.SingleToUInt32Bits(1);
        actual[12] = BitConverter.SingleToUInt32Bits(2);
        var error = new CoverageCarryError(7);
        error.Include(expected, actual);
        error.Include(expected, CoverageCarryReferenceTests.Words(expected));
        Assert.Equal(7, error.Actor);
        Assert.Equal(2, error.ComparedVertices);
        Assert.Equal(5, error.Position.MaximumAbsolute);
        Assert.Equal(1, error.Position.MaximumRelative);
        Assert.Equal(1, error.Normal.MaximumAbsolute);
        Assert.Equal(1, error.Normal.MaximumRelative);
        Assert.Equal(2, error.Tangent.MaximumAbsolute);
        Assert.Equal(0, error.Tangent.MaximumRelative);
        Assert.Equal(1, error.Tangent.ZeroReferenceNonzeroErrors);
        Assert.Equal(0, error.TangentHandednessAbsolute);
    }

    [Fact]
    public void Nonfinite_comparisons_are_refused_instead_of_becoming_zero_error()
    {
        var expected = new ModelVertex[1];
        uint[] actual = new uint[16];
        actual[0] = 0x7fc00000;
        Assert.Throws<InvalidOperationException>(() => new CoverageCarryError(0).Include(expected, actual));
    }

    [Fact]
    public void Half_means_keep_chronological_order_and_every_odd_sample()
    {
        var halves = CoverageCarryMeasurement.HalfMeans([10, 8, 2, 4, 6]);
        Assert.Equal(2, halves.FirstHalfCount);
        Assert.Equal(3, halves.SecondHalfCount);
        Assert.Equal(9, halves.FirstHalfMean);
        Assert.Equal(4, halves.SecondHalfMean);
        Assert.Equal(4d / 9, halves.SecondOverFirst);
    }
}
