using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapDoc.Render3D;

public static class NativeMapPlacements
{
    public static MapPlacementRenderBatch Prepare(MapScopedScene scene, MapPreparedInputLease input,
        MapPlacementPolicy policy, MapPlacementRouting routing) => Prepare(scene, input, policy, routing, null);

    internal static MapPlacementRenderBatch Prepare(MapScopedScene scene, MapPreparedInputLease input,
        MapPlacementPolicy policy, MapPlacementRouting routing, MapPlacementCellInventory? cells)
    {
        ArgumentNullException.ThrowIfNull(input);
        var retained = input.Retain();
        try
        {
            var batch = PrepareCore(scene, retained.Assets, policy, routing, cells);
            batch.AttachInput(retained); return batch;
        }
        catch { retained.Dispose(); throw; }
    }

    public static MapPlacementRenderBatch Prepare(MapScopedScene scene, MapRenderAssets assets,
        MapPlacementPolicy policy, MapPlacementRouting routing) => PrepareCore(scene, assets, policy, routing, null);

    static MapPlacementRenderBatch PrepareCore(MapScopedScene scene, MapRenderAssets assets,
        MapPlacementPolicy policy, MapPlacementRouting routing, MapPlacementCellInventory? cells)
    {
        ArgumentNullException.ThrowIfNull(scene); ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(policy); ArgumentNullException.ThrowIfNull(routing);
        routing.Validate(scene, policy);
        var admission = MapSceneAdmission.Current ?? new MapSceneAdmission(scene.Source.Data.Limits);
        using var activation = admission.Enter();
        using var preparation = admission.BeginPreparation();
        if (scene.Witness.ClosureHash != assets.Closure.Hash) throw new MapDocumentException("Placement assets belong to another closure.");
        cells ??= MapPlacementCellInventory.Capture(scene, routing);
        using var cellDependency = cells.PreparationOwnership is { } cellOwner ? preparation.WorkspaceOwner.Borrow(cellOwner) : null;
        foreach (var placement in scene.Placements)
            if (!assets.ById.ContainsKey(placement.AssetId)) throw new MapRenderAssetRefusal(MapRenderAssetStatus.Missing, "Placement has no selected asset: " + placement.AssetId);
        foreach (var placement in cells.Cells.SelectMany(cell => cell.Members))
            if (!assets.ById.ContainsKey(placement.AssetId)) throw new MapRenderAssetRefusal(MapRenderAssetStatus.Missing, "Canonical cell member has no selected asset: " + placement.AssetId);
        try
        {
            admission.ReserveBytes(checked(1024L + 512L * assets.ById.Count + 128L * scene.Placements.Count +
                assets.ById.Values.Sum(a => a.Lods.Sum(l => 128L + 32L * l.BorrowParts.Count))));
            var instances = new List<NativePropInstance>();
            var parts = new List<MapPlacementPart>();
            var boundsById = new Dictionary<string, MapBox3>(StringComparer.Ordinal);
            var posesById = new Dictionary<string, MapEffectiveTransform>(StringComparer.Ordinal);
            var handles = new Dictionary<string, IReadOnlyList<NativePropLod>>(StringComparer.Ordinal);
            int nextHandle = 1;
            foreach (var asset in assets.ById.Values)
            {
                var tiers = new List<NativePropLod>();
                foreach (var lod in asset.Lods)
                {
                    var ids = new List<MeshHandle>();
                    foreach (var part in lod.BorrowParts) { ids.Add(new(nextHandle++)); }
                    tiers.Add(new(lod.Index, ids));
                }
                handles.Add(asset.AssetId, Array.AsReadOnly(tiers.ToArray()));
            }
            var roofs = scene.CapturedBuildingIntent.Roofs.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
            roofs.UnionWith(cells.Cells.SelectMany(c => c.RoofIds));
            var selectedPlacements = scene.Placements.Where(p => routing.Entries[p.PlacementId] == MapPlacementRoute.Ordinary)
                .Select(p => (Scene: scene, Placement: p))
                .Concat(cells.Cells.SelectMany(cell => cell.Members.Select(p => (Scene: cell.Scene, Placement: p))));
            foreach (var (placementScene, placement) in selectedPlacements)
            {
                admission.ReserveBytes(256);
                var pose = Pose(placementScene, placement); var anchor = Anchor(pose);
                posesById.Add(placement.PlacementId, pose);
                var instance = new NativePropInstance(placement.PlacementId, placement.NumericId, placement.AssetId,
                    MapRenderTransform.ToLocal(pose, anchor), new(anchor.X, anchor.Y, anchor.Z))
                {
                    WeldPlacement = policy.Layers.Any(l => l.HlodDistance > 0 && l.HlodWeldCell > 0 && l.AssetIds.Contains(placement.AssetId))
                    ? MapRenderTransform.ToWeldPlacement(placement.AssetId, pose) : null
                };
                instances.Add(instance);
                var bounds = Bounds(assets.ById[placement.AssetId].Descriptor, pose);
                boundsById.Add(placement.PlacementId, bounds);
                var kind = roofs.Contains(placement.PlacementId) ? MapRenderKind.Roof : MapRenderKind.Placement;
                foreach (var lod in assets.ById[placement.AssetId].Lods)
                    for (int i = 0; i < lod.BorrowParts.Count; i++)
                    {
                        var part = lod.BorrowParts[i];
                        var key = new MapRenderKey(kind, placement.PlacementId, "part/" + i, lod.Index);
                        var payload = MapPlacementPayloadPreparation.Copy(part.Mesh, part.Maps);
                        string digest = MapPlacementDigest.Hash(w =>
                        {
                            w.WriteStartArray(); w.WriteStringValue(assets.Closure.GetResource(lod.ResourceId).Reference.Sha256);
                            w.WriteNumberValue(assets.ById[placement.AssetId].Descriptor.SourceUnitsToMetres); w.WriteNumberValue(i); w.WriteEndArray();
                        });
                        parts.Add(new(key, handles[placement.AssetId][lod.Index].Parts[i], anchor, bounds, payload, digest));
                    }
            }
            var clusters = new List<NativePropClusterCpuBuild>();
            var ordinary = new List<PropLayer>();
            var roofLayers = new List<PropLayer>();
            foreach (var layer in policy.Layers)
            {
                var selected = instances.Where(i => layer.AssetIds.Contains(i.AssetId)).ToArray();
                if (selected.Length == 0) continue;
                if (layer.HlodDistance == 0)
                {
                    var plain = selected.Where(i => !roofs.Contains(i.Id)).ToArray();
                    var caps = selected.Where(i => roofs.Contains(i.Id)).ToArray();
                    if (plain.Length > 0) ordinary.Add(Shared(plain, layer));
                    if (caps.Length > 0) roofLayers.Add(Shared(caps, layer));
                    continue;
                }
                foreach (var cell in selected.GroupBy(i => MapPlacementCells.Key(layer.Id, posesById[i.Id], scene.Source.Data.TileSize, roofs.Contains(i.Id)))
                    .OrderBy(c => c.Key.X).ThenBy(c => c.Key.Z))
                {
                    var members = cell.OrderBy(i => i.Id, StringComparer.Ordinal).ToArray();
                    using var cellPreparation = admission.BeginPreparation();
                    var domain = MapPlacementCells.Domain(cell.Key, scene.Source.Data.TileSize, members.Min(i => i.Anchor.Y));
                    using var sourcePreparation = admission.Workspace();
                    var sources = new Dictionary<string, GltfMesh>(StringComparer.Ordinal);
                    foreach (string id in members.Select(i => i.AssetId).Distinct(StringComparer.Ordinal))
                    {
                        var source = assets.ById[id].Lods[^1];
                        sourcePreparation.Reserve(checked(8 * source.BorrowParts.Sum(p => Bytes(p.Mesh, p.Maps))));
                        sources.Add(id, layer.HlodWeldCell > 0
                            ? MapPlacementProxySource.Load(assets.ById[id], source, assets.Closure)
                            : MapPlacementProxySource.Concatenate(source.BorrowParts));
                    }
                    long mergeBytes = members.Sum(i => Bytes(sources[i.AssetId], default));
                    var (build, clusterBytes) = MapPlacementClusterPreparation.Build(mergeBytes, () =>
                        {
                            var shared = Shared(members, layer).WithHlod(sources, layer.HlodDistance, layer.HlodWeldCell, layer.HlodCrossfadeWidth);
                            var request = new NativePropClusterRequest(cell.Key, 0, domain.Area, shared, members, domain.Anchor);
                            sourcePreparation.Dispose();
                            return request;
                        }, request => PrepareCluster(cells.Get(cell.Key), request));
                    clusters.Add(build);
                    var anchor = new MapSubmissionAnchor(domain.Anchor.X, domain.Anchor.Y, domain.Anchor.Z);
                    string inventory = ClusterInventoryDigest(members, assets.Closure.Hash, layer.HlodWeldCell);
                    if (cell.Key.Cohort == 1)
                        for (int member = 0; member < members.Length; member++)
                        {
                            var contribution = build.BorrowContribution(member);
                            if (contribution.TriangleCount == 0) continue;
                            // The batch owns this detached copy directly. Cell completion must not refund it.
                            var payload = MapPlacementPayloadPreparation.Copy(contribution, default, preparation);
                            var outlineKey = new MapRenderKey(MapRenderKind.Roof, members[member].Id, "outline-hlod", 0);
                            string selectedId = members[member].Id;
                            string digest = MapAnchorRelativeDigest.Hash(anchor, w =>
                            {
                                w.WriteStartArray(); w.WriteStringValue("kemap/roof-outline-contribution/2");
                                w.WriteStringValue(inventory); w.WriteStringValue(selectedId); w.WriteEndArray();
                            });
                            parts.Add(new(outlineKey, new(nextHandle++), anchor, MeshBounds(contribution, anchor), payload, digest));
                        }
                    var mesh = build.BorrowMesh;
                    if (mesh.TriangleCount == 0) { cellPreparation.Complete(clusterBytes); continue; }
                    var bounds = members.Select(i => boundsById[i.Id]).Aggregate(Union);
                    var key = new MapRenderKey(cell.Key.Cohort == 1 ? MapRenderKind.Roof : MapRenderKind.Placement, MapPlacementCells.Owner(cell.Key), "hlod", 0);
                    parts.Add(new(key, new(nextHandle++), anchor, bounds, MapPlacementPayloadPreparation.Copy(mesh, default, preparation),
                        MapAnchorRelativeDigest.Hash(anchor, w =>
                        {
                            w.WriteStartArray(); w.WriteStringValue(routing.PolicyDigest);
                            w.WriteStringValue(inventory); w.WriteEndArray();
                        })));
                    cellPreparation.Complete(clusterBytes);
                }
            }
            var defaults = instances.Where(i => policy.For(i.AssetId) is null).ToArray();
            var ordinaryDefaults = defaults.Where(i => !roofs.Contains(i.Id)).ToArray();
            var roofDefaults = defaults.Where(i => roofs.Contains(i.Id)).ToArray();
            if (ordinaryDefaults.Length > 0) ordinary.Add(Shared(ordinaryDefaults, null));
            if (roofDefaults.Length > 0) roofLayers.Add(Shared(roofDefaults, null));
            // Frozen bindings, cluster lookup slots and the batch's copied list storage.
            long roofMembers = clusters.Where(c => c.Request.Key.Cohort == 1).Sum(c => (long)c.RepresentedIds.Count);
            long policyRows = cells.Cells.Sum(c => (long)(c.RoofEvaluation?.Roofs.Count ?? 0));
            long containers = (long)ordinary.Count + roofLayers.Count + scene.Placements.Count +
                cells.Cells.Sum(c => (long)c.Members.Count) + scene.EffectiveMarkers.Count;
            // Lookup growth includes old/new backing overlap. Existing policy products already have
            // their receipt in this operation and are not debited again for a cohort reference.
            admission.ReserveBytes(checked(1024L + parts.Count * 192L + clusters.Count * 512L +
                policyRows * 128L + roofMembers * 288L + containers * 32L));
            var preparedInventory = scene.Placements.Where(p => routing.Entries[p.PlacementId] != MapPlacementRoute.Cluster)
                .Concat(cells.Cells.SelectMany(cell => cell.Members)).ToArray();
            var result = new MapPlacementRenderBatch(preparedInventory, MapEffectiveAnnotations.Markers(scene), ordinary, roofLayers, parts, routing, clusters, 0, cells);
            long retainedBytes = preparation.DeduplicateSharedProducts(result.RetainedBytes, result.SharedMembershipBytes, cells.PreparationOwnership);
            preparation.EnsureCapacity(retainedBytes, cells.PreparationOwnership);
            cellDependency?.Absorb();
            result.Own(preparation.Complete(retainedBytes));
            return result;

            PropLayer Shared(IReadOnlyList<NativePropInstance> rows, MapPlacementLayer? layer)
            {
                using var layerPreparation = admission.BeginPreparation();
                admission.ReserveBytes(checked(256L + MapNativeInventory.InstanceBytes * rows.Count + handles.Values.Sum(t => 64L + 32L * t.Count + 8L * t.Sum(l => l.Parts.Count))));
                var tiers = rows.Select(i => i.AssetId).Distinct(StringComparer.Ordinal).ToDictionary(id => id, id => handles[id], StringComparer.Ordinal);
                var shared = PropLayer.PlacementLayer(rows, tiers, layer?.DrawRadius ?? policy.DefaultDrawRadius,
                    layer?.LodDistances ?? Array.Empty<float>(), layer?.LodCrossfadeWidth ?? 0, layer?.CastsShadows ?? true, layer?.HlodCrossfadeWidth ?? 0);
                var result = layer is null ? shared : shared.WithIdentity(layer.Id);
                layerPreparation.Complete(checked(MapNativeInventory.InstanceBytes * rows.Count + 64L * tiers.Count + 8L * tiers.Values.Sum(t => t.Count) + 4L * result.LodDistances.Count));
                return result;
            }
        }
        catch (MapSceneCapacityException error) { throw new MapRenderAssetRefusal(MapRenderAssetStatus.CapacityExceeded, error.Message, error); }
    }

    /// <summary>The sole upper entry to native cluster merging. Validate the entire request before the lower
    /// builder enters. Roof and ordinary cohorts retain distinct resource and visibility identities.</summary>
    public static NativePropClusterCpuBuild PrepareCluster(MapScopedScene scene, MapPlacementRouting routing, NativePropClusterRequest request) =>
        PrepareCluster(scene, routing, request, PropClusterRenderer.BuildNativeCpu);

    internal static NativePropClusterCpuBuild PrepareCluster(MapPlacementCellInventory.Cell cell, NativePropClusterRequest request) =>
        PrepareCluster(cell.Scene, cell.Routing, request, cell,
            request.Key.Cohort == 1 ? MapRoofContributionPreparation.Build : PropClusterRenderer.BuildNativeCpu);

    internal static NativePropClusterCpuBuild PrepareCluster(MapScopedScene scene, MapPlacementRouting routing,
        NativePropClusterRequest request, Func<NativePropClusterRequest, NativePropClusterCpuBuild> build)
    {
        ArgumentNullException.ThrowIfNull(scene); ArgumentNullException.ThrowIfNull(routing); ArgumentNullException.ThrowIfNull(request);
        routing.Validate(scene);
        var admission = MapSceneAdmission.Current ?? new MapSceneAdmission(scene.Source.Data.Limits);
        using var activation = admission.Enter();
        using (admission.BeginPreparation())
        {
            var cell = MapPlacementCellInventory.Acquire(scene, routing, request.Key);
            ValidateCluster(cell.Scene, cell.Routing, request, cell);
        }
        long mergeBytes = request.Instances.Sum(i => MapNativeInventory.Mesh(request.BorrowLayer.HlodSourceMeshes![i.AssetId]));
        try
        {
            // Canonical validation products end here. The returned build owns its independent receipt.
            return MapPlacementClusterPreparation.Build(mergeBytes, () => request, build, publishToParent: false).Build;
        }
        catch (MapSceneCapacityException error) { throw new MapRenderAssetRefusal(MapRenderAssetStatus.CapacityExceeded, error.Message, error); }
    }

    static NativePropClusterCpuBuild PrepareCluster(MapScopedScene scene, MapPlacementRouting routing,
        NativePropClusterRequest request, MapPlacementCellInventory.Cell cell,
        Func<NativePropClusterRequest, NativePropClusterCpuBuild> build)
    {
        ArgumentNullException.ThrowIfNull(build);
        ValidateCluster(scene, routing, request, cell);
        return build(request);
    }

    static void ValidateCluster(MapScopedScene scene, MapPlacementRouting routing,
        NativePropClusterRequest request, MapPlacementCellInventory.Cell cell)
    {
        ArgumentNullException.ThrowIfNull(request);
        routing.Validate(scene);
        var roofs = scene.CapturedBuildingIntent.Roofs.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        var placements = scene.Placements.ToDictionary(p => p.PlacementId, StringComparer.Ordinal);
        if (request.Instances.Count == 0) throw new MapDocumentException("Native cluster requires its effective members.");
        foreach (var instance in request.Instances)
        {
            if (roofs.Contains(instance.Id) != (request.Key.Cohort == 1) || !routing.Entries.TryGetValue(instance.Id, out var route) || route != MapPlacementRoute.Cluster ||
                !placements.TryGetValue(instance.Id, out var placement) || instance.AssetId != placement.AssetId || instance.NumericId != placement.NumericId ||
                instance.LocalTransform != MapRenderTransform.ToLocal(Pose(scene, placement), new(instance.Anchor.X, instance.Anchor.Y, instance.Anchor.Z)) ||
                (instance.WeldPlacement is { } weldPlacement && !weldPlacement.Equals(MapRenderTransform.ToWeldPlacement(placement.AssetId, Pose(scene, placement)))) ||
                MapPlacementCells.Key(request.Key.LayerId, Pose(scene, placement), scene.Source.Data.TileSize, roofs.Contains(instance.Id)) != request.Key)
                throw new MapDocumentException("Native cluster request disagrees with the effective scene routing.");
            var anchor = Anchor(Pose(scene, placement));
            if (instance.Anchor != new RenderAnchor(anchor.X, anchor.Y, anchor.Z))
                throw new MapDocumentException("Native cluster member disagrees with its effective anchor.");
        }
        var domain = MapPlacementCells.Domain(request.Key, scene.Source.Data.TileSize, request.Instances.Min(i => i.Anchor.Y));
        if (request.Anchor != domain.Anchor || !request.Area.Equals(domain.Area) || request.BorrowLayer.Identity != request.Key.LayerId)
            throw new MapDocumentException("Native cluster request disagrees with its region cell.");
        if (request.BorrowLayer.NativePlacements is not { } native || !native.SequenceEqual(request.Instances))
            throw new MapDocumentException("Native cluster request disagrees with its frozen layer inventory.");
        MapPlacementCellInventory.RequireMembers(cell, request);
        CertifyCluster(request);
    }

    static void CertifyCluster(NativePropClusterRequest request)
    {
        const double coordinateLimit = 2048;
        var sources = request.BorrowLayer.HlodSourceMeshes;
        foreach (var instance in request.Instances)
        {
            if (sources is null || !sources.TryGetValue(instance.AssetId, out var source) || source.Vertices.Length == 0)
                throw new MapRenderAssetRefusal(MapRenderAssetStatus.Invalid, "Native cluster requires every member's source geometry.");
            var transform = new NativePropClusterTransform(instance, request.Anchor);
            foreach (var vertex in source.Vertices)
            {
                var position = transform.Position(vertex.Position);
                // Below 2048 m the final binary32 half ULP stays within the 0.0001 m submission allowance.
                if (!(Math.Abs(position.X) < coordinateLimit && Math.Abs(position.Y) < coordinateLimit && Math.Abs(position.Z) < coordinateLimit))
                    throw new MapRenderAssetRefusal(MapRenderAssetStatus.NotRepresentable,
                        "Native cluster vertex exceeds the anchor-relative precision bound: " + instance.Id);
            }
        }
    }
    static string ClusterInventoryDigest(IReadOnlyList<NativePropInstance> instances, string closure, float weld) => MapPlacementDigest.Hash(w =>
    {
        w.WriteStartArray(); w.WriteStringValue("kemap/native-cluster/1"); w.WriteStringValue(closure); w.WriteNumberValue(weld);
        foreach (var instance in instances.OrderBy(i => i.Id, StringComparer.Ordinal))
        {
            w.WriteStringValue(instance.Id); w.WriteStringValue(instance.AssetId);
            w.WriteNumberValue(instance.Anchor.X); w.WriteNumberValue(instance.Anchor.Y); w.WriteNumberValue(instance.Anchor.Z);
            var m = instance.LocalTransform;
            foreach (float value in new[] { m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24,
                m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44 }) w.WriteNumberValue(value);
            if (weld > 0 && instance.WeldPlacement is { } placement)
            {
                w.WriteStartArray();
                foreach (float value in new[] { placement.X, placement.Y, placement.Z, placement.Scale, placement.Yaw }) w.WriteNumberValue(value);
                w.WriteEndArray();
            }
        }
        w.WriteEndArray();
    });

    internal static MapEffectiveTransform Pose(MapScopedScene scene, MapResolvedPlacement placement) =>
        scene.Transforms.TryGetValue(MapEffectiveContext.Key(MapPrefabChildKind.Placement, placement.PlacementId), out var pose)
            ? pose : MapEffectiveTransform.From(placement.Transform);
    // Discovery metadata deliberately has unbounded height. Render registrations instead retain finite 3D
    // descriptor coverage, including every tier and declared light reach. The submission error allowance also
    // covers the final local binary32 matrix and vertex arithmetic at the culling boundary.
    internal static MapBox3 Bounds(MapResolvedAsset asset, MapEffectiveTransform pose, bool includeLights = true)
    {
        const double submissionAllowance = .0001;
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity, minZ = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity, maxZ = double.NegativeInfinity;
        var (sin, cos) = Math.SinCos(pose.YawRadians);
        foreach (var bounds in new MapLocalBounds?[] { asset.RenderBounds, asset.LodBounds, includeLights ? asset.LightBounds : null })
        {
            if (bounds is not { } box) continue;
            for (int corner = 0; corner < 8; corner++)
            {
                double x = ((corner & 1) == 0 ? box.Min.X : box.Max.X) * (double)asset.SourceUnitsToMetres * pose.Scale;
                double y = ((corner & 2) == 0 ? box.Min.Y : box.Max.Y) * (double)asset.SourceUnitsToMetres * pose.Scale;
                double z = ((corner & 4) == 0 ? box.Min.Z : box.Max.Z) * (double)asset.SourceUnitsToMetres * pose.Scale;
                double wx = pose.X + cos * x + sin * z, wy = pose.Y + y, wz = pose.Z - sin * x + cos * z;
                minX = Math.Min(minX, wx); minY = Math.Min(minY, wy); minZ = Math.Min(minZ, wz);
                maxX = Math.Max(maxX, wx); maxY = Math.Max(maxY, wy); maxZ = Math.Max(maxZ, wz);
            }
        }
        return new(Math.BitDecrement(minX - submissionAllowance), Math.BitDecrement(minY - submissionAllowance), Math.BitDecrement(minZ - submissionAllowance),
            Math.BitIncrement(maxX + submissionAllowance), Math.BitIncrement(maxY + submissionAllowance), Math.BitIncrement(maxZ + submissionAllowance));
    }

    static MapBox3 MeshBounds(GltfMesh mesh, MapSubmissionAnchor anchor)
    {
        var min = new Vector3(float.PositiveInfinity); var max = new Vector3(float.NegativeInfinity);
        foreach (var vertex in mesh.Vertices) { min = Vector3.Min(min, vertex.Position); max = Vector3.Max(max, vertex.Position); }
        return new(anchor.X + (double)min.X - .0001, anchor.Y + (double)min.Y - .0001, anchor.Z + (double)min.Z - .0001,
            anchor.X + (double)max.X + .0001, anchor.Y + (double)max.Y + .0001, anchor.Z + (double)max.Z + .0001);
    }
    static MapSubmissionAnchor Anchor(MapEffectiveTransform pose) => new(checked((long)Math.Floor(pose.X)), checked((long)Math.Floor(pose.Y)), checked((long)Math.Floor(pose.Z)));
    static long Bytes(GltfMesh mesh, GltfMaterialMaps maps) => checked(256L + (long)mesh.Vertices.Length * ModelVertex.SizeInBytes +
        4L * mesh.Indices32.Length + (maps.Albedo?.Rgba.LongLength ?? 0) + (maps.Normal?.Rgba.LongLength ?? 0) + (maps.Roughness?.Rgba.LongLength ?? 0));
    static MapBox3 Union(MapBox3 a, MapBox3 b) => new(Math.Min(a.MinX, b.MinX), Math.Min(a.MinY, b.MinY), Math.Min(a.MinZ, b.MinZ),
        Math.Max(a.MaxX, b.MaxX), Math.Max(a.MaxY, b.MaxY), Math.Max(a.MaxZ, b.MaxZ));
}
