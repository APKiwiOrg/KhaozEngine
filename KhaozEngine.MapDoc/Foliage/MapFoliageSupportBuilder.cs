using KhaozEngine.Diagnostics;
using DemandDiagnostics = KhaozEngine.Diagnostics.CapacityDemandDiagnostics;
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
    IReadOnlyList<MapSurfaceRef> declared, MapFoliageLayer layer, MapFoliageChunkKey chunk, MapFoliageLimits limits,
    IReadOnlyDictionary<MapFoliageChunkKey, string>? densityDigests)
{
    readonly List<string> _parts = new();
    long _faces;
    MapPreparedSupport? _support;
    readonly Dictionary<MapPatchKey, (MapCompiledPatch Patch, MapSlotCellMask Mask)> _logical = new();
    long _contextFaces;

    // Preparation runs no support query, so SurfaceQueries stays zero.
    internal MapFoliageWork Work() => new(0, 0, 1, checked(_faces + _contextFaces), 0, 0, 0, 0);

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
        // One reach widens the acquisition, the support grids and the shared context, so every cell centre and neighbour
        // centre clearance reads has its prepared cell, its normal halo and its compiled faces.
        double reach = MapFoliageSupport.ReadReach(layer, floors.Select(s => (s, Kernel(s)?.Transform)));
        (MapExactXz min, MapExactXz max) = Rectangle(reach);
        if (reach > 0)
        {
            MapExactRect acquired = view.Scope.WorldRectangle();
            if (min.X.CompareTo(acquired.MinX) < 0 || min.Z.CompareTo(acquired.MinZ) < 0 ||
                max.X.CompareTo(acquired.MaxX) > 0 || max.Z.CompareTo(acquired.MaxZ) > 0)
                throw new MapFoliageRefusal(MapFoliageStatus.Unavailable, Work(), "clearance read reach lies outside the chunk acquisition");
            Part("context-reach|" + BitConverter.DoubleToInt64Bits(reach).ToString(CultureInfo.InvariantCulture));
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
            long consumed = _faces - PhysicalFaces();
            candidates = MapFoliageCandidateSupport.Create(view, _support, grids, binding, min, max, limits.MaxFaces - consumed,
                out _, ReusePhysical, RetainContextFaces, new("MaxFaces", consumed, MapFoliageDemandOrigins.Profile(limits).MaxFaces, limits));
        }
        catch (MapFoliageRefusal refusal) { throw new MapFoliageRefusal(refusal.Status, Work(), refusal.Message); }

        Describe(binding);
        var materials = new MapFoliageMaterials(layer, paint);
        return new(layer, chunk, MapFoliageStatus.Complete, null, Work(), _parts.Order(StringComparer.Ordinal).ToArray(), grids, materials, candidates);
    }

    void Part(string value)
    {
        limits.ChargePreparation?.Invoke(checked(128 + 2L * System.Text.Encoding.UTF8.GetByteCount(value)));
        _parts.Add(value);
    }

    void RetainContextFaces(long count)
    {
        limits.ChargePreparation?.Invoke(checked(1280 * count));
        _contextFaces = checked(_contextFaces + count);
    }

    long PhysicalFaces() => _logical.Sum(pair =>
    {
        Func<int, bool> fallback = Fallback(pair.Key);
        return pair.Value.Patch.Faces.LongCount(f => !fallback(f.Key.Primitive));
    });

    // Looks the patch and its surface up once, then tests slot cells of that patch.
    Func<int, bool> Fallback(MapPatchKey key)
    {
        MapSurfacePatch patch = Patch(key)!;
        MapSurfaceRef surface = view.Surfaces.First(s => s.Id == key.SurfaceId);
        return slot => MapSurfaceCompiler.Fallback(surface, patch.Cells[(slot / 64 - patch.CellMinZ) * patch.Width + slot % 64 - patch.CellMinX]);
    }

    // Reuse the logical triangulation, filtering fallback cells from physical membership. Portal demands outside a
    // grid compile only their additional cells. Existing exact coordinates, face keys and normals remain unchanged, and
    // the fallback cell list is the one a physical compile of the same mask emits.
    MapCompiledPatch ReusePhysical(MapValidatedSurfacePatch validated, MapSlotCellMask mask)
    {
        if (!_logical.TryGetValue(validated.Patch.Key, out var logical))
        {
            RetainContextFaces(MapSurfaceCompiler.CountFaces(validated, mask, logical: false));
            return MapSurfaceCompiler.Compile(validated, mask, MapSurfaceCompiler.MaxFacesPerPatch);
        }
        MapCompiledPatch cached = logical.Patch;
        Func<int, bool> fallback = Fallback(cached.Key);
        var faces = cached.Faces.Where(f => mask.Contains(f.Key.Primitive) && !fallback(f.Key.Primitive)).ToArray();
        MapSlotCellMask missing = MapSlotCellMask.Of(Enumerable.Range(0, 4096).Where(s => mask.Contains(s) && !logical.Mask.Contains(s)));
        if (missing.Count != 0)
            RetainContextFaces(MapSurfaceCompiler.CountFaces(validated, missing, logical: false));
        MapCompiledPatch? extra = missing.Count == 0 ? null : MapSurfaceCompiler.Compile(validated, missing, MapSurfaceCompiler.MaxFacesPerPatch);
        int offset = cached.VertexIds.Count;
        var additional = extra?.Faces.Select(f => f with { A = f.A + offset, B = f.B + offset, C = f.C + offset }) ?? [];
        var selectedPaint = cached.Paint.Where(p => mask.Contains(p.Face.Primitive) && !fallback(p.Face.Primitive));
        return new(cached.Key, cached.Role, cached.Anchor,
            cached.VertexIds.Concat(extra?.VertexIds ?? []), cached.ExactVertices.Concat(extra?.ExactVertices ?? []),
            cached.Offsets.Concat(extra?.Offsets ?? []), faces.Concat(additional), selectedPaint.Concat(extra?.Paint ?? []),
            MapSurfaceCompiler.FallbackCells(validated, mask));
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
        double cellArea = ((double)x1 - x0) * ((double)z1 - z0);
        bool refused = cellArea > 4.0 * limits.MaxFaces + 64;
        if (DemandDiagnostics.IsObserved)
        {
            decimal cells;
            bool saturated = false;
            try { cells = ((decimal)x1 - x0) * ((decimal)z1 - z0); }
            catch (OverflowException) { cells = decimal.MaxValue; saturated = true; }
            var profile = MapFoliageDemandOrigins.Profile(limits);
            DemandDiagnostics.Check(this, "MapFoliageSupportBuilder.MaxFaces.1", "MaxFaces", 0, cells,
                4m * profile.MaxFaces + 64, cellArea > 4.0 * profile.MaxFaces + 64, CapacityDemandLifetime.Transient, saturated: saturated);
        }
        if (refused)
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
            bool faceRefused = checked(_faces + count) > limits.MaxFaces;
            if (DemandDiagnostics.IsObserved)
                new MapCapacityDemandOrigin("MaxFaces", _faces, MapFoliageDemandOrigins.Profile(limits).MaxFaces)
                    .Report(this, "MapFoliageSupportBuilder.MaxFaces.2", count, CapacityDemandLifetime.Cumulative);
            if (faceRefused)
                throw new MapFoliageRefusal(MapFoliageStatus.CapacityExceeded, Work(), "prepared faces");
            // One 1280-byte charge per unique face covers both holds: this grid's record and the membership context's
            // reused hold of the same face (its transformed copy, bound face and index entries). ReusePhysical charges
            // only faces it compiles beyond the grid.
            limits.ChargePreparation?.Invoke(checked(1280 * count));
            _faces += count;
            MapCompiledPatch local = MapFoliageTriangles.Compile(validated, mask, (int)count);
            _logical.Add(key, (local, mask));
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
            Part("normal-slot|" + MapFoliagePatchFaces.Describe(new(surface.Id, sx, sz)) + "|" + (patch is null ? "absent" : MapSurfaceSemantics.PatchDigest(patch)));
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
        Part(MapFoliageDependencies.PolicyPart(layer));
        Part($"binding|{binding.Kind}|{binding.SurfaceId}|{binding.SpaceId}|{N(binding.ReferenceY)}|{N(binding.SearchBelow)}|{N(binding.SearchAbove)}");
        Part(MapFoliageDependencies.RasterPart(layer));
        IReadOnlyDictionary<MapFoliageChunkKey, string> digests = densityDigests ?? layer.ChunkDigests.ToDictionary(d => d.Key, d => d.Sha256);
        foreach (var key in MapFoliageRasterReads.Density(layer, chunk))
            Part(string.Create(CultureInfo.InvariantCulture, $"density|{key.X}|{key.Z}|{(digests.TryGetValue(key, out string? sha) ? sha : "absent")}"));
        Part("allowed|" + string.Join(",", layer.AllowedVisibleMaterials.Select(m => m.ToString(CultureInfo.InvariantCulture))) +
            "|indoors|" + layer.ExcludeIndoors.ToString(CultureInfo.InvariantCulture));
        foreach (MapFoliageMaterial material in layer.Materials)
            Part($"material|{material.Id.ToString(CultureInfo.InvariantCulture)}|{material.ResourceId}|{material.Kind}");
        foreach (var (key, digest) in view.Witness.Present) Part("patch|" + MapFoliagePatchFaces.Describe(key) + "|" + digest);
        foreach (MapCoveredRange range in view.Witness.KnownEmpty)
            Part(string.Create(CultureInfo.InvariantCulture,
                $"empty|{range.SurfaceId}|{range.Slots.MinX}|{range.Slots.MinZ}|{range.Slots.MaxXExclusive}|{range.Slots.MaxZExclusive}"));
        foreach (MapRecordRef record in view.Witness.Records) Part("record|" + record.Id + "|" + MapFoliagePatchFaces.Describe(record.Anchor));
        foreach (var (target, set) in paint) Part("paint|" + target + "|" + set.Digest);
        // Surface metadata and effective poses decide the lattice, world paint precedence and every transformed point.
        foreach (MapSurfaceRef surface in view.Surfaces)
        {
            MapLatticeFrame frame = surface.Frame;
            Part(string.Create(CultureInfo.InvariantCulture,
                $"surface|{surface.Id}|{surface.Role}|{surface.PresencePolicy}|{surface.PaintTargetSurfaceId}|{surface.PaintPriority}|" +
                $"{frame.CellUnitMetres.Numerator}/{frame.CellUnitMetres.Denominator}|{frame.HeightUnitMetres.Numerator}/{frame.HeightUnitMetres.Denominator}|" +
                $"{frame.RowDirection}|{frame.Datum}"));
        }
        if (view.EffectiveContext is { } context)
        {
            var owners = view.Surfaces.Select(s => MapEffectiveContext.Key(MapPrefabChildKind.Surface, s.Id))
                .Concat(view.Witness.Records.Select(r => MapEffectiveContext.Key(MapPrefabChildKind.Record, r.Id))).ToHashSet(StringComparer.Ordinal);
            foreach (string key in owners.Order(StringComparer.Ordinal))
                if (context.Transforms.TryGetValue(key, out MapEffectiveTransform pose)) Part(string.Create(CultureInfo.InvariantCulture, $"transform|{key}|{BitConverter.DoubleToInt64Bits(pose.X)}|" +
                    $"{BitConverter.DoubleToInt64Bits(pose.Y)}|{BitConverter.DoubleToInt64Bits(pose.Z)}|" +
                    $"{BitConverter.DoubleToInt64Bits(pose.YawRadians)}|{BitConverter.DoubleToInt64Bits(pose.Scale)}"));
        }
    }
}
