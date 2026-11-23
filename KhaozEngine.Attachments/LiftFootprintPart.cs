using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace KhaozEngine.Attachments;

/// <summary>One measured part's triangles in upload order, with shared packed storage for their entries.</summary>
public sealed class LiftFootprintPart
{
    /// <summary>Snapshot the triangles of a part in material part order, then in triangle order.</summary>
    /// <remarks>The containing <see cref="LiftFootprint"/> validates the part ID and its body cell range.</remarks>
    public LiftFootprintPart(uint partId, IEnumerable<LiftFootprintTriangle> triangles)
    {
        ArgumentNullException.ThrowIfNull(triangles);
        var builder = new Builder();
        foreach (LiftFootprintTriangle triangle in triangles)
        {
            foreach (int key in triangle.CellKeys) builder.AddCell(key);
            foreach (ushort height in triangle.HeightUnits) builder.AddHeight(height);
            builder.EndTriangle(triangle.Swinging);
        }

        PartId = partId;
        Triangles = builder.Pack();
    }

    LiftFootprintPart(uint partId, ImmutableArray<LiftFootprintTriangle> triangles)
    {
        PartId = partId;
        Triangles = triangles;
    }

    /// <summary>The attachment definition's part ID.</summary>
    public uint PartId { get; }

    /// <summary>The number of triangles in the part's upload.</summary>
    public int TriangleCount => Triangles.Count;

    /// <summary>The part's triangles in material part order, then triangle order.</summary>
    public IReadOnlyList<LiftFootprintTriangle> Triangles { get; }

    // The model constructor and the file reader both pack their admitted entries into two arrays per part.
    internal sealed class Builder
    {
        readonly List<int> cells = [];
        readonly List<ushort> heights = [];
        readonly List<TriangleRange> triangles = [];
        int cellStart;
        int heightStart;

        internal void AddCell(int key) => cells.Add(key);
        internal void AddHeight(ushort height) => heights.Add(height);
        internal int CellKey(int index) => cells[cellStart + index];

        internal void EndTriangle(bool swinging)
        {
            triangles.Add(new TriangleRange(cellStart, swinging ? -1 : heightStart, cells.Count - cellStart));
            cellStart = cells.Count;
            heightStart = heights.Count;
        }

        internal LiftFootprintPart Build(uint partId) => new(partId, Pack());

        internal ImmutableArray<LiftFootprintTriangle> Pack()
        {
            var entries = new LiftFootprintTriangle.Entries(cells.ToArray(), heights.ToArray());
            var packed = ImmutableArray.CreateBuilder<LiftFootprintTriangle>(triangles.Count);
            foreach (TriangleRange range in triangles)
                packed.Add(new LiftFootprintTriangle(entries, range.CellStart, range.HeightStart, range.Count));
            return packed.MoveToImmutable();
        }

        readonly record struct TriangleRange(int CellStart, int HeightStart, int Count);
    }
}
