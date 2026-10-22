using System;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D.Rendering;

/// <summary>Owns interleaved real.xyzw/dual.xyzw joint words and their GPU lifetime. Current and outline
/// palettes have no matrix header. Motion palettes prepend only PrevModel, never a matrix bone array.</summary>
internal sealed class DualQuaternionPaletteBuffer : IDisposable
{
    internal const uint SlotBytes = 4096u;
    internal const uint MotionPayloadBytes = 64u + SlotBytes;
    internal const uint MotionSlotBytes = (MotionPayloadBytes + 255u) & ~255u;

    readonly IGpuDevice _device;
    readonly GpuRetireQueue _retired;
    readonly IGpuBuffer? _motionFrame;
    readonly uint _stride;
    IGpuBuffer? _buffer;
    IGpuResourceSet? _set;
    uint _slots;
    byte[] _image = Array.Empty<byte>();
    bool _disposed;

    internal DualQuaternionPaletteBuffer(IGpuDevice device, GpuRetireQueue retired, IGpuBuffer? motionFrame = null)
    {
        _device = device;
        _retired = retired;
        _motionFrame = motionFrame;
        _stride = motionFrame is null ? SlotBytes : MotionSlotBytes;
        Layout = device.Factory.CreateResourceLayout(motionFrame is null
            ? new GpuResourceLayoutDescription(new GpuResourceLayoutElement("Palette",
                GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex, dynamic: true))
            : new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("MotionFrame", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex),
                new GpuResourceLayoutElement("PrevPalette", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex, dynamic: true)));
    }

    internal IGpuResourceLayout Layout { get; }
    internal IGpuResourceSet Set => _set ?? throw new InvalidOperationException("DQ palette capacity has not been allocated.");
    internal IGpuBuffer? Buffer => _buffer;
    internal uint OffsetFor(uint slot) => checked(slot * _stride);

    internal void EnsureCapacity(uint slots)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (slots == 0 || _buffer is not null && _slots >= slots) return;
        uint capacity = Math.Max(slots, _slots == 0 ? 8u : checked(_slots * 2));
        var image = new byte[checked((int)(capacity * _stride))];
        _image.AsSpan().CopyTo(image);
        IGpuBuffer buffer = _device.Factory.CreateBuffer(new GpuBufferDescription((uint)image.Length, GpuBufferUsage.UniformBuffer));
        IGpuResourceSet set;
        try
        {
            var range = new GpuBufferRange(buffer, 0, _stride);
            set = _device.Factory.CreateResourceSet(_motionFrame is null
                ? new GpuResourceSetDescription(Layout, range)
                : new GpuResourceSetDescription(Layout, _motionFrame, range));
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
        _retired.Retire(_set, _buffer, null);
        _slots = capacity;
        _image = image;
        _buffer = buffer;
        _set = set;
    }

    internal void Pack(uint slot, ReadOnlySpan<RigidDualQuaternion> palette)
    {
        if (_motionFrame is not null) throw new InvalidOperationException("A motion palette requires PrevModel.");
        WritePalette(slot, palette, 0);
    }

    internal void PackPrevious(uint slot, in Matrix4x4 previousWorld, ReadOnlySpan<RigidDualQuaternion> palette)
    {
        if (_motionFrame is null) throw new InvalidOperationException("Only a motion palette accepts PrevModel.");
        WritePalette(slot, palette, 64);
        MemoryMarshal.Write(_image.AsSpan(checked((int)OffsetFor(slot)), 64), in previousWorld);
    }

    void WritePalette(uint slot, ReadOnlySpan<RigidDualQuaternion> palette, int headerBytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (slot >= _slots) throw new ArgumentOutOfRangeException(nameof(slot));
        if (palette.Length > SkinningMath.MaxBonesPerDraw)
            throw new ArgumentException("DQ palette exceeds the per-draw joint limit.", nameof(palette));
        // Copy the prepared binary32 words exactly, including signed zeros. No conversion or arithmetic.
        MemoryMarshal.AsBytes(palette).CopyTo(_image.AsSpan(checked((int)OffsetFor(slot) + headerBytes), (int)SlotBytes));
    }

    /// <summary>The whole allocated image is uploaded once. Includes unused capacity and motion alignment.</summary>
    internal long Upload(IGpuCommandList commands)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_buffer is null) return 0;
        commands.UpdateBuffer(_buffer, 0, (ReadOnlySpan<byte>)_image);
        return _image.Length;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _set?.Dispose();
        _buffer?.Dispose();
        Layout.Dispose();
        _set = null;
        _buffer = null;
        _image = Array.Empty<byte>();
    }
}
