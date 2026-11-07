using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering;

internal sealed partial class ModelRenderer
{
    IGpuResourceLayout? _foliageOpaqueLayout;
    IGpuResourceSet? _foliageOpaqueSet, _foliageOpaqueDefault;
    IGpuPipeline? _foliageOpaquePipeline;
    IGpuShaderSet? _foliageOpaqueShaders, _foliageOpaqueMotionShaders;

    IGpuResourceLayout FoliageOpaqueLayout() => _foliageOpaqueLayout ??=
        _gd.Factory.CreateResourceLayout(new GpuResourceLayoutDescription(
            AppendWaterVolumeLayout(AtmosphereBindings.AppendLayout(
                new("U", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex | GpuShaderStages.Fragment),
                new("PointLights", GpuResourceKind.StructuredBufferReadOnly, GpuShaderStages.Fragment),
                new("PointLightClusters", GpuResourceKind.StructuredBufferReadOnly, GpuShaderStages.Fragment),
                new("ShadowMap", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
                new("ShadowSamp", GpuResourceKind.Sampler, GpuShaderStages.Fragment),
                new("PointShadowMap", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
                new("PointShadowTransientMap", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment)))));

    IGpuPipeline OpaqueFoliagePipeline()
    {
        if (_foliageOpaquePipeline is not null) return _foliageOpaquePipeline;
        var factory = _gd.Factory;
        IGpuResourceLayout layout = FoliageOpaqueLayout();
        ModelMotionResources? motion = MotionMath.IsTemporal(_foliageOutputs) ? _motion : null;
        string vertex = motion is null ? ShaderSources.FoliageVert : ShaderSources.FoliageMotionVert;
        string fragment = motion is null ? ShaderSources.FoliageOpaqueFrag : ShaderSources.FoliageOpaqueMotionFrag;
        IGpuShaderSet shaders = _visibilityShaders.TrySelect(factory, _foliageOutputs, vertex, fragment,
            preserve: false, out IGpuShaderSet visibility) ? visibility
            : motion is null ? _foliageOpaqueShaders ??= factory.CreateShadersFromSpirv(vertex, fragment)
            : _foliageOpaqueMotionShaders ??= factory.CreateShadersFromSpirv(vertex, fragment);
        return _foliageOpaquePipeline = OpaqueMotionPipeline(factory, _foliageOutputs, shaders,
            motion is null ? [layout, _foliageLayout!] : [layout, _foliageLayout!, motion.FrameLayout],
            new(FoliageVertexLayouts.Create()));
    }
    IGpuResourceSet OpaqueFoliageSet()
    {
        // The ordinary default changes transactionally when light buffers or shadow atlases are replaced.
        // Keep this subset on the same generation, retiring its previous bindings before the next draw.
        if (!ReferenceEquals(_foliageOpaqueDefault, _defaultSet))
        {
            if (_foliageOpaqueSet is not null) _retired.Retire(_foliageOpaqueSet);
            _foliageOpaqueSet = _gd.Factory.CreateResourceSet(new GpuResourceSetDescription(_foliageOpaqueLayout!,
                AppendWaterVolumeResources(_atmosphere.AppendResources(_ubo, _pointLightBuffer,
                    _pointLightClusterBuffer, _shadowMap.ShadowTexture, _shadowMap.ShadowSampler,
                    _pointShadowTexture, _pointShadowTransientTexture))));
            _foliageOpaqueDefault = _defaultSet;
        }
        return _foliageOpaqueSet!;
    }
}
