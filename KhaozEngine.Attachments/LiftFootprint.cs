using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace KhaozEngine.Attachments;

/// <summary>A worn set's measured cells and heights above one body at rest.</summary>
public sealed class LiftFootprint
{
    internal const int MaxBodyTriangles = int.MaxValue / BodyCell.CellsPerTriangle;

    /// <summary>Admit a footprint measured on the named body, with parts supplied in strictly ascending ID order.</summary>
    /// <exception cref="LiftFootprintDataException">The supplied data breaks a footprint rule.</exception>
    public LiftFootprint(string bodyRegionMapSha256, int bodyTriangleCount, IEnumerable<LiftFootprintPart> parts)
    {
        ArgumentNullException.ThrowIfNull(bodyRegionMapSha256);
        ArgumentNullException.ThrowIfNull(parts);
        if (!AttachmentSetDefinition.ValidHash(bodyRegionMapSha256))
            throw new LiftFootprintDataException(LiftFootprintRefusal.BodyRegionMapSha256Invalid, "The body region map requires a lowercase SHA-256.")
            { BodyRegionMapSha256 = bodyRegionMapSha256 };

        CheckBodyTriangleCount(bodyTriangleCount);
        int cellLimit = bodyTriangleCount * BodyCell.CellsPerTriangle;
        var admitted = ImmutableArray.CreateBuilder<LiftFootprintPart>();
        uint previousId = 0;
        foreach (LiftFootprintPart part in parts)
        {
            int partIndex = admitted.Count;
            if (part is null)
                throw new LiftFootprintDataException(LiftFootprintRefusal.PartMissing, "A footprint part is required.")
                { PartIndex = partIndex };

            CheckPartId(part.PartId, previousId, partIndex);
            for (int triangle = 0; triangle < part.TriangleCount; triangle++)
            {
                ReadOnlySpan<int> keys = part.Triangles[triangle].CellKeys;
                for (int entry = 0; entry < keys.Length; entry++)
                {
                    int key = keys[entry];
                    if (key >= cellLimit)
                        throw new LiftFootprintDataException(LiftFootprintRefusal.CellKeyOutsideBody, "The cell key lies outside the declared body triangles.")
                        {
                            PartId = part.PartId, PartIndex = partIndex, TriangleIndex = triangle,
                            EntryIndex = entry, CellKey = key, BodyTriangleCount = bodyTriangleCount,
                        };
                }
            }

            admitted.Add(part);
            previousId = part.PartId;
        }

        BodyRegionMapSha256 = bodyRegionMapSha256;
        BodyTriangleCount = bodyTriangleCount;
        Parts = admitted.ToImmutable();
    }

    /// <summary>The lowercase SHA-256 of the canonical body region map.</summary>
    public string BodyRegionMapSha256 { get; }

    /// <summary>The number of body triangles across all map parts.</summary>
    public int BodyTriangleCount { get; }

    /// <summary>The barycentric grid level used for all body triangles.</summary>
    public int Level => BodyCell.Level;

    /// <summary>The measured parts in strictly ascending ID order.</summary>
    public IReadOnlyList<LiftFootprintPart> Parts { get; }

    /// <summary>Find the measured part with the given ID, or return null if it is absent.</summary>
    public LiftFootprintPart? PartById(uint id)
    {
        int lower = 0;
        int upper = Parts.Count - 1;
        while (lower <= upper)
        {
            int middle = lower + (upper - lower) / 2;
            LiftFootprintPart part = Parts[middle];
            if (part.PartId == id) return part;
            if (part.PartId < id) lower = middle + 1;
            else upper = middle - 1;
        }
        return null;
    }

    /// <summary>
    /// Return the maximum fixed height in metres per region, in body map region order.
    /// A region with no fixed entries has a null height.
    /// </summary>
    /// <exception cref="ArgumentException">The body's digest or total triangle count differs from the footprint's.</exception>
    public IReadOnlyList<double?> RegionHeights(BodyRegionMap body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (body.Sha256 != BodyRegionMapSha256)
            throw new ArgumentException("The footprint was measured on another body region map.", nameof(body));

        long triangleCount = 0;
        for (int part = 0; part < body.Parts.Count; part++)
            triangleCount += body.TriangleCount(part);
        if (triangleCount != BodyTriangleCount)
            throw new ArgumentException("The footprint's triangle count differs from the body region map's.", nameof(body));

        var regions = new byte[BodyTriangleCount];
        int offset = 0;
        for (int part = 0; part < body.Parts.Count; part++)
        {
            int count = body.TriangleCount(part);
            body.WriteTriangleRegions(part, regions.AsSpan(offset, count));
            offset += count;
        }

        var highest = new double?[body.Regions.Count];
        foreach (LiftFootprintPart part in Parts)
        {
            foreach (LiftFootprintTriangle triangle in part.Triangles)
            {
                ReadOnlySpan<int> keys = triangle.CellKeys;
                ReadOnlySpan<ushort> units = triangle.HeightUnits;
                for (int entry = 0; entry < units.Length; entry++)
                {
                    int region = regions[keys[entry] / BodyCell.CellsPerTriangle];
                    double height = LiftFootprintTriangle.ToMetres(units[entry]);
                    if (highest[region] is not double prior || height > prior)
                        highest[region] = height;
                }
            }
        }

        return ImmutableArray.Create(highest);
    }

    internal static void CheckBodyTriangleCount(long count, int? byteOffset = null)
    {
        if (count is < 1 or > MaxBodyTriangles)
            throw new LiftFootprintDataException(LiftFootprintRefusal.BodyTriangleCountOutsideRange, $"The body triangle count must be in [1, {MaxBodyTriangles}].")
            { BodyTriangleCount = count, ByteOffset = byteOffset };
    }

    internal static void CheckPartId(uint id, uint previousId, int partIndex, int? byteOffset = null)
    {
        if (id == 0)
            throw new LiftFootprintDataException(LiftFootprintRefusal.PartIdZero, "Part IDs must be positive.")
            { PartId = id, PartIndex = partIndex, ByteOffset = byteOffset };
        if (id <= previousId)
            throw new LiftFootprintDataException(LiftFootprintRefusal.PartIdsNotAscending, "Part IDs must increase strictly.")
            { PartId = id, PartIndex = partIndex, ByteOffset = byteOffset };
    }
}
