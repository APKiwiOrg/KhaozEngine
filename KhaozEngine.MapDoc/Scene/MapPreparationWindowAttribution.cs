using KhaozEngine.Diagnostics;
using System;
using System.Collections.Generic;

namespace KhaozEngine.MapDoc.Scene;

/// <summary>One shared obligation per borrowing ancestor, retained until its last consumer leaves.</summary>
internal sealed class MapPreparationWindowAttribution(object origin)
{
    readonly object _gate = new();
    readonly Dictionary<object, View> _views = new(ReferenceEqualityComparer.Instance);

    internal IDisposable? Acquire(long bytes, object window)
    {
        lock (_gate)
        {
            var windows = new List<MapPreparationLedger.Window>();
            MapPreparationLedger.Window? binding = null;
            for (var current = window as MapPreparationLedger.Window; current is not null; current = current.Parent)
            {
                if (Covers(current, origin)) break;
                windows.Add(current);
                if (!_views.ContainsKey(current) && (binding is null || current.Remaining < binding.Remaining))
                    binding = current;
            }
            if (windows.Count == 0) return null;
            if (binding is not null && CapacityDemandDiagnostics.Check(binding, "MapPreparationWindowAttribution.Acquire", binding.LimitName, binding.Total, bytes, binding.Allowance, bytes > binding.Remaining, CapacityDemandLifetime.Transient))
                throw new MapSceneCapacityException(binding.RefuseReference(bytes,
                    windows[0], windows[^1].Parent, _views.Keys));
            var covered = new List<(object Key, View Value)>();
            try
            {
                foreach (var current in windows)
                {
                    if (!_views.TryGetValue(current, out var view))
                    {
                        view = new(current.ReferenceOnly(bytes));
                        _views.Add(current, view);
                    }
                    view.Owners++;
                    covered.Add((current, view));
                }
                return new Lease(() => { lock (_gate) ReleaseViews(); });
            }
            catch { ReleaseViews(); throw; }
            void ReleaseViews()
            {
                foreach (var ancestor in covered)
                    if (--ancestor.Value.Owners == 0)
                    { ancestor.Value.Reference.Dispose(); _views.Remove(ancestor.Key); }
            }
        }
    }
    static bool Covers(object ancestor, object window)
    {
        if (ReferenceEquals(ancestor, window)) return true;
        for (var current = window as MapPreparationLedger.Window; current is not null; current = current.Parent)
            if (ReferenceEquals(ancestor, current)) return true;
        return false;
    }
    sealed class View(IDisposable reference)
    { internal int Owners; internal IDisposable Reference => reference; }
    sealed class Lease(Action release) : IDisposable
    {
        bool _disposed;
        public void Dispose() { if (_disposed) return; _disposed = true; release(); }
    }
}
