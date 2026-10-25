using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Markers;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;

namespace KhaozEngine.MapDoc.Scene;

/// <summary>Bounded dependency closure, followed by one atomic neutral snapshot publication.</summary>
internal static class MapSceneAcquisition
{
    // Neutral callers bind this signature without a foliage type in their assembly metadata.
    internal static MapScopedSceneResult Run(MapSceneSource source, MapSceneScope scope, MapSceneAdmission? sharedAdmission = null)
        => RunCore(source, scope, sharedAdmission, null);

    internal static MapScopedSceneResult Run(MapSceneSource source, MapSceneScope scope, MapSceneAdmission sharedAdmission,
        MapFoliageWindowRequest foliageRequest)
        => RunCore(source, scope, sharedAdmission, foliageRequest);

    static MapScopedSceneResult RunCore(MapSceneSource source, MapSceneScope scope, MapSceneAdmission? sharedAdmission,
        MapFoliageWindowRequest? foliageRequest)
    {
        var data = source.Data;
        var admission = sharedAdmission ?? new MapSceneAdmission(data.Limits);
        using var diagnostics = admission.AcquisitionDiagnostics?.Enter(MapAcquisitionStage.Scene);
        using var preparation = admission.BeginPreparation();
        long visits = 0, metadataRows = 0, memberships = 0, retained = 0;
        string digest = "";
        var required = new HashSet<MapSceneResourceKey>();
        var present = new HashSet<MapSceneResourceKey>();
        var empty = new HashSet<MapSceneResourceKey>();
        var unavailable = new HashSet<MapSceneResourceKey>();
        try
        {
            ArgumentNullException.ThrowIfNull(scope);
            scope.Validate();
            admission.ReserveBytes(checked(128L + 4L * scope.Surfaces.Roles.Count + 8L * (scope.Surfaces.SpaceIds?.Count ?? 0)));
            scope = scope with
            {
                Surfaces = scope.Surfaces with
                {
                    Roles = Array.AsReadOnly(scope.Surfaces.Roles.ToArray()),
                    SpaceIds = scope.Surfaces.SpaceIds is null ? null : Array.AsReadOnly(scope.Surfaces.SpaceIds.ToArray()),
                    Limits = scope.Surfaces.Limits with { },
                }
            };
            digest = scope.Digest;
            MapSceneSource.CheckManifest(data.Directory, data.ManifestDigest);
            if (!MapSceneAdmission.Supports(data.Surfaces)) throw new MapSceneSourceException(MapSceneStatus.Invalid, "Surface source cannot participate in scene admission.");
            using var admitted = admission.Enter();
            var owners = new SortedSet<string>(StringComparer.Ordinal);
            var pending = new Queue<string>();
            var nativeNeeded = new HashSet<MapPatchKey>();
            var nativeDeclared = new HashSet<string>(StringComparer.Ordinal);
            void AddKey(MapSceneResourceKey key)
            {
                if (required.Contains(key)) return;
                admission.BeforeResource(key);
                admission.ReserveBytes(512);
                required.Add(key);
                metadataRows++;
                if (data.Producing.TryGetValue(key, out var id)) AddOwner(id);
                else if (data.NativePatches.TryGetValue(key, out var nativeEntry)) nativeNeeded.Add(nativeEntry.Key);
                else if (key.Kind == "Record" && data.NativeRecords.TryGetValue(key.Id, out var anchor)) AddKey(PatchKey(anchor));
                else if (key.Kind == "Surface" && data.NativeRefs.ContainsKey(key.Id)) nativeDeclared.Add(key.Id);
                else if (!data.FoliageRows.ContainsKey(key) && !data.GlobalKeys.Contains(key))
                {
                    unavailable.Add(key);
                    throw new MapSceneSourceException(MapSceneStatus.Missing, "Required scene resource has no attested owner: " + key.Kind + "/" + key.Id);
                }
            }
            void AddOwner(string id)
            {
                if (!owners.Add(id)) return;
                if (owners.Count > data.Limits.MaxSelectedResources) throw new MapSceneCapacityException(nameof(MapSceneLimits.MaxSelectedResources), owners.Count, data.Limits.MaxSelectedResources);
                pending.Enqueue(id);
                foreach (var key in data.Owners[id].Resources) AddKey(key);
            }
            void Discover(MapBox3 bounds, Func<MapSceneResourceKey, bool> accepts)
            {
                long read = 0;
                try
                {
                    foreach (var key in data.Index.Query(bounds, out read, data.Limits.MaxSelectedResources, accepts)) AddKey(key);
                }
                finally { visits = checked(visits + read); }
            }
            if (foliageRequest is not null) foreach (var key in foliageRequest.Owners) AddKey(key);
            Discover(scope.DiscoveryBounds, k => Included(k.Kind, scope.Channels));
            var physical = scope.Surfaces.WorldRectangle();
            if (foliageRequest is null || scope.Channels != 0)
                Discover(new(physical.MinX.ToDouble(), double.NegativeInfinity, physical.MinZ.ToDouble(),
                    physical.MaxX.ToDouble(), double.PositiveInfinity, physical.MaxZ.ToDouble()), k => k.Kind is "Surface" or "Record" or "Patch");
            foreach (var seat in foliageRequest?.Seats ?? [])
            {
                var rect = seat.WorldRectangle();
                Discover(new(rect.MinX.ToDouble(), double.NegativeInfinity, rect.MinZ.ToDouble(), rect.MaxX.ToDouble(),
                    double.PositiveInfinity, rect.MaxZ.ToDouble()), k => k.Kind is "Surface" or "Record" or "Patch");
            }
            while (pending.Count != 0)
            {
                string id = pending.Dequeue();
                using var ownerPreparation = admission.BeginPreparation();
                foreach (string dependency in data.Dependencies[id]) { memberships++; AddOwner(dependency); }
                var owner = data.Owners[id];
                foreach (var key in owner.Resources)
                    if (data.ResourceDependencies.TryGetValue(key, out var dependencies)) foreach (var dependency in dependencies) AddKey(dependency);
                admission.BeforePayload(checked(owner.Resources.Count * 512L));
                foreach (var attachment in owner.Input.Prefabs?.Attachments ?? [])
                    foreach (var child in attachment.Children)
                    {
                        if (child.LocalChild.Kind == MapPrefabChildKind.Surface)
                            foreach (var entry in data.NativeBySurface.GetValueOrDefault(child.WorldId) ?? []) AddKey(PatchKey(entry.Key));
                        if (child.Anchor is { } anchor) AddKey(PatchKey(anchor));
                    }
                foreach (var key in owner.Resources)
                    if (key.Kind == "Surface" && data.ResourceDependencies.TryGetValue(key, out var deps) && deps.Any(d => d.Kind == "Surface" && data.NativeRefs.ContainsKey(d.Id)))
                    {
                        var bounds = data.MetadataByKey[key].Bounds;
                        Discover(bounds, k => k.Kind == "Patch" && deps.Any(d => d.Id == data.NativePatches[k].Key.SurfaceId));
                    }
                var enumeration = MapEffectiveResolutionKernel.Enumerate(owner.Input, data.Assets, metadataOnly: true);
                foreach (var placed in enumeration.Children.OfType<MapPrefabPlacementChild>().Where(p => p.Value.Y is null))
                {
                    var pose = enumeration.Transforms["Placement:" + placed.Value.Id];
                    DiscoverSeat(pose.X, pose.Z);
                }
                foreach (var marker in MapMarkerExpansion.Capture(owner.Input, data.Assets))
                {
                    if (marker.Value.HeightMode != MapMarkerHeightMode.Support) continue;
                    var position = marker.Value.Position;
                    var point = new MapExactPoint(MapExactValue.FromSingle(position.X), MapExactValue.FromSingle(position.Y), MapExactValue.FromSingle(position.Z));
                    if (marker.Frame is { } frame) point = new MapResourceTransform(frame).Point(point);
                    DiscoverSeat(point.X.ToDouble(), point.Z.ToDouble());
                }
            }
            void DiscoverSeat(double x, double z) => Discover(new(x - 2, double.NegativeInfinity, z - 2, x + 2, double.PositiveInfinity, z + 2),
                k => k.Kind is "Surface" or "Record" or "Patch");
            var selectedFoliage = required.Where(k => k.Kind == "Foliage").ToArray();
            var foliage = AcquireFoliage(data, selectedFoliage, admission, required, present, empty, unavailable, foliageRequest);
            if (unavailable.Count != 0) throw new MapSceneSourceException(MapSceneStatus.Incomplete, "Required foliage density is unavailable.");
            using var inputDiagnostics = admission.AcquisitionDiagnostics?.Enter(MapAcquisitionStage.NativeInput);
            using var inputPreparation = admission.BeginPreparation();
            admission.ReserveBytes(checked(4096L + 128L * owners.Count + 128L * nativeNeeded.Count));
            var input = Merge(data, owners.Select(id => data.Owners[id]).ToArray());
            var (acquiredNative, nativeOwnership) = AcquireNativeOwned(data, nativeNeeded, admission, scope.Surfaces.Limits);
            int acquiredPatchCount = acquiredNative.Patches.Count;
            foreach (var patch in acquiredNative.Patches) input.Surfaces.Patches.Add(patch.Key, patch.Value);
            foreach (var surface in acquiredNative.Refs.Where(s => s.Role == MapSurfaceRole.PaintOverride))
                admission.BeforePaintTarget(surface.PaintTargetSurfaceId ?? surface.Id);
            var nativeIds = acquiredNative.Refs.Select(s => s.Id).Concat(nativeDeclared).ToHashSet(StringComparer.Ordinal);
            foreach (string id in nativeIds) input.Surfaces.Refs.Add(data.NativeRefs[id]);
            var nativeSource = new MapSceneSurfaceSource(data.Surfaces, nativeIds, nativeIds.Select(id => data.NativeRefs[id]).ToArray());
            var poseKeys = nativeIds.Select(id => (Kind: MapPrefabChildKind.Surface, Id: id))
                .Concat(acquiredNative.AllRecords().Select(r => (Kind: MapPrefabChildKind.Record, Id: r.Id)));
            var rootTransforms = poseKeys.Select(k => data.NativePoseByKey.GetValueOrDefault(k)).OfType<MapStandaloneResourceTransform>();
            input = input with { NativeResourceTransforms = (input.NativeResourceTransforms ?? []).Concat(rootTransforms).DistinctBy(t => (t.Kind, t.OwnerId)).ToArray() };
            // The input keeps selected patch payloads. The copied native-set metadata has no further consumer.
            nativeOwnership.Release(MapSceneInventory.SurfaceMetadata(acquiredNative));
            acquiredNative.Patches.Clear(); acquiredNative.Refs.Clear();
            // Foliage has already acquired its complete native read closure. Resolve those admitted payloads directly,
            // so preparation never re-enters storage or spends one point query's page allowance over the whole closure.
            var product = MapEffectiveResolutionKernel.Resolve(input, data.Assets, foliageRequest is null ? nativeSource : null,
                queryLimits: scope.Surfaces.Limits);
            if (product.PreparationOwnership is { } productOwner) inputPreparation.Export(productOwner, preparation);
            input.Surfaces.Patches.Clear(); input.Surfaces.Refs.Clear();
            inputPreparation.Dispose();
            inputDiagnostics?.Dispose();
            var view = product.Acquire(scope.Surfaces with { SceneAdmission = admission });
            Record(view);
            foreach (var seat in foliageRequest?.Seats ?? [])
            {
                using var seatDiagnostics = admission.AcquisitionDiagnostics?.Enter(MapAcquisitionStage.FoliageSeat, MapAcquisitionCaller.FoliageSeat);
                using var seatPreparation = admission.BeginPreparation();
                Record(product.Acquire(seat with { SceneAdmission = admission, Limits = scope.Surfaces.Limits }));
            }
            void Record(MapScopedSurfaces acquired)
            {
                foreach (var patch in acquired.Witness.Present)
                {
                    var key = PatchKey(patch.Key); required.Add(key); present.Add(key);
                }
                foreach (var missing in acquired.Witness.Unavailable)
                {
                    var key = PatchKey(missing.Key); required.Add(key); unavailable.Add(key);
                }
                foreach (var range in acquired.Witness.KnownEmpty) empty.Add(new("SurfaceRange", RangeId(range)));
                if (acquired.Status != MapAcquireStatus.Complete)
                {
                    var status = acquired.Status switch
                    {
                        MapAcquireStatus.CapacityExceeded => MapSceneStatus.CapacityExceeded,
                        MapAcquireStatus.NotRepresentable => MapSceneStatus.NotRepresentable,
                        _ => acquired.Witness.Unavailable.Any(u => u.Status == MapPatchStatus.Corrupt) ? MapSceneStatus.Corrupt
                            : acquired.Witness.Unavailable.Any(u => u.Status == MapPatchStatus.Missing) ? MapSceneStatus.Missing : MapSceneStatus.Incomplete,
                    };
                    throw new MapSceneSourceException(status, acquired.Detail ?? "Incomplete surface dependencies.", acquired.Capacity);
                }
            }
            foreach (var key in required) if (!unavailable.Contains(key) && !empty.Contains(key)) present.Add(key);
            if (required.Count > data.Limits.MaxSelectedResources) throw new MapSceneCapacityException(nameof(MapSceneLimits.MaxSelectedResources), required.Count, data.Limits.MaxSelectedResources);
            if (required.Count == 0 && empty.Count == 0) empty.Add(new("Scene", digest));
            bool whole = scope.Channels == MapSceneChannels.All && scope.Surfaces.MinY is null && scope.Surfaces.MaxY is null &&
                scope.Surfaces.SpaceIds is null &&
                Enum.GetValues<MapSurfaceRole>().All(scope.Surfaces.Roles.Contains) && owners.Count == data.Owners.Count &&
                selectedFoliage.Length == data.FoliageRows.Count && acquiredPatchCount == data.NativeEntries.Count && scope.DrawBounds.MinX <= data.MinX && scope.DrawBounds.MaxX >= data.MaxX &&
                scope.DrawBounds.MinZ <= data.MinZ && scope.DrawBounds.MaxZ >= data.MaxZ && physical.MinX.ToDouble() <= data.MinX &&
                physical.MaxX.ToDouble() >= data.MaxX && physical.MinZ.ToDouble() <= data.MinZ && physical.MaxZ.ToDouble() >= data.MaxZ;
            MapSceneSource.CheckManifest(data.Directory, data.ManifestDigest);
            var witness = Witness(true, whole);
            using var publication = admission.AcquisitionDiagnostics?.Enter(MapAcquisitionStage.ScenePublication);
            var scene = new MapScopedScene(source, scope, witness, product, view, foliage);
            retained = scene.InventoryBytes;
            scene.PreparationOwnership = preparation.Complete(retained, retainDependencies: true);
            return new(whole ? MapSceneStatus.WholeWorldComplete : MapSceneStatus.CompleteForScope, scene, witness, Work(true));
        }
        catch (Exception error) when (MapSceneSource.Classify(error) is not null)
        {
            var status = Enum.Parse<MapSceneStatus>(MapSceneSource.Classify(error)!.Value.ToString());
            return new(status, null, Witness(false, false), Work(false)) { Capacity = MapSceneCapacity.From(error), Findings = Array.AsReadOnly(new[] { error.Message }) };
        }
        MapSceneWork Work(bool accepted) => new(0, metadataRows, memberships, 0, accepted ? retained : 0,
            admission.PreparedFaces, admission.PayloadReads, visits);
        MapSceneWitness Witness(bool complete, bool whole) => new(source.ClosureHash, source.SourceGeneration, digest,
            Ordered(required), Ordered(present), complete ? Ordered(empty) : Array.Empty<MapSceneResourceKey>(), Ordered(unavailable), complete, whole)
        { RenderExtentsDigest = source.RenderExtentsDigest };
    }

    static IReadOnlyList<MapSceneResourceKey> Ordered(IEnumerable<MapSceneResourceKey> keys) =>
        Array.AsReadOnly(keys.OrderBy(k => k.Kind, StringComparer.Ordinal).ThenBy(k => k.Id, StringComparer.Ordinal).ToArray());
    static bool Included(string kind, MapSceneChannels channels) => kind switch
    {
        "Placement" => channels.HasFlag(MapSceneChannels.Placements),
        "Surface" or "Record" or "Patch" => (channels & (MapSceneChannels.Terrain | MapSceneChannels.Rooms)) != 0,
        "WaterBody" or "WaterMedium" => channels.HasFlag(MapSceneChannels.Water),
        "Foliage" => channels.HasFlag(MapSceneChannels.Foliage),
        _ => channels.HasFlag(MapSceneChannels.Annotations),
    };
    internal static MapSceneResourceKey PatchKey(MapPatchKey key) => new("Patch", FormattableString.Invariant($"{key.SurfaceId}/{key.SlotX}/{key.SlotZ}"));
    static string RangeId(MapCoveredRange range) => FormattableString.Invariant(
        $"{range.SurfaceId}/{range.Slots.MinX}/{range.Slots.MinZ}/{range.Slots.MaxXExclusive}/{range.Slots.MaxZExclusive}");

    internal static MapSurfaceSet AcquireNative(MapSceneData data, IReadOnlySet<MapPatchKey> needed, MapSceneAdmission admission, MapQueryLimits queries)
        => AcquireNativeOwned(data, needed, admission, queries).Surfaces;
    static (MapSurfaceSet Surfaces, MapPreparationReservation Ownership) AcquireNativeOwned(MapSceneData data,
        IReadOnlySet<MapPatchKey> needed, MapSceneAdmission admission, MapQueryLimits queries)
    {
        using var preparation = admission.BeginPreparation();
        preparation.Reserve(128);
        var surfaces = new MapSurfaceSet();
        var refs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in needed.Order())
        {
            if (surfaces.Patches.ContainsKey(key)) continue;
            using var diagnostics = admission.AcquisitionDiagnostics?.Enter(MapAcquisitionStage.NativeQuery, MapAcquisitionCaller.NativeKey);
            using var queryPreparation = admission.BeginPreparation();
            var surface = data.NativeRefs[key.SurfaceId];
            var cells = new MapCellRect(checked(key.SlotX * 64), checked(key.SlotZ * 64), checked((key.SlotX + 1) * 64), checked((key.SlotZ + 1) * 64));
            var rect = MapSurfaceRanges.World(cells, surface.Frame);
            var frame = KhaozEngine.Primitives.WorldFrame.Nearest((float)rect.MinX.ToDouble(), (float)rect.MinZ.ToDouble());
            double ax = (double)frame.X * KhaozEngine.Primitives.WorldFrame.Grid, az = (double)frame.Z * KhaozEngine.Primitives.WorldFrame.Grid;
            var request = new MapSurfaceScope(frame, new((float)(rect.MinX.ToDouble() - ax), (float)(rect.MinZ.ToDouble() - az)),
                new((float)(rect.MaxX.ToDouble() - ax), (float)(rect.MaxZ.ToDouble() - az)), null, null, Enum.GetValues<MapSurfaceRole>(), null, queries)
            { DiscoverySurfaces = new HashSet<string>(StringComparer.Ordinal) { key.SurfaceId }, SceneAdmission = admission };
            var view = MapScopedSurfaces.Acquire(data.Surfaces, request, data.Assets.Roots.Select(a => a.Sha256).ToArray());
            if (view.Status != MapAcquireStatus.Complete) throw new MapSceneSourceException(view.Status == MapAcquireStatus.CapacityExceeded
                ? MapSceneStatus.CapacityExceeded : view.Status == MapAcquireStatus.NotRepresentable ? MapSceneStatus.NotRepresentable :
                view.Witness.Unavailable.Any(u => u.Status == MapPatchStatus.Corrupt) ? MapSceneStatus.Corrupt :
                view.Witness.Unavailable.Any(u => u.Status == MapPatchStatus.Missing) ? MapSceneStatus.Missing : MapSceneStatus.Incomplete,
                view.Detail ?? "Unavailable native dependency payload.", view.Capacity);
            foreach (var descriptor in view.Surfaces)
                if (refs.Add(descriptor.Id))
                {
                    preparation.Reserve(checked(256L + 8L * (descriptor.IndoorSpan?.DomainTags.Count ?? 0)));
                    surfaces.Refs.Add(MapAcquiredFacts.CopySurface(descriptor));
                }
            foreach (var entry in view.Witness.Present)
                if (!surfaces.Patches.ContainsKey(entry.Key))
                {
                    preparation.Reserve(64);
                    var patch = view.Patch(entry.Key).Patch!;
                    if (patch.PreparationOwnership is { } ownership) queryPreparation.Export(ownership, preparation);
                    surfaces.Patches.Add(entry.Key, patch);
                }
        }
        var resultOwnership = preparation.Complete(new MapSceneInventory().Surfaces(surfaces));
        return (surfaces, resultOwnership);
    }

    static MapEffectiveResolutionInput Merge(MapSceneData data, IReadOnlyList<MapSceneOwner> owners)
    {
        var inputs = owners.Select(o => o.Input).ToArray();
        var instances = inputs.SelectMany(i => i.Prefabs?.Instances ?? []).ToArray();
        var transfers = inputs.SelectMany(i => i.Prefabs?.Transfers ?? []).ToArray();
        var attachments = inputs.SelectMany(i => i.Prefabs?.Attachments ?? []).ToArray();
        var placements = inputs.SelectMany(i => i.Placements).ToArray();
        var resources = owners.SelectMany(o => o.Resources).ToHashSet();
        var intent = new MapBuildingIntent(1, inputs.SelectMany(i => i.BuildingIntent?.RoofLinks ?? []).ToArray(),
            inputs.SelectMany(i => i.BuildingIntent?.RoofGroups ?? []).ToArray(), inputs.SelectMany(i => i.BuildingIntent?.Lights ?? []).ToArray(),
            inputs.SelectMany(i => i.BuildingIntent?.Markers ?? []).ToArray())
        {
            Roofs = data.Roofs.Where(r => resources.Contains(new(r.Kind.ToString(), r.Id))).ToArray(),
            Storeys = data.Storeys,
        };
        var water = new MapWaterSet();
        water.Media.AddRange(inputs.SelectMany(i => i.Water?.Media ?? []).DistinctBy(m => m.Key));
        water.Bodies.AddRange(inputs.SelectMany(i => i.Water?.Bodies ?? []));
        var surfaces = new MapSurfaceSet();
        // Native descriptors and payloads are added only from the admitted dependency acquisitions.
        var transforms = inputs.SelectMany(i => i.NativeResourceTransforms ?? []).DistinctBy(t => (t.Kind, t.OwnerId)).ToArray();
        return new(placements, new(1, 1, instances, transfers) { Attachments = attachments }, intent, transforms,
            surfaces, water.Bodies.Count == 0 && water.Media.Count == 0 ? null : water, data.RootDigest);
    }

    static MapFoliageSet? AcquireFoliage(MapSceneData data, IReadOnlyList<MapSceneResourceKey> selected, MapSceneAdmission admission,
        HashSet<MapSceneResourceKey> required, HashSet<MapSceneResourceKey> present, HashSet<MapSceneResourceKey> empty, HashSet<MapSceneResourceKey> unavailable,
        MapFoliageWindowRequest? window = null)
    {
        if (data.Foliage is null) return null;
        var layers = new List<MapFoliageLayer>();
        foreach (var grouping in (window?.Chunks ?? selected.Select(k => data.FoliageRows[k]).ToArray()).GroupBy(v => v.Layer.Id))
        {
            var layer = grouping.First().Layer;
            var chunks = new List<MapFoliageChunk>();
            var keys = grouping.SelectMany(g => MapFoliageRasterReads.Density(layer, g.Chunk)).Distinct().ToArray();
            foreach (var chunkKey in keys)
            {
                var key = FoliageKey(layer.Id, chunkKey); admission.BeforeResource(key); required.Add(key);
                if (required.Count > data.Limits.MaxSelectedResources) throw new MapSceneCapacityException(nameof(MapSceneLimits.MaxSelectedResources), required.Count, data.Limits.MaxSelectedResources);
                if (!data.FoliageRows.ContainsKey(key)) { empty.Add(key); continue; }
                // Pack decoding stages every contained chunk. Admit that exact density demand before the read.
                long bytes = MapFoliageChunk.ByteLength;
                if (data.FoliageStorage is { } storage && storage.TryGetChunk(layer.Id, chunkKey, out var row) &&
                    storage.TryGetPack(layer.Id, row.OwnerRegion, out var pack)) bytes = checked(pack.ByteLength + pack.ChunkCount * (long)MapFoliageChunk.ByteLength);
                using var chunkPreparation = admission.BeginPreparation();
                admission.BeforePayload(bytes);
                MapFoliageChunkRead read;
                if (data.FoliageStorage is { } stored) read = stored.Read(layer.Id, chunkKey);
                else
                {
                    var resident = data.ResidentFoliageChunks.GetValueOrDefault(key);
                    read = new(resident is null ? MapFoliageReadStatus.Unavailable : MapFoliageReadStatus.Found, resident);
                }
                if (read.Status != MapFoliageReadStatus.Found || read.Chunk is null) { unavailable.Add(key); continue; }
                chunks.Add(read.Chunk); present.Add(key);
                chunkPreparation.Complete(MapFoliageChunk.ByteLength);
            }
            long metadataBytes = checked(MapSceneResourceBounds.FoliageMetadataBytes(layer) - layer.ChunkDigests.Count * 80L + keys.Length * 80L);
            admission.BeforeInputBytes(metadataBytes);
            var digests = keys.Where(k => data.MetadataByKey.ContainsKey(FoliageKey(layer.Id, k)))
                .Select(k => new MapFoliageChunkDigest(k, data.MetadataByKey[FoliageKey(layer.Id, k)].Digest)).ToArray();
            layers.Add(MapFoliageLayer.FromStorage(layer, digests, chunks));
        }
        return new(data.Foliage.PayloadVersion, layers);
    }
    internal static MapSceneResourceKey FoliageKey(string layer, MapFoliageChunkKey chunk) =>
        new("Foliage", FormattableString.Invariant($"{layer}/{chunk.X}/{chunk.Z}"));
}

/// <summary>A declared subset of an admitted native producer, with explicit dependencies forwarded to its session.</summary>
internal sealed class MapSceneSurfaceSource : IMapSurfaceAcquisitionSource
{
    internal IMapSurfaceSource Inner { get; }
    readonly IReadOnlySet<string> _ids;
    internal MapPreparationReservation? PreparationOwnership { get; }
    internal long IndexBytes => checked(128L + 32L * ((HashSet<string>)_ids).EnsureCapacity(0) + 8L * Surfaces.Count);
    internal MapSceneSurfaceSource(IMapSurfaceSource inner, IReadOnlySet<string> ids, IReadOnlyList<MapSurfaceRef> descriptors)
    {
        using var preparation = MapSceneAdmission.Current?.BeginPreparation();
        MapSceneAdmission.Current?.ReserveBytes(checked(128L + 32L * (4L * ids.Count + 3) + 8L * descriptors.Count));
        Inner = inner; _ids = ids.ToHashSet(StringComparer.Ordinal); Surfaces = Array.AsReadOnly(descriptors.ToArray());
        PreparationOwnership = preparation?.Complete(IndexBytes);
    }
    public string SnapshotId => Inner.SnapshotId;
    public string RootSha256 => Inner.RootSha256;
    public IReadOnlyList<MapSurfaceRef> Surfaces { get; }
    public MapPatchRead ReadPatch(MapPatchKey key) => Inner.ReadPatch(key);
    public MapPatchFindResult FindPatches(MapSurfaceScope scope)
    { using var session = OpenAcquisition(scope); return session.FindPatches(); }
    public IMapSurfaceAcquisitionSession OpenAcquisition(MapSurfaceScope scope) => ((IMapSurfaceAcquisitionSource)Inner).OpenAcquisition(scope with
    { DiscoverySurfaces = scope.DiscoverySurfaces is { } selected ? selected.Where(_ids.Contains).ToHashSet(StringComparer.Ordinal) : _ids });
}
