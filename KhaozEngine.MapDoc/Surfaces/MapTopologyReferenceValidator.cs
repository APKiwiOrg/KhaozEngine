using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Spaces;

namespace KhaozEngine.MapDoc.Surfaces;

/// <summary>Complete-view reference integrity. Geometry certification is a separate operation.</summary>
public static class MapTopologyReferenceValidator
{
    public static IReadOnlyList<string> Validate(IReadOnlyList<MapSurfaceRef> surfaces, IReadOnlyCollection<MapSurfacePatch> patches)
    {
        ArgumentNullException.ThrowIfNull(surfaces);
        ArgumentNullException.ThrowIfNull(patches);
        var context = new Context(surfaces, patches);
        context.Validate();
        return context.Errors;
    }

    sealed class Context
    {
        readonly Dictionary<string, MapSurfaceRef> _surfaces = new(StringComparer.Ordinal);
        readonly Dictionary<MapPatchKey, MapSurfacePatch> _patches = new();
        readonly Dictionary<string, (MapPatchKey Anchor, MapTopologyRecord Record)> _records = new(StringComparer.Ordinal);
        readonly HashSet<(string Record, MapSide Side)> _sides = new();
        readonly Dictionary<(string Space, MapRecordRef Reference, MapSide Side), int> _portalMembership = new();
        readonly Dictionary<(string Space, MapRecordRef Reference), int> _linkMembership = new();
        internal List<string> Errors { get; } = new();

        internal Context(IReadOnlyList<MapSurfaceRef> surfaces, IReadOnlyCollection<MapSurfacePatch> patches)
        {
            foreach (MapSurfaceRef surface in surfaces)
            {
                if (surface is null || string.IsNullOrWhiteSpace(surface.Id)) { Errors.Add("invalid surface id"); continue; }
                if (!_surfaces.TryAdd(surface.Id, surface)) Errors.Add($"duplicate id '{surface.Id}' in surfaces");
                if (!Enum.IsDefined(surface.Role) || !Enum.IsDefined(surface.PresencePolicy) || surface.Frame is null)
                    Errors.Add($"invalid surface role/frame '{surface.Id}'");
                else
                    try { _ = surface.Frame.WorldXz(MapLatticeAddress.Corner(0, 0)); }
                    catch (MapDocumentException) { Errors.Add($"invalid surface frame '{surface.Id}'"); }
            }
            foreach (MapSurfacePatch patch in patches.OrderBy(p => p.Key))
            {
                IReadOnlyList<string> local = patch.ValidateLocal();
                if (local.Count != 0) { Errors.AddRange(local); continue; }
                if (!_surfaces.ContainsKey(patch.Key.SurfaceId)) Errors.Add($"missing surface '{patch.Key.SurfaceId}'");
                if (!_patches.TryAdd(patch.Key, patch)) Errors.Add("duplicate patch key");
                foreach (MapTopologyRecord record in patch.Records)
                {
                    if (record is null || string.IsNullOrWhiteSpace(record.Id)) { Errors.Add("invalid record id"); continue; }
                    if (!_records.TryAdd(record.Id, (patch.Key, record))) Errors.Add($"duplicate id '{record.Id}'");
                }
            }
            foreach (MapSpaceDoc space in _records.Values.Select(r => r.Record).OfType<MapSpaceDoc>())
            {
                foreach (MapBoundaryRef boundary in space.Portals)
                {
                    var key = (space.Id, boundary.Record, boundary.Side);
                    _portalMembership[key] = _portalMembership.GetValueOrDefault(key) + 1;
                }
                foreach (MapRecordRef link in space.Links)
                {
                    var key = (space.Id, link);
                    _linkMembership[key] = _linkMembership.GetValueOrDefault(key) + 1;
                }
            }
        }

        internal void Validate()
        {
            foreach (var row in _records.OrderBy(r => r.Key, StringComparer.Ordinal).Select(r => r.Value))
            {
                MapTopologyRecord record = row.Record;
                switch (record)
                {
                    case MapSurfaceSeam seam:
                        Edge(seam.First, seam.Id); Edge(seam.Second, seam.Id);
                        foreach (var pair in seam.Pairs) { Vertex(pair.First, seam.Id); Vertex(pair.Second, seam.Id); }
                        break;
                    case MapBoundaryChain chain: Chain(chain); break;
                    case MapWallStrip strip:
                        _ = Find<MapBoundaryChain>(strip.LowerChain, strip.Id); _ = Find<MapBoundaryChain>(strip.UpperChain, strip.Id);
                        if (!Enum.IsDefined(strip.Facing)) Errors.Add($"invalid strip facing '{strip.Id}'");
                        break;
                    case MapCavePortal portal: Portal(portal, row.Anchor); break;
                    case MapHorizontalOpening opening: Slots(opening.Patch, opening.SlotCells, opening.Id, requireAbsent: true); break;
                    case MapVerticalLink link: Link(link, row.Anchor); break;
                    case MapSpaceDoc space: Space(space); break;
                    case MapSpaceFootprint footprint: Footprint(footprint); break;
                    default: Errors.Add($"unknown record type '{record.Id}'"); break;
                }
            }
            ValidateParentCycles();
        }

        void ValidateParentCycles()
        {
            // One call-owned traversal state prevents a long parent chain being walked once per space.
            var state = new Dictionary<string, byte>(StringComparer.Ordinal);
            foreach (MapSpaceDoc space in _records.Values.Select(r => r.Record).OfType<MapSpaceDoc>())
            {
                var path = new List<string>();
                MapSpaceDoc current = space;
                while (true)
                {
                    byte seen = state.GetValueOrDefault(current.Id);
                    if (seen != 0)
                    {
                        if (seen == 1) Errors.Add($"parent cycle at '{current.Id}'");
                        break;
                    }
                    state[current.Id] = 1;
                    path.Add(current.Id);
                    if (current.Parent is not { } parent || !_records.TryGetValue(parent.Id, out var row) ||
                        row.Anchor != parent.Anchor || row.Record is not MapSpaceDoc next) break;
                    current = next;
                }
                foreach (string id in path) state[id] = 2;
            }
        }

        T? Find<T>(MapRecordRef? reference, string source) where T : MapTopologyRecord
        {
            if (reference is null || string.IsNullOrWhiteSpace(reference.Id)) { Errors.Add($"missing reference in '{source}'"); return null; }
            if (!_records.TryGetValue(reference.Id, out var row)) { Errors.Add($"missing record '{reference.Id}' in '{source}'"); return null; }
            if (row.Anchor != reference.Anchor) { Errors.Add($"wrong anchor for '{reference.Id}' in '{source}'"); return null; }
            if (row.Record is not T typed) { Errors.Add($"wrong type for '{reference.Id}' in '{source}'"); return null; }
            return typed;
        }
        void Vertex(MapLatticeVertex vertex, string source)
        {
            if (string.IsNullOrWhiteSpace(vertex.SurfaceId) || !_surfaces.ContainsKey(vertex.SurfaceId))
                Errors.Add($"missing vertex surface in '{source}'");
            try { vertex.Address.RequireValid(); }
            catch (MapDocumentException) { Errors.Add($"invalid vertex denominator in '{source}'"); }
        }
        void Edge(MapSurfaceEdgeRef edge, string source)
        {
            if (!_patches.ContainsKey(edge.Patch)) Errors.Add($"missing edge patch in '{source}'");
            Vertex(edge.From, source); Vertex(edge.To, source);
            if (edge.From.SurfaceId != edge.Patch.SurfaceId || edge.To.SurfaceId != edge.Patch.SurfaceId)
                Errors.Add($"edge surface mismatch in '{source}'");
        }
        void Chain(MapBoundaryChain chain)
        {
            if (!Enum.IsDefined(chain.Kind) || chain.Vertices.Count is < 2 or > 4097)
                Errors.Add($"invalid chain kind/vertices '{chain.Id}'");
            if (chain.Kind == MapChainKind.SurfaceEdge && (chain.SourcePatch is null || !_patches.ContainsKey(chain.SourcePatch.Value)))
                Errors.Add($"missing chain source patch '{chain.Id}'");
            if (chain.Kind == MapChainKind.Authored && chain.SourcePatch is not null)
                Errors.Add($"authored chain has source patch '{chain.Id}'");
            foreach (MapChainVertex v in chain.Vertices)
            {
                Vertex(v.Vertex, chain.Id);
                if ((chain.Kind == MapChainKind.Authored) != v.HeightUnits.HasValue)
                    Errors.Add($"chain height kind mismatch '{chain.Id}'");
                if (chain.Kind == MapChainKind.SurfaceEdge && chain.SourcePatch is { } patch && v.Vertex.SurfaceId != patch.SurfaceId)
                    Errors.Add($"chain source surface mismatch '{chain.Id}'");
            }
        }
        void Portal(MapCavePortal portal, MapPatchKey anchor)
        {
            MapSpaceDoc? from = Find<MapSpaceDoc>(portal.FromSpace, portal.Id), to = Find<MapSpaceDoc>(portal.ToSpace, portal.Id);
            _ = Find<MapBoundaryChain>(portal.BandBottom, portal.Id);
            if (portal.BandTop is not null) _ = Find<MapBoundaryChain>(portal.BandTop, portal.Id);
            else if (from is not null && to is not null && (from.Kind != MapSpaceKind.Exterior || to.Kind != MapSpaceKind.Exterior))
                Errors.Add($"open top portal requires Exterior spaces '{portal.Id}'");
            if (portal.Interval.Count < 2) Errors.Add($"invalid portal interval '{portal.Id}'");
            foreach (MapLatticeVertex v in portal.Interval) Vertex(v, portal.Id);
            var reference = new MapRecordRef(portal.Id, anchor);
            if (from is not null && _portalMembership.GetValueOrDefault((from.Id, reference, MapSide.Front)) != 1)
                Errors.Add($"portal list mismatch '{portal.Id}'");
            if (to is not null && _portalMembership.GetValueOrDefault((to.Id, reference, MapSide.Back)) != 1)
                Errors.Add($"portal list mismatch '{portal.Id}'");
        }
        void Link(MapVerticalLink link, MapPatchKey anchor)
        {
            MapSpaceDoc? upper = Find<MapSpaceDoc>(link.UpperSpace, link.Id), lower = Find<MapSpaceDoc>(link.LowerSpace, link.Id);
            RefSet<MapHorizontalOpening>(link.Openings, link.Id); RefSet<MapCavePortal>(link.Portals, link.Id);
            RefSet<MapTopologyRecord>(link.GeometryOwners, link.Id);
            foreach (MapRecordRef owner in link.GeometryOwners)
                if (_records.TryGetValue(owner.Id, out var row) && row.Record is MapSpaceDoc or MapSpaceFootprint or MapVerticalLink)
                    Errors.Add($"wrong geometry owner type '{owner.Id}' in '{link.Id}'");
            var reference = new MapRecordRef(link.Id, anchor);
            if (upper is not null && _linkMembership.GetValueOrDefault((upper.Id, reference)) != 1) Errors.Add($"link list mismatch '{link.Id}'");
            if (lower is not null && _linkMembership.GetValueOrDefault((lower.Id, reference)) != 1) Errors.Add($"link list mismatch '{link.Id}'");
        }
        void RefSet<T>(IReadOnlyList<MapRecordRef> refs, string source) where T : MapTopologyRecord
        {
            var seen = new HashSet<MapRecordRef>();
            foreach (MapRecordRef reference in refs)
            {
                if (!seen.Add(reference)) Errors.Add($"duplicate reference '{reference.Id}' in '{source}'");
                _ = Find<T>(reference, source);
            }
        }
        void Space(MapSpaceDoc space)
        {
            if (!Enum.IsDefined(space.Kind)) Errors.Add($"invalid space kind '{space.Id}'");
            if (space.Parent is not null) _ = Find<MapSpaceDoc>(space.Parent, space.Id);
            if (space.AliasOf is not null)
            {
                MapSpaceDoc? target = Find<MapSpaceDoc>(space.AliasOf, space.Id);
                if (target is not null && (target.AliasOf is not null || target.Parent != space.Parent || target.Kind != space.Kind))
                    Errors.Add($"alias must target an alias-free peer '{space.Id}'");
            }
            foreach (MapBoundaryRef wall in space.Walls)
            {
                _ = Find<MapWallStrip>(wall.Record, space.Id); Side(wall, space.Id);
            }
            foreach (MapBoundaryRef boundary in space.Portals)
            {
                MapCavePortal? portal = Find<MapCavePortal>(boundary.Record, space.Id); Side(boundary, space.Id);
                if (portal is not null)
                {
                    MapRecordRef endpoint = boundary.Side == MapSide.Front ? portal.FromSpace : portal.ToSpace;
                    if (Find<MapSpaceDoc>(endpoint, portal.Id)?.Id != space.Id) Errors.Add($"portal list mismatch '{space.Id}'");
                }
            }
            RefSet<MapVerticalLink>(space.Links, space.Id);
            foreach (MapRecordRef reference in space.Links)
                if (_records.TryGetValue(reference.Id, out var row) && row.Record is MapVerticalLink link &&
                    link.UpperSpace.Id != space.Id && link.LowerSpace.Id != space.Id)
                    Errors.Add($"link list mismatch '{space.Id}'");
        }
        void Side(MapBoundaryRef boundary, string source)
        {
            if (!Enum.IsDefined(boundary.Side)) Errors.Add($"invalid boundary side in '{source}'");
            if (!_sides.Add((boundary.Record.Id, boundary.Side))) Errors.Add($"duplicate boundary side '{boundary.Record.Id}'");
        }
        void Footprint(MapSpaceFootprint footprint)
        {
            MapSpaceDoc? space = Find<MapSpaceDoc>(footprint.Space, footprint.Id);
            Slots(footprint.Lattice, footprint.SlotCells, footprint.Id, requireAbsent: false);
            Bound(footprint.Lower, lower: true, space, footprint.Id);
            Bound(footprint.Upper, lower: false, space, footprint.Id);
            _surfaces.TryGetValue(footprint.Lower.SurfaceId ?? "", out MapSurfaceRef? lowerSurface);
            if (space is not null && MapLegacyExteriorRecipe.Check(footprint, space, lowerSurface) is { } failure)
                Errors.Add($"{failure}: footprint '{footprint.Id}'");
        }
        void Bound(MapBoundRef bound, bool lower, MapSpaceDoc? space, string source)
        {
            if (bound.Kind == MapBoundKind.OpenTop)
            {
                if (lower || space?.Kind != MapSpaceKind.Exterior || bound.SurfaceId is not null || bound.Opening is not null)
                    Errors.Add($"invalid open top bound '{source}'");
                return;
            }
            if (bound.Kind == MapBoundKind.HorizontalOpening)
            {
                _ = Find<MapHorizontalOpening>(bound.Opening, source);
                if (bound.SurfaceId is not null) Errors.Add($"invalid opening bound surface '{source}'");
                return;
            }
            MapSurfaceRole expected = lower ? MapSurfaceRole.SupportFloor : MapSurfaceRole.Ceiling;
            bool kind = lower ? bound.Kind is MapBoundKind.SupportFloor or MapBoundKind.LegacyExteriorV1 : bound.Kind == MapBoundKind.Ceiling;
            if (!kind || bound.Opening is not null || bound.SurfaceId is null ||
                !_surfaces.TryGetValue(bound.SurfaceId, out MapSurfaceRef? surface) || surface.Role != expected)
                Errors.Add($"bound surface role mismatch '{source}'");
        }
        void Slots(MapPatchKey key, IReadOnlyList<int> cells, string source, bool requireAbsent)
        {
            if (!_patches.TryGetValue(key, out MapSurfacePatch? patch)) { Errors.Add($"missing lattice patch '{source}'"); return; }
            if (cells.Count is < 1 or > 4096) Errors.Add($"invalid slot cell count '{source}'");
            int previous = -1;
            foreach (int cell in cells)
            {
                if (cell <= previous) Errors.Add($"duplicate or unordered slot cell '{source}'");
                previous = cell;
                int x = cell % 64 - patch.CellMinX, z = cell / 64 - patch.CellMinZ;
                if (cell is < 0 or >= 4096 || x < 0 || x >= patch.Width || z < 0 || z >= patch.Depth)
                    Errors.Add($"slot cell outside patch '{source}'");
                else if (requireAbsent && patch.IsPresent(x, z)) Errors.Add($"opening over present cell '{source}'");
            }
        }
    }
}
