using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D.Rendering;

internal sealed class TargetOutlineSkinningStore : IDisposable
{
    internal static readonly uint PaletteSlotBytes =
        (uint)SkinningMath.MaxBonesPerDraw * 64;

    readonly IGpuDevice _device;
    readonly List<IDisposable> _retired = new();
    IGpuBuffer? _paletteBuffer;
    IGpuResourceSet? _paletteSet;
    byte[] _paletteImage = Array.Empty<byte>();
    int _paletteCapacity;
    IGpuBuffer? _cpuVertexBuffer;
    ModelVertex[] _cpuVertexImage = Array.Empty<ModelVertex>();
    int _cpuVertexCapacity;

    internal TargetOutlineSkinningStore(IGpuDevice device)
    {
        _device = device;
        PaletteLayout = device.Factory.CreateResourceLayout(new GpuResourceLayoutDescription(
            new GpuResourceLayoutElement("Palette", GpuResourceKind.UniformBuffer,
                GpuShaderStages.Vertex, dynamic: true)));
    }

    internal IGpuResourceLayout PaletteLayout { get; }
    internal IGpuResourceSet PaletteSet => _paletteSet
        ?? throw new InvalidOperationException("outline palette capacity has not been allocated.");

    internal void EnsurePaletteCapacity(int slotCount)
    {
        if (slotCount <= 0 || _paletteBuffer is not null && _paletteCapacity >= slotCount) return;
        int freshCapacity = Math.Max(slotCount, _paletteCapacity == 0 ? 4 : _paletteCapacity * 2);
        var freshImage = new byte[freshCapacity * PaletteSlotBytes];
        _paletteImage.AsSpan().CopyTo(freshImage);
        IGpuBuffer freshBuffer = _device.Factory.CreateBuffer(new GpuBufferDescription(
            (uint)freshImage.Length, GpuBufferUsage.UniformBuffer));
        IGpuResourceSet freshSet;
        try
        {
            freshSet = _device.Factory.CreateResourceSet(new GpuResourceSetDescription(PaletteLayout,
                new GpuBufferRange(freshBuffer, 0, PaletteSlotBytes)));
        }
        catch
        {
            freshBuffer.Dispose();
            throw;
        }

        if (_paletteSet is not null) _retired.Add(_paletteSet);
        if (_paletteBuffer is not null) _retired.Add(_paletteBuffer);
        _paletteImage = freshImage;
        _paletteBuffer = freshBuffer;
        _paletteSet = freshSet;
        _paletteCapacity = freshCapacity;
    }

    internal void PackPalette(int slot, ReadOnlySpan<Matrix4x4> bones)
    {
        if ((uint)slot >= (uint)_paletteCapacity) throw new ArgumentOutOfRangeException(nameof(slot));
        if (bones.Length > SkinningMath.MaxBonesPerDraw)
            throw new ArgumentException("outline palette exceeds the per-draw bone limit.", nameof(bones));
        Span<byte> bytes = _paletteImage.AsSpan(slot * (int)PaletteSlotBytes, (int)PaletteSlotBytes);
        Span<Matrix4x4> matrices = MemoryMarshal.Cast<byte, Matrix4x4>(bytes);
        matrices.Fill(Matrix4x4.Identity);
        bones.CopyTo(matrices);
    }

    internal void UploadPalette(IGpuCommandList commands)
    {
        if (_paletteBuffer is null) return;
        commands.UpdateBuffer(_paletteBuffer, 0, (ReadOnlySpan<byte>)_paletteImage);
    }

    internal IGpuBuffer EnsureCpuVertexCapacity(int vertexCount)
    {
        if (_cpuVertexBuffer is not null && _cpuVertexCapacity >= vertexCount) return _cpuVertexBuffer;
        int freshCapacity = Math.Max(vertexCount, _cpuVertexCapacity == 0 ? 256 : _cpuVertexCapacity * 2);
        IGpuBuffer freshBuffer = _device.Factory.CreateBuffer(new GpuBufferDescription(
            (uint)(freshCapacity * ModelVertex.SizeInBytes), GpuBufferUsage.VertexBuffer));
        var freshImage = new ModelVertex[freshCapacity];
        _cpuVertexImage.AsSpan().CopyTo(freshImage);
        if (_cpuVertexBuffer is not null) _retired.Add(_cpuVertexBuffer);
        _cpuVertexBuffer = freshBuffer;
        _cpuVertexImage = freshImage;
        _cpuVertexCapacity = freshCapacity;
        return freshBuffer;
    }

    internal void UploadCpuVertices(IGpuCommandList commands, ReadOnlySpan<ModelVertex> vertices)
    {
        if (vertices.IsEmpty) return;
        IGpuBuffer buffer = EnsureCpuVertexCapacity(vertices.Length);
        vertices.CopyTo(_cpuVertexImage);
        commands.UpdateBuffer(buffer, 0, _cpuVertexImage.AsSpan(0, vertices.Length));
    }

    public void Dispose()
    {
        _paletteSet?.Dispose();
        _paletteBuffer?.Dispose();
        _cpuVertexBuffer?.Dispose();
        foreach (IDisposable resource in _retired) resource.Dispose();
        PaletteLayout.Dispose();
    }
}
