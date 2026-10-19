using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Support;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;

namespace KhaozEngine.MapDoc.Foliage;

/// <summary>Why a foliage sample was rejected, or Accepted. Task-later reasons (Solid, RoofAbove, Door, EdgeZero and
/// DensityRejected) belong to clearance and distribution and are never produced by surface sampling.</summary>
public enum MapFoliageReason
{
    Accepted, OutsideDomain, RasterZero, MissingUnderlay, MaterialNotAllowed, WaterMaterial, Indoor, Solid, RoofAbove, Door,
    EdgeZero, DensityRejected,
}

/// <summary>One sampled foliage surface point. Only a Complete status carries a sample decision. A refused point has no
/// face, zero density and reason OutsideDomain, and its caller refuses the whole batch. SupportOwnerId is the canonical
/// selected support surface (R7-R4) for every accepted point, OccupiedSpaceId is membership only. Height and Normal are
/// world facts after the support's effective transform. Density is the clamped raster density of an accepted point and
/// zero otherwise.</summary>
public sealed record MapFoliageSurfacePoint(MapFoliageStatus Status, MapFoliageReason Reason, string? SupportOwnerId,
    string? OccupiedSpaceId, MapFaceKey? Face, float Height, Vector3 Normal, float Density, ushort VisibleMaterial);

/// <summary>A typed foliage refusal that unwinds a whole preparation or batch atomically. Carries the work charged before
/// the refusal.</summary>
public sealed class MapFoliageRefusal : Exception
{
    public MapFoliageRefusal(MapFoliageStatus status, MapFoliageWork work, string detail) : base(detail)
    {
        ArgumentNullException.ThrowIfNull(work);
        Status = status;
        Work = work;
    }

    public MapFoliageStatus Status { get; }
    public MapFoliageWork Work { get; }
}

/// <summary>Prepares one density chunk's support surfaces once from a captured context: one bounded acquisition of the
/// chunk with its normal halo, the layer binding selected once per support cell, logical ground triangles, smoothed
/// corner normals and visible paint. Sampling a candidate afterwards reads only those prepared facts and never runs a
/// support query.</summary>
public static class MapFoliageSupport
{
    internal static readonly MapFoliageWork NoWork = new(0, 0, 0, 0, 0, 0, 0, 0);

    /// <summary>Prepares <paramref name="chunk"/> of <paramref name="layer"/>, which must be one of the context's captured
    /// layers. A refusal is a prepared value with a non-Complete status, never a partial preparation.</summary>
    public static MapPreparedFoliageSupport Prepare(MapFoliageGenerationContext context, MapFoliageLayer layer, MapFoliageChunkKey chunk,
        MapFoliageLimits limits)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(limits);
        if (context.Foliage is null || !context.Foliage.Layers.Contains(layer))
            throw new ArgumentException("the layer is not one of the context's captured layers", nameof(layer));
        IReadOnlyList<MapSurfaceRef> declared = context.Resources.SurfaceSource.Surfaces;
        MapSurfaceScope scope;
        try { scope = Scope(layer, chunk, declared, context.Resources.Transforms); }
        catch (Exception error) when (error is MapExactOverflowException or OverflowException or ArgumentException)
        { return MapPreparedFoliageSupport.Refused(layer, chunk, MapFoliageStatus.NotRepresentable, NoWork, "chunk scope is not representable"); }
        if (limits.MaxPrepareScopes < 1)
            return MapPreparedFoliageSupport.Refused(layer, chunk, MapFoliageStatus.CapacityExceeded, NoWork, "prepare scopes");
        MapScopedSurfaces view;
        IReadOnlyDictionary<string, MapPaintRegionSet> paint;
        try
        {
            view = context.Acquire(scope);
            paint = context.PaintFor(scope);
        }
        catch (Exception error) when (Classify(error) is { } status)
        { return MapPreparedFoliageSupport.Refused(layer, chunk, status, NoWork with { PrepareScopes = 1 }, error.Message); }
        return Prepare(view, paint, declared, layer, chunk, limits);
    }

    /// <summary>The acquisition scope of a chunk: its world rectangle plus a halo of three of the largest declared support
    /// cells, which holds every neighbour corner a boundary normal reads. Every role is acquired, since membership reads
    /// ceilings and visible paint reads paint overrides.</summary>
    internal static MapSurfaceScope Scope(MapFoliageLayer layer, MapFoliageChunkKey chunk, IReadOnlyList<MapSurfaceRef> declared,
        IReadOnlyDictionary<string, MapEffectiveTransform> transforms)
    {
        (double minX, double minZ, double maxX, double maxZ) = Rect(layer, chunk);
        double cell = 0;
        foreach (MapSurfaceRef surface in declared.Where(s => s.Role == MapSurfaceRole.SupportFloor))
        {
            double scale = transforms.TryGetValue(MapEffectiveContext.Key(MapPrefabChildKind.Surface, surface.Id), out MapEffectiveTransform pose) ? pose.Scale : 1;
            cell = Math.Max(cell, surface.Frame.CellUnitMetres.Exact().ToDouble() * scale);
        }
        double halo = 3 * cell + 0.01;
        minX -= halo; minZ -= halo; maxX += halo; maxZ += halo;
        var frame = WorldFrame.Nearest((float)((minX + maxX) / 2), (float)((minZ + maxZ) / 2));
        double ax = (double)frame.X * WorldFrame.Grid, az = (double)frame.Z * WorldFrame.Grid;
        var min = new Vector2(MathF.BitDecrement((float)(minX - ax)), MathF.BitDecrement((float)(minZ - az)));
        var max = new Vector2(MathF.BitIncrement((float)(maxX - ax)), MathF.BitIncrement((float)(maxZ - az)));
        if (!float.IsFinite(min.X) || !float.IsFinite(min.Y) || !float.IsFinite(max.X) || !float.IsFinite(max.Y))
            throw new MapExactOverflowException();
        return new(frame, min, max, null, null, Enum.GetValues<MapSurfaceRole>(), null, new MapQueryLimits());
    }

    /// <summary>The chunk's world rectangle. Rows advance positive world Z.</summary>
    internal static (double MinX, double MinZ, double MaxX, double MaxZ) Rect(MapFoliageLayer layer, MapFoliageChunkKey chunk)
    {
        double span = (double)MapFoliageChunk.Size * layer.CellSize;
        double minX = layer.OriginX + chunk.X * span, minZ = layer.OriginZ + chunk.Z * span;
        return (minX, minZ, minX + span, minZ + span);
    }

    /// <summary>The preparation core over an acquired chunk view, its scoped partial paint and the effective declared
    /// surfaces. The public entry acquires all three from the captured context.</summary>
    internal static MapPreparedFoliageSupport Prepare(MapScopedSurfaces view, IReadOnlyDictionary<string, MapPaintRegionSet> paint,
        IReadOnlyList<MapSurfaceRef> declared, MapFoliageLayer layer, MapFoliageChunkKey chunk, MapFoliageLimits limits)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(paint);
        ArgumentNullException.ThrowIfNull(declared);
        var builder = new MapFoliageSupportBuilder(view, paint, declared, layer, chunk, limits);
        try { return builder.Build(); }
        catch (MapFoliageRefusal refusal)
        { return MapPreparedFoliageSupport.Refused(layer, chunk, refusal.Status, builder.Work(), refusal.Message); }
        catch (Exception error) when (Classify(error) is { } status)
        { return MapPreparedFoliageSupport.Refused(layer, chunk, status, builder.Work(), error.Message); }
    }

    /// <summary>The released raster interpolation: float u and v, zero outside [0, Width - 1] by [0, Depth - 1], endpoint
    /// clamped bilinear <c>a + (b - a) * t</c> along X then Z, then divided by 255.</summary>
    internal static float RasterDensity(MapFoliageLayer layer, float worldX, float worldZ)
    {
        float u = (worldX - layer.OriginX) / layer.CellSize;
        float v = (worldZ - layer.OriginZ) / layer.CellSize;
        if (u < 0f || v < 0f || u > layer.Width - 1 || v > layer.Depth - 1) return 0f;
        int x0 = Math.Min((int)MathF.Floor(u), layer.Width - 1);
        int z0 = Math.Min((int)MathF.Floor(v), layer.Depth - 1);
        int x1 = Math.Min(x0 + 1, layer.Width - 1);
        int z1 = Math.Min(z0 + 1, layer.Depth - 1);
        float fx = u - x0;
        float fz = v - z0;
        float south = Lerp(layer.DensityAt(x0, z0), layer.DensityAt(x1, z0), fx);
        float north = Lerp(layer.DensityAt(x0, z1), layer.DensityAt(x1, z1), fx);
        return Lerp(south, north, fz) / 255f;
    }

    static float Lerp(float a, float b, float t) => a + (b - a) * t;

    internal static MapFoliageStatus Map(MapSupportStatus status) => status switch
    {
        MapSupportStatus.NoSupport => MapFoliageStatus.MissingSupport,
        MapSupportStatus.MissingGeometry => MapFoliageStatus.Unavailable,
        MapSupportStatus.Ambiguous => MapFoliageStatus.Ambiguous,
        MapSupportStatus.CapacityExceeded => MapFoliageStatus.CapacityExceeded,
        MapSupportStatus.NotRepresentable => MapFoliageStatus.NotRepresentable,
        MapSupportStatus.Invalid => MapFoliageStatus.Invalid,
        _ => MapFoliageStatus.Complete,
    };

    static MapFoliageStatus? Classify(Exception error) => error switch
    {
        MapSurfaceCapacityException => MapFoliageStatus.CapacityExceeded,
        MapExactOverflowException or OverflowException => MapFoliageStatus.NotRepresentable,
        MapDocumentException when MapCommonRefinement.NotRepresentable(error) => MapFoliageStatus.NotRepresentable,
        MapDocumentException { Message: var message } when message.Contains("budget", StringComparison.Ordinal) ||
            message.Contains("limit exceeded", StringComparison.Ordinal) => MapFoliageStatus.CapacityExceeded,
        MapDocumentException { Message: var message } when message.Contains("unavailable", StringComparison.Ordinal) ||
            message.StartsWith("MissingGeometry", StringComparison.Ordinal) => MapFoliageStatus.Unavailable,
        MapDocumentException => MapFoliageStatus.Invalid,
        _ => null,
    };
}

/// <summary>One prepared chunk of support surfaces. Immutable after preparation, so samples may run concurrently.</summary>
public sealed class MapPreparedFoliageSupport
{
    readonly MapFoliageLayer _layer;
    readonly IReadOnlyList<MapFoliageSupportGrid> _grids;
    readonly MapFoliageMaterials? _materials;

    internal MapPreparedFoliageSupport(MapFoliageLayer layer, MapFoliageChunkKey chunk, MapFoliageStatus status, string? detail,
        MapFoliageWork work, IReadOnlyList<string> dependencyParts, IReadOnlyList<MapFoliageSupportGrid> grids, MapFoliageMaterials? materials)
    {
        _layer = layer;
        _grids = grids;
        _materials = materials;
        LayerId = layer.Id;
        Chunk = chunk;
        Status = status;
        Detail = detail;
        Work = work;
        DependencyParts = new ReadOnlyCollection<string>(dependencyParts.ToArray());
        (double minX, double minZ, double maxX, double maxZ) = MapFoliageSupport.Rect(layer, chunk);
        Bounds = new((float)minX, (float)minZ, (float)maxX, (float)maxZ);
        MinX = minX; MinZ = minZ; MaxX = maxX; MaxZ = maxZ;
    }

    internal static MapPreparedFoliageSupport Refused(MapFoliageLayer layer, MapFoliageChunkKey chunk, MapFoliageStatus status,
        MapFoliageWork work, string detail) => new(layer, chunk, status, detail, work, Array.Empty<string>(), Array.Empty<MapFoliageSupportGrid>(), null);

    public string LayerId { get; }
    public MapFoliageChunkKey Chunk { get; }
    /// <summary>Complete, or the typed refusal that every sample of this chunk reports.</summary>
    public MapFoliageStatus Status { get; }
    public string? Detail { get; }
    /// <summary>Preparation work: one prepared scope, its logical faces and its per-cell support queries.</summary>
    public MapFoliageWork Work { get; }
    /// <summary>Canonical ordered descriptions of every input the prepared samples read: policy, raster chunks, materials,
    /// binding, acquired patches and records, normal-halo slots and scoped paint. Never a whole-root digest.</summary>
    public IReadOnlyList<string> DependencyParts { get; }
    /// <summary>The chunk's world rectangle.</summary>
    public MapFoliageRect Bounds { get; }
    double MinX { get; }
    double MinZ { get; }
    double MaxX { get; }
    double MaxZ { get; }

    /// <summary>Samples the prepared chunk at a world XZ inside <see cref="Bounds"/>, using only prepared facts.</summary>
    public MapFoliageSurfacePoint Sample(float worldX, float worldZ)
    {
        if (Status != MapFoliageStatus.Complete) return Refusal(Status);
        if (!float.IsFinite(worldX) || !float.IsFinite(worldZ)) throw new ArgumentException("foliage sample coordinates must be finite.");
        if (worldX < MinX || worldX > MaxX || worldZ < MinZ || worldZ > MaxZ)
            throw new ArgumentOutOfRangeException(nameof(worldX), "the sample lies outside the prepared chunk.");
        float density;
        try { density = MapFoliageSupport.RasterDensity(_layer, worldX, worldZ); }
        catch (MapDocumentException) { return Refusal(MapFoliageStatus.Unavailable); }

        MapFoliageSupportCell? chosen = null;
        float fx = 0, fz = 0;
        int selected = 0;
        foreach (MapFoliageSupportGrid grid in _grids)
        {
            (long x, long z, float localX, float localZ) = grid.Locate(worldX, worldZ);
            if (!grid.Cells.TryGetValue((x, z), out MapFoliageSupportCell? cell)) continue;
            if (cell.Refusal is { } refusal) return Refusal(refusal);
            if (!cell.Selected) continue;
            selected++;
            if (chosen is null) (chosen, fx, fz) = (cell, localX, localZ);
        }
        if (selected > 1) return Refusal(MapFoliageStatus.Ambiguous);
        if (chosen is null)
        {
            string? requested = _layer.Support.Kind == MapSupportBindingKind.Surface ? _layer.Support.SurfaceId : null;
            return new(MapFoliageStatus.Complete, MapFoliageReason.OutsideDomain, requested, null, null, 0, Vector3.UnitY, 0, 0);
        }

        MapFoliageGround ground = MapFoliageNormals.Ground(chosen.Corners, chosen.Cell, chosen.Split, fx, fz);
        var exact = new MapExactXz(MapExactValue.FromSingle(worldX), MapExactValue.FromSingle(worldZ));
        (MapFaceKey face, MapPaintCoverage painted) = chosen.Faces.Face(chosen.SlotCell, ground.Parent, exact);
        MapPaintCoverage coverage = _materials!.Coverage(face, painted, exact);
        MapFoliageReason reason = _materials.Reason(coverage, chosen.Cell.Flags, out ushort visible);
        if (density <= 0f) reason = MapFoliageReason.RasterZero;
        float height = ground.Height;
        Vector3 normal = ground.Normal;
        if (chosen.Kernel is { } kernel)
        {
            height = kernel.Height(MapExactValue.FromSingle(height)).ToSingle();
            normal = kernel.Normal(normal);
        }
        return new(MapFoliageStatus.Complete, reason, chosen.OwnerId, chosen.SpaceId, face, height, normal,
            reason == MapFoliageReason.Accepted ? Math.Clamp(density, 0f, 1f) : 0f, visible);
    }

    static MapFoliageSurfacePoint Refusal(MapFoliageStatus status) =>
        new(status, MapFoliageReason.OutsideDomain, null, null, null, 0, Vector3.UnitY, 0, 0);
}

/// <summary>One support surface's prepared cells within a chunk, keyed by global lattice cell.</summary>
internal sealed class MapFoliageSupportGrid(MapSurfaceRef surface, MapResourceTransform? kernel)
{
    readonly float _cell = surface.Frame.CellUnitMetres.Exact().ToSingle();
    internal MapSurfaceRef Surface { get; } = surface;
    internal MapResourceTransform? Kernel { get; } = kernel;
    internal Dictionary<(long X, long Z), MapFoliageSupportCell> Cells { get; } = new();

    /// <summary>The lattice cell holding a world XZ and the local fractions inside it, in the released float operations:
    /// tile X is world X over the cell size, tile Z negates world Z first on a NegativeZ frame.</summary>
    internal (long X, long Z, float LocalX, float LocalZ) Locate(float worldX, float worldZ)
    {
        float tx, tz;
        bool negative = Surface.Frame.RowDirection == MapRowDirection.NegativeZ;
        if (Kernel is null)
        {
            tx = worldX / _cell;
            tz = negative ? -worldZ / _cell : worldZ / _cell;
        }
        else
        {
            MapEffectiveTransform pose = Kernel.Transform;
            (double s, double c) = Math.SinCos(pose.YawRadians);
            double dx = worldX - pose.X, dz = worldZ - pose.Z;
            double lx = (c * dx - s * dz) / pose.Scale, lz = (s * dx + c * dz) / pose.Scale;
            tx = (float)(lx / _cell);
            tz = (float)((negative ? -lz : lz) / _cell);
        }
        long x = (long)MathF.Floor(tx), z = (long)MathF.Floor(tz);
        return (x, z, tx - x, tz - z);
    }
}

/// <summary>One prepared support cell: its authored cell, released corners and split, its logical faces and the binding's
/// selection, made once at preparation.</summary>
internal sealed class MapFoliageSupportCell
{
    internal required MapPatchKey Key { get; init; }
    internal required int SlotCell { get; init; }
    internal required MapSurfaceCell Cell { get; init; }
    internal required bool Split { get; init; }
    internal required bool Fallback { get; init; }
    internal required MapFoliageCellCorners Corners { get; init; }
    internal required MapFoliagePatchFaces Faces { get; init; }
    internal required MapResourceTransform? Kernel { get; init; }
    internal bool Selected { get; private set; }
    internal string? OwnerId { get; private set; }
    internal string? SpaceId { get; private set; }
    internal MapFoliageStatus? Refusal { get; private set; }

    internal void Select(string? owner, string? space)
    {
        Selected = owner is not null;
        OwnerId = owner;
        SpaceId = space;
    }

    internal void Refuse(MapFoliageStatus status) => Refusal ??= status;
}

/// <summary>A patch's logical faces in effective coordinates with their world-painted coverage, indexed by cell and
/// parent triangle.</summary>
internal sealed class MapFoliagePatchFaces
{
    readonly MapCompiledPatch _compiled;
    readonly Dictionary<(int Primitive, byte Parent), List<int>> _index = new();

    internal MapFoliagePatchFaces(MapCompiledPatch compiled)
    {
        _compiled = compiled;
        for (int i = 0; i < compiled.Faces.Count; i++)
        {
            MapFaceKey key = compiled.Faces[i].Key;
            if (!_index.TryGetValue((key.Primitive, key.ParentTriangle), out List<int>? list)) _index.Add((key.Primitive, key.ParentTriangle), list = new());
            list.Add(i);
        }
    }

    internal int Count => _compiled.Faces.Count;

    /// <summary>The canonical face of a parent triangle at a world point. An unsubdivided parent is its one face. A
    /// subdivided parent picks its first child holding the point exactly.</summary>
    internal (MapFaceKey Face, MapPaintCoverage Paint) Face(int slotCell, byte parent, MapExactXz point)
    {
        List<int> faces = _index[(slotCell, parent)];
        int chosen = faces[0];
        if (faces.Count > 1)
            foreach (int index in faces)
                if (MapSubdividedTriangle.Height(_compiled.ExactTriangle(_compiled.Faces[index]), point.X, point.Z) is not null)
                {
                    chosen = index;
                    break;
                }
        return (_compiled.Faces[chosen].Key, _compiled.Paint[chosen]);
    }

    internal static string Describe(MapPatchKey key) =>
        string.Create(CultureInfo.InvariantCulture, $"{key.SurfaceId}|{key.SlotX}|{key.SlotZ}");
}

/// <summary>Builds one chunk preparation: acquisition checks, cell discovery, logical faces with world paint, released
/// corners and normals, then one binding selection per support cell. Every charge is counted before its allocation.</summary>
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
        if (binding.Kind == MapSupportBindingKind.Surface) SelectSurface(grids, bound!);
        else SelectSpace(grids, binding);

        Describe(binding);
        var materials = new MapFoliageMaterials(layer, paint);
        return new(layer, chunk, MapFoliageStatus.Complete, null, Work(), _parts.Order(StringComparer.Ordinal).ToArray(), grids, materials);
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
            new(MapExactValue.FromSingle(MathF.BitDecrement((float)minX)), MapExactValue.FromSingle(MathF.BitDecrement((float)minZ))),
            new(MapExactValue.FromSingle(MathF.BitIncrement((float)maxX)), MapExactValue.FromSingle(MathF.BitIncrement((float)maxZ))));
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

    // A Space binding selects across every acquired support floor, once per distinct cell centre. The selected face's
    // cell is marked, whichever floor it lies on. No eligible support inside the bound space refuses, outside it is
    // simply not that space's domain.
    void SelectSpace(MapFoliageSupportGrid[] grids, MapSupportBinding binding)
    {
        MapSpaceDoc? space = _support!.Records.OfType<MapSpaceDoc>().FirstOrDefault(s => s.Id == binding.SpaceId);
        if (space is null) return;
        string owner = MapSpaceGeometry.Owner(view, space);
        var byOwner = grids.ToDictionary(g => g.Surface.Id, StringComparer.Ordinal);
        foreach (MapFoliageSupportGrid grid in grids)
            foreach (var ((x, z), cell) in grid.Cells.OrderBy(c => c.Key.Z).ThenBy(c => c.Key.X))
            {
                MapExactXz centre = Centre(grid.Surface, x, z);
                MapSupportResult result = Query(binding, centre);
                (MapPatchKey Patch, int SlotCell)? target = result.Status switch
                {
                    MapSupportStatus.Supported when result.Face is { Patch: { } patch } face => (patch, face.Primitive),
                    MapSupportStatus.LegacyFallback when result.Compatibility is { } tag => (tag.Patch, tag.SlotCell),
                    _ => null,
                };
                if (target is { } t)
                {
                    if (byOwner.TryGetValue(t.Patch.SurfaceId, out MapFoliageSupportGrid? floor) &&
                        floor.Cells.TryGetValue((t.Patch.SlotX * MapPatchKey.SlotCells + t.SlotCell % 64, t.Patch.SlotZ * MapPatchKey.SlotCells + t.SlotCell / 64),
                            out MapFoliageSupportCell? selected) && !selected.Selected)
                        selected.Select(MapFoliageSupportIdentity.From(result), result.SpaceId);
                }
                else if (result.Status != MapSupportStatus.NoSupport) cell.Refuse(MapFoliageSupport.Map(result.Status));
                else if (InSpace(owner, centre, binding.ReferenceY!.Value)) cell.Refuse(MapFoliageStatus.MissingSupport);
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

    bool InSpace(string owner, MapExactXz centre, float referenceY)
    {
        foreach (MapSpaceFootprint footprint in _support!.Footprints)
            if (MapSpaceGeometry.Owner(view, MapSpaceGeometry.Record<MapSpaceDoc>(view, footprint.Space)) == owner &&
                MapSpaceGeometry.FootprintCell(view, footprint, centre) is not null) return true;
        _queriesRun++;
        return _support.Membership.QueryExact(new(centre.X, MapExactValue.FromSingle(referenceY), centre.Z)).SpaceId == owner;
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
    }
}
