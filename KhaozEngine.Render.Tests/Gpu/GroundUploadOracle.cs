using System;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Restores the pre-compaction upload at the real device boundary, including buffer sizes.</summary>
internal sealed class GroundUploadOracle
{
    GltfMesh? _mesh;
    Vector3[] _outlines = [];
    int _buffer;
    internal int SavedVertices { get; private set; }

    internal MeshHandle Upload(GltfMesh mesh, Func<MeshHandle> upload)
    {
        _mesh = mesh;
        _outlines = OutlineNormalBuilder.Build(mesh.Vertices, mesh.Indices32);
        _buffer = 0;
        try { return upload(); }
        finally { _mesh = null; }
    }

    internal GpuBufferDescription Resize(GpuBufferDescription description)
    {
        if (_mesh is null) return description;
        uint size = _buffer++ switch
        {
            0 => (uint)_mesh.Vertices.Length * ModelVertex.SizeInBytes,
            1 => (uint)_outlines.Length * 12,
            2 => (uint)_mesh.Indices32.Length * (_mesh.IndexFormat == GpuIndexFormat.UInt16 ? 2u : 4u),
            _ => description.SizeInBytes,
        };
        return new(size, description.Usage, description.StructureByteStride);
    }

    internal byte[] Restore(Type type, byte[] bytes)
    {
        if (_mesh is null) return bytes;
        if (type == typeof(ModelVertex))
        {
            SavedVertices += _mesh.Vertices.Length - bytes.Length / (int)ModelVertex.SizeInBytes;
            return MemoryMarshal.AsBytes(_mesh.Vertices.AsSpan()).ToArray();
        }
        if (type == typeof(Vector3)) return MemoryMarshal.AsBytes(_outlines.AsSpan()).ToArray();
        if (type == typeof(ushort)) return MemoryMarshal.AsBytes(_mesh.Indices.AsSpan()).ToArray();
        if (type == typeof(uint)) return MemoryMarshal.AsBytes(_mesh.Indices32.AsSpan()).ToArray();
        return bytes;
    }
}
