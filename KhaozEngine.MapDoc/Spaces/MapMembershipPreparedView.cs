using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Spaces;

/// <summary>Immutable view products and private semantic rows, independent of point-query registers and stamps.</summary>
internal sealed class MapMembershipPreparedView
{
    internal readonly record struct Prism(MapSpaceDoc Space, string Owner, string Parent, MapMembershipPolygonRange Rings,
        MapExactValue Lower, MapExactValue Upper);
    internal readonly MapScopedSurfaces View;
    internal readonly MapMembershipPreparedGeometry Geometry;
    internal readonly MapMembershipPreparedFootprint[] Footprints;
    internal readonly MapMembershipPreparedPortal[] Portals;
    internal readonly Dictionary<string, MapSpaceDoc> Spaces;
    internal readonly Dictionary<string, string> Owners;
    internal readonly Dictionary<string, MapSpaceDoc[]> Ancestors;
    internal readonly MapPrismDomain[] Prisms;
    internal readonly Prism[] Pieces;
    internal readonly MapMembershipGeometryArena? ArenaOwner;
    internal readonly long PrismVertices;
    internal readonly string? RoomFailure;
    internal long SharedBytes => Footprints.Sum(f => f.SharedBytes);
    internal long InventoryBytes => checked(256L + Geometry.InventoryBytes + (ArenaOwner?.InventoryBytes ?? 0) +
        Footprints.Sum(f => 8L + f.InventoryBytes) + Portals.Sum(p => 8L + p.InventoryBytes) +
        192L * Spaces.Count + Ancestors.Values.Sum(a => 96L + 8L * a.Length) + 128L * Pieces.Length);
    internal MapMembershipPreparedView(MapScopedSurfaces view, MapMembershipDemandPlan? plan)
    {
        using var building = MapMembershipArenaBuilder.Begin();
        ArenaOwner = building.Owns ? building.Arena : null;
        View = view;
        var records = MapSpaceGeometry.Records(view).OrderBy(r => r.Id, StringComparer.Ordinal).ToArray();
        Prisms = records.OfType<MapPrismDomain>().ToArray();
        Spaces = records.OfType<MapSpaceDoc>().ToDictionary(s => s.Id, StringComparer.Ordinal);
        Owners = Spaces.ToDictionary(p => p.Key, p => MapSpaceGeometry.Owner(view, p.Value), StringComparer.Ordinal);
        Ancestors = Spaces.ToDictionary(p => p.Key, p => MapSpaceGeometry.Ancestors(view, p.Value).ToArray(), StringComparer.Ordinal);
        PrismVertices = Prisms.Sum(MapPrismGeometry.Cost);
        var roots = records.OfType<MapSpaceFootprint>().ToArray();
        if (roots.Any(f => Spaces.TryGetValue(f.Space.Id, out var s) && s.Kind == MapSpaceKind.Room))
            RoomFailure = "room occupancy requires its prism, not a second lattice domain";
        else if (Spaces.Values.Any(s => s.Kind == MapSpaceKind.Room && s.AliasOf is null && Prisms.Count(p => p.Space.Id == s.Id) != 1))
            RoomFailure = "room requires one acquired prism";
        plan ??= new(view, records);
        Geometry = new(view, records, plan);
        Footprints = roots.Select(f => new MapMembershipPreparedFootprint(view, Geometry, f, plan)).ToArray();
        Portals = records.OfType<MapCavePortal>().Select(p => new MapMembershipPreparedPortal(view, Geometry, p, Prisms)).ToArray();
        Pieces = Prisms.Select(p =>
        {
            using var domainWorkspace = MapMembershipBuild.Kernel(checked(32768L + 1024L * MapPrismGeometry.Cost(p)));
            MapPrismCoverage.RequireStorey(view, p);
            var s = Spaces[p.Space.Id];
            if (s.Kind != MapSpaceKind.Room || s.Parent != p.Parent) throw new MapDocumentException("missing geometry: prism parent mismatch");
            var domain = MapPrismGeometry.Prepare(view, p);
            var heights = MapPrismGeometry.Heights(view, p);
            int first = building.Arena.Polygons.Count;
            foreach (var piece in domain.Pieces) _ = new MapMembershipPolygon(piece.Ring);
            return new Prism(Spaces[Owners[s.Id]], Owners[s.Id], Owners[p.Parent.Id],
                new(building.Arena, first, domain.Pieces.Count), heights.Lower, heights.Upper);
        }).ToArray();
        Geometry.Bounds.Freeze();
    }
}
