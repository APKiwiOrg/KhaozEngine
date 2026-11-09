using System;
using KhaozEngine.MapDoc.Water;

namespace KhaozEngine.MapDoc.Spaces;

/// <summary>Persistent domain outputs and piece-local arithmetic have separate simultaneous capacities.</summary>
internal static class MapPrismPreparationCapacity
{
    internal static long DomainBytes(MapPrismDomain prism) => checked(4096L + 256L * MapPrismGeometry.Cost(prism));
    internal static long LargestPiece(MapPrismDomain prism)
    {
        long vertices = prism.Footprint.Rects.Count == 0 ? 0 : 4;
        foreach (var ring in prism.Footprint.Rings) vertices = Math.Max(vertices, ring.Vertices.Count);
        return vertices;
    }
    internal static long ConvexPartsBytes(MapWaterDomain domain)
    {
        long bytes = 128;
        for (int i = 0; i < domain.Pieces.Count; i++)
        {
            bool convex = domain.PieceIsConvex(i);
            long parts = convex ? 1 : domain.Pieces[i].Ring.Count - 2;
            long copiedVertices = convex ? 0 : checked(3 * parts);
            // Each descriptor holds polygon references and exact bounds, including collection growth.
            // Convex pieces borrow the domain's ring. Triangulated pieces own their copied vertices.
            bytes = checked(bytes + 512 * parts + 256 * copiedVertices);
        }
        return bytes;
    }
}
