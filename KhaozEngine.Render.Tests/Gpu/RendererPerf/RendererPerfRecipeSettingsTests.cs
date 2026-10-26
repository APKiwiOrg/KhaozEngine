using System;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
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

        Assert.Equal(legacy.ToJsonString(), JsonSerializer.Serialize(settings, RendererPerfRecipe.Json));
    }

    [Fact]
    public void Inherited_underwater_overrides_keep_the_legacy_look_encoding()
    {
        var look = new WaterLook { WaveSource = WaterWaveSource.Procedural, SwellAmplitude = 0f };
        var legacy = JsonSerializer.SerializeToNode(look, LegacyOptions)!.AsObject();
        legacy.Remove(nameof(WaterLook.UnderwaterSunlight));
        legacy.Remove(nameof(WaterLook.UnderwaterPenumbraPerMetre));

        Assert.Equal(legacy.ToJsonString(), JsonSerializer.Serialize(look, RendererPerfRecipe.Json));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_explicit_sunlight_override_is_captured_in_the_identity(bool enabled)
    {
        var look = new WaterLook { UnderwaterSunlight = enabled };
        var encoded = JsonSerializer.SerializeToNode(look, RendererPerfRecipe.Json)!.AsObject();

        Assert.Equal(enabled, encoded[nameof(WaterLook.UnderwaterSunlight)]!.GetValue<bool>());
        Assert.NotEqual(RendererPerfRecipe.Digest(new WaterLook()), RendererPerfRecipe.Digest(look));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.03f)]
    public void An_explicit_penumbra_override_is_captured_in_the_identity(float penumbra)
    {
        var look = new WaterLook { UnderwaterPenumbraPerMetre = penumbra };
        var encoded = JsonSerializer.SerializeToNode(look, RendererPerfRecipe.Json)!.AsObject();

        Assert.Equal(penumbra, encoded[nameof(WaterLook.UnderwaterPenumbraPerMetre)]!.GetValue<float>());
        Assert.NotEqual(RendererPerfRecipe.Digest(new WaterLook()), RendererPerfRecipe.Digest(look));
    }

    [Fact]
    public void An_unbounded_floor_keeps_the_legacy_plane_encoding()
    {
        var plane = new WaterPlane(0f, 2f, 0f, 4f);

        Assert.Equal("{\"CenterX\":0,\"CenterZ\":0,\"SurfaceY\":2,\"HalfExtentX\":4,\"HalfExtentZ\":4,\"Look\":null}",
            JsonSerializer.Serialize(plane, RendererPerfRecipe.Json));
    }

    [Fact]
    public void Enabled_underwater_sunlight_is_captured_in_the_identity()
    {
        var settings = new WaterSettings { UnderwaterSunlight = true };
        var encoded = JsonSerializer.SerializeToNode(settings, RendererPerfRecipe.Json)!.AsObject();

        Assert.True(encoded[nameof(WaterSettings.UnderwaterSunlight)]!.GetValue<bool>());
        Assert.NotEqual(RendererPerfRecipe.Digest(new WaterSettings()), RendererPerfRecipe.Digest(settings));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.09f)]
    public void A_nondefault_penumbra_is_captured_in_the_identity(float penumbra)
    {
        var settings = new WaterSettings { UnderwaterPenumbraPerMetre = penumbra };
        var encoded = JsonSerializer.SerializeToNode(settings, RendererPerfRecipe.Json)!.AsObject();

        Assert.Equal(penumbra, encoded[nameof(WaterSettings.UnderwaterPenumbraPerMetre)]!.GetValue<float>());
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
        using var recipe = new ChangedIndirectRecipe(mode, irradiance);
        var error = Assert.Throws<InvalidOperationException>(recipe.Freeze);
        Assert.Contains("indirect", error.Message, StringComparison.OrdinalIgnoreCase);
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
