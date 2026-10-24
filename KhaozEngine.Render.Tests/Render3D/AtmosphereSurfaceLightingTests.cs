using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class AtmosphereSurfaceLightingTests
{
    [Fact]
    public void UniformModeWritesTodaysAmbientAndFill()
    {
        var post = PhysicalPost();

        var applied = SunCycle.Apply(Noon(), post, Vector3.Zero);

        Assert.Equal(applied.AmbientColor, post.AmbientColor);
        Assert.Equal(applied.FillLightColor, post.FillLightColor);
        Assert.True(post.FillLightColor.R > 0);
        Assert.True(post.FillLightColor.G > 0);
        Assert.True(post.FillLightColor.B > 0);
        Assert.Equal(IrradianceSH.Zero, post.AmbientIrradiance);
    }

    [Fact]
    public void DirectionalModeMovesTheBounceIntoTheIrradiance()
    {
        var post = PhysicalPost();
        var state = Noon();
        var camera = new Vector3(0, 2000, 0);
        var uniform = SunCycle.Apply(state, post, camera);
        var model = post.ResolveAtmosphereModel();
        var frame = model.CreateFrame(state);
        float heightKm = post.Atmosphere.HeightKm(camera.Y);
        var expected = model.EvaluateIrradiance(heightKm, frame);
        post.IndirectDiffuse = IndirectDiffuseMode.Directional;

        var directional = SunCycle.Apply(state, post, camera);

        Assert.Equal(uniform, directional);
        Assert.Equal(uniform.AmbientColor, post.AmbientColor);
        Assert.Equal(new Color(0, 0, 0, uniform.FillLightColor.A), post.FillLightColor);
        Assert.Equal(expected, post.AmbientIrradiance);
        Assert.NotEqual(IrradianceSH.Zero, post.AmbientIrradiance);
    }

    [Fact]
    public void SwitchingBackToUniformRestoresTheFill()
    {
        var post = PhysicalPost();
        var state = Noon();
        var uniform = SunCycle.Apply(state, post, Vector3.Zero);
        post.IndirectDiffuse = IndirectDiffuseMode.Directional;
        SunCycle.Apply(state, post, Vector3.Zero);
        Assert.NotEqual(IrradianceSH.Zero, post.AmbientIrradiance);
        post.IndirectDiffuse = IndirectDiffuseMode.Uniform;

        SunCycle.Apply(state, post, Vector3.Zero);

        Assert.Equal(uniform.FillLightColor, post.FillLightColor);
        Assert.Equal(IrradianceSH.Zero, post.AmbientIrradiance);
    }

    [Theory]
    [InlineData(IndirectDiffuseMode.Uniform)]
    [InlineData(IndirectDiffuseMode.Directional)]
    public void SharedWriterPreservesCallerColorsAndFillAlpha(IndirectDiffuseMode mode)
    {
        var post = PhysicalPost();
        post.IndirectDiffuse = mode;
        post.AmbientIrradiance = IrradianceSH.Uniform(Vector3.One);
        var model = post.ResolveAtmosphereModel();
        var frame = new AtmosphereFrame(Vector3.Normalize(new Vector3(1, 1, 1)),
            -Vector3.UnitY, .2f, new Vector3(.5f, .75f, 1));
        var ambient = new Color(.25f, .3f, .4f, .6f);
        var fill = new Color(.7f, .5f, .2f, .35f);

        AtmosphereSurfaceLighting.Write(post, model, .4f, frame, ambient, fill);

        Assert.Equal(ambient, post.AmbientColor);
        Assert.Equal(mode == IndirectDiffuseMode.Uniform ? fill : new Color(0, 0, 0, fill.A),
            post.FillLightColor);
        Assert.Equal(mode == IndirectDiffuseMode.Uniform ? IrradianceSH.Zero
            : model.EvaluateIrradiance(.4f, frame), post.AmbientIrradiance);
    }

    static PixelPostProcessSettings PhysicalPost()
    {
        var post = new PixelPostProcessSettings();
        post.Atmosphere.Enabled = true;
        return post;
    }

    static SunCycleState Noon() => SunCycle.Evaluate(.5f, new SunCycleSettings
    {
        LatitudeDegrees = 0,
        SolarDeclinationDegrees = 0,
        MoonDeclinationDegrees = 0,
        NightKey = NightKeyMode.None,
    });
}
