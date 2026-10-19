using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Support;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Foliage;

/// <summary>A binding's support decision at one candidate. Space is the occupied space, never the support identity.</summary>
internal readonly record struct MapFoliageCandidateOutcome(MapFoliageStatus Status, MapFoliageSupportGrid? Grid, MapFoliageSupportCell? Cell,
    long CellX, long CellZ, string? OwnerId, string? SpaceId);

/// <summary>Decides support and occupied space per candidate from one prepared chunk context, with no support query and
/// no second membership rule. Every bound, portal source and indoor face a candidate in the chunk can demand is compiled
/// once into a shared <see cref="MapBoundFaceContext"/>. Each candidate then runs the released membership and Column paths
/// over that context at its own heights, so parent clipping, nested rooms, portal bands, missing bounds and their refusals
/// are the released ones at that exact point. The support rule follows the released selection kernel: the lowest-keyed
/// face holding the point on each support floor, the binding's search band and an upward normal, room parts from the
/// Column, footprints and reachability, the highest eligible face with equal-height independent owners ambiguous, then
/// the legacy fallback from membership at ReferenceY.</summary>
internal sealed class MapFoliageCandidateSupport
{
    readonly MapScopedSurfaces _view;
    readonly MapPreparedSupport _support;
    readonly MapBoundFaceContext _context;
    readonly IReadOnlyList<MapFoliageSupportGrid> _grids;
    readonly MapSupportBinding _binding;
    readonly MapSpaceDoc? _space;
    readonly string? _owner;
    readonly bool _prisms;

    MapFoliageCandidateSupport(MapScopedSurfaces view, MapPreparedSupport support, MapBoundFaceContext context,
        IReadOnlyList<MapFoliageSupportGrid> grids, MapSupportBinding binding, MapSpaceDoc? space)
    {
        _view = view;
        _support = support;
        _context = context;
        _grids = grids;
        _binding = binding;
        _space = space;
        _owner = space is null ? null : MapSpaceGeometry.Owner(view, space);
        _prisms = support.Records.OfType<MapPrismDomain>().Any();
    }

    /// <summary>Prepares the shared context for every candidate in the chunk rectangle. Refuses Unavailable once when a
    /// Space binding's space is absent from the acquisition, since a scoped absence never proves a known outside.</summary>
    internal static MapFoliageCandidateSupport Create(MapScopedSurfaces view, MapPreparedSupport support, IReadOnlyList<MapFoliageSupportGrid> grids,
        MapSupportBinding binding, MapExactXz min, MapExactXz max, long maxFaces, out long faces)
    {
        MapSpaceDoc? space = null;
        if (binding.Kind == MapSupportBindingKind.Space &&
            (space = support.Records.OfType<MapSpaceDoc>().FirstOrDefault(s => s.Id == binding.SpaceId)) is null)
            throw new MapFoliageRefusal(MapFoliageStatus.Unavailable, MapFoliageSupport.NoWork,
                $"missing geometry: space '{binding.SpaceId}' is not in the chunk acquisition");
        MapBoundFaceContext context = Context(view, support, min, max, maxFaces, out faces);
        return new(view, support, context, grids, binding, space);
    }

    // Every present physical and indoor cell of every acquired support, ceiling and opening surface over the chunk, each
    // acquired patch there validated even when its cells there are absent, every opening cell there, and the source cells
    // of every acquired portal band segment.
    static MapBoundFaceContext Context(MapScopedSurfaces view, MapPreparedSupport support, MapExactXz min, MapExactXz max, long maxFaces, out long faces)
    {
        var limits = new MapRefinementLimits(MaxContextPatches: view.Scope.Limits.MaxCandidatePatches);
        var bounds = new MapSpacePointBounds(view, limits, null);
        var demands = new List<MapBoundFaceDemand>();
        foreach (MapSurfaceRef surface in view.Surfaces.Where(s => s.Role != MapSurfaceRole.PaintOverride))
        {
            MapCellRect range = MapEffectiveGeometry.CellRange(view, surface.Id, min, max);
            for (long z = checked(range.MinZ - 1); z <= range.MaxZExclusive; z++)
                for (long x = checked(range.MinX - 1); x <= range.MaxXExclusive; x++)
                {
                    MapPatchKey key = MapPatchKey.ForCell(surface.Id, x, z);
                    if (!view.TryAcquiredPatch(key, out MapSurfacePatch? patch, out _) || patch is null) continue;
                    int slot = MapLowerCellClassifier.SlotCell(x, z);
                    int lx = slot % MapPatchKey.SlotCells - patch.CellMinX, lz = slot / MapPatchKey.SlotCells - patch.CellMinZ;
                    if (lx < 0 || lx >= patch.Width || lz < 0 || lz >= patch.Depth) continue;
                    if (bounds.Preparation.ValidatedPatch(surface, patch) is null)
                        throw new MapFoliageRefusal(MapFoliageStatus.CapacityExceeded, MapFoliageSupport.NoWork, bounds.Preparation.Refusal ?? "context patches");
                    if (patch.IsPresent(lx, lz)) demands.Add(new(new(key, slot), null));
                }
        }
        foreach (MapHorizontalOpening opening in support.Records.OfType<MapHorizontalOpening>())
        {
            MapRecordRef reference = view.Witness.Records.First(r => r.Id == opening.Id);
            MapCellRect range = MapEffectiveGeometry.CellRange(view, opening.Patch.SurfaceId, min, max);
            foreach (int slot in opening.SlotCells)
            {
                long x = checked(opening.Patch.SlotX * MapPatchKey.SlotCells + slot % MapPatchKey.SlotCells);
                long z = checked(opening.Patch.SlotZ * MapPatchKey.SlotCells + slot / MapPatchKey.SlotCells);
                if (x >= range.MinX - 1 && x <= range.MaxXExclusive && z >= range.MinZ - 1 && z <= range.MaxZExclusive)
                    demands.Add(new(new(opening.Patch, slot), reference));
            }
        }
        foreach (MapCavePortal portal in support.Records.OfType<MapCavePortal>())
            foreach (MapRecordRef band in portal.BandTop is { } top ? new[] { portal.BandBottom, top } : new[] { portal.BandBottom })
            {
                // A band whose chain or source patch is not acquired is left to the point's own refusal.
                if (!view.TryRecord(band, out MapTopologyRecord? record, out _, borrow: true) || record is not MapBoundaryChain chain) continue;
                for (int i = 0; i + 1 < chain.Vertices.Count; i++)
                {
                    MapBoundaryChain segment = chain with { Vertices = Array.AsReadOnly(new[] { chain.Vertices[i], chain.Vertices[i + 1] }) };
                    try { demands.AddRange(bounds.SourceCells(segment).Select(cell => new MapBoundFaceDemand(cell, null))); }
                    catch (MapDocumentException) { }
                }
            }
        var work = new MapBoundFaceWork();
        MapBoundFaceContext? context = MapBoundFaceContext.PrepareBounds(view, demands, limits.MaxContextPatches, maxFaces, work,
            out string? refusal, bounds.Preparation);
        faces = work.CompiledFaces;
        return context ?? throw new MapFoliageRefusal(MapFoliageStatus.CapacityExceeded, MapFoliageSupport.NoWork, refusal ?? "context faces");
    }

    /// <summary>A Space binding at an exact point.</summary>
    internal MapFoliageCandidateOutcome Space(MapExactXz point)
    {
        MapExactValue y = MapExactValue.FromSingle(_binding.ReferenceY!.Value);
        MapExactValue low = y.Subtract(MapExactValue.FromSingle(_binding.SearchBelow!.Value));
        MapExactValue high = y.Add(MapExactValue.FromSingle(_binding.SearchAbove!.Value));
        var world = new MapExactPoint(point.X, y, point.Z);
        // The released order: membership at ReferenceY, then the intersections' capacity, then the membership status.
        MapMembershipResult membership = _support.Membership.QueryExact(world, _context);
        var intersections = Intersections(point, _grids, out MapFoliageStatus? capacity);
        if (capacity is { } refusal) return Refused(refusal);
        if (membership.Status is not (MapMembershipStatus.Resolved or MapMembershipStatus.Outside)) return Refused(membership.Status);
        MapSpaceFootprint[] footprints = Footprints(point);
        string owner = _owner!;
        Dictionary<string, MapRecordRef?> reachable = MapSupportSelection.Reachable(_view, _support.Membership, owner, footprints, _support.Records, point);
        IReadOnlyList<MapSpaceBoundsPart> occupied = Array.Empty<MapSpaceBoundsPart>();
        if (_space!.Kind == MapSpaceKind.Room)
        {
            MapMembershipResult column = _support.Membership.Column(MapEffectiveGeometry.Presentation(_view, world), world, _context, out occupied);
            if (column.Status is not (MapMembershipStatus.Resolved or MapMembershipStatus.Outside)) return Refused(column.Status);
        }
        var eligible = new List<(MapSupportSelection.Candidate Candidate, int Index)>();
        for (int i = 0; i < intersections.Count; i++)
        {
            var (_, _, _, _, face, height) = intersections[i];
            if (_space.Kind == MapSpaceKind.Room && occupied.Any(p => p.Owner == owner &&
                height.CompareTo(p.Lower) >= 0 && (p.Upper is not { } top || height.CompareTo(top) < 0)))
            {
                MapMembershipResult sample = _support.Membership.QueryExact(new(point.X, height, point.Z), _context);
                if (sample.Status != MapMembershipStatus.Resolved) return Refused(sample.Status);
                if (sample.SpaceId == owner && Band(height, low, high) && face.Normal.Y > 0) eligible.Add((new(face, height, owner, null), i));
                continue;
            }
            foreach (MapSpaceFootprint footprint in footprints)
            {
                if (footprint.Lower.Kind is not (MapBoundKind.SupportFloor or MapBoundKind.LegacyExteriorV1) ||
                    footprint.Lower.SurfaceId != face.Key.OwnerId) continue;
                string candidateOwner = SpaceOwner(footprint);
                if (!reachable.TryGetValue(candidateOwner, out MapRecordRef? via)) continue;
                if (!Band(height, low, high) || face.Normal.Y <= 0) continue;
                eligible.Add((new(face, height, candidateOwner, via), i));
                break;
            }
        }
        eligible.Sort((a, b) =>
        {
            int order = b.Candidate.Height.CompareTo(a.Candidate.Height);
            return order != 0 ? order : a.Candidate.Face.Key.CompareTo(b.Candidate.Face.Key);
        });
        if (eligible.Count != 0)
        {
            var highest = eligible.TakeWhile(c => c.Candidate.Height == eligible[0].Candidate.Height).ToArray();
            if (!MapSupportSelection.OneSeamOwner(_view, highest.Select(c => c.Candidate).ToArray(), _support.Seams, point))
                return Refused(MapFoliageStatus.Ambiguous);
            var selected = intersections[highest[0].Index];
            return new(MapFoliageStatus.Complete, selected.Grid, selected.Cell, selected.X, selected.Z, selected.Face.Key.OwnerId,
                highest[0].Candidate.SpaceId);
        }
        // The released fallback: membership at ReferenceY names the legacy lower cell and the bound space.
        if (membership.LowerCompatibility is { } tag && membership.SpaceId == owner)
        {
            MapExactValue height = MapEffectiveGeometry.LegacyHeight(_view, _context.ValidatedPatch(tag.Patch), tag.SlotCell, point);
            if (Band(height, low, high))
            {
                long cx = checked(tag.Patch.SlotX * MapPatchKey.SlotCells + tag.SlotCell % MapPatchKey.SlotCells);
                long cz = checked(tag.Patch.SlotZ * MapPatchKey.SlotCells + tag.SlotCell / MapPatchKey.SlotCells);
                MapFoliageSupportGrid? grid = _grids.FirstOrDefault(g => g.Surface.Id == tag.Patch.SurfaceId);
                MapFoliageSupportCell? cell = grid?.Cells.GetValueOrDefault((cx, cz));
                if (cell is null) return Refused(MapFoliageStatus.Unavailable);
                string? support = MapFoliageSupportIdentity.From(new MapSupportResult(MapSupportStatus.LegacyFallback, null, height.ToSingle(),
                    null, tag, owner, null, null, _view.ReadWitness));
                return new(MapFoliageStatus.Complete, grid, cell, cx, cz, support, owner);
            }
        }
        // No eligible support: inside the bound space's occupied column it refuses, outside it there is no sample.
        if (_space.Kind != MapSpaceKind.Room)
        {
            MapMembershipResult column = _support.Membership.Column(MapEffectiveGeometry.Presentation(_view, world), world, _context, out occupied);
            if (column.Status is not (MapMembershipStatus.Resolved or MapMembershipStatus.Outside)) return Refused(column.Status);
        }
        return occupied.Any(p => p.Owner == owner) ? Refused(MapFoliageStatus.MissingSupport)
            : new(MapFoliageStatus.Complete, null, null, 0, 0, null, null);
    }

    /// <summary>A Surface binding's occupied space at an exact point, as named-surface selection resolves it: membership at
    /// the binding's reference height must resolve, then a physical face takes the footprint owner of its surface, or with
    /// rooms present the membership at the face height. A logical fallback cell takes the membership at its legacy
    /// height.</summary>
    internal (MapFoliageStatus Status, string? SpaceId) Occupied(MapExactXz point, MapFoliageSupportGrid grid, MapFoliageSupportCell cell, float legacyHeight)
    {
        MapExactPoint world = new(point.X, MapExactValue.FromSingle(_binding.ReferenceY ?? 0), point.Z);
        MapMembershipResult membership = _support.Membership.QueryExact(world, _context);
        var hit = Intersections(point, [grid], out MapFoliageStatus? capacity).FirstOrDefault(i => ReferenceEquals(i.Cell, cell));
        if (capacity is { } refusal) return (refusal, null);
        if (membership.Status is not (MapMembershipStatus.Resolved or MapMembershipStatus.Outside)) return (Map(membership.Status), null);
        if (hit.Cell is null)
        {
            MapMembershipResult logical = _support.Membership.QueryExact(new(point.X, MapExactValue.FromSingle(legacyHeight), point.Z), _context);
            return logical.Status switch
            {
                MapMembershipStatus.Resolved => (MapFoliageStatus.Complete, logical.SpaceId),
                MapMembershipStatus.Outside => (MapFoliageStatus.Complete, null),
                _ => (Map(logical.Status), null),
            };
        }
        string surfaceId = grid.Surface.Id;
        MapSpaceFootprint? footprint = Footprints(point).FirstOrDefault(f => f.Lower.SurfaceId == surfaceId);
        string? owner = footprint is null ? null : SpaceOwner(footprint);
        if (_prisms)
        {
            MapMembershipResult occupied = _support.Membership.QueryExact(new(point.X, hit.Height, point.Z), _context);
            if (occupied.Status is not (MapMembershipStatus.Resolved or MapMembershipStatus.Outside)) return (Map(occupied.Status), null);
            owner = occupied.SpaceId;
        }
        return (MapFoliageStatus.Complete, owner);
    }

    // Per support floor in ordinal order, each cell at the exact point and the lowest-keyed context face holding it.
    List<(MapFoliageSupportGrid Grid, MapFoliageSupportCell? Cell, long X, long Z, MapCompiledFace Face, MapExactValue Height)> Intersections(
        MapExactXz point, IReadOnlyList<MapFoliageSupportGrid> grids, out MapFoliageStatus? refusal)
    {
        refusal = null;
        var result = new List<(MapFoliageSupportGrid, MapFoliageSupportCell?, long, long, MapCompiledFace, MapExactValue)>();
        foreach (MapFoliageSupportGrid grid in grids)
            foreach (var (x, z) in MapEffectiveGeometry.CellCandidates(_view, grid.Surface.Id, point))
            {
                if (!grid.Cells.TryGetValue((x, z), out MapFoliageSupportCell? cell) || cell.Fallback) continue;
                var demand = new MapCellDemand(cell.Key, cell.SlotCell);
                MapCompiledPatch compiled = _context.Compiled(cell.Key);
                (MapCompiledFace Face, MapExactValue Height)? selected = null;
                foreach (MapBoundFace face in _context.Faces(demand))
                {
                    if (MapSubdividedTriangle.Height(face.Triangle, point.X, point.Z) is not { } height) continue;
                    if (selected is null || face.Key.CompareTo(selected.Value.Face.Key) < 0)
                        selected = (compiled.Faces.First(f => f.Key == face.Key), height);
                }
                if (selected is not { } hit) continue;
                if (result.Count >= _view.Scope.Limits.MaxSupportIntersections) { refusal = MapFoliageStatus.CapacityExceeded; return result; }
                result.Add((grid, cell, x, z, hit.Face, hit.Height));
            }
        return result;
    }

    MapSpaceFootprint[] Footprints(MapExactXz point) =>
        _support.Footprints.Where(f => MapSpaceGeometry.FootprintCell(_view, f, point) is not null).ToArray();

    string SpaceOwner(MapSpaceFootprint footprint) => MapSpaceGeometry.Owner(_view, MapSpaceGeometry.Record<MapSpaceDoc>(_view, footprint.Space));

    static bool Band(MapExactValue height, MapExactValue low, MapExactValue high) => height.CompareTo(low) >= 0 && height.CompareTo(high) <= 0;

    static MapFoliageStatus Map(MapMembershipStatus status) => status switch
    {
        MapMembershipStatus.MissingGeometry => MapFoliageStatus.Unavailable,
        MapMembershipStatus.Ambiguous => MapFoliageStatus.Ambiguous,
        MapMembershipStatus.CapacityExceeded => MapFoliageStatus.CapacityExceeded,
        MapMembershipStatus.NotRepresentable => MapFoliageStatus.NotRepresentable,
        _ => MapFoliageStatus.Invalid,
    };

    static MapFoliageCandidateOutcome Refused(MapMembershipStatus status) => Refused(Map(status));
    static MapFoliageCandidateOutcome Refused(MapFoliageStatus status) => new(status, null, null, 0, 0, null, null);
}
