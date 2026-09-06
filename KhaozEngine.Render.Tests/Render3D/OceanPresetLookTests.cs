using System;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public class OceanPresetLookTests
{
    [Fact]
    public void ExistingScenePreset_NullLiteralRetainsItsValidationContract()
        => Assert.Throws<ArgumentNullException>(() => OceanPresets.Apply(OceanPresetKind.Calm, null!));

    [Theory]
    [InlineData(OceanPresetKind.Calm)]
    [InlineData(OceanPresetKind.Moderate)]
    [InlineData(OceanPresetKind.Rough)]
    public void LookPreset_MatchesWholeScenePresetWithoutChangingItsSourceOrShore(OceanPresetKind kind)
    {
        var scene = new WaterSettings { SwellAmplitude = 3, GlintStrength = 2, GlintRoughness = 0.9f };
        var expected = new WaterSettings();
        expected.CopyFrom(scene);
        OceanPresets.Apply(kind, expected);
        var look = new WaterLook { WaveSource = WaterWaveSource.Procedural, SurfStrength = 0.17f, ShoreFadeDistance = 0.23f };
        OceanPresets.ApplyToLook(kind, look);
        WaterSettings actual = look.ResolveInto(new WaterSettings(), scene);
        Assert.Equal(expected.SwellAmplitude, actual.SwellAmplitude);
        Assert.Equal(expected.SwellWavelength, actual.SwellWavelength);
        Assert.Equal(expected.SwellSteepness, actual.SwellSteepness);
        Assert.Equal(expected.SwellSpeed, actual.SwellSpeed);
        Assert.Equal(expected.NormalStrength, actual.NormalStrength);
        Assert.Equal(expected.FoamStrength, actual.FoamStrength);
        Assert.Equal(expected.FoamCrestCoverage, actual.FoamCrestCoverage);
        Assert.Equal(expected.GlintStrength, actual.GlintStrength);
        Assert.Equal(expected.GlintRoughness, actual.GlintRoughness);
        Assert.Equal(WaterWaveSource.Procedural, actual.WaveSource);
        Assert.Equal(0.17f, actual.SurfStrength);
        Assert.Equal(0.23f, actual.ShoreFadeDistance);
        Assert.Equal(3, scene.SwellAmplitude);
        Assert.Equal(2, scene.GlintStrength);
        Assert.Equal(0.9f, scene.GlintRoughness);
        var inheriting = new WaterLook();
        OceanPresets.ApplyToLook(kind, inheriting);
        Assert.Null(inheriting.WaveSource);
        Assert.Null(inheriting.GlintDistantRoughness);
        Assert.Null(inheriting.GlintExponent);
    }

    [Fact]
    public void InvalidPreset_DoesNotPartiallyMutateLook()
    {
        var look = new WaterLook { SwellAmplitude = 7, GlintStrength = 3 };
        Assert.Throws<ArgumentOutOfRangeException>(() => OceanPresets.ApplyToLook((OceanPresetKind)99, look));
        Assert.Equal(7, look.SwellAmplitude);
        Assert.Equal(3, look.GlintStrength);
        Assert.Throws<ArgumentNullException>(() => OceanPresets.ApplyToLook(OceanPresetKind.Calm, null!));
    }
}
