using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;

namespace KhaozEngine.MapDoc.Water;

/// <summary>Acquires complete canonical cells before containment or refinement. The requested window selects
/// memberships only. It never clips a cell's payload, level, medium or material. Scoped completeness is #1360.
/// Point sampling remains the separate #1358 consumer dependency.</summary>
public static class MapWaterScopedCompiler
{
    public const int MaxCells = 262144;
    public const int MaxEmittedVertices = 1048576;
    readonly record struct Cell(MapTileCoord Owner, long X, long Z, MapWideXz Min, MapWideXz Max);

    public static MapScopedWaterResult Compile(MapScopedScene scene, float cellMetres = 2f, MapWaterCompileLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (!float.IsFinite(cellMetres) || cellMetres <= 0 || cellMetres > 2) throw new ArgumentOutOfRangeException(nameof(cellMetres));
        limits ??= new(); limits.Validate();
        var admission = MapSceneAdmission.Current ?? new MapSceneAdmission(scene.Source.Data.Limits);
        using var admitted = admission.Enter();
        using var preparation = admission.BeginPreparation();
        var pairs = new MapWaterWorkBudget(limits.MaxPairChecks, compile: true);
        var witnesses = new List<MapSceneWitness>();
        var cellKeys = new List<MapSceneResourceKey>();
        var allCells = new List<MapWaterResourceCell>();
        long candidates = 0, vertices = 0;
        string geometry = "kemap/water-cell/1/grid-origin-0-0/half-open/x-slab-fan/" +
            BitConverter.SingleToInt32Bits(cellMetres).ToString("x8", CultureInfo.InvariantCulture) + "/tiles/" +
            BitConverter.SingleToInt32Bits(scene.Source.Data.TileSize).ToString("x8", CultureInfo.InvariantCulture);
        try
        {
            admission.ReserveBytes(512);
            if (!scene.Witness.CompleteForScope) return Refuse(MapSceneStatus.Incomplete, "Requested scene coverage is incomplete.");
            var output = new List<MapWaterResourceCell>();
            if (!scene.Scope.Channels.HasFlag(MapSceneChannels.Water)) return Result(MapSceneStatus.CompleteForScope, output);
            // The shared water clone and compile kernels participate in this same admission activation.
            var water = scene.Water;
            foreach (var body in (water?.Bodies ?? []).OrderBy(b => b.Id, StringComparer.Ordinal))
            {
                using var bodyPreparation = admission.BeginPreparation();
                int bodyStart = allCells.Count;
                var meter = new MapWaterWorkMeter(limits);
                meter.ChargeDomain(body.Domain);
                var effective = MapEffectiveWater.Resolve(scene.SurfaceView, body, pairs);
                var parts = Enumerable.Range(0, effective.Domain.Pieces.Count).SelectMany(i => effective.Domain.ConvexParts(i, pairs)).ToArray();
                admission.ReserveBytes(checked(256L + parts.Length * 256L));
                var index = new MapBoundsIndex(parts.Select(MapWaterPolygons.Bounds.Of).ToArray());
                var drawBounds = scene.Scope.DiscoveryBounds;
                var drawCells = Candidates(parts, drawBounds, scene.Source.Data.TileSize, cellMetres, admission, pairs, ref candidates);
                using var expandedStorage = admission.Workspace();
                var cells = drawCells;
                if (scene.Scope.MotionPaddingMetres > 0)
                {
                    // Motion selects potential draw cells first. Their complete extents then select the
                    // separate clip dependencies, including cells beyond the padded draw window.
                    var expanded = MapPreparationCollections.CopySet(cells, expandedStorage, out long expandedBytes, entryBytes: 128);
                    foreach (var selected in drawCells)
                    {
                        using var neighbourhoodPreparation = admission.BeginPreparation();
                        double padding = scene.Scope.MotionPaddingMetres;
                        var bounds = new MapBox3(selected.Min.X.ToDouble() - padding, -float.MaxValue, selected.Min.Z.ToDouble() - padding,
                            selected.Max.X.ToDouble() + padding, float.MaxValue, selected.Max.Z.ToDouble() + padding);
                        if (!double.IsFinite(bounds.MinX) || !double.IsFinite(bounds.MinZ) || !double.IsFinite(bounds.MaxX) || !double.IsFinite(bounds.MaxZ))
                            throw new MapExactOverflowException();
                        var nearby = index.Overlapping(new(Point(bounds.MinX, bounds.MinZ), Point(bounds.MaxX, bounds.MaxZ)), pairs, admission);
                        admission.ReserveBytes(checked(128L + nearby.Count * 8L));
                        foreach (var neighbour in Candidates(nearby.Select(i => parts[i]).ToArray(), bounds, scene.Source.Data.TileSize,
                            cellMetres, admission, pairs, ref candidates))
                        {
                            if (expanded.Contains(neighbour)) continue;
                            MapPreparationCollections.Set(expanded, expandedStorage, ref expandedBytes, entryBytes: 128);
                            expanded.Add(neighbour);
                        }
                    }
                    cells = expanded;
                }
                foreach (var cell in cells.OrderBy(c => c.Owner.X).ThenBy(c => c.Owner.Z).ThenBy(c => c.X).ThenBy(c => c.Z))
                {
                    using var cellPreparation = admission.BeginPreparation();
                    int cellStart = allCells.Count;
                    // All pieces of the body participate, including pieces entirely outside the caller window.
                    // Candidate lookup avoids a full body-pieces traversal for every selected lattice cell.
                    var intersecting = index.Overlapping(new(cell.Min, cell.Max), pairs, admission);
                    pairs.Charge(intersecting.Count);
                    var clipped = new List<MapWideXz[]>();
                    foreach (int partIndex in intersecting)
                    {
                        var part = parts[partIndex];
                        admission.ReserveBytes(checked(64L + 48L * (part.Count + 4)));
                        var polygon = MapWaterPolygons.ClipToRect(part, cell.Min, cell.Max);
                        if (polygon.Count != 0) clipped.Add(polygon.ToArray());
                    }
                    if (clipped.Count == 0) continue;
                    admission.ReserveBytes(checked(256L + 128L * clipped.Count));
                    var domain = new MapWaterDomain(clipped.ToArray(), Enumerable.Repeat("canonical cell", clipped.Count).ToArray());
                    var scope = DependencyScope(scene, cell);
                    var acquired = scene.AcquireDependencies(scope);
                    witnesses.Add(acquired.Witness);
                    if (!acquired.Witness.CompleteForScope || acquired.Scene is null)
                        return Refuse(acquired.Status is MapSceneStatus.Missing ? MapSceneStatus.Incomplete : acquired.Status,
                            string.Join("\n", acquired.Findings), acquired.Capacity);
                    var compiled = MapWaterCompiler.CompileCell(acquired.Scene.SurfaceView, water!, body, effective with { Domain = domain }, limits);
                    if (compiled.Status != MapWaterCompileStatus.Complete)
                        return Refuse(compiled.Status switch
                        {
                            MapWaterCompileStatus.CapacityExceeded => MapSceneStatus.CapacityExceeded,
                            MapWaterCompileStatus.NotRepresentable => MapSceneStatus.NotRepresentable,
                            MapWaterCompileStatus.Invalid => MapSceneStatus.Invalid,
                            _ => MapSceneStatus.Incomplete,
                        }, compiled.Capacity?.Message ?? compiled.Detail ?? "Water cell compilation refused.", compiled.Capacity);
                    foreach (var group in compiled.Regions.GroupBy(r => (r.SpaceId, Fragment: r.Fragment?.Id ?? ""))
                        .OrderBy(g => g.Key.SpaceId, StringComparer.Ordinal).ThenBy(g => g.Key.Fragment, StringComparer.Ordinal))
                    {
                        var key = new MapWaterCellKey(body.Id, group.Key.SpaceId, group.Key.Fragment, cell.Owner, cell.X, cell.Z, geometry);
                        var regions = new List<MapWaterRegion>();
                        foreach (var kind in group.GroupBy(r => r.Kind).OrderBy(g => g.Key))
                        {
                            admission.ReserveBytes(checked(8L * kind.Sum(r => r.Triangles.Count)));
                            var triangles = MapWaterCellGeometry.Union(kind.SelectMany(r => r.Triangles).ToArray(), compiled.SurfaceY, pairs, admission,
                                checked((int)(MaxEmittedVertices - vertices)));
                            vertices = checked(vertices + 3L * triangles.Count);
                            if (vertices > MaxEmittedVertices) throw new MapWaterCapacityException("water emitted vertices");
                            regions.Add(new(group.Key.SpaceId, kind.Key, triangles) { Fragment = kind.First().Fragment });
                        }
                        var id = new MapSceneResourceKey("WaterCell", CellId(key));
                        admission.BeforeResource(id); cellKeys.Add(id);
                        var canonical = new MapWaterResourceCell(key, compiled.SurfaceY, compiled.Medium, compiled.RenderMaterial,
                            regions, Digest(key, compiled, regions))
                        { CellBounds = new(cell.Min.X.ToDouble(), compiled.SurfaceY, cell.Min.Z.ToDouble(), cell.Max.X.ToDouble(), compiled.SurfaceY, cell.Max.Z.ToDouble()) };
                        allCells.Add(canonical);
                        // Membership is evaluated against the already complete canonical payload.
                        if (drawCells.Contains(cell) && Intersects(canonical, drawBounds, pairs, admission)) output.Add(canonical);
                    }
                    cellPreparation.Complete(allCells.Skip(cellStart)
                        .Sum(c => 256L + c.Regions.Sum(r => 128L + 144L * r.Triangles.Count)), retainDependencies: false);
                }
                bodyPreparation.Complete(allCells.Skip(bodyStart)
                    .Sum(c => 256L + c.Regions.Sum(r => 128L + 144L * r.Triangles.Count)));
            }
            return Result(MapSceneStatus.CompleteForScope, output);
        }
        catch (Exception error) when (error is MapSceneCapacityException or MapWaterCapacityException)
        { return Refuse(MapSceneStatus.CapacityExceeded, MapSceneCapacity.From(error)?.Message ?? error.Message, MapSceneCapacity.From(error)); }
        catch (Exception error) when (error is MapExactOverflowException or OverflowException)
        { return Refuse(MapSceneStatus.NotRepresentable, "Water cell coordinates are not representable."); }
        catch (MapDocumentException error) { return Refuse(MapSceneStatus.Invalid, error.Message); }

        MapScopedWaterResult Refuse(MapSceneStatus status, string detail, MapSceneCapacity? capacity = null) => Result(status, []) with { Capacity = capacity, Findings = Array.AsReadOnly(new[] { detail }) };
        MapScopedWaterResult Result(MapSceneStatus status, IReadOnlyList<MapWaterResourceCell> cells)
        {
            bool complete = status == MapSceneStatus.CompleteForScope;
            // Failure witnesses are control-plane diagnostics, outside the exhausted preparation attempt.
            using IDisposable? diagnostic = complete ? null : MapSceneAdmission.Suspend();
            var required = witnesses.SelectMany(w => w.Required).Concat(cellKeys).Distinct().OrderBy(k => k.Kind, StringComparer.Ordinal).ThenBy(k => k.Id, StringComparer.Ordinal).ToArray();
            var present = witnesses.SelectMany(w => w.Present).Concat(complete ? cellKeys : []).Distinct().OrderBy(k => k.Kind, StringComparer.Ordinal).ThenBy(k => k.Id, StringComparer.Ordinal).ToArray();
            var acquired = scene.Witness with
            {
                ScopeDigest = MapCanonical.HashHex(w =>
                {
                    w.WriteStartArray(); w.WriteStringValue(geometry);
                    foreach (var witness in witnesses.OrderBy(w => w.ScopeDigest, StringComparer.Ordinal)) w.WriteStringValue(witness.ScopeDigest);
                    foreach (var key in cellKeys.OrderBy(k => k.Id, StringComparer.Ordinal)) w.WriteStringValue(key.Id); w.WriteEndArray();
                }),
                Required = Array.AsReadOnly(required),
                Present = Array.AsReadOnly(present),
                KnownEmpty = complete ? Array.AsReadOnly(witnesses.SelectMany(w => w.KnownEmpty).Distinct().ToArray()) : [],
                Unavailable = Array.AsReadOnly(witnesses.SelectMany(w => w.Unavailable).Distinct().ToArray()),
                CompleteForScope = complete,
                WholeWorldComplete = false,
            };
            var requested = scene.Witness with
            {
                CompleteForScope = complete,
                WholeWorldComplete = false,
                Required = Array.AsReadOnly(scene.Witness.Required.Concat(required).Distinct().ToArray()),
                Present = Array.AsReadOnly(scene.Witness.Present.Concat(present).Distinct().ToArray()),
                KnownEmpty = complete ? scene.Witness.KnownEmpty : [],
                Unavailable = acquired.Unavailable
            };
            long retained = complete ? checked(512L + 64L * (required.Length + present.Length) +
                allCells.Sum(c => 256L + c.Regions.Sum(r => 128L + 144L * r.Triangles.Count))) : 0;
            using var owned = complete ? preparation.Complete(retained, publishToParent: false) : null;
            var ownership = complete ? MapPreparationInventory.Acquire(admission,
                allCells.Select(c => ((object)c, MapScopedWaterResult.CellInventoryBytes(c)))
                    .Append((new object(), checked(retained - allCells.Sum(MapScopedWaterResult.CellInventoryBytes)))),
                ownership: owned) : null;
            if (ownership is not null) preparation.TrackParentCleanup(ownership);
            return new(status, Array.AsReadOnly(cells.ToArray()), requested, acquired)
            {
                Limits = scene.Source.Data.Limits,
                AcquiredCells = complete ? Array.AsReadOnly(allCells.ToArray()) : [],
                MotionPaddingMetres = scene.Scope.MotionPaddingMetres,
                CandidateCells = candidates,
                PairChecks = pairs.Charged,
                PreparationOwnership = ownership,
                PreparationChargeBytes = retained
            };
        }
    }

    static HashSet<Cell> Candidates(IReadOnlyList<IReadOnlyList<MapWideXz>> parts, MapBox3 window, float tileSize,
        float size, MapSceneAdmission admission, MapWaterWorkBudget pairs, ref long candidates)
    {
        using var preparation = admission.BeginPreparation();
        var result = new HashSet<Cell>();
        foreach (var part in parts)
        {
            admission.ReserveBytes(checked(64L + 48L * (part.Count + 4)));
            var clipped = MapWaterPolygons.ClipToRect(part, Point(window.MinX, window.MinZ), Point(window.MaxX, window.MaxZ));
            if (clipped.Count == 0) continue;
            var bounds = MapWaterPolygons.Bounds.Of(clipped);
            long x0 = Floor(bounds.Min.X.ToDouble() / size), x1 = Ceiling(bounds.Max.X.ToDouble() / size);
            long z0 = Floor(bounds.Min.Z.ToDouble() / size), z1 = Ceiling(bounds.Max.Z.ToDouble() / size);
            long count = checked((x1 - x0) * (z1 - z0));
            candidates = checked(candidates + count);
            if (candidates > MaxCells) throw new MapWaterCapacityException("water candidate cells");
            admission.ReserveBytes(checked(count * 128)); pairs.Charge(count);
            for (long z = z0; z < z1; z++)
                for (long x = x0; x < x1; x++)
                {
                    double minX = x * (double)size, minZ = z * (double)size;
                    double maxX = (x + 1) * (double)size, maxZ = (z + 1) * (double)size;
                    int tx0 = checked((int)Floor(minX / tileSize)), tx1 = checked((int)Ceiling(maxX / tileSize));
                    int tz0 = checked((int)Floor(minZ / tileSize)), tz1 = checked((int)Ceiling(maxZ / tileSize));
                    long owners = checked(((long)tx1 - tx0) * ((long)tz1 - tz0));
                    if (owners > MaxCells - result.Count) throw new MapWaterCapacityException("water storage cells");
                    pairs.Charge(owners); admission.ReserveBytes(checked(owners * 128));
                    for (int tz = tz0; tz < tz1; tz++)
                        for (int tx = tx0; tx < tx1; tx++)
                        {
                            var min = Point(Math.Max(minX, tx * (double)tileSize), Math.Max(minZ, tz * (double)tileSize));
                            var max = Point(Math.Min(maxX, (tx + 1d) * tileSize), Math.Min(maxZ, (tz + 1d) * tileSize));
                            if (MapWaterPolygons.ClipToRect(clipped, min, max).Count != 0) result.Add(new(new(tx, tz), x, z, min, max));
                        }
                }
        }
        preparation.Complete(checked(128L * result.Count));
        return result;
    }

    static MapSceneScope DependencyScope(MapScopedScene scene, Cell cell)
    {
        var frame = WorldFrame.Nearest((float)cell.Min.X.ToDouble(), (float)cell.Min.Z.ToDouble());
        double ax = (double)frame.X * WorldFrame.Grid, az = (double)frame.Z * WorldFrame.Grid;
        return new(new(frame, new(LocalBound(cell.Min.X.ToDouble() - ax, true), LocalBound(cell.Min.Z.ToDouble() - az, true)),
            new(LocalBound(cell.Max.X.ToDouble() - ax, false), LocalBound(cell.Max.Z.ToDouble() - az, false)), null, null,
            Enum.GetValues<MapSurfaceRole>(), null, scene.Scope.Surfaces.Limits),
            new(cell.Min.X.ToDouble(), -float.MaxValue, cell.Min.Z.ToDouble(), cell.Max.X.ToDouble(), float.MaxValue, cell.Max.Z.ToDouble()), MapSceneChannels.Water);
    }

    static float LocalBound(double value, bool lower)
    {
        float result = (float)value;
        if (!float.IsFinite(result)) throw new MapExactOverflowException();
        return lower && result > value ? MathF.BitDecrement(result) : !lower && result < value ? MathF.BitIncrement(result) : result;
    }

    static bool Intersects(MapWaterResourceCell cell, MapBox3 window, MapWaterWorkBudget pairs, MapSceneAdmission admission)
    {
        using var workspace = admission.Workspace();
        foreach (var triangle in cell.Regions.SelectMany(r => r.Triangles))
        {
            pairs.Charge(1); workspace.Reserve(512);
            try
            {
                if (MapWaterPolygons.ClipToRect(new[] { MapWidePoint.From(triangle.A).Xz, MapWidePoint.From(triangle.C).Xz, MapWidePoint.From(triangle.B).Xz },
                    Point(window.MinX, window.MinZ), Point(window.MaxX, window.MaxZ)).Count != 0) return true;
            }
            finally { workspace.Release(512); }
        }
        return false;
    }

    internal static string CellId(MapWaterCellKey key) => MapWaterJson.Hash("kemap/water-cell-key/1", w =>
    {
        w.WriteStartArray(); w.WriteStringValue(key.BodyId); w.WriteStringValue(key.SpaceId); w.WriteStringValue(key.FragmentId);
        w.WriteNumberValue(key.StorageOwner.X); w.WriteNumberValue(key.StorageOwner.Z); w.WriteNumberValue(key.X); w.WriteNumberValue(key.Z);
        w.WriteStringValue(key.GeometryPolicy); w.WriteEndArray();
    });
    static string Digest(MapWaterCellKey key, MapCompiledWaterBody body, IReadOnlyList<MapWaterRegion> regions) => MapWaterJson.Hash("kemap/water-cell/1", w =>
    {
        w.WriteStartArray(); w.WriteStringValue(CellId(key)); w.WriteNumberValue(BitConverter.SingleToInt32Bits(body.SurfaceY));
        w.WriteStringValue(MapWaterSet.MediumDigest(body.Medium)); w.WriteStringValue(body.RenderMaterial);
        foreach (var region in regions)
        {
            w.WriteNumberValue((byte)region.Kind);
            foreach (var t in region.Triangles) foreach (var p in new[] { t.A, t.B, t.C })
            { w.WriteStringValue(p.X.ToString()); w.WriteStringValue(p.Y.ToString()); w.WriteStringValue(p.Z.ToString()); }
        }
        w.WriteEndArray();
    });
    static MapWideXz Point(double x, double z) => new(MapWideRational.FromDouble(x), MapWideRational.FromDouble(z));
    static long Floor(double value) => checked((long)Math.Floor(value));
    static long Ceiling(double value) => checked((long)Math.Ceiling(value));
}
