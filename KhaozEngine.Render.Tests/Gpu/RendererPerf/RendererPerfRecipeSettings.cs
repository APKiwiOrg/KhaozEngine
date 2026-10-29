using System;
using System.Text.Json.Serialization.Metadata;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

/// <summary>Keeps the frozen v1 uniform-lighting profile and unbound water planes exact as the renderer gains
/// opt-in settings.</summary>
internal static class RendererPerfRecipeSettings
{
    internal static DefaultJsonTypeInfoResolver Resolver()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(static info =>
        {
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
                        or nameof(WaterSettings.WaterColumnLight))
                        property.ShouldSerialize = static (_, field) => field is true;
                    else if (property.Name == nameof(WaterSettings.UnderwaterPenumbraPerMetre))
                        property.ShouldSerialize = static (_, field) => field is float penumbra && penumbra != 0.03f;
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
                        or nameof(WaterLook.WaterColumnLight)
                        or nameof(WaterLook.WaterColor)
                        or nameof(WaterLook.Clarity))
                        property.ShouldSerialize = static (_, field) => field is not null;
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
