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
public sealed record MapFoliageContextResult(MapFoliageStatus Status, MapFoliageGenerationContext? Context, MapFoliageContextWork Work);

/// <summary>One immutable foliage generation input per accepted document snapshot. Capture admits its budget from
/// metadata, expands the effective resources exactly once, then copies the foliage and indexes owner and paint
/// metadata spatially. Every chunk-scoped read is answered from those captured facts, never by another expansion or a
/// whole payload serialization, so one context serves any number of Generate and Query calls. A later document edit
/// needs a new context and never reaches this one. A partially resident document refuses, since generation needs every
/// tile, surface page and foliage chunk loaded. A windowed context over partial residency is a later addition.</summary>
public sealed class MapFoliageGenerationContext
{
    [ThreadStatic] static long t_captures;
    readonly SpatialIndex _owners;
    readonly MapResolvedPlacement[] _ownerPlacements;
    readonly Bounds[] _ownerBounds;
    readonly SpatialIndex _paint;
    readonly SpatialIndex _floorIndex;
    readonly MapSurfaceRef[] _floorEntries;
    readonly IReadOnlyDictionary<string, IReadOnlyDictionary<MapFoliageChunkKey, string>> _densityDigests;
    readonly PaintEntry[] _paintEntries;

    MapFoliageGenerationContext(MapFoliageSet? foliage, MapAssetClosure assets, MapEffectiveResources resources, MapFoliageContextWork work,
        string sourceFingerprint)
    {
        SourceFingerprint = sourceFingerprint;
        ClosureHash = assets.Hash;
        Foliage = foliage;
        _densityDigests = (foliage?.Layers ?? []).ToDictionary(l => l.Id,
            l => (IReadOnlyDictionary<MapFoliageChunkKey, string>)new ReadOnlyDictionary<MapFoliageChunkKey, string>(
                l.ChunkDigests.ToDictionary(d => d.Key, d => d.Sha256)), StringComparer.Ordinal);
        Assets = assets;
        Resources = resources;
        Work = work;
        (_ownerPlacements, _ownerBounds, _owners) = Owners(foliage, resources);
        (_paintEntries, _paint) = Paint(resources);
        (_floorEntries, _floorIndex) = SupportInfluences(resources);
        ClearanceOwners = new(foliage, resources);
        SnapshotId = Snapshot(foliage, assets, resources, _paintEntries);
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
    internal MapFoliageOwners ClearanceOwners { get; }
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
    public static MapFoliageContextResult Capture(MapDocument document, MapAssetClosure assets, MapFoliageContextLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(assets);
        long expansions = MapPrefabResolver.ThreadExpansions, writes = MapFoliageJson.ThreadPayloadWrites;
        MapFoliageContextMeasure measure = MapFoliageContextPreflight.Measure(document, assets, limits ?? new());
        if (measure.Status != MapFoliageStatus.Complete) return new(measure.Status, null, measure.Work);
        if (MapFoliageValidation.ValidateBound(document, assets).Count != 0) return new(MapFoliageStatus.Invalid, null, measure.Work);
        MapEffectiveResources resources;
        try { resources = MapPrefabResolver.Expand(document, assets); }
        catch (Exception error) when (Refusal(error) is { } status) { return new(status, null, Observed(measure.Work, expansions, writes)); }
        string baseline = resources.SourceFingerprint ?? Editing.MapDocumentEditFinalizer.BaseFingerprint(document);
        return Build(document, assets, resources, measure.Work, expansions, writes, baseline);
    }

    /// <summary>Captures exact resources a pure or native transaction already expanded for this document and closure,
    /// without another expansion. The transaction measures before its own expansion. This verifies the provenance the
    /// expansion recorded, its closure hash and the fingerprint without foliage of the document it was expanded from,
    /// checks that the resources correspond to the document's placements and declared surfaces, then runs the context
    /// preflight again before staging. It is not a route to inject unrelated resources. <paramref name="baseline"/> is the
    /// document's fingerprint without foliage when the caller already computed it from exactly this document, so it is
    /// not hashed again.</summary>
    internal static MapFoliageContextResult CaptureEffective(MapDocument document, MapAssetClosure assets, MapEffectiveResources resources,
        MapFoliageContextLimits? limits = null, string? baseline = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(resources);
        long expansions = MapPrefabResolver.ThreadExpansions, writes = MapFoliageJson.ThreadPayloadWrites;
        MapFoliageContextMeasure measure = MapFoliageContextPreflight.Measure(document, assets, limits ?? new());
        if (measure.Status != MapFoliageStatus.Complete) return new(measure.Status, null, measure.Work);
        if (MapFoliageValidation.ValidateBound(document, assets).Count != 0 || !Corresponds(document, assets, resources))
            return new(MapFoliageStatus.Invalid, null, measure.Work);
        // A snapshot expanded from another document or closure never stands in for this one. An expansion of a document
        // without foliage records no fingerprint and serves only that document's own empty capture.
        baseline ??= Editing.MapDocumentEditFinalizer.BaseFingerprint(document);
        if (!string.Equals(resources.ClosureHash, assets.Hash, StringComparison.Ordinal) ||
            (resources.SourceFingerprint is { } recorded ? !string.Equals(recorded, baseline, StringComparison.Ordinal) : document.Foliage is not null))
            return new(MapFoliageStatus.Invalid, null, measure.Work);
        return Build(document, assets, resources, measure.Work, expansions, writes, baseline);
    }

    /// <summary>Acquires a chunk scope from the captured effective resources.</summary>
    public MapScopedSurfaces Acquire(MapSurfaceScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return Resources.Acquire(scope);
    }

    internal IReadOnlyDictionary<MapFoliageChunkKey, string> DensityDigests(MapFoliageLayer layer) => _densityDigests[layer.Id];

    // A captured per-patch world-bounds index. Only reached floors size the normal and clearance halo, so a
    // distant coarse prefab never sizes a local request. Iterate the reached set to closure: a newly reached lattice
    // may enlarge the clearance read chain. Acquisition, grids and clearance still use the single ReadReach recipe.
    internal IReadOnlyList<MapSurfaceRef> SupportFloorsFor(MapFoliageLayer layer, MapFoliageChunkKey chunk)
    {
        var rect = MapFoliageSupport.Rect(layer, chunk);
        var found = new Dictionary<string, MapSurfaceRef>(StringComparer.Ordinal);
        double pad = 0.01;
        while (true)
        {
            int previous = found.Count;
            foreach (int i in _floorIndex.Query(new(rect.MinX - pad, rect.MinZ - pad, rect.MaxX + pad, rect.MaxZ + pad), out _))
                found.TryAdd(_floorEntries[i].Id, _floorEntries[i]);
            if (found.Count == previous) return found.Values.OrderBy(s => s.Id, StringComparer.Ordinal).ToArray();
            var floors = found.Values.Select(s => (Surface: s, Pose: Resources.Transforms.TryGetValue(
                MapEffectiveContext.Key(MapPrefabChildKind.Surface, s.Id), out MapEffectiveTransform t) ? t : (MapEffectiveTransform?)null)).ToArray();
            double cell = floors.Max(f => f.Surface.Frame.CellUnitMetres.Exact().ToDouble() * (f.Pose?.Scale ?? 1));
            pad = 3 * cell + MapFoliageSupport.ReadReach(layer, floors) + 0.01;
        }
    }

    static (MapSurfaceRef[], SpatialIndex) SupportInfluences(MapEffectiveResources resources)
    {
        var entries = new List<MapSurfaceRef>();
        var bounds = new List<Bounds>();
        if (resources.SurfaceSource is not IMapEffectiveSurfaceContext source)
            throw new MapDocumentException("foliage context requires captured effective surface bounds");
        MapEffectiveContext effective = source.EffectiveContext;
        var floors = effective.Surfaces.Refs.Where(s => s.Role == MapSurfaceRole.SupportFloor).ToDictionary(s => s.Id, StringComparer.Ordinal);
        foreach (MapSurfacePatch patch in effective.Surfaces.Patches.Values)
        {
            if (!floors.TryGetValue(patch.Key.SurfaceId, out MapSurfaceRef? surface)) continue;
            long x = checked(patch.Key.SlotX * 64 + patch.CellMinX), z = checked(patch.Key.SlotZ * 64 + patch.CellMinZ);
            MapExactRect rect = MapEffectiveGeometry.Bounds(new[] { MapLatticeAddress.Corner(x, z), MapLatticeAddress.Corner(x + patch.Width, z),
                MapLatticeAddress.Corner(x + patch.Width, z + patch.Depth), MapLatticeAddress.Corner(x, z + patch.Depth) }
                .Select(a => MapEffectiveGeometry.Vertex(effective, new(surface.Id, a))));
            Bounds b = Bounds.Of(rect);
            entries.Add(surface);
            bounds.Add(b);
        }
        return (entries.ToArray(), new SpatialIndex(bounds));
    }

    /// <summary>The captured paint partition restricted to the scope: per target, the regions whose exact XZ bounds
    /// touch the scope's world rectangle, in published order. Targets with no such region are absent. Each subset has
    /// its own digest over only its regions.</summary>
    public IReadOnlyDictionary<string, MapPaintRegionSet> PaintFor(MapSurfaceScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        scope.Validate();
        MapExactRect rect = scope.WorldRectangle();
        List<int> hits = _paint.Query(Bounds.Of(rect), out _);
        var result = new Dictionary<string, MapPaintRegionSet>(StringComparer.Ordinal);
        foreach (var target in hits.Where(i => _paintEntries[i].Rect.Touches(rect)).Select(i => _paintEntries[i])
            .GroupBy(e => e.Target, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            PaintEntry[] entries = target.OrderBy(e => e.Order).ToArray();
            string digest = MapCanonical.HashHex(w =>
            {
                w.WriteStartArray(); w.WriteStringValue("kemap/foliage-scoped-paint/1"); w.WriteStringValue(target.Key);
                foreach (PaintEntry entry in entries) w.WriteStringValue(entry.Digest);
                w.WriteEndArray();
            });
            result.Add(target.Key, new MapPaintRegionSet(entries.Select(e => e.Region).ToArray(), digest));
        }
        return new ReadOnlyDictionary<string, MapPaintRegionSet>(result);
    }

    /// <summary>The effective placements that own foliage clearing whose proxy bounds meet <paramref name="bounds"/>, in
    /// effective order. A proxy is the union of the placement's applicable clearance rule and exception mask footprints
    /// through its effective transform, each Door footprint widened to include its origin and the layer's door
    /// clearance. Interaction envelopes never enter a proxy. Callers pad <paramref name="bounds"/> by the clearance read reach.</summary>
    public IReadOnlyList<MapResolvedPlacement> PlacementsIn(MapFoliageRect bounds) => PlacementsIn(bounds, out _);

    /// <summary><see cref="PlacementsIn(MapFoliageRect)"/>, also reporting how many indexed owners it inspected.</summary>
    /// <summary>Every indexed clearance owner with its proxy bounds, in effective order.</summary>
    internal IEnumerable<(MapResolvedPlacement Placement, Bounds Proxy)> OwnerProxies() =>
        _ownerPlacements.Select((p, i) => (p, _ownerBounds[i]));

    internal IReadOnlyList<MapResolvedPlacement> PlacementsIn(MapFoliageRect bounds, out long candidates)
    {
        if (!float.IsFinite(bounds.MinX) || !float.IsFinite(bounds.MinZ) || !float.IsFinite(bounds.MaxX) || !float.IsFinite(bounds.MaxZ) ||
            bounds.MinX > bounds.MaxX || bounds.MinZ > bounds.MaxZ)
            throw new ArgumentException("foliage bounds must be finite and ordered.", nameof(bounds));
        var query = new Bounds(bounds.MinX, bounds.MinZ, bounds.MaxX, bounds.MaxZ);
        List<int> hits = _owners.Query(query, out candidates);
        hits.Sort();
        return Array.AsReadOnly(hits.Select(i => _ownerPlacements[i]).ToArray());
    }

    static MapFoliageContextResult Build(MapDocument document, MapAssetClosure assets, MapEffectiveResources resources,
        MapFoliageContextWork measured, long expansions, long writes, string baseline)
    {
        // Copying and indexing neither expand nor serialize, so the observed work is final before staging begins.
        MapFoliageContextWork work = Observed(measured, expansions, writes);
        string fingerprint = Editing.MapDocumentEditFinalizer.Fingerprint(baseline, document.Foliage);
        try { return new(MapFoliageStatus.Complete, new(document.Foliage?.Clone(), assets, resources, work, fingerprint), work); }
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

    // Owner proxies, indexed spatially. Rules are looked up by asset and masks by owner, never scanned per placement.
    static (MapResolvedPlacement[], Bounds[], SpatialIndex) Owners(MapFoliageSet? set, MapEffectiveResources resources)
    {
        var rules = new Dictionary<string, List<(MapFoliageLayer Layer, MapFoliageClearanceRule Rule)>>(StringComparer.Ordinal);
        var masks = new Dictionary<string, List<(MapFoliageLayer Layer, MapFoliageMask Mask)>>(StringComparer.Ordinal);
        foreach (MapFoliageLayer layer in set?.Layers ?? [])
        {
            foreach (MapFoliageClearanceRule rule in layer.ClearanceRules)
            {
                if (!rules.TryGetValue(rule.AssetId, out var list)) rules.Add(rule.AssetId, list = new());
                list.Add((layer, rule));
            }
            foreach (MapFoliageMask mask in layer.ExceptionMasks.Where(m => m.Owner is { Scope: MapPrefabReferenceScope.World, Kind: MapPrefabChildKind.Placement }))
            {
                if (!masks.TryGetValue(mask.Owner.Id, out var list)) masks.Add(mask.Owner.Id, list = new());
                list.Add((layer, mask));
            }
        }
        var placements = new List<MapResolvedPlacement>();
        var bounds = new List<Bounds>();
        if (rules.Count == 0 && masks.Count == 0) return (placements.ToArray(), bounds.ToArray(), new SpatialIndex(bounds));
        foreach (MapResolvedPlacement placement in resources.Placements)
        {
            Bounds? proxy = null;
            MapEffectiveTransform pose = resources.Transforms.TryGetValue(MapEffectiveContext.Key(MapPrefabChildKind.Placement, placement.PlacementId),
                out MapEffectiveTransform effective) ? effective : MapEffectiveTransform.From(placement.Transform);
            if (rules.TryGetValue(placement.AssetId, out var applicable))
                foreach (var (layer, rule) in applicable)
                    if (rule.PlacementTag is not { } tag || placement.Tags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)))
                        proxy = Bounds.Union(proxy, Proxy(pose, rule.Footprint, rule.Kind == MapFoliageClearanceKind.Door ? layer.DoorClearance : null));
            if (masks.TryGetValue(placement.PlacementId, out var owned))
                foreach (var (layer, mask) in owned)
                    proxy = Bounds.Union(proxy, Proxy(pose, mask.Footprint, mask.Kind == MapFoliageClearanceKind.Door ? layer.DoorClearance : null));
            if (proxy is not { } value) continue;
            placements.Add(placement);
            bounds.Add(value);
        }
        return (placements.ToArray(), bounds.ToArray(), new SpatialIndex(bounds));
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

    sealed record PaintEntry(string Target, int Order, MapPaintRegion Region, MapExactRect Rect, string Digest);

    // Every published paint region with its exact XZ bounds and its own content digest, indexed spatially.
    static (PaintEntry[], SpatialIndex) Paint(MapEffectiveResources resources)
    {
        var entries = new List<PaintEntry>();
        foreach (var (target, set) in resources.PaintRegions.OrderBy(p => p.Key, StringComparer.Ordinal))
            for (int i = 0; i < set.Regions.Count; i++)
            {
                MapPaintRegion region = set.Regions[i];
                if (region.Polygon.Count == 0) continue;
                MapExactValue minX = region.Polygon[0].X, maxX = minX, minZ = region.Polygon[0].Z, maxZ = minZ;
                foreach (MapExactPoint p in region.Polygon)
                {
                    if (p.X.CompareTo(minX) < 0) minX = p.X;
                    if (p.X.CompareTo(maxX) > 0) maxX = p.X;
                    if (p.Z.CompareTo(minZ) < 0) minZ = p.Z;
                    if (p.Z.CompareTo(maxZ) > 0) maxZ = p.Z;
                }
                entries.Add(new(target, i, region, new(minX, minZ, maxX, maxZ), RegionDigest(region)));
            }
        return (entries.ToArray(), new SpatialIndex(entries.Select(e => Bounds.Of(e.Rect)).ToList()));
    }

    static string RegionDigest(MapPaintRegion region) => MapCanonical.HashHex(w =>
    {
        w.WriteStartArray(); w.WriteStringValue("kemap/foliage-paint-region/1");
        MapFaceKey face = region.Face;
        w.WriteStringValue(face.OwnerId); w.WriteStringValue(face.Patch?.SurfaceId);
        w.WriteStringValue(face.Patch?.SlotX.ToString(CultureInfo.InvariantCulture));
        w.WriteStringValue(face.Patch?.SlotZ.ToString(CultureInfo.InvariantCulture));
        w.WriteNumberValue(face.Primitive); w.WriteNumberValue(face.ParentTriangle); w.WriteNumberValue(face.Child); w.WriteNumberValue((byte)face.Side);
        w.WriteStringValue(region.PaintId); w.WriteNumberValue(region.Priority);
        w.WriteNumberValue(region.Material.Underlay); w.WriteNumberValue(region.Material.Overlay);
        w.WriteBooleanValue(region.Material.OverlayCovers); w.WriteBooleanValue(region.Material.Feather);
        foreach (MapExactPoint p in region.Polygon)
            foreach (MapExactValue value in new[] { p.X, p.Y, p.Z })
            { w.WriteNumberValue(value.Numerator); w.WriteNumberValue(value.Denominator); }
        w.WriteEndArray();
    });

    static string Snapshot(MapFoliageSet? foliage, MapAssetClosure assets, MapEffectiveResources resources, PaintEntry[] paint) =>
        MapCanonical.HashHex(w =>
        {
            w.WriteStartArray(); w.WriteStringValue("kemap/foliage-context/1");
            w.WriteStringValue(resources.Digest); w.WriteStringValue(resources.AnnotationDigest); w.WriteStringValue(assets.Hash);
            w.WriteStringValue(MapFoliageProjection.Digest(foliage));
            foreach (MapResolvedPlacement p in resources.Placements)
            {
                w.WriteStartArray();
                w.WriteStringValue(p.PlacementId); w.WriteStringValue(p.Kind); w.WriteStringValue(p.AssetId);
                if (p.NumericId is { } id) w.WriteNumberValue(id); else w.WriteNullValue();
                foreach (float value in new[] { p.Transform.Position.X, p.Transform.Position.Y, p.Transform.Position.Z, p.Transform.YawRadians, p.Transform.Scale })
                    w.WriteNumberValue(BitConverter.SingleToInt32Bits(value));
                foreach (string tag in p.Tags) w.WriteStringValue(tag);
                w.WriteEndArray();
            }
            foreach (PaintEntry entry in paint) { w.WriteStringValue(entry.Target); w.WriteStringValue(entry.Digest); }
            w.WriteEndArray();
        });

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
