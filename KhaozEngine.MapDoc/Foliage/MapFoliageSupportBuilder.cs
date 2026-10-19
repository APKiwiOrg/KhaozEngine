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
    long _faces;
    MapPreparedSupport? _support;

    // Preparation runs no support query, so SurfaceQueries stays zero.
    internal MapFoliageWork Work() => new(0, 0, 1, _faces, 0, 0, 0, 0);

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

        MapSurfaceRef[] floors = view.Surfaces.Where(s => s.Role == MapSurfaceRole.SupportFloor && (bound is null || s.Id == bound))
            .OrderBy(s => s.Id, StringComparer.Ordinal).ToArray();
        // One reach widens both the support grids and the shared context, so every neighbour centre edge fade reads has
        // its prepared cell and its compiled faces. A layer without edge fade keeps the chunk rectangle.
        double reach = ContextReach(floors);
        (MapExactXz min, MapExactXz max) = Rectangle(reach);
        if (reach > 0)
        {
            MapExactRect acquired = view.Scope.WorldRectangle();
            if (min.X.CompareTo(acquired.MinX) < 0 || min.Z.CompareTo(acquired.MinZ) < 0 ||
                max.X.CompareTo(acquired.MaxX) > 0 || max.Z.CompareTo(acquired.MaxZ) > 0)
                throw new MapFoliageRefusal(MapFoliageStatus.Unavailable, Work(), "edge fade reach lies outside the chunk acquisition");
            _parts.Add("context-reach|" + BitConverter.DoubleToInt64Bits(reach).ToString(CultureInfo.InvariantCulture));
        }
        var grids = floors.Select(s => Grid(s, min, max)).ToArray();
        _support = MapSupportPreparation.PrepareFoliage(view);
        // A Surface binding owns every present cell of its surface, a logical fallback cell through its compatibility tag.
        if (binding.Kind == MapSupportBindingKind.Surface)
            foreach (MapFoliageSupportCell cell in grids.SelectMany(g => g.Cells.Values)) cell.Select(cell.Fallback ? FallbackOwner(cell) : bound!);
        // Support and membership are decided per candidate over one shared context, built for every binding so each
        // candidate keeps the released intersection budget over every support floor. Only invariant states refuse here:
        // the acquisition above and, for a Space binding, a bound space absent from it.
        MapFoliageCandidateSupport? candidates = null;
        try
        {
            candidates = MapFoliageCandidateSupport.Create(view, _support, grids, binding, min, max, limits.MaxFaces - _faces, out long faces);
            _faces = checked(_faces + faces);
        }
        catch (MapFoliageRefusal refusal) { throw new MapFoliageRefusal(refusal.Status, Work(), refusal.Message); }

        Describe(binding);
        var materials = new MapFoliageMaterials(layer, paint);
        return new(layer, chunk, MapFoliageStatus.Complete, null, Work(), _parts.Order(StringComparer.Ordinal).ToArray(), grids, materials, candidates);
    }

    // Clearance edge fade evaluates the four neighbour cell centres of a sample's support cell. A centre lies at most
    // 1.5 cells from the sample along one cell axis and 0.5 along the other, so its world offset on either axis is at
    // most 1.5 cells, or sqrt(1.5^2 + 0.5^2) < 1.6 cells under a yawed transform. The reach takes the largest effective
    // cell of the surfaces that become grids, since a centre from a coarse grid may land on a finer surface.
    double ContextReach(IEnumerable<MapSurfaceRef> floors)
    {
        if (layer.EdgeFade <= 0) return 0;
        double reach = 0;
        foreach (MapSurfaceRef surface in floors)
        {
            MapResourceTransform? kernel = Kernel(surface);
            double cell = surface.Frame.CellUnitMetres.Exact().ToDouble() * (kernel?.Transform.Scale ?? 1);
            reach = Math.Max(reach, (kernel is { Transform.YawRadians: not 0 } ? 1.6 : 1.5) * cell);
        }
        return reach;
    }

    MapResourceTransform? Kernel(MapSurfaceRef surface) => view.EffectiveContext?.Transform(MapPrefabChildKind.Surface, surface.Id);

    (MapExactXz Min, MapExactXz Max) Rectangle(double reach)
    {
        (double minX, double minZ, double maxX, double maxZ) = MapFoliageSupport.Rect(layer, chunk);
        minX -= reach; minZ -= reach; maxX += reach; maxZ += reach;
        return (new(MapExactValue.FromSingle(MapFoliageSupport.Outward(minX, lower: true)), MapExactValue.FromSingle(MapFoliageSupport.Outward(minZ, lower: true))),
            new(MapExactValue.FromSingle(MapFoliageSupport.Outward(maxX, lower: false)), MapExactValue.FromSingle(MapFoliageSupport.Outward(maxZ, lower: false))));
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

    MapFoliageSupportGrid Grid(MapSurfaceRef surface, MapExactXz min, MapExactXz max)
    {
        var grid = new MapFoliageSupportGrid(surface, Kernel(surface));
        MapCellRect range = MapEffectiveGeometry.CellRange(view, surface.Id, min, max);
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
            var faces = new MapFoliagePatchFaces(MapEffectiveGeometry.TransformPatch(view, local));
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

    string? FallbackOwner(MapFoliageSupportCell cell) => MapFoliageSupportIdentity.From(new MapSupportResult(MapSupportStatus.LegacyFallback,
        null, 0, null, new MapLegacyCellTag(cell.Key, cell.SlotCell, MapFoliageValidation.LegacyTilePolicy), null, null, null, view.ReadWitness));

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
