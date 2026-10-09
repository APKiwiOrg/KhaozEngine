using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Spaces;

namespace KhaozEngine.MapDoc.Surfaces;

public readonly record struct MapFaceKey(string OwnerId, MapPatchKey? Patch, int Primitive,
    byte ParentTriangle, ushort Child, MapSide Side) : IComparable<MapFaceKey>
{
    public int CompareTo(MapFaceKey other)
    {
        int order = StringComparer.Ordinal.Compare(OwnerId, other.OwnerId);
        if (order != 0) return order;
        order = Nullable.Compare(Patch, other.Patch);
        if (order != 0) return order;
        order = Primitive.CompareTo(other.Primitive);
        if (order != 0) return order;
        order = ParentTriangle.CompareTo(other.ParentTriangle);
        if (order != 0) return order;
        order = Child.CompareTo(other.Child);
        return order != 0 ? order : Side.CompareTo(other.Side);
    }
}

public enum MapFaceRole : byte { SupportFloor, Ceiling, Wall }
public readonly record struct MapVertexId(string SurfaceId, MapLatticeAddress Address);
public readonly record struct MapSubmissionAnchor(long X, long Y, long Z);
public readonly record struct MapExactTriangle(MapExactPoint A, MapExactPoint B, MapExactPoint C);
public readonly record struct MapCompiledFace(MapFaceKey Key, MapFaceRole Role, int A, int B, int C, Vector3 Normal);
public readonly record struct MapPaintCoverage(MapFaceKey Face, ushort Underlay, ushort Overlay,
    bool OverlayCovers, bool Feather);
public sealed record MapFallbackCell(int SlotCell, int Sw, int Se, int Nw, int Ne);

/// <summary>An immutable canonical patch with exact world geometry and local submission positions.</summary>
public sealed class MapCompiledPatch
{
    public MapPatchKey Key { get; }
    public MapSurfaceRole Role { get; }
    public MapSubmissionAnchor Anchor { get; }
    public IReadOnlyList<MapVertexId> VertexIds { get; }
    public IReadOnlyList<MapExactPoint> ExactVertices { get; }
    public IReadOnlyList<Vector3> Offsets { get; }
    public IReadOnlyList<MapCompiledFace> Faces { get; }
    public IReadOnlyList<MapPaintCoverage> Paint { get; }
    public IReadOnlyList<MapFallbackCell> LegacyFallbackCells { get; }

    internal MapCompiledPatch(MapPatchKey key, MapSurfaceRole role, MapSubmissionAnchor anchor,
        IEnumerable<MapVertexId> vertexIds, IEnumerable<MapExactPoint> exactVertices, IEnumerable<Vector3> offsets,
        IEnumerable<MapCompiledFace> faces, IEnumerable<MapPaintCoverage> paint, IEnumerable<MapFallbackCell> fallback)
    {
        Key = key;
        Role = role;
        Anchor = anchor;
        VertexIds = Array.AsReadOnly(vertexIds.ToArray());
        ExactVertices = Array.AsReadOnly(exactVertices.ToArray());
        Offsets = Array.AsReadOnly(offsets.ToArray());
        Faces = Array.AsReadOnly(faces.ToArray());
        Paint = Array.AsReadOnly(paint.ToArray());
        LegacyFallbackCells = Array.AsReadOnly(fallback.ToArray());
    }

    public MapExactTriangle ExactTriangle(MapCompiledFace face) =>
        new(ExactVertices[face.A], ExactVertices[face.B], ExactVertices[face.C]);
}
