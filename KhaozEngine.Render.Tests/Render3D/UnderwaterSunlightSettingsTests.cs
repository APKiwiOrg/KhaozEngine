using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class UnderwaterSunlightSettingsTests
{
    static Scene3D Scene() => new(new FakeGpuDevice(), new GpuOutputDescription(null, GpuPixelFormat.R8G8B8A8UNorm));

    [Fact]
    public void UnderwaterLightingIsOffByDefault()
    {
        var settings = new WaterSettings();
        Assert.False(settings.UnderwaterSunlight);
        Assert.Equal(0.03f, settings.UnderwaterPenumbraPerMetre);
        Assert.False(settings.UnderwaterCaustics);
        Assert.Equal(1f, settings.CausticStrength);
    }

    [Theory]
    [InlineData(false, 0.01f, true, 0.09f, 2f, 0f)]
    [InlineData(true, 0.07f, false, 0f, 0f, 4f)]
    public void LookOverridesAndInheritsUnderwaterLighting(bool sceneEnabled, float scenePenumbra,
        bool overrideEnabled, float overridePenumbra, float sceneStrength, float overrideStrength)
    {
        var scene = new WaterSettings
        {
            UnderwaterSunlight = sceneEnabled,
            UnderwaterPenumbraPerMetre = scenePenumbra,
            UnderwaterCaustics = sceneEnabled,
            CausticStrength = sceneStrength,
        };
        var scratch = new WaterSettings();
        var look = new WaterLook();
        Assert.Null(look.UnderwaterSunlight);
        Assert.Null(look.UnderwaterPenumbraPerMetre);
        Assert.Null(look.UnderwaterCaustics);
        Assert.Null(look.CausticStrength);

        look.UnderwaterSunlight = overrideEnabled;
        look.UnderwaterCaustics = overrideEnabled;
        WaterSettings resolved = look.ResolveInto(scratch, scene);
        Assert.Same(scratch, resolved);
        Assert.Equal(overrideEnabled, resolved.UnderwaterSunlight);
        Assert.Equal(scenePenumbra, resolved.UnderwaterPenumbraPerMetre);
        Assert.Equal(overrideEnabled, resolved.UnderwaterCaustics);
        Assert.Equal(sceneStrength, resolved.CausticStrength);

        look.UnderwaterSunlight = null;
        look.UnderwaterPenumbraPerMetre = overridePenumbra;
        look.UnderwaterCaustics = null;
        look.CausticStrength = overrideStrength;
        resolved = look.ResolveInto(scratch, scene);
        Assert.Equal(sceneEnabled, resolved.UnderwaterSunlight);
        Assert.Equal(overridePenumbra, resolved.UnderwaterPenumbraPerMetre);
        Assert.Equal(sceneEnabled, resolved.UnderwaterCaustics);
        Assert.Equal(overrideStrength, resolved.CausticStrength);

        look.UnderwaterSunlight = overrideEnabled;
        look.UnderwaterCaustics = overrideEnabled;
        resolved = look.ResolveInto(scratch, scene);
        Assert.Equal(overrideEnabled, resolved.UnderwaterSunlight);
        Assert.Equal(overridePenumbra, resolved.UnderwaterPenumbraPerMetre);
        Assert.Equal(overrideEnabled, resolved.UnderwaterCaustics);
        Assert.Equal(overrideStrength, resolved.CausticStrength);

        look.UnderwaterSunlight = null;
        look.UnderwaterPenumbraPerMetre = null;
        look.UnderwaterCaustics = null;
        look.CausticStrength = null;
        resolved = look.ResolveInto(scratch, scene);
        Assert.Equal(sceneEnabled, resolved.UnderwaterSunlight);
        Assert.Equal(scenePenumbra, resolved.UnderwaterPenumbraPerMetre);
        Assert.Equal(sceneEnabled, scene.UnderwaterSunlight);
        Assert.Equal(scenePenumbra, scene.UnderwaterPenumbraPerMetre);
        Assert.Equal(sceneEnabled, resolved.UnderwaterCaustics);
        Assert.Equal(sceneStrength, resolved.CausticStrength);
        Assert.Equal(sceneEnabled, scene.UnderwaterCaustics);
        Assert.Equal(sceneStrength, scene.CausticStrength);
    }

    [Theory]
    [InlineData(true, -0.01f)]
    [InlineData(true, float.NaN)]
    [InlineData(true, float.PositiveInfinity)]
    [InlineData(true, float.NegativeInfinity)]
    [InlineData(false, -0.01f)]
    [InlineData(false, float.NaN)]
    [InlineData(false, float.PositiveInfinity)]
    [InlineData(false, float.NegativeInfinity)]
    public void PrepareFrameValidatesOnlyEnabledCausticStrength(bool enabled, float strength)
    {
        using Scene3D scene = Scene();
        scene.Post.Water.UnderwaterCaustics = !enabled;
        var look = new WaterLook { UnderwaterCaustics = enabled, CausticStrength = 0f };
        scene.DrawWater(new WaterPlane(0f, 2f, 0f, 4f, look: look));
        look.CausticStrength = strength;

        if (enabled)
        {
            ArgumentException error = Assert.Throws<ArgumentException>(() => scene.PrepareFrame());
            Assert.Equal(nameof(WaterSettings.CausticStrength), error.ParamName);
            look.CausticStrength = 0f;
        }

        scene.PrepareFrame();
        Assert.Equal(1, scene.WaterPlaneCount);
    }

    [Fact]
    public void FloorYDefaultsToUnboundedAndSurvivesWithOptics()
    {
        var look = new WaterLook { UnderwaterSunlight = true };
        var plane = new WaterPlane(3f, 2f, 5f, 4f, 6f, look, floorY: -1f);
        var binding = new WaterOpticsBinding(new WaterOpticsHandle(Guid.NewGuid(), 0, 1),
            Vector3.Zero, new Vector2(20f));
        WaterPlane bound = plane.WithOptics(binding);

        Assert.Equal(-1f, bound.FloorY);
        Assert.Equal(binding, bound.Optics);
        Assert.Equal((3f, 2f, 5f, 4f, 6f),
            (bound.CenterX, bound.SurfaceY, bound.CenterZ, bound.HalfExtentX, bound.HalfExtentZ));
        Assert.Same(look, bound.Look);
        Assert.Equal(-1f, bound.WithOptics(default).FloorY);
        Assert.Null(new WaterPlane(0f, 2f, 0f, 4f).FloorY);
        Assert.Null(new WaterPlane(0f, 2f, 0f, 4f).WithOptics(binding).FloorY);
    }

    [Theory]
    [InlineData(0f, 4_194_301.75f)]
    [InlineData(4_194_304f, -2.25f)]
    public void RenderOriginSubtractsFromFloorY(float originY, float expectedFloor)
    {
        using Scene3D scene = Scene();
        scene.RenderOrigin = new Vector3(0f, originY, 0f);
        scene.Begin();
        var bounded = new WaterPlane(0f, 4_194_306f, 0f, 4f, floorY: 4_194_301.75f);
        scene.DrawWater(bounded);
        scene.DrawWater(new WaterPlane(0f, 4_194_306f, 0f, 4f));

        Assert.Equal(expectedFloor, scene.RelativeWaterPlanes()[0].FloorY);
        Assert.Null(scene.RelativeWaterPlanes()[1].FloorY);
        Assert.Equal(expectedFloor, scene.RelativeWaterPlanes()[0].FloorY);
        Assert.Equal(4_194_301.75f, bounded.FloorY);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(3f)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void DrawWaterRejectsAnInvalidFloor(float floor)
    {
        using Scene3D scene = Scene();
        var plane = new WaterPlane(0f, 2f, 0f, 4f, floorY: floor);

        ArgumentException error = Assert.Throws<ArgumentException>(() => scene.DrawWater(plane));
        Assert.Equal(nameof(WaterPlane.FloorY), error.ParamName);
        Assert.Equal(0, scene.WaterPlaneCount);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(-0.01f)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void DrawWaterRejectsAnInvalidPenumbraOverride(float value)
    {
        using Scene3D scene = Scene();
        var look = new WaterLook { UnderwaterPenumbraPerMetre = value };
        var plane = new WaterPlane(0f, 2f, 0f, 4f, look: look);

        ArgumentException error = Assert.Throws<ArgumentException>(() => scene.DrawWater(plane));
        Assert.Equal(nameof(WaterLook.UnderwaterPenumbraPerMetre), error.ParamName);
        Assert.Equal(0, scene.WaterPlaneCount);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(-0.01f)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void DrawWaterRejectsAnInvalidScenePenumbra(float value)
    {
        using Scene3D scene = Scene();
        scene.Post.Water.UnderwaterPenumbraPerMetre = value;

        ArgumentException error = Assert.Throws<ArgumentException>(() =>
            scene.DrawWater(new WaterPlane(0f, 2f, 0f, 4f)));
        Assert.Equal(nameof(WaterSettings.UnderwaterPenumbraPerMetre), error.ParamName);
        Assert.Equal(0, scene.WaterPlaneCount);
    }

    [Fact]
    public void DrawWaterAcceptsTheSurfaceAsFloorAndAZeroPenumbraOverride()
    {
        using Scene3D scene = Scene();
        scene.Post.Water.UnderwaterPenumbraPerMetre = float.NaN;
        var look = new WaterLook { UnderwaterPenumbraPerMetre = 0f };

        scene.DrawWater(new WaterPlane(0f, 2f, 0f, 4f, look: look, floorY: 2f));

        Assert.Equal(1, scene.WaterPlaneCount);
    }
}
