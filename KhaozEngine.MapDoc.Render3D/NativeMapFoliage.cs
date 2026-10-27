using DemandDiagnostics = KhaozEngine.Diagnostics.CapacityDemandDiagnostics;
using KhaozEngine.Diagnostics;
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
        MapPreparedInputLease input, NativeFoliagePolicy policy, MapPlacementRouting routing, Func<long, IDisposable>? retainSamples)
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
        MapRenderAssets assets, NativeFoliagePolicy policy, MapPlacementRouting routing, Func<long, IDisposable>? retainSamples)
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
        var samples = new List<MapFoliageColumnOwner.Reader>();
        using var readers = new MapFoliageRenderCache.Readers();
        var cache = MapFoliageRenderCache.For(context);
        var ceilings = scene.Source.Data.Limits;
        var declared = ceilings.Preparation?.Foliage;
        var requested = declared ?? new MapFoliageLimits();
        long sampleLimit = requested.MaxQuerySamples;
        long count = 0;
        foreach (var layer in layers)
        {
            // Preparation consumes exactly the model-reaching generation owners admitted by window capture.
            foreach (var key in context.ChunksFor(layer.Id))
            {
                // Known off-raster owners have no geometry and must not populate the generation cache.
                if (MapFoliageDistribution.KnownOffRaster(layer, MapFoliageDistribution.Rectangle(layer, key))) continue;
                if (DemandDiagnostics.Check(admission, "NativeMapFoliage.MaxSelectedResources.1", "MaxSelectedResources", samples.Count, 1, ceilings.MaxSelectedResources, samples.Count >= ceilings.MaxSelectedResources, CapacityDemandLifetime.Inventory)) throw new MapSceneCapacityException(nameof(MapSceneLimits.MaxSelectedResources), (long)samples.Count + 1, ceilings.MaxSelectedResources);
                long available = admission.RemainingPreparedBytes;
                // Bound retention and support scratch by the active composite and current window, not source defaults.
                var limits = requested with
                {
                    MaxPrepareScopes = Math.Min(declared?.MaxPrepareScopes ?? int.MaxValue, admission.RemainingPatchScopes),
                    MaxFaces = checked((int)Math.Min(declared?.MaxFaces ?? int.MaxValue, Math.Min(admission.RemainingPreparedFaces, available / 2560))),
                    MaxQuerySamples = sampleLimit - count,
                    MaxQueryRetainedBytes = Math.Min(declared?.MaxQueryRetainedBytes ?? long.MaxValue, available),
                    ChargePreparation = bytes => MapFoliageGenerationAdmission.Charge(admission, bytes),
                };
                var reader = cache.Acquire(layer.Id, key, limits, admission, retainSamples, requested.MaxQueryRetainedBytes);
                var batch = readers.Add(reader);
                count += batch.Count;
                if (DemandDiagnostics.Check(admission, "NativeMapFoliage.MaxQuerySamples.1", "MaxQuerySamples", 0, count, sampleLimit, count > sampleLimit, CapacityDemandLifetime.Inventory)) throw new MapSceneCapacityException(nameof(MapFoliageLimits.MaxQuerySamples), count, sampleLimit);
                samples.Add(reader);
            }
        }
        cache.Publish(admission, retainSamples, requested.MaxQueryRetainedBytes);
        var result = AssembleCore(scene, assets, policy, routing, samples.Select(s => new AssemblyInput(null, s)).ToArray());
        readers.Transfer();
        return result;
    }

    // Assembly is separate from distribution. It is also the single adapter for already retained generated rows.
    internal static MapNativeFoliageBatch Assemble(MapScopedScene scene, MapRenderAssets assets,
        NativeFoliagePolicy policy, MapPlacementRouting routing, IReadOnlyList<MapFoliageSampleBatch> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        return AssembleCore(scene, assets, policy, routing, samples.Select(s => new AssemblyInput(s, null)).ToArray());
    }

    sealed record AssemblyInput(MapFoliageSampleBatch? Full, MapFoliageColumnOwner.Reader? Reader)
    {
        internal string LayerId => Full?.LayerId ?? Reader!.Batch.LayerId;
        internal MapFoliageChunkKey Chunk => Full?.Chunk ?? Reader!.Batch.Chunk;
        internal MapFoliageStatus Status => Full?.Status ?? Reader!.Batch.Status;
        internal string DependencyDigest => Full?.DependencyDigest ?? Reader!.Batch.DependencyDigest;
        internal IEnumerable<string> Models => Full is { } full ? full.Samples.Select(s => s.AssetId) :
            Enumerable.Range(0, Reader!.Columns.ModelCount).Select(Reader.Columns.ModelId);
        internal MapFoliageInstanceRenderBatch Handoff(IReadOnlyList<MapFoliageModelExtent> extents)
        {
            if (Full is null) return MapFoliageRenderHandoff.Create(Reader!.Batch, extents);
            var result = MapFoliageRenderHandoff.Create(Full, extents);
            return new(result.Status, result.Bounds, result.DependencyDigest);
        }
    }

    static MapNativeFoliageBatch AssembleCore(MapScopedScene scene, MapRenderAssets assets,
        NativeFoliagePolicy policy, MapPlacementRouting routing, IReadOnlyList<AssemblyInput> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        Validate(scene, assets, policy, routing, samples.SelectMany(b => b.Models));
        var admission = MapSceneAdmission.Current ?? new MapSceneAdmission(scene.Source.Data.Limits);
        using var activation = admission.Enter();
        using var preparation = admission.BeginPreparation();
        List<MapFoliageColumnOwner.Reader>? columnSources = null;
        bool transferred = false;
        try
        {
            admission.ReserveBytes(checked(512L + 128L * samples.Count + 128L * scene.Placements.Count));
            var groups = new List<MapNativeFoliageGroup>();
            columnSources = new List<MapFoliageColumnOwner.Reader>();
            var generations = new List<MapFoliageGenerationKey>();
            var explicitIds = new List<string>();
            var owners = new HashSet<string>(scene.Placements.Select(p => p.PlacementId), StringComparer.Ordinal);
            var keys = new HashSet<MapFoliageCacheKey>();
            foreach (var source in samples)
            {
                using var handoffPreparation = admission.BeginPreparation();
                if (source.Status != MapFoliageStatus.Complete || !keys.Add(new(source.LayerId, source.Chunk)))
                    throw new MapDocumentException("Native foliage needs unique complete generated batches.");
                admission.ReserveBytes(512);
                generations.Add(new(source.LayerId, source.Chunk, source.DependencyDigest));
                string owner = FormattableString.Invariant($"{source.LayerId}/{source.Chunk.X}/{source.Chunk.Z}");
                if (!owners.Add(owner)) throw new MapDocumentException("Generated foliage owner conflicts with a placement or generated batch.");
                var handoff = source.Handoff(policy.Extents.Values.ToArray());
                if (handoff.Status != MapFoliageStatus.Complete) throw new MapDocumentException("Native foliage has no complete model extent handoff.");
                if (handoff.Bounds is not { } bounds)
                {
                    if (source.Reader is { } empty) columnSources.Add(empty);
                    handoffPreparation.Complete(source.Reader is null ? 8 : 32); continue;
                }
                var compact = source.Reader?.Batch ?? MapFoliageInstanceImport.Create(source.Full!, admission.ReserveBytes, admission.ReleaseUnusedBytes);
                long view = checked((source.Reader is null ? MapFoliageColumnOwner.ControlBytes : 0) +
                    24L + 64L + MapFoliageInventory.Array((compact.Count + 127) / 128, 16) + 256L + 192L * compact.Columns.ModelCount);
                admission.ReserveBytes(view);
                var reader = source.Reader;
                if (reader is null) { var columns = new MapFoliageColumnOwner(compact); reader = columns.Retain(); columns.Release(); }
                columnSources.Add(reader);
                var cover = new GroundCoverBatch(reader);
                var group = Group(owner, cover, bounds, policy, instances: reader);
                groups.Add(group);
                handoffPreparation.Complete(checked(8L + (source.Reader is null ? compact.Work.RetainedBytes : 0) + view));
            }
            foreach (var placement in scene.Placements)
            {
                if (routing.Entries[placement.PlacementId] != MapPlacementRoute.Animated) continue;
                using var placementPreparation = admission.BeginPreparation();
                admission.ReserveBytes(1024);
                explicitIds.Add(placement.PlacementId);
                var pose = NativeMapPlacements.Pose(scene, placement);
                var anchor = new MapSubmissionAnchor(checked((long)Math.Floor(pose.X)), checked((long)Math.Floor(pose.Y)), checked((long)Math.Floor(pose.Z)));
                Matrix4x4 transform = MapRenderTransform.ToLocal(pose, anchor);
                var cover = new GroundCoverBatch(new[] { new GroundCoverInstance(placement.AssetId, transform.Translation, transform, 0) });
                var bounds = ExplicitBounds(policy.Extents[placement.AssetId], transform, anchor);
                groups.Add(Group(placement.PlacementId, cover, bounds, policy, explicitlyPlaced: true, anchorOverride: anchor));
                placementPreparation.Complete(checked(264L + 240L * cover.Count + 128L * groups[^1].Models.Count));
            }
            var parts = new List<MapNativeFoliagePart>();
            foreach (string id in groups.SelectMany(g => g.Cover.Select(i => i.ModelId)).Distinct(StringComparer.Ordinal))
            {
                var asset = assets.ById[id]; var lod = asset.Lods[0];
                var bounds = groups.Where(g => g.Cover.Any(i => i.ModelId == id)).Select(g => g.Bounds).Aggregate((a, b) => a.Union(b));
                for (int i = 0; i < lod.BorrowParts.Count; i++)
                {
                    using var copyPreparation = admission.BeginPreparation();
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
                    copyPreparation.Complete(checked(256L + bytes));
                }
            }
            admission.ReserveBytes(checked(512L + 16L * (groups.Count + parts.Count + columnSources.Count + explicitIds.Count + generations.Count)));
            var result = new MapNativeFoliageBatch(scene.Witness, routing, policy, groups, parts, columnSources, explicitIds, generations, 0);
            result.Own(preparation.Complete(result.RetainedBytes));
            transferred = true;
            return result;
        }
        catch (MapSceneCapacityException error) { throw new MapRenderAssetRefusal(MapRenderAssetStatus.CapacityExceeded, error.Message, error); }
        finally { if (!transferred && columnSources is not null) foreach (var source in columnSources) source.Dispose(); }
    }

    static MapNativeFoliageGroup Group(string owner, GroundCoverBatch cover, MapBox3 bounds, NativeFoliagePolicy policy,
        bool explicitlyPlaced = false, MapSubmissionAnchor? anchorOverride = null, MapFoliageColumnOwner.Reader? instances = null)
    {
        IReadOnlyList<string> models;
        int[]? indices = null;
        if (instances is not null) models = new MapFoliageColumnOwner.ModelIds(instances);
        else
        {
            var selected = new List<string>(); indices = new int[cover.Count];
            var lookup = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < cover.Count; i++)
            {
                if (!lookup.TryGetValue(cover[i].ModelId, out int index))
                { index = selected.Count; lookup.Add(cover[i].ModelId, index); selected.Add(cover[i].ModelId); }
                indices[i] = index;
            }
            models = selected.ToArray();
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
                w.WriteNumberValue(instances is not null ? instances.Columns.ModelIndex(i) : indices![i]);
            }
            w.WriteEndArray();
        });
        return new(owner, anchor, bounds, cover, models, indices, explicitlyPlaced, models.Any(id => policy.Models[id].CastShadows), digest, instances);
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
}
