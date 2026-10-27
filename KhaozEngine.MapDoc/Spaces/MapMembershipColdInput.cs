using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Spaces;

/// <summary>The allocating point adapter retains released preparation and provenance materialization.</summary>
internal sealed class MapMembershipColdInput(MapScopedSurfaces view, MapExactXz point, MapPrismDomain[] prisms,
    int candidateCount, int portalCount, Func<int, MapMembershipPart?> candidate, Func<int, MapMembershipPart?> portal, Func<MapMembershipStatus> failure)
    : IMapMembershipPointInput
{
    public MapPrismDomain[] Prisms => prisms;
    public int CandidateCount => candidateCount;
    public int PortalCount => portalCount;
    public int MaxDepth => view.Scope.Limits.MaxRecordDepth;
    public object? MaxDepthSource => view.Scope.Limits;
    public bool PruneAncestors => view.EffectiveContext is not null;
    public MapMembershipStatus FailureStatus => failure();
    public MapMembershipPart? Candidate(int index) => candidate(index);
    public MapMembershipPart? Portal(int index) => portal(index);
    MapSpaceDoc Space(MapRecordRef reference) => MapSpaceGeometry.Record<MapSpaceDoc>(view, reference);
    public string Owner(MapSpaceDoc space) => MapSpaceGeometry.Owner(view, space);
    public string PrismOwner(int index) => Owner(Space(prisms[index].Space));
    public string PrismParent(int index) => Owner(Space(prisms[index].Parent));
    public void ValidatePrism(int index)
    {
        var prism = prisms[index];
        MapPrismCoverage.RequireStorey(view, prism);
        var space = Space(prism.Space);
        _ = Owner(Space(prism.Parent));
        if (space.Kind != MapSpaceKind.Room || space.Parent != prism.Parent)
            throw new MapDocumentException("missing geometry: prism parent mismatch");
    }
    public bool PrismContains(int index) => MapPrismGeometry.Compile(view, prisms[index]).Contains(point);
    public (MapExactValue Lower, MapExactValue Upper) PrismHeights(int index) => MapPrismGeometry.Heights(view, prisms[index]);
    public IReadOnlyList<MapSpaceDoc> Ancestors(MapSpaceDoc space) => MapSpaceGeometry.Ancestors(view, space);
    public bool Ancestor(string ancestor, string child) => MapOccupiedPartitions.Ancestor(view, ancestor, child);
    public int Direction(MapMembershipPart part, bool upper) => MapSpaceGeometry.Direction(
        upper ? part.Cold!.UpperFaces : part.Cold!.LowerFaces, point);
    public MapMembershipPart Child(int index, int parentIndex, MapMembershipPart parent, MapExactValue lower,
        MapExactValue upper, bool parentLow, bool parentHigh)
    {
        var prism = prisms[index]; var prior = parent.Cold!;
        string owner = PrismOwner(index);
        var lo = parentLow ? prior.LowerBoundary! : MapOccupiedPartitions.Boundary(view, prism.Id, "prism-lower");
        var hi = parentHigh ? prior.UpperBoundary! : MapOccupiedPartitions.Boundary(view, prism.Id, "prism-upper");
        var part = new MapSpaceBoundsPart(owner, owner, lower, upper, lo.OwnerId, hi.OwnerId, false)
        {
            LowerBoundary = lo,
            UpperBoundary = hi,
            LowerFaces = parentLow ? prior.LowerFaces : Array.Empty<MapBoundFace>(),
            UpperFaces = parentHigh ? prior.UpperFaces : Array.Empty<MapBoundFace>(),
            AuthoredParent = prism.Parent,
            Path = Array.AsReadOnly(prior.Path.Concat(new[] { prism.Parent.Id, prior.LowerBoundary!.OwnerId,
                prior.LowerBoundary.Role, prior.UpperBoundary!.OwnerId, prior.UpperBoundary.Role }).ToArray())
        };
        return new(MapSpaceGeometry.Records(view).OfType<MapSpaceDoc>().First(s => s.Id == owner), owner, lower, upper,
            MapOccupiedPartitions.Identify(view, part), Parent: parentIndex, Prism: index);
    }
    public MapMembershipPart Carve(MapMembershipPart part, MapExactValue lower, MapExactValue? upper,
        MapMembershipPart? lowCut, MapMembershipPart? highCut)
    {
        var prior = part.Cold!;
        var lo = lowCut?.Cold?.UpperBoundary ?? prior.LowerBoundary;
        var hi = highCut?.Cold?.LowerBoundary ?? prior.UpperBoundary;
        var carved = prior with
        {
            Lower = lower,
            Upper = upper,
            LowerBoundaryId = lo!.OwnerId,
            LowerBoundary = lo,
            UpperBoundaryId = hi!.OwnerId,
            UpperBoundary = hi
        };
        return part with { Lower = lower, Upper = upper, Cold = MapOccupiedPartitions.Identify(view, carved) };
    }
}
