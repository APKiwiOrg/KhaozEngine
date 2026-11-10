using System;
using System.Text.Json.Serialization.Metadata;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

/// <summary>Keeps frozen workloads and their encoding exact as renderer defaults and draw metadata evolve.</summary>
internal static class RendererPerfRecipeSettings
{
    internal static DefaultJsonTypeInfoResolver Resolver()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(static info =>
        {
            // The recipe pins the historical one-texel workload. Other holds remain authored settings.
            if (info.Type == typeof(TemporalSettings))
            {
                foreach (var property in info.Properties)
                    if (property.Name == nameof(TemporalSettings.ShadowLightHoldTexels))
                        property.ShouldSerialize = static (_, field) => field is float hold && hold != 1f;
                return;
            }
            if (info.Type == typeof(RigidInstanceDraw))
            {
                foreach (var property in info.Properties)
                    if (property.Name == nameof(RigidInstanceDraw.ShadowMobility))
                        property.ShouldSerialize = static (_, field) =>
                            field is ShadowCasterMobility mobility && mobility != ShadowCasterMobility.Static;
                return;
            }
            // New water defaults preserve the frozen encoding. Explicit opt-ins remain authored inputs.
            if (info.Type == typeof(WaterPlane))
            {
                foreach (var property in info.Properties)
                    if (property.Name == nameof(WaterPlane.Optics)) property.ShouldSerialize = BoundOptics;
                    else if (property.Name == nameof(WaterPlane.FloorY))
                        property.ShouldSerialize = static (_, field) => field is not null;
                return;
            }
            if (info.Type == typeof(WaterSettings))
            {
                foreach (var property in info.Properties)
                    if (property.Name is nameof(WaterSettings.UnderwaterSunlight)
                        or nameof(WaterSettings.UnderwaterCaustics)
                        or nameof(WaterSettings.WaterColumnLight))
                        property.ShouldSerialize = static (_, field) => field is true;
                    else if (property.Name == nameof(WaterSettings.UnderwaterPenumbraPerMetre))
                        property.ShouldSerialize = static (_, field) => field is float penumbra && penumbra != 0.03f;
                    else if (property.Name == nameof(WaterSettings.CausticStrength))
                        property.ShouldSerialize = static (_, field) => field is float strength && strength != 1f;
                    else if (property.Name == nameof(WaterSettings.WaterColor))
                        property.ShouldSerialize = static (_, field) => field is Color color && color != new Color(0.10f, 0.13f, 0.10f, 1f);
                    else if (property.Name == nameof(WaterSettings.Clarity))
                        property.ShouldSerialize = static (_, field) => field is float clarity && clarity != 2f;
                return;
            }
            if (info.Type == typeof(WaterLook))
            {
                foreach (var property in info.Properties)
                    if (property.Name is nameof(WaterLook.UnderwaterSunlight)
                        or nameof(WaterLook.UnderwaterPenumbraPerMetre)
                        or nameof(WaterLook.UnderwaterCaustics)
                        or nameof(WaterLook.CausticStrength)
                        or nameof(WaterLook.WaterColumnLight)
                        or nameof(WaterLook.WaterColor)
                        or nameof(WaterLook.Clarity))
                        property.ShouldSerialize = static (_, field) => field is not null;
                return;
            }
            // A new atmosphere default preserves the frozen encoding. An explicit sample count remains an authored input.
            if (info.Type == typeof(AtmosphereSettings))
            {
                foreach (var property in info.Properties)
                    if (property.Name == nameof(AtmosphereSettings.AerialShadowSamples))
                        property.ShouldSerialize = static (_, field) => field is int samples && samples != 8;
                return;
            }
            if (info.Type != typeof(PixelPostProcessSettings)) return;
            foreach (var property in info.Properties)
                if (property.Name is nameof(PixelPostProcessSettings.IndirectDiffuse)
                    or nameof(PixelPostProcessSettings.AmbientIrradiance))
                    property.ShouldSerialize = LegacyIndirect;
        });
        return resolver;
    }

    static bool BoundOptics(object value, object? field) => field is WaterOpticsBinding { IsBound: true };

    static bool LegacyIndirect(object value, object? field)
    {
        var settings = (PixelPostProcessSettings)value;
        // These fields did not exist in v1. Refuse changed work before preserving its original encoding.
        if (settings.IndirectDiffuse != IndirectDiffuseMode.Uniform || settings.AmbientIrradiance != IrradianceSH.Zero)
            throw new InvalidOperationException("The frozen renderer profile requires uniform indirect lighting and zero irradiance coefficients.");
        return false;
    }
}
