using System;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class WaterColumnSettingsTests
{
    [Fact]
    public void ColumnFieldsInheritOverrideCopyAndReset()
    {
        Assert.False(new WaterSettings().WaterColumnLight);
        Assert.Equal(new Color(0.10f, 0.13f, 0.10f, 1f), new WaterSettings().WaterColor);
        Assert.Equal(2f, new WaterSettings().Clarity);
        Assert.Null(new WaterLook().WaterColumnLight);
        Assert.Null(new WaterLook().WaterColor);
        Assert.Null(new WaterLook().Clarity);

        var scratch = new WaterSettings();
        var look = new WaterLook();
        WaterSettings resolved = look.ResolveInto(scratch, new WaterSettings());
        Assert.Same(scratch, resolved);
        Assert.False(resolved.WaterColumnLight);
        Assert.Equal(new Color(0.10f, 0.13f, 0.10f, 1f), resolved.WaterColor);
        Assert.Equal(2f, resolved.Clarity);
        Assert.False(new WaterLook { WaterColumnLight = false }
            .ResolveInto(scratch, new WaterSettings { WaterColumnLight = true }).WaterColumnLight);
        Assert.Equal(7f, new WaterLook().ResolveInto(scratch, new WaterSettings { Clarity = 7f }).Clarity);

        var scene = new WaterSettings
        {
            WaterColumnLight = true,
            WaterColor = new Color(0.2f, 0.3f, 0.4f, 0.5f),
            Clarity = 7f,
        };
        resolved = look.ResolveInto(scratch, scene);
        Assert.True(resolved.WaterColumnLight);
        Assert.Equal(new Color(0.2f, 0.3f, 0.4f, 0.5f), resolved.WaterColor);
        Assert.Equal(7f, resolved.Clarity);

        look.WaterColumnLight = false;
        look.WaterColor = new Color(0.6f, 0.7f, 0.8f, 0.9f);
        look.Clarity = 4f;
        resolved = look.ResolveInto(scratch, scene);
        Assert.False(resolved.WaterColumnLight);
        Assert.Equal(new Color(0.6f, 0.7f, 0.8f, 0.9f), resolved.WaterColor);
        Assert.Equal(4f, resolved.Clarity);
        Assert.True(scene.WaterColumnLight);
        Assert.Equal(new Color(0.2f, 0.3f, 0.4f, 0.5f), scene.WaterColor);
        Assert.Equal(7f, scene.Clarity);

        var copy = new WaterSettings();
        copy.CopyFrom(resolved);
        look.WaterColumnLight = null;
        look.WaterColor = null;
        look.Clarity = null;
        resolved = look.ResolveInto(scratch, scene);
        Assert.True(resolved.WaterColumnLight);
        Assert.Equal(new Color(0.2f, 0.3f, 0.4f, 0.5f), resolved.WaterColor);
        Assert.Equal(7f, resolved.Clarity);
        Assert.False(copy.WaterColumnLight);
        Assert.Equal(new Color(0.6f, 0.7f, 0.8f, 0.9f), copy.WaterColor);
        Assert.Equal(4f, copy.Clarity);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void DrawWaterRejectsInvalidResolvedColumnInputs(bool enabled, bool useLook)
    {
        using var device = new FakeGpuDevice();
        using var scene = new Scene3D(device, new(null, GpuPixelFormat.R8G8B8A8UNorm));
        WaterSettings settings = scene.Post.Water;
        settings.WaterColumnLight = useLook ? !enabled : enabled;
        WaterLook? look = useLook ? new WaterLook { WaterColumnLight = enabled } : null;
        var plane = new WaterPlane(0f, 0f, 0f, 1f, look: look);

        foreach (float invalid in new[] { 0f, -1f, float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            if (look is null) settings.Clarity = invalid;
            else look.Clarity = invalid;
            scene.Begin();
            if (enabled)
            {
                ArgumentException clarityError = Assert.Throws<ArgumentException>(() => scene.DrawWater(plane));
                Assert.Equal("Clarity", clarityError.ParamName);
                Assert.Equal(0, scene.WaterPlaneCount);
            }
            else
            {
                scene.DrawWater(plane);
                Assert.Equal(1, scene.WaterPlaneCount);
            }
        }

        if (look is null) settings.Clarity = 2f;
        else look.Clarity = 2f;
        foreach (float invalid in new[] { -0.01f, 1.01f, float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            foreach (Color color in new[]
                     {
                         new Color(invalid, 0.3f, 0.4f),
                         new Color(0.2f, invalid, 0.4f),
                         new Color(0.2f, 0.3f, invalid),
                     })
            {
                if (look is null) settings.WaterColor = color;
                else look.WaterColor = color;
                scene.Begin();
                if (enabled)
                {
                    ArgumentException colorError = Assert.Throws<ArgumentException>(() => scene.DrawWater(plane));
                    Assert.Equal("WaterColor", colorError.ParamName);
                    Assert.Equal(0, scene.WaterPlaneCount);
                }
                else
                {
                    scene.DrawWater(plane);
                    Assert.Equal(1, scene.WaterPlaneCount);
                }
            }
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ClarityBelowOneMillimetreIsRejectedBeforeEnqueue(bool enabled, bool useLook)
    {
        using var device = new FakeGpuDevice();
        using var scene = new Scene3D(device, new(null, GpuPixelFormat.R8G8B8A8UNorm));
        WaterSettings settings = scene.Post.Water;
        settings.WaterColumnLight = useLook ? !enabled : enabled;
        WaterLook? look = useLook ? new WaterLook { WaterColumnLight = enabled } : null;
        var plane = new WaterPlane(0f, 0f, 0f, 1f, look: look);

        foreach (float clarity in new[] { 0.000999f, float.Epsilon })
        {
            if (look is null) settings.Clarity = clarity;
            else look.Clarity = clarity;
            scene.Begin();
            if (enabled)
            {
                ArgumentException error = Assert.Throws<ArgumentException>(() => scene.DrawWater(plane));
                Assert.Equal("Clarity", error.ParamName);
                Assert.Equal(0, scene.WaterPlaneCount);
            }
            else
            {
                scene.DrawWater(plane);
                Assert.Equal(1, scene.WaterPlaneCount);
            }
        }

        if (look is null) settings.Clarity = 0.001f;
        else look.Clarity = 0.001f;
        scene.Begin();
        scene.DrawWater(plane);
        Assert.Equal(1, scene.WaterPlaneCount);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    [InlineData(-1f)]
    [InlineData(2f)]
    public void DrawWaterAcceptsRgbEndpointsAndIgnoresAlpha(float alpha)
    {
        using var device = new FakeGpuDevice();
        using var scene = new Scene3D(device, new(null, GpuPixelFormat.R8G8B8A8UNorm));
        scene.Post.Water.WaterColumnLight = true;
        scene.Post.Water.WaterColor = new Color(0f, 1f, 0f, alpha);
        scene.DrawWater(new WaterPlane(0f, 0f, 0f, 1f));
        scene.Post.Water.WaterColor = new Color(1f, 0f, 1f, alpha);
        scene.DrawWater(new WaterPlane(2f, 0f, 0f, 1f));
        Assert.Equal(2, scene.WaterPlaneCount);
    }

    [Fact]
    public void DrawWaterValidLookOverridesInvalidSceneValues()
    {
        using var device = new FakeGpuDevice();
        using var scene = new Scene3D(device, new(null, GpuPixelFormat.R8G8B8A8UNorm));
        scene.Post.Water.WaterColumnLight = true;
        scene.Post.Water.WaterColor = new Color(float.NaN, float.PositiveInfinity, -0.01f);
        scene.Post.Water.Clarity = float.NaN;
        var look = new WaterLook
        {
            WaterColor = new Color(0.2f, 0.3f, 0.4f),
            Clarity = 7f,
        };
        var plane = new WaterPlane(0f, 0f, 0f, 1f, look: look);
        scene.DrawWater(plane);
        Assert.Equal(1, scene.WaterPlaneCount);

        scene.Begin();
        look.Clarity = null;
        ArgumentException clarityError = Assert.Throws<ArgumentException>(() => scene.DrawWater(plane));
        Assert.Equal("Clarity", clarityError.ParamName);
        Assert.Equal(0, scene.WaterPlaneCount);

        look.Clarity = 7f;
        look.WaterColor = null;
        ArgumentException colorError = Assert.Throws<ArgumentException>(() => scene.DrawWater(plane));
        Assert.Equal("WaterColor", colorError.ParamName);
        Assert.Equal(0, scene.WaterPlaneCount);

        look.WaterColumnLight = false;
        scene.DrawWater(plane);
        Assert.Equal(1, scene.WaterPlaneCount);
    }
}
