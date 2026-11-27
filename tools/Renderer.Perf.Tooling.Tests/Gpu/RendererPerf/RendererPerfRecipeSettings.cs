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
            // The display history grid and its default pixel budget preserve the frozen encoding. An explicit
            // fine grid or another budget remains an authored input.
            if (info.Type == typeof(TemporalSettings))
            {
                foreach (var property in info.Properties)
                    if (property.Name == nameof(TemporalSettings.ShadowLightHoldTexels))
                        property.ShouldSerialize = static (_, field) => field is float hold && hold != 1f;
                    else if (property.Name == nameof(TemporalSettings.HistoryDetail))
                        property.ShouldSerialize = static (_, field) =>
                            field is TemporalHistoryDetail detail && detail != TemporalHistoryDetail.Display;
                    else if (property.Name == nameof(TemporalSettings.HistoryDetailPixelLimit))
                        property.ShouldSerialize = static (_, field) => field is long budget && budget != 2560L * 1440;
                return;
            }
            // Linear sampling preserves the frozen encoding. Other anisotropy values remain authored inputs.
            if (info.Type == typeof(RenderQuality))
            {
                foreach (var property in info.Properties)
                    if (property.Name == nameof(RenderQuality.MaterialAnisotropy))
                        property.ShouldSerialize = static (_, field) => field is int anisotropy && anisotropy != 1;
                return;
            }
            // A surface without an emissive map or colour preserves the frozen encoding. Either one remains an authored input.
            if (info.Type == typeof(Scene3D.SurfaceMaps))
            {
                foreach (var property in info.Properties)
                    if (property.Name == nameof(Scene3D.SurfaceMaps.Emissive))
                        property.ShouldSerialize = static (_, field) => field is Scene3D.TextureHandle map && map.IsValid;
                    else if (property.Name == nameof(Scene3D.SurfaceMaps.EmissiveFactor))
                        property.ShouldSerialize = static (_, field) => field is System.Numerics.Vector3 colour && colour != System.Numerics.Vector3.Zero;
                return;
            }
            if (info.Type == typeof(RigidInstanceDraw))
            {
                foreach (var property in info.Properties)
                    if (property.Name == nameof(RigidInstanceDraw.ShadowMobility))
                        property.ShouldSerialize = static (_, field) =>
                            field is ShadowCasterMobility mobility && mobility != ShadowCasterMobility.Static;
                    else if (property.Name == nameof(RigidInstanceDraw.Coverage)) property.ShouldSerialize = NoCoverage;
                return;
            }
            if (info.Type == typeof(SkinnedInstanceDraw))
            {
                foreach (var property in info.Properties)
                    if (property.Name == nameof(SkinnedInstanceDraw.Coverage)) property.ShouldSerialize = NoCoverage;
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
            info.OnSerializing += RequireFrozenPostEffects;
            for (int i = info.Properties.Count - 1; i >= 0; i--)
            {
                var property = info.Properties[i];
                // Omit both contracts before JSON resolves nested types. A value predicate alone still
                // discovers the colour cube's span constructor, even when the recipe has no look.
                if (property.Name is nameof(PixelPostProcessSettings.ColorGrade)
                    or nameof(PixelPostProcessSettings.AmbientOcclusion))
                    info.Properties.RemoveAt(i);
                else if (property.Name is nameof(PixelPostProcessSettings.IndirectDiffuse)
                    or nameof(PixelPostProcessSettings.AmbientIrradiance))
                    property.ShouldSerialize = LegacyIndirect;
            }
        });
        return resolver;
    }

    static void RequireFrozenPostEffects(object value)
    {
        var settings = (PixelPostProcessSettings)value;
        if (settings.AmbientOcclusion is { Enabled: true, Strength: > 0f })
            throw new InvalidOperationException("The frozen renderer profile requires ambient occlusion to be bypassed.");
        if (settings.ColorGrade is { Look.IsIdentity: false, Strength: > 0f })
            throw new InvalidOperationException("The frozen renderer profile requires colour grading to be bypassed.");
    }

    static bool BoundOptics(object value, object? field) => field is WaterOpticsBinding { IsBound: true };

    static bool NoCoverage(object value, object? field)
    {
        // Coverage descriptors did not exist in v1 and are opaque frame evidence with no public state. They would
        // encode as an empty object, so refuse staged coverage before preserving the original draw encoding.
        if (field is CoverageEquipmentDraw rigid && !rigid.Equals(default(CoverageEquipmentDraw))
            || field is SkinnedCoverageFrame skinned && !skinned.Equals(default(SkinnedCoverageFrame)))
            throw new InvalidOperationException("The frozen renderer profile cannot describe staged equipment coverage.");
        return false;
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
