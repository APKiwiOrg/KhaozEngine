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
        MapPlacementPolicy policy, MapPlacementRouting routing)
    {
        ArgumentNullException.ThrowIfNull(input);
        var retained = input.Retain();
        try
        {
            var batch = Prepare(scene, retained.Assets, policy, routing);
            batch.AttachInput(retained); return batch;
        }
        catch { retained.Dispose(); throw; }
    }

    public static MapPlacementRenderBatch Prepare(MapScopedScene scene, MapRenderAssets assets,
        MapPlacementPolicy policy, MapPlacementRouting routing)
    {
        ArgumentNullException.ThrowIfNull(scene); ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(policy); ArgumentNullException.ThrowIfNull(routing);
        routing.Validate(scene, policy);
        if (scene.Witness.ClosureHash != assets.Closure.Hash) throw new MapDocumentException("Placement assets belong to another closure.");
        foreach (var placement in scene.Placements)
            if (!assets.ById.ContainsKey(placement.AssetId)) throw new MapRenderAssetRefusal(MapRenderAssetStatus.Missing, "Placement has no selected asset: " + placement.AssetId);
        var admission = new MapSceneAdmission(scene.Source.Data.Limits);
        try
        {
            var instances = new List<NativePropInstance>();
            var parts = new List<MapPlacementPart>();
            var boundsById = new Dictionary<string, MapBox3>(StringComparer.Ordinal);
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
            var roofs = scene.BuildingIntent.Roofs.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var placement in scene.Placements)
            {
                if (routing.Entries[placement.PlacementId] == MapPlacementRoute.Animated) continue;
                admission.ReserveBytes(256);
                var pose = Pose(scene, placement); var anchor = Anchor(pose);
                var instance = new NativePropInstance(placement.PlacementId, placement.NumericId, placement.AssetId,
                    MapRenderTransform.ToLocal(pose, anchor), new(anchor.X, anchor.Y, anchor.Z));
                instances.Add(instance);
                var bounds = Bounds(assets.ById[placement.AssetId].Descriptor, pose);
                boundsById.Add(placement.PlacementId, bounds);
                var kind = roofs.Contains(placement.PlacementId) ? MapRenderKind.Roof : MapRenderKind.Placement;
                foreach (var lod in assets.ById[placement.AssetId].Lods)
                    for (int i = 0; i < lod.BorrowParts.Count; i++)
                    {
                        var part = lod.BorrowParts[i];
                        admission.ReserveBytes(checked(512L + 4 * Bytes(part.Mesh, part.Maps)));
                        var key = new MapRenderKey(kind, placement.PlacementId, "part/" + i, lod.Index);
                        var payload = new MapRigidResource(part.Mesh, part.Maps);
                        string digest = MapPlacementDigest.Hash(w =>
                        {
                            w.WriteStartArray(); w.WriteStringValue(assets.Closure.GetResource(lod.ResourceId).Reference.Sha256);
                            w.WriteNumberValue(assets.ById[placement.AssetId].Descriptor.SourceUnitsToMetres); w.WriteNumberValue(i); w.WriteEndArray();
                        });
                        parts.Add(new(key, handles[placement.AssetId][lod.Index].Parts[i], anchor, bounds, payload, digest));
                    }
            }
            var clusters = new List<NativePropClusterCpuBuild>();
            foreach (var layer in policy.Layers.Where(l => l.HlodDistance > 0))
            {
                var selected = instances.Where(i => routing.Entries[i.Id] == MapPlacementRoute.Cluster && layer.AssetIds.Contains(i.AssetId)).ToArray();
                if (selected.Length == 0) continue;
                var first = selected[0].Anchor;
                var local = selected.Select(i => (X: (float)((double)i.Anchor.X - first.X + i.LocalTransform.M41),
                    Z: (float)((double)i.Anchor.Z - first.Z + i.LocalTransform.M43))).ToArray();
                var area = new RectArea(local.Min(p => p.X), local.Min(p => p.Z), local.Max(p => p.X), local.Max(p => p.Z));
                var sources = new Dictionary<string, GltfMesh>(StringComparer.Ordinal);
                foreach (string id in selected.Select(i => i.AssetId).Distinct(StringComparer.Ordinal))
                {
                    var source = assets.ById[id].Lods[^1];
                    admission.ReserveBytes(checked(8 * source.BorrowParts.Sum(p => Bytes(p.Mesh, p.Maps))));
                    sources.Add(id, Flatten(source.BorrowParts));
                }
                long mergeBytes = selected.Sum(i => Bytes(sources[i.AssetId], default));
                admission.ReserveBytes(checked(8 * mergeBytes));
                var shared = PropLayer.PlacementLayer(Array.Empty<PropPlacement>(), new Dictionary<string, MeshHandle>(), layer.DrawRadius,
                    castsShadows: layer.CastsShadows).WithHlod(sources, layer.HlodDistance, layer.HlodWeldCell, layer.HlodCrossfadeWidth).WithIdentity(layer.Id);
                // A cluster is the frozen selected inventory, never an inferred spatial or art category.
                // Including that inventory prevents two different scoped subsets from aliasing one merged owner.
                string clusterId = layer.Id + "/" + ClusterInventoryDigest(selected, assets.Closure.Hash, layer.HlodWeldCell);
                var request = new NativePropClusterRequest(new(clusterId, 0, 0, 0), 0, area, shared, selected, first);
                var build = PrepareCluster(scene, routing, request);
                clusters.Add(build);
                var anchor = new MapSubmissionAnchor(first.X, first.Y, first.Z);
                var mesh = build.BorrowMesh;
                if (mesh.TriangleCount == 0) continue;
                var bounds = selected.Select(i => boundsById[i.Id]).Aggregate(Union);
                var key = new MapRenderKey(MapRenderKind.Placement, "cluster/" + clusterId, "hlod", 0);
                parts.Add(new(key, new(nextHandle++), anchor, bounds, new MapRigidResource(mesh, default),
                    MapPlacementDigest.Hash(w =>
                    { w.WriteStartArray(); w.WriteStringValue(routing.SceneWitnessDigest); w.WriteStringValue(routing.PolicyDigest); w.WriteEndArray(); })));
            }
            return new(scene.Placements, MapEffectiveAnnotations.Markers(scene), instances, parts, handles, policy, routing, clusters, admission.RetainedBytes);
        }
        catch (MapSceneCapacityException) { throw new MapRenderAssetRefusal(MapRenderAssetStatus.CapacityExceeded, "Placement preparation exceeds its scene allowance."); }
    }

    /// <summary>The sole upper entry to native cluster merging. Validate the entire request before the lower
    /// builder enters. Effective roofs must remain independently addressable for every presentation channel.</summary>
    public static NativePropClusterCpuBuild PrepareCluster(MapScopedScene scene, MapPlacementRouting routing, NativePropClusterRequest request) =>
        PrepareCluster(scene, routing, request, PropClusterRenderer.BuildNativeCpu);

    internal static NativePropClusterCpuBuild PrepareCluster(MapScopedScene scene, MapPlacementRouting routing,
        NativePropClusterRequest request, Func<NativePropClusterRequest, NativePropClusterCpuBuild> build)
    {
        ArgumentNullException.ThrowIfNull(scene); ArgumentNullException.ThrowIfNull(routing); ArgumentNullException.ThrowIfNull(request);
        routing.Validate(scene);
        var roofs = scene.BuildingIntent.Roofs.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        var placements = scene.Placements.ToDictionary(p => p.PlacementId, StringComparer.Ordinal);
        foreach (var instance in request.Instances)
        {
            if (roofs.Contains(instance.Id) || !routing.Entries.TryGetValue(instance.Id, out var route) || route != MapPlacementRoute.Cluster ||
                !placements.TryGetValue(instance.Id, out var placement) || instance.AssetId != placement.AssetId || instance.NumericId != placement.NumericId ||
                instance.LocalTransform != MapRenderTransform.ToLocal(Pose(scene, placement), new(instance.Anchor.X, instance.Anchor.Y, instance.Anchor.Z)))
                throw new MapDocumentException("Native cluster request disagrees with the effective scene routing.");
        }
        return build(request);
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

    static MapSubmissionAnchor Anchor(MapEffectiveTransform pose) => new(checked((long)Math.Floor(pose.X)), checked((long)Math.Floor(pose.Y)), checked((long)Math.Floor(pose.Z)));
    static long Bytes(GltfMesh mesh, GltfMaterialMaps maps) => checked(256L + (long)mesh.Vertices.Length * ModelVertex.SizeInBytes +
        4L * mesh.Indices32.Length + (maps.Albedo?.Rgba.LongLength ?? 0) + (maps.Normal?.Rgba.LongLength ?? 0) + (maps.Roughness?.Rgba.LongLength ?? 0));
    static MapBox3 Union(MapBox3 a, MapBox3 b) => new(Math.Min(a.MinX, b.MinX), Math.Min(a.MinY, b.MinY), Math.Min(a.MinZ, b.MinZ),
        Math.Max(a.MaxX, b.MaxX), Math.Max(a.MaxY, b.MaxY), Math.Max(a.MaxZ, b.MaxZ));
    static GltfMesh Flatten(IReadOnlyList<GltfMeshPart> parts)
    {
        var vertices = new List<ModelVertex>(); var indices = new List<uint>();
        foreach (var part in parts)
        {
            uint start = (uint)vertices.Count;
            var albedo = part.Maps.Albedo is { } image ? GltfLoader.AverageAlbedo(image) : Vector3.One;
            foreach (var v in part.Mesh.Vertices)
            {
                var output = v; output.Color *= new Vector4(albedo, 1); vertices.Add(output);
            }
            indices.AddRange(part.Mesh.Indices32.Select(i => start + i));
        }
        return new(vertices.ToArray(), indices.ToArray());
    }
}
