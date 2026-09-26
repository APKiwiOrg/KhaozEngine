using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering;

/// <summary>
/// What the model pass's temporal variants read (TEMPORAL-FOUNDATIONS-DESIGN-2026-09-24 section 4). Created on the first
/// build against a model target that carries the motion attachment and retired when the target loses it, so with
/// temporal rendering off none of it exists. The temporal programs compile when the renderer builds against that
/// target, whether or not a scene draws every path.
/// Later tasks add the ground members.
/// </summary>
internal sealed class ModelMotionResources : IDisposable
{
    /// <summary>The rigid variant's per-instance motion slot: vertex buffer slot 2, location 15, one float per instance,
    /// parallel to the instance stream so a draw's first instance selects both.</summary>
    internal static readonly GpuVertexLayoutDescription SlotLayout = new(stride: 4, instanceStepRate: 1,
        elements: new[] { new GpuVertexElement("IMotionSlot", GpuVertexElementFormat.Float1) });

    /// <summary>The CPU-skinned variant's last-frame positions: vertex buffer slot 2, location 15, per vertex.</summary>
    internal static readonly GpuVertexLayoutDescription CpuPreviousLayout = new(stride: 12, instanceStepRate: 0,
        elements: new[] { new GpuVertexElement("PrevPosition", GpuVertexElementFormat.Float3) });

    const uint MatrixBytes = 64;
    const uint InitialPrevious = 64;

    readonly IGpuDevice _gd;
    readonly GpuRetireQueue _retired;
    readonly IGpuBuffer _frame;
    IGpuBuffer _previous;
    uint _previousCapacity;
    IGpuBuffer? _slots;
    uint _slotCapacity;
    IGpuShaderSet? _rigid;
    IGpuShaderSet? _skinned, _skinnedDissolve;
    IGpuBuffer? _cpuPrevious;
    uint _cpuPreviousCapacity;
    IGpuShaderSet? _cpuSkinned, _cpuSkinnedDissolve;
    IGpuShaderSet? _foliage;

    internal ModelMotionResources(IGpuDevice gd, GpuRetireQueue retired)
    {
        _gd = gd;
        _retired = retired;
        IGpuResourceFactory f = gd.Factory;
        _frame = f.CreateBuffer(new GpuBufferDescription(MotionFrameUbo.SizeInBytes, GpuBufferUsage.UniformBuffer));
        FrameLayout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
            new GpuResourceLayoutElement("MotionFrame", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex)));
        FrameSet = f.CreateResourceSet(new GpuResourceSetDescription(FrameLayout, _frame));
        RigidLayout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
            new GpuResourceLayoutElement("MotionFrame", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex),
            new GpuResourceLayoutElement("PreviousInstanceTransforms", GpuResourceKind.StructuredBufferReadOnly,
                GpuShaderStages.Vertex)));
        _previousCapacity = InitialPrevious;
        _previous = CreatePrevious(_previousCapacity);
        RigidSet = f.CreateResourceSet(new GpuResourceSetDescription(RigidLayout, _frame, _previous));
        SkinnedPalette = new SkinnedMotionPalette(gd, _frame, retired);
    }

    /// <summary>The frame's <c>MotionFrame</c> block, 144 bytes, uploaded whole once per temporal frame.</summary>
    internal IGpuBuffer FrameBuffer => _frame;

    /// <summary>A set holding the motion block alone: set 1 of the CPU-skinned variants, set 2 of the foliage and
    /// ground variants.</summary>
    internal IGpuResourceLayout FrameLayout { get; }
    internal IGpuResourceSet FrameSet { get; }

    /// <summary>Set 1 of the rigid variant: the motion block and <c>PreviousInstanceTransforms</c>, vertex stage only.</summary>
    internal IGpuResourceLayout RigidLayout { get; }

    /// <summary>The rigid variant's set 1 over this frame's buffers. Replaced when the previous transforms grow.</summary>
    internal IGpuResourceSet RigidSet { get; private set; }

    /// <summary>This frame's motion slots. Uploaded before any rigid draw binds them.</summary>
    internal IGpuBuffer SlotBuffer => _slots ?? throw new InvalidOperationException(
        "No motion slots were uploaded this frame, so a rigid motion draw has nothing to read.");

    /// <summary>ModelMotionVert with ModelMotionFrag.</summary>
    internal IGpuShaderSet RigidShaders =>
        _rigid ??= _gd.Factory.CreateShadersFromSpirv(ShaderSources.ModelMotionVert, ShaderSources.ModelMotionFrag);

    /// <summary>Last frame's world and palette of each GPU-skinned caster, and set 3 of the skinned variant.</summary>
    internal SkinnedMotionPalette SkinnedPalette { get; }

    /// <summary>SkinnedModelMotionVert with SkinnedModelMotionFrag.</summary>
    internal IGpuShaderSet SkinnedShaders => _skinned ??= _gd.Factory.CreateShadersFromSpirv(
        ShaderSources.SkinnedModelMotionVert, ShaderSources.SkinnedModelMotionFrag);

    /// <summary>SkinnedModelMotionVert with SkinnedModelDissolveMotionFrag.</summary>
    internal IGpuShaderSet SkinnedDissolveShaders => _skinnedDissolve ??= _gd.Factory.CreateShadersFromSpirv(
        ShaderSources.SkinnedModelMotionVert, ShaderSources.SkinnedModelDissolveMotionFrag);

    /// <summary>This frame's CPU-skinned last-frame positions. Uploaded before any CPU-skinned draw binds them.</summary>
    internal IGpuBuffer CpuPrevious => _cpuPrevious ?? throw new InvalidOperationException(
        "No CPU-skinned previous positions were uploaded this frame.");

    /// <summary>ModelCpuSkinnedMotionVert with ModelMotionFrag.</summary>
    internal IGpuShaderSet CpuSkinnedShaders => _cpuSkinned ??= _gd.Factory.CreateShadersFromSpirv(
        ShaderSources.ModelCpuSkinnedMotionVert, ShaderSources.ModelMotionFrag);

    /// <summary>ModelCpuSkinnedMotionVert with ModelDissolveMotionFrag.</summary>
    internal IGpuShaderSet CpuSkinnedDissolveShaders => _cpuSkinnedDissolve ??= _gd.Factory.CreateShadersFromSpirv(
        ShaderSources.ModelCpuSkinnedMotionVert, ShaderSources.ModelDissolveMotionFrag);

    /// <summary>FoliageMotionVert with ModelMotionFrag.</summary>
    internal IGpuShaderSet FoliageShaders => _foliage ??= _gd.Factory.CreateShadersFromSpirv(
        ShaderSources.FoliageMotionVert, ShaderSources.ModelMotionFrag);

    IGpuBuffer CreatePrevious(uint capacity) => _gd.Factory.CreateBuffer(new GpuBufferDescription(
        capacity * MatrixBytes, GpuBufferUsage.StructuredBufferReadOnly, MatrixBytes));

    /// <summary>Upload this frame's motion block, whole.</summary>
    internal void UploadFrame(IGpuCommandList cl, in MotionFrameUbo frame) => cl.UpdateBuffer(_frame, 0, in frame);

    /// <summary>Upload the motion slots and the previous transforms. Both grow geometrically and retire what they
    /// replace, because an earlier frame may still read it.</summary>
    internal void UploadRigid(IGpuCommandList cl, ReadOnlySpan<float> slots, ReadOnlySpan<Matrix4x4> previous)
    {
        if (slots.Length == 0) return;
        if (_slots is null || _slotCapacity < slots.Length)
        {
            _retired.Retire(_slots);
            _slotCapacity = Math.Max((uint)slots.Length, _slotCapacity == 0 ? 256u : _slotCapacity * 2);
            _slots = _gd.Factory.CreateBuffer(new GpuBufferDescription(_slotCapacity * 4, GpuBufferUsage.VertexBuffer));
        }
        cl.UpdateBuffer(_slots, 0, slots);
        if (previous.Length == 0) return;
        if (_previousCapacity < previous.Length)
        {
            _retired.Retire(RigidSet, _previous, null);
            _previousCapacity = Math.Max((uint)previous.Length, _previousCapacity * 2);
            _previous = CreatePrevious(_previousCapacity);
            RigidSet = _gd.Factory.CreateResourceSet(new GpuResourceSetDescription(RigidLayout, _frame, _previous));
        }
        cl.UpdateBuffer(_previous, 0, previous);
    }

    /// <summary>Upload the CPU-skinned last-frame positions, growing geometrically like the deformed vertex stream.</summary>
    internal void UploadCpuPrevious(IGpuCommandList cl, ReadOnlySpan<Vector3> positions)
    {
        if (positions.Length == 0) return;
        if (_cpuPrevious is null || _cpuPreviousCapacity < positions.Length)
        {
            _retired.Retire(_cpuPrevious);
            _cpuPreviousCapacity = Math.Max((uint)positions.Length, _cpuPreviousCapacity == 0 ? 4096u : _cpuPreviousCapacity * 2);
            _cpuPrevious = _gd.Factory.CreateBuffer(new GpuBufferDescription(_cpuPreviousCapacity * 12, GpuBufferUsage.VertexBuffer));
        }
        cl.UpdateBuffer(_cpuPrevious, 0, positions);
    }

    public void Dispose()
    {
        RigidSet.Dispose();
        RigidLayout.Dispose();
        _previous.Dispose();
        _slots?.Dispose();
        SkinnedPalette.Dispose();
        _skinned?.Dispose();
        _skinnedDissolve?.Dispose();
        FrameSet.Dispose();
        FrameLayout.Dispose();
        _cpuPrevious?.Dispose();
        _cpuSkinned?.Dispose();
        _cpuSkinnedDissolve?.Dispose();
        _foliage?.Dispose();
        _frame.Dispose();
        _rigid?.Dispose();
    }
}
