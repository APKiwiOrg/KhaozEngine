using KhaozEngine.Diagnostics;
using DemandDiagnostics = KhaozEngine.Diagnostics.CapacityDemandDiagnostics;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Identity;
using KhaozEngine.MapDoc.Markers;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;

namespace KhaozEngine.MapDoc.Scene;

/// <summary>One attested generation of metadata. Surface and density payloads remain with their pinned producers.</summary>
public sealed class MapSceneSource
{
    readonly MapSceneData _data;
    readonly MapAssetExtentProofSet? _proofs;
    internal MapSceneSource(MapSceneData data, MapAssetExtentProofSet? proofs = null) { _data = data; _proofs = proofs; }
    public string SourceGeneration => _data.Generation;
    public string ClosureHash => _data.Assets.Hash;
    public IReadOnlyList<string> RequiredAssetIds => _data.RequiredAssets;
    public string? RenderExtentsDigest => _proofs?.Digest;
    internal MapAssetExtentProofSet? RenderExtents => _proofs;
    internal MapSceneData Data => _data;

    public static MapSceneSourceResult Capture(MapDocumentSource source, MapAssetClosure assets, MapSceneLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(assets);
        var scan = new MapSceneScan(limits ?? new());
        using var demandScope = DemandDiagnostics.Enter(scan, "MapSceneSource.Capture");
        try
        {
            scan.Limits.Validate();
            if (DemandDiagnostics.Check(scan, "MapSceneSource.MaxTiles.1", "MaxTiles", 0, source.Tiles.Entries.Count, scan.Limits.MaxTiles, source.Tiles.Entries.Count > scan.Limits.MaxTiles, CapacityDemandLifetime.Inventory)) throw new MapSceneCapacityException(nameof(MapSceneLimits.MaxTiles), source.Tiles.Entries.Count, scan.Limits.MaxTiles);
            string? manifestDigest = source.Tiles.ManifestSha256;
            CheckManifest(source.SceneDirectory, manifestDigest, scan);
            if (source.ScenePartial) throw new MapSceneSourceException(MapSceneStatus.Incomplete, "Unloaded tile metadata cannot attest a reverse index.");
            IMapSurfaceSource native;
            if (source.SceneDocument is { } resident)
            {
                // The scene pin owns this detached payload. Admit its retention before the producer clones it.
                long bytes = 0;
                foreach (var patch in resident.Surfaces.Patches.Values)
                    bytes = checked(bytes + 128 + MapSceneAdmission.PatchBytes(patch));
                scan.RetainBytes(bytes);
                native = MapDocumentSurfaceSource.Capture(resident);
            }
            else native = source.SceneSurfaces;
            var root = source.Manifest;
            var nativeTransforms = (root.NativeResourceTransforms ?? []).ToArray();
            var transformsByOwner = nativeTransforms.ToLookup(p => p.OwnerId, StringComparer.Ordinal);
            var transformsByKey = nativeTransforms.ToLookup(p => (p.Kind, p.OwnerId));
            var attachmentsByOwner = (root.Prefabs?.Attachments ?? []).ToLookup(a => a.PlacementId, StringComparer.Ordinal);
            var rootLightIds = (root.BuildingIntent?.Lights ?? []).Select(l => l.Id).ToHashSet(StringComparer.Ordinal);
            MapBoundDocumentValidation.RequireRoots(root.NativeAssets, assets);
            if (MapFoliageValidation.ValidateBound(root, assets).Count != 0)
                throw new MapSceneSourceException(MapSceneStatus.Invalid, "Foliage metadata does not bind to the verified closure.");
            if (!float.IsFinite(root.Bounds.MinX) || !float.IsFinite(root.Bounds.MinZ) || !float.IsFinite(root.Bounds.MaxX) ||
                !float.IsFinite(root.Bounds.MaxZ) || root.Bounds.MinX > root.Bounds.MaxX || root.Bounds.MinZ > root.Bounds.MaxZ)
                throw new ArgumentException("Invalid scene world bounds.");
            var nativeEntries = MapSceneMetadataReader.ReadSurfaces(native, scan);
            var registry = new MapPrefabIdentityRegistry(root);
            registry.AddSurfaces(native.Surfaces.Select(s => s.Id));
            foreach (var entry in nativeEntries) registry.AddRecords(entry.Key, entry.RecordIds);
            string rootDigest = MapSurfaceSemantics.RootDigest(root);
            var owners = new Dictionary<string, MapSceneOwner>(StringComparer.Ordinal);
            var rows = new Dictionary<MapSceneResourceKey, MapSceneResourceMetadata>();
            var producing = new Dictionary<MapSceneResourceKey, string>();
            var nativeRefs = native.Surfaces.ToDictionary(s => s.Id, StringComparer.Ordinal);
            foreach (var surface in nativeRefs.Values) scan.Retain(checked(128L + Encoding.UTF8.GetByteCount(surface.Id)));
            foreach (var entry in nativeEntries)
            {
                var pose = transformsByKey[(MapPrefabChildKind.Surface, entry.Key.SurfaceId)].FirstOrDefault()?.Transform ?? new(0, 0, 0, 0, 1);
                var bounds = MapSceneResourceBounds.Rectangle(entry.Cells, nativeRefs[entry.Key.SurfaceId].Frame, pose);
                var key = MapSceneAcquisition.PatchKey(entry.Key);
                rows.Add(key, new(key, default, bounds, entry.SemanticSha256));
            }
            var populatedNative = nativeEntries.Select(e => e.Key.SurfaceId).ToHashSet(StringComparer.Ordinal);
            foreach (var surface in nativeRefs.Values)
                if (!populatedNative.Contains(surface.Id))
                {
                    var key = new MapSceneResourceKey("Surface", surface.Id);
                    rows.Add(key, new(key, default, new(root.Bounds.MinX, double.NegativeInfinity, root.Bounds.MinZ,
                        root.Bounds.MaxX, double.PositiveInfinity, root.Bounds.MaxZ), surface.SemanticSha256));
                }
            var foliageMetadata = new MapFoliageWindowMetadata(root.Foliage);
            foreach (var entry in nativeEntries)
            {
                var surface = nativeRefs[entry.Key.SurfaceId];
                var pose = transformsByKey[(MapPrefabChildKind.Surface, surface.Id)].FirstOrDefault()?.Transform ?? new(0, 0, 0, 0, 1);
                foliageMetadata.AddFloor(surface, entry.Cells, pose, scan);
            }
            foreach (var surface in nativeRefs.Values)
                foliageMetadata.AddNative(new(MapPrefabChildKind.Surface, surface.Id),
                    transformsByKey[(MapPrefabChildKind.Surface, surface.Id)].FirstOrDefault()?.Transform ?? new(0, 0, 0, 0, 1), scan);
            foreach (var entry in nativeEntries)
                foreach (string record in entry.RecordIds)
                    foliageMetadata.AddNative(new(MapPrefabChildKind.Record, record),
                        transformsByKey[(MapPrefabChildKind.Record, record)].FirstOrDefault()?.Transform ?? new(0, 0, 0, 0, 1), scan);
            var poses = new Dictionary<string, MapEffectiveTransform>(StringComparer.Ordinal);
            var requiredAssets = new HashSet<string>(StringComparer.Ordinal);
            var dependencyEdges = new Dictionary<MapSceneResourceKey, HashSet<MapSceneResourceKey>>();
            var externalLights = new List<(MapSceneResourceKey Key, string Owner, MapEffectiveTransform Local, MapLocalLightDoc Value)>();
            var numeric = new HashSet<long>();
            var allNames = new HashSet<string>(StringComparer.Ordinal);
            var shapes = new MapSceneShapeReach(assets, scan);
            MapEffectiveResolutionInput Empty() => new(Array.Empty<MapPlacement>(), null, null, null, new(), null, rootDigest);

            void AddOwner(string id, MapTileCoord storage, MapEffectiveResolutionInput input, bool prepaidWater = false)
            {
                if (owners.ContainsKey(id)) throw new MapDocumentException("Duplicate scene owner.");
                // Metadata row admission precedes effective remap/copy work.
                long prospective = input.Placements.Count + (input.Prefabs?.Instances.Sum(i => i.Bindings.Entries.Count) ?? 0) +
                    (input.BuildingIntent?.Lights.Count ?? 0) + (input.BuildingIntent?.Markers.Count ?? 0) +
                    (input.BuildingIntent?.RoofLinks.Count ?? 0) + (input.BuildingIntent?.RoofGroups.Count ?? 0) + (prepaidWater ? 0 : input.Water?.Bodies.Count ?? 0);
                if (prospective > scan.Limits.MaxMetadataRows - scan.MetadataRows) throw new MapSceneCapacityException(nameof(MapSceneLimits.MaxMetadataRows), scan.MetadataRows + prospective, scan.Limits.MaxMetadataRows);
                var expanded = MapEffectiveResolutionKernel.Enumerate(input, assets, metadataOnly: true);
                var ownerRows = new List<MapSceneResourceKey>();
                MapBox3? groupBounds = null;
                foreach (var child in expanded.Children)
                {
                    if (child is MapPrefabPlacementChild placement)
                    {
                        requiredAssets.Add(placement.Value.AssetId!);
                        if (!allNames.Add(placement.Value.Id)) throw new MapDocumentException("Duplicate scene placement identity.");
                        if (placement.Value.NumericId is { } number && (number <= 0 || number > root.NumericIdHighWaterMark || !numeric.Add(number)))
                            throw new MapDocumentException("Invalid or duplicate scene numeric identity.");
                    }
                    var key = Key(child.Key);
                    var pose = expanded.Transforms.GetValueOrDefault(MapEffectiveContext.Key(child.Key.Kind, child.Key.Key), new(0, 0, 0, 0, 1));
                    poses.TryAdd(MapEffectiveContext.Key(child.Key.Kind, child.Key.Key), pose);
                    var bounds = MapSceneResourceBounds.Child(child, pose, assets, shapes);
                    if (bounds is { } b) groupBounds = groupBounds?.Union(b) ?? b;
                    AddRow(key, bounds, child, pose);
                    if (child is MapPrefabLightChild light && expanded.WorldLightFrames.TryGetValue(light.Value.Id, out var local) && light.Value.Owner is { } owner)
                        externalLights.Add((key, owner.Id, MapEffectiveTransform.From(local), light.Value));
                }
                foreach (var light in input.BuildingIntent?.Lights ?? [])
                {
                    var key = new MapSceneResourceKey("Light", light.Id);
                    var frame = input.NativeResourceTransforms?.FirstOrDefault(t => t.Kind == MapPrefabChildKind.Light && t.OwnerId == light.Id)?.Transform
                        ?? MapEffectiveAnnotations.LightFrame(poses, light) ?? new(0, 0, 0, 0, 1);
                    var b = MapSceneResourceBounds.Light(light, frame);
                    groupBounds = groupBounds?.Union(b) ?? b;
                    AddRow(key, b, new MapPrefabLightChild(new(MapPrefabChildKind.Light, light.Id), light));
                    if (light.Owner is { } owner) externalLights.Add((key, owner.Id, frame, light));
                }
                foreach (var marker in input.BuildingIntent?.Markers ?? [])
                    AddRow(new("Marker", marker.Id), MapSceneResourceBounds.Point(marker.Position, input.NativeResourceTransforms?.FirstOrDefault(t =>
                        t.Kind == MapPrefabChildKind.Marker && t.OwnerId == marker.Id)?.Transform ?? new(0, 0, 0, 0, 1)),
                        new MapPrefabMarkerChild(new(MapPrefabChildKind.Marker, marker.Id), marker));
                foreach (var body in input.Water?.Bodies ?? [])
                    AddRow(new("WaterBody", body.Id), MapSceneResourceBounds.Domain(body.Domain, new(0, 0, 0, 0, 1)),
                        new MapPrefabWaterChild(new(MapPrefabChildKind.WaterBody, body.Id), body));
                foreach (var link in input.BuildingIntent?.RoofLinks ?? []) AddRow(new("RoofLink", link.Id), null, new MapPrefabRoofChild(new(MapPrefabChildKind.RoofLink, link.Id), link));
                foreach (var group in input.BuildingIntent?.RoofGroups ?? []) AddRow(new("RoofGroup", group.Id), null, new MapPrefabRoofGroupChild(new(MapPrefabChildKind.RoofGroup, group.Id), group));
                var fallback = groupBounds ?? new MapBox3(root.Bounds.MinX, double.NegativeInfinity, root.Bounds.MinZ,
                    root.Bounds.MaxX, double.PositiveInfinity, root.Bounds.MaxZ);
                foreach (var key in ownerRows) if (double.IsNaN(rows[key].Bounds.MinX)) rows[key] = rows[key] with { Bounds = fallback };
                owners.Add(id, new(id, storage, input, Array.AsReadOnly(ownerRows.ToArray())));

                void AddRow(MapSceneResourceKey key, MapBox3? bounds, MapPrefabChild child, MapEffectiveTransform? pose = null)
                {
                    foliageMetadata.Add(child, pose ?? transformsByKey[(child.Key.Kind, child.Key.Key)].FirstOrDefault()?.Transform ?? new(0, 0, 0, 0, 1), scan);
                    var dependencies = MapSceneResourceBounds.Dependencies(child).ToHashSet();
                    if (!prepaidWater || child is not MapPrefabWaterChild)
                    {
                        long bytes = MapSceneResourceBounds.MetadataBytes(child);
                        scan.Retain(checked(bytes + Encoding.UTF8.GetByteCount(id)), checked(dependencies.Count + 1));
                    }
                    if (!producing.TryAdd(key, id)) throw new MapDocumentException("Duplicate effective scene resource identity.");
                    ownerRows.Add(key);
                    dependencyEdges.Add(key, dependencies);
                    rows.Add(key, new(key, storage, bounds ?? new(double.NaN, 0, 0, 0, 0, 0), MapPrefabIdentity.ChildDigest(child)));
                }
            }

            foreach (var tile in source.Tiles.Entries)
            {
                foreach (var placement in MapSceneMetadataReader.Read(source, tile, scan))
                {
                    registry.AddPlacements(new[] { placement });
                    var copy = MapPrefabReferenceWalker.Copy(placement, placement.Id, placement.SupportBinding);
                    var owned = attachmentsByOwner[placement.Id].ToArray();
                    var prefabs = owned.Length == 0 ? null : new MapPrefabSet(1, 1, [], []) { Attachments = owned };
                    var posesForOwner = owned.SelectMany(a => a.Children).Select(c => c.WorldId).Prepend(placement.Id)
                        .Distinct(StringComparer.Ordinal).SelectMany(owner => transformsByOwner[owner]).ToArray();
                    AddOwner("Placement:" + placement.Id, tile.Coord, Empty() with { Placements = new[] { copy }, Prefabs = prefabs, NativeResourceTransforms = posesForOwner });
                }
            }
            registry.Finish();
            MapPrefabValidation.RequireDocument(root, assets);
            MapMarkerExpansion.RequireUnique(root, assets);
            foreach (var instance in root.Prefabs?.Instances ?? [])
            {
                if (instance.Removed) continue;
                if (DemandDiagnostics.Check(scan, "MapSceneSource.MaxMetadataRows.1", "MaxMetadataRows", scan.MetadataRows, instance.Bindings.Entries.Count, scan.Limits.MaxMetadataRows, instance.Bindings.Entries.Count > scan.Limits.MaxMetadataRows - scan.MetadataRows, CapacityDemandLifetime.Inventory)) throw new MapSceneCapacityException(nameof(MapSceneLimits.MaxMetadataRows), scan.MetadataRows + instance.Bindings.Entries.Count, scan.Limits.MaxMetadataRows);
                scan.RetainBytes(MapSceneResourceBounds.InstanceBytes(instance));
                var resource = assets.GetResource(instance.ResourceId);
                if (resource.Kind != MapResourceKind.Prefab || resource.Reference.Sha256 != instance.ResourceSha256)
                    throw new MapDocumentException("Scene instance differs from its verified prefab pin.");
                AddOwner("Prefab:" + instance.Id, MapTileGrid.CoordOf(instance.Transform.Position.X, instance.Transform.Position.Z, source.Tiles.TileSize),
                    Empty() with { Prefabs = new(1, 1, new[] { MapPrefabJson.Copy(instance) }, []) });
            }
            foreach (var light in root.BuildingIntent?.Lights ?? []) AddOwner("Light:" + light.Id, default,
                Empty() with { BuildingIntent = new(1, [], [], new[] { light }, []), NativeResourceTransforms = transformsByKey[(MapPrefabChildKind.Light, light.Id)].ToArray() });
            foreach (var storey in root.BuildingIntent?.Storeys ?? [])
                scan.Retain(checked(64L + Encoding.UTF8.GetByteCount(storey.Id) + Encoding.UTF8.GetByteCount(storey.Name)));
            foreach (var marker in root.BuildingIntent?.Markers ?? []) AddOwner("Marker:" + marker.Id, default,
                Empty() with { BuildingIntent = new(1, [], [], [], new[] { MapPrefabJson.Copy(marker) }), NativeResourceTransforms = transformsByKey[(MapPrefabChildKind.Marker, marker.Id)].ToArray() });
            foreach (var link in root.BuildingIntent?.RoofLinks ?? []) AddOwner("RoofLink:" + link.Id, default,
                Empty() with { BuildingIntent = new(1, new[] { MapPrefabJson.Copy(link) }, [], [], []) });
            foreach (var group in root.BuildingIntent?.RoofGroups ?? []) AddOwner("RoofGroup:" + group.Id, default,
                Empty() with { BuildingIntent = new(1, [], new[] { MapPrefabJson.Copy(group) }, [], []) });
            foreach (var body in root.Water?.Bodies ?? [])
            {
                var water = MapSceneWaterCapture.Copy(root.Water!, body, scan);
                AddOwner("WaterBody:" + body.Id, default, Empty() with { Water = water }, prepaidWater: true);
            }
            foreach (var reach in externalLights)
            {
                if (!poses.TryGetValue("Placement:" + reach.Owner, out var ownerPose)) throw new MapDocumentException("Missing scene light owner.");
                bool explicitFrame = transformsByKey.Contains((MapPrefabChildKind.Light, reach.Key.Id));
                var pose = explicitFrame ? poses.GetValueOrDefault("Light:" + reach.Key.Id, reach.Local)
                    : rootLightIds.Contains(reach.Key.Id) ? ownerPose : MapEffectiveTransform.Compose(ownerPose, reach.Local);
                rows[reach.Key] = rows[reach.Key] with { Bounds = MapSceneResourceBounds.Light(reach.Value, pose) };
            }
            var dependenciesByOwner = owners.Keys.ToDictionary(k => k, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
            foreach (var (resource, dependencies) in dependencyEdges)
                foreach (var dependency in dependencies)
                    if (producing.TryGetValue(dependency, out string? owner))
                    {
                        dependenciesByOwner[producing[resource]].Add(owner);
                        if (resource.Kind is "RoofLink" or "RoofGroup" or "Light" || resource.Kind == "Surface" &&
                            owners[producing[resource]].Input.Prefabs is not null)
                            dependenciesByOwner[owner].Add(producing[resource]);
                    }
            // Non-geometric annotation groups get the finite reach of their referenced producers.
            foreach (var pair in rows.ToArray())
                if (pair.Key.Kind is "RoofLink" or "RoofGroup" or "Storey")
                {
                    MapBox3? b = null;
                    foreach (var dependency in dependencyEdges[pair.Key]) if (rows.TryGetValue(dependency, out var row)) b = b?.Union(row.Bounds) ?? row.Bounds;
                    if (b is { } box) rows[pair.Key] = pair.Value with { Bounds = box };
                }
            var foliageStorage = source.SceneDocument?.Tiles?.Foliage ?? source.Tiles.Foliage;
            var foliage = CaptureFoliage(root.Foliage, foliageStorage, assets, rows, scan, requiredAssets);
            CheckManifest(source.SceneDirectory, manifestDigest, scan);
            if (manifestDigest != source.Tiles.ManifestSha256) throw new MapSceneSourceException(MapSceneStatus.Stale, "Source generation changed during capture.");
            var metadata = Array.AsReadOnly(rows.Values.OrderBy(r => r.Key.Kind, StringComparer.Ordinal).ThenBy(r => r.Key.Id, StringComparer.Ordinal).ToArray());
            string generation = manifestDigest ?? MapCanonical.HashHex(w =>
            {
                w.WriteStartArray(); w.WriteStringValue(rootDigest); w.WriteStringValue(native.SnapshotId);
                foreach (var tile in source.Tiles.Entries) { w.WriteNumberValue(tile.Coord.X); w.WriteNumberValue(tile.Coord.Z); w.WriteStringValue(tile.Hash); }
                foreach (var row in metadata) { w.WriteStringValue(row.Key.Kind); w.WriteStringValue(row.Key.Id); w.WriteStringValue(row.Digest); }
                w.WriteEndArray();
            });
            foliageMetadata.Finish();
            scan.RetainBytes(checked(40L * (root.Foliage?.Layers.Sum(l => (long)l.Chunks.Count) ?? 0)));
            var data = new MapSceneData(generation, manifestDigest, source.SceneDirectory, assets, scan.Limits, native, source.Tiles.TileSize,
                root.Bounds.MinX, root.Bounds.MinZ, root.Bounds.MaxX, root.Bounds.MaxZ, rootDigest, owners, producing,
                dependenciesByOwner, metadata, new(metadata), Array.AsReadOnly(requiredAssets.Order(StringComparer.Ordinal).ToArray()),
                foliage, foliageStorage, root.Foliage, scan.Work(true), root.BuildingIntent?.Roofs ?? [], root.BuildingIntent?.Storeys ?? [],
                nativeEntries, Array.AsReadOnly(nativeTransforms), dependencyEdges, foliageMetadata);
            return new(MapSceneSourceStatus.Ready, new(data), scan.Work(true));
        }
        catch (Exception error) when (Classify(error) is not null)
        {
            return new(Classify(error)!.Value, null, scan.Work(false)) { Capacity = MapSceneCapacity.From(error), Findings = Array.AsReadOnly(new[] { error.Message }) };
        }
    }

    public MapSceneSourceResult WithRenderExtents(MapAssetExtentProofSet proofs)
    {
        ArgumentNullException.ThrowIfNull(proofs);
        if (proofs.ClosureHash != ClosureHash || RequiredAssetIds.Any(id => !proofs.ByAsset.ContainsKey(id)))
            return new(MapSceneSourceStatus.Invalid, null, _data.CaptureWork with { RetainedBytes = 0 });
        foreach (string id in RequiredAssetIds)
        {
            var asset = _data.Assets.GetAsset(id); var proof = proofs.ByAsset[id];
            if (proof.SourceUnitsToMetres != asset.SourceUnitsToMetres || proof.ValidationVersion < 1 ||
                proof.Resources.Count != 1 + asset.LodResourceIds.Count ||
                proof.Resources.Any(r => _data.Assets.GetResource(r.ResourceId).Reference.Sha256 != r.Sha256 || !Contains(r.DeclaredBounds, r.VerifiedBounds)))
                return new(MapSceneSourceStatus.Invalid, null, _data.CaptureWork with { RetainedBytes = 0 });
        }
        if (DemandDiagnostics.Check(this, "MapSceneSource.MaxMetadataBytes.1", "MaxMetadataBytes", _data.CaptureWork.RetainedBytes, proofs.MetadataBytes, _data.Limits.MaxMetadataBytes, proofs.MetadataBytes > _data.Limits.MaxMetadataBytes - _data.CaptureWork.RetainedBytes, CapacityDemandLifetime.Inventory))
            return new(MapSceneSourceStatus.CapacityExceeded, null, _data.CaptureWork with { RetainedBytes = 0 })
            {
                Capacity = new(nameof(MapSceneLimits.MaxMetadataBytes), (decimal)_data.CaptureWork.RetainedBytes + proofs.MetadataBytes, _data.Limits.MaxMetadataBytes),
                Findings = [new MapSceneCapacity(nameof(MapSceneLimits.MaxMetadataBytes), (decimal)_data.CaptureWork.RetainedBytes + proofs.MetadataBytes, _data.Limits.MaxMetadataBytes).Message]
            };
        return new(MapSceneSourceStatus.Ready, new(_data, proofs), _data.CaptureWork with { RetainedBytes = checked(_data.CaptureWork.RetainedBytes + proofs.MetadataBytes) });
    }
    static bool Contains(MapLocalBounds declared, MapLocalBounds actual) => actual.Min.X >= declared.Min.X && actual.Min.Y >= declared.Min.Y &&
        actual.Min.Z >= declared.Min.Z && actual.Max.X <= declared.Max.X && actual.Max.Y <= declared.Max.Y && actual.Max.Z <= declared.Max.Z;
    public MapScopedSceneResult Acquire(MapSceneScope scope) => MapSceneAcquisition.Run(this, scope);
    internal static MapSceneResourceKey Key(MapPrefabChildKey key) => new(key.Kind.ToString(), key.Key);

    internal static void CheckManifest(string? directory, string? expected, MapSceneScan? scan = null)
    {
        if (directory is null) return;
        using var file = new FileStream(Path.Combine(directory, MapTiledFile.ManifestName), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        scan?.Scan(file.Length);
        if (Convert.ToHexStringLower(SHA256.HashData(file)) != expected) throw new MapSceneSourceException(MapSceneStatus.Stale, "Pinned manifest generation is stale.");
    }
    internal static MapSceneSourceStatus? Classify(Exception error) => error switch
    {
        MapSceneCapacityException or MapSurfaceCapacityException => MapSceneSourceStatus.CapacityExceeded,
        MapSceneSourceException e => Enum.Parse<MapSceneSourceStatus>(e.Status.ToString()),
        FileNotFoundException or DirectoryNotFoundException => MapSceneSourceStatus.Missing,
        IOException or UnauthorizedAccessException or System.Text.Json.JsonException => MapSceneSourceStatus.Corrupt,
        MapExactOverflowException or OverflowException => MapSceneSourceStatus.NotRepresentable,
        ArgumentException => MapSceneSourceStatus.Invalid,
        MapDocumentException when MapSceneCapacity.From(error) is not null => MapSceneSourceStatus.CapacityExceeded,
        MapDocumentException => MapSceneSourceStatus.Corrupt,
        _ => null,
    };

    static MapFoliageSet? CaptureFoliage(MapFoliageSet? foliage, MapFoliageStorageIndex? storage, MapAssetClosure assets,
        Dictionary<MapSceneResourceKey, MapSceneResourceMetadata> rows, MapSceneScan scan, HashSet<string> requiredAssets)
    {
        if (foliage is null) return null;
        foreach (var layer in foliage.Layers)
        {
            scan.Retain(MapSceneResourceBounds.FoliageMetadataBytes(layer));
            foreach (var model in layer.Models) { _ = assets.GetAsset(model.AssetId); requiredAssets.Add(model.AssetId); }
            foreach (var chunk in layer.ChunkDigests)
            {
                var key = MapSceneAcquisition.FoliageKey(layer.Id, chunk.Key);
                var bounds = MapSceneResourceBounds.Foliage(layer, chunk.Key, assets);
                scan.Retain(checked(Encoding.UTF8.GetByteCount(key.Id) + 64L));
                rows.Add(key, new(key, storage?.TryGetChunk(layer.Id, chunk.Key, out var stored) == true ? stored.OwnerRegion : default, bounds, chunk.Sha256));
            }
        }
        return new(foliage.PayloadVersion, foliage.Layers.Select(l => MapFoliageLayer.FromStorage(l, l.ChunkDigests, [])).ToArray());
    }
}

internal sealed record MapSceneOwner(string Id, MapTileCoord StorageOwner, MapEffectiveResolutionInput Input, IReadOnlyList<MapSceneResourceKey> Resources);
internal sealed record MapSceneData(string Generation, string? ManifestDigest, string? Directory, MapAssetClosure Assets, MapSceneLimits Limits,
    IMapSurfaceSource Surfaces, float TileSize, double MinX, double MinZ, double MaxX, double MaxZ, string RootDigest,
    IReadOnlyDictionary<string, MapSceneOwner> Owners, IReadOnlyDictionary<MapSceneResourceKey, string> Producing,
    IReadOnlyDictionary<string, HashSet<string>> Dependencies, IReadOnlyList<MapSceneResourceMetadata> Metadata, MapSceneSpatialIndex Index,
    IReadOnlyList<string> RequiredAssets, MapFoliageSet? Foliage, MapFoliageStorageIndex? FoliageStorage, MapFoliageSet? ResidentFoliage, MapSceneWork CaptureWork,
    IReadOnlyList<MapPrefabReference> Roofs, IReadOnlyList<KhaozEngine.MapDoc.Interiors.MapStoreyDoc> Storeys, IReadOnlyList<MapSurfaceIndexEntry> NativeEntries,
    IReadOnlyList<MapStandaloneResourceTransform> NativeTransforms, IReadOnlyDictionary<MapSceneResourceKey, HashSet<MapSceneResourceKey>> ResourceDependencies, MapFoliageWindowMetadata FoliageMetadata)
{
    internal IReadOnlyDictionary<(MapPrefabChildKind Kind, string Id), MapStandaloneResourceTransform> NativePoseByKey { get; } = NativeTransforms.ToDictionary(t => (t.Kind, t.OwnerId));
    internal IReadOnlyDictionary<MapSceneResourceKey, MapSceneResourceMetadata> MetadataByKey { get; } = Metadata.ToDictionary(r => r.Key);
    internal IReadOnlyDictionary<string, IReadOnlyList<MapSurfaceIndexEntry>> NativeBySurface { get; } = NativeEntries.GroupBy(e => e.Key.SurfaceId).ToDictionary(g => g.Key, g => (IReadOnlyList<MapSurfaceIndexEntry>)Array.AsReadOnly(g.ToArray()), StringComparer.Ordinal);
    internal IReadOnlySet<MapSceneResourceKey> GlobalKeys { get; } = Owners.Values.SelectMany(o => o.Input.Water?.Media ?? []).Select(m => new MapSceneResourceKey("WaterMedium", m.Key))
        .Concat(Storeys.Select(s => new MapSceneResourceKey("Storey", s.Id))).ToHashSet();
    internal IReadOnlyDictionary<MapSceneResourceKey, MapSurfaceIndexEntry> NativePatches { get; } = NativeEntries.ToDictionary(e => MapSceneAcquisition.PatchKey(e.Key));
    internal IReadOnlyDictionary<string, MapSurfaceRef> NativeRefs { get; } = Surfaces.Surfaces.ToDictionary(s => s.Id, StringComparer.Ordinal);
    internal IReadOnlyDictionary<string, MapPatchKey> NativeRecords { get; } = NativeEntries.SelectMany(e => e.RecordIds.Select(id => (Id: id, e.Key))).ToDictionary(v => v.Id, v => v.Key, StringComparer.Ordinal);
    internal IReadOnlyDictionary<MapSceneResourceKey, MapFoliageChunk> ResidentFoliageChunks { get; } =
        (ResidentFoliage?.Layers ?? []).SelectMany(l => l.Chunks.Select(c => (Key: MapSceneAcquisition.FoliageKey(l.Id, c.Key), Chunk: c)))
        .ToDictionary(v => v.Key, v => v.Chunk);
    internal IReadOnlyDictionary<MapSceneResourceKey, (MapFoliageLayer Layer, MapFoliageChunkKey Chunk)> FoliageRows { get; } =
        (Foliage?.Layers ?? []).SelectMany(l => l.ChunkDigests.Select(c => (Key: MapSceneAcquisition.FoliageKey(l.Id, c.Key), Layer: l, Chunk: c.Key)))
        .ToDictionary(v => v.Key, v => (v.Layer, v.Chunk));
}
