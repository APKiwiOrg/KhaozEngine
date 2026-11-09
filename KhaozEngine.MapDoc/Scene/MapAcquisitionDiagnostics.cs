using System;
using System.Collections.Generic;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Scene;

internal enum MapAcquisitionStage { Scene, NativeInput, NativeQuery, View, Seat, ResolverGeometry, ResolverTables, ProductCapture, ScenePublication, FoliageSeat, Dependency, Policy }
internal enum MapAcquisitionCaller { NativeKey, Seat, Marker, ProductView, FoliageSeat, Dependency, PolicyReacquisition }
internal sealed class MapAcquisitionStageCounts
{
    internal long RetainedHighWater { get; set; }
    internal long WorkspaceHighWater { get; set; }
    internal int ReceiptHighWater { get; set; }
}
internal sealed class MapAcquisitionCallerCounts
{
    internal long Scopes { get; set; }
    internal long Scenes { get; set; }
    internal long Keys { get; set; }
    internal long RepeatedSources { get; set; }
    internal long RepeatedKeys { get; set; }
}
internal readonly record struct MapAcquisitionBoundary(MapAcquisitionStage Stage, bool Entering, long RetainedBytes, long WorkspaceBytes, int Receipts);

/// <summary>Opt-in modeled counts. Published observations contain no source names, keys or content.</summary>
internal sealed class MapAcquisitionDiagnostics(Func<(long Retained, long Workspace, int Receipts)> current)
{
    readonly Dictionary<MapAcquisitionStage, MapAcquisitionStageCounts> _stages = new();
    readonly Dictionary<MapAcquisitionCaller, MapAcquisitionCallerCounts> _callers = new();
    readonly HashSet<string> _sources = new(StringComparer.Ordinal);
    readonly HashSet<(string Source, MapPatchKey Key)> _keys = new();
    readonly List<MapAcquisitionStage> _active = new();
    MapAcquisitionCaller _caller = MapAcquisitionCaller.ProductView;
    internal IReadOnlyDictionary<MapAcquisitionStage, MapAcquisitionStageCounts> Stages => _stages;
    internal IReadOnlyDictionary<MapAcquisitionCaller, MapAcquisitionCallerCounts> Callers => _callers;
    internal Action<MapAcquisitionBoundary>? Boundary { get; set; }
    internal IDisposable Enter(MapAcquisitionStage stage, MapAcquisitionCaller? caller = null) => new Stage(this, stage, caller);
    MapAcquisitionCallerCounts Caller(MapAcquisitionCaller caller)
    {
        if (!_callers.TryGetValue(caller, out var counts)) _callers.Add(caller, counts = new());
        return counts;
    }
    internal void Scene(MapAcquisitionCaller caller) => Caller(caller).Scenes++;
    internal void Scope(string source)
    {
        var counts = Caller(_caller); counts.Scopes++;
        if (!_sources.Add(source)) counts.RepeatedSources++;
    }
    internal void Key(string source, MapPatchKey key)
    {
        var counts = Caller(_caller); counts.Keys++;
        if (!_keys.Add((source, key))) counts.RepeatedKeys++;
    }
    internal void Sample()
    {
        var live = current();
        foreach (var stage in _active)
        {
            var counts = _stages[stage];
            counts.RetainedHighWater = Math.Max(counts.RetainedHighWater, live.Retained);
            counts.WorkspaceHighWater = Math.Max(counts.WorkspaceHighWater, live.Workspace);
            counts.ReceiptHighWater = Math.Max(counts.ReceiptHighWater, live.Receipts);
        }
    }
    void Observe(MapAcquisitionStage stage, bool entering)
    {
        Sample();
        var live = current();
        Boundary?.Invoke(new(stage, entering, live.Retained, live.Workspace, live.Receipts));
    }
    sealed class Stage : IDisposable
    {
        readonly MapAcquisitionDiagnostics _owner;
        readonly MapAcquisitionStage _stage;
        readonly MapAcquisitionCaller _previous;
        bool _disposed;
        internal Stage(MapAcquisitionDiagnostics owner, MapAcquisitionStage stage, MapAcquisitionCaller? caller)
        {
            _owner = owner; _stage = stage; _previous = owner._caller;
            if (caller is { } value) owner._caller = value;
            if (!owner._stages.ContainsKey(stage)) owner._stages.Add(stage, new());
            owner._active.Add(stage); owner.Observe(stage, true);
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true; _owner.Observe(_stage, false);
            _owner._active.RemoveAt(_owner._active.Count - 1); _owner._caller = _previous;
        }
    }
}
