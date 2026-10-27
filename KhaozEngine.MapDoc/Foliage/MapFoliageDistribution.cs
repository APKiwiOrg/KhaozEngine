using KhaozEngine.Diagnostics;
using DemandDiagnostics = KhaozEngine.Diagnostics.CapacityDemandDiagnostics;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapDoc.Foliage;

/// <summary>Chunk-owned generation over immutable captured inputs. No expanded instance list precedes retention.
/// Query shares one sample and byte budget across its entire half-open request, including off-raster chunks.</summary>
public static partial class MapFoliageDistribution
{
    public static MapFoliageSampleBatch Generate(MapFoliageGenerationContext context, string layerId, MapFoliageChunkKey chunk,
        MapFoliageLimits? limits = null, Action<MapFoliageDecision>? observe = null)
 => Generate((IMapFoliageInputs)context, layerId, chunk, limits, observe);

    public static MapFoliageSampleBatch Generate(MapWindowFoliageContext context, string layerId, MapFoliageChunkKey chunk,
        MapFoliageLimits? limits = null, Action<MapFoliageDecision>? observe = null)
 => Generate((IMapFoliageInputs)context, layerId, chunk, limits, observe);

    static MapFoliageSampleBatch Generate(IMapFoliageInputs context, string layerId, MapFoliageChunkKey chunk,
        MapFoliageLimits? limits = null, Action<MapFoliageDecision>? observe = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        MapFoliageLimits admitted = Limits(limits);
        var meter = new Meter();
        var budget = new MapFoliageRetentionBudget(Math.Min(admitted.MaxSamples, admitted.MaxQuerySamples), admitted.MaxQueryRetainedBytes);
        try
        {
            MapFoliageLayer layer = Layer(context, layerId);
            var rect = Rectangle(layer, chunk);
            return GenerateCore(context, layer, chunk, rect, admitted, budget, meter, observe);
        }
        catch (MapFoliageRefusal refusal) { return new(layerId, chunk, refusal.Status, "", [], meter.Work(budget.Bytes)); }
    }

    public static MapFoliageQueryResult Query(MapFoliageGenerationContext context, string layerId, RectArea area,
        MapFoliageLimits? limits = null, Action<MapFoliageDecision>? observe = null)
 => Query((IMapFoliageInputs)context, layerId, area, limits, observe);

    public static MapFoliageQueryResult Query(MapWindowFoliageContext context, string layerId, RectArea area,
        MapFoliageLimits? limits = null, Action<MapFoliageDecision>? observe = null)
 => Query((IMapFoliageInputs)context, layerId, area, limits, observe);

    static MapFoliageQueryResult Query(IMapFoliageInputs context, string layerId, RectArea area,
        MapFoliageLimits? limits = null, Action<MapFoliageDecision>? observe = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        MapFoliageLimits admitted = Limits(limits);
        var meter = new Meter();
        var budget = new MapFoliageRetentionBudget(admitted.MaxQuerySamples, admitted.MaxQueryRetainedBytes);
        try
        {
            MapFoliageLayer layer = Layer(context, layerId);
            if (!float.IsFinite(area.MinX) || !float.IsFinite(area.MinZ) || !float.IsFinite(area.MaxX) || !float.IsFinite(area.MaxZ) ||
                area.MinX > area.MaxX || area.MinZ > area.MaxZ)
                throw new ArgumentException("foliage query area must be finite and ordered", nameof(area));
            if (area.MinX == area.MaxX || area.MinZ == area.MaxZ) return new(MapFoliageStatus.Complete, [], meter.Work());
            int x0 = ChunkIndex(layer, area.MinX, true), z0 = ChunkIndex(layer, area.MinZ, false);
            int x1 = ChunkIndex(layer, MathF.BitDecrement(area.MaxX), true), z1 = ChunkIndex(layer, MathF.BitDecrement(area.MaxZ), false);
            int[] xs = QueryChunks(layer, x0, x1, true, admitted.MaxQueryChunks);
            int[] zs = QueryChunks(layer, z0, z1, false, admitted.MaxQueryChunks);
            if (DemandDiagnostics.Check(context, "MapFoliageDistribution.MaxQueryChunks.1", "MaxQueryChunks", 0, (decimal)xs.Length * zs.Length, admitted.MaxQueryChunks, (long)xs.Length * zs.Length > admitted.MaxQueryChunks, CapacityDemandLifetime.Cumulative)) throw Refuse(MapFoliageStatus.CapacityExceeded, "query chunks");
            var batches = new List<MapFoliageSampleBatch>();
            foreach (int z in zs)
                foreach (int x in xs)
                {
                    var key = new MapFoliageChunkKey(x, z);
                    RectArea rect = Rectangle(layer, key);
                    var clipped = new RectArea(MathF.Max(rect.MinX, area.MinX), MathF.Max(rect.MinZ, area.MinZ),
                        MathF.Min(rect.MaxX, area.MaxX), MathF.Min(rect.MaxZ, area.MaxZ));
                    if (clipped.MinX >= clipped.MaxX || clipped.MinZ >= clipped.MaxZ) continue;
                    batches.Add(GenerateCore(context, layer, key, clipped, admitted, budget, meter, observe));
                }
            return new(MapFoliageStatus.Complete, batches, meter.Work(budget.Bytes));
        }
        catch (MapFoliageRefusal refusal) { return new(refusal.Status, [], meter.Work(budget.Bytes)); }
    }

    static int[] QueryChunks(MapFoliageLayer layer, int first, int last, bool x, int maxChunks)
    {
        var chunks = new List<int>();
        foreach (int chunk in MapFoliageRasterReads.Chunks(layer, first, last, x))
        {
            if (DemandDiagnostics.Check(chunks, "MapFoliageDistribution.MaxQueryChunks.2", "MaxQueryChunks", chunks.Count, 1, maxChunks, chunks.Count >= maxChunks, CapacityDemandLifetime.Inventory)) throw Refuse(MapFoliageStatus.CapacityExceeded, "query chunks");
            chunks.Add(chunk);
        }
        return chunks.ToArray();
    }

    static MapFoliageSampleBatch GenerateCore(IMapFoliageInputs context, MapFoliageLayer layer, MapFoliageChunkKey chunk,
        RectArea area, MapFoliageLimits limits, MapFoliageRetentionBudget budget, Meter meter, Action<MapFoliageDecision>? observe)
    {
        context.RequireChunk(layer, chunk);
        MapFoliageLatticeRange range = MapFoliageLatticeRange.Of(area.MinX, area.MinZ, area.MaxX, area.MaxZ, layer.Spacing);
        if (DemandDiagnostics.Check(budget, "MapFoliageDistribution.MaxCandidates.1", "MaxCandidates", 0, (decimal)range.Count, limits.MaxCandidates, range.Count > limits.MaxCandidates, CapacityDemandLifetime.Cumulative)) throw Refuse(MapFoliageStatus.CapacityExceeded, "chunk candidates");
        if (!budget.TryRetainBatch()) throw Refuse(MapFoliageStatus.CapacityExceeded, "retained batch bytes");
        long previousSamples = meter.Samples;
        long initialBytes = budget.Bytes - 256;
        MapFoliageWork initial = meter.Work();
        bool offRaster = KnownOffRaster(layer, Rectangle(layer, chunk));
        var evaluation = new Evaluation(context, layer, limits, meter);
        var samples = new List<MapFoliageSample>();
        Scan(evaluation, range, area, charge => !DemandDiagnostics.Check(meter, "MapFoliageDistribution.MaxSamples.1", "MaxSamples", meter.Samples - previousSamples, 1, limits.MaxSamples, !(meter.Samples - previousSamples < limits.MaxSamples), CapacityDemandLifetime.Cumulative) && budget.TryRetain(charge), row =>
        {
            observe?.Invoke(row);
            if (row.Sample is { } sample) samples.Add(sample);
        });
        // An empty batch binds its exact prepared dependencies. A known off-raster chunk prepared nothing, so it binds
        // only the raster facts that make it off-raster.
        System.Diagnostics.Debug.Assert(!offRaster || meter.PrepareScopes == initial.PrepareScopes);
        string digest = offRaster ? MapFoliageDependencies.OffRaster(layer, chunk) : evaluation.Digest(chunk);
        MapFoliageWork work = meter.Difference(initial, budget.Bytes - initialBytes);
        return new(layer.Id, chunk, MapFoliageStatus.Complete, digest, samples, work);
    }

    /// <summary>True only when no candidate the chunk can own reaches support or clearance preparation, so its batch is
    /// a Complete empty batch. A candidate prepares only from <see cref="Evaluation.Decide"/>, after its jittered point
    /// passes the half-open clip (at least MinX and below MaxX, likewise on Z) and then the released interpolation's
    /// raster test (u from 0 through Width - 1, v from 0 through Depth - 1). Generate clips to the chunk rectangle and Query to
    /// a part of it, so every owned point lies in [MinX, BitDecrement(MaxX)] on X and likewise on Z, whatever the seed,
    /// spacing or jitter. A candidate coordinate is a finite product, or an infinity the clip rejects, never NaN. The
    /// raster coordinate never decreases as the world coordinate grows (see
    /// <see cref="MapFoliageSupport.RasterCoordinate"/>), so its values at the first and last owned floats bound every
    /// owned point's. When either bound lies outside the raster on either axis, every owned point is RasterZero before
    /// preparation. Zero density bytes inside the raster still prepare, since those rows report support reasons. Any
    /// other case, including an empty rectangle or a cell size that is not positive, prepares.</summary>
    internal static bool KnownOffRaster(MapFoliageLayer layer, RectArea chunk)
    {
        if (!(layer.CellSize > 0)) return false;
        float lastX = MathF.BitDecrement(chunk.MaxX), lastZ = MathF.BitDecrement(chunk.MaxZ);
        if (!(chunk.MinX <= lastX && chunk.MinZ <= lastZ)) return false;
        return MapFoliageSupport.RasterCoordinate(lastX, layer.OriginX, layer.CellSize) < 0 ||
            MapFoliageSupport.RasterCoordinate(chunk.MinX, layer.OriginX, layer.CellSize) > layer.Width - 1 ||
            MapFoliageSupport.RasterCoordinate(lastZ, layer.OriginZ, layer.CellSize) < 0 ||
            MapFoliageSupport.RasterCoordinate(chunk.MinZ, layer.OriginZ, layer.CellSize) > layer.Depth - 1;
    }

    internal static MapFoliageLayer Layer(IMapFoliageInputs context, string id) =>
        context.Foliage?.Layers.FirstOrDefault(l => string.Equals(l.Id, id, StringComparison.Ordinal)) ??
        throw Refuse(MapFoliageStatus.Invalid, "unknown foliage layer");

    internal static RectArea Rectangle(MapFoliageLayer layer, MapFoliageChunkKey chunk)
    {
        var r = MapFoliageSupport.Rect(layer, chunk);
        if (!float.IsFinite((float)r.MinX) || !float.IsFinite((float)r.MinZ) || !float.IsFinite((float)r.MaxX) || !float.IsFinite((float)r.MaxZ))
            throw Refuse(MapFoliageStatus.NotRepresentable, "chunk rectangle");
        return new((float)r.MinX, (float)r.MinZ, (float)r.MaxX, (float)r.MaxZ);
    }

    internal static int ChunkIndex(MapFoliageLayer layer, float point, bool x) => MapFoliageRasterReads.Owner(layer, point, x);

    internal static MapFoliageRefusal Refuse(MapFoliageStatus status, string detail) => new(status, MapFoliageSupport.NoWork, detail);

    internal static MapFoliageLimits Limits(MapFoliageLimits? requested)
    {
        MapFoliageLimits l = requested ?? new();
        if (l.MaxCandidates < 0 || l.MaxSamples < 0 || l.MaxPrepareScopes < 0 || l.MaxFaces < 0 || l.MaxRuleCandidates < 0 ||
            l.MaxMaskCandidates < 0 || l.MaxQueryChunks < 0 || l.MaxQuerySamples < 0 || l.MaxQueryRetainedBytes < 0 ||
            l.MaxDifferentialCandidates < 0 || l.MaxChangeRows < 0 || l.MaxComparisonPreparedChunks < 0 || l.MaxComparisonRetainedBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(requested));
        return l;
    }

    internal sealed class Meter
    {
        internal long Candidates, Samples, PrepareScopes, Faces, Rules, Masks, Queries;
        internal void Preparation(MapFoliageWork w)
        { PrepareScopes += w.PrepareScopes; Faces += w.Faces; Rules += w.RuleCandidates; Masks += w.MaskCandidates; Queries += w.SurfaceQueries; }
        internal MapFoliageWork Work(long retained = 0) => new(Candidates, Samples, PrepareScopes, Faces, Rules, Masks, Queries, retained);
        internal MapFoliageWork Difference(MapFoliageWork old, long retained) => new(Candidates - old.Candidates, Samples - old.Samples,
            PrepareScopes - old.PrepareScopes, Faces - old.Faces, Rules - old.RuleCandidates, Masks - old.MaskCandidates, Queries - old.SurfaceQueries, retained);
    }

    /// <summary>Shared candidate-to-sample sink for generation and reconciliation. Prepared entries have FIFO ownership
    /// and are charged against a shared block budget on both proposals before preparation or retention.</summary>
    internal sealed class Evaluation(IMapFoliageInputs context, MapFoliageLayer layer, MapFoliageLimits limits, Meter meter,
        Reconciliation? reconciliation = null)
    {
        readonly GroundCoverCandidateEvaluator _evaluator = GroundCoverDistribution.Prepare(new()
        {
            Seed = layer.Seed,
            Spacing = layer.Spacing,
            ScaleMin = layer.ScaleMin,
            ScaleMax = layer.ScaleMax,
            RootOffset = layer.RootOffset,
            Models = layer.Models.Select(m => new GroundCoverModel(m.AssetId, m.Weight)).ToArray(),
        });
        readonly Dictionary<MapFoliageChunkKey, Prepared> _cache = new();
        readonly Queue<MapFoliageChunkKey> _order = new();
        sealed record Prepared(MapPreparedFoliageClearance Clearance, long Charge);

        internal MapFoliageDecision Decide(int x, int z, RectArea? clip, Func<long, bool> admitSample,
            MapFoliageInstanceBuilder? instances = null)
        {
            meter.Candidates++;
            MapFoliageSurfacePoint? point = null;
            bool outside = false;
            GroundCoverDecision distribution = _evaluator.Evaluate(x, z, c =>
            {
                if (clip is { } area && (c.WorldX < area.MinX || c.WorldX >= area.MaxX || c.WorldZ < area.MinZ || c.WorldZ >= area.MaxZ))
                { outside = true; return new(null); }
                // Outside the authored raster is known zero, even if no support geometry exists there. KnownOffRaster
                // derives its chunk test from this point test.
                float u = MapFoliageSupport.RasterCoordinate(c.WorldX, layer.OriginX, layer.CellSize);
                float v = MapFoliageSupport.RasterCoordinate(c.WorldZ, layer.OriginZ, layer.CellSize);
                if (u < 0 || v < 0 || u > layer.Width - 1 || v > layer.Depth - 1)
                {
                    point = new(MapFoliageStatus.Complete, MapFoliageReason.RasterZero, layer.Support.SurfaceId, null, null, 0, Vector3.UnitY, 0, 0);
                    return new(new GroundCoverSample(0, Vector3.UnitY, 0));
                }
                var key = new MapFoliageChunkKey(ChunkIndex(layer, c.WorldX, true), ChunkIndex(layer, c.WorldZ, false));
                Prepared prepared = Get(key);
                point = prepared.Clearance.Evaluate(prepared.Clearance.Support.Sample(c.WorldX, c.WorldZ), c.WorldX, c.WorldZ);
                if (point.Status != MapFoliageStatus.Complete) throw Refuse(point.Status, "candidate support or clearance");
                return point.Reason == MapFoliageReason.OutsideDomain ? new(null) : new(new GroundCoverSample(point.Height, point.Normal, point.Density));
            });
            if (outside) distribution = distribution with { Kind = GroundCoverDecisionKind.OutsideArea };
            var address = new MapFoliageAddress(layer.Id, point?.SupportOwnerId, x, z);
            MapFoliageSample? sample = null;
            MapFoliageReason reason = point?.Reason ?? MapFoliageReason.OutsideDomain;
            if (distribution.Instance is { } instance)
            {
                MapResolvedAsset model = context.Assets.GetAsset(instance.ModelId);
                long charge = instances is null ? MapFoliageRetentionBudget.ChargeSample(layer.Id, point!.SupportOwnerId!, model.Id, layer.Policy.Id, model.MaterialResourceIds) : 0;
                if (!admitSample(charge) || instances is not null && !instances.TryPrepare(point!.SupportOwnerId, model.Id))
                    throw Refuse(MapFoliageStatus.CapacityExceeded, "retained samples or bytes");
                if (!Matrix4x4.Decompose(instance.Transform, out _, out Quaternion rotation, out _))
                    throw Refuse(MapFoliageStatus.NotRepresentable, "candidate matrix decomposition");
                meter.Samples++;
                if (instances is null)
                    sample = new(new(address, 1, layer.Policy), point!.OccupiedSpaceId, point.Face, model.Id, model.MaterialResourceIds,
                        point.VisibleMaterial, instance.Position, Quaternion.Normalize(rotation), distribution.Scale, instance.Transform, instance.ThinningRank, true);
                else instances.AppendPrepared(x, z, instance);
            }
            else if (distribution.Kind == GroundCoverDecisionKind.DensityRejected) reason = MapFoliageReason.DensityRejected;
            return new(address, point?.OccupiedSpaceId, reason, distribution, sample) { Surface = point };
        }

        Prepared Get(MapFoliageChunkKey key)
        {
            if (_cache.TryGetValue(key, out Prepared? result)) return result;
            int cap = reconciliation is null ? 1 : limits.MaxComparisonPreparedChunks;
            if (DemandDiagnostics.Check(this, "MapFoliageDistribution.MaxComparisonPreparedChunks.1", "MaxComparisonPreparedChunks", 0, 1, cap, cap == 0, CapacityDemandLifetime.Inventory)) throw Refuse(MapFoliageStatus.CapacityExceeded, "comparison prepared chunks");
            if (DemandDiagnostics.Check(this, "MapFoliageDistribution.MaxComparisonPreparedChunks.2", "MaxComparisonPreparedChunks", _cache.Count, 1, cap, _cache.Count >= cap, CapacityDemandLifetime.Inventory)) Evict();
            MapFoliageLimits scoped = limits;
            long charge = checked(4096 + 512L * layer.ClearanceRules.Count + 128L * layer.Materials.Count +
                128L * layer.AllowedVisibleMaterials.Count + 256L * layer.Models.Count);
            if (reconciliation is not null)
            {
                while (reconciliation.Available < charge && _order.Count != 0) Evict();
                reconciliation.Take(charge);
                scoped = limits with
                {
                    MaxFaces = (int)Math.Min(limits.MaxFaces, reconciliation.Available / 1280),
                    ChargePreparation = bytes => { reconciliation.Take(bytes); charge += bytes; },
                };
            }
            var clearance = MapFoliageClearance.Prepare(context, layer, key, scoped);
            meter.Preparation(clearance.Work);
            if (clearance.Status != MapFoliageStatus.Complete) throw Refuse(clearance.Status, clearance.Detail ?? "preparation");

            result = new(clearance, charge);
            _cache.Add(key, result); _order.Enqueue(key);
            return result;
        }

        void Evict()
        {
            MapFoliageChunkKey key = _order.Dequeue();
            Prepared old = _cache[key]; _cache.Remove(key);
            reconciliation?.Release(old.Charge);
        }

        internal string Digest(MapFoliageChunkKey key)
        {
            Prepared prepared = Get(key);
            return MapFoliageDependencies.Compute(prepared.Clearance.Support, prepared.Clearance, layer, key);
        }
    }

    internal sealed class Reconciliation(long limit)
    {
        long _current;
        internal long Peak { get; private set; }
        internal long Available => limit - _current;
        internal void Take(long bytes)
        {
            if (DemandDiagnostics.Check(this, "MapFoliageDistribution.MaxComparisonRetainedBytes.1", "MaxComparisonRetainedBytes", _current, bytes, limit, bytes > Available, CapacityDemandLifetime.Transient)) throw Refuse(MapFoliageStatus.CapacityExceeded, "comparison retained bytes");
            _current += bytes; Peak = Math.Max(Peak, _current);
        }
        internal void Release(long bytes) => _current -= bytes;
    }
}
