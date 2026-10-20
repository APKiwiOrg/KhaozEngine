using System;
using System.Collections.Generic;
using SeamStaticHandle = KhaozEngine.Physics.StaticHandle;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Mutation-maintained general-family selection. Queries read counts without scanning
/// registrations or exclusions. Weak view bindings do not extend a view's lifetime.</summary>
internal sealed class CapsuleSweepSelection
{
    readonly HashSet<int> _statics = new(), _dynamics = new();
    readonly List<WeakReference<StaticQueryExclusions>> _views = new();
    readonly Dictionary<int, CapsuleSweepPruningBounds> _staticBounds = new(), _dynamicBounds = new();
    CapsuleSweepPruningBounds _staticMaximum, _dynamicMaximum;
    bool _staticDirty, _dynamicDirty;

    internal bool HasGeneral(QueryMobility mobility, StaticQueryExclusions? exclusions) =>
        mobility != QueryMobility.Statics && _dynamics.Count != 0 ||
        mobility != QueryMobility.Dynamics && (exclusions?.SelectedGeneralSweepStatics ?? _statics.Count) != 0;

    internal CapsuleSweepPruningBounds Bounds(QueryMobility mobility, StaticQueryExclusions? exclusions) =>
        (mobility == QueryMobility.Dynamics ? default : exclusions?.GeneralSweepBounds ?? _staticMaximum)
            .Include(mobility == QueryMobility.Statics ? default : _dynamicMaximum);

    internal bool Contains(int id, bool dynamic) => (dynamic ? _dynamics : _statics).Contains(id);

    CapsuleSweepPruningBounds StaticMaximum(StaticQueryExclusions? exclusions)
    {
        CapsuleSweepPruningBounds result = default;
        foreach (var entry in _staticBounds)
            if (exclusions is null || !exclusions.ExcludesSweepStatic(entry.Key)) result = result.Include(entry.Value);
        return result;
    }

    internal StaticQueryExclusions CreateExclusions(SeamStaticHandle[] snapshot, IReadOnlyDictionary<int, int> reverse)
    {
        var exclusions = new StaticQueryExclusions(snapshot, reverse);
        exclusions.InitializeGeneralSweepStatics(_statics);
        exclusions.GeneralSweepBounds = StaticMaximum(exclusions);
        // Several readers may construct views concurrently. Mutations already hold the
        // exclusive owner gate, including every update to the published selection counts.
        lock (_views)
        {
            for (int i = _views.Count - 1; i >= 0; i--)
                if (!_views[i].TryGetTarget(out _)) _views.RemoveAt(i);
            _views.Add(new(exclusions));
        }
        return exclusions;
    }

    internal void Observe(int id, bool dynamic, bool general, CapsuleSweepPruningBounds bounds = default)
    {
        HashSet<int> set = dynamic ? _dynamics : _statics;
        bool changed = general ? set.Add(id) : set.Remove(id);
        var records = dynamic ? _dynamicBounds : _staticBounds;
        if (general)
        {
            if (!changed && records.TryGetValue(id, out var previous) && previous == bounds) return;
            records[id] = bounds;
        }
        else if (!records.Remove(id) && !changed) return;
        if (dynamic)
        {
            if (changed && general) _dynamicMaximum = _dynamicMaximum.Include(bounds);
            else _dynamicDirty = true;
            return;
        }
        if (changed && general) _staticMaximum = _staticMaximum.Include(bounds);
        else _staticDirty = true;
        lock (_views)
        {
            for (int i = _views.Count - 1; i >= 0; i--)
            {
                if (!_views[i].TryGetTarget(out var view)) { _views.RemoveAt(i); continue; }
                if (!view.ExcludesSweepStatic(id) && changed)
                {
                    view.SelectedGeneralSweepStatics += general ? 1 : -1;
                    if (general) view.GeneralSweepBounds = view.GeneralSweepBounds.Include(bounds);
                }
            }
        }
    }

    internal void PublishBounds()
    {
        // A Step or rebase can refresh every registration. Rebuild each dirty maximum
        // once at transaction completion, rather than once per changed installation.
        if (_dynamicDirty)
        {
            _dynamicMaximum = default;
            foreach (var entry in _dynamicBounds) _dynamicMaximum = _dynamicMaximum.Include(entry.Value);
            _dynamicDirty = false;
        }
        if (!_staticDirty) return;
        _staticMaximum = StaticMaximum(null);
        lock (_views)
        {
            for (int i = _views.Count - 1; i >= 0; i--)
            {
                if (!_views[i].TryGetTarget(out var view)) { _views.RemoveAt(i); continue; }
                view.GeneralSweepBounds = StaticMaximum(view);
            }
        }
        _staticDirty = false;
    }

    internal void Clear()
    {
        _statics.Clear(); _dynamics.Clear(); _staticBounds.Clear(); _dynamicBounds.Clear();
        _staticMaximum = _dynamicMaximum = default;
        _staticDirty = _dynamicDirty = false;
        lock (_views)
        {
            foreach (var binding in _views)
                if (binding.TryGetTarget(out var view)) { view.SelectedGeneralSweepStatics = 0; view.GeneralSweepBounds = default; }
            _views.Clear();
        }
    }
}
