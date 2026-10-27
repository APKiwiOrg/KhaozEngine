using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Support;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Scene;

namespace KhaozEngine.MapDoc.Foliage;

/// <summary>A binding's support decision at one candidate. Space is the occupied space, never the support identity.</summary>
internal readonly record struct MapFoliageCandidateOutcome(MapFoliageStatus Status, MapFoliageSupportGrid? Grid, MapFoliageSupportCell? Cell,
    long CellX, long CellZ, string? OwnerId, string? SpaceId);

/// <summary>Decides support and occupied space per candidate over one prepared chunk context, with no context prepared
/// per candidate and no copied selection rule. Every support, bound, portal source and indoor face a candidate in the
/// chunk can demand is compiled once into a shared <see cref="MapBoundFaceContext"/>. Each candidate then runs the one
/// released selection kernel over that context, which keeps the point's own inspected-face and intersection budgets, so
/// membership, parent clipping, portal bands, missing bounds, ambiguity, legacy fallback and every refusal are the released
/// ones at that exact point. A Space NoSupport is MissingSupport inside the bound space's released membership column and
/// has no sample outside it.</summary>
internal sealed class MapFoliageCandidateSupport
{
    readonly MapScopedSurfaces _view;
    readonly MapPreparedSupport _support;
    readonly MapBoundFaceContext _context;
    readonly Dictionary<string, MapFoliageSupportGrid> _grids;
    readonly MapSupportBinding _binding;
    readonly string? _owner;

    MapFoliageCandidateSupport(MapScopedSurfaces view, MapPreparedSupport support, MapBoundFaceContext context,
        IReadOnlyList<MapFoliageSupportGrid> grids, MapSupportBinding binding, MapSpaceDoc? space)
    {
        _view = view;
        _support = support;
        _context = context;
        _grids = grids.ToDictionary(g => g.Surface.Id, StringComparer.Ordinal);
        _binding = binding;
        _owner = space is null ? null : MapSpaceGeometry.Owner(view, space);
    }

    internal MapBoundFaceContext BoundContext => _context;

    /// <summary>Prepares the shared context for every candidate in the chunk rectangle. Refuses Unavailable once when a
    /// Space binding's space is absent from the acquisition, since a scoped absence never proves a known outside.</summary>
    internal static MapFoliageCandidateSupport Create(MapScopedSurfaces view, MapPreparedSupport support, IReadOnlyList<MapFoliageSupportGrid> grids,
        MapSupportBinding binding, MapExactXz min, MapExactXz max, long maxFaces, out long faces,
        Func<MapValidatedSurfacePatch, MapSlotCellMask, MapCompiledPatch>? compile = null, Action<long>? chargeFaces = null,
        MapCapacityDemandOrigin? faceOrigin = null)
    {
        MapSpaceDoc? space = null;
        if (binding.Kind == MapSupportBindingKind.Space &&
            (space = support.Records.OfType<MapSpaceDoc>().FirstOrDefault(s => s.Id == binding.SpaceId)) is null)
            throw new MapFoliageRefusal(MapFoliageStatus.Unavailable, MapFoliageSupport.NoWork,
                $"missing geometry: space '{binding.SpaceId}' is not in the chunk acquisition");
        MapBoundFaceContext context = Context(view, support, min, max, maxFaces, out faces, compile, chargeFaces, faceOrigin: faceOrigin);
        return new(view, support, context, grids, binding, space);
    }

    // Every cell of every acquired support, ceiling and indoor patch over the chunk, each such patch validated, every
    // opening cell there, and the source cells of every acquired portal band segment.
    internal static MapBoundFaceContext Context(MapScopedSurfaces view, MapPreparedSupport support, MapExactXz min, MapExactXz max, long maxFaces, out long faces,
        Func<MapValidatedSurfacePatch, MapSlotCellMask, MapCompiledPatch>? compile = null, Action<long>? chargeFaces = null,
        Action? inspectCell = null, MapCapacityDemandOrigin? faceOrigin = null)
    {
        // This is aggregate admission over a subset of the acquisition's at most 256 patches. It cannot lower a
        // point's released patch allowance, and the supplied-context query still checks that point's face budget.
        var limits = new MapRefinementLimits(MaxContextPatches: view.Scope.Limits.MaxCandidatePatches);
        var bounds = new MapSpacePointBounds(view, limits, null);
        // The foliage context's patch ceiling is the acquisition's candidate patch allowance, never a refinement profile.
        if (KhaozEngine.Diagnostics.CapacityDemandDiagnostics.IsObserved)
            MapDemandOrigins.Derive(limits, nameof(MapRefinementLimits.MaxContextPatches),
                MapDemandOrigins.Of(view.Scope.Limits, nameof(MapQueryLimits.MaxCandidatePatches), view.Scope.Limits.MaxCandidatePatches));
        // Stream demands into PrepareBounds. Its face admission precedes retained masks and compiled arrays, and no
        // chunk-wide demand list is allocated before that gate.
        IEnumerable<MapBoundFaceDemand> Demands()
        {
            foreach (MapSurfaceRef surface in Participating(view, support))
            {
                MapCellRect range = MapEffectiveGeometry.CellRange(view, surface.Id, min, max);
                foreach (var row in view.Witness.Present.Where(p => p.Key.SurfaceId == surface.Id))
                {
                    MapPatchKey key = row.Key;
                    if (!view.TryAcquiredPatch(key, out MapSurfacePatch? patch, out _) || patch is null) continue;
                    // Point selection also demands known empty cells outside a patch's authored rectangle within its
                    // acquired slot. Keep those zero-face answers, bounded by 4096 cells per acquired patch.
                    long px = checked(key.SlotX * MapPatchKey.SlotCells);
                    long pz = checked(key.SlotZ * MapPatchKey.SlotCells);
                    long x0 = Math.Max(checked(range.MinX - 1), px), z0 = Math.Max(checked(range.MinZ - 1), pz);
                    long x1 = Math.Min(range.MaxXExclusive, checked(px + MapPatchKey.SlotCells - 1));
                    long z1 = Math.Min(range.MaxZExclusive, checked(pz + MapPatchKey.SlotCells - 1));
                    if (x0 > x1 || z0 > z1) continue;
                    if (bounds.Preparation.ValidatedPatch(surface, patch) is null)
                        throw new MapFoliageRefusal(MapFoliageStatus.CapacityExceeded, MapFoliageSupport.NoWork, bounds.Preparation.Refusal ?? "context patches");
                    for (long z = z0; z <= z1; z++)
                        for (long x = x0; x <= x1; x++)
                        {
                            inspectCell?.Invoke();
                            yield return new(new(key, MapLowerCellClassifier.SlotCell(x, z)), null);
                        }
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
                        yield return new(new(opening.Patch, slot), reference);
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
                        MapCellDemand[] cells;
                        try { cells = bounds.SourceCells(segment).ToArray(); }
                        catch (MapDocumentException) { continue; }
                        foreach (var cell in cells) yield return new(cell, null);
                    }
                }
        }
        var work = new MapBoundFaceWork();
        if (compile is null && chargeFaces is not null)
            compile = (patch, mask) =>
            {
                chargeFaces(MapSurfaceCompiler.CountFaces(patch, mask, logical: false));
                return MapSurfaceCompiler.Compile(patch, mask, MapSurfaceCompiler.MaxFacesPerPatch);
            };
        MapBoundFaceContext? context = MapBoundFaceContext.PrepareBounds(view, Demands(), limits.MaxContextPatches, maxFaces, work,
            out string? refusal, bounds.Preparation, compile, chargeFaces,
            faceOrigin ?? new MapCapacityDemandOrigin("MaxFaces", 0, maxFaces));
        faces = work.CompiledFaces;
        return context ?? throw new MapFoliageRefusal(MapFoliageStatus.CapacityExceeded, MapFoliageSupport.NoWork, refusal ?? "context faces");
    }

    // All support floors can affect the released per-point intersection budget, even in named-surface mode.
    // A ceiling participates only as a bound or indoor span. Portal source demands are added separately below.
    static IEnumerable<MapSurfaceRef> Participating(MapScopedSurfaces view, MapPreparedSupport support)
    {
        var bounds = support.Footprints.SelectMany(f => new[] { f.Lower.SurfaceId, f.Upper.SurfaceId })
            .Where(id => id is not null).ToHashSet(StringComparer.Ordinal);
        foreach (MapSurfaceRef surface in view.Surfaces)
            if (surface.Role == MapSurfaceRole.SupportFloor || surface.IndoorSpan is not null || bounds.Contains(surface.Id))
                yield return surface;
    }

    /// <summary>A Space binding at an exact point.</summary>
    internal MapFoliageCandidateOutcome Space(MapExactXz point)
    {
        MapSupportResult result = Select(point);
        switch (result.Status)
        {
            case MapSupportStatus.Supported when result.Face is { Patch: { } patch } face:
                return Outcome(patch, face.Primitive, MapFoliageSupportIdentity.From(result), result.SpaceId);
            case MapSupportStatus.LegacyFallback when result.Compatibility is { } tag:
                return Outcome(tag.Patch, tag.SlotCell, MapFoliageSupportIdentity.From(result), result.SpaceId);
            case MapSupportStatus.NoSupport:
                // Inside the bound space's released membership column there is no support, outside it there is no sample.
                var world = new MapExactPoint(point.X, MapExactValue.FromSingle(_binding.ReferenceY!.Value), point.Z);
                MapMembershipResult column = _support.Membership.Column(MapEffectiveGeometry.Presentation(_view, world), world, _context,
                    out IReadOnlyList<MapSpaceBoundsPart> parts);
                if (column.Status is not (MapMembershipStatus.Resolved or MapMembershipStatus.Outside)) return Refused(Map(column.Status));
                foreach (MapSpaceBoundsPart part in parts)
                    if (part.Owner == _owner) return Refused(MapFoliageStatus.MissingSupport);
                return new(MapFoliageStatus.Complete, null, null, 0, 0, null, null);
            default:
                return Refused(MapFoliageSupport.Map(result.Status));
        }
    }

    /// <summary>A Surface binding's occupied space at an exact point, from the released named-surface selection: its space
    /// at the selected face. A logical fallback cell, which that selection never supports, takes the released membership
    /// at its legacy height.</summary>
    internal (MapFoliageStatus Status, string? SpaceId) Occupied(MapExactXz point, float legacyHeight)
    {
        MapSupportResult result = Select(point);
        if (result.Status == MapSupportStatus.Supported) return (MapFoliageStatus.Complete, result.SpaceId);
        if (result.Status != MapSupportStatus.NoSupport) return (MapFoliageSupport.Map(result.Status), null);
        MapMembershipResult logical = _support.Membership.QueryExact(new(point.X, MapExactValue.FromSingle(legacyHeight), point.Z), _context);
        return logical.Status switch
        {
            MapMembershipStatus.Resolved => (MapFoliageStatus.Complete, logical.SpaceId),
            MapMembershipStatus.Outside => (MapFoliageStatus.Complete, null),
            _ => (Map(logical.Status), null),
        };
    }

    internal MapSupportResult Target(MapSupportBinding binding, MapExactXz point) =>
        MapFoliageSupportIdentity.ForClearance(_view, binding, point, Select(binding, point));

    internal bool Indoor(string? space) => space is not null && _support.Records.OfType<MapSpaceDoc>()
        .Any(r => r.Kind == MapSpaceKind.Room && MapSpaceGeometry.Owner(_view, r) == space);

    // The binding's released request at the point, through the one selection kernel over the shared context.
    MapSupportResult Select(MapExactXz point) => Select(_binding, point);

    internal MapSupportResult Select(MapSupportBinding binding, MapExactXz point)
    {
        if (MapSupportSelection.BindingRequest(_view, binding, point) is not { } bound)
            return MapSupportSelection.Refusal(_view, MapSupportStatus.Invalid, "support binding lacks its kind's members");
        return MapSupportSelection.Compute(_support, bound.Request, null, bound.NamedSurfaceOnly, bound.World, _context).Result;
    }

    MapFoliageCandidateOutcome Outcome(MapPatchKey patch, int slotCell, string? owner, string? space)
    {
        long x = checked(patch.SlotX * MapPatchKey.SlotCells + slotCell % MapPatchKey.SlotCells);
        long z = checked(patch.SlotZ * MapPatchKey.SlotCells + slotCell / MapPatchKey.SlotCells);
        if (!_grids.TryGetValue(patch.SurfaceId, out MapFoliageSupportGrid? grid) || !grid.Cells.TryGetValue((x, z), out MapFoliageSupportCell? cell))
            return Refused(MapFoliageStatus.Unavailable);
        return new(MapFoliageStatus.Complete, grid, cell, x, z, owner, space);
    }

    static MapFoliageStatus Map(MapMembershipStatus status) => status switch
    {
        MapMembershipStatus.MissingGeometry => MapFoliageStatus.Unavailable,
        MapMembershipStatus.Ambiguous => MapFoliageStatus.Ambiguous,
        MapMembershipStatus.CapacityExceeded => MapFoliageStatus.CapacityExceeded,
        MapMembershipStatus.NotRepresentable => MapFoliageStatus.NotRepresentable,
        _ => MapFoliageStatus.Invalid,
    };

    static MapFoliageCandidateOutcome Refused(MapFoliageStatus status) => new(status, null, null, 0, 0, null, null);
}
