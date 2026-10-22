using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering;

/// <summary>Directional DQ depth variants. The model renderer owns and uploads the current palette once.
/// Each cascade changes only its header window. LBS programs and raster states remain independent.</summary>
internal sealed partial class ShadowMapRenderer
{
    readonly IGpuShaderSet?[] _dqDepthShaders = new IGpuShaderSet[5];
    readonly IGpuPipeline?[] _dqDepthPipelines = new IGpuPipeline[5];
    readonly Dictionary<(IGpuTexture, IGpuSampler), IGpuResourceSet> _dqCutoutSets = new();

    IGpuPipeline DualQuaternionDepthPipeline(DualQuaternionPaletteBuffer palette, bool dissolve, bool inverted, bool cutout)
    {
        int variant = cutout ? inverted ? 4 : 3 : dissolve ? inverted ? 2 : 1 : 0;
        if (_dqDepthPipelines[variant] is { } cached) return cached;
        string vertex = cutout ? ShaderSources.DqSkinnedShadowDepthCutoutVert
            : dissolve ? ShaderSources.DqSkinnedShadowDepthDissolveVert : ShaderSources.DqSkinnedShadowDepthVert;
        string fragment = variant switch
        {
            0 => ShaderSources.ShadowDepthFrag,
            1 => ShaderSources.SkinnedShadowDepthDissolveFrag,
            2 => ShaderSources.SkinnedShadowDepthDissolveInvertedFrag,
            3 => ShaderSources.DqSkinnedShadowDepthCutoutFrag,
            _ => ShaderSources.DqSkinnedShadowDepthCutoutInvertedFrag,
        };
        IGpuShaderSet shaders = _gd.Factory.CreateShadersFromSpirv(vertex, fragment);
        try
        {
            // The atlas layout transaction preserves these output formats and sample count. Resolution and
            // cascade-count changes do not invalidate a pipeline, which has no framebuffer dimensions.
            IGpuPipeline pipeline = BuildSkinnedPipeline(_gd.Factory, Outputs, paletteLayout: palette.Layout,
                shaders: shaders, cutout: cutout);
            _dqDepthShaders[variant] = shaders;
            return _dqDepthPipelines[variant] = pipeline;
        }
        catch
        {
            shaders.Dispose();
            throw;
        }
    }

    internal void BindCascadeSkinned(IGpuCommandList commands, int cascade, SkinningProfile profile,
        DualQuaternionPaletteBuffer palette, bool dissolve, bool invertedDissolve, bool cutout)
    {
        if (profile.Method == SkinningMethod.LinearBlend)
        {
            BindCascadeSkinned(commands, cascade, dissolve, invertedDissolve);
            return;
        }
        commands.SetPipeline(DualQuaternionDepthPipeline(palette, dissolve, invertedDissolve, cutout));
        SetCascadeScissor(commands, cascade);
        commands.SetGraphicsResourceSet(0, _skinnedSet!, 0);
        commands.SetGraphicsResourceSet(1, palette.Set, 0);
    }

    internal void DrawGpuSkinnedCaster(IGpuCommandList commands, IGpuBuffer restVb, IGpuBuffer ib, int indexCount,
        GpuIndexFormat indexFormat, uint slot, uint paletteSlot, SkinningProfile profile, DualQuaternionPaletteBuffer palette)
    {
        if (profile.Method == SkinningMethod.LinearBlend)
        {
            DrawGpuSkinnedCaster(commands, restVb, ib, indexCount, indexFormat, slot, paletteSlot);
            return;
        }
        commands.SetGraphicsResourceSet(0, _skinnedSet!, slot * SkinnedDepthSlotBytes);
        commands.SetGraphicsResourceSet(1, palette.Set, palette.OffsetFor(paletteSlot));
        commands.SetVertexBuffer(0, restVb);
        commands.SetIndexBuffer(ib, indexFormat);
        commands.DrawIndexed((uint)indexCount, 1, 0, 0, 0);
    }

    // A cutout set names only the albedo and sampler, never the atlas currently being written. Reuse it across
    // this pass's cascades, then retire it before the next pass so unloaded textures are not kept in a cache.
    internal void BindDqCutoutMaterial(IGpuCommandList commands, IGpuTexture albedo, IGpuSampler sampler, bool gpu)
    {
        if (!_dqCutoutSets.TryGetValue((albedo, sampler), out IGpuResourceSet? set))
        {
            set = _gd.Factory.CreateResourceSet(new GpuResourceSetDescription(_cutoutMaterialLayout, albedo, sampler));
            _dqCutoutSets.Add((albedo, sampler), set);
        }
        commands.SetGraphicsResourceSet(gpu ? 2u : 1u, set);
    }

    void RetireDqCutoutSets()
    {
        foreach (IGpuResourceSet set in _dqCutoutSets.Values) _retired.Retire(set);
        _dqCutoutSets.Clear();
    }

    void DisposeDualQuaternionDepth()
    {
        foreach (IGpuPipeline? pipeline in _dqDepthPipelines) pipeline?.Dispose();
        foreach (IGpuShaderSet? shaders in _dqDepthShaders) shaders?.Dispose();
        foreach (IGpuResourceSet set in _dqCutoutSets.Values) set.Dispose();
        _dqCutoutSets.Clear();
    }
}
