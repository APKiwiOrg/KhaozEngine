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

    internal bool HasGeneral(QueryMobility mobility, StaticQueryExclusions? exclusions) =>
        mobility != QueryMobility.Statics && _dynamics.Count != 0 ||
        mobility != QueryMobility.Dynamics && (exclusions?.SelectedGeneralSweepStatics ?? _statics.Count) != 0;

    internal StaticQueryExclusions CreateExclusions(SeamStaticHandle[] snapshot, IReadOnlyDictionary<int, int> reverse)
    {
        var exclusions = new StaticQueryExclusions(snapshot, reverse);
        exclusions.InitializeGeneralSweepStatics(_statics);
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

    internal void Observe(int id, bool dynamic, bool general)
    {
        HashSet<int> set = dynamic ? _dynamics : _statics;
        bool changed = general ? set.Add(id) : set.Remove(id);
        if (!changed || dynamic) return;
        lock (_views)
        {
            for (int i = _views.Count - 1; i >= 0; i--)
            {
                if (!_views[i].TryGetTarget(out var view)) { _views.RemoveAt(i); continue; }
                if (!view.ExcludesSweepStatic(id)) view.SelectedGeneralSweepStatics += general ? 1 : -1;
            }
        }
    }

    internal void Clear()
    {
        _statics.Clear(); _dynamics.Clear();
        lock (_views)
        {
            foreach (var binding in _views)
                if (binding.TryGetTarget(out var view)) view.SelectedGeneralSweepStatics = 0;
            _views.Clear();
        }
    }
}
