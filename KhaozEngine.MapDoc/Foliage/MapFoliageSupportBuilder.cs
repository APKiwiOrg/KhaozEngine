using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Support;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Foliage;

/// <summary>Builds one chunk preparation: acquisition checks, cell discovery, logical faces with world paint, released
/// corners and normals, then the binding's prepared selection. Every charge is counted before its allocation.</summary>
internal sealed class MapFoliageSupportBuilder(MapScopedSurfaces view, IReadOnlyDictionary<string, MapPaintRegionSet> paint,
    IReadOnlyList<MapSurfaceRef> declared, MapFoliageLayer layer, MapFoliageChunkKey chunk, MapFoliageLimits limits)
{
    readonly List<string> _parts = new();
    readonly Dictionary<MapExactXz, MapSupportResult> _queries = new();
    long _faces;
    long _queriesRun;
    MapPreparedSupport? _support;

    internal MapFoliageWork Work() => new(0, 0, 1, _faces, 0, 0, _queriesRun, 0);

    internal MapPreparedFoliageSupport Build()
    {
        Require(view.Status);
        MapSupportBinding binding = layer.Support;
        string? bound = binding.Kind == MapSupportBindingKind.Surface ? binding.SurfaceId : null;
        if (bound is not null && declared.FirstOrDefault(s => s.Id == bound) is not { Role: MapSurfaceRole.SupportFloor })
            throw new MapFoliageRefusal(MapFoliageStatus.MissingSupport, Work(), $"support surface '{bound}' is not an effective support floor");
        if (binding.Kind == MapSupportBindingKind.Space && (binding.SpaceId is null || binding.ReferenceY is null ||
            binding.SearchBelow is null || binding.SearchAbove is null))
            throw new MapFoliageRefusal(MapFoliageStatus.Invalid, Work(), "space binding lacks its reference height or search interval");

        var grids = view.Surfaces.Where(s => s.Role == MapSurfaceRole.SupportFloor && (bound is null || s.Id == bound))
            .OrderBy(s => s.Id, StringComparer.Ordinal).Select(Grid).ToArray();
        _support = MapSupportPreparation.PrepareFoliage(view);
        MapFoliageSpaceSelection? space = null;
        if (binding.Kind == MapSupportBindingKind.Surface) SelectSurface(grids, bound!);
        else
        {
            GateSpace(grids, binding);
            space = MapFoliageSpaceSelection.Create(view, _support, grids, binding);
        }

        Describe(binding);
        var materials = new MapFoliageMaterials(layer, paint);
        return new(layer, chunk, MapFoliageStatus.Complete, null, Work(), _parts.Order(StringComparer.Ordinal).ToArray(), grids, materials, space);
    }

    void Require(MapAcquireStatus status)
    {
        if (status == MapAcquireStatus.Complete) return;
        MapFoliageStatus mapped = status switch
        {
            MapAcquireStatus.Incomplete => MapFoliageStatus.Unavailable,
            MapAcquireStatus.CapacityExceeded => MapFoliageStatus.CapacityExceeded,
            _ => MapFoliageStatus.NotRepresentable,
        };
        throw new MapFoliageRefusal(mapped, Work(), view.Detail ?? "incomplete chunk acquisition");
    }

    MapFoliageSupportGrid Grid(MapSurfaceRef surface)
    {
        MapResourceTransform? kernel = view.EffectiveContext?.Transform(MapPrefabChildKind.Surface, surface.Id);
        var grid = new MapFoliageSupportGrid(surface, kernel);
        (double minX, double minZ, double maxX, double maxZ) = MapFoliageSupport.Rect(layer, chunk);
        MapCellRect range = MapEffectiveGeometry.CellRange(view, surface.Id,
            new(MapExactValue.FromSingle(MapFoliageSupport.Outward(minX, lower: true)), MapExactValue.FromSingle(MapFoliageSupport.Outward(minZ, lower: true))),
            new(MapExactValue.FromSingle(MapFoliageSupport.Outward(maxX, lower: false)), MapExactValue.FromSingle(MapFoliageSupport.Outward(maxZ, lower: false))));
        // One cell of margin on every side absorbs the released float floor at the rectangle's edges.
        long x0 = checked(range.MinX - 1), z0 = checked(range.MinZ - 1), x1 = checked(range.MaxXExclusive + 1), z1 = checked(range.MaxZExclusive + 1);
        if (((double)x1 - x0) * ((double)z1 - z0) > 4.0 * limits.MaxFaces + 64)
            throw new MapFoliageRefusal(MapFoliageStatus.CapacityExceeded, Work(), "support cells");
        var lattice = new MapFoliageLattice(view, surface, layer.Policy.NormalBoundaryHeight);
        var byPatch = new Dictionary<MapPatchKey, List<(long X, long Z)>>();
        for (long z = z0; z < z1; z++)
            for (long x = x0; x < x1; x++)
            {
                MapPatchKey key = MapPatchKey.ForCell(surface.Id, x, z);
                if (Patch(key) is not { } patch) continue;
                int lx = (int)(x - key.SlotX * MapPatchKey.SlotCells) - patch.CellMinX, lz = (int)(z - key.SlotZ * MapPatchKey.SlotCells) - patch.CellMinZ;
                if (lx < 0 || lx >= patch.Width || lz < 0 || lz >= patch.Depth || !patch.IsPresent(lx, lz)) continue;
                if (!byPatch.TryGetValue(key, out var cells)) byPatch.Add(key, cells = new());
                cells.Add((x, z));
            }
        foreach (var (key, cells) in byPatch.OrderBy(p => p.Key))
        {
            MapSurfacePatch patch = Patch(key)!;
            var validated = new MapValidatedSurfacePatch(surface, patch);
            var mask = MapSlotCellMask.Of(cells.Select(c => MapLowerCellClassifier.SlotCell(c.X, c.Z)));
            long count = MapFoliageTriangles.CountFaces(validated, mask);
            if (checked(_faces + count) > limits.MaxFaces)
                throw new MapFoliageRefusal(MapFoliageStatus.CapacityExceeded, Work(), "prepared faces");
            _faces += count;
            MapCompiledPatch local = MapFoliageTriangles.Compile(validated, mask, (int)count);
            local = MapFoliageMaterials.ApplyWorldPaint(view, local, MapFoliageMaterials.WorldPaint(view, surface.Id));
            var faces = new MapFoliagePatchFaces(MapEffectiveGeometry.TransformPatch(view, local), validated);
            foreach (var (x, z) in cells)
            {
                int lx = (int)(x - key.SlotX * MapPatchKey.SlotCells) - patch.CellMinX, lz = (int)(z - key.SlotZ * MapPatchKey.SlotCells) - patch.CellMinZ;
                MapSurfaceCell cell = patch.Cells[lz * patch.Width + lx];
                int sw = patch.Height(lx, lz), se = patch.Height(lx + 1, lz), nw = patch.Height(lx, lz + 1), ne = patch.Height(lx + 1, lz + 1);
                MapLatticeFrame frame = surface.Frame;
                var corners = new MapFoliageCellCorners(MapFoliageNormals.Metres(frame, sw), MapFoliageNormals.Metres(frame, se),
                    MapFoliageNormals.Metres(frame, nw), MapFoliageNormals.Metres(frame, ne),
                    lattice.Normal(x, z), lattice.Normal(checked(x + 1), z), lattice.Normal(x, checked(z + 1)), lattice.Normal(checked(x + 1), checked(z + 1)));
                grid.Cells.Add((x, z), new()
                {
                    Key = key,
                    SlotCell = MapLowerCellClassifier.SlotCell(x, z),
                    Cell = cell,
                    Split = MapSurfaceTopology.SplitSwNe(sw, se, nw, ne, cell.Cut, cell.Rotation, cell.Topology),
                    Fallback = MapSurfaceCompiler.Fallback(surface, cell),
                    Corners = corners,
                    Faces = faces,
                    Kernel = grid.Kernel,
                });
            }
        }
        foreach (var ((sx, sz), patch) in lattice.Slots.OrderBy(s => s.Key))
            _parts.Add("normal-slot|" + MapFoliagePatchFaces.Describe(new(surface.Id, sx, sz)) + "|" + (patch is null ? "absent" : MapSurfaceSemantics.PatchDigest(patch)));
        return grid;
    }

    MapSurfacePatch? Patch(MapPatchKey key)
    {
        if (view.TryAcquiredPatch(key, out MapSurfacePatch? patch, out MapPatchStatus status)) return patch;
        if (view.HasRead(key)) throw new MapFoliageRefusal(MapFoliageStatus.Unavailable, Work(), $"patch {key}: {status}");
        return null;
    }

    // A Surface binding samples its named owner in every cell. Without any space record the named selection is purely
    // geometric and needs no query, otherwise one named query per cell centre supplies the occupied space.
    void SelectSurface(MapFoliageSupportGrid[] grids, string surfaceId)
    {
        bool spaces = _support!.Records.OfType<MapSpaceDoc>().Any();
        foreach (MapFoliageSupportGrid grid in grids)
            foreach (var ((x, z), cell) in grid.Cells.OrderBy(c => c.Key.Z).ThenBy(c => c.Key.X))
            {
                if (!spaces) { cell.Select(cell.Fallback ? FallbackOwner(cell) : surfaceId, null); continue; }
                MapExactXz centre = Centre(grid.Surface, x, z);
                MapSupportResult result = Query(layer.Support, centre);
                if (result.Status == MapSupportStatus.Supported && result.Face is { } face && face.OwnerId == surfaceId &&
                    face.Patch == cell.Key && face.Primitive == cell.SlotCell)
                    cell.Select(MapFoliageSupportIdentity.From(result), result.SpaceId);
                else if (result.Status == MapSupportStatus.NoSupport && cell.Fallback)
                    cell.Select(FallbackOwner(cell), Occupied(centre, cell));
                else if (result.Status != MapSupportStatus.NoSupport)
                    cell.Refuse(MapFoliageSupport.Map(result.Status));
            }
    }

    // A Space binding is decided per candidate by MapFoliageSpaceSelection. Preparation only gates hard refusals: one
    // selection per distinct cell centre across every support floor, and a cell whose selection meets missing geometry,
    // capacity, representability or invalid input refuses. Support, eligibility, ambiguity and the domain are never
    // decided here.
    void GateSpace(MapFoliageSupportGrid[] grids, MapSupportBinding binding)
    {
        foreach (MapFoliageSupportGrid grid in grids)
            foreach (var ((x, z), cell) in grid.Cells.OrderBy(c => c.Key.Z).ThenBy(c => c.Key.X))
            {
                MapSupportResult result = Query(binding, Centre(grid.Surface, x, z));
                if (result.Status is MapSupportStatus.MissingGeometry or MapSupportStatus.CapacityExceeded or
                    MapSupportStatus.NotRepresentable or MapSupportStatus.Invalid) cell.Refuse(MapFoliageSupport.Map(result.Status));
            }
    }

    MapSupportResult Query(MapSupportBinding binding, MapExactXz centre)
    {
        if (_queries.TryGetValue(centre, out MapSupportResult? cached)) return cached;
        _queriesRun++;
        MapSupportResult result = _support!.SampleBinding(binding, centre);
        _queries.Add(centre, result);
        return result;
    }

    // A logical fallback cell has no physical face, so its occupied space is the membership at its legacy ground height.
    string? Occupied(MapExactXz centre, MapFoliageSupportCell cell)
    {
        _queriesRun++;
        float height = MapFoliageNormals.Ground(cell.Corners, cell.Cell, cell.Split, 0.5f, 0.5f).Height;
        if (cell.Kernel is { } kernel) height = kernel.Height(MapExactValue.FromSingle(height)).ToSingle();
        MapMembershipResult membership = _support!.Membership.QueryExact(new(centre.X, MapExactValue.FromSingle(height), centre.Z));
        return membership.Status == MapMembershipStatus.Resolved ? membership.SpaceId : null;
    }

    string? FallbackOwner(MapFoliageSupportCell cell) => MapFoliageSupportIdentity.From(new MapSupportResult(MapSupportStatus.LegacyFallback,
        null, 0, null, new MapLegacyCellTag(cell.Key, cell.SlotCell, MapFoliageValidation.LegacyTilePolicy), null, null, null, view.ReadWitness));

    MapExactXz Centre(MapSurfaceRef surface, long x, long z) => MapEffectiveGeometry.Vertex(view,
        new MapLatticeVertex(surface.Id, MapLatticeAddress.Create(checked(2 * x + 1), checked(2 * z + 1), 2)));

    void Describe(MapSupportBinding binding)
    {
        string F(float value) => BitConverter.SingleToInt32Bits(value).ToString(CultureInfo.InvariantCulture);
        string N(float? value) => value is { } v ? F(v) : "null";
        _parts.Add($"policy|{layer.Policy.Id}|{layer.Policy.Version.ToString(CultureInfo.InvariantCulture)}|{F(layer.Policy.NormalBoundaryHeight)}");
        _parts.Add($"binding|{binding.Kind}|{binding.SurfaceId}|{binding.SpaceId}|{N(binding.ReferenceY)}|{N(binding.SearchBelow)}|{N(binding.SearchAbove)}");
        _parts.Add($"raster|{F(layer.OriginX)}|{F(layer.OriginZ)}|{F(layer.CellSize)}|{layer.Width.ToString(CultureInfo.InvariantCulture)}|{layer.Depth.ToString(CultureInfo.InvariantCulture)}");
        var digests = layer.ChunkDigests.ToDictionary(d => d.Key, d => d.Sha256);
        // Bilinear reads reach one sample into the next chunk on each axis.
        for (int dz = 0; dz <= 1; dz++)
            for (int dx = 0; dx <= 1; dx++)
            {
                var key = new MapFoliageChunkKey(chunk.X + dx, chunk.Z + dz);
                _parts.Add(string.Create(CultureInfo.InvariantCulture, $"density|{key.X}|{key.Z}|{(digests.TryGetValue(key, out string? sha) ? sha : "absent")}"));
            }
        _parts.Add("allowed|" + string.Join(",", layer.AllowedVisibleMaterials.Select(m => m.ToString(CultureInfo.InvariantCulture))) +
            "|indoors|" + layer.ExcludeIndoors.ToString(CultureInfo.InvariantCulture));
        foreach (MapFoliageMaterial material in layer.Materials)
            _parts.Add($"material|{material.Id.ToString(CultureInfo.InvariantCulture)}|{material.ResourceId}|{material.Kind}");
        foreach (var (key, digest) in view.Witness.Present) _parts.Add("patch|" + MapFoliagePatchFaces.Describe(key) + "|" + digest);
        foreach (MapCoveredRange range in view.Witness.KnownEmpty)
            _parts.Add(string.Create(CultureInfo.InvariantCulture,
                $"empty|{range.SurfaceId}|{range.Slots.MinX}|{range.Slots.MinZ}|{range.Slots.MaxXExclusive}|{range.Slots.MaxZExclusive}"));
        foreach (MapRecordRef record in view.Witness.Records) _parts.Add("record|" + record.Id + "|" + MapFoliagePatchFaces.Describe(record.Anchor));
        foreach (var (target, set) in paint) _parts.Add("paint|" + target + "|" + set.Digest);
        // Surface metadata and effective poses decide the lattice, world paint precedence and every transformed point.
        foreach (MapSurfaceRef surface in view.Surfaces)
        {
            MapLatticeFrame frame = surface.Frame;
            _parts.Add(string.Create(CultureInfo.InvariantCulture,
                $"surface|{surface.Id}|{surface.Role}|{surface.PresencePolicy}|{surface.PaintTargetSurfaceId}|{surface.PaintPriority}|" +
                $"{frame.CellUnitMetres.Numerator}/{frame.CellUnitMetres.Denominator}|{frame.HeightUnitMetres.Numerator}/{frame.HeightUnitMetres.Denominator}|" +
                $"{frame.RowDirection}|{frame.Datum}"));
        }
        if (view.EffectiveContext is { } context)
        {
            var owners = view.Surfaces.Select(s => MapEffectiveContext.Key(MapPrefabChildKind.Surface, s.Id))
                .Concat(view.Witness.Records.Select(r => MapEffectiveContext.Key(MapPrefabChildKind.Record, r.Id))).ToHashSet(StringComparer.Ordinal);
            foreach (var (key, pose) in context.Transforms.Where(t => owners.Contains(t.Key)))
                _parts.Add(string.Create(CultureInfo.InvariantCulture, $"transform|{key}|{BitConverter.DoubleToInt64Bits(pose.X)}|" +
                    $"{BitConverter.DoubleToInt64Bits(pose.Y)}|{BitConverter.DoubleToInt64Bits(pose.Z)}|" +
                    $"{BitConverter.DoubleToInt64Bits(pose.YawRadians)}|{BitConverter.DoubleToInt64Bits(pose.Scale)}"));
        }
    }
}
