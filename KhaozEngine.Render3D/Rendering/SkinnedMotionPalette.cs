using System;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D.Rendering;

/// <summary>
/// Last frame's world matrix and composed palette of each GPU-skinned caster, for the skinned temporal variant
/// (TEMPORAL-FOUNDATIONS-DESIGN-2026-09-24 section 3, skinned twice). One slot per caster at the slot index its
/// <see cref="SkinnedBonePalette"/> slot uses, selected by a per-draw dynamic offset, uploaded once a frame as one
/// whole-buffer write for the reason that palette gives. The set also carries the frame's <c>MotionFrame</c> block,
/// because the skinned pipeline already uses sets 0 to 2 and Vulkan guarantees four.
/// </summary>
internal sealed class SkinnedMotionPalette : IDisposable
{
    /// <summary>The block the shader reads: <c>mat4 PrevModel</c> then <c>mat4 prevBones[128]</c>.</summary>
    internal const uint PayloadBytes = 64u + (uint)SkinningMath.MaxBonesPerDraw * 64u;

    /// <summary>One slot rounded up to the 256-byte dynamic-offset alignment: 8448 bytes, 528 D3D11 constants.</summary>
    internal const uint SlotBytes = (PayloadBytes + 255u) & ~255u;

    internal const uint DqPayloadBytes = DualQuaternionPaletteBuffer.MotionPayloadBytes;
    internal const uint DqSlotBytes = DualQuaternionPaletteBuffer.MotionSlotBytes;

    readonly DualQuaternionPaletteBuffer? _dq;
    readonly IGpuDevice _gd;
    readonly IGpuBuffer _frame;
    readonly GpuRetireQueue _retired;
    IGpuBuffer? _buffer;
    IGpuResourceSet? _set;
    uint _slots;
    byte[] _image = Array.Empty<byte>();

    internal SkinnedMotionPalette(IGpuDevice gd, IGpuBuffer motionFrame, GpuRetireQueue retired,
        SkinningMethod method = SkinningMethod.LinearBlend)
    {
        _gd = gd;
        _frame = motionFrame;
        _retired = retired;
        if (method == SkinningMethod.DualQuaternionRigid)
        {
            _dq = new DualQuaternionPaletteBuffer(gd, retired, motionFrame);
            Layout = _dq.Layout;
            return;
        }
        if (method != SkinningMethod.LinearBlend) throw new ArgumentOutOfRangeException(nameof(method));
        Layout = gd.Factory.CreateResourceLayout(new GpuResourceLayoutDescription(
            new GpuResourceLayoutElement("MotionFrame", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex),
            new GpuResourceLayoutElement("PrevPalette", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex, dynamic: true)));
    }

    /// <summary>Set 3 of the skinned variant: the motion block, then this caster's last-frame window.</summary>
    internal IGpuResourceLayout Layout { get; }

    /// <summary>The single-slot window set, rebased per draw by <see cref="OffsetFor"/>.</summary>
    internal IGpuResourceSet Set => _dq?.Set ?? _set ?? throw new InvalidOperationException(
        "EnsureCapacity has not run, so no skinned motion slot exists to bind.");

    /// <summary>The slot buffer, null before <see cref="EnsureCapacity"/>. For tests.</summary>
    internal IGpuBuffer? BufferForTests => _dq?.Buffer ?? _buffer;

    /// <summary>The dynamic offset that selects caster <paramref name="slot"/>.</summary>
    internal static uint OffsetFor(uint slot) => slot * SlotBytes;

    internal static uint DqOffsetFor(uint slot) => checked(slot * DqSlotBytes);

    /// <summary>Hold at least <paramref name="slotCount"/> casters, growing geometrically and retiring what it
    /// replaces. The CPU image carries across a grow.</summary>
    internal void EnsureCapacity(uint slotCount)
    {
        if (_dq is not null) { _dq.EnsureCapacity(slotCount); return; }
        if (_buffer != null && _slots >= slotCount) return;
        _retired.Retire(_buffer, _set, null);
        _slots = Math.Max(slotCount, _slots == 0 ? 8u : _slots * 2);
        var image = new byte[checked((int)(_slots * SlotBytes))];
        _image.AsSpan().CopyTo(image);
        _image = image;
        _buffer = _gd.Factory.CreateBuffer(new GpuBufferDescription(_slots * SlotBytes, GpuBufferUsage.UniformBuffer));
        _set = _gd.Factory.CreateResourceSet(new GpuResourceSetDescription(
            Layout, _frame, new GpuBufferRange(_buffer, 0, SlotBytes)));
    }

    /// <summary>Pack one caster's last frame. Only its own bones are written, and its indices are load-validated below
    /// its bone count, so the rest of the slot is never read.</summary>
    internal void Pack(uint slot, in Matrix4x4 previousWorld, ReadOnlySpan<Matrix4x4> previousBones)
    {
        if (_dq is not null) throw new InvalidOperationException("DQ motion palettes require real/dual joint pairs.");
        Span<byte> destination = _image.AsSpan(checked((int)(slot * SlotBytes)), checked((int)PayloadBytes));
        MemoryMarshal.Write(destination, in previousWorld);
        MemoryMarshal.AsBytes(previousBones).CopyTo(destination[64..]);
    }

    internal void Pack(uint slot, in Matrix4x4 previousWorld, ReadOnlySpan<RigidDualQuaternion> previousBones)
    {
        if (_dq is null) throw new InvalidOperationException("LBS motion palettes require matrix joints.");
        _dq.PackPrevious(slot, previousWorld, previousBones);
    }

    /// <summary>Upload every packed slot in one whole-buffer write. Returns the bytes uploaded.</summary>
    internal long Upload(IGpuCommandList cl)
    {
        if (_dq is not null) return _dq.Upload(cl);
        cl.UpdateBuffer(_buffer!, 0, (ReadOnlySpan<byte>)_image);
        return _image.Length;
    }

    public void Dispose()
    {
        if (_dq is not null) { _dq.Dispose(); return; }
        _set?.Dispose();
        _buffer?.Dispose();
        Layout.Dispose();
    }
}
