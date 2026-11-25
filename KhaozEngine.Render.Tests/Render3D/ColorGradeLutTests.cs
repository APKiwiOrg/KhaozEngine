using System;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class ColorGradeLutTests
{
    [Fact]
    public void IdentityLatticeIsRecognizedWithoutTreatingASingleChangedSampleAsIdentity()
    {
        Vector3[] lattice = new Vector3[ColorGradeLut.SampleCount];
        for (int b = 0, i = 0; b < 32; ++b)
        for (int g = 0; g < 32; ++g)
        for (int r = 0; r < 32; ++r)
            lattice[i++] = new Vector3(r / 31f, g / 31f, b / 31f);

        var identity = new ColorGradeLut(lattice);
        lattice[^1] = new Vector3(0.9f);
        Assert.True(identity.IsIdentity);
        Assert.False(new ColorGradeLut(lattice).IsIdentity);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(-0.01f)]
    [InlineData(1.01f)]
    public void CubeRejectsInvalidDisplayValues(float component)
    {
        Vector3[] lattice = new Vector3[ColorGradeLut.SampleCount];
        lattice[11] = new Vector3(component);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ColorGradeLut(lattice));
    }

    [Fact]
    public void CubeRejectsAShortLattice() =>
        Assert.Throws<ArgumentException>(() => new ColorGradeLut(new Vector3[31 * 32 * 32]));
}
