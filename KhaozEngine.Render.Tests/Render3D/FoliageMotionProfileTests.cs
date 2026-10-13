using System;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class FoliageMotionProfileTests
{
    [Fact]
    public void DefaultIsBladeButAuthoredZeroStaysZero()
    {
        var blade = default(FoliageMotionProfile);
        Assert.Equal(new Vector4(1f, .65f, 1f, 0f), blade.ToVector4());
        Assert.Equal(default, FoliageMotionProfile.Blade);
        Assert.Equal(blade, new FoliageMotionProfile(1f, .65f, 1f, 0f));
        Assert.Equal(1f, blade.BendScale);
        Assert.Equal(.65f, blade.MaxLean);
        Assert.Equal(1f, blade.WindScale);
        Assert.Equal(0f, blade.Shiver);

        var zero = new FoliageMotionProfile(0f, 0f, 0f, 0f);
        Assert.Equal(Vector4.Zero, zero.ToVector4());
        Assert.NotEqual(blade, zero);
        Assert.Equal(0f, zero.BendScale);
        Assert.Equal(0f, zero.MaxLean);
        Assert.Equal(0f, zero.WindScale);
        Assert.Equal(0f, zero.Shiver);
    }

    [Theory]
    [InlineData(0f, 0f, 0f, 0f)]
    [InlineData(.3f, .15f, .4f, .035f)]
    [InlineData(4f, 1f, 4f, .25f)]
    public void PackingPreservesTheFourCoefficients(float bendScale, float maxLean, float windScale, float shiver)
    {
        var profile = new FoliageMotionProfile(bendScale, maxLean, windScale, shiver);

        Assert.Equal(new Vector4(bendScale, maxLean, windScale, shiver), profile.ToVector4());
        Assert.Equal(bendScale, profile.BendScale);
        Assert.Equal(maxLean, profile.MaxLean);
        Assert.Equal(windScale, profile.WindScale);
        Assert.Equal(shiver, profile.Shiver);
    }

    [Theory]
    [InlineData(0, -.01f, "BendScale")]
    [InlineData(0, 4f + .01f, "BendScale")]
    [InlineData(0, float.NaN, "BendScale")]
    [InlineData(0, float.PositiveInfinity, "BendScale")]
    [InlineData(0, float.NegativeInfinity, "BendScale")]
    [InlineData(1, -.01f, "MaxLean")]
    [InlineData(1, 1f + .01f, "MaxLean")]
    [InlineData(1, float.NaN, "MaxLean")]
    [InlineData(1, float.PositiveInfinity, "MaxLean")]
    [InlineData(1, float.NegativeInfinity, "MaxLean")]
    [InlineData(2, -.01f, "WindScale")]
    [InlineData(2, 4f + .01f, "WindScale")]
    [InlineData(2, float.NaN, "WindScale")]
    [InlineData(2, float.PositiveInfinity, "WindScale")]
    [InlineData(2, float.NegativeInfinity, "WindScale")]
    [InlineData(3, -.01f, "Shiver")]
    [InlineData(3, .25f + .01f, "Shiver")]
    [InlineData(3, float.NaN, "Shiver")]
    [InlineData(3, float.PositiveInfinity, "Shiver")]
    [InlineData(3, float.NegativeInfinity, "Shiver")]
    public void InvalidCoefficientNamesItsField(int index, float invalidValue, string field)
    {
        var coefficients = new[] { 1f, .65f, 1f, 0f };
        coefficients[index] = invalidValue;

        var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new FoliageMotionProfile(coefficients[0], coefficients[1], coefficients[2], coefficients[3]));

        Assert.Equal(field, error.ParamName);
    }

    [Fact]
    public void ExistingInstanceConstructionKeepsBladeAndNoCasting()
    {
        var instance = new FoliageInstance(new MeshHandle(4), Matrix4x4.Identity, .1f);
        Assert.Equal(FoliageMotionProfile.Blade, instance.Motion);
        Assert.False(instance.CastsShadows);

        var (mesh, transform, rank) = instance;
        Assert.Equal((instance.Mesh, instance.Transform, instance.ThinningRank), (mesh, transform, rank));

        var motion = new FoliageMotionProfile(.3f, .15f, .4f, .035f);
        var authored = new FoliageInstance(mesh, transform, rank, motion, true);
        Assert.Equal(motion, authored.Motion);
        Assert.True(authored.CastsShadows);
    }
}
