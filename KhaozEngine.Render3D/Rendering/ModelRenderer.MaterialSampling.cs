using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering;

/// <summary>What one model material set binds: up to four real maps (null is the slot's 1x1 default) and its
/// parameter block. Kept with the set so every rebuild (shadow atlas, point lights, interior cut, caustics, a sampler
/// change) binds the same maps with the samplers current at that moment.</summary>
internal sealed record ModelMaterialSlots(IGpuTexture? Albedo, IGpuTexture? Normal, IGpuTexture? Roughness,
    IGpuTexture? Emissive, IGpuBuffer Params)
{
    internal bool HasMap => Albedo is not null || Normal is not null || Roughness is not null || Emissive is not null;
}

/// <summary>
/// Material texture sampling and the per-material parameter block (issue #1776).
/// <para>
/// PER-SLOT SAMPLERS. Each of the four model maps has its own sampler binding. A slot with a real map binds the
/// material sampler, anisotropic at <see cref="MaterialAnisotropy"/>. A slot that falls back to its 1x1 default binds
/// the device's built-in linear sampler, exactly as every slot did before. So the defaults sample exactly white, flat
/// normal and zero roughness on every backend by construction rather than by trusting each rasterizer to average
/// identical anisotropic probes back to the same value, the default set (every untextured mesh) is byte-identical,
/// and a textured mesh changes only in its real map slots. Anisotropy 1 IS the built-in linear sampler, so it
/// reproduces the old textured render byte for byte too.
/// </para>
/// <para>
/// THE PARAMETER BLOCK is one std140 vec4 today: rgb is the material's emissive colour (glTF emissiveFactor times
/// KHR_materials_emissive_strength), a is reserved. Every material without a glow binds the shared zero block, so the
/// shader adds exactly zero for it. Later material factors append here, so the block's one uniform-buffer descriptor
/// is spent once.
/// </para>
/// </summary>
internal sealed partial class ModelRenderer
{
    /// <summary>The highest anisotropy the material sampler is built with. Requests above it clamp to it.</summary>
    internal const int MaxMaterialAnisotropy = 16;

    /// <summary>std140 mirror of the <c>MaterialParams</c> block in every model fragment.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct MaterialParamsUbo
    {
        public Vector4 Emissive;
        public const uint SizeInBytes = 16;
    }

    int _materialAnisotropy;
    IGpuSampler _materialSampler = null!;        // _sampler at anisotropy 1 (not owned), otherwise owned here
    IGpuBuffer _zeroMaterialParams = null!;      // the no-glow block every default and non-emissive set binds

    /// <summary>The anisotropy the material sampler currently samples real maps with, 1 for trilinear.</summary>
    internal int MaterialAnisotropy => _materialAnisotropy;

    /// <summary>The sampler a real map slot binds. Borrowed for observation: never dispose it.</summary>
    internal IGpuSampler MaterialSamplerForTests => _materialSampler;

    internal static int ClampMaterialAnisotropy(int requested) => Math.Clamp(requested, 1, MaxMaterialAnisotropy);

    void CreateMaterialSampling(IGpuResourceFactory factory, int anisotropy)
    {
        _materialAnisotropy = ClampMaterialAnisotropy(anisotropy);
        _materialSampler = CreateMaterialSampler(factory, _materialAnisotropy);
        _zeroMaterialParams = CreateMaterialParams(Vector3.Zero);
    }

    // Wrap on all three axes because tileable kit textures repeat. No LOD bias: props keep their detail, unlike the
    // terrain sampler's +1. A device without anisotropy falls back to trilinear in its backend, like the terrain one.
    IGpuSampler CreateMaterialSampler(IGpuResourceFactory factory, int anisotropy) => anisotropy <= 1
        ? _sampler
        : factory.CreateSampler(new GpuSamplerDescription(GpuSamplerFilter.Anisotropic, GpuSamplerAddress.Wrap,
            GpuSamplerAddress.Wrap, GpuSamplerAddress.Wrap, maximumAnisotropy: (uint)anisotropy));

    IGpuSampler SlotSampler(IGpuTexture? map) => map is null ? _sampler : _materialSampler;

    /// <summary>A material's own parameter block carrying <paramref name="emissive"/>. The caller owns it.</summary>
    internal IGpuBuffer CreateMaterialParams(Vector3 emissive)
    {
        IGpuBuffer buffer = _gd.Factory.CreateBuffer(
            new GpuBufferDescription(MaterialParamsUbo.SizeInBytes, GpuBufferUsage.UniformBuffer));
        try
        {
            var block = new MaterialParamsUbo { Emissive = new Vector4(emissive, 0f) };
            _gd.UpdateBuffer(buffer, 0, in block);
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
        return buffer;
    }

    IGpuResourceSet CreateModelMaterialSet(IGpuResourceLayout layout, ModelMaterialSlots slots)
    {
        bool rigid = ReferenceEquals(layout, _layout);
        var binding = new ShadowSamplingBinding(layout, Array.Empty<IGpuBindableResource>(), includesPointLights: rigid,
            includesAtmosphere: rigid, slots);
        IGpuResourceSet set = BuildShadowSamplingSet(
            binding, _shadowMap.ShadowTexture, _pointShadowTexture, _pointShadowTransientTexture, _pointLightBuffer);
        _shadowSamplingBindings.Add(set, binding);
        return set;
    }

    // The resources a model set binds before its shadow tail: the frame block first on the rigid layout (the point
    // light buffers are spliced in after it), then the three maps and the albedo slot's sampler.
    IGpuBindableResource[] ModelMaterialPrefix(ShadowSamplingBinding binding, ModelMaterialSlots slots)
    {
        IGpuBindableResource[] maps =
            [slots.Albedo ?? _white, slots.Normal ?? _flatNormal, slots.Roughness ?? _defaultRough, SlotSampler(slots.Albedo)];
        return binding.IncludesPointLights ? [_ubo, .. maps] : maps;
    }

    // The tail every model layout ends with, after its shadow, atmosphere and water bindings.
    IGpuBindableResource[] ModelMaterialTail(ModelMaterialSlots slots) =>
        [SlotSampler(slots.Normal), SlotSampler(slots.Roughness), slots.Emissive ?? _white, SlotSampler(slots.Emissive), slots.Params];

    /// <summary>The layout elements behind <see cref="ModelMaterialTail"/>, appended to the rigid model layout and the
    /// skinned fragment layout. Appending keeps every existing binding number where it was.</summary>
    static GpuResourceLayoutElement[] AppendModelMaterialTail(params GpuResourceLayoutElement[] prefix) =>
    [
        .. prefix,
        new("NormalSamp", GpuResourceKind.Sampler, GpuShaderStages.Fragment),
        new("RoughnessSamp", GpuResourceKind.Sampler, GpuShaderStages.Fragment),
        new("EmissiveMap", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
        new("EmissiveSamp", GpuResourceKind.Sampler, GpuShaderStages.Fragment),
        new("MaterialParams", GpuResourceKind.UniformBuffer, GpuShaderStages.Fragment),
    ];

    /// <summary>Rebuild every live model material set that binds a real map against a new material sampler, then
    /// commit the replacements and retire the old sets and sampler through the queue. The same transaction the
    /// shadow atlas uses: on any failure every new set and the new sampler are freed and the current ones stay.</summary>
    internal ShadowLayoutReplacementResult ReplaceMaterialSampler(int anisotropy,
        IReadOnlyList<IGpuResourceSet> liveMaterialSets, Action<Func<IGpuResourceSet, IGpuResourceSet>> commitMaterialSets)
    {
        anisotropy = ClampMaterialAnisotropy(anisotropy);
        if (anisotropy == _materialAnisotropy) return ShadowLayoutReplacementResult.Unchanged;

        IGpuSampler previous = _materialSampler;
        int previousAnisotropy = _materialAnisotropy;
        var replacements = new Dictionary<IGpuResourceSet, IGpuResourceSet>(ReferenceEqualityComparer.Instance);
        var replacementBindings = new Dictionary<IGpuResourceSet, ShadowSamplingBinding>(ReferenceEqualityComparer.Instance);
        try
        {
            _materialSampler = CreateMaterialSampler(_gd.Factory, anisotropy);
            _materialAnisotropy = anisotropy;
            foreach (IGpuResourceSet set in liveMaterialSets)
            {
                if (replacements.ContainsKey(set)) continue;
                if (!_shadowSamplingBindings.TryGetValue(set, out ShadowSamplingBinding? binding)) continue;
                if (binding.Model is not { HasMap: true }) continue;
                IGpuResourceSet rebuilt = BuildShadowSamplingSet(binding, _shadowMap.ShadowTexture, _pointShadowTexture,
                    _pointShadowTransientTexture, _pointLightBuffer);
                replacements.Add(set, rebuilt);
                replacementBindings.Add(rebuilt, binding);
            }
        }
        catch
        {
            foreach (IGpuResourceSet set in replacements.Values) set.Dispose();
            if (!ReferenceEquals(_materialSampler, previous) && !ReferenceEquals(_materialSampler, _sampler))
                _materialSampler.Dispose();
            _materialSampler = previous;
            _materialAnisotropy = previousAnisotropy;
            return ShadowLayoutReplacementResult.Failed;
        }

        CommitShadowSamplingSets(replacements, replacementBindings, commitMaterialSets, _retired);
        if (!ReferenceEquals(previous, _sampler)) _retired.Retire(previous);
        return ShadowLayoutReplacementResult.Replaced;
    }

    void DisposeMaterialSampling()
    {
        if (!ReferenceEquals(_materialSampler, _sampler)) _materialSampler.Dispose();
        _zeroMaterialParams.Dispose();
    }
}
