using System;
using System.Text.Json.Serialization.Metadata;
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
            // WaterPlane.Optics did not exist in the frozen recipes. Only a bound binding is new work to encode.
            if (info.Type == typeof(WaterPlane))
            {
                foreach (var property in info.Properties)
                    if (property.Name == nameof(WaterPlane.Optics)) property.ShouldSerialize = BoundOptics;
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
