using System;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed class RendererPerfRecipeSettingsTests
{
    static readonly JsonSerializerOptions LegacyOptions = new()
    {
        IncludeFields = true,
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public void Default_underwater_settings_keep_the_legacy_water_encoding()
    {
        var settings = new WaterSettings();
        var legacy = JsonSerializer.SerializeToNode(settings, LegacyOptions)!.AsObject();
        legacy.Remove(nameof(WaterSettings.UnderwaterSunlight));
        legacy.Remove(nameof(WaterSettings.UnderwaterPenumbraPerMetre));
        legacy.Remove(nameof(WaterSettings.UnderwaterCaustics));
        legacy.Remove(nameof(WaterSettings.CausticStrength));
        legacy.Remove(nameof(WaterSettings.WaterColumnLight));
        legacy.Remove(nameof(WaterSettings.WaterColor));
        legacy.Remove(nameof(WaterSettings.Clarity));

        Assert.Equal(legacy.ToJsonString(), JsonSerializer.Serialize(settings, RendererPerfRecipe.Json));
    }

    [Fact]
    public void Default_aerial_shadow_samples_keep_the_legacy_atmosphere_encoding()
    {
        var settings = new AtmosphereSettings();
        var legacy = JsonSerializer.SerializeToNode(settings, LegacyOptions)!.AsObject();
        legacy.Remove(nameof(AtmosphereSettings.AerialShadowSamples));

        Assert.Equal(legacy.ToJsonString(), JsonSerializer.Serialize(settings, RendererPerfRecipe.Json));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    public void An_explicit_aerial_shadow_sample_count_is_captured_in_the_identity(int samples)
    {
        var settings = new AtmosphereSettings { AerialShadowSamples = samples };
        var encoded = JsonSerializer.SerializeToNode(settings, RendererPerfRecipe.Json)!.AsObject();

        Assert.Equal(samples, encoded[nameof(AtmosphereSettings.AerialShadowSamples)]!.GetValue<int>());
        Assert.NotEqual(RendererPerfRecipe.Digest(new AtmosphereSettings()), RendererPerfRecipe.Digest(settings));
    }

    [Fact]
    public void Inherited_underwater_overrides_keep_the_legacy_look_encoding()
    {
        var look = new WaterLook { WaveSource = WaterWaveSource.Procedural, SwellAmplitude = 0f };
        var legacy = JsonSerializer.SerializeToNode(look, LegacyOptions)!.AsObject();
        legacy.Remove(nameof(WaterLook.UnderwaterSunlight));
        legacy.Remove(nameof(WaterLook.UnderwaterPenumbraPerMetre));
        legacy.Remove(nameof(WaterLook.UnderwaterCaustics));
        legacy.Remove(nameof(WaterLook.CausticStrength));
        legacy.Remove(nameof(WaterLook.WaterColumnLight));
        legacy.Remove(nameof(WaterLook.WaterColor));
        legacy.Remove(nameof(WaterLook.Clarity));

        Assert.Equal(legacy.ToJsonString(), JsonSerializer.Serialize(look, RendererPerfRecipe.Json));
    }

    [Fact]
    public void ExplicitColumnLookFieldsAreInTheIdentity()
    {
        var color = new Color(0.10f, 0.13f, 0.10f, 1f);
        var explicitLook = new WaterLook { WaterColumnLight = false, WaterColor = color, Clarity = 2f };
        var explicitLookJson = JsonSerializer.SerializeToNode(explicitLook, RendererPerfRecipe.Json)!.AsObject();

        Assert.False(explicitLookJson["WaterColumnLight"]!.GetValue<bool>());
        var encodedColor = explicitLookJson["WaterColor"]!;
        Assert.Equal(0.10f, encodedColor["R"]!.GetValue<float>());
        Assert.Equal(0.13f, encodedColor["G"]!.GetValue<float>());
        Assert.Equal(0.10f, encodedColor["B"]!.GetValue<float>());
        Assert.Equal(1f, encodedColor["A"]!.GetValue<float>());
        Assert.Equal(2f, explicitLookJson["Clarity"]!.GetValue<float>());
        string inherited = RendererPerfRecipe.Digest(new WaterLook());
        Assert.NotEqual(inherited, RendererPerfRecipe.Digest(explicitLook));
        Assert.NotEqual(inherited, RendererPerfRecipe.Digest(new WaterLook { WaterColumnLight = false }));
        Assert.NotEqual(inherited, RendererPerfRecipe.Digest(new WaterLook { WaterColumnLight = true }));
        Assert.NotEqual(inherited, RendererPerfRecipe.Digest(new WaterLook { WaterColor = color }));
        Assert.NotEqual(inherited, RendererPerfRecipe.Digest(new WaterLook { Clarity = 2f }));
    }

    [Fact]
    public void Enabled_column_lighting_is_captured_in_the_identity()
    {
        var settings = new WaterSettings { WaterColumnLight = true };
        var encoded = JsonSerializer.SerializeToNode(settings, RendererPerfRecipe.Json)!.AsObject();

        Assert.True(encoded[nameof(WaterSettings.WaterColumnLight)]!.GetValue<bool>());
        Assert.NotEqual(RendererPerfRecipe.Digest(new WaterSettings()), RendererPerfRecipe.Digest(settings));
    }

    [Theory]
    [InlineData(0.2f, 0.13f, 0.10f, 1f)]
    [InlineData(0.10f, 0.2f, 0.10f, 1f)]
    [InlineData(0.10f, 0.13f, 0.2f, 1f)]
    [InlineData(0.10f, 0.13f, 0.10f, 0f)]
    public void A_nondefault_column_color_is_captured_in_the_identity(float red, float green, float blue, float alpha)
    {
        var settings = new WaterSettings { WaterColor = new Color(red, green, blue, alpha) };
        var encoded = JsonSerializer.SerializeToNode(settings, RendererPerfRecipe.Json)!.AsObject();

        var encodedColor = encoded[nameof(WaterSettings.WaterColor)]!;
        Assert.Equal(red, encodedColor["R"]!.GetValue<float>());
        Assert.Equal(green, encodedColor["G"]!.GetValue<float>());
        Assert.Equal(blue, encodedColor["B"]!.GetValue<float>());
        Assert.Equal(alpha, encodedColor["A"]!.GetValue<float>());
        Assert.NotEqual(RendererPerfRecipe.Digest(new WaterSettings()), RendererPerfRecipe.Digest(settings));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(4f)]
    public void A_nondefault_clarity_is_captured_in_the_identity(float clarity)
    {
        var settings = new WaterSettings { Clarity = clarity };
        var encoded = JsonSerializer.SerializeToNode(settings, RendererPerfRecipe.Json)!.AsObject();

        Assert.Equal(clarity, encoded[nameof(WaterSettings.Clarity)]!.GetValue<float>());
        Assert.NotEqual(RendererPerfRecipe.Digest(new WaterSettings()), RendererPerfRecipe.Digest(settings));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void An_explicit_sunlight_or_caustic_override_is_captured_in_the_identity(bool enabled, bool caustics)
    {
        var look = caustics ? new WaterLook { UnderwaterCaustics = enabled } : new WaterLook { UnderwaterSunlight = enabled };
        string field = caustics ? nameof(WaterLook.UnderwaterCaustics) : nameof(WaterLook.UnderwaterSunlight);
        var encoded = JsonSerializer.SerializeToNode(look, RendererPerfRecipe.Json)!.AsObject();

        Assert.Equal(enabled, encoded[field]!.GetValue<bool>());
        Assert.NotEqual(RendererPerfRecipe.Digest(new WaterLook()), RendererPerfRecipe.Digest(look));
    }

    [Theory]
    [InlineData(0f, false)]
    [InlineData(0.03f, false)]
    [InlineData(0f, true)]
    [InlineData(1f, true)]
    public void An_explicit_penumbra_or_caustic_strength_override_is_captured_in_the_identity(float value, bool caustics)
    {
        var look = caustics ? new WaterLook { CausticStrength = value } : new WaterLook { UnderwaterPenumbraPerMetre = value };
        string field = caustics ? nameof(WaterLook.CausticStrength) : nameof(WaterLook.UnderwaterPenumbraPerMetre);
        var encoded = JsonSerializer.SerializeToNode(look, RendererPerfRecipe.Json)!.AsObject();

        Assert.Equal(value, encoded[field]!.GetValue<float>());
        Assert.NotEqual(RendererPerfRecipe.Digest(new WaterLook()), RendererPerfRecipe.Digest(look));
    }

    [Fact]
    public void An_unbounded_floor_keeps_the_legacy_plane_encoding()
    {
        var plane = new WaterPlane(0f, 2f, 0f, 4f);

        Assert.Equal("{\"CenterX\":0,\"CenterZ\":0,\"SurfaceY\":2,\"HalfExtentX\":4,\"HalfExtentZ\":4,\"Look\":null}",
            JsonSerializer.Serialize(plane, RendererPerfRecipe.Json));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Enabled_underwater_sunlight_or_caustics_is_captured_in_the_identity(bool caustics)
    {
        var settings = caustics ? new WaterSettings { UnderwaterCaustics = true } : new WaterSettings { UnderwaterSunlight = true };
        string field = caustics ? nameof(WaterSettings.UnderwaterCaustics) : nameof(WaterSettings.UnderwaterSunlight);
        var encoded = JsonSerializer.SerializeToNode(settings, RendererPerfRecipe.Json)!.AsObject();

        Assert.True(encoded[field]!.GetValue<bool>());
        Assert.NotEqual(RendererPerfRecipe.Digest(new WaterSettings()), RendererPerfRecipe.Digest(settings));
    }

    [Theory]
    [InlineData(0f, false)]
    [InlineData(0.09f, false)]
    [InlineData(0f, true)]
    [InlineData(4f, true)]
    public void A_nondefault_penumbra_or_caustic_strength_is_captured_in_the_identity(float value, bool caustics)
    {
        var settings = caustics ? new WaterSettings { CausticStrength = value } : new WaterSettings { UnderwaterPenumbraPerMetre = value };
        string field = caustics ? nameof(WaterSettings.CausticStrength) : nameof(WaterSettings.UnderwaterPenumbraPerMetre);
        var encoded = JsonSerializer.SerializeToNode(settings, RendererPerfRecipe.Json)!.AsObject();

        Assert.Equal(value, encoded[field]!.GetValue<float>());
        Assert.NotEqual(RendererPerfRecipe.Digest(new WaterSettings()), RendererPerfRecipe.Digest(settings));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    public void A_bounded_floor_is_captured_in_the_identity(float floor)
    {
        var plane = new WaterPlane(0f, 2f, 0f, 4f, floorY: floor);
        var encoded = JsonSerializer.SerializeToNode(plane, RendererPerfRecipe.Json)!.AsObject();

        Assert.Equal(floor, encoded[nameof(WaterPlane.FloorY)]!.GetValue<float>());
        Assert.NotEqual(RendererPerfRecipe.Digest(new WaterPlane(0f, 2f, 0f, 4f)), RendererPerfRecipe.Digest(plane));
    }

    [Theory]
    [InlineData(IndirectDiffuseMode.Directional, false)]
    [InlineData((IndirectDiffuseMode)99, false)]
    [InlineData(IndirectDiffuseMode.Uniform, true)]
    public void Frozen_construction_refuses_new_indirect_lighting_work(IndirectDiffuseMode mode, bool irradiance)
    {
        using var control = new ChangedIndirectRecipe(IndirectDiffuseMode.Uniform, false);
        control.Freeze();
        using var recipe = new ChangedIndirectRecipe(mode, irradiance);
        Assert.Throws<InvalidOperationException>(recipe.Freeze);
    }

    sealed class ChangedIndirectRecipe(IndirectDiffuseMode mode, bool irradiance) : RendererPerfRecipe("empty")
    {
        protected override void ConfigureSettings(PixelPostProcessSettings post)
        {
            post.IndirectDiffuse = mode;
            post.AmbientIrradiance = irradiance ? IrradianceSH.Uniform(Vector3.One) : IrradianceSH.Zero;
        }
    }
}
