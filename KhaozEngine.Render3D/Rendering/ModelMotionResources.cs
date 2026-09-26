using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering;

/// <summary>
/// What the model pass's temporal variants read (TEMPORAL-FOUNDATIONS-DESIGN-2026-09-24 section 4). Created on the first
/// build against a model target that carries the motion attachment and retired when the target loses it, so with
/// temporal rendering off none of it exists. Programs compile on first use, so a scene pays only for the paths it draws.
/// Later tasks add the CPU-skinned, foliage and ground members.
/// </summary>
internal sealed class ModelMotionResources : IDisposable
{
    /// <summary>The rigid variant's per-instance motion slot: vertex buffer slot 2, location 15, one float per instance,
    /// parallel to the instance stream so a draw's first instance selects both.</summary>
    internal static readonly GpuVertexLayoutDescription SlotLayout = new(stride: 4, instanceStepRate: 1,
        elements: new[] { new GpuVertexElement("IMotionSlot", GpuVertexElementFormat.Float1) });

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

    internal ModelMotionResources(IGpuDevice gd, GpuRetireQueue retired)
    {
        _gd = gd;
        _retired = retired;
        IGpuResourceFactory f = gd.Factory;
        _frame = f.CreateBuffer(new GpuBufferDescription(MotionFrameUbo.SizeInBytes, GpuBufferUsage.UniformBuffer));
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

    public void Dispose()
    {
        RigidSet.Dispose();
        RigidLayout.Dispose();
        _previous.Dispose();
        _slots?.Dispose();
        SkinnedPalette.Dispose();
        _skinned?.Dispose();
        _skinnedDissolve?.Dispose();
        _frame.Dispose();
        _rigid?.Dispose();
    }
}
