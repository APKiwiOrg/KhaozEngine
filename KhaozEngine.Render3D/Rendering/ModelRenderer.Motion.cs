using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering;

/// <summary>
/// The model pass's temporal variants (TEMPORAL-FOUNDATIONS-DESIGN-2026-09-24 section 4). While the model framebuffer
/// carries the motion attachment, the pipelines this renderer builds also write screen-space motion, and what they read
/// lives in <see cref="ModelMotionResources"/>. With temporal rendering off <see cref="_motion"/> stays null, so no
/// variant, buffer or layout exists.
/// </summary>
internal sealed partial class ModelRenderer
{
    ModelMotionResources? _motion;

    /// <summary>The temporal resources, null while the model framebuffer has no motion attachment.</summary>
    internal ModelMotionResources? Motion => _motion;

    // The temporal members exist only while the model target carries the motion attachment. A call without it is a
    // wiring error in the frame, so it says so instead of failing on a null.
    ModelMotionResources RequireMotion() => _motion ?? throw new InvalidOperationException(
        "The model renderer has no motion resources: it was built against a target without the motion attachment, so "
        + "temporal rendering is off for this frame.");

    /// <summary>Create the temporal resources on the first build against a temporal target and retire them when the
    /// target loses its motion attachment.</summary>
    void EnsureMotionResources(bool temporal)
    {
        if (temporal) { _motion ??= new ModelMotionResources(_gd, _retired); return; }
        if (_motion is null) return;
        _retired.Retire(_motion);
        _motion = null;
    }

    /// <summary>The state every opaque model-pass variant shares with its base pipeline. Only the program, the layouts
    /// and the vertex streams differ between paths.</summary>
    static IGpuPipeline OpaqueMotionPipeline(IGpuResourceFactory factory, GpuOutputDescription outputs,
        IGpuShaderSet shaders, IGpuResourceLayout[] layouts, List<GpuVertexLayoutDescription> vertexLayouts) =>
        factory.CreateGraphicsPipeline(new GpuPipelineDescription
        {
            BlendFactor = Vector4.Zero,
            BlendAttachments = ModelTargetBlends.Opaque(outputs),
            DepthStencil = GpuDepthStencilState.DepthOnlyLessEqual,
            Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise,
                depthClipEnabled: true, scissorTestEnabled: false),
            Topology = GpuPrimitiveTopology.TriangleList,
            ResourceLayouts = layouts,
            ShaderSet = shaders,
            VertexLayouts = vertexLayouts,
            Outputs = outputs,
        });

    /// <summary>The rigid instanced variant: the model material set at set 0, the motion set at set 1, and the motion
    /// slots at vertex buffer slot 2.</summary>
    IGpuPipeline CreateRigidMotionPipeline(IGpuResourceFactory factory, GpuOutputDescription outputs,
        GpuVertexLayoutDescription vertexLayout, GpuVertexLayoutDescription instanceLayout)
    {
        ModelMotionResources motion = RequireMotion();
        return OpaqueMotionPipeline(factory, outputs, motion.RigidShaders, new[] { _layout, motion.RigidLayout },
            new List<GpuVertexLayoutDescription> { vertexLayout, instanceLayout, ModelMotionResources.SlotLayout });
    }

    /// <summary>The GPU-skinned variant: the base pipeline's sets 0 to 2 and, at set 3, the motion block with this
    /// caster's last frame.</summary>
    IGpuPipeline CreateSkinnedMotionPipeline(IGpuResourceFactory factory, GpuOutputDescription outputs,
        GpuVertexLayoutDescription skinnedVertexLayout, bool dissolve)
    {
        ModelMotionResources motion = RequireMotion();
        return OpaqueMotionPipeline(factory, outputs, dissolve ? motion.SkinnedDissolveShaders : motion.SkinnedShaders,
            new[] { _skinnedMainLayout, _skinnedFragLayout, _bonePalette.Layout, motion.SkinnedPalette.Layout },
            new List<GpuVertexLayoutDescription> { skinnedVertexLayout });
    }

    // The CPU-skinned non-dissolving variant. The base pass draws CPU-skinned meshes through the rigid pipeline, which a
    // temporal target cannot, because the rigid variant reads a per-instance slot stream where these need per-vertex
    // positions. Null while the target has no motion attachment.
    IGpuPipeline? _cpuSkinnedMotionPipeline;

    /// <summary>A CPU-skinned variant: the model material set at set 0, the motion block at set 1, and last frame's
    /// positions at vertex buffer slot 2.</summary>
    IGpuPipeline CreateCpuSkinnedMotionPipeline(IGpuResourceFactory factory, GpuOutputDescription outputs,
        GpuVertexLayoutDescription vertexLayout, GpuVertexLayoutDescription instanceLayout, bool dissolve)
    {
        ModelMotionResources motion = RequireMotion();
        return OpaqueMotionPipeline(factory, outputs,
            dissolve ? motion.CpuSkinnedDissolveShaders : motion.CpuSkinnedShaders,
            new[] { _layout, motion.FrameLayout },
            new List<GpuVertexLayoutDescription> { vertexLayout, instanceLayout, ModelMotionResources.CpuPreviousLayout });
    }

    /// <summary>Bind the pipeline non-dissolving CPU-skinned draws use: the rigid pipeline, as CPU skinning always has,
    /// or the CPU-skinned variant while the target is temporal. Dissolving draws keep <see cref="BindDissolvePass"/>,
    /// whose pipeline is the CPU-skinned dissolve variant while temporal.</summary>
    public void BindCpuSkinnedPass(IGpuCommandList cl) => cl.SetPipeline(_cpuSkinnedMotionPipeline ?? _pipeline);

    /// <summary>Upload last frame's CPU-skinned positions, before the model pass.</summary>
    internal void UploadCpuSkinnedPrevious(IGpuCommandList cl, ReadOnlySpan<Vector3> positions)
        => RequireMotion().UploadCpuPrevious(cl, positions);

    /// <summary>Hold a last-frame slot per GPU-skinned caster.</summary>
    internal void EnsureSkinnedMotionCapacity(uint slotCount) => RequireMotion().SkinnedPalette.EnsureCapacity(slotCount);

    /// <summary>Pack one caster's last frame into its slot.</summary>
    internal void PackSkinnedMotion(uint slot, in Matrix4x4 previousWorld, ReadOnlySpan<Matrix4x4> previousBones)
        => RequireMotion().SkinnedPalette.Pack(slot, previousWorld, previousBones);

    /// <summary>Upload every packed last-frame slot, before any skinned draw.</summary>
    internal void UploadSkinnedMotion(IGpuCommandList cl) => RequireMotion().SkinnedPalette.Upload(cl);

    /// <summary>Upload this frame's motion block. Once per temporal frame, before the model pass.</summary>
    internal void UploadMotionFrame(IGpuCommandList cl, in MotionFrameUbo frame) => RequireMotion().UploadFrame(cl, frame);

    /// <summary>Upload one motion slot per grouped instance and the previous transforms they index, before the model
    /// pass.</summary>
    internal void UploadRigidMotion(IGpuCommandList cl, ReadOnlySpan<float> slots, ReadOnlySpan<Matrix4x4> previous)
        => RequireMotion().UploadRigid(cl, slots, previous);

    void DisposeMotionResources()
    {
        _cpuSkinnedMotionPipeline?.Dispose();
        _cpuSkinnedMotionPipeline = null;
        _motion?.Dispose();
        _motion = null;
    }
}
