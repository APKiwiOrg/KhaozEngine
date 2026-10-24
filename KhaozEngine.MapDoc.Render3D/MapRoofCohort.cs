using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapDoc.Render3D;

/// <summary>Admitted canonical roof policy retained with one immutable cluster presentation generation.</summary>
internal sealed class MapRoofCohort : IPropClusterVisibility
{
    readonly NativePropClusterCpuBuild _build;
    readonly int[] _indices;
    readonly int _policyCapacity;
    MapFramePoint _observer;
    MapRoofMode _mode;
    bool _evaluated;
    internal MapRoofEvaluationBuffer Evaluation { get; }
    internal IReadOnlyDictionary<string, int> Indices { get; }
    internal long RetainedBytes => checked(128L + 4L * _indices.LongLength + 64L * _policyCapacity + Evaluation.RetainedBytes);
    internal MapRoofCohort(NativePropClusterCpuBuild build, MapRoofEvaluationBuffer evaluation)
    {
        _build = build; Evaluation = evaluation;
        var indices = evaluation.Roofs.Select((r, i) => (r.RoofPlacementId, i))
            .ToDictionary(r => r.RoofPlacementId, r => r.i, StringComparer.Ordinal);
        Indices = indices; _policyCapacity = indices.EnsureCapacity(0);
        _indices = new int[build.RepresentedIds.Count];
        for (int i = 0; i < _indices.Length; i++)
            _indices[i] = Indices.TryGetValue(build.RepresentedIds[i], out int index) ? index
                : throw new MapDocumentException("Canonical roof policy omitted a cohort member.");
    }
    internal void Evaluate(MapFrameState frame)
    {
        if (_evaluated && _observer == frame.Observer && _mode == frame.RoofMode) return;
        Evaluation.Evaluate(frame.Observer, frame.RoofMode);
        _observer = frame.Observer; _mode = frame.RoofMode; _evaluated = true;
    }
    public bool Controls(PropClusterKey key) => key == _build.Request.Key;
    public int PlacementCount(PropClusterKey key, long generation)
    {
        if (!Controls(key) || generation != _build.Request.Generation)
            throw new MapDocumentException("Roof policy belongs to another presentation generation.");
        return _indices.Length;
    }
    public bool IsHidden(PropClusterKey key, long generation, int placementIndex)
    {
        _ = PlacementCount(key, generation);
        return !Evaluation.Roofs[_indices[placementIndex]].ColourVisible;
    }
}
