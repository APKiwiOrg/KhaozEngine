using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Storage;

namespace KhaozEngine.MapDoc.Surfaces;

/// <summary>Checks complete authored edge sequences and owner dependencies with exact comparisons.</summary>
public static class MapSeamValidator
{
    public static IReadOnlyList<string> Validate(MapSurfaceSeam seam, MapScopedSurfaces view) => Validate(seam, view, null);

    // Typed findings remain beside prose until paint-priority admission completes the lower producers.
    internal static IReadOnlyList<string> Validate(MapSurfaceSeam seam, MapScopedSurfaces view, List<MapDocumentFinding>? typedFindings)
    {
        ArgumentNullException.ThrowIfNull(seam);
        ArgumentNullException.ThrowIfNull(view);
        var findings = new List<string>();
        try
        {
            MapBoundarySurface? first = MapBoundaryGeometry.Read(seam.First.Patch, view, out string? firstDetail);
            MapBoundarySurface? second = seam.First.Patch == seam.Second.Patch ? first
                : MapBoundaryGeometry.Read(seam.Second.Patch, view, out _);
            if (first is null) Add(new(MapDocumentRefusal.PatchUnavailable, firstDetail!)
            { Patch = seam.First.Patch, FieldPath = "First.Patch" });
            if (second is null) Add(new(MapDocumentRefusal.PatchUnavailable, $"unresolved seam second patch '{seam.Second.Patch}'")
            { Patch = seam.Second.Patch, FieldPath = "Second.Patch" });
            if (first is null || second is null) return findings.AsReadOnly();
            Dictionary<MapLatticeAddress, MapExactPoint> firstEdge = Edge(seam.First, first, seam.Id, "First");
            Dictionary<MapLatticeAddress, MapExactPoint> secondEdge = Edge(seam.Second, second, seam.Id, "Second");
            var firstPairs = new HashSet<MapLatticeAddress>();
            var secondPairs = new HashSet<MapLatticeAddress>();
            int index = -1;
            foreach (var pair in seam.Pairs)
            {
                index++;
                if (pair.First.SurfaceId != first.Surface.Id || pair.Second.SurfaceId != second.Surface.Id)
                {
                    bool firstMismatch = pair.First.SurfaceId != first.Surface.Id;
                    Add(new(MapDocumentRefusal.AuthoredValueMismatch, "seam position: pair surface does not match its edge")
                    {
                        SurfaceId = firstMismatch ? pair.First.SurfaceId : pair.Second.SurfaceId,
                        Patch = firstMismatch ? seam.First.Patch : seam.Second.Patch, Index = index,
                        FieldPath = $"Pairs[{index}].{(firstMismatch ? "First" : "Second")}.SurfaceId",
                    });
                    continue;
                }
                MapExactXz a = first.Surface.Frame.WorldXz(pair.First.Address);
                MapExactXz b = second.Surface.Frame.WorldXz(pair.Second.Address);
                if (a != b) Add(new(MapDocumentRefusal.SurfaceSeamPositionMismatch, "seam position: paired vertices disagree in world XZ")
                { Index = index, Address = pair.First.Address, SurfaceId = first.Surface.Id, FieldPath = $"Pairs[{index}]" });
                bool firstRepeated;
                if ((firstRepeated = !firstPairs.Add(pair.First.Address)) || !secondPairs.Add(pair.Second.Address))
                    Add(new(MapDocumentRefusal.IdentityDuplicate, "seam subdivide: duplicate paired vertex")
                    {
                        Index = index, Address = firstRepeated ? pair.First.Address : pair.Second.Address,
                        SurfaceId = firstRepeated ? first.Surface.Id : second.Surface.Id, FieldPath = $"Pairs[{index}]",
                    });
                bool hasFirst = firstEdge.TryGetValue(pair.First.Address, out MapExactPoint firstPoint);
                bool hasSecond = secondEdge.TryGetValue(pair.Second.Address, out MapExactPoint secondPoint);
                if (!hasFirst || !hasSecond) Add(new(MapDocumentRefusal.SurfaceSeamVertexUndeclared, "seam subdivide: pair is not a declared edge vertex")
                {
                    Index = index, Address = !hasFirst ? pair.First.Address : pair.Second.Address,
                    SurfaceId = !hasFirst ? first.Surface.Id : second.Surface.Id, FieldPath = $"Pairs[{index}]",
                });
                else if (firstPoint.Y != secondPoint.Y) Add(new(MapDocumentRefusal.GeometryVertexHeightMismatch, "seam height: paired vertices disagree")
                { Index = index, Address = pair.First.Address, SurfaceId = first.Surface.Id, FieldPath = $"Pairs[{index}]" });
            }
            bool firstIncomplete;
            if ((firstIncomplete = !firstPairs.SetEquals(firstEdge.Keys)) || !secondPairs.SetEquals(secondEdge.Keys))
                Add(new(MapDocumentRefusal.GeometryVertexSequenceInvalid, "seam subdivide: pairs must cover every vertex of both edges")
                {
                    SurfaceId = firstIncomplete ? first.Surface.Id : second.Surface.Id,
                    ActualCount = firstIncomplete ? firstPairs.Count : secondPairs.Count,
                    ExpectedCount = firstIncomplete ? firstEdge.Count : secondEdge.Count, FieldPath = "Pairs",
                });
        }
        catch (Exception error) when (error is MapExactOverflowException or OverflowException)
        {
            Add(new(MapDocumentRefusal.GeometryNotRepresentable, "seam geometry is not representable")
            { Operation = nameof(Validate) });
        }
        catch (MapDocumentException error)
        {
            if (MapDocumentFinding.FromException(error) is { } finding) Add(finding);
            else findings.Add(error.Message); // Paint-priority admission is converted with its producer.
        }
        return findings.AsReadOnly();

        void Add(MapDocumentFinding finding)
        {
            findings.Add(finding.Message);
            typedFindings?.Add(finding with { RecordId = finding.RecordId ?? seam.Id });
        }
    }

    public static IReadOnlyList<string> ValidateCornerDependencies(MapSurfacePatch patch, MapScopedSurfaces view) =>
        ValidateCornerDependencies(patch, view, null);

    internal static IReadOnlyList<string> ValidateCornerDependencies(MapSurfacePatch patch, MapScopedSurfaces view,
        List<MapDocumentFinding>? typedFindings)
    {
        ArgumentNullException.ThrowIfNull(patch);
        ArgumentNullException.ThrowIfNull(view);
        var findings = new List<string>();
        try
        {
            MapSurfaceRef? surface = MapBoundaryGeometry.Surface(view, patch.Key.SurfaceId);
            if (surface is null)
            {
                Add(new(MapDocumentRefusal.ReferenceMissing, "unresolved dependent surface")
                { Patch = patch.Key, SurfaceId = patch.Key.SurfaceId, FieldPath = "Key.SurfaceId" });
                return findings.AsReadOnly();
            }
            MapSurfaceCompiler.Validate(surface, patch);
            var builder = new MapSurfaceVertexBuilder(surface, patch, MapSurfaceCompiler.Anchor(surface, patch));
            var owners = new Dictionary<MapPatchKey, MapBoundarySurface?>();
            int index = -1;
            foreach (MapCornerDependency dependency in patch.CornerDependencies)
            {
                index++;
                if (!owners.TryGetValue(dependency.Owner.Patch, out MapBoundarySurface? owner))
                {
                    owner = MapBoundaryGeometry.Read(dependency.Owner.Patch, view, out _);
                    owners.Add(dependency.Owner.Patch, owner);
                }
                if (owner is null)
                {
                    Add(new(MapDocumentRefusal.PatchUnavailable, $"unresolved corner owner patch '{dependency.Owner.Patch}'")
                    { Patch = patch.Key, ExpectedPatch = dependency.Owner.Patch, Address = dependency.Owner.Address, Index = index });
                    continue;
                }
                MapExactPoint dependent = builder.Point(dependency.CornerX, dependency.CornerZ, MapLatticePoint.Sw).Exact;
                MapExactXz ownerPosition = owner.Surface.Frame.WorldXz(dependency.Owner.Address);
                if (dependent.X != ownerPosition.X || dependent.Z != ownerPosition.Z)
                    Add(new(MapDocumentRefusal.SurfaceSeamPositionMismatch, "corner dependency position: dependent and owner disagree in world XZ")
                    {
                        Patch = patch.Key, ExpectedPatch = dependency.Owner.Patch, Address = dependency.Owner.Address,
                        Index = index, CellX = dependency.CornerX, CellZ = dependency.CornerZ,
                    });
                if (!owner.Vertices.TryGetValue(dependency.Owner.Address, out MapExactPoint ownerPoint))
                    Add(new(MapDocumentRefusal.SurfaceSeamVertexUndeclared, "corner dependency owner: address is not a declared surface vertex")
                    { Patch = patch.Key, ExpectedPatch = dependency.Owner.Patch, Address = dependency.Owner.Address, Index = index });
                else if (dependent.Y != ownerPoint.Y)
                    Add(new(MapDocumentRefusal.GeometryVertexHeightMismatch, "corner dependency owner: exact height disagrees")
                    { Patch = patch.Key, ExpectedPatch = dependency.Owner.Patch, Address = dependency.Owner.Address, Index = index });
            }
        }
        catch (Exception error) when (error is MapExactOverflowException or OverflowException)
        {
            Add(new(MapDocumentRefusal.GeometryNotRepresentable, "corner dependency geometry is not representable")
            { Patch = patch.Key, Operation = nameof(ValidateCornerDependencies) });
        }
        catch (MapDocumentException error)
        {
            if (MapDocumentFinding.FromException(error) is { } finding) Add(finding);
            else findings.Add(error.Message); // Paint-priority admission is converted with its producer.
        }
        return findings.AsReadOnly();

        void Add(MapDocumentFinding finding)
        {
            findings.Add(finding.Message);
            typedFindings?.Add(finding);
        }
    }

    static Dictionary<MapLatticeAddress, MapExactPoint> Edge(MapSurfaceEdgeRef edge, MapBoundarySurface source,
        string seamId, string side)
    {
        if (edge.From.SurfaceId != source.Surface.Id || edge.To.SurfaceId != source.Surface.Id)
            throw new MapDocumentException(MapDocumentRefusal.AuthoredValueMismatch, "seam position: edge surface mismatch")
            {
                RecordId = seamId, Patch = edge.Patch,
                SurfaceId = edge.From.SurfaceId != source.Surface.Id ? edge.From.SurfaceId : edge.To.SurfaceId,
                FieldPath = side + (edge.From.SurfaceId != source.Surface.Id ? ".From.SurfaceId" : ".To.SurfaceId"),
            };
        MapExactXz from = source.Surface.Frame.WorldXz(edge.From.Address);
        MapExactXz to = source.Surface.Frame.WorldXz(edge.To.Address);
        if (from == to || (from.X != to.X && from.Z != to.Z))
            throw new MapDocumentException(MapDocumentRefusal.SurfaceSeamEdgeInvalid, "seam position: edge must be a nonzero lattice edge")
            { RecordId = seamId, Patch = edge.Patch, SurfaceId = source.Surface.Id, FieldPath = side };
        var vertices = source.Vertices.Where(vertex => OnEdge(vertex.Value, from, to))
            .ToDictionary(vertex => vertex.Key, vertex => vertex.Value);
        bool missingFrom = !vertices.ContainsKey(edge.From.Address);
        if (missingFrom || !vertices.ContainsKey(edge.To.Address))
            throw new MapDocumentException(MapDocumentRefusal.SurfaceSeamVertexUndeclared, "seam subdivide: edge endpoints must be declared surface vertices")
            {
                RecordId = seamId, Patch = edge.Patch, SurfaceId = source.Surface.Id,
                Address = missingFrom ? edge.From.Address : edge.To.Address, FieldPath = side + (missingFrom ? ".From" : ".To"),
            };
        return vertices;
    }

    static bool OnEdge(MapExactPoint point, MapExactXz from, MapExactXz to) =>
        from.X == to.X ? point.X == from.X && Between(point.Z, from.Z, to.Z)
            : point.Z == from.Z && Between(point.X, from.X, to.X);

    static bool Between(MapExactValue value, MapExactValue first, MapExactValue last) =>
        first.CompareTo(last) <= 0 ? value.CompareTo(first) >= 0 && value.CompareTo(last) <= 0
            : value.CompareTo(last) >= 0 && value.CompareTo(first) <= 0;
}
