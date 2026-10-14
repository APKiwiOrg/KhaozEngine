using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class AtmosphereSettingsTests
{
    [Fact]
    public void EarthPreset_IsDisabledAndExact()
    {
        var s = AtmosphereSettings.Earth();

        Assert.False(s.Enabled);
        Assert.False(new PixelPostProcessSettings().Atmosphere.Enabled);
        Assert.Equal(6360f, s.PlanetRadiusKm);
        Assert.Equal(100f, s.AtmosphereHeightKm);
        Assert.Equal(new Vector3(.005802f, .013558f, .0331f), s.RayleighScatteringPerKm);
        Assert.Equal(8f, s.RayleighScaleHeightKm);
        Assert.Equal(.003996f, s.MieScatteringPerKm);
        Assert.Equal(.004440f, s.MieExtinctionPerKm);
        Assert.Equal(1.2f, s.MieScaleHeightKm);
        Assert.Equal(.8f, s.MieAnisotropy);
        Assert.Equal(new Vector3(.000650f, .001881f, .000085f), s.OzoneAbsorptionPerKm);
        Assert.Equal(25f, s.OzoneCenterKm);
        Assert.Equal(30f, s.OzoneWidthKm);
        Assert.Equal(.3f, s.GroundAlbedo);
        Assert.Equal(2.7f, s.SunIlluminance);
        Assert.Equal(.04f, s.MoonIlluminanceFraction);
        Assert.Equal(.004675f, s.SunAngularRadiusRadians);
        Assert.Equal(1000f, s.WorldUnitsPerKilometre);
        Assert.Equal(20f, s.AerialDistanceScale);
        Assert.Equal(0f, s.AerialOnsetDistance);
        Assert.Equal(1f, s.AmbientScale);
        Assert.Equal(1f, s.FillScale);
        Assert.Equal(.05f, s.NightAmbientFloor);
        Assert.Equal(.09f, s.NightSkyFloor);
        Assert.Equal(0f, s.HazeDensityPerKm);
        Assert.Equal(.25f, s.HazeScaleHeightKm);
        Assert.Equal(Vector3.One, s.DayTint);
        Assert.Equal(Vector3.One, s.DuskTint);
        Assert.Equal(new Vector3(.45f, .65f, 1f), s.NightTint);
        Assert.Equal(1f, s.HeightKm(1000f));
        s.Validate();

        Assert.True(s.ValueEquals(new AtmosphereSettings()));
        Assert.True(s.ValueEquals(new PixelPostProcessSettings().Atmosphere));
    }

    [Fact]
    public void EachPostBagAndEarthPresetOwnsItsSettings()
    {
        var first = new PixelPostProcessSettings();
        var second = new PixelPostProcessSettings();
        first.Atmosphere.SunIlluminance = 2f;
        Assert.Equal(2.7f, second.Atmosphere.SunIlluminance);

        var earth = AtmosphereSettings.Earth();
        earth.PlanetRadiusKm = 5000f;
        Assert.Equal(6360f, AtmosphereSettings.Earth().PlanetRadiusKm);
    }

    [Theory]
    [InlineData(1000f, 0f, 0f)]
    [InlineData(1000f, 1000f, 1f)]
    [InlineData(1000f, -1000f, -1f)]
    [InlineData(100f, 150f, 1.5f)]
    public void HeightKm_UsesAbsoluteWorldYAndConfiguredUnits(float units, float absoluteY, float expectedKm)
    {
        var s = AtmosphereSettings.Earth();
        s.WorldUnitsPerKilometre = units;
        Assert.Equal(expectedKm, s.HeightKm(absoluteY));
    }

    [Theory]
    [MemberData(nameof(NonFiniteScalarSettings))]
    [InlineData(nameof(AtmosphereSettings.PlanetRadiusKm), 0f)]
    [InlineData(nameof(AtmosphereSettings.PlanetRadiusKm), -1f)]
    [InlineData(nameof(AtmosphereSettings.AtmosphereHeightKm), 0f)]
    [InlineData(nameof(AtmosphereSettings.RayleighScaleHeightKm), 0f)]
    [InlineData(nameof(AtmosphereSettings.MieScaleHeightKm), 0f)]
    [InlineData(nameof(AtmosphereSettings.OzoneCenterKm), 0f)]
    [InlineData(nameof(AtmosphereSettings.OzoneWidthKm), 0f)]
    [InlineData(nameof(AtmosphereSettings.WorldUnitsPerKilometre), 0f)]
    [InlineData(nameof(AtmosphereSettings.HazeScaleHeightKm), 0f)]
    [InlineData(nameof(AtmosphereSettings.MieScatteringPerKm), -1f)]
    [InlineData(nameof(AtmosphereSettings.MieExtinctionPerKm), -1f)]
    [InlineData(nameof(AtmosphereSettings.MieExtinctionPerKm), .003f)]
    [InlineData(nameof(AtmosphereSettings.MieAnisotropy), 1f)]
    [InlineData(nameof(AtmosphereSettings.MieAnisotropy), -1f)]
    [InlineData(nameof(AtmosphereSettings.GroundAlbedo), -.01f)]
    [InlineData(nameof(AtmosphereSettings.GroundAlbedo), 1.01f)]
    [InlineData(nameof(AtmosphereSettings.SunIlluminance), -1f)]
    [InlineData(nameof(AtmosphereSettings.MoonIlluminanceFraction), -1f)]
    [InlineData(nameof(AtmosphereSettings.SunAngularRadiusRadians), 0f)]
    [InlineData(nameof(AtmosphereSettings.SunAngularRadiusRadians), .1001f)]
    [InlineData(nameof(AtmosphereSettings.AerialDistanceScale), -1f)]
    [InlineData(nameof(AtmosphereSettings.AerialOnsetDistance), -1f)]
    [InlineData(nameof(AtmosphereSettings.AmbientScale), -1f)]
    [InlineData(nameof(AtmosphereSettings.FillScale), -1f)]
    [InlineData(nameof(AtmosphereSettings.NightAmbientFloor), -1f)]
    [InlineData(nameof(AtmosphereSettings.NightSkyFloor), -1f)]
    [InlineData(nameof(AtmosphereSettings.HazeDensityPerKm), -1f)]
    public void InvalidSettings_AreRejected(string propertyName, float value)
    {
        var s = AtmosphereSettings.Earth();
        typeof(AtmosphereSettings).GetProperty(propertyName)!.SetValue(s, value);
        var error = Assert.Throws<ArgumentOutOfRangeException>(s.Validate);
        Assert.Equal(propertyName, error.ParamName);
    }

    public static IEnumerable<object[]> NonFiniteScalarSettings()
    {
        foreach (var property in typeof(AtmosphereSettings).GetProperties())
        {
            if (property.PropertyType != typeof(float)) continue;
            yield return new object[] { property.Name, float.NaN };
            yield return new object[] { property.Name, float.PositiveInfinity };
            yield return new object[] { property.Name, float.NegativeInfinity };
        }
    }

    [Theory]
    [MemberData(nameof(InvalidRgbSettings))]
    public void InvalidRgbSettings_AreRejected(string propertyName, int component, float value)
    {
        var s = AtmosphereSettings.Earth();
        var rgb = Vector3.One;
        if (component == 0) rgb.X = value;
        else if (component == 1) rgb.Y = value;
        else rgb.Z = value;
        typeof(AtmosphereSettings).GetProperty(propertyName)!.SetValue(s, rgb);
        var error = Assert.Throws<ArgumentOutOfRangeException>(s.Validate);
        Assert.Equal(propertyName, error.ParamName);
    }

    public static IEnumerable<object[]> InvalidRgbSettings()
    {
        foreach (var property in typeof(AtmosphereSettings).GetProperties())
        {
            if (property.PropertyType != typeof(Vector3)) continue;
            for (int component = 0; component < 3; component++)
            {
                yield return new object[] { property.Name, component, -1f };
                yield return new object[] { property.Name, component, float.NaN };
                yield return new object[] { property.Name, component, float.PositiveInfinity };
                yield return new object[] { property.Name, component, float.NegativeInfinity };
            }
        }
    }

    [Fact]
    public void ZeroCoefficientsIlluminanceAndArtScales_AreValid()
    {
        var s = new AtmosphereSettings
        {
            Enabled = true,
            RayleighScatteringPerKm = Vector3.Zero,
            MieScatteringPerKm = 0f,
            MieExtinctionPerKm = 0f,
            OzoneAbsorptionPerKm = Vector3.Zero,
            GroundAlbedo = 0f,
            SunIlluminance = 0f,
            MoonIlluminanceFraction = 0f,
            AerialDistanceScale = 0f,
            AmbientScale = 0f,
            FillScale = 0f,
            NightAmbientFloor = 0f,
            NightSkyFloor = 0f,
            HazeDensityPerKm = 0f,
            DayTint = Vector3.Zero,
            DuskTint = Vector3.Zero,
            NightTint = Vector3.Zero,
        };

        s.Validate();
    }

    [Theory]
    [InlineData(-.999f, 0f, .1f)]
    [InlineData(.999f, 1f, .001f)]
    public void ValidRangeBoundaries_AreAccepted(float anisotropy, float albedo, float angularRadius)
    {
        var s = AtmosphereSettings.Earth();
        s.MieAnisotropy = anisotropy;
        s.GroundAlbedo = albedo;
        s.SunAngularRadiusRadians = angularRadius;
        s.MieExtinctionPerKm = s.MieScatteringPerKm;
        s.Validate();
    }

    [Fact]
    public void ValueEquals_ComparesIndependentSettingsByValue()
    {
        var s = AtmosphereSettings.Earth();
        var copy = s.Clone();

        Assert.NotSame(s, copy);
        Assert.True(s.ValueEquals(copy));
        Assert.True(copy.ValueEquals(s));
        Assert.True(s.ValueEquals(AtmosphereSettings.Earth()));
        Assert.False(s.ValueEquals(null));

        s.SunIlluminance = 2f;
        Assert.Equal(2.7f, copy.SunIlluminance);
        Assert.False(s.ValueEquals(copy));
        s.SunIlluminance = 2.7f;
        Assert.True(s.ValueEquals(copy));
    }

    [Theory]
    [MemberData(nameof(SettingNames))]
    public void ValueEquals_DetectsEverySettingChange(string propertyName)
    {
        var s = AtmosphereSettings.Earth();
        var copy = s.Clone();
        var property = typeof(AtmosphereSettings).GetProperty(propertyName)!;
        object changed = property.GetValue(s) switch
        {
            bool value => !value,
            float value => value + 1f,
            Vector3 value => value + Vector3.One,
            _ => throw new InvalidOperationException("Unknown atmosphere property type."),
        };
        property.SetValue(s, changed);

        Assert.False(s.ValueEquals(copy));
        Assert.False(copy.ValueEquals(s));
        Assert.True(s.ValueEquals(s));
        Assert.True(s.ValueEquals(s.Clone()));
    }

    public static IEnumerable<object[]> SettingNames()
    {
        foreach (var property in typeof(AtmosphereSettings).GetProperties())
            yield return new object[] { property.Name };
    }

    [Theory]
    [InlineData(nameof(AtmosphereSettings.PlanetRadiusKm), false)]
    [InlineData(nameof(AtmosphereSettings.AtmosphereHeightKm), false)]
    [InlineData(nameof(AtmosphereSettings.RayleighScatteringPerKm), false)]
    [InlineData(nameof(AtmosphereSettings.RayleighScaleHeightKm), false)]
    [InlineData(nameof(AtmosphereSettings.MieScatteringPerKm), false)]
    [InlineData(nameof(AtmosphereSettings.MieExtinctionPerKm), false)]
    [InlineData(nameof(AtmosphereSettings.MieScaleHeightKm), false)]
    [InlineData(nameof(AtmosphereSettings.MieAnisotropy), false)]
    [InlineData(nameof(AtmosphereSettings.OzoneAbsorptionPerKm), false)]
    [InlineData(nameof(AtmosphereSettings.OzoneCenterKm), false)]
    [InlineData(nameof(AtmosphereSettings.OzoneWidthKm), false)]
    [InlineData(nameof(AtmosphereSettings.GroundAlbedo), false)]
    [InlineData(nameof(AtmosphereSettings.Enabled), true)]
    [InlineData(nameof(AtmosphereSettings.SunIlluminance), true)]
    [InlineData(nameof(AtmosphereSettings.MoonIlluminanceFraction), true)]
    [InlineData(nameof(AtmosphereSettings.SunAngularRadiusRadians), true)]
    [InlineData(nameof(AtmosphereSettings.WorldUnitsPerKilometre), true)]
    [InlineData(nameof(AtmosphereSettings.AerialDistanceScale), true)]
    [InlineData(nameof(AtmosphereSettings.AerialOnsetDistance), true)]
    [InlineData(nameof(AtmosphereSettings.AmbientScale), true)]
    [InlineData(nameof(AtmosphereSettings.FillScale), true)]
    [InlineData(nameof(AtmosphereSettings.NightAmbientFloor), true)]
    [InlineData(nameof(AtmosphereSettings.NightSkyFloor), true)]
    [InlineData(nameof(AtmosphereSettings.HazeDensityPerKm), true)]
    [InlineData(nameof(AtmosphereSettings.HazeScaleHeightKm), true)]
    [InlineData(nameof(AtmosphereSettings.DayTint), true)]
    [InlineData(nameof(AtmosphereSettings.DuskTint), true)]
    [InlineData(nameof(AtmosphereSettings.NightTint), true)]
    public void PhysicalValueEquals_DetectsOnlyStaticTableInputs(string propertyName, bool equal)
    {
        var s = AtmosphereSettings.Earth();
        var copy = s.Clone();
        Assert.True(s.PhysicalValueEquals(copy));
        Assert.False(s.PhysicalValueEquals(null));
        var property = typeof(AtmosphereSettings).GetProperty(propertyName)!;
        object changed = property.GetValue(s) switch
        {
            bool value => !value,
            float value => value + .01f,
            Vector3 value => value + new Vector3(.01f),
            _ => throw new InvalidOperationException("Unknown atmosphere property type."),
        };
        property.SetValue(s, changed);
        Assert.Equal(equal, s.PhysicalValueEquals(copy));
        Assert.Equal(equal, copy.PhysicalValueEquals(s));
        Assert.True(s.PhysicalValueEquals(s.Clone()));
    }
}
