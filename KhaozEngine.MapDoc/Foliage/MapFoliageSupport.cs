using KhaozEngine.Diagnostics;
using KhaozEngine.Primitives;
using DemandDiagnostics = KhaozEngine.Diagnostics.CapacityDemandDiagnostics;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Support;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Foliage;

/// <summary>Why a foliage sample was rejected, or Accepted. Task-later reasons (Solid, RoofAbove, Door, EdgeZero and
/// DensityRejected) belong to clearance and distribution and are never produced by surface sampling. Refused marks a
/// point whose status is a typed refusal, so it carries no sample decision.</summary>
public enum MapFoliageReason
{
    Accepted, OutsideDomain, RasterZero, MissingUnderlay, MaterialNotAllowed, WaterMaterial, Indoor, Solid, RoofAbove, Door,
    EdgeZero, DensityRejected, Refused,
}

/// <summary>One sampled foliage surface point. Only a Complete status carries a sample decision. A refused point has
/// reason Refused, no face and zero density, and its caller refuses the whole batch. SupportOwnerId is the canonical
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
 => Prepare((IMapFoliageInputs)context, layer, chunk, limits);

    internal static MapPreparedFoliageSupport Prepare(IMapFoliageInputs context, MapFoliageLayer layer, MapFoliageChunkKey chunk,
        MapFoliageLimits limits)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(limits);
        if (context.Foliage is null || !context.Foliage.Layers.Contains(layer))
            throw new ArgumentException("the layer is not one of the context's captured layers", nameof(layer));
        IReadOnlyList<MapSurfaceRef> declared = context.DeclaredSurfaces;
        MapSurfaceScope scope;
        try { scope = Scope(layer, chunk, context.SupportFloorsFor(layer, chunk), context.Transforms, context.QueryLimits); }
        catch (Exception error) when (error is MapExactOverflowException or OverflowException or ArgumentException)
        { return MapPreparedFoliageSupport.Refused(layer, chunk, MapFoliageStatus.NotRepresentable, NoWork, "chunk scope is not representable"); }
        if (DemandDiagnostics.Check(context, "MapFoliageSupport.MaxPrepareScopes.1", "MaxPrepareScopes", 0, 1, limits.MaxPrepareScopes, limits.MaxPrepareScopes < 1, CapacityDemandLifetime.Cumulative))
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
        return Prepare(view, paint, declared, layer, chunk, limits, context.DensityDigests(layer)).WithAssets(context.Assets);
    }

    // The closure iteration is shared by metadata acquisition and retained generation inputs.
    internal static IReadOnlyList<MapSurfaceRef> FloorsFor(MapFoliageLayer layer, MapFoliageChunkKey chunk,
        IReadOnlyDictionary<string, MapEffectiveTransform> transforms,
        Func<MapFoliageGenerationContext.Bounds, IEnumerable<MapSurfaceRef>> find)
    {
        var rect = MapFoliageSupport.Rect(layer, chunk);
        var found = new Dictionary<string, MapSurfaceRef>(StringComparer.Ordinal);
        double pad = 0.01;
        while (true)
        {
            int previous = found.Count;
            foreach (MapSurfaceRef surface in find(new(rect.MinX - pad, rect.MinZ - pad, rect.MaxX + pad, rect.MaxZ + pad)))
                found.TryAdd(surface.Id, surface);
            if (found.Count == previous) return found.Values.OrderBy(s => s.Id, StringComparer.Ordinal).ToArray();
            var floors = found.Values.Select(s => (Surface: s, Pose: transforms.TryGetValue(
                MapEffectiveContext.Key(MapPrefabChildKind.Surface, s.Id), out MapEffectiveTransform t) ? t : (MapEffectiveTransform?)null)).ToArray();
            double cell = floors.Max(f => f.Surface.Frame.CellUnitMetres.Exact().ToDouble() * (f.Pose?.Scale ?? 1));
            pad = 3 * cell + MapFoliageSupport.ReadReach(layer, floors) + 0.01;
        }
    }

    /// <summary>The acquisition scope of a chunk: its world rectangle widened by the clearance read reach over the spatially contributing
    /// support floors, since grids and the shared context widen by that reach, plus a halo of three of the largest
    /// contributing support cells, which holds every neighbour corner a boundary normal reads. Every role is acquired, since
    /// membership reads ceilings and visible paint reads paint overrides.</summary>
    internal static MapSurfaceScope Scope(MapFoliageLayer layer, MapFoliageChunkKey chunk, IReadOnlyList<MapSurfaceRef> declared,
        IReadOnlyDictionary<string, MapEffectiveTransform> transforms, MapQueryLimits? queryLimits = null)
    {
        (double minX, double minZ, double maxX, double maxZ) = Rect(layer, chunk);
        double cell = 0;
        var floors = new List<(MapSurfaceRef Surface, MapEffectiveTransform? Pose)>();
        foreach (MapSurfaceRef surface in declared.Where(s => s.Role == MapSurfaceRole.SupportFloor))
        {
            MapEffectiveTransform? posed = transforms.TryGetValue(MapEffectiveContext.Key(MapPrefabChildKind.Surface, surface.Id), out MapEffectiveTransform pose) ? pose : null;
            cell = Math.Max(cell, surface.Frame.CellUnitMetres.Exact().ToDouble() * (posed?.Scale ?? 1));
            floors.Add((surface, posed));
        }
        double halo = 3 * cell + 0.01;
        double reach = ReadReach(layer, floors);
        if (reach > 0) halo += reach;
        minX -= halo; minZ -= halo; maxX += halo; maxZ += halo;
        var frame = WorldFrame.Nearest((float)((minX + maxX) / 2), (float)((minZ + maxZ) / 2));
        double ax = (double)frame.X * WorldFrame.Grid, az = (double)frame.Z * WorldFrame.Grid;
        var min = new Vector2(Outward(minX - ax, lower: true), Outward(minZ - az, lower: true));
        var max = new Vector2(Outward(maxX - ax, lower: false), Outward(maxZ - az, lower: false));
        if (!float.IsFinite(min.X) || !float.IsFinite(min.Y) || !float.IsFinite(max.X) || !float.IsFinite(max.Y))
            throw new MapExactOverflowException();
        return new(frame, min, max, null, null, Enum.GetValues<MapSurfaceRole>(), null, queryLimits ?? new MapQueryLimits());
    }

    /// <summary>How far outside the chunk clearance reads support, the one reach the acquisition scope, the support grids,
    /// the shared context and the clearance owner rectangle widen by. Every clearance read lands at a sample or at a
    /// support-cell centre of some grid, so the reach follows the read chain over the largest effective cell c (cell unit
    /// metres times pose scale) of the given floors:
    /// <list type="bullet">
    /// <item>The sample's own cell centre, where Solid and roof footprints and their mask targets are tested. It lies
    /// at most 0.5 c from the sample along each cell axis, so 0.5 c on each world axis, or half the diagonal
    /// sqrt(0.5) c &lt; 0.71 c under a yawed pose. Always read.</item>
    /// <item>With edge fade, each cardinal neighbour centre. It lies at most 1.5 c along one cell axis and 0.5 c along the
    /// other, so 1.5 c on each world axis, or sqrt(1.5^2 + 0.5^2) c &lt; 1.6 c under yaw.</item>
    /// <item>With edge fade, that neighbour's own cell centre on the grid that supports it, which may be another lattice:
    /// a further 0.5 c, or 0.71 c under yaw.</item>
    /// </list>
    /// Each term takes the largest cell over the floors, since a centre from a coarse grid may land on a finer surface.
    /// The reach is a bound on world-axis offsets from the chunk rectangle, so every read point lies inside the rectangle
    /// widened by it. Each surface's one-cell ring past that rectangle absorbs float rounding in locating and centring.</summary>
    internal static double ReadReach(MapFoliageLayer layer, IEnumerable<(MapSurfaceRef Surface, MapEffectiveTransform? Pose)> floors)
    {
        double centre = 0, neighbour = 0;
        foreach (var (surface, pose) in floors)
        {
            double cell = surface.Frame.CellUnitMetres.Exact().ToDouble() * (pose?.Scale ?? 1);
            bool yawed = pose is { YawRadians: not 0 };
            centre = Math.Max(centre, (yawed ? 0.71 : 0.5) * cell);
            neighbour = Math.Max(neighbour, (yawed ? 1.6 : 1.5) * cell);
        }
        return layer.EdgeFade > 0 ? neighbour + centre : centre;
    }

    /// <summary>The nearest float on the outer side of <paramref name="value"/>: itself when exact, else one step outward.
    /// Zero stays zero, so no denormal bound reaches exact conversion.</summary>
    internal static float Outward(double value, bool lower)
    {
        float rounded = (float)value;
        return lower ? (rounded > value ? MathF.BitDecrement(rounded) : rounded) : (rounded < value ? MathF.BitIncrement(rounded) : rounded);
    }

    /// <summary>The chunk's world rectangle. Rows advance positive world Z.</summary>
    internal static (double MinX, double MinZ, double MaxX, double MaxZ) Rect(MapFoliageLayer layer, MapFoliageChunkKey chunk)
    {
        return (MapFoliageRasterReads.Edge(layer, chunk.X, true), MapFoliageRasterReads.Edge(layer, chunk.Z, false),
            MapFoliageRasterReads.Edge(layer, (long)chunk.X + 1, true), MapFoliageRasterReads.Edge(layer, (long)chunk.Z + 1, false));
    }

    /// <summary>The preparation core over an acquired chunk view, its scoped partial paint and the effective declared
    /// surfaces. The public entry acquires all three from the captured context.</summary>
    internal static MapPreparedFoliageSupport Prepare(MapScopedSurfaces view, IReadOnlyDictionary<string, MapPaintRegionSet> paint,
        IReadOnlyList<MapSurfaceRef> declared, MapFoliageLayer layer, MapFoliageChunkKey chunk, MapFoliageLimits limits,
        IReadOnlyDictionary<MapFoliageChunkKey, string>? densityDigests = null)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(paint);
        ArgumentNullException.ThrowIfNull(declared);
        var builder = new MapFoliageSupportBuilder(view, paint, declared, layer, chunk, limits, densityDigests);
        try { return builder.Build(); }
        catch (MapFoliageRefusal refusal)
        { return MapPreparedFoliageSupport.Refused(layer, chunk, refusal.Status, builder.Work(), refusal.Message); }
        catch (Exception error) when (Classify(error) is { } status)
        { return MapPreparedFoliageSupport.Refused(layer, chunk, status, builder.Work(), error.Message); }
    }

    /// <summary>The released interpolation's raster coordinate on one axis, in samples from the origin. Single precision
    /// subtraction and division by a positive cell size round monotonically, so it never decreases as the world
    /// coordinate grows.</summary>
    internal static float RasterCoordinate(float world, float origin, float cellSize) => (world - origin) / cellSize;

    /// <summary>The released raster interpolation: float u and v, zero outside [0, Width - 1] by [0, Depth - 1], endpoint
    /// clamped bilinear <c>a + (b - a) * t</c> along X then Z, then divided by 255.</summary>
    internal static float RasterDensity(MapFoliageLayer layer, float worldX, float worldZ)
    {
        float u = RasterCoordinate(worldX, layer.OriginX, layer.CellSize);
        float v = RasterCoordinate(worldZ, layer.OriginZ, layer.CellSize);
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
        // Only refusal statuses reach this map. Anything else is a caller error and refuses as Invalid.
        _ => MapFoliageStatus.Invalid,
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
    readonly MapFoliageCandidateSupport? _candidates;

    internal MapPreparedFoliageSupport(MapFoliageLayer layer, MapFoliageChunkKey chunk, MapFoliageStatus status, string? detail,
        MapFoliageWork work, IReadOnlyList<string> dependencyParts, IReadOnlyList<MapFoliageSupportGrid> grids, MapFoliageMaterials? materials,
        MapFoliageCandidateSupport? candidates = null)
    {
        _layer = layer;
        _grids = grids;
        _materials = materials;
        _candidates = candidates;
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

    internal MapAssetClosure? Assets { get; private set; }
    internal MapPreparedFoliageSupport WithAssets(MapAssetClosure assets) { Assets = assets; return this; }
    internal IReadOnlyList<MapFoliageSupportGrid> Grids => _grids;
    internal MapFoliageCandidateSupport? Candidates => _candidates;
    internal bool Indoor(string? space) => _candidates?.Indoor(space) == true;

    // Clearance's cardinal tests ignore density and doors. They read the same prepared logical surface, visible paint
    // and membership at neighbour centres, including the one-cell halo outside render ownership.
    internal MapFoliageSurfacePoint Eligibility(float worldX, float worldZ) =>
        Decide(new(MapExactValue.FromSingle(worldX), MapExactValue.FromSingle(worldZ)), worldX, worldZ, 1f);

    internal MapSupportResult Target(MapSupportBinding binding, float worldX, float worldZ) =>
        _candidates!.Target(binding, new(MapExactValue.FromSingle(worldX), MapExactValue.FromSingle(worldZ)));

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

        try
        {
            var exact = new MapExactXz(MapExactValue.FromSingle(worldX), MapExactValue.FromSingle(worldZ));
            return Decide(exact, worldX, worldZ, density);
        }
        catch (Exception error) when (error is MapExactOverflowException or OverflowException) { return Refusal(MapFoliageStatus.NotRepresentable); }
        catch (MapDocumentException error)
        {
            return Refusal(MapCommonRefinement.NotRepresentable(error) ? MapFoliageStatus.NotRepresentable
                : error.Message.Contains("invalid", StringComparison.Ordinal) ? MapFoliageStatus.Invalid : MapFoliageStatus.Unavailable);
        }
        // Only a demand outside the prepared chunk context: this point's facts were not prepared.
        catch (InvalidOperationException error) when (error.Message.StartsWith("context demand", StringComparison.Ordinal))
        { return Refusal(MapFoliageStatus.Unavailable); }
    }

    MapFoliageSurfacePoint Decide(MapExactXz exact, float worldX, float worldZ, float density)
    {
        MapFoliageSupportCell? chosen = null;
        string? owner = null, space = null;
        float fx = 0, fz = 0;
        if (_layer.Support.Kind == MapSupportBindingKind.Surface)
            foreach (MapFoliageSupportGrid candidate in _grids)
            {
                (long x, long z, float localX, float localZ) = candidate.Locate(worldX, worldZ);
                if (candidate.Cells.TryGetValue((x, z), out MapFoliageSupportCell? cell) && cell.Selected)
                    (chosen, owner, fx, fz) = (cell, cell.OwnerId, localX, localZ);
            }
        else
        {
            MapFoliageCandidateOutcome outcome = _candidates!.Space(exact);
            if (outcome.Status != MapFoliageStatus.Complete) return Refusal(outcome.Status);
            if (outcome.Cell is { } cell)
            {
                (float localX, float localZ) = outcome.Grid!.LocalIn(worldX, worldZ, outcome.CellX, outcome.CellZ);
                (chosen, owner, space, fx, fz) = (cell, outcome.OwnerId, outcome.SpaceId, localX, localZ);
            }
        }
        if (chosen is null)
        {
            string? requested = _layer.Support.Kind == MapSupportBindingKind.Surface ? _layer.Support.SurfaceId : null;
            return new(MapFoliageStatus.Complete, MapFoliageReason.OutsideDomain, requested, null, null, 0, Vector3.UnitY, 0, 0);
        }

        MapFoliageGround ground = MapFoliageNormals.Ground(chosen.Corners, chosen.Cell, chosen.Split, fx, fz);
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
        if (_layer.Support.Kind == MapSupportBindingKind.Surface && _candidates is not null)
        {
            (MapFoliageStatus status, string? occupied) = _candidates.Occupied(exact, height);
            if (status != MapFoliageStatus.Complete) return Refusal(status);
            space = occupied;
        }
        return new(MapFoliageStatus.Complete, reason, owner, space, face, height, normal,
            reason == MapFoliageReason.Accepted ? Math.Clamp(density, 0f, 1f) : 0f, visible);
    }

    static MapFoliageSurfacePoint Refusal(MapFoliageStatus status) =>
        new(status, MapFoliageReason.Refused, null, null, null, 0, Vector3.UnitY, 0, 0);
}
