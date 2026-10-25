using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class WaterOpticsCoverageTests
{
    static readonly WaterOpticsHandle Handle = new(Guid.NewGuid(), 0, 1);
    static WaterOpticsBinding Binding() => new(Handle, new Vector3(-5f, 7f, -3f), new Vector2(10f, 6f));
    static WaterSettings Flat() => new() { SwellAmplitude = 0f };
    static Scene3D Scene() => new(new FakeGpuDevice(), new GpuOutputDescription(null, GpuPixelFormat.R8G8B8A8UNorm));

    [Fact]
    public void ExactRectangleEdgesHaveCompleteInterpolationSupport()
    {
        WaterOpticsCoverage.Validate(new WaterPlane(0f, 9f, 0f, 5f, 3f, optics: Binding()), Flat());
    }

    [Theory]
    [InlineData(-0.01f, 0f, 5f, 3f)]
    [InlineData(0.01f, 0f, 5f, 3f)]
    [InlineData(0f, -0.01f, 5f, 3f)]
    [InlineData(0f, 0.01f, 5f, 3f)]
    [InlineData(0f, 0f, 5.01f, 3f)]
    [InlineData(0f, 0f, 5f, 3.01f)]
    public void IncompleteCoverageIsRefused(float x, float z, float halfX, float halfZ)
    {
        var plane = new WaterPlane(x, 7f, z, halfX, halfZ, optics: Binding());
        Assert.Throws<ArgumentException>(() => WaterOpticsCoverage.Validate(plane, Flat()));
    }

    [Theory]
    [InlineData(float.NaN, 7f, 0f, 5f)]
    [InlineData(0f, float.PositiveInfinity, 0f, 5f)]
    [InlineData(0f, 7f, float.NegativeInfinity, 5f)]
    [InlineData(0f, 7f, 0f, float.NaN)]
    [InlineData(0f, 7f, 0f, 0f)]
    [InlineData(0f, 7f, 0f, -1f)]
    public void InvalidBoundGeometryIsRefused(float x, float y, float z, float half)
    {
        var plane = new WaterPlane(x, y, z, half, optics: Binding());
        Assert.Throws<ArgumentException>(() => WaterOpticsCoverage.Validate(plane, Flat()));
    }

    [Fact]
    public void DefaultLegacyBindingPreservesUnboundFftAndGeometryBehavior()
    {
        var plane = new WaterPlane(float.NaN, 7f, 0f, -1f);
        Assert.False(plane.Optics.IsBound);
        Assert.False(default(WaterOpticsHandle).IsValid);
        WaterOpticsCoverage.Validate(plane, new WaterSettings { WaveSource = WaterWaveSource.FftOcean });
        using Scene3D scene = Scene();
        scene.DrawWater(plane);
        Assert.Equal(1, scene.WaterPlaneCount);
        Assert.Equal(default, scene.WaterOpticsStats);
    }

    [Fact]
    public void BoundFftIsRefusedEvenWhenItsHeightOrProceduralSwellIsZero()
    {
        var plane = new WaterPlane(0f, 7f, 0f, 1f, optics: Binding());
        var settings = new WaterSettings { WaveSource = WaterWaveSource.FftOcean, SwellAmplitude = 0f };
        Assert.Throws<NotSupportedException>(() => WaterOpticsCoverage.Validate(plane, settings));
        using Scene3D scene = Scene();
        scene.Post.Water.WaveSource = WaterWaveSource.FftOcean;
        Assert.Throws<NotSupportedException>(() => scene.GetWaterOpticsPadding());
    }

    [Fact]
    public void PaddingPreflightRefusesUncertifiedDisplacementWithoutThrowing()
    {
        using Scene3D scene = Scene();
        var swell = new WaterLook { SwellAmplitude = 0.01f, SwellWavelength = 2f * MathF.PI, SwellSteepness = -0.5f };
        Assert.True(scene.TryGetWaterOpticsPadding(swell, out float padding, out _));
        Assert.Equal(scene.GetWaterOpticsPadding(swell), padding);
        var fft = new WaterLook { WaveSource = WaterWaveSource.FftOcean, SwellAmplitude = 0f };
        Assert.False(scene.TryGetWaterOpticsPadding(fft, out padding, out WaterOpticsSurfaceFailure reason));
        Assert.Equal((0f, WaterOpticsSurfaceFailure.UncertifiedDisplacement), (padding, reason));
        Assert.Throws<NotSupportedException>(() => scene.GetWaterOpticsPadding(fft));
        var nonFinite = new WaterLook { SwellAmplitude = float.NaN };
        Assert.False(scene.TryGetWaterOpticsPadding(nonFinite, out padding, out reason));
        Assert.Equal((0f, WaterOpticsSurfaceFailure.UncertifiedDisplacement), (padding, reason));
        Assert.Throws<ArgumentException>(() => scene.GetWaterOpticsPadding(nonFinite));
        scene.Post.Water.WaveSource = WaterWaveSource.FftOcean;
        Assert.False(scene.TryGetWaterOpticsPadding(null, out _, out _));
    }

    [Fact]
    public void EffectivePerPlaneSwellUsesOrbitalReachRatherThanAmplitude()
    {
        using Scene3D scene = Scene();
        scene.Post.Water.SwellAmplitude = 0f;
        scene.Post.Water.SwellWavelength = 100f;
        scene.Post.Water.SwellSteepness = 1f;
        WaterOpticsHandle handle = scene.LoadWaterOpticsField(new WaterOpticsField(2, 2, [2f, 2f, 2f, 2f]));
        var binding = new WaterOpticsBinding(handle, new Vector3(-2f, 7f, -2f), new Vector2(4f));
        var swell = new WaterLook { SwellAmplitude = 0.01f, SwellWavelength = 2f * MathF.PI, SwellSteepness = -0.5f };
        Assert.Equal(0.5f, scene.GetWaterOpticsPadding(swell), 6);
        Assert.Equal(0f, scene.GetWaterOpticsPadding());
        scene.DrawWater(new WaterPlane(0f, 7f, 0f, 1.49f, look: swell, optics: binding));
        Assert.Throws<ArgumentException>(() => scene.DrawWater(
            new WaterPlane(0f, 7f, 0f, 1.51f, look: swell, optics: binding)));
        swell.SwellAmplitude = 0f;
        scene.DrawWater(new WaterPlane(0f, 7f, 0f, 2f, look: swell, optics: binding));
        Assert.Equal(2, scene.WaterPlaneCount);
    }

    [Fact]
    public void PlaneCanOptOutOfSceneFftButCannotOptIntoBoundFft()
    {
        using Scene3D scene = Scene();
        scene.Post.Water.WaveSource = WaterWaveSource.FftOcean;
        scene.Post.Water.SwellAmplitude = 0f;
        WaterOpticsHandle handle = scene.LoadWaterOpticsField(new WaterOpticsField(2, 2, [2f, 2f, 2f, 2f]));
        var binding = new WaterOpticsBinding(handle, new Vector3(-5f, 7f, -3f), new Vector2(10f, 6f));
        var look = new WaterLook { WaveSource = WaterWaveSource.Procedural };
        Assert.Equal(0f, scene.GetWaterOpticsPadding(look));
        scene.DrawWater(new WaterPlane(0f, 7f, 0f, 5f, 3f, look, binding));
        look.WaveSource = WaterWaveSource.FftOcean;
        Assert.Throws<NotSupportedException>(() => scene.DrawWater(new WaterPlane(0f, 7f, 0f, 1f, look: look, optics: binding)));
        Assert.Equal(1, scene.WaterPlaneCount);
    }

    [Fact]
    public void InvalidDisplacementCannotBeTreatedAsNoPadding()
    {
        var plane = new WaterPlane(0f, 7f, 0f, 1f, optics: Binding());
        Assert.Throws<ArgumentException>(() => WaterOpticsCoverage.Validate(plane,
            new WaterSettings { SwellAmplitude = float.NaN }));
        Assert.Throws<ArgumentException>(() => WaterOpticsCoverage.Validate(plane,
            new WaterSettings { SwellSteepness = float.PositiveInfinity }));
        Assert.Throws<ArgumentException>(() => WaterOpticsCoverage.Validate(plane,
            new WaterSettings { SwellWavelength = float.MaxValue, SwellSteepness = float.MaxValue }));
    }

    [Fact]
    public void BindingConstructorRefusesInvalidMappings()
    {
        Assert.Throws<ArgumentException>(() => new WaterOpticsBinding(default, Vector3.Zero, Vector2.One));
        Assert.Throws<ArgumentException>(() => new WaterOpticsBinding(new(Guid.Empty, 0, 1), Vector3.Zero, Vector2.One));
        Assert.Throws<ArgumentException>(() => new WaterOpticsBinding(new(Handle.Owner, -1, 1), Vector3.Zero, Vector2.One));
        Assert.Throws<ArgumentException>(() => new WaterOpticsBinding(new(Handle.Owner, 0, -1), Vector3.Zero, Vector2.One));
        Assert.Throws<ArgumentException>(() => new WaterOpticsBinding(Handle, new Vector3(0f, float.NaN, 0f), Vector2.One));
        Assert.Throws<ArgumentException>(() => new WaterOpticsBinding(Handle, new Vector3(float.PositiveInfinity, 0f, 0f), Vector2.One));
        Assert.Throws<ArgumentException>(() => new WaterOpticsBinding(Handle, new Vector3(0f, 0f, float.NegativeInfinity), Vector2.One));
        Assert.Throws<ArgumentException>(() => new WaterOpticsBinding(Handle, Vector3.Zero, new Vector2(0f, 1f)));
        Assert.Throws<ArgumentException>(() => new WaterOpticsBinding(Handle, Vector3.Zero, new Vector2(1f, -1f)));
        Assert.Throws<ArgumentException>(() => new WaterOpticsBinding(Handle, Vector3.Zero, new Vector2(float.NaN, 1f)));
        Assert.Throws<ArgumentException>(() => new WaterOpticsBinding(Handle, Vector3.Zero, new Vector2(1f, float.PositiveInfinity)));
    }

    [Theory]
    [InlineData(0f, 0f, 0f)]
    [InlineData(100_096f, 13f, -99_968f)]
    public void BindingSurvivesOriginReduction(float ox, float oy, float oz)
    {
        using Scene3D scene = Scene();
        scene.RenderOrigin = new Vector3(ox, oy, oz);
        scene.Post.Water.SwellAmplitude = 0f;
        WaterOpticsHandle handle = scene.LoadWaterOpticsField(new WaterOpticsField(2, 2, [2f, -1f, 30f, 100f]));
        var look = new WaterLook { FoamStrength = 0.25f };
        var original = new WaterPlane(100_100f, 17f, -99_960f, 3f, 4f, look);
        var binding = new WaterOpticsBinding(handle, new Vector3(100_095f, 15f, -99_965f), new Vector2(10f, 12f));
        WaterPlane plane = original.WithOptics(binding);
        scene.Begin();
        scene.DrawWater(plane);
        WaterPlane relative = scene.RelativeWaterPlanes()[0];
        WaterPlane again = scene.RelativeWaterPlanes()[0];

        Assert.Equal(100_100f - ox, relative.CenterX);
        Assert.Equal(17f - oy, relative.SurfaceY);
        Assert.Equal(-99_960f - oz, relative.CenterZ);
        Assert.Equal(3f, relative.HalfExtentX);
        Assert.Equal(4f, relative.HalfExtentZ);
        Assert.Same(look, relative.Look);
        Assert.True(relative.Optics.IsBound);
        Assert.Equal(handle, relative.Optics.Field);
        Assert.Equal(new Vector3(100_095f - ox, 15f - oy, -99_965f - oz), relative.Optics.Origin);
        Assert.Equal(new Vector2(10f, 12f), relative.Optics.Size);
        Assert.Equal(relative.Optics.Origin, again.Optics.Origin);
        Assert.Equal(binding.Origin, plane.Optics.Origin);
        Assert.False(original.Optics.IsBound);
        WaterOpticsCoverage.Validate(relative, Flat());
    }

    [Fact]
    public void ReleasedHandleAndLateSubmissionNeverEnterTheQueue()
    {
        using Scene3D scene = Scene();
        scene.Post.Water.SwellAmplitude = 0f;
        WaterOpticsHandle handle = scene.LoadWaterOpticsField(new WaterOpticsField(2, 2, [2f, 2f, 2f, 2f]));
        var plane = new WaterPlane(0f, 7f, 0f, 1f,
            optics: new WaterOpticsBinding(handle, new Vector3(-2f, 7f, -2f), new Vector2(4f)));
        scene.UnloadWaterOpticsField(handle);
        Assert.Throws<ArgumentException>(() => scene.DrawWater(plane));
        Assert.Equal(0, scene.WaterPlaneCount);
        scene.PrepareFrame();
        Assert.Throws<InvalidOperationException>(() => scene.DrawWater(new WaterPlane(0f, 0f, 0f, 1f)));
        Assert.Equal(0, scene.WaterPlaneCount);
    }
}
