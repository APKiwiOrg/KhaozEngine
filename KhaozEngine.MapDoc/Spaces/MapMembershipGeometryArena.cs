using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Spaces;

/// <summary>Frozen exact geometry columns. Handles preserve input order without retaining per-product arrays.</summary>
internal sealed class MapMembershipGeometryArena
{
    internal readonly record struct PolygonRow(MapExactRect Bounds, int Vertex, int Count, int Affine, int Flags);
    internal readonly record struct TriangleRow(MapExactTriangle Triangle, int Affine, int Orientation, int Owns, int Reserved);
    internal readonly record struct FaceRow(int Owner, int Patch, int Primitive, int Triangle, int Parent, int Child, int Side, int Reserved);
    internal readonly record struct CellRow(int Surface, int Patch, int Slot, int Physical, int Polygon,
        int Triangle, int Count, int Face, int SpanLower, int SpanUpper, int LegacyX, int LegacyZ,
        int FaceSet, int Flags, int Rank, int SpanCount);
    internal readonly MapMembershipArenaPages<PolygonRow> Polygons;
    internal readonly MapMembershipArenaPages<MapExactXz> Vertices;
    internal readonly MapMembershipArenaPages<byte> Corners;
    internal readonly MapMembershipArenaPages<TriangleRow> Triangles;
    internal readonly MapMembershipArenaPages<FaceRow> Faces;
    internal readonly MapMembershipArenaPages<CellRow> Cells;
    internal readonly MapMembershipCoefficientStore Coefficients;
    internal readonly MapMembershipArenaPages<MapSurfaceRef> Surfaces;
    internal readonly MapMembershipArenaPages<MapSurfacePatch> Patches;
    internal readonly MapMembershipArenaPages<MapResourceTransform?> Transforms;
    readonly MapMembershipArenaPages<string> _owners;
    readonly MapMembershipArenaPages<MapPatchKey?> _keys;
    MapMembershipGeometryArena()
    {
        Polygons = MapMembershipArenaPages<PolygonRow>.Create(64);
        Vertices = MapMembershipArenaPages<MapExactXz>.Create(128);
        Corners = MapMembershipArenaPages<byte>.Create(128);
        Triangles = MapMembershipArenaPages<TriangleRow>.Create(32);
        Faces = MapMembershipArenaPages<FaceRow>.Create(128);
        Cells = MapMembershipArenaPages<CellRow>.Create(64);
        Coefficients = MapMembershipCoefficientStore.Create();
        Surfaces = MapMembershipArenaPages<MapSurfaceRef>.Create(32);
        Patches = MapMembershipArenaPages<MapSurfacePatch>.Create(32);
        Transforms = MapMembershipArenaPages<MapResourceTransform?>.Create(32);
        _owners = MapMembershipArenaPages<string>.Create(32);
        _keys = MapMembershipArenaPages<MapPatchKey?>.Create(32);
    }
    internal static MapMembershipGeometryArena Create() { MapMembershipBuild.Inventory(112); return new(); }
    internal long InventoryBytes => checked(112 + Polygons.InventoryBytes + Vertices.InventoryBytes + Corners.InventoryBytes +
        Triangles.InventoryBytes + Faces.InventoryBytes + Cells.InventoryBytes + Coefficients.InventoryBytes +
        Surfaces.InventoryBytes + Patches.InventoryBytes + Transforms.InventoryBytes + _owners.InventoryBytes + _keys.InventoryBytes);
    internal IEnumerable<(object Identity, long Bytes)> Inventory()
    {
        yield return (this, 112);
        foreach (var p in Polygons.Inventory()) yield return p;
        foreach (var p in Vertices.Inventory()) yield return p;
        foreach (var p in Corners.Inventory()) yield return p;
        foreach (var p in Triangles.Inventory()) yield return p;
        foreach (var p in Faces.Inventory()) yield return p;
        foreach (var p in Cells.Inventory()) yield return p;
        foreach (var p in Coefficients.Inventory()) yield return p;
        foreach (var p in Surfaces.Inventory()) yield return p;
        foreach (var p in Patches.Inventory()) yield return p;
        foreach (var p in Transforms.Inventory()) yield return p;
        foreach (var p in _owners.Inventory()) yield return p;
        foreach (var p in _keys.Inventory()) yield return p;
    }
    internal int Surface(MapScopedSurfaces view, MapSurfaceRef surface)
    {
        for (int i = 0; i < Surfaces.Count; i++) if (Surfaces[i].Id == surface.Id) return i;
        int index = Surfaces.Append(surface);
        Transforms.Append(view.EffectiveContext?.Transform(MapPrefabChildKind.Surface, surface.Id));
        return index;
    }
    internal int Patch(MapSurfacePatch patch)
    {
        for (int i = 0; i < Patches.Count; i++) if (Patches[i].Key == patch.Key) return i;
        return Patches.Append(patch);
    }
    internal void Face(MapFaceKey key, int triangle)
    {
        int owner = -1, patch = -1;
        for (int i = 0; i < _owners.Count; i++) if (_owners[i] == key.OwnerId) { owner = i; break; }
        for (int i = 0; i < _keys.Count; i++) if (_keys[i] == key.Patch) { patch = i; break; }
        if (owner < 0) owner = _owners.Append(key.OwnerId);
        if (patch < 0) patch = _keys.Append(key.Patch);
        Faces.Append(new(owner, patch, key.Primitive, triangle, key.ParentTriangle, key.Child, (int)key.Side, 0));
        if (MapMembershipBuild.Current is { } build) build.Counts.FaceKeyRows++;
    }
    internal MapFaceKey Key(int index)
    {
        var f = Faces[index];
        return new(_owners[f.Owner], _keys[f.Patch], f.Primitive, (byte)f.Parent, (ushort)f.Child, (MapSide)f.Side);
    }
    internal MapExactXz Centre(int polygon)
    {
        ref readonly var p = ref Polygons[polygon];
        MapExactValue x = default, z = default;
        for (int i = 0; i < p.Count; i++)
        { x = x.Add(Vertices[p.Vertex + i].X); z = z.Add(Vertices[p.Vertex + i].Z); }
        return new(x.Divide(new(p.Count, 1)), z.Divide(new(p.Count, 1)));
    }
    internal void Freeze()
    {
        Polygons.Freeze(); Vertices.Freeze(); Corners.Freeze(); Triangles.Freeze(); Faces.Freeze(); Cells.Freeze();
        Coefficients.Freeze(); Surfaces.Freeze(); Patches.Freeze(); Transforms.Freeze(); _owners.Freeze(); _keys.Freeze();
        if (MapMembershipBuild.Current is { } build)
        {
            build.Counts.GeometryArenaPages += Polygons.PageCount + Vertices.PageCount + Corners.PageCount +
                Triangles.PageCount + Faces.PageCount + Cells.PageCount + Coefficients.TokenPages + Coefficients.MagnitudePages +
                Surfaces.PageCount + Patches.PageCount + Transforms.PageCount + _owners.PageCount + _keys.PageCount;
            build.Counts.GeometryArenaCapacityBytes += InventoryBytes;
            build.Counts.SideTableRows += Surfaces.Count + Patches.Count + Transforms.Count + _owners.Count + _keys.Count;
            build.Counts.SideTableBytes += Surfaces.InventoryBytes + Patches.InventoryBytes + Transforms.InventoryBytes + _owners.InventoryBytes + _keys.InventoryBytes;
        }
    }
}

internal readonly record struct MapMembershipPolygonRange(MapMembershipGeometryArena Arena, int Start, int Length)
{ internal MapMembershipPolygon this[int index] => new(Arena, checked(Start + index)); }
internal readonly record struct MapMembershipTriangleRange(MapMembershipGeometryArena Arena, int Start, int Length)
{ internal MapMembershipTriangle this[int index] => new(Arena, checked(Start + index)); }
