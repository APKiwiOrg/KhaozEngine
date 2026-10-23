using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering;

/// <summary>The DQ temporal family pairs one prepared current palette with copied previous DQ words.
/// Its frame block and previous palette share set 3, preserving the four-set device contract.</summary>
internal sealed partial class ModelRenderer
{
    DualQuaternionMotionResources? _dqMotion;

    DualQuaternionMotionResources RequireDualQuaternionMotion() => _dqMotion ??=
        new DualQuaternionMotionResources(_gd, RequireMotion().FrameBuffer, _retired);

    void EnsureDualQuaternionMotionPipelines()
    {
        DualQuaternionMotionResources motion = RequireDualQuaternionMotion();
        IGpuResourceLayout[] layouts = [_skinnedMainLayout, _skinnedFragLayout, _dqPalette.Layout, motion.Palette.Layout];
        var vertices = new List<GpuVertexLayoutDescription> { _dqMaterialVertexLayout };
        IGpuPipeline ordinary = OpaqueMotionPipeline(_gd.Factory, _dqMaterialOutputs, motion.Shaders, layouts, vertices);
        try
        {
            _dqSkinnedDissolvePipeline = OpaqueMotionPipeline(_gd.Factory, _dqMaterialOutputs,
                motion.DissolveShaders, layouts, vertices);
            _dqSkinnedPipeline = ordinary;
        }
        catch
        {
            ordinary.Dispose();
            throw;
        }
    }

    internal void PackSkinnedMotion(uint slot, in Matrix4x4 previousWorld, ReadOnlySpan<RigidDualQuaternion> previousDq)
        => RequireDualQuaternionMotion().Palette.Pack(slot, previousWorld, previousDq);

    void BindSkinnedMotion(IGpuCommandList commands, uint slot, SkinningProfile profile)
    {
        SkinnedMotionPalette palette = profile.Method == SkinningMethod.DualQuaternionRigid
            ? RequireDualQuaternionMotion().Palette : RequireMotion().SkinnedPalette;
        commands.SetGraphicsResourceSet(3, palette.Set, palette.OffsetForSlot(slot));
    }

    void RetireDualQuaternionMotion()
    {
        _retired.Retire(_dqMotion);
        _dqMotion = null;
    }

    /// <summary>Created lazily for DQ temporal draws and retired before the frame block it references.</summary>
    sealed class DualQuaternionMotionResources : IDisposable
    {
        readonly IGpuDevice _device;
        IGpuShaderSet? _shaders, _dissolveShaders;

        internal DualQuaternionMotionResources(IGpuDevice device, IGpuBuffer frame, GpuRetireQueue retired)
        {
            _device = device;
            Palette = new SkinnedMotionPalette(device, frame, retired, SkinningMethod.DualQuaternionRigid);
        }

        internal SkinnedMotionPalette Palette { get; }
        internal IGpuShaderSet Shaders => _shaders ??= _device.Factory.CreateShadersFromSpirv(
            ShaderSources.DqSkinnedModelMotionVert, ShaderSources.SkinnedModelMotionFrag);
        internal IGpuShaderSet DissolveShaders => _dissolveShaders ??= _device.Factory.CreateShadersFromSpirv(
            ShaderSources.DqSkinnedModelMotionVert, ShaderSources.SkinnedModelDissolveMotionFrag);

        public void Dispose()
        {
            Palette.Dispose();
            _shaders?.Dispose();
            _dissolveShaders?.Dispose();
        }
    }
}
