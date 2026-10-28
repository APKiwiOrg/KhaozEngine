using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Foliage;

/// <summary>A capture outcome. Complete always carries a context, a refusal never does.</summary>
public sealed record MapFoliageContextResult(MapFoliageStatus Status, MapFoliageGenerationContext? Context, MapFoliageContextWork Work)
{
    public Scene.MapSceneCapacity? Capacity { get; init; }
}

/// <summary>One immutable foliage generation input per accepted document snapshot. Capture admits its budget from
/// metadata, expands the effective resources exactly once, then copies the foliage and indexes owner and paint
/// metadata spatially. Every chunk-scoped read is answered from those captured facts, never by another expansion or a
/// whole payload serialization, so one context serves any number of Generate and Query calls. A later document edit
/// needs a new context and never reaches this one. A partially resident document refuses, since generation needs every
/// tile, surface page and foliage chunk loaded. Windowed acquisition uses the separate MapWindowFoliageContext.</summary>
public sealed class MapFoliageGenerationContext : IMapFoliageInputs
{
    [ThreadStatic] static long t_captures;
    readonly MapFoliageInputKernel _inputs;
    MapFoliageGenerationContext(MapFoliageSet? foliage, MapAssetClosure assets, MapEffectiveResources resources, MapFoliageContextWork work,
        string sourceFingerprint, MapQueryLimits? queryLimits = null)
    {
        SourceFingerprint = sourceFingerprint;
        ClosureHash = assets.Hash;
        Foliage = foliage;
        Assets = assets;
        Resources = resources;
        Work = work;
        _inputs = new(foliage, assets, resources.Product, queryLimits: queryLimits);
        SnapshotId = _inputs.SnapshotId;
        t_captures++;
    }

    /// <summary>The contexts this thread constructed, counted once per completed construction and never per read.</summary>
    internal static long ThreadCaptures => t_captures;

    /// <summary>The captured foliage, detached from the source document. Null when the document authors none.</summary>
    public MapFoliageSet? Foliage { get; }
    /// <summary>The verified closure the snapshot was captured against.</summary>
    public MapAssetClosure Assets { get; }
    /// <summary>The effective resources of the snapshot, expanded once.</summary>
    public MapEffectiveResources Resources { get; }
    internal MapFoliageOwners ClearanceOwners => _inputs.ClearanceOwners;
    /// <summary>Identifies the captured snapshot from facts already captured: effective resources, annotations, the
    /// closure, logical foliage, every effective placement and the paint partition.</summary>
    public string SnapshotId { get; }
    /// <summary>The construction work, fixed at capture.</summary>
    public MapFoliageContextWork Work { get; }
    /// <summary>The fingerprint of the document this context was captured from, foliage included.</summary>
    internal string SourceFingerprint { get; }
    /// <summary>The hash of the verified closure this context was captured against.</summary>
    internal string ClosureHash { get; }

    /// <summary>Captures a fully loaded document, reading it without editing it. Refuses before expansion when the
    /// document is partially resident, its preflight demand exceeds <paramref name="limits"/> or its foliage does not
    /// bind to the closure.</summary>
    public static MapFoliageContextResult Capture(MapDocument document, MapAssetClosure assets, MapFoliageContextLimits? limits = null) =>
        Capture(document, assets, limits, null);

    /// <summary>Captures complete inputs under caller context and surface query allowances.</summary>
    public static MapFoliageContextResult Capture(MapDocument document, MapAssetClosure assets, MapFoliageContextLimits? limits,
        MapQueryLimits? queryLimits) => Capture(document, assets, limits, queryLimits, null);

    /// <summary>Captures complete inputs under caller context, surface query and prefab expansion allowances. A null
    /// allowance keeps the engine default. The preflight's prefab count and the expansion both take
    /// <paramref name="expansionLimits"/>.</summary>
    public static MapFoliageContextResult Capture(MapDocument document, MapAssetClosure assets, MapFoliageContextLimits? limits,
        MapQueryLimits? queryLimits, MapPrefabExpansionLimits? expansionLimits)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(assets);
        long expansions = MapPrefabResolver.ThreadExpansions, writes = MapFoliageJson.ThreadPayloadWrites;
        MapFoliageContextMeasure measure = MapFoliageContextPreflight.Measure(document, assets, limits ?? new(), expansionLimits);
        if (measure.Status != MapFoliageStatus.Complete) return new(measure.Status, null, measure.Work) { Capacity = measure.Capacity };
        if (MapFoliageValidation.ValidateBound(document, assets).Count != 0) return new(MapFoliageStatus.Invalid, null, measure.Work);
        MapEffectiveResources resources;
        try { resources = MapPrefabResolver.Expand(document, assets, expansionLimits ?? new(), null, null, queryLimits); }
        catch (Exception error) when (Refusal(error) is { } status) { return new(status, null, Observed(measure.Work, expansions, writes)); }
        string baseline = resources.SourceFingerprint ?? Editing.MapDocumentEditFinalizer.BaseFingerprint(document);
        return Build(document, assets, resources, measure.Work, expansions, writes, baseline, queryLimits);
    }

    /// <summary>Captures exact resources a pure or native transaction already expanded for this document and closure,
    /// without another expansion. The transaction measures before its own expansion. This verifies the provenance the
    /// expansion recorded, its closure hash and the fingerprint without foliage of the document it was expanded from,
    /// checks that the resources correspond to the document's placements and declared surfaces, then runs the context
    /// preflight again before staging. It is not a route to inject unrelated resources. <paramref name="baseline"/> is the
    /// document's fingerprint without foliage when the caller already computed it from exactly this document, so it is
    /// not hashed again. The preflight's prefab count takes <paramref name="expansionLimits"/>. A non-null
    /// <paramref name="queryLimits"/> caps every context acquisition, as in
    /// <see cref="Capture(MapDocument, MapAssetClosure, MapFoliageContextLimits?, MapQueryLimits?)"/>.</summary>
    internal static MapFoliageContextResult CaptureEffective(MapDocument document, MapAssetClosure assets, MapEffectiveResources resources,
        MapFoliageContextLimits? limits = null, string? baseline = null, MapQueryLimits? queryLimits = null,
        MapPrefabExpansionLimits? expansionLimits = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(resources);
        long expansions = MapPrefabResolver.ThreadExpansions, writes = MapFoliageJson.ThreadPayloadWrites;
        MapFoliageContextMeasure measure = MapFoliageContextPreflight.Measure(document, assets, limits ?? new(), expansionLimits);
        if (measure.Status != MapFoliageStatus.Complete) return new(measure.Status, null, measure.Work) { Capacity = measure.Capacity };
        if (MapFoliageValidation.ValidateBound(document, assets).Count != 0 || !Corresponds(document, assets, resources))
            return new(MapFoliageStatus.Invalid, null, measure.Work);
        // A snapshot expanded from another document or closure never stands in for this one. An expansion of a document
        // without foliage records no fingerprint and serves only that document's own empty capture.
        baseline ??= Editing.MapDocumentEditFinalizer.BaseFingerprint(document);
        if (!string.Equals(resources.ClosureHash, assets.Hash, StringComparison.Ordinal) ||
            (resources.SourceFingerprint is { } recorded ? !string.Equals(recorded, baseline, StringComparison.Ordinal) : document.Foliage is not null))
            return new(MapFoliageStatus.Invalid, null, measure.Work);
        return Build(document, assets, resources, measure.Work, expansions, writes, baseline, queryLimits);
    }

    /// <summary>Acquires a chunk scope from the captured effective resources.</summary>
    MapQueryLimits? IMapFoliageInputs.QueryLimits => _inputs.QueryLimits;
    public MapScopedSurfaces Acquire(MapSurfaceScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return Resources.Acquire(scope);
    }

    internal IReadOnlyDictionary<MapFoliageChunkKey, string> DensityDigests(MapFoliageLayer layer) => _inputs.DensityDigests(layer);
    internal IReadOnlyList<MapSurfaceRef> SupportFloorsFor(MapFoliageLayer layer, MapFoliageChunkKey chunk) => _inputs.SupportFloorsFor(layer, chunk);
    public IReadOnlyDictionary<string, MapPaintRegionSet> PaintFor(MapSurfaceScope scope) => _inputs.PaintFor(scope);
    public IReadOnlyList<MapResolvedPlacement> PlacementsIn(MapFoliageRect bounds) => _inputs.PlacementsIn(bounds);
    internal IReadOnlyList<MapResolvedPlacement> PlacementsIn(MapFoliageRect bounds, out long candidates) => _inputs.PlacementsIn(bounds, out candidates);
    internal IEnumerable<(MapResolvedPlacement Placement, Bounds Proxy)> OwnerProxies() => _inputs.OwnerProxies();
    IReadOnlyList<MapSurfaceRef> IMapFoliageInputs.DeclaredSurfaces => _inputs.DeclaredSurfaces;
    IReadOnlyDictionary<string, MapEffectiveTransform> IMapFoliageInputs.Transforms => _inputs.Transforms;
    MapFoliageOwners IMapFoliageInputs.ClearanceOwners => ClearanceOwners;
    IReadOnlyDictionary<MapFoliageChunkKey, string> IMapFoliageInputs.DensityDigests(MapFoliageLayer layer) => DensityDigests(layer);
    IReadOnlyList<MapSurfaceRef> IMapFoliageInputs.SupportFloorsFor(MapFoliageLayer layer, MapFoliageChunkKey chunk) => SupportFloorsFor(layer, chunk);
    IReadOnlyList<MapResolvedPlacement> IMapFoliageInputs.PlacementsIn(MapFoliageRect bounds, out long candidates) => PlacementsIn(bounds, out candidates);
    MapEffectiveTransform IMapFoliageInputs.Pose(MapPrefabReference owner) => _inputs.Pose(owner);
    void IMapFoliageInputs.RequireChunk(MapFoliageLayer layer, MapFoliageChunkKey chunk) { }

    /// <summary>Captures foliage for the requested generation window against its pinned scene source.</summary>
    public static MapWindowFoliageContextResult CaptureWindow(Scene.MapSceneSource source, Scene.MapSceneScope scope,
        MapFoliageContextLimits? limits = null) => MapFoliageWindowCapture.Capture(source, scope, limits ?? new());

    static MapFoliageContextResult Build(MapDocument document, MapAssetClosure assets, MapEffectiveResources resources,
        MapFoliageContextWork measured, long expansions, long writes, string baseline, MapQueryLimits? queryLimits = null)
    {
        // Copying and indexing neither expand nor serialize, so the observed work is final before staging begins.
        MapFoliageContextWork work = Observed(measured, expansions, writes);
        string fingerprint = Editing.MapDocumentEditFinalizer.Fingerprint(baseline, document.Foliage);
        try { return new(MapFoliageStatus.Complete, new(document.Foliage?.Clone(), assets, resources, work, fingerprint, queryLimits), work); }
        catch (Exception error) when (Refusal(error) is { } status) { return new(status, null, work); }
    }

    static MapFoliageContextWork Observed(MapFoliageContextWork measured, long expansions, long writes) => measured with
    {
        Expansions = MapPrefabResolver.ThreadExpansions - expansions,
        SnapshotSerializations = MapFoliageJson.ThreadPayloadWrites - writes,
    };

    static MapFoliageStatus? Refusal(Exception error) => error switch
    {
        MapSurfaceCapacityException => MapFoliageStatus.CapacityExceeded,
        MapExactOverflowException or OverflowException => MapFoliageStatus.NotRepresentable,
        MapDocumentException when MapCommonRefinement.NotRepresentable(error) => MapFoliageStatus.NotRepresentable,
        MapDocumentException { Message: var message } when message.Contains("limit exceeded", StringComparison.Ordinal) => MapFoliageStatus.CapacityExceeded,
        MapDocumentException => MapFoliageStatus.Invalid,
        _ => null,
    };

    // The resources must hold every authored placement under its own identity, only placements the document can
    // produce, only assets the closure verifies, and every declared surface.
    static bool Corresponds(MapDocument document, MapAssetClosure assets, MapEffectiveResources resources)
    {
        var effective = new Dictionary<string, MapResolvedPlacement>(StringComparer.Ordinal);
        foreach (MapResolvedPlacement placement in resources.Placements)
            if (!effective.TryAdd(placement.PlacementId, placement)) return false;
        foreach (MapPlacement placement in document.Placements)
            if (!effective.TryGetValue(placement.Id, out MapResolvedPlacement? found) || found.NumericId != placement.NumericId ||
                !string.Equals(found.Kind, placement.Kind, StringComparison.Ordinal)) return false;
        var producible = new HashSet<string>(document.Placements.Select(p => p.Id), StringComparer.Ordinal);
        if (document.Prefabs is { } set)
        {
            foreach (MapPrefabInstance instance in set.Instances) foreach (MapPrefabBinding binding in instance.Bindings.Entries) producible.Add(binding.WorldId);
            foreach (MapPrefabBindingTransfer transfer in set.Transfers) producible.Add(transfer.Binding.WorldId);
            foreach (MapPrefabAttachmentTransfer attachment in set.Attachments) foreach (MapPrefabAttachedChild child in attachment.Children) producible.Add(child.WorldId);
        }
        var assetIds = new HashSet<string>(assets.Assets.Select(a => a.Id), StringComparer.Ordinal);
        if (resources.Placements.Any(p => !producible.Contains(p.PlacementId) || !assetIds.Contains(p.AssetId))) return false;
        var surfaces = new HashSet<string>(resources.SurfaceSource.Surfaces.Select(s => s.Id), StringComparer.Ordinal);
        return document.Surfaces.Refs.All(s => surfaces.Contains(s.Id));
    }

    // A local footprint through the effective pose, as MapTransform.TransformPoint applies it, padded outward by a
    // millimetre so exact downstream arithmetic never falls outside it. A Door footprint first widens to include the
    // owner's origin, then by the door clearance in owner units, which the pose scales once.
    internal static Bounds Proxy(MapEffectiveTransform pose, MapFoliageRect footprint, float? doorClearance)
    {
        double minX = footprint.MinX, minZ = footprint.MinZ, maxX = footprint.MaxX, maxZ = footprint.MaxZ;
        if (doorClearance is { } reach)
        {
            minX = Math.Min(minX, 0) - reach; minZ = Math.Min(minZ, 0) - reach;
            maxX = Math.Max(maxX, 0) + reach; maxZ = Math.Max(maxZ, 0) + reach;
        }
        double c = Math.Cos(pose.YawRadians), s = Math.Sin(pose.YawRadians);
        Bounds? result = null;
        foreach (var (x, z) in new[] { (minX, minZ), (maxX, minZ), (maxX, maxZ), (minX, maxZ) })
        {
            double wx = pose.X + (c * x + s * z) * pose.Scale, wz = pose.Z + (-s * x + c * z) * pose.Scale;
            result = Bounds.Union(result, new Bounds(wx, wz, wx, wz));
        }
        Bounds b = result!.Value;
        double pad = 1e-3 + 1e-9 * Math.Max(Math.Max(Math.Abs(b.MinX), Math.Abs(b.MaxX)), Math.Max(Math.Abs(b.MinZ), Math.Abs(b.MaxZ)));
        if (!double.IsFinite(b.MinX) || !double.IsFinite(b.MinZ) || !double.IsFinite(b.MaxX) || !double.IsFinite(b.MaxZ))
            throw new MapExactOverflowException();
        return new(b.MinX - pad, b.MinZ - pad, b.MaxX + pad, b.MaxZ + pad);
    }

    internal readonly record struct Bounds(double MinX, double MinZ, double MaxX, double MaxZ)
    {
        internal static Bounds Union(Bounds? a, Bounds b) => a is not { } x ? b :
            new(Math.Min(x.MinX, b.MinX), Math.Min(x.MinZ, b.MinZ), Math.Max(x.MaxX, b.MaxX), Math.Max(x.MaxZ, b.MaxZ));
        // A conservative double envelope of an exact rectangle, for bucketing only. Exact tests decide membership.
        internal static Bounds Of(MapExactRect rect)
        {
            double minX = rect.MinX.ToDouble(), minZ = rect.MinZ.ToDouble(), maxX = rect.MaxX.ToDouble(), maxZ = rect.MaxZ.ToDouble();
            double pad = 1e-3 + 1e-9 * Math.Max(Math.Max(Math.Abs(minX), Math.Abs(maxX)), Math.Max(Math.Abs(minZ), Math.Abs(maxZ)));
            return new(minX - pad, minZ - pad, maxX + pad, maxZ + pad);
        }
        internal bool Meets(Bounds b) => MinX <= b.MaxX && MaxX >= b.MinX && MinZ <= b.MaxZ && MaxZ >= b.MinZ;
    }

    // A bucket hierarchy. Level k has square buckets of 32 * 8^k metres. Each entry lives at the finest level where it
    // spans at most 64 buckets, so small and wide entries alike are bucketed and nothing is scanned unconditionally. At
    // each occupied level a query walks its own bucket range, or the occupied buckets when there are fewer. Both walks
    // inspect the same entries: those bucketed inside the query's range. Results are complete, since an entry meeting
    // the query shares a bucket with it at the entry's own level, and entries far from the query are never inspected.
    internal sealed class SpatialIndex
    {
        const double Cell = 32;
        const double Branch = 8;
        const long MaxSpanCells = 64;
        readonly Bounds[] _bounds;
        readonly List<Dictionary<(long X, long Z), List<int>>> _levels = new();

        internal SpatialIndex(IReadOnlyList<Bounds> bounds)
        {
            _bounds = bounds.ToArray();
            for (int i = 0; i < _bounds.Length; i++)
            {
                int level = 0;
                var (x0, z0, x1, z1) = Range(_bounds[i], level);
                while (Cells(x0, z0, x1, z1) > MaxSpanCells) (x0, z0, x1, z1) = Range(_bounds[i], ++level);
                while (_levels.Count <= level) _levels.Add(new());
                foreach (var cell in Walk(x0, z0, x1, z1))
                {
                    if (!_levels[level].TryGetValue(cell, out var list)) _levels[level].Add(cell, list = new());
                    list.Add(i);
                }
            }
        }

        /// <summary>Every entry meeting <paramref name="query"/>, with the count of distinct entries inspected.</summary>
        internal List<int> Query(Bounds query, out long candidates)
        {
            var seen = new HashSet<int>();
            var hits = new List<int>();
            for (int level = 0; level < _levels.Count; level++)
            {
                Dictionary<(long X, long Z), List<int>> cells = _levels[level];
                if (cells.Count == 0) continue;
                var (x0, z0, x1, z1) = Range(query, level);
                if (Cells(x0, z0, x1, z1) > cells.Count)
                {
                    foreach (var (cell, list) in cells)
                        if (cell.X >= x0 && cell.X <= x1 && cell.Z >= z0 && cell.Z <= z1) foreach (int i in list) Inspect(i);
                }
                else
                {
                    foreach (var cell in Walk(x0, z0, x1, z1))
                        if (cells.TryGetValue(cell, out var list)) foreach (int i in list) Inspect(i);
                }
            }
            candidates = seen.Count;
            return hits;

            void Inspect(int i)
            {
                if (seen.Add(i) && _bounds[i].Meets(query)) hits.Add(i);
            }
        }

        // Bucket counts and walks stay exact at saturated bucket indices.
        static double Cells(long x0, long z0, long x1, long z1) => ((double)x1 - x0 + 1) * ((double)z1 - z0 + 1);

        static IEnumerable<(long X, long Z)> Walk(long x0, long z0, long x1, long z1)
        {
            for (long z = z0; ; z++)
            {
                for (long x = x0; ; x++)
                {
                    yield return (x, z);
                    if (x == x1) break;
                }
                if (z == z1) break;
            }
        }

        static (long, long, long, long) Range(Bounds b, int level)
        {
            double size = Cell * Math.Pow(Branch, level);
            return ((long)Math.Floor(b.MinX / size), (long)Math.Floor(b.MinZ / size), (long)Math.Floor(b.MaxX / size), (long)Math.Floor(b.MaxZ / size));
        }
    }
}
