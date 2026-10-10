using System;
using System.Collections.Generic;

namespace KhaozEngine.MapDoc.Surfaces;

/// <summary>A bounded authored rectangle within a signed 64-cell patch slot.</summary>
public sealed partial class MapSurfacePatch
{
    public MapPatchKey Key { get; set; }
    public int CellMinX { get; set; }
    public int CellMinZ { get; set; }
    public int Width { get; set; }
    public int Depth { get; set; }
    public int[] Heights { get; set; } = Array.Empty<int>();
    public MapSurfaceCell[] Cells { get; set; } = Array.Empty<MapSurfaceCell>();
    public ulong[] Presence { get; set; } = Array.Empty<ulong>();
    public List<MapCornerDependency> CornerDependencies { get; } = new();
    public List<MapEdgeSubdivision> EdgeSubdivisions { get; } = new();

    internal static bool ValidRectangle(int minX, int minZ, int width, int depth) =>
        minX is >= 0 and < MapPatchKey.SlotCells && minZ is >= 0 and < MapPatchKey.SlotCells &&
        width is >= 1 and <= MapPatchKey.SlotCells && depth is >= 1 and <= MapPatchKey.SlotCells &&
        minX + width <= MapPatchKey.SlotCells && minZ + depth <= MapPatchKey.SlotCells;

    int CellIndex(int x, int z)
    {
        if (!ValidRectangle(CellMinX, CellMinZ, Width, Depth) || x < 0 || x >= Width || z < 0 || z >= Depth)
            throw new MapDocumentException("cell outside patch rectangle");
        return z * Width + x;
    }
    public bool IsPresent(int x, int z)
    {
        int index = CellIndex(x, z);
        if (Presence is null || Presence.Length != (Width * Depth + 63) / 64)
            throw new MapDocumentException("invalid presence length");
        return (Presence[index / 64] & (1UL << (index % 64))) != 0;
    }
    public void SetPresent(int x, int z, bool present)
    {
        int index = CellIndex(x, z);
        _ = IsPresent(x, z);
        ulong bit = 1UL << (index % 64);
        if (present) Presence[index / 64] |= bit;
        else Presence[index / 64] &= ~bit;
    }
    void RequireCorner(int x, int z)
    {
        if (!ValidRectangle(CellMinX, CellMinZ, Width, Depth) || x < 0 || x > Width || z < 0 || z > Depth)
            throw new MapDocumentException("corner outside patch rectangle");
    }
    public int Height(int cornerX, int cornerZ)
    {
        RequireCorner(cornerX, cornerZ);
        if (Heights is null || Heights.Length != (Width + 1) * (Depth + 1))
            throw new MapDocumentException("invalid heights length");
        return Heights[cornerZ * (Width + 1) + cornerX];
    }
    public MapLatticeAddress CornerAddress(int cornerX, int cornerZ)
    {
        RequireCorner(cornerX, cornerZ);
        Int128 x = (Int128)Key.SlotX * MapPatchKey.SlotCells + CellMinX + cornerX;
        Int128 z = (Int128)Key.SlotZ * MapPatchKey.SlotCells + CellMinZ + cornerZ;
        if (x < long.MinValue || x > long.MaxValue || z < long.MinValue || z > long.MaxValue)
            throw new MapExactOverflowException();
        return MapLatticeAddress.Corner((long)x, (long)z);
    }
    public MapSurfacePatch Clone()
    {
        var copy = new MapSurfacePatch
        {
            Key = Key,
            CellMinX = CellMinX,
            CellMinZ = CellMinZ,
            Width = Width,
            Depth = Depth,
            Heights = (int[])Heights.Clone(),
            Cells = (MapSurfaceCell[])Cells.Clone(),
            Presence = (ulong[])Presence.Clone(),
        };
        // Metadata records and their nested values are immutable. Lists and all mutable arrays are copied.
        copy.CornerDependencies.AddRange(CornerDependencies);
        copy.EdgeSubdivisions.AddRange(EdgeSubdivisions);
        CloneRecordsTo(copy);
        return copy;
    }

    public IReadOnlyList<string> ValidateLocal()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Key.SurfaceId)) errors.Add("surface id is required");
        if (!ValidRectangle(CellMinX, CellMinZ, Width, Depth))
        {
            errors.Add("invalid patch width, depth or cell minimum");
            return errors;
        }
        int count = Width * Depth;
        if (Heights is null || Heights.Length != (Width + 1) * (Depth + 1)) errors.Add("invalid heights length");
        if (Cells is null || Cells.Length != count) errors.Add("invalid cells length");
        else
            foreach (MapSurfaceCell cell in Cells)
            {
                if (cell.Rotation > 3) errors.Add("invalid cell rotation");
                if ((byte)cell.Flags > 31) errors.Add("invalid cell flags");
                if (cell.Cut > MapOverlayCut.CornerThreeQuarter) errors.Add("invalid cell cut");
                if (cell.Topology > MapCellTopology.ForceNwSe) errors.Add("invalid cell topology");
            }
        bool validPresence = Presence is not null && Presence.Length == (count + 63) / 64;
        if (!validPresence) errors.Add("invalid presence length");
        else if (count % 64 != 0 && (Presence![^1] >> (count % 64)) != 0) errors.Add("nonzero unused presence bits");
        if (CornerDependencies.Count > (Width + 1) * (Depth + 1)) errors.Add("too many corner dependencies");
        if (EdgeSubdivisions.Count > count * 4) errors.Add("too many edge subdivisions");
        if (errors.Count != 0) return errors;

        var corners = new HashSet<(int, int)>();
        foreach (MapCornerDependency dependency in CornerDependencies)
        {
            if (dependency is null || dependency.CornerX < 0 || dependency.CornerX > Width ||
                dependency.CornerZ < 0 || dependency.CornerZ > Depth || dependency.Owner is null ||
                string.IsNullOrWhiteSpace(dependency.Owner.Patch.SurfaceId))
            {
                errors.Add("invalid corner dependency");
                continue;
            }
            if (!corners.Add((dependency.CornerX, dependency.CornerZ))) errors.Add("duplicate corner dependency");
            try { dependency.Owner.Address.RequireValid(); }
            catch (MapDocumentException) { errors.Add("invalid dependency address denominator"); }
        }
        var edges = new HashSet<(int, int, MapCellEdge)>();
        foreach (MapEdgeSubdivision edge in EdgeSubdivisions)
        {
            if (edge is null || edge.CellX < 0 || edge.CellX >= Width || edge.CellZ < 0 || edge.CellZ >= Depth ||
                edge.Edge > MapCellEdge.West)
            {
                errors.Add("invalid subdivision edge");
                continue;
            }
            if (edge.Segments is < 2 or > 64) errors.Add("subdivision segments must be 2 to 64");
            if (!edges.Add((edge.CellX, edge.CellZ, edge.Edge))) errors.Add("duplicate edge subdivision");
            if (!IsPresent(edge.CellX, edge.CellZ)) errors.Add("subdivision on absent cell");
            (int x, int z) = edge.Edge switch
            {
                MapCellEdge.South => (edge.CellX, edge.CellZ - 1),
                MapCellEdge.East => (edge.CellX + 1, edge.CellZ),
                MapCellEdge.North => (edge.CellX, edge.CellZ + 1),
                _ => (edge.CellX - 1, edge.CellZ),
            };
            if (x >= 0 && x < Width && z >= 0 && z < Depth && IsPresent(x, z))
                errors.Add("subdivision on interior present-cell edge");
        }
        return errors;
    }
}
