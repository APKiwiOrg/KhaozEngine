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
            throw new MapDocumentException(!ValidRectangle(CellMinX, CellMinZ, Width, Depth)
                ? MapDocumentRefusal.BoundsInvalid : MapDocumentRefusal.SurfaceAddressOutOfPatch, "cell outside patch rectangle")
            {
                Patch = Key, Width = Width, Depth = Depth, CellX = x, CellZ = z,
                FieldPath = !ValidRectangle(CellMinX, CellMinZ, Width, Depth) ? "rectangle" : x < 0 || x >= Width ? "x" : "z",
                ActualInteger = !ValidRectangle(CellMinX, CellMinZ, Width, Depth) ? null : x < 0 || x >= Width ? x : z,
                MinimumInteger = !ValidRectangle(CellMinX, CellMinZ, Width, Depth) ? null : 0,
                MaximumInteger = !ValidRectangle(CellMinX, CellMinZ, Width, Depth) ? null : x < 0 || x >= Width ? Width - 1 : Depth - 1,
            };
        return z * Width + x;
    }
    public bool IsPresent(int x, int z)
    {
        int index = CellIndex(x, z);
        if (Presence is null || Presence.Length != (Width * Depth + 63) / 64)
            throw new MapDocumentException(Presence is null ? MapDocumentRefusal.SchemaValueRequired
                : MapDocumentRefusal.SchemaArrayLengthMismatch, "invalid presence length")
            { Patch = Key, FieldPath = nameof(Presence), ActualCount = Presence?.LongLength, ExpectedCount = (Width * Depth + 63) / 64 };
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
            throw new MapDocumentException(!ValidRectangle(CellMinX, CellMinZ, Width, Depth)
                ? MapDocumentRefusal.BoundsInvalid : MapDocumentRefusal.SurfaceAddressOutOfPatch, "corner outside patch rectangle")
            {
                Patch = Key, Width = Width, Depth = Depth, CellX = x, CellZ = z,
                FieldPath = !ValidRectangle(CellMinX, CellMinZ, Width, Depth) ? "rectangle" : x < 0 || x > Width ? "x" : "z",
                ActualInteger = !ValidRectangle(CellMinX, CellMinZ, Width, Depth) ? null : x < 0 || x > Width ? x : z,
                MinimumInteger = !ValidRectangle(CellMinX, CellMinZ, Width, Depth) ? null : 0,
                MaximumInteger = !ValidRectangle(CellMinX, CellMinZ, Width, Depth) ? null : x < 0 || x > Width ? Width : Depth,
            };
    }
    public int Height(int cornerX, int cornerZ)
    {
        RequireCorner(cornerX, cornerZ);
        if (Heights is null || Heights.Length != (Width + 1) * (Depth + 1))
            throw new MapDocumentException(Heights is null ? MapDocumentRefusal.SchemaValueRequired
                : MapDocumentRefusal.SchemaArrayLengthMismatch, "invalid heights length")
            { Patch = Key, FieldPath = nameof(Heights), ActualCount = Heights?.LongLength, ExpectedCount = (Width + 1) * (Depth + 1) };
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

    public IReadOnlyList<string> ValidateLocal() => ValidateLocal(null);

    /// <summary>Returns the local validation rules and their typed subjects in diagnostic order.</summary>
    public IReadOnlyList<MapDocumentFinding> ValidateLocalFindings()
    {
        var findings = new List<MapDocumentFinding>();
        ValidateLocal(findings);
        return findings.AsReadOnly();
    }

    internal IReadOnlyList<string> ValidateLocal(List<MapDocumentFinding>? findings)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Key.SurfaceId))
            Add(new(MapDocumentRefusal.SchemaValueRequired, "surface id is required") { FieldPath = "Key.SurfaceId" });
        if (!ValidRectangle(CellMinX, CellMinZ, Width, Depth))
        {
            Add(new(MapDocumentRefusal.BoundsInvalid, "invalid patch width, depth or cell minimum")
            { FieldPath = "rectangle", Width = Width, Depth = Depth });
            return errors;
        }
        int count = Width * Depth;
        if (Heights is null || Heights.Length != (Width + 1) * (Depth + 1))
            Add(new(Heights is null ? MapDocumentRefusal.SchemaValueRequired : MapDocumentRefusal.SchemaArrayLengthMismatch, "invalid heights length")
            { FieldPath = nameof(Heights), ActualCount = Heights?.LongLength, ExpectedCount = (Width + 1) * (Depth + 1) });
        if (Cells is null || Cells.Length != count)
            Add(new(Cells is null ? MapDocumentRefusal.SchemaValueRequired : MapDocumentRefusal.SchemaArrayLengthMismatch, "invalid cells length")
            { FieldPath = nameof(Cells), ActualCount = Cells?.LongLength, ExpectedCount = count });
        else
            for (int i = 0; i < Cells.Length; i++)
            {
                MapSurfaceCell cell = Cells[i];
                if (cell.Rotation > 3)
                    Add(new(MapDocumentRefusal.ValueOutsideRange, "invalid cell rotation")
                    { FieldPath = $"Cells[{i}].Rotation", Index = i, ActualInteger = cell.Rotation, MinimumInteger = 0, MaximumInteger = 3 });
                if ((byte)cell.Flags > 31)
                    Add(new(MapDocumentRefusal.SchemaEnumUnsupported, "invalid cell flags")
                    { FieldPath = $"Cells[{i}].Flags", Index = i, ActualInteger = (byte)cell.Flags, EnumType = typeof(MapCellFlags) });
                if (cell.Cut > MapOverlayCut.CornerThreeQuarter)
                    Add(new(MapDocumentRefusal.SchemaEnumUnsupported, "invalid cell cut")
                    { FieldPath = $"Cells[{i}].Cut", Index = i, ActualInteger = (byte)cell.Cut, EnumType = typeof(MapOverlayCut) });
                if (cell.Topology > MapCellTopology.ForceNwSe)
                    Add(new(MapDocumentRefusal.SchemaEnumUnsupported, "invalid cell topology")
                    { FieldPath = $"Cells[{i}].Topology", Index = i, ActualInteger = (byte)cell.Topology, EnumType = typeof(MapCellTopology) });
            }
        bool validPresence = Presence is not null && Presence.Length == (count + 63) / 64;
        if (!validPresence)
            Add(new(Presence is null ? MapDocumentRefusal.SchemaValueRequired : MapDocumentRefusal.SchemaArrayLengthMismatch, "invalid presence length")
            { FieldPath = nameof(Presence), ActualCount = Presence?.LongLength, ExpectedCount = (count + 63) / 64 });
        else if (count % 64 != 0 && (Presence![^1] >> (count % 64)) != 0)
            Add(new(MapDocumentRefusal.SurfacePresencePaddingNonzero, "nonzero unused presence bits")
            { FieldPath = nameof(Presence), Index = Presence.Length - 1 });
        if (CornerDependencies.Count > (Width + 1) * (Depth + 1))
            Add(new(MapDocumentRefusal.WorkCapacityExceeded, "too many corner dependencies")
            { FieldPath = nameof(CornerDependencies), Budget = nameof(CornerDependencies), ActualCount = CornerDependencies.Count, Limit = (Width + 1) * (Depth + 1) });
        if (EdgeSubdivisions.Count > count * 4)
            Add(new(MapDocumentRefusal.WorkCapacityExceeded, "too many edge subdivisions")
            { FieldPath = nameof(EdgeSubdivisions), Budget = nameof(EdgeSubdivisions), ActualCount = EdgeSubdivisions.Count, Limit = count * 4 });
        if (errors.Count != 0) return errors;

        var corners = new HashSet<(int, int)>();
        for (int i = 0; i < CornerDependencies.Count; i++)
        {
            MapCornerDependency dependency = CornerDependencies[i];
            if (dependency is null || dependency.CornerX < 0 || dependency.CornerX > Width ||
                dependency.CornerZ < 0 || dependency.CornerZ > Depth || dependency.Owner is null ||
                string.IsNullOrWhiteSpace(dependency.Owner.Patch.SurfaceId))
            {
                bool badX = dependency is not null && (dependency.CornerX < 0 || dependency.CornerX > Width);
                bool badZ = dependency is not null && !badX && (dependency.CornerZ < 0 || dependency.CornerZ > Depth);
                string field = dependency is null ? "" : badX ? ".CornerX" : badZ ? ".CornerZ"
                    : dependency.Owner is null ? ".Owner" : ".Owner.Patch.SurfaceId";
                Add(new(badX || badZ ? MapDocumentRefusal.SurfaceAddressOutOfPatch : MapDocumentRefusal.SchemaValueRequired,
                    "invalid corner dependency")
                {
                    FieldPath = $"CornerDependencies[{i}]{field}", Index = i,
                    CellX = dependency?.CornerX, CellZ = dependency?.CornerZ,
                    ActualInteger = badX ? dependency!.CornerX : badZ ? dependency!.CornerZ : null,
                    MinimumInteger = badX || badZ ? 0 : null, MaximumInteger = badX ? Width : badZ ? Depth : null,
                    Width = Width, Depth = Depth,
                });
                continue;
            }
            if (!corners.Add((dependency.CornerX, dependency.CornerZ)))
                Add(new(MapDocumentRefusal.IdentityDuplicate, "duplicate corner dependency")
                { FieldPath = $"CornerDependencies[{i}]", Index = i, CellX = dependency.CornerX, CellZ = dependency.CornerZ });
            try { dependency.Owner.Address.RequireValid(); }
            catch (MapDocumentException error)
            {
                Add(MapDocumentFinding.FromException(error)! with
                {
                    Message = "invalid dependency address denominator", FieldPath = $"CornerDependencies[{i}].Owner.Address.Denominator",
                    Index = i, CellX = dependency.CornerX, CellZ = dependency.CornerZ,
                });
            }
        }
        var edges = new HashSet<(int, int, MapCellEdge)>();
        for (int i = 0; i < EdgeSubdivisions.Count; i++)
        {
            MapEdgeSubdivision edge = EdgeSubdivisions[i];
            if (edge is null || edge.CellX < 0 || edge.CellX >= Width || edge.CellZ < 0 || edge.CellZ >= Depth ||
                edge.Edge > MapCellEdge.West)
            {
                bool badX = edge is not null && (edge.CellX < 0 || edge.CellX >= Width);
                bool badZ = edge is not null && !badX && (edge.CellZ < 0 || edge.CellZ >= Depth);
                string field = edge is null ? "" : badX ? ".CellX" : badZ ? ".CellZ" : ".Edge";
                Add(new(edge is null ? MapDocumentRefusal.SchemaValueRequired : badX || badZ
                    ? MapDocumentRefusal.SurfaceAddressOutOfPatch : MapDocumentRefusal.SchemaEnumUnsupported, "invalid subdivision edge")
                {
                    FieldPath = $"EdgeSubdivisions[{i}]{field}", Index = i, CellX = edge?.CellX, CellZ = edge?.CellZ, Edge = edge?.Edge,
                    ActualInteger = edge is null ? null : badX ? edge.CellX : badZ ? edge.CellZ : (byte)edge.Edge,
                    MinimumInteger = badX || badZ ? 0 : null, MaximumInteger = badX ? Width - 1 : badZ ? Depth - 1 : null,
                    EnumType = edge is not null && !badX && !badZ ? typeof(MapCellEdge) : null, Width = Width, Depth = Depth,
                });
                continue;
            }
            if (edge.Segments is < 2 or > 64)
                Add(new(MapDocumentRefusal.ValueOutsideRange, "subdivision segments must be 2 to 64")
                {
                    FieldPath = $"EdgeSubdivisions[{i}].Segments", Index = i, CellX = edge.CellX, CellZ = edge.CellZ, Edge = edge.Edge,
                    ActualInteger = edge.Segments, MinimumInteger = 2, MaximumInteger = 64,
                });
            if (!edges.Add((edge.CellX, edge.CellZ, edge.Edge)))
                Add(new(MapDocumentRefusal.IdentityDuplicate, "duplicate edge subdivision")
                { FieldPath = $"EdgeSubdivisions[{i}]", Index = i, CellX = edge.CellX, CellZ = edge.CellZ, Edge = edge.Edge });
            if (!IsPresent(edge.CellX, edge.CellZ))
                Add(new(MapDocumentRefusal.SurfaceSubdivisionAbsentCell, "subdivision on absent cell")
                { FieldPath = $"EdgeSubdivisions[{i}]", Index = i, CellX = edge.CellX, CellZ = edge.CellZ, Edge = edge.Edge });
            (int x, int z) = edge.Edge switch
            {
                MapCellEdge.South => (edge.CellX, edge.CellZ - 1),
                MapCellEdge.East => (edge.CellX + 1, edge.CellZ),
                MapCellEdge.North => (edge.CellX, edge.CellZ + 1),
                _ => (edge.CellX - 1, edge.CellZ),
            };
            if (x >= 0 && x < Width && z >= 0 && z < Depth && IsPresent(x, z))
                Add(new(MapDocumentRefusal.SurfaceSubdivisionInteriorEdge, "subdivision on interior present-cell edge")
                { FieldPath = $"EdgeSubdivisions[{i}]", Index = i, CellX = edge.CellX, CellZ = edge.CellZ, Edge = edge.Edge });
        }
        return errors;

        void Add(MapDocumentFinding finding)
        {
            errors.Add(finding.Message);
            findings?.Add(finding with { Patch = Key });
        }
    }
}
