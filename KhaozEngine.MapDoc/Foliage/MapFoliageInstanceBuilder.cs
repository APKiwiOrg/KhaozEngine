using KhaozEngine.Diagnostics;
using DemandDiagnostics = KhaozEngine.Diagnostics.CapacityDemandDiagnostics;
using System;
using System.Numerics;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapDoc.Foliage;

/// <summary>One-shot accepted rows with exact frozen arrays. Original matrix and position bits are copied independently.</summary>
internal sealed class MapFoliageInstanceBuilder : IDisposable
{
    readonly string _layerId;
    readonly MapFoliagePolicy _policy;
    readonly MapFoliageChunkKey _chunk;
    readonly long _maxRows;
    readonly MapFoliageColumnBudget _budget;
    readonly MapFoliageOrdinalTable _models, _supports;
    Row[]? _rows;
    int _count, _model, _support;
    long _metadata;
    bool _prepared, _frozen, _disposed;
    internal long LiveBytes => _budget.Live;
    internal long PeakBytes => _budget.Peak;
    internal readonly record struct Row(Matrix4x4 Transform, Vector3 Position, float Rank, int X, int Z, int Support, int Model);

    internal MapFoliageInstanceBuilder(string layerId, MapFoliagePolicy policy, MapFoliageChunkKey chunk, MapFoliageLimits limits)
    {
        _layerId = layerId; _policy = policy; _chunk = chunk;
        _maxRows = Math.Min(limits.MaxSamples, limits.MaxQuerySamples);
        _budget = new(limits.MaxQueryRetainedBytes, limits.AllocateInstances, limits.ReleaseInstances);
        _budget.Take(MapFoliageInventory.ConstructionControls);
        _models = new(_budget); _supports = new(_budget);
        _metadata = checked(32 + MapFoliageInventory.Text(layerId) +
            (ReferenceEquals(layerId, policy.Id) ? 0 : MapFoliageInventory.Text(policy.Id)));
        _budget.Take(_metadata);
    }
    void Open()
    { if (_disposed || _frozen || _prepared) throw new InvalidOperationException("The instance builder cannot accept another operation."); }
    bool Holds(string value) => ReferenceEquals(value, _layerId) || ReferenceEquals(value, _policy.Id) || _models.Holds(value) || _supports.Holds(value);
    void RetainText(string? value)
    {
        if (value is null || Holds(value)) return;
        long bytes = MapFoliageInventory.Text(value);
        _budget.Take(bytes); _metadata = checked(_metadata + bytes);
    }
    internal bool TryPrepare(string? support, string model)
    {
        Open(); ArgumentNullException.ThrowIfNull(model);
        if (DemandDiagnostics.Check(this, "MapFoliageInstanceBuilder.MaxQuerySamples.1", "MaxQuerySamples", _count, 1, _maxRows, _count >= _maxRows, CapacityDemandLifetime.Inventory)) return false;
        if (!_supports.Contains(support)) RetainText(support);
        _support = _supports.Intern(support);
        if (!_models.Contains(model)) RetainText(model);
        _model = _models.Intern(model);
        if (_rows is null || _count == _rows.Length)
        {
            int capacity = _rows is null ? 4 : checked(_rows.Length * 2);
            long old = _rows is null ? 0 : MapFoliageInventory.Array(_rows.Length, 96);
            _budget.Take(MapFoliageInventory.Array(capacity, 96));
            Grow(capacity);
            _budget.Release(old);
        }
        _prepared = true;
        return true;
    }
    void Grow(int capacity)
    {
        var rows = new Row[capacity];
        if (_rows is not null) Array.Copy(_rows, rows, _count);
        _rows = rows;
    }
    internal void AppendPrepared(int x, int z, GroundCoverInstance instance)
    {
        if (!_prepared || _frozen || _disposed) throw new InvalidOperationException("Prepare an accepted instance before appending it.");
        _rows![_count++] = new(instance.Transform, instance.Position, instance.ThinningRank, x, z, _support, _model);
        _prepared = false;
    }
    internal MapFoliageInstanceBatch Freeze(string digest, MapFoliageWork work)
    {
        Open(); RetainText(digest);
        _budget.Take(MapFoliageInventory.FrozenControls);
        var transforms = Allocate<Matrix4x4>(64); var positions = Allocate<Vector3>(12);
        var ranks = Allocate<float>(4); var modelIndices = Allocate<int>(4);
        var xs = Allocate<int>(4); var zs = Allocate<int>(4); var supports = Allocate<int>(4);
        var modelIds = _models.FreezeModels(); var supportIds = _supports.Freeze();
        for (int i = 0; i < _count; i++)
        {
            var row = _rows![i];
            transforms[i] = row.Transform; positions[i] = row.Position; ranks[i] = row.Rank; modelIndices[i] = row.Model;
            xs[i] = row.X; zs[i] = row.Z; supports[i] = row.Support;
        }
        var columns = GroundCoverInstanceColumns.TakeOwnership(transforms, positions, ranks, modelIndices, modelIds);
        ClearWorkspace(); _budget.Release(MapFoliageInventory.ConstructionControls);
        var result = new MapFoliageInstanceBatch(_layerId, _chunk, digest, _policy, columns, xs, zs, supports, supportIds,
            work with { RetainedBytes = _budget.Live });
        _frozen = true;
        return result;
    }
    T[] Allocate<T>(int size)
    { _budget.Take(MapFoliageInventory.Array(_count, size)); return new T[_count]; }
    void ClearWorkspace()
    {
        long rows = _rows is null ? 0 : MapFoliageInventory.Array(_rows.Length, 96);
        _rows = null; _models.Clear(); _supports.Clear(); _budget.Release(rows);
    }
    public void Dispose()
    {
        if (_disposed) return;
        ClearWorkspace();
        if (!_frozen) _budget.Release(_budget.Live);
        _disposed = true;
    }
}
