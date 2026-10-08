using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Storage;

namespace KhaozEngine.MapDoc.Surfaces;

public enum MapResolveStatus { Resolved, MissingGeometry, Invalid }
public sealed record MapChainResolution(MapResolveStatus Status, IReadOnlyList<MapExactPoint> Points, string? Detail);

/// <summary>Resolves authored and source-edge chains without substituting missing geometry.</summary>
public static class MapBoundaryGeometry
{
    public static MapChainResolution ResolveChain(MapBoundaryChain chain, MapScopedSurfaces view)
    {
        ArgumentNullException.ThrowIfNull(chain);
        ArgumentNullException.ThrowIfNull(view);
        try
        {
            if (!Enum.IsDefined(chain.Kind) || chain.Vertices.Count is < 2 or > 4097 ||
                (chain.Kind == MapChainKind.SurfaceEdge) != chain.SourcePatch.HasValue)
                return Failure(MapResolveStatus.Invalid, "invalid chain kind, source patch or vertex sequence");
            MapBoundarySurface? source = null;
            if (chain.SourcePatch is { } key)
            {
                source = Read(key, view, out string? detail);
                if (source is null) return Failure(MapResolveStatus.MissingGeometry, detail!);
            }
            var points = new List<MapExactPoint>(chain.Vertices.Count);
            foreach (MapChainVertex vertex in chain.Vertices)
            {
                if ((chain.Kind == MapChainKind.Authored) != vertex.HeightUnits.HasValue)
                    return Failure(MapResolveStatus.Invalid, "chain height kind mismatch");
                if (source is not null)
                {
                    if (vertex.Vertex.SurfaceId != source.Surface.Id ||
                        !source.Vertices.TryGetValue(vertex.Vertex.Address, out MapExactPoint point))
                        return Failure(MapResolveStatus.Invalid, "chain source vertex is not a surface edge vertex");
                    points.Add(point);
                }
                else
                {
                    MapSurfaceRef? surface = Surface(view, vertex.Vertex.SurfaceId);
                    if (surface is null) return Failure(MapResolveStatus.MissingGeometry, "unresolved chain surface");
                    MapExactXz xz = surface.Frame.WorldXz(vertex.Vertex.Address);
                    points.Add(new(xz.X, surface.Frame.Metres(new(vertex.HeightUnits!.Value, 1)), xz.Z));
                }
            }
            return new(MapResolveStatus.Resolved, points.AsReadOnly(), null);
        }
        catch (Exception error) when (error is MapExactOverflowException or OverflowException)
        {
            return Failure(MapResolveStatus.Invalid, "chain geometry is not representable");
        }
        catch (MapDocumentException error)
        {
            return Failure(MapResolveStatus.Invalid, error.Message);
        }
    }

    static MapChainResolution Failure(MapResolveStatus status, string detail) =>
        new(status, Array.Empty<MapExactPoint>(), detail);

    internal static MapSurfaceRef? Surface(MapScopedSurfaces view, string id) =>
        view.Surfaces.FirstOrDefault(surface => surface.Id == id);

    internal static MapBoundarySurface? Read(MapPatchKey key, MapScopedSurfaces view, out string? detail)
    {
        MapPatchRead read = view.Patch(key);
        MapSurfaceRef? surface = Surface(view, key.SurfaceId);
        if (read.Status != MapPatchStatus.Present || read.Patch is null || surface is null)
        {
            detail = $"unresolved surface patch '{key}' ({read.Status})";
            return null;
        }
        detail = null;
        return new(surface, read.Patch);
    }
}

/// <summary>Source vertices use the compiler's corner, legacy midpoint and subdivision operations.</summary>
internal sealed class MapBoundarySurface
{
    internal MapSurfaceRef Surface { get; }
    internal Dictionary<MapLatticeAddress, MapExactPoint> Vertices { get; } = new();

    internal MapBoundarySurface(MapSurfaceRef surface, MapSurfacePatch patch)
    {
        MapSurfaceCompiler.Validate(surface, patch);
        Surface = surface;
        var builder = new MapSurfaceVertexBuilder(surface, patch, MapSurfaceCompiler.Anchor(surface, patch));
        var subdivisions = new int[patch.Cells.Length * 4];
        foreach (MapEdgeSubdivision edge in patch.EdgeSubdivisions)
            subdivisions[4 * (edge.CellZ * patch.Width + edge.CellX) + (int)edge.Edge] = edge.Segments;
        Span<MapLatticeTriangle> parents = stackalloc MapLatticeTriangle[4];
        Span<MapSurfaceVertex> boundary = stackalloc MapSurfaceVertex[MapSurfaceCompiler.MaxChildrenPerParent];
        for (int z = 0; z < patch.Depth; z++)
            for (int x = 0; x < patch.Width; x++)
            {
                // Stored corners remain addressable even beside an authored hole.
                Add(builder.Point(x, z, MapLatticePoint.Sw));
                Add(builder.Point(x, z, MapLatticePoint.Se));
                Add(builder.Point(x, z, MapLatticePoint.Nw));
                Add(builder.Point(x, z, MapLatticePoint.Ne));
                if (!patch.IsPresent(x, z)) continue;
                int count = MapSurfaceCompiler.Describe(patch, x, z, parents);
                ReadOnlySpan<int> segments = subdivisions.AsSpan(4 * (z * patch.Width + x), 4);
                for (int p = 0; p < count; p++)
                {
                    int vertices = MapSubdividedTriangle.Boundary(builder, x, z, parents[p], segments, boundary);
                    for (int i = 0; i < vertices; i++) Add(boundary[i]);
                }
            }
    }

    void Add(MapSurfaceVertex vertex)
    {
        if (Vertices.TryGetValue(vertex.Address, out MapExactPoint point) && point != vertex.Exact)
            throw new MapDocumentException("inconsistent exact height for shared surface vertex");
        Vertices[vertex.Address] = vertex.Exact;
    }
}
