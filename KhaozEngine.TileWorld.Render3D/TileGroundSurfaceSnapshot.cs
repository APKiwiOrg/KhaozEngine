using System;
using System.Numerics;
using System.Threading;
using KhaozEngine.Render3D;

namespace KhaozEngine.TileWorld;

/// <summary>A process-unique completed surface identity. Never recycled on unload or view recreation.</summary>
internal readonly record struct TileGroundSurfaceId(RegionCoord Region, int Plane, long Generation);

/// <summary>Position-only copy of the completed mesh. Original uint indices and vertex ordinals are retained.
/// Compact means removal of normals, colours and material data, not welding or retriangulation.</summary>
internal sealed class TileGroundPositionPayload
{
    readonly Vector3[] _positions;
    readonly uint[] _indices;

    internal TileGroundPositionPayload(GltfMesh? mesh)
    {
        if (mesh is null)
        {
            _positions = [];
            _indices = [];
            return;
        }
        _positions = new Vector3[mesh.Vertices.Length];
        for (int i = 0; i < _positions.Length; i++) _positions[i] = mesh.Vertices[i].Position;
        _indices = (uint[])mesh.Indices32.Clone();
        if (_indices.Length % 3 != 0) throw new ArgumentException("Ground indices must be triangles.", nameof(mesh));
        foreach (uint index in _indices)
            if (index >= _positions.Length) throw new ArgumentException("Ground index is out of range.", nameof(mesh));
        RetainedBytes = checked(12L * _positions.Length + 4L * _indices.Length);
    }

    internal ReadOnlySpan<Vector3> Positions => _positions;
    internal ReadOnlySpan<uint> Indices => _indices;
    // Owned array storage only. Managed headers and GPU/mesh storage are not charged here.
    internal long RetainedBytes { get; }
}

/// <summary>The exact uploaded surface and its granular source witness, with no scene handles or mutable mesh.
/// A non-null empty snapshot is known empty. A missing accepted snapshot is unavailable.</summary>
internal sealed class TileGroundSurfaceSnapshot
{
    static long _generation;

    internal TileGroundSurfaceSnapshot(TileWorldDocument document, RegionCoord region, int plane, TileGroundLod lod,
        GltfMesh? mesh, TileGroundSurfaceAudit sourceWitness)
    {
        Id = new TileGroundSurfaceId(region, plane, NextGeneration());
        Lod = lod;
        TileSize = document.TileSize;
        WorldTransform = TileGroundMesher.WorldMatrix(document, region);
        Payload = new TileGroundPositionPayload(mesh);
        SourceWitness = sourceWitness;
        RetainedBytes = checked(Payload.RetainedBytes + SourceWitness.RetainedBytes);
    }

    static long NextGeneration()
    {
        while (true)
        {
            long previous = Interlocked.Read(ref _generation);
            long next = checked(previous + 1);
            if (Interlocked.CompareExchange(ref _generation, next, previous) == previous) return next;
        }
    }

    internal TileGroundSurfaceId Id { get; }
    internal TileGroundLod Lod { get; }
    // Preserve actual binary32 mesh-local coordinates and the exact uploaded transform separately.
    // TileSize and integer Region also let a producer assess source/anchor conversion in double precision.
    internal float TileSize { get; }
    internal Matrix4x4 WorldTransform { get; }
    internal TileGroundPositionPayload Payload { get; }
    internal TileGroundSurfaceAudit SourceWitness { get; }
    internal bool IsEmpty => Payload.Indices.IsEmpty;
    internal long RetainedBytes { get; }
}
