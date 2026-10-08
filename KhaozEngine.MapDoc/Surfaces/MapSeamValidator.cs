using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Storage;

namespace KhaozEngine.MapDoc.Surfaces;

/// <summary>Checks complete authored edge sequences and owner dependencies with exact comparisons.</summary>
public static class MapSeamValidator
{
    public static IReadOnlyList<string> Validate(MapSurfaceSeam seam, MapScopedSurfaces view)
    {
        ArgumentNullException.ThrowIfNull(seam);
        ArgumentNullException.ThrowIfNull(view);
        var findings = new List<string>();
        try
        {
            MapBoundarySurface? first = MapBoundaryGeometry.Read(seam.First.Patch, view, out string? firstDetail);
            MapBoundarySurface? second = seam.First.Patch == seam.Second.Patch ? first
                : MapBoundaryGeometry.Read(seam.Second.Patch, view, out _);
            if (first is null) findings.Add(firstDetail!);
            if (second is null) findings.Add($"unresolved seam second patch '{seam.Second.Patch}'");
            if (first is null || second is null) return findings.AsReadOnly();
            Dictionary<MapLatticeAddress, MapExactPoint> firstEdge = Edge(seam.First, first);
            Dictionary<MapLatticeAddress, MapExactPoint> secondEdge = Edge(seam.Second, second);
            var firstPairs = new HashSet<MapLatticeAddress>();
            var secondPairs = new HashSet<MapLatticeAddress>();
            foreach (var pair in seam.Pairs)
            {
                if (pair.First.SurfaceId != first.Surface.Id || pair.Second.SurfaceId != second.Surface.Id)
                {
                    findings.Add("seam position: pair surface does not match its edge");
                    continue;
                }
                MapExactXz a = first.Surface.Frame.WorldXz(pair.First.Address);
                MapExactXz b = second.Surface.Frame.WorldXz(pair.Second.Address);
                if (a != b) findings.Add("seam position: paired vertices disagree in world XZ");
                if (!firstPairs.Add(pair.First.Address) || !secondPairs.Add(pair.Second.Address))
                    findings.Add("seam subdivide: duplicate paired vertex");
                bool hasFirst = firstEdge.TryGetValue(pair.First.Address, out MapExactPoint firstPoint);
                bool hasSecond = secondEdge.TryGetValue(pair.Second.Address, out MapExactPoint secondPoint);
                if (!hasFirst || !hasSecond) findings.Add("seam subdivide: pair is not a declared edge vertex");
                else if (firstPoint.Y != secondPoint.Y) findings.Add("seam height: paired vertices disagree");
            }
            if (!firstPairs.SetEquals(firstEdge.Keys) || !secondPairs.SetEquals(secondEdge.Keys))
                findings.Add("seam subdivide: pairs must cover every vertex of both edges");
        }
        catch (Exception error) when (error is MapExactOverflowException or OverflowException)
        {
            findings.Add("seam geometry is not representable");
        }
        catch (MapDocumentException error)
        {
            findings.Add(error.Message);
        }
        return findings.AsReadOnly();
    }

    public static IReadOnlyList<string> ValidateCornerDependencies(MapSurfacePatch patch, MapScopedSurfaces view)
    {
        ArgumentNullException.ThrowIfNull(patch);
        ArgumentNullException.ThrowIfNull(view);
        var findings = new List<string>();
        try
        {
            MapSurfaceRef? surface = MapBoundaryGeometry.Surface(view, patch.Key.SurfaceId);
            if (surface is null) return Array.AsReadOnly(new[] { "unresolved dependent surface" });
            MapSurfaceCompiler.Validate(surface, patch);
            var builder = new MapSurfaceVertexBuilder(surface, patch, MapSurfaceCompiler.Anchor(surface, patch));
            var owners = new Dictionary<MapPatchKey, MapBoundarySurface?>();
            foreach (MapCornerDependency dependency in patch.CornerDependencies)
            {
                if (!owners.TryGetValue(dependency.Owner.Patch, out MapBoundarySurface? owner))
                {
                    owner = MapBoundaryGeometry.Read(dependency.Owner.Patch, view, out _);
                    owners.Add(dependency.Owner.Patch, owner);
                }
                if (owner is null)
                {
                    findings.Add($"unresolved corner owner patch '{dependency.Owner.Patch}'");
                    continue;
                }
                MapExactPoint dependent = builder.Point(dependency.CornerX, dependency.CornerZ, MapLatticePoint.Sw).Exact;
                MapExactXz ownerPosition = owner.Surface.Frame.WorldXz(dependency.Owner.Address);
                if (dependent.X != ownerPosition.X || dependent.Z != ownerPosition.Z)
                    findings.Add("corner dependency position: dependent and owner disagree in world XZ");
                if (!owner.Vertices.TryGetValue(dependency.Owner.Address, out MapExactPoint ownerPoint))
                    findings.Add("corner dependency owner: address is not a declared surface vertex");
                else if (dependent.Y != ownerPoint.Y)
                    findings.Add("corner dependency owner: exact height disagrees");
            }
        }
        catch (Exception error) when (error is MapExactOverflowException or OverflowException)
        {
            findings.Add("corner dependency geometry is not representable");
        }
        catch (MapDocumentException error)
        {
            findings.Add(error.Message);
        }
        return findings.AsReadOnly();
    }

    static Dictionary<MapLatticeAddress, MapExactPoint> Edge(MapSurfaceEdgeRef edge, MapBoundarySurface source)
    {
        if (edge.From.SurfaceId != source.Surface.Id || edge.To.SurfaceId != source.Surface.Id)
            throw new MapDocumentException("seam position: edge surface mismatch");
        MapExactXz from = source.Surface.Frame.WorldXz(edge.From.Address);
        MapExactXz to = source.Surface.Frame.WorldXz(edge.To.Address);
        if (from == to || (from.X != to.X && from.Z != to.Z))
            throw new MapDocumentException("seam position: edge must be a nonzero lattice edge");
        var vertices = source.Vertices.Where(vertex => OnEdge(vertex.Value, from, to))
            .ToDictionary(vertex => vertex.Key, vertex => vertex.Value);
        if (!vertices.ContainsKey(edge.From.Address) || !vertices.ContainsKey(edge.To.Address))
            throw new MapDocumentException("seam subdivide: edge endpoints must be declared surface vertices");
        return vertices;
    }

    static bool OnEdge(MapExactPoint point, MapExactXz from, MapExactXz to) =>
        from.X == to.X ? point.X == from.X && Between(point.Z, from.Z, to.Z)
            : point.Z == from.Z && Between(point.X, from.X, to.X);

    static bool Between(MapExactValue value, MapExactValue first, MapExactValue last) =>
        first.CompareTo(last) <= 0 ? value.CompareTo(first) >= 0 && value.CompareTo(last) <= 0
            : value.CompareTo(last) >= 0 && value.CompareTo(first) <= 0;
}
