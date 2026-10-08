using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Spaces;

/// <summary>Bounded connectivity over acquired portals and opening bounds, without expanding space boundary lists.</summary>
public sealed class MapSpaceRelations
{
    readonly MapScopedSurfaces _view;

    public MapSpaceRelations(MapScopedSurfaces scoped)
    {
        ArgumentNullException.ThrowIfNull(scoped);
        _view = scoped;
    }

    sealed record Edge(MapRecordRef Record, MapRecordRef From, MapRecordRef To, MapRecordRef? Link);

    public MapRelationResult Relate(MapFramePoint a, MapFramePoint b, MapRelationQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var membership = new MapSpaceMembership(_view);
        MapMembershipResult first = membership.Query(a), second = membership.Query(b);
        if (query.MaxPortalHops < 0) return Failure(MapRelationStatus.Invalid, "invalid negative portal hop bound");
        foreach (MapMembershipResult endpoint in new[] { first, second })
            if (endpoint.Status is not (MapMembershipStatus.Resolved or MapMembershipStatus.Outside))
                return Failure(Status(endpoint.Status), endpoint.Detail);
        if (first.SpaceId is null || second.SpaceId is null)
            return Resolved(MapGeometricRelation.NotConnectedWithinBound, Array.Empty<MapPortalFact>());
        if (first.SpaceId == second.SpaceId)
            return Resolved(MapGeometricRelation.SameSpace, Array.Empty<MapPortalFact>());
        if (query.MaxPortalHops == 0)
            return Resolved(MapGeometricRelation.NotConnectedWithinBound, Array.Empty<MapPortalFact>());

        try
        {
            var references = _view.Witness.Records.ToDictionary(r => r.Id, StringComparer.Ordinal);
            MapTopologyRecord[] records = MapSpaceGeometry.Records(_view).OrderBy(r => r.Id, StringComparer.Ordinal).ToArray();
            MapVerticalLink[] links = records.OfType<MapVerticalLink>().ToArray();
            MapSpaceFootprint[] footprints = records.OfType<MapSpaceFootprint>().ToArray();
            var owners = new Dictionary<MapRecordRef, MapRecordRef>();
            var edges = new List<Edge>();
            foreach (MapCavePortal portal in records.OfType<MapCavePortal>())
            {
                MapRecordRef reference = references[portal.Id];
                edges.Add(new(reference, Owner(portal.FromSpace), Owner(portal.ToSpace), Link(reference, opening: false)));
            }
            if (query.IncludeVerticalLinks)
                foreach (MapHorizontalOpening opening in records.OfType<MapHorizontalOpening>())
                {
                    MapRecordRef reference = references[opening.Id];
                    MapRecordRef[] above = Bounds(reference, lower: true), below = Bounds(reference, lower: false);
                    foreach (MapRecordRef upper in above)
                        foreach (MapRecordRef lower in below)
                            if (upper != lower) edges.Add(new(reference, upper, lower, Link(reference, opening: true)));
                }
            Edge[] ordered = edges.OrderBy(e => e.Record.Id, StringComparer.Ordinal)
                .ThenBy(e => e.From.Id, StringComparer.Ordinal).ThenBy(e => e.To.Id, StringComparer.Ordinal).ToArray();
            MapRecordRef start = references[first.SpaceId], target = references[second.SpaceId];
            var pending = new Queue<(MapRecordRef Space, int Hops)>();
            var visited = new HashSet<MapRecordRef> { start };
            var parents = new Dictionary<MapRecordRef, Edge>();
            pending.Enqueue((start, 0));
            while (pending.TryDequeue(out var current))
            {
                if (current.Hops >= query.MaxPortalHops) continue;
                // Only acquired footprint bounds are consulted. Space Walls, Portals and Links are never traversed.
                if (query.IncludeVerticalLinks)
                    foreach (MapSpaceFootprint footprint in footprints)
                        if (Owner(footprint.Space) == current.Space)
                            foreach (MapBoundRef bound in new[] { footprint.Lower, footprint.Upper })
                                if (bound.Kind == MapBoundKind.HorizontalOpening && bound.Opening is { } opening)
                                    _ = MapSpaceGeometry.Record<MapHorizontalOpening>(_view, opening);
                foreach (Edge edge in ordered)
                {
                    Edge? step = edge.From == current.Space ? edge : edge.To == current.Space
                        ? edge with { From = edge.To, To = edge.From } : null;
                    if (step is null || !visited.Add(step.To)) continue;
                    parents.Add(step.To, step);
                    if (step.To == target)
                    {
                        var path = new List<Edge>();
                        for (MapRecordRef node = target; node != start; node = parents[node].From) path.Add(parents[node]);
                        path.Reverse();
                        MapPortalFact[] facts = path.Select(step => MapRelationApertures.Compile(_view,
                            step.Record, step.From, step.To, step.Link)).ToArray();
                        return Resolved(facts.Any(f => f.Kind == MapPortalKind.HorizontalOpening)
                            ? MapGeometricRelation.ConnectedThroughVerticalLink : MapGeometricRelation.ConnectedThroughPortals, facts);
                    }
                    pending.Enqueue((step.To, current.Hops + 1));
                }
            }
            return Resolved(MapGeometricRelation.NotConnectedWithinBound, Array.Empty<MapPortalFact>());

            MapRecordRef Owner(MapRecordRef reference)
            {
                if (owners.TryGetValue(reference, out MapRecordRef? result)) return result;
                string id = MapSpaceGeometry.Owner(_view, MapSpaceGeometry.Record<MapSpaceDoc>(_view, reference));
                result = references[id];
                owners.Add(reference, result);
                return result;
            }
            MapRecordRef[] Bounds(MapRecordRef reference, bool lower) => footprints
                .Where(f => (lower ? f.Lower : f.Upper) is { Kind: MapBoundKind.HorizontalOpening, Opening: { } bound } && bound == reference)
                .Select(f => Owner(f.Space)).Distinct().OrderBy(r => r.Id, StringComparer.Ordinal).ToArray();
            MapRecordRef? Link(MapRecordRef reference, bool opening) => links
                .Where(l => (opening ? l.Openings : l.Portals).Contains(reference)).Select(l => references[l.Id]).FirstOrDefault();
        }
        catch (Exception error) when (error is MapExactOverflowException or OverflowException)
        { return Failure(MapRelationStatus.NotRepresentable, "overflow"); }
        catch (MapDocumentException error)
        {
            return Failure(MapCommonRefinement.NotRepresentable(error) || error.Message.Contains("not representable", StringComparison.Ordinal)
                ? MapRelationStatus.NotRepresentable :
                error.Message.Contains("invalid", StringComparison.Ordinal) ? MapRelationStatus.Invalid : MapRelationStatus.MissingGeometry,
                error.Message);
        }

        MapRelationResult Failure(MapRelationStatus status, string? detail) => new(status, a, b, query, first, second,
            MapGeometricRelation.Undetermined, Array.Empty<MapPortalFact>(), detail, _view.ReadWitness);
        MapRelationResult Resolved(MapGeometricRelation relation, IEnumerable<MapPortalFact> path) =>
            new(MapRelationStatus.Resolved, a, b, query, first, second, relation, path, null, _view.ReadWitness);
    }

    static MapRelationStatus Status(MapMembershipStatus status) => status switch
    {
        MapMembershipStatus.MissingGeometry => MapRelationStatus.MissingGeometry,
        MapMembershipStatus.Ambiguous => MapRelationStatus.Ambiguous,
        MapMembershipStatus.CapacityExceeded => MapRelationStatus.CapacityExceeded,
        MapMembershipStatus.NotRepresentable => MapRelationStatus.NotRepresentable,
        _ => MapRelationStatus.Invalid,
    };
}
