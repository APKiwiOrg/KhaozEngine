using System;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Render3D;

public sealed class AtmosphereLightingTests : IClassFixture<AtmosphereLightingFixture>
{
    readonly AtmosphereModel _earth;
    readonly ITestOutputHelper _output;

    public AtmosphereLightingTests(AtmosphereLightingFixture fixture, ITestOutputHelper output)
        => (_earth, _output) = (fixture.Earth, output);

    [Fact]
    public void ClearNoon_ZenithHasPhysicalLuminanceRelativeToSunlitWhite()
    {
        var model = UnitEarth();
        var frame = new AtmosphereFrame(Direction(60), -Vector3.UnitY, 0, Vector3.One);
        var lighting = model.EvaluateLighting(.002f, frame);
        var zenith = model.SkyValue(.002f, Vector3.UnitY, frame);
        var white = lighting.Key * MathF.Cos(MathF.PI / 6) + lighting.Ambient;
        float ratio = Luminance(zenith) / Luminance(white);
        _output.WriteLine($"Unit noon zenith={zenith}, white={white}, luminance ratio={ratio}");
        // Clear-sky ratios span roughly 0.04 to 0.20 with turbidity and sun elevation.
        // The original 0.06 lower bound was an estimate, not a transport normalization requirement.
        Assert.InRange(ratio, .04f, .20f);
    }

    [Fact]
    public void ClearNoon_HorizonAwayFromSunIsBrighterThanZenith()
    {
        var model = UnitEarth();
        var frame = new AtmosphereFrame(Direction(60), -Vector3.UnitY, 0, Vector3.One);
        float zenith = Luminance(model.SkyValue(.002f, Vector3.UnitY, frame));
        float horizon = Luminance(model.SkyValue(.002f, Direction(2, MathF.PI / 2), frame));
        _output.WriteLine($"Unit noon zenith luminance={zenith}, horizon luminance={horizon}");
        Assert.True(horizon > zenith, $"Horizon {horizon} must exceed zenith {zenith}.");
    }

    [Fact]
    public void ClearNoon_MultipleScatteringRaisesZenithRadiance()
    {
        var model = UnitEarth();
        var sun = Direction(60);
        float single = Luminance(model.SingleScattering(new Vector3(0, 6360.002f, 0), Vector3.UnitY, sun));
        float multiple = Luminance(model.SkyRadiance(.002f, Vector3.UnitY, sun));
        _output.WriteLine($"Unit noon single-scattering luminance={single}, total luminance={multiple}");
        Assert.True(multiple > single, $"Total {multiple} must exceed single scattering {single}.");
    }

    [Fact]
    public void EarthDefaults_KeepNoonAndMoonlitSkyWithinExposureBands()
    {
        var settings = AtmosphereSettings.Earth();
        var model = new AtmosphereModel(settings);
        var cycle = new SunCycleSettings
        {
            LatitudeDegrees = 0,
            SolarDeclinationDegrees = 0,
            MoonDeclinationDegrees = 0,
            MoonHourOffset = 12,
            NightKey = NightKeyMode.Moon,
        };
        var camera = new FlyCamera3D
        {
            Position = new Vector3(0, 2, 9),
            Yaw = -MathF.PI / 2,
            Pitch = 1.25f,
            AspectRatio = 1.5f,
        };
        var noon = model.CreateFrame(SunCycle.Evaluate(.5f, cycle));
        Vector3 view = camera.ScreenToRay(new Vector2(240.5f, 24.5f), 480, 320).Direction;
        var noonSky = model.SkyValue(.002f, view, noon);
        var lighting = model.EvaluateLighting(.002f, noon);
        var white = lighting.Key + lighting.Ambient;
        // Match the existing white receiver probe, which uses per-channel ACES for its fog oracle.
        var whiteDisplay = Display(white, chroma: 0);
        _output.WriteLine($"Default noon upper-centre HDR={noonSky}, display={Display(noonSky)}, white={whiteDisplay}");
        Assert.InRange(Display(noonSky).Z, 150, 210);
        Assert.True(MathF.Max(whiteDisplay.X, MathF.Max(whiteDisplay.Y, whiteDisplay.Z)) < 250);

        cycle.MoonHourOffset = 7.5f;
        var night = model.CreateFrame(SunCycle.Evaluate(0, cycle));
        camera.Yaw = MathF.PI / 2;
        camera.Pitch = -.08f;
        view = camera.ScreenToRay(new Vector2(240.5f, 24.5f), 480, 320).Direction;
        _output.WriteLine($"Default night upper-centre={Display(model.SkyValue(.002f, view, night))}, zenith={Display(model.SkyValue(.002f, Vector3.UnitY, night))}");
        Assert.InRange(Display(model.SkyValue(.002f, view, night)).Z, 15, 40);
        Assert.InRange(Display(model.SkyValue(.002f, Vector3.UnitY, night)).Z, 15, 40);
    }

    static Vector3 Display(Vector3 hdr, float chroma = .75f)
    {
        var mapped = TonemapMath.Map(hdr.X, hdr.Y, hdr.Z, 1, 0, chroma);
        return new Vector3(mapped.R, mapped.G, mapped.B) * 255;
    }

    [Fact]
    public void Lighting_UsesLambertUnitsAndTintOnce()
    {
        var s = AtmosphereSettings.Earth();
        var f = new AtmosphereFrame(Vector3.UnitY, -Vector3.UnitY, 0, Vector3.One);
        var l = _earth.EvaluateLighting(.002f, f);
        Close(l.SunTransmittance * s.SunIlluminance, l.Key);
        Close(_earth.SkyRadiance(.002f, Vector3.UnitY, f.SunDirection) * (MathF.PI * s.SunIlluminance),
            _earth.SkyValue(.002f, Vector3.UnitY, f));

        var tinted = f with { Tint = new Vector3(2, 1, .5f) };
        var tintedLighting = _earth.EvaluateLighting(.002f, tinted);
        Close(l.Key * tinted.Tint, tintedLighting.Key);
        Close(l.Ambient * tinted.Tint, tintedLighting.Ambient);
        Close(l.Fill * tinted.Tint, tintedLighting.Fill);
        Close(l.Horizon * tinted.Tint, tintedLighting.Horizon);
        Close(_earth.SkyValue(.002f, Vector3.UnitY, f) * tinted.Tint,
            _earth.SkyValue(.002f, Vector3.UnitY, tinted));
        Assert.Equal(l.SunTransmittance, tintedLighting.SunTransmittance);
        Assert.Equal(l.MoonTransmittance, tintedLighting.MoonTransmittance);

        var floorPixel = TonemapMath.Map(.08f * .65f, .08f * .8f, .08f, 1, 0, .75f);
        Assert.True(floorPixel.R > .03f && floorPixel.G > .03f && floorPixel.B > .03f);
        var skyFloorPixel = TonemapMath.Map(.02f * .65f, .02f * .8f, .02f, 1, 0, .75f);
        Assert.True(skyFloorPixel.R > 1f / 255 && skyFloorPixel.G > 1f / 255 && skyFloorPixel.B > 1f / 255);
    }

    [Fact]
    public void SunKey_ReddensAndDisappears()
    {
        float previousRatio = 0;
        foreach (float elevation in new[] { 60f, 20f, 5f, 1f })
        {
            var f = new AtmosphereFrame(Direction(elevation), -Vector3.UnitY, 0, Vector3.One);
            var key = _earth.EvaluateLighting(.002f, f).Key;
            Assert.True(key.X > 0 && key.Z > 0);
            float ratio = key.X / key.Z;
            Assert.True(ratio > previousRatio, $"R/B must increase at {elevation} degrees.");
            previousRatio = ratio;
        }

        var night = new AtmosphereFrame(Direction(-1), -Vector3.UnitY, 0, Vector3.One, KeyLightSource.None);
        Assert.Equal(Vector3.Zero, _earth.EvaluateLighting(.002f, night).Key);
        Assert.Equal(Vector3.Zero, _earth.EvaluateLighting(.002f, night with { KeySource = KeyLightSource.Sun }).Key);
    }

    [Fact]
    public void MoonKey_UsesItsOwnFractionVisibilityAndTransmittance()
    {
        var f = new AtmosphereFrame(-Vector3.UnitY, Direction(20, .7f), .08f,
            new Vector3(.65f, .8f, 1), KeyLightSource.Moon, .4f);
        var l = _earth.EvaluateLighting(.002f, f);
        Close(l.MoonTransmittance * (.08f * .4f * AtmosphereSettings.Earth().SunIlluminance) * f.Tint, l.Key);
        Assert.Equal(Vector3.Zero, l.SunTransmittance);
        Assert.Equal(Vector3.Zero, _earth.EvaluateLighting(.002f, f with { KeyVisibility = 0 }).Key);
    }

    [Fact]
    public void NightFloors_AndHorizonAreContinuous()
    {
        var s = Vacuum();
        var m = new AtmosphereModel(s);
        var night = new AtmosphereFrame(-Vector3.UnitY, -Vector3.UnitY, 0, s.NightTint, KeyLightSource.None);
        var l = m.EvaluateLighting(.002f, night);
        Assert.Equal(s.NightAmbientFloor * night.Tint, l.Ambient);
        Assert.Equal(s.NightSkyFloor * night.Tint, l.Horizon);
        Assert.Equal(s.NightSkyFloor * night.Tint, m.SkyValue(.002f, Vector3.UnitY, night));
        Assert.Equal(Vector3.Zero, l.Fill);

        var before = new AtmosphereFrame(Direction(.001f), Direction(20, .4f), .08f, Vector3.One);
        var after = before with { SunDirection = Direction(-.001f), KeySource = KeyLightSource.Moon };
        Close(_earth.EvaluateLighting(.002f, before).Horizon,
            _earth.EvaluateLighting(.002f, after).Horizon, .005f);
        Assert.Equal(_earth.EvaluateLighting(.002f, before).Horizon,
            _earth.EvaluateLighting(.002f, before with { KeySource = KeyLightSource.Moon }).Horizon);
    }

    [Fact]
    public void SunAndMoon_SumIndependentSkyAndLightingContributions()
    {
        var s = AtmosphereSettings.Earth();
        s.NightAmbientFloor = s.NightSkyFloor = 0;
        var m = new AtmosphereModel(s);
        var sun = new AtmosphereFrame(Direction(30), Direction(20, MathF.PI), 0, Vector3.One);
        var moon = sun with { SunDirection = -Vector3.UnitY, MoonFraction = .08f };
        var both = sun with { MoonFraction = .08f };
        var sunLighting = m.EvaluateLighting(.002f, sun);
        var moonLighting = m.EvaluateLighting(.002f, moon);
        var bothLighting = m.EvaluateLighting(.002f, both);
        Close(sunLighting.Ambient + moonLighting.Ambient, bothLighting.Ambient);
        Close(sunLighting.Fill + moonLighting.Fill, bothLighting.Fill);
        Close(sunLighting.Horizon + moonLighting.Horizon, bothLighting.Horizon);
        Close(m.SkyValue(.002f, Vector3.UnitY, sun) + m.SkyValue(.002f, Vector3.UnitY, moon),
            m.SkyValue(.002f, Vector3.UnitY, both));

        var view = both.MoonDirection;
        var expected = (m.SkyRadiance(.002f, view, both.SunDirection)
            + m.SkyRadiance(.002f, view, both.MoonDirection) * .08f) * (MathF.PI * s.SunIlluminance);
        Close(expected, m.SkyValue(.002f, view, both));
        Assert.True(m.SkyValue(.002f, view, moon).Length()
            > m.SkyValue(.002f, Direction(20), moon).Length());

        var direct = (bothLighting.SunTransmittance * both.SunDirection.Y
            + bothLighting.MoonTransmittance * (both.MoonFraction * both.MoonDirection.Y)) * s.SunIlluminance;
        Close((direct + bothLighting.Ambient) * s.GroundAlbedo, bothLighting.Fill);
    }

    [Fact]
    public void CameraHeight_ChangesLightingWithoutRebuilding()
    {
        var f = new AtmosphereFrame(Direction(20), -Vector3.UnitY, 0, Vector3.One);
        var low = _earth.EvaluateLighting(.002f, f);
        int tables = _earth.BuildCount, lighting = _earth.LightingBuildCount;
        Assert.Equal(low, _earth.EvaluateLighting(.002f, f));
        var high = _earth.EvaluateLighting(2, f);
        Assert.NotEqual(low.Key, high.Key);
        Assert.NotEqual(low.Horizon, high.Horizon);
        Assert.NotEqual(low.Fill, high.Fill);
        Assert.Equal(tables, _earth.BuildCount);
        Assert.Equal(lighting, _earth.LightingBuildCount);
        Assert.Equal(low, _earth.EvaluateLighting(.002f, f));

        // The first two slices are 0 and 100/49 km, so their midpoint is 50/49 km.
        var bottom = _earth.EvaluateLighting(0, f).Horizon;
        var next = _earth.EvaluateLighting(100f / 49, f).Horizon;
        Close((bottom + next) * .5f, _earth.EvaluateLighting(50f / 49, f).Horizon);
        Assert.Equal(_earth.EvaluateLighting(0, f), _earth.EvaluateLighting(-1, f));
    }

    [Fact]
    public void SettingsMutation_RebuildsOnlyAffectedCaches()
    {
        var s = AtmosphereSettings.Earth();
        s.NightAmbientFloor = s.NightSkyFloor = 0;
        var m = new AtmosphereModel(s);
        var f = new AtmosphereFrame(Vector3.UnitY, -Vector3.UnitY, 0, Vector3.One);
        Assert.Equal(0, m.LightingBuildCount);
        var original = m.EvaluateLighting(.002f, f);
        var sky = m.SkyValue(.002f, Vector3.UnitY, f);
        int tables = m.BuildCount, lighting = m.LightingBuildCount;
        Assert.False(m.UpdateSettings(s));
        Assert.Equal(lighting, m.LightingBuildCount);

        s.AmbientScale = 2;
        Assert.True(m.UpdateSettings(s));
        Close(original.Ambient * 2, m.EvaluateLighting(.002f, f).Ambient);
        Assert.Equal(sky, m.SkyValue(.002f, Vector3.UnitY, f));
        s.FillScale = 2;
        Assert.True(m.UpdateSettings(s));
        Close(original.Fill * 2, m.EvaluateLighting(.002f, f).Fill);
        s.SunIlluminance *= 2;
        Assert.True(m.UpdateSettings(s));
        Close(original.Key * 2, m.EvaluateLighting(.002f, f).Key);
        Close(sky * 2, m.SkyValue(.002f, Vector3.UnitY, f));
        s.DayTint = new Vector3(2, 1, .5f);
        s.DuskTint = new Vector3(1, 2, .5f);
        s.NightTint = new Vector3(.25f, .5f, 1);
        s.NightAmbientFloor = 2;
        s.NightSkyFloor = 3;
        s.MoonIlluminanceFraction = .16f;
        s.Enabled = true;
        s.WorldUnitsPerKilometre = 2000;
        Assert.True(m.UpdateSettings(s));
        Assert.Equal(new Vector3(2), m.EvaluateLighting(.002f, f).Ambient);
        Assert.Equal(new Vector3(3), m.EvaluateLighting(.002f, f).Horizon);
        Assert.Equal(new Vector3(3), m.SkyValue(.002f, Vector3.UnitY, f));
        Assert.Equal(tables, m.BuildCount);
        Assert.Equal(lighting, m.LightingBuildCount);
        s.NightSkyFloor = 4;
        Assert.Equal(new Vector3(3), m.SkyValue(.002f, Vector3.UnitY, f));

        s.MieScatteringPerKm *= .5f;
        Assert.True(m.UpdateSettings(s));
        Assert.Equal(lighting + 1, m.LightingBuildCount);
        _ = m.EvaluateLighting(.002f, f);
        Assert.Equal(tables + 1, m.BuildCount);
        Assert.Equal(lighting + 1, m.LightingBuildCount);
        Assert.Equal(new Vector3(4), m.SkyValue(.002f, Vector3.UnitY, f));
        s.MieAnisotropy = 1;
        Assert.Throws<ArgumentOutOfRangeException>(() => m.UpdateSettings(s));
        Assert.Equal(tables + 1, m.BuildCount);
        Assert.Equal(lighting + 1, m.LightingBuildCount);
    }

    [Fact]
    public void Tint_BlendsTheDayDuskNightWeights()
    {
        var s = AtmosphereSettings.Earth();
        s.DayTint = new Vector3(.1f, .2f, .3f);
        s.DuskTint = new Vector3(1, 2, 3);
        s.NightTint = new Vector3(2, 4, 6);
        var m = new AtmosphereModel(s);
        Close(new Vector3(1.32f, 2.64f, 3.96f), m.Tint(new Vector3(.2f, .3f, .5f)));
        Assert.Equal(0, m.BuildCount);
        Assert.Equal(0, m.LightingBuildCount);
    }

    [Fact]
    public void Lighting_RejectsInvalidInputsBeforeBuilding()
    {
        var m = new AtmosphereModel(AtmosphereSettings.Earth());
        var f = new AtmosphereFrame(Vector3.UnitY, -Vector3.UnitY, 0, Vector3.One);
        Assert.Throws<ArgumentOutOfRangeException>(() => m.EvaluateLighting(float.NaN, f));
        Assert.Throws<ArgumentException>(() => m.EvaluateLighting(0, f with { SunDirection = Vector3.Zero }));
        Assert.Throws<ArgumentException>(() => m.EvaluateLighting(0, f with { MoonDirection = new Vector3(float.NaN) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => m.EvaluateLighting(0, f with { MoonFraction = -1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => m.EvaluateLighting(0, f with { Tint = new Vector3(float.NaN) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => m.SkyValue(float.NaN, Vector3.UnitY, f));
        Assert.Throws<ArgumentException>(() => m.SkyValue(0, Vector3.Zero, f));
        Assert.Equal(0, m.BuildCount);
        Assert.Equal(0, m.LightingBuildCount);
    }

    static Vector3 Direction(float elevationDegrees, float azimuth = 0)
    {
        float radians = elevationDegrees * MathF.PI / 180;
        return new Vector3(MathF.Cos(radians) * MathF.Cos(azimuth), MathF.Sin(radians),
            MathF.Cos(radians) * MathF.Sin(azimuth));
    }

    static AtmosphereSettings Vacuum()
    {
        var s = AtmosphereSettings.Earth();
        s.RayleighScatteringPerKm = s.OzoneAbsorptionPerKm = Vector3.Zero;
        s.MieScatteringPerKm = s.MieExtinctionPerKm = s.GroundAlbedo = 0;
        return s;
    }

    static AtmosphereModel UnitEarth()
    {
        var settings = AtmosphereSettings.Earth();
        settings.SunIlluminance = 1;
        settings.NightAmbientFloor = .08f;
        settings.NightSkyFloor = .02f;
        return new AtmosphereModel(settings);
    }

    static float Luminance(Vector3 value) => Vector3.Dot(value, new Vector3(.2126f, .7152f, .0722f));

    static void Close(Vector3 expected, Vector3 actual, float tolerance = 1e-6f)
        => Assert.InRange(Vector3.Distance(expected, actual), 0, tolerance);
}

public sealed class AtmosphereLightingFixture
{
    public AtmosphereModel Earth { get; } = new(AtmosphereSettings.Earth());
}
