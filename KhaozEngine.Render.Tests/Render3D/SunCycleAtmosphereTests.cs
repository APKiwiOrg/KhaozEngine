using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class SunCycleAtmosphereTests
{
    [Fact]
    public void PhysicalSky_OverridesBackgroundAndAnchor()
    {
        var post = new PixelPostProcessSettings();
        post.Atmosphere.Enabled = true;
        Assert.False(post.Sky.Enabled);
        Assert.Equal(BackgroundMode.Sky, post.Background);
        post.Background = BackgroundMode.Sky;
        Assert.True(post.Atmosphere.Enabled);
        post.Background = BackgroundMode.Solid;
        Assert.False(post.Atmosphere.Enabled);
        post.Atmosphere.Enabled = true;
        post.Background = BackgroundMode.Starfield;
        Assert.False(post.Atmosphere.Enabled);
        post.Background = BackgroundMode.Sky;
        Assert.False(post.Atmosphere.Enabled);

        var sky = post.Sky;
        sky.SunDirectionOverride = Vector3.Normalize(new Vector3(-1, .1f, 0));
        sky.SunRadius = .01f;
        sky.ExtraDiscs.Add(new SkyDisc { Direction = Vector3.UnitZ, Radius = .02f, Color = Color.White });
        var view = Matrix4x4.CreateLookAt(new Vector3(0, 2, 9), new Vector3(-1, 2, 9), Vector3.UnitY);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3, 1.5f, .1f, 100);
        sky.Anchor = SunAnchor.StylizedBackdrop;
        sky.Horizon = SkyHorizon.Screen;
        var screen = SkyRenderer.PackUbo(sky, view, projection, -Vector3.UnitY, 480, 320, atmosphereEnabled: true);
        sky.Anchor = SunAnchor.World;
        sky.Horizon = SkyHorizon.World;
        var world = SkyRenderer.PackUbo(sky, view, projection, -Vector3.UnitY, 480, 320, atmosphereEnabled: true);
        Assert.Equal(2, world.Res.W);
        for (int i = 0; i < 2; i++)
        {
            Assert.Equal(world.DiscDirection[i], screen.DiscDirection[i]);
            Assert.Equal(world.DiscColor[i], screen.DiscColor[i]);
        }
        Assert.InRange(Vector4.Distance(new Vector4(-.9950372f, .09950372f, 0, .01f), world.DiscDirection[0]), 0, 1e-6f);
        Assert.Equal(new Vector4(Vector3.UnitZ, .02f), world.DiscDirection[1]);
        Assert.Equal(1, world.DiscHalo[0].Y);
        Assert.Equal(0, world.DiscHalo[1].Y);
        var legacy = SkyRenderer.PackUbo(sky, view, projection, -Vector3.UnitY, 480, 320);
        Assert.Equal(0, legacy.DiscHalo[0].Y);

        var ortho = Matrix4x4.CreateOrthographic(8, 6, .1f, 100);
        Assert.Equal(0, SkyRenderer.PackUbo(sky, view, ortho, -Vector3.UnitY, 480, 320, true).Res.W);
        sky.SunEnabled = false;
        var moonOnly = SkyRenderer.PackUbo(sky, view, projection, -Vector3.UnitY, 480, 320, true);
        Assert.Equal(1, moonOnly.Res.W);
        Assert.Equal(0, moonOnly.DiscHalo[0].Y);
        Assert.Equal(new Vector4(Vector3.UnitZ, .02f), moonOnly.DiscDirection[0]);
    }

    [Fact]
    public void SunDiscValue_NormalizesLimbAndAppliesTransmissionOpacityAndTintOnce()
    {
        var post = VacuumPost();
        post.Atmosphere.SunIlluminance = 2;
        post.Atmosphere.SunAngularRadiusRadians = .02f;
        var model = post.ResolveAtmosphereModel();
        var tint = new Vector3(.5f, .75f, 1);
        var frame = new AtmosphereFrame(Vector3.UnitY, -Vector3.UnitY, 0, tint, SunOpacity: .5f);
        float scale = 2 / MathF.Pow(MathF.Sin(.02f), 2);
        Close(tint * (scale * .625f), model.SunDiscValue(0, Vector3.UnitY, frame), .002f);
        var innerLimb = new Vector3(MathF.Sin(.015f), MathF.Cos(.015f), 0);
        // At three quarters of the angular radius, the normalized profile is 0.9960784.
        Close(tint * (scale * .5f * .9960784f), model.SunDiscValue(0, innerLimb, frame), .003f);
        Assert.Equal(Vector3.Zero, model.SunDiscValue(0, Vector3.UnitX, frame));
        Assert.Equal(Vector3.Zero, model.SunDiscValue(0, Vector3.UnitY, frame with { SunOpacity = 0 }));

        var earth = new AtmosphereModel(AtmosphereSettings.Earth());
        var unscaled = frame with { Tint = Vector3.One, SunOpacity = 1 };
        var centre = earth.SunDiscValue(.002f, Vector3.UnitY, unscaled);
        Assert.True(centre.X > centre.Z, "Rayleigh extinction must colour the solar disc.");
        Close(centre * tint * .5f, earth.SunDiscValue(.002f, Vector3.UnitY, frame), .005f);
    }

    [Fact]
    public void SunDiscValue_PlanetClipsTheLimbAndNegativeAltitudeUsesSurface()
    {
        var post = VacuumPost();
        post.Atmosphere.SunAngularRadiusRadians = .02f;
        var model = post.ResolveAtmosphereModel();
        var frame = new AtmosphereFrame(Vector3.UnitX, -Vector3.UnitX, 0, Vector3.One);
        var upper = new Vector3(MathF.Cos(.01f), MathF.Sin(.01f), 0);
        var lower = new Vector3(MathF.Cos(.01f), -MathF.Sin(.01f), 0);
        Assert.True(model.SunDiscValue(0, upper, frame).LengthSquared() > 0);
        Assert.Equal(Vector3.Zero, model.SunDiscValue(0, lower, frame));
        Assert.Equal(model.SunDiscValue(0, upper, frame), model.SunDiscValue(-1, upper, frame));
        Assert.Throws<ArgumentOutOfRangeException>(() => model.SunDiscValue(float.NaN, upper, frame));
        Assert.Throws<ArgumentException>(() => model.SunDiscValue(0, Vector3.Zero, frame));
    }

    [Fact]
    public void SunDiscValue_ClipsInsideThePlanetToleranceBandAtSurface()
    {
        var post = VacuumPost();
        post.Atmosphere.SunAngularRadiusRadians = .02f;
        var model = post.ResolveAtmosphereModel();
        var frame = new AtmosphereFrame(Vector3.UnitX, -Vector3.UnitX, 0, Vector3.One);
        float angle = .02865f * MathF.PI / 180;
        var upper = new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0);
        var lower = new Vector3(upper.X, -upper.Y, 0);
        Assert.Equal(Vector3.Zero, model.SunDiscValue(0, lower, frame));
        Assert.True(model.SunDiscValue(0, upper, frame).X > 0);
    }

    [Theory]
    [InlineData(.7803962f, 0f, true)]
    [InlineData(.7814696f, 0f, false)]
    [InlineData(.7836206f, 1000f, true)]
    [InlineData(.7850579f, 1000f, false)]
    [InlineData(.7803962f, -10f, true)]
    public void ApplyPhysical_DefaultCycleKeepsFullDiscOpacityUntilTheLimbSets(
        float time, float cameraHeight, bool limbVisible)
    {
        // Default latitude and declination put these evening centres at -0.1, -0.4, -1 and -1.4 degrees.
        // At 1 km the horizon dips about 1.016 degrees, extending visibility beyond the surface limb set.
        var raw = SunCycle.Evaluate(time, new SunCycleSettings());
        Assert.True(raw.SunElevationDegrees < 0);
        Assert.False(raw.SunEnabled);
        Assert.Equal(0, raw.SunColor.A);
        var camera = new Vector3(0, cameraHeight, 0);
        var view = Matrix4x4.CreateLookAt(camera, camera + raw.AtmosphereSample!.Value.SunDirection, Vector3.UnitY);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3, 1.5f, .1f, 100);
        var post = VacuumPost();
        var applied = SunCycle.Apply(raw, post, camera);
        var packed = SkyRenderer.PackUbo(post.Sky, view, projection, post.LightDirection, 480, 320, true);
        Assert.Equal(limbVisible, applied.SunEnabled);
        Assert.Equal(limbVisible, post.Sky.SunEnabled);
        Assert.Equal(limbVisible ? 1f : 0f, post.Sky.SunColor.A);
        Assert.Equal(limbVisible ? 1f : 0f, packed.Res.W);
        if (limbVisible)
        {
            Assert.Equal(1, packed.DiscHalo[0].Y);
            Assert.Equal(1, packed.DiscColor[0].W);
            var model = post.ResolveAtmosphereModel();
            var frame = AtmosphereRenderer.ResolveFrame(post, model);
            var lighting = model.EvaluateLighting(post.Atmosphere.HeightKm(cameraHeight), frame);
            var atmosphere = AtmosphereRenderer.PackUbo(post.Atmosphere, frame, lighting, camera,
                Vector3.Zero, view, projection, 480, 320, post.Hdr);
            Assert.Equal(1, atmosphere.SunT.W);
        }

        post.Atmosphere.Enabled = false;
        Assert.Equal(raw, SunCycle.Apply(raw, post, camera));
        var legacy = SkyRenderer.PackUbo(post.Sky, view, projection, post.LightDirection, 480, 320);
        Assert.False(post.Sky.SunEnabled);
        Assert.Equal(raw.SunColor, post.Sky.SunColor);
        Assert.Equal(0, legacy.Res.W);
    }

    [Fact]
    public void SunDiscValue_UpperLimbStaysRedAfterCentreSets()
    {
        var model = new AtmosphereModel(AtmosphereSettings.Earth());
        static Vector3 Direction(float elevationDegrees)
        {
            float angle = elevationDegrees * MathF.PI / 180;
            return new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0);
        }
        float horizonDegrees = (float)(-Math.Acos(6360 / 6360.002) * 180 / Math.PI);
        var frame = new AtmosphereFrame(Direction(horizonDegrees - .1f), -Vector3.UnitY, 0, Vector3.One);
        // The upper ray is 0.2 degrees from the centre, inside Earth's solar radius.
        var upper = model.SunDiscValue(.002f, Direction(horizonDegrees + .1f), frame);
        Assert.True(upper.X > 0, "The unoccluded upper limb must remain visible after the centre sets.");
        Assert.True(upper.X > upper.Y && upper.Y > upper.Z, "The remaining solar limb must redden.");
        Assert.Equal(Vector3.Zero, model.SunDiscValue(.002f, Direction(horizonDegrees - .2f), frame));
        Assert.Equal(Vector3.Zero, model.SunDiscValue(.002f, frame.SunDirection, frame));
    }

    [Fact]
    public void ApplyPhysical_IgnoresPalettesAndReturnsHorizon()
    {
        var cycle = Equatorial(NightKeyMode.Moon);
        var post = new PixelPostProcessSettings();
        post.Atmosphere.Enabled = true;
        var raw = SunCycle.Evaluate(.5f, cycle);
        var physical = SunCycle.Apply(raw, post, new Vector3(0, 2, 0));
        Assert.Equal(raw.LightDirection, physical.LightDirection);
        Assert.Equal(raw.ActiveSource, physical.ActiveSource);
        Assert.Equal(physical.HorizonColor, post.Sky.HorizonColor);
        Assert.Equal(physical.LightColor, post.LightColor);
        Assert.Equal(physical.AmbientColor, post.AmbientColor);
        Assert.Equal(physical.FillLightColor, post.FillLightColor);
        Assert.Equal(raw, post.AtmosphereCycle);
        Assert.NotEqual(raw.HorizonColor, physical.HorizonColor);

        cycle.DayPalette = cycle.DuskPalette = cycle.NightPalette = MagentaPalette();
        cycle.MoonKeyColor = cycle.MoonDiscColor = new Color(99, 0, 99, 0);
        var changed = SunCycle.Apply(SunCycle.Evaluate(.5f, cycle), post, new Vector3(0, 2, 0));
        AssertPhysicalColorsEqual(physical, changed);

        var night = SunCycle.Apply(SunCycle.Evaluate(0, cycle), post, new Vector3(0, 2, 0));
        cycle.DayPalette = SunCyclePalette.DefaultDay();
        cycle.DuskPalette = SunCyclePalette.DefaultDusk();
        cycle.NightPalette = SunCyclePalette.DefaultNight();
        cycle.MoonKeyColor = cycle.MoonDiscColor = new Color(0, 99, 0, 1);
        AssertPhysicalColorsEqual(night,
            SunCycle.Apply(SunCycle.Evaluate(0, cycle), post, new Vector3(0, 2, 0)));
    }

    [Fact]
    public void ApplyPhysical_PreservesAllNightKeyDirectionsAndFades()
    {
        var post = VacuumPost();
        post.Atmosphere.SunIlluminance = 2;
        post.Atmosphere.MoonIlluminanceFraction = .125f;
        post.FillLightDirection = new Vector3(1, -2, 3);
        foreach (var mode in new[] { NightKeyMode.None, NightKeyMode.AntiSolarMoon, NightKeyMode.Moon })
        {
            var cycle = Equatorial(mode);
            cycle.DayPalette.LightColor = cycle.DuskPalette.LightColor = cycle.NightPalette.LightColor
                = cycle.MoonKeyColor = new Color(1, 1, 1);
            foreach (float time in new[] { .24f, .25f, .26f, .74f, .75f, .76f, .249f, .251f, .749f, .751f })
            {
                var raw = SunCycle.Evaluate(time, cycle);
                var physical = SunCycle.Apply(raw, post, Vector3.Zero);
                var frame = post.ResolveAtmosphereModel().CreateFrame(raw);
                Assert.Equal(raw.LightDirection, physical.LightDirection);
                Assert.Equal(raw.ActiveSource, physical.ActiveSource);
                Assert.Equal(new Vector3(1, -2, 3), post.FillLightDirection);
                Assert.Equal(raw.LightColor.R, frame.KeyVisibility);
                var trueSun = SunCycle.SolarDirection(time, 0, 0, 0, out _);
                Assert.Equal(trueSun, frame.SunDirection);
                Assert.Equal(trueSun, physical.DiscDirectionOverride);
                Assert.Equal(mode == NightKeyMode.AntiSolarMoon ? -trueSun : -raw.MoonDirection,
                    frame.MoonDirection);
                Assert.Equal(mode == NightKeyMode.None ? 0 : .125f, frame.MoonFraction);
                var expectedSource = mode == NightKeyMode.AntiSolarMoon && raw.SunElevationDegrees <= 0
                    ? KeyLightSource.Moon : raw.ActiveSource;
                Assert.Equal(expectedSource, frame.KeySource);
                float sourceStrength = expectedSource == KeyLightSource.Sun ? 2
                    : expectedSource == KeyLightSource.Moon ? .25f : 0;
                Close(new Vector3(sourceStrength * frame.KeyVisibility), Rgb(physical.LightColor));
                if (time == .25f || time == .75f)
                    Assert.InRange(Rgb(physical.LightColor).Length(), 0, 1e-8f);
            }
        }
    }

    [Fact]
    public void CreateFrame_MoonDipUsesMinimumEnvelope()
    {
        var cycle = Equatorial(NightKeyMode.Moon);
        var model = new AtmosphereModel(AtmosphereSettings.Earth());
        var state = SunCycle.Evaluate(.249f, cycle);
        var frame = model.CreateFrame(state);
        // Equatorial opposition puts both elevations at 0.36 degrees. SmoothStep(0, 2, .36) is .085536.
        Assert.Equal(.085536f, frame.KeyVisibility, 4);
        cycle.MoonHorizonKeyDipDegrees = 4;
        frame = model.CreateFrame(SunCycle.Evaluate(.249f, cycle));
        Assert.Equal(.022842f, frame.KeyVisibility, 4);

        cycle = new SunCycleSettings { NightKey = NightKeyMode.Moon, MoonHourOffset = 8 };
        state = SunCycle.Evaluate(.2f, cycle);
        frame = model.CreateFrame(state);
        Assert.Equal(KeyLightSource.Moon, frame.KeySource);
        Assert.Equal(1f, frame.KeyVisibility);
    }

    [Theory]
    [InlineData(10f, 1f, 0f, 0f)]
    [InlineData(0f, 0f, 1f, 0f)]
    [InlineData(-12f, 0f, 0f, 1f)]
    [InlineData(5f, .5f, .5f, 0f)]
    [InlineData(-6f, 0f, .5f, .5f)]
    [InlineData(2.5f, .15625f, .84375f, 0f)]
    [InlineData(-3f, 0f, .84375f, .15625f)]
    public void CreateFrame_UsesLegacyElevationWeights(float elevation, float day, float dusk, float night)
    {
        var cycle = Equatorial(NightKeyMode.None);
        var settings = AtmosphereSettings.Earth();
        settings.DayTint = Vector3.UnitX;
        settings.DuskTint = Vector3.UnitY;
        settings.NightTint = Vector3.UnitZ;
        var model = new AtmosphereModel(settings);
        var state = SunCycle.Evaluate(EveningTime(elevation), cycle);
        var frame = model.CreateFrame(state);
        Close(new Vector3(day, dusk, night), frame.Tint, 1e-5f);
        Assert.True(state.AtmosphereSample.HasValue);
        Close(new Vector3(day, dusk, night), state.AtmosphereSample.Value.PaletteWeights, 1e-5f);
    }

    [Fact]
    public void ApplyPhysical_KeepsTrueSunPrimaryAndMoonExtraWithPhysicalDiscRadiance()
    {
        var post = VacuumPost();
        post.Atmosphere.SunIlluminance = 2;
        post.Atmosphere.MoonIlluminanceFraction = .125f;
        post.Atmosphere.SunAngularRadiusRadians = .02f;
        post.Atmosphere.DayTint = post.Atmosphere.DuskTint = post.Atmosphere.NightTint = new Vector3(.5f, .75f, 1);
        var cycle = Equatorial(NightKeyMode.Moon);
        cycle.MoonHourOffset = 16;
        cycle.DiscSetElevationDegrees = 6;
        cycle.DayPalette.SunColor = cycle.DuskPalette.SunColor = cycle.NightPalette.SunColor = new Color(99, 0, 99, 0);
        cycle.MoonDiscColor = new Color(99, 0, 99, 0);
        post.Sky.ExtraDiscs.Add(new SkyDisc());

        var state = SunCycle.Evaluate(.7f, cycle);
        var physical = SunCycle.Apply(state, post, new Vector3(0, 2, 0));
        Assert.Equal(-state.LightDirection, post.Sky.SunDirectionOverride);
        Assert.True(post.Sky.SunEnabled);
        float radiance = 2 / MathF.Pow(MathF.Sin(.02f), 2);
        Close(new Vector3(.5f, .75f, 1) * radiance, Rgb(post.Sky.SunColor), .002f);
        Assert.Equal(1, post.Sky.SunColor.A);
        var moon = Assert.Single(post.Sky.ExtraDiscs);
        Assert.Equal(-state.MoonDirection, moon.Direction);
        Close(new Vector3(.5f, .75f, 1) * (radiance * .125f), Rgb(moon.Color), .002f);
        Assert.Equal(.02f, moon.Radius);
        Assert.Equal(1, physical.ExtraDiscCount);
        Assert.Equal(moon.Color, physical.GetExtraDisc(0).Color);

        state = SunCycle.Evaluate(EveningTime(-3), cycle);
        SunCycle.Apply(state, post, new Vector3(0, 2, 0));
        Assert.False(post.Sky.SunEnabled);
        Assert.Equal(0, post.Sky.SunColor.A);
        Assert.Equal(state.AtmosphereSample!.Value.SunDirection, post.Sky.SunDirectionOverride);
        Assert.Single(post.Sky.ExtraDiscs);

        state = SunCycle.Evaluate(0, cycle);
        physical = SunCycle.Apply(state, post, new Vector3(0, 2, 0));
        Assert.Equal(KeyLightSource.Moon, physical.ActiveSource);
        Assert.False(post.Sky.SunEnabled);
        Assert.Equal(state.AtmosphereSample!.Value.SunDirection, post.Sky.SunDirectionOverride);
        Assert.Equal(-state.MoonDirection, Assert.Single(post.Sky.ExtraDiscs).Direction);
        cycle.NightKey = NightKeyMode.None;
        SunCycle.Apply(SunCycle.Evaluate(0, cycle), post, Vector3.Zero);
        Assert.Empty(post.Sky.ExtraDiscs);
    }

    [Fact]
    public void ApplyPhysical_UsesCachedTransmittanceForBothDiscs()
    {
        var post = new PixelPostProcessSettings();
        post.Atmosphere.Enabled = true;
        var cycle = Equatorial(NightKeyMode.Moon);
        cycle.MoonHourOffset = 16;
        var raw = SunCycle.Evaluate(.7f, cycle);
        SunCycle.Apply(raw, post, new Vector3(0, 2, 0));
        var model = post.ResolveAtmosphereModel();
        var frame = model.CreateFrame(raw);
        var lighting = model.EvaluateLighting(.002f, frame);
        float discScale = post.Atmosphere.SunIlluminance
            / MathF.Pow(MathF.Sin(post.Atmosphere.SunAngularRadiusRadians), 2);
        Close(lighting.SunTransmittance * discScale * frame.Tint, Rgb(post.Sky.SunColor), .01f);
        Close(lighting.MoonTransmittance * (discScale * frame.MoonFraction) * frame.Tint,
            Rgb(Assert.Single(post.Sky.ExtraDiscs).Color), .01f);
    }

    [Fact]
    public void ApplyPhysical_AdvancingCycleUsesCachedSkyBands()
    {
        var post = new PixelPostProcessSettings();
        post.Atmosphere.Enabled = true;
        post.Atmosphere.NightSkyFloor = 0;
        var cycle = Equatorial(NightKeyMode.Moon);
        cycle.MoonHourOffset = 16;
        var first = SunCycle.Apply(SunCycle.Evaluate(.5f, cycle), post, new Vector3(0, 2, 0));
        var model = post.ResolveAtmosphereModel();
        int scattering = model.ScatteringIntegrationCount;
        int tables = model.BuildCount, lightingTables = model.LightingBuildCount;
        Assert.True(scattering > 0);

        var next = SunCycle.Evaluate(.7f, cycle);
        var second = SunCycle.Apply(next, post, new Vector3(0, 2000, 0));
        Assert.NotEqual(first.ZenithColor, second.ZenithColor);
        Assert.NotEqual(first.GroundColor, second.GroundColor);
        Assert.Equal(scattering, model.ScatteringIntegrationCount);
        Assert.Equal(tables, model.BuildCount);
        Assert.Equal(lightingTables, model.LightingBuildCount);

        post.Atmosphere.SunIlluminance = 3;
        post.Atmosphere.DayTint = post.Atmosphere.DuskTint = post.Atmosphere.NightTint = new Vector3(.5f);
        post.Atmosphere.AmbientScale = post.Atmosphere.FillScale = 0;
        var scaled = SunCycle.Apply(next, post, new Vector3(0, 2000, 0));
        Close(Rgb(second.ZenithColor) * 1.5f, Rgb(scaled.ZenithColor));
        Close(Rgb(second.GroundColor) * 1.5f, Rgb(scaled.GroundColor));
        Assert.Equal(scattering, model.ScatteringIntegrationCount);

        post.Atmosphere.SunIlluminance = 0;
        post.Atmosphere.NightSkyFloor = .125f;
        var floored = SunCycle.Apply(next, post, new Vector3(0, 2000, 0));
        Assert.Equal(new Vector3(.0625f), Rgb(floored.ZenithColor));
        Assert.Equal(new Vector3(.0625f), Rgb(floored.GroundColor));
        Assert.Equal(scattering, model.ScatteringIntegrationCount);
        Assert.Equal(tables, model.BuildCount);
        Assert.Equal(lightingTables, model.LightingBuildCount);
    }

    [Fact]
    public void ApplyPhysical_CachedSkyBandsMatchDirectSkyAcrossElevationAndHeight()
    {
        var post = new PixelPostProcessSettings();
        post.Atmosphere.Enabled = true;
        post.Atmosphere.SunIlluminance = 2.5f;
        post.Atmosphere.MoonIlluminanceFraction = .125f;
        post.Atmosphere.NightSkyFloor = .002f;
        post.Atmosphere.DayTint = new Vector3(.7f, .9f, 1.2f);
        post.Atmosphere.DuskTint = new Vector3(1.1f, .8f, .6f);
        post.Atmosphere.NightTint = new Vector3(.6f, .8f, 1.1f);
        post.Atmosphere.AmbientScale = post.Atmosphere.FillScale = 0;
        var cycle = Equatorial(NightKeyMode.Moon);
        cycle.MoonHourOffset = 16;
        var model = post.ResolveAtmosphereModel();
        foreach (float elevation in new[] { 60f, 5f, -6f })
        {
            var raw = SunCycle.Evaluate(EveningTime(elevation), cycle);
            var frame = model.CreateFrame(raw);
            Assert.InRange(MathF.Abs(raw.SunElevationDegrees - elevation), 0, 1e-4f);
            foreach (float heightKm in new[] { .002f, 2f })
            {
                var physical = SunCycle.Apply(raw, post, new Vector3(0, heightKm * 1000, 0));
                CloseCachedSky(model.SkyValue(heightKm, Vector3.UnitY, frame), Rgb(physical.ZenithColor));
                CloseCachedSky(model.SkyValue(heightKm, -Vector3.UnitY, frame), Rgb(physical.GroundColor));
                Assert.Equal(physical.ZenithColor, post.Sky.ZenithColor);
                Assert.Equal(physical.GroundColor, post.Sky.GroundColor);
            }
        }
    }

    [Fact]
    public void ApplyPhysical_UpdatesLiveSettingsAndMapsAbsoluteCameraHeight()
    {
        var post = new PixelPostProcessSettings();
        post.Atmosphere.Enabled = true;
        post.Atmosphere.WorldUnitsPerKilometre = 100;
        var raw = SunCycle.Evaluate(.3f, Equatorial(NightKeyMode.Moon));
        var low = SunCycle.Apply(raw, post, new Vector3(70, 2, -90));
        var model = post.ResolveAtmosphereModel();
        var frame = model.CreateFrame(raw);
        Close(model.EvaluateLighting(.02f, frame).Horizon, Rgb(low.HorizonColor));
        int tables = model.BuildCount, lightingTables = model.LightingBuildCount;
        var high = SunCycle.Apply(raw, post, new Vector3(-70, 200, 90));
        Assert.NotEqual(low.LightColor, high.LightColor);
        Assert.NotEqual(low.HorizonColor, high.HorizonColor);
        Assert.Equal(tables, model.BuildCount);
        Assert.Equal(lightingTables, model.LightingBuildCount);

        post.Atmosphere.DayTint = post.Atmosphere.DuskTint = post.Atmosphere.NightTint = new Vector3(.5f);
        var tinted = SunCycle.Apply(high, post, new Vector3(0, 200, 0));
        Close(Rgb(high.LightColor) * .5f, Rgb(tinted.LightColor));
        Close(Rgb(high.HorizonColor) * .5f, Rgb(tinted.HorizonColor));
        Assert.Same(model, post.ResolveAtmosphereModel());
        Assert.Equal(tables, model.BuildCount);
        Assert.Equal(lightingTables, model.LightingBuildCount);

        post.Atmosphere.FillScale = 0;
        var noFill = SunCycle.Apply(raw, post, new Vector3(0, 200, 0));
        Assert.Equal(Vector3.Zero, Rgb(noFill.FillLightColor));
        Assert.Equal(tinted.GroundColor, noFill.GroundColor);
        post.Atmosphere.RayleighScatteringPerKm *= 2;
        var changed = SunCycle.Apply(raw, post, new Vector3(0, 200, 0));
        Assert.NotEqual(noFill.LightColor, changed.LightColor);
        Assert.NotEqual(noFill.ZenithColor, changed.ZenithColor);
        Assert.NotEqual(noFill.GroundColor, changed.GroundColor);
        Assert.Equal(tables + 1, model.BuildCount);
        Assert.Equal(lightingTables + 1, model.LightingBuildCount);
    }

    [Fact]
    public void ApplyPhysical_VoidOverloadUsesWorldZero()
    {
        var post = VacuumPost();
        var raw = SunCycle.Evaluate(.5f, Equatorial(NightKeyMode.Moon));
        SunCycle.Apply(raw, post);
        var key = post.LightColor;
        var horizon = post.Sky.HorizonColor;
        var sun = post.Sky.SunColor;
        var physical = SunCycle.Apply(raw, post, Vector3.Zero);
        Assert.Equal(key, physical.LightColor);
        Assert.Equal(horizon, physical.HorizonColor);
        Assert.Equal(sun, physical.SunColor);
    }

    [Fact]
    public void ApplyPhysical_RejectsUnsampledStateAndLeavesSettingsAlone()
    {
        var state = ManualState();
        var post = new PixelPostProcessSettings();
        post.Atmosphere.Enabled = true;
        var before = post.LightColor;
        var exception = Assert.Throws<InvalidOperationException>(() => SunCycle.Apply(state, post, Vector3.Zero));
        Assert.Contains("Evaluate", exception.Message);
        Assert.Equal(before, post.LightColor);
        Assert.Null(post.AtmosphereCycle);
        Assert.Throws<InvalidOperationException>(() => post.ResolveAtmosphereModel().CreateFrame(state));
    }

    [Fact]
    public void ApplyDisabled_PreservesLegacySettingsAndReturnsOriginalState()
    {
        var post = new PixelPostProcessSettings();
        post.Atmosphere.DayTint = new Vector3(-1);
        post.FillLightDirection = Vector3.UnitZ;
        post.Sky.SunRadius = .3f;
        post.Water.HorizonColor = new Color(9, 8, 7);
        foreach (var mode in new[] { NightKeyMode.None, NightKeyMode.AntiSolarMoon, NightKeyMode.Moon })
        {
            var cycle = Equatorial(mode);
            cycle.DiscSetElevationDegrees = 6;
            foreach (float time in new[] { 0, .24f, .25f, .5f, .74f, .75f, .76f })
                AssertLegacyApply(SunCycle.Evaluate(time, cycle), post);
        }
        AssertLegacyApply(ManualState(), post);
    }

    static void AssertLegacyApply(SunCycleState raw, PixelPostProcessSettings post)
    {
        var applied = SunCycle.Apply(raw, post, new Vector3(0, float.NaN, 0));
        Assert.Equal(raw, applied);
        Assert.Equal(raw.LightDirection, post.LightDirection);
        Assert.Equal(raw.LightColor, post.LightColor);
        Assert.Equal(raw.AmbientColor, post.AmbientColor);
        Assert.Equal(raw.FillLightColor, post.FillLightColor);
        Assert.Equal(raw.HorizonColor, post.Sky.HorizonColor);
        Assert.Equal(raw.ZenithColor, post.Sky.ZenithColor);
        Assert.Equal(raw.GroundColor, post.Sky.GroundColor);
        Assert.Equal(raw.SunColor, post.Sky.SunColor);
        Assert.Equal(raw.SunEnabled, post.Sky.SunEnabled);
        Assert.Equal(raw.DiscDirectionOverride, post.Sky.SunDirectionOverride);
        Assert.Equal(raw.ExtraDiscCount, post.Sky.ExtraDiscs.Count);
        for (int i = 0; i < raw.ExtraDiscCount; i++)
        {
            Assert.Equal(raw.GetExtraDisc(i).Direction, post.Sky.ExtraDiscs[i].Direction);
            Assert.Equal(raw.GetExtraDisc(i).Color, post.Sky.ExtraDiscs[i].Color);
            Assert.Equal(.3f, post.Sky.ExtraDiscs[i].Radius);
        }
        Assert.Equal(Vector3.UnitZ, post.FillLightDirection);
        Assert.Equal(.3f, post.Sky.SunRadius);
        Assert.Equal(new Color(9, 8, 7), post.Water.HorizonColor);
        Assert.Null(post.AtmosphereCycle);
    }

    static void AssertPhysicalColorsEqual(SunCycleState expected, SunCycleState actual)
    {
        Assert.Equal(expected.LightColor, actual.LightColor);
        Assert.Equal(expected.AmbientColor, actual.AmbientColor);
        Assert.Equal(expected.FillLightColor, actual.FillLightColor);
        Assert.Equal(expected.HorizonColor, actual.HorizonColor);
        Assert.Equal(expected.ZenithColor, actual.ZenithColor);
        Assert.Equal(expected.GroundColor, actual.GroundColor);
        Assert.Equal(expected.SunColor, actual.SunColor);
        Assert.Equal(expected.ExtraDiscCount, actual.ExtraDiscCount);
        for (int i = 0; i < expected.ExtraDiscCount; i++)
            Assert.Equal(expected.GetExtraDisc(i), actual.GetExtraDisc(i));
    }

    static SunCycleSettings Equatorial(NightKeyMode mode) => new()
    {
        LatitudeDegrees = 0,
        SolarDeclinationDegrees = 0,
        MoonDeclinationDegrees = 0,
        NightKey = mode,
    };

    static PixelPostProcessSettings VacuumPost()
    {
        var post = new PixelPostProcessSettings();
        post.Atmosphere.Enabled = true;
        post.Atmosphere.RayleighScatteringPerKm = post.Atmosphere.OzoneAbsorptionPerKm = Vector3.Zero;
        post.Atmosphere.MieScatteringPerKm = post.Atmosphere.MieExtinctionPerKm = post.Atmosphere.GroundAlbedo = 0;
        post.Atmosphere.DayTint = post.Atmosphere.DuskTint = post.Atmosphere.NightTint = Vector3.One;
        return post;
    }

    static SunCycleState ManualState() => new(Vector3.UnitY, 40, new Color(1, 2, 3), new Color(4, 5, 6),
        new Color(7, 8, 9), true, new Color(1, 3, 5), new Color(2, 4, 6), new Color(3, 5, 7));

    static SunCyclePalette MagentaPalette() => new()
    {
        HorizonColor = new Color(99, 0, 99),
        ZenithColor = new Color(99, 0, 99),
        SunColor = new Color(99, 0, 99, 0),
        LightColor = new Color(99, 0, 99),
        AmbientColor = new Color(99, 0, 99),
        FillColor = new Color(99, 0, 99),
        GroundColor = new Color(99, 0, 99),
    };

    static float EveningTime(float elevation) => .5f + MathF.Acos(MathF.Sin(elevation * MathF.PI / 180)) / MathF.Tau;
    static Vector3 Rgb(Color color) => new(color.R, color.G, color.B);
    static void CloseCachedSky(Vector3 expected, Vector3 actual)
    {
        // The 0 and 100/49 km slices bracket these heights. Allow 2% interpolation error,
        // plus 0.0001 Lambert units so channels near the sky floor have a useful absolute bound.
        for (int channel = 0; channel < 3; channel++)
            Assert.InRange(MathF.Abs(expected[channel] - actual[channel]), 0,
                MathF.Abs(expected[channel]) * .02f + .0001f);
    }

    static void Close(Vector3 expected, Vector3 actual, float tolerance = 1e-6f)
        => Assert.InRange(Vector3.Distance(expected, actual), 0, tolerance);
}
