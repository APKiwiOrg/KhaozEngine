using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapDoc.Render3D;

/// <summary>Adapts complete window generation directly, retaining original sample rows before GPU conversion.</summary>
public static class NativeMapFoliage
{
    public static MapNativeFoliageBatch Prepare(MapScopedScene scene, MapWindowFoliageContext context,
        MapPreparedInputLease input, NativeFoliagePolicy policy, MapPlacementRouting routing) =>
        Prepare(scene, context, input, policy, routing, null);
    internal static MapNativeFoliageBatch Prepare(MapScopedScene scene, MapWindowFoliageContext context,
        MapPreparedInputLease input, NativeFoliagePolicy policy, MapPlacementRouting routing, Action<long>? retainSamples)
    {
        ArgumentNullException.ThrowIfNull(input);
        var retained = input.Retain();
        try
        {
            var result = Prepare(scene, context, retained.Assets, policy, routing, retainSamples);
            result.AttachInput(retained); return result;
        }
        catch { retained.Dispose(); throw; }
    }

    public static MapNativeFoliageBatch Prepare(MapScopedScene scene, MapWindowFoliageContext context,
        MapRenderAssets assets, NativeFoliagePolicy policy, MapPlacementRouting routing) =>
        Prepare(scene, context, assets, policy, routing, null);
    static MapNativeFoliageBatch Prepare(MapScopedScene scene, MapWindowFoliageContext context,
        MapRenderAssets assets, NativeFoliagePolicy policy, MapPlacementRouting routing, Action<long>? retainSamples)
    {
        ArgumentNullException.ThrowIfNull(scene); ArgumentNullException.ThrowIfNull(context);
        var witness = context.Witness;
        if (!witness.CompleteForScope || context.ClosureHash != scene.Witness.ClosureHash ||
            witness.SourceGeneration != scene.Witness.SourceGeneration || witness.ScopeDigest != scene.Witness.ScopeDigest ||
            witness.RenderExtentsDigest != scene.Witness.RenderExtentsDigest)
            throw new MapDocumentException("Foliage context belongs to another source, closure, extent proof or scope.");
        IReadOnlyList<MapFoliageLayer> layers = (scene.Scope.Channels & MapSceneChannels.Foliage) != 0
            ? ((IMapFoliageInputs)context).Foliage?.Layers ?? [] : [];
        // Only admitted density dependencies can require decoded generation models. Off-raster metadata stays cheap.
        Validate(scene, assets, policy, routing, layers.Where(l => l.ChunkDigests.Count != 0).SelectMany(l => l.Models.Select(m => m.AssetId)));
        var admission = MapSceneAdmission.Current ?? new MapSceneAdmission(scene.Source.Data.Limits);
        using var activation = admission.Enter();
        var samples = new List<MapFoliageSampleBatch>();
        var cache = MapFoliageRenderCache.For(context);
        var bounds = scene.Scope.DiscoveryBounds;
        var ceilings = scene.Source.Data.Limits;
        long count = 0;
        foreach (var layer in layers)
        {
            if (bounds.MinX >= bounds.MaxX || bounds.MinZ >= bounds.MaxZ) continue;
            // Use the released rounded ownership and chunk enumeration used by window capture.
            int x0 = MapFoliageRasterReads.Owner(layer, MapFoliageSupport.Outward(bounds.MinX, true), true);
            int z0 = MapFoliageRasterReads.Owner(layer, MapFoliageSupport.Outward(bounds.MinZ, true), false);
            int x1 = MapFoliageRasterReads.Owner(layer, MathF.BitDecrement(MapFoliageSupport.Outward(bounds.MaxX, false)), true);
            int z1 = MapFoliageRasterReads.Owner(layer, MathF.BitDecrement(MapFoliageSupport.Outward(bounds.MaxZ, false)), false);
            foreach (int z in MapFoliageRasterReads.Chunks(layer, z0, z1, false))
                foreach (int x in MapFoliageRasterReads.Chunks(layer, x0, x1, true))
                {
                    if (samples.Count >= ceilings.MaxSelectedResources) throw Capacity();
                    long available = admission.RemainingPreparedBytes;
                    // Bound retention and support scratch by the active composite and current window, not source defaults.
                    var limits = new MapFoliageLimits(MaxPrepareScopes: admission.RemainingPatchScopes,
                        MaxFaces: checked((int)Math.Min(admission.RemainingPreparedFaces, available / 2560)), MaxQuerySamples: 262_144 - count,
                        MaxQueryRetainedBytes: available / 8)
                    { ChargePreparation = bytes => MapFoliageGenerationAdmission.Charge(admission, bytes) };
                    var batch = cache.Get(layer.Id, new(x, z), limits, admission, retainSamples);
                    count += batch.Samples.Count;
                    if (count > 262_144) throw Capacity();
                    samples.Add(batch);
                }
        }
        return Assemble(scene, assets, policy, routing, samples);
    }

    // Assembly is separate from distribution. It is also the single adapter for already retained generated rows.
    internal static MapNativeFoliageBatch Assemble(MapScopedScene scene, MapRenderAssets assets,
        NativeFoliagePolicy policy, MapPlacementRouting routing, IReadOnlyList<MapFoliageSampleBatch> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        Validate(scene, assets, policy, routing, samples.SelectMany(b => b.Samples.Select(s => s.AssetId)));
        var admission = MapSceneAdmission.Current ?? new MapSceneAdmission(scene.Source.Data.Limits);
        try
        {
            var groups = new List<MapNativeFoliageGroup>();
            var identities = new List<MapFoliageSampleKey>();
            var generations = new List<MapFoliageGenerationKey>();
            var explicitIds = new List<string>();
            var owners = new HashSet<string>(scene.Placements.Select(p => p.PlacementId), StringComparer.Ordinal);
            var keys = new HashSet<MapFoliageCacheKey>();
            foreach (var source in samples)
            {
                if (source.Status != MapFoliageStatus.Complete || !keys.Add(new(source.LayerId, source.Chunk)))
                    throw new MapDocumentException("Native foliage needs unique complete generated batches.");
                admission.ReserveBytes(checked(512L + 4 * source.Work.RetainedBytes + 512L * source.Samples.Count));
                generations.Add(new(source.LayerId, source.Chunk, source.DependencyDigest));
                string owner = FormattableString.Invariant($"{source.LayerId}/{source.Chunk.X}/{source.Chunk.Z}");
                if (!owners.Add(owner)) throw new MapDocumentException("Generated foliage owner conflicts with a placement or generated batch.");
                var handoff = MapFoliageRenderHandoff.Create(source, policy.Extents.Values.ToArray());
                if (handoff.Status != MapFoliageStatus.Complete) throw new MapDocumentException("Native foliage has no complete model extent handoff.");
                identities.AddRange(source.Samples.Select(s => s.Key));
                if (handoff.Bounds is not { } bounds) continue;
                var cover = new GroundCoverBatch(source.Samples.Select(s => new GroundCoverInstance(s.AssetId, s.Position, s.Transform, s.ThinningRank)).ToArray());
                groups.Add(Group(owner, cover, bounds, policy));
            }
            foreach (var placement in scene.Placements)
            {
                if (routing.Entries[placement.PlacementId] != MapPlacementRoute.Animated) continue;
                admission.ReserveBytes(1024);
                explicitIds.Add(placement.PlacementId);
                var pose = NativeMapPlacements.Pose(scene, placement);
                var anchor = new MapSubmissionAnchor(checked((long)Math.Floor(pose.X)), checked((long)Math.Floor(pose.Y)), checked((long)Math.Floor(pose.Z)));
                Matrix4x4 transform = MapRenderTransform.ToLocal(pose, anchor);
                var cover = new GroundCoverBatch(new[] { new GroundCoverInstance(placement.AssetId, transform.Translation, transform, 0) });
                var bounds = ExplicitBounds(policy.Extents[placement.AssetId], transform, anchor);
                groups.Add(Group(placement.PlacementId, cover, bounds, policy, explicitlyPlaced: true, anchorOverride: anchor));
            }
            var parts = new List<MapNativeFoliagePart>();
            foreach (string id in groups.SelectMany(g => g.Cover.Select(i => i.ModelId)).Distinct(StringComparer.Ordinal))
            {
                var asset = assets.ById[id]; var lod = asset.Lods[0];
                var bounds = groups.Where(g => g.Cover.Any(i => i.ModelId == id)).Select(g => g.Bounds).Aggregate((a, b) => a.Union(b));
                for (int i = 0; i < lod.BorrowParts.Count; i++)
                {
                    var part = lod.BorrowParts[i];
                    long bytes = checked(256L + (long)part.Mesh.Vertices.Length * ModelVertex.SizeInBytes + 4L * part.Mesh.Indices32.Length +
                        (part.Maps.Albedo?.Rgba.LongLength ?? 0) + (part.Maps.Normal?.Rgba.LongLength ?? 0) + (part.Maps.Roughness?.Rgba.LongLength ?? 0));
                    admission.ReserveBytes(checked(512 + 4 * bytes));
                    string digest = MapPlacementDigest.Hash(w =>
                    {
                        w.WriteStartArray(); w.WriteStringValue("kemap/native-foliage-model/1");
                        w.WriteStringValue(assets.Closure.GetResource(lod.ResourceId).Reference.Sha256); w.WriteNumberValue(i);
                        w.WriteNumberValue(asset.Descriptor.SourceUnitsToMetres); w.WriteEndArray();
                    });
                    parts.Add(new(new(MapRenderKind.Foliage, id, "model-part/" + i, 0), bounds, new(part.Mesh, part.Maps), digest));
                }
            }
            return new(scene.Witness, routing, policy, groups, parts, identities, explicitIds, generations, admission.RetainedBytes);
        }
        catch (MapSceneCapacityException) { throw Capacity(); }
    }

    static MapNativeFoliageGroup Group(string owner, GroundCoverBatch cover, MapBox3 bounds, NativeFoliagePolicy policy, bool explicitlyPlaced = false, MapSubmissionAnchor? anchorOverride = null)
    {
        var models = new List<string>(); var indices = new int[cover.Count];
        var lookup = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < cover.Count; i++)
        {
            if (!lookup.TryGetValue(cover[i].ModelId, out int index))
            { index = models.Count; lookup.Add(cover[i].ModelId, index); models.Add(cover[i].ModelId); }
            indices[i] = index;
        }
        if (explicitlyPlaced != anchorOverride.HasValue) throw new MapDocumentException("Explicit foliage requires its precise submission anchor.");
        var anchor = anchorOverride ?? new MapSubmissionAnchor(checked((long)Math.Floor(cover.Min(i => (double)i.Transform.M41))),
            checked((long)Math.Floor(cover.Min(i => (double)i.Transform.M42))), checked((long)Math.Floor(cover.Min(i => (double)i.Transform.M43))));
        string digest = MapPlacementDigest.Hash(w =>
        {
            w.WriteStartArray(); w.WriteStringValue("kemap/native-foliage-rows/3");
            w.WriteStringValue(owner); w.WriteBooleanValue(explicitlyPlaced);
            w.WriteNumberValue((int)(explicitlyPlaced ? MapFoliageTransformSpace.AnchorLocal : MapFoliageTransformSpace.World)); w.WriteNumberValue(anchor.X); w.WriteNumberValue(anchor.Y); w.WriteNumberValue(anchor.Z);
            foreach (string id in models) w.WriteStringValue(id);
            for (int i = 0; i < cover.Count; i++)
            {
                var m = cover[i].Transform;
                foreach (float value in new[] { m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24,
                    m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44, cover[i].ThinningRank })
                    w.WriteNumberValue(BitConverter.SingleToInt32Bits(value));
                w.WriteNumberValue(indices[i]);
            }
            w.WriteEndArray();
        });
        return new(owner, anchor, bounds, cover, models.ToArray(), indices, explicitlyPlaced, models.Any(id => policy.Models[id].CastShadows), digest);
    }
    // Use the submitted local matrix for cosmetic bounds, adding the integer anchor only in binary64.
    static MapBox3 ExplicitBounds(MapFoliageModelExtent extent, Matrix4x4 local, MapSubmissionAnchor anchor)
    {
        MapBox3? bounds = null;
        for (int corner = 0; corner < 8; corner++)
        {
            var box = extent.BoundsInSourceUnits;
            var source = new Vector3((corner & 1) == 0 ? box.Min.X : box.Max.X,
                (corner & 2) == 0 ? box.Min.Y : box.Max.Y, (corner & 4) == 0 ? box.Min.Z : box.Max.Z);
            var point = Vector3.Transform(source * extent.SourceUnitsToMetres, local);
            if (!float.IsFinite(point.X) || !float.IsFinite(point.Y) || !float.IsFinite(point.Z))
                throw new MapRenderAssetRefusal(MapRenderAssetStatus.NotRepresentable, "Explicit foliage bounds are not representable.");
            double x = anchor.X + (double)point.X, y = anchor.Y + (double)point.Y, z = anchor.Z + (double)point.Z;
            var cornerBounds = new MapBox3(x, y, z, x, y, z);
            bounds = bounds?.Union(cornerBounds) ?? cornerBounds;
        }
        const double submissionAllowance = .0001;
        double padding = extent.MotionPaddingMetres + submissionAllowance;
        var b = bounds!.Value;
        return new(Math.BitDecrement(b.MinX - padding), Math.BitDecrement(b.MinY - padding), Math.BitDecrement(b.MinZ - padding),
            Math.BitIncrement(b.MaxX + padding), Math.BitIncrement(b.MaxY + padding), Math.BitIncrement(b.MaxZ + padding));
    }

    static void Validate(MapScopedScene scene, MapRenderAssets assets, NativeFoliagePolicy policy, MapPlacementRouting routing, IEnumerable<string> generatedModels)
    {
        ArgumentNullException.ThrowIfNull(scene); ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(policy); ArgumentNullException.ThrowIfNull(routing);
        routing.Validate(scene);
        if (scene.Witness.ClosureHash != assets.Closure.Hash) throw new MapDocumentException("Foliage assets belong to another closure.");
        var required = generatedModels
            .Concat(scene.Placements.Where(p => routing.Entries[p.PlacementId] == MapPlacementRoute.Animated).Select(p => p.AssetId)).Distinct(StringComparer.Ordinal);
        foreach (string id in required)
        {
            if (!assets.ById.TryGetValue(id, out var asset) || !policy.Extents.TryGetValue(id, out var extent) ||
                extent.SourceUnitsToMetres != asset.Descriptor.SourceUnitsToMetres || !Contains(extent.BoundsInSourceUnits, asset.Descriptor.RenderBounds) ||
                asset.Descriptor.LodBounds is { } lod && !Contains(extent.BoundsInSourceUnits, lod) || extent.MotionPaddingMetres > scene.Scope.MotionPaddingMetres)
                throw new MapDocumentException("Foliage model requires its selected asset, verified bounds, units and scoped motion allowance: " + id);
        }
    }
    static bool Contains(MapLocalBounds outer, MapLocalBounds inner) => outer.Min.X <= inner.Min.X && outer.Min.Y <= inner.Min.Y && outer.Min.Z <= inner.Min.Z &&
        outer.Max.X >= inner.Max.X && outer.Max.Y >= inner.Max.Y && outer.Max.Z >= inner.Max.Z;
    static MapRenderAssetRefusal Capacity() => new(MapRenderAssetStatus.CapacityExceeded, "Native foliage exceeds its aggregate scene preparation allowance.");
}
