using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Spaces;

namespace KhaozEngine.MapDoc.Surfaces;

public readonly record struct MapPortalPlaneTriangle(MapVertexId A, MapVertexId B, MapVertexId C, Vector3 Normal);

/// <summary>A membership plane only. It supplies no support, drawing, capture or collision faces.</summary>
public sealed record MapOpeningPlane(string OpeningId, MapSubmissionAnchor Anchor, IReadOnlyList<Vector3> Offsets,
    IReadOnlyList<MapPortalPlaneTriangle> Triangles, IReadOnlyList<MapExactTriangle> ExactTriangles,
    IReadOnlyList<MapFaceKey> Keys);

/// <summary>Describes the canonical upward plane of absent cells without making them physical.</summary>
public static class MapOpeningBoundary
{
    public static MapOpeningPlane Compile(MapSurfaceRef surface, MapSurfacePatch patch, MapHorizontalOpening opening)
    {
        ArgumentNullException.ThrowIfNull(opening);
        return Compile(new MapValidatedSurfacePatch(surface, patch), opening);
    }

    internal static MapOpeningPlane Compile(MapValidatedSurfacePatch validated, MapHorizontalOpening opening)
    {
        ArgumentNullException.ThrowIfNull(opening);
        MapSurfaceRef surface = validated.Surface;
        MapSurfacePatch patch = validated.Patch;
        if (opening.Patch != patch.Key || opening.SlotCells.Count == 0)
            throw new MapDocumentException("opening must name absent cells in its patch");
        try
        {
            MapSubmissionAnchor anchor = MapSurfaceCompiler.Anchor(surface, patch);
            var builder = new MapSurfaceVertexBuilder(surface, patch, anchor);
            var offsets = new List<Vector3>();
            var vertices = new Dictionary<MapVertexId, MapSurfaceVertex>();
            var triangles = new List<MapPortalPlaneTriangle>();
            var exact = new List<MapExactTriangle>();
            var keys = new List<MapFaceKey>();
            var cells = new HashSet<int>();
            Span<MapLatticeTriangle> parents = stackalloc MapLatticeTriangle[4];
            foreach (int slot in opening.SlotCells.OrderBy(cell => cell))
            {
                int x = slot % MapPatchKey.SlotCells - patch.CellMinX;
                int z = slot / MapPatchKey.SlotCells - patch.CellMinZ;
                if (slot < 0 || slot >= MapPatchKey.SlotCells * MapPatchKey.SlotCells ||
                    x < 0 || x >= patch.Width || z < 0 || z >= patch.Depth ||
                    !cells.Add(slot) || patch.IsPresent(x, z))
                    throw new MapDocumentException("opening must name distinct absent cells in its patch");
                int count = MapSurfaceCompiler.Describe(patch, x, z, parents);
                for (byte parent = 0; parent < count; parent++)
                {
                    MapLatticeTriangle triangle = parents[parent];
                    if (surface.Frame.RowDirection == MapRowDirection.PositiveZ)
                        triangle = new(triangle.A, triangle.C, triangle.B, triangle.Overlay);
                    MapSurfaceVertex a = builder.Point(x, z, triangle.A);
                    MapSurfaceVertex b = builder.Point(x, z, triangle.B);
                    MapSurfaceVertex c = builder.Point(x, z, triangle.C);
                    Vector3 normal = Vector3.Normalize(Vector3.Cross(b.Offset - a.Offset, c.Offset - a.Offset));
                    if (!float.IsFinite(normal.X) || !float.IsFinite(normal.Y) || !float.IsFinite(normal.Z) || normal.Y <= 0)
                        throw new MapDocumentException("opening plane normal is not representable");
                    triangles.Add(new(Vertex(a), Vertex(b), Vertex(c), normal));
                    exact.Add(new(a.Exact, b.Exact, c.Exact));
                    keys.Add(new(opening.Id, patch.Key, slot, parent, 0, MapSide.Front));
                }
            }
            return new(opening.Id, anchor, offsets.AsReadOnly(), triangles.AsReadOnly(), exact.AsReadOnly(), keys.AsReadOnly());

            MapVertexId Vertex(MapSurfaceVertex vertex)
            {
                var id = new MapVertexId(surface.Id, vertex.Address);
                if (vertices.TryGetValue(id, out MapSurfaceVertex existing))
                {
                    if (existing.Exact != vertex.Exact)
                        throw new MapDocumentException("inconsistent exact height for shared opening vertex");
                }
                else
                {
                    vertices.Add(id, vertex);
                    offsets.Add(vertex.Offset);
                }
                return id;
            }
        }
        catch (Exception error) when (error is MapExactOverflowException or OverflowException)
        {
            throw new MapDocumentException("opening plane geometry is not representable", error);
        }
    }
}
