using System;
using System.Text.Json.Serialization.Metadata;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

/// <summary>Keeps the frozen v1 uniform-lighting profile exact as the renderer gains opt-in settings.</summary>
internal static class RendererPerfRecipeSettings
{
    internal static DefaultJsonTypeInfoResolver Resolver()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(static info =>
        {
            if (info.Type != typeof(PixelPostProcessSettings)) return;
            foreach (var property in info.Properties)
                if (property.Name is nameof(PixelPostProcessSettings.IndirectDiffuse)
                    or nameof(PixelPostProcessSettings.AmbientIrradiance))
                    property.ShouldSerialize = LegacyIndirect;
        });
        return resolver;
    }

    static bool LegacyIndirect(object value, object? field)
    {
        var settings = (PixelPostProcessSettings)value;
        // These fields did not exist in v1. Refuse changed work before preserving its original encoding.
        if (settings.IndirectDiffuse != IndirectDiffuseMode.Uniform || settings.AmbientIrradiance != IrradianceSH.Zero)
            throw new InvalidOperationException("The frozen renderer profile requires uniform indirect lighting and zero irradiance coefficients.");
        return false;
    }
}
