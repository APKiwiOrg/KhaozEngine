using System;
using System.Collections.Generic;

namespace KhaozEngine.MapDoc.Scene;

/// <summary>Before-work admission of live modeled capacity, independent of garbage collection.</summary>
internal sealed class MapPreparationLedger(long allowance, string limitName)
{
    Window? _window;
    long _external;
    readonly Dictionary<object, MapPreparationReservation> _legacy = new();
    internal long WorkspaceBytes { get; private set; }
    internal long RetainedBytes { get; private set; }
    internal long PeakBytes { get; private set; }
    internal int RetainedReceipts { get; private set; }
    internal Action? Changed { get; set; }
    internal void ReceiptChanged(int delta) { RetainedReceipts = checked(RetainedReceipts + delta); Changed?.Invoke(); }
    internal void Notify() => Changed?.Invoke();
    internal object WindowIdentity => (object?)_window ?? this;
    internal IDisposable? ReferenceWindow(long bytes, object? origin, ICollection<object>? references)
    {
        if (_window is null) return null;
        var covered = new HashSet<object>(ReferenceEqualityComparer.Instance);
        Cover(origin);
        foreach (var reference in references ?? []) Cover(reference);
        return _window.Reference(bytes, covered);
        void Cover(object? key)
        { for (var view = key as Window; view is not null; view = view.Parent) covered.Add(view); }
    }
    internal long Remaining
    {
        get
        {
            long remaining = allowance - WorkspaceBytes - RetainedBytes - _external;
            for (var view = _window; view is not null; view = view.Parent)
                remaining = Math.Min(remaining, view.Remaining);
            return remaining;
        }
    }
    internal MapSceneCapacity Capacity(decimal additional)
    {
        Window? binding = null;
        long remaining = allowance - WorkspaceBytes - RetainedBytes - _external;
        for (var view = _window; view is not null; view = view.Parent)
            if (view.Remaining <= remaining) { remaining = view.Remaining; binding = view; }
        return binding is null ? new(limitName, (decimal)WorkspaceBytes + RetainedBytes + _external + additional, allowance)
            : binding.Capacity(additional);
    }
    internal MapPreparationReservation Workspace() => new(this, _window, false);
    internal Window OpenWindow(long bytes, string name, long reused)
    {
        if (bytes < 0 || reused < 0) throw new MapSceneCapacityException(name, reused, checked(bytes + reused));
        return new(this, _window, checked(bytes + reused), name, reused);
    }
    internal void ReserveLegacy(long bytes)
    {
        object key = (object?)_window ?? this;
        if (!_legacy.TryGetValue(key, out var owner)) _legacy.Add(key, owner = new(this, _window, true));
        owner.Reserve(bytes);
    }
    internal void ReleaseLegacy(long bytes)
    {
        object key = (object?)_window ?? this;
        if (!_legacy.TryGetValue(key, out var owner)) throw new ArgumentOutOfRangeException(nameof(bytes));
        owner.Release(bytes);
    }
    internal void Add(Window? view, long bytes, bool retained)
    {
        if (bytes < 0 || bytes > Remaining) throw new MapSceneCapacityException(Capacity(bytes));
        if (retained) RetainedBytes += bytes; else WorkspaceBytes += bytes;
        Adjust(view, bytes);
        PeakBytes = Math.Max(PeakBytes, WorkspaceBytes + RetainedBytes + _external);
    }
    internal void Remove(Window? view, long bytes, bool retained)
    {
        if (retained) RetainedBytes = checked(RetainedBytes - bytes); else WorkspaceBytes = checked(WorkspaceBytes - bytes);
        Adjust(view, -bytes);
    }
    internal void Transfer(long bytes)
    { WorkspaceBytes -= bytes; RetainedBytes += bytes; }
    internal void ReturnToWorkspace(long bytes)
    { RetainedBytes -= bytes; WorkspaceBytes += bytes; }
    internal void Reattribute(Window? source, Window? destination, long bytes)
    {
        Window? binding = null;
        for (var view = destination; view is not null; view = view.Parent)
        {
            bool covered = false;
            for (var previous = source; previous is not null; previous = previous.Parent)
                if (previous == view) { covered = true; break; }
            if (!covered && (binding is null || view.Remaining < binding.Remaining)) binding = view;
        }
        if (binding is not null && bytes > binding.Remaining)
            throw new MapSceneCapacityException(binding.Capacity(bytes));
        // Common ancestors already include this inventory. Only its attribution changes, not root demand.
        Adjust(source, -bytes);
        Adjust(destination, bytes);
    }
    static void Adjust(Window? view, long bytes)
    { for (; view is not null; view = view.Parent) view.Live = checked(view.Live + bytes); }

    internal sealed class Window : IDisposable
    {
        readonly MapPreparationLedger _owner;
        readonly long _allowance, _reused;
        readonly string _name;
        long _external;
        bool _disposed;
        internal Window? Parent { get; }
        internal long Live;
        internal long Remaining => _allowance - _reused - Live;
        internal Window(MapPreparationLedger owner, Window? parent, long allowance, string name, long reused)
        {
            _owner = owner; Parent = parent; _allowance = allowance; _name = name; _reused = reused;
            owner._window = this;
        }
        internal MapSceneCapacity Capacity(decimal bytes) => new(_name, (decimal)Live + _reused + bytes, _allowance);
        internal IDisposable Reference(long bytes, HashSet<object> covered)
        {
            Window? stop = this;
            while (stop is not null && !covered.Contains(stop)) stop = stop.Parent;
            Window? binding = null;
            for (var view = this; view != stop; view = view!.Parent)
                if (binding is null || view!.Remaining < binding.Remaining) binding = view;
            if (bytes < 0 || binding is not null && bytes > binding.Remaining)
                throw new MapSceneCapacityException((binding ?? this).Capacity(bytes));
            AdjustReference(this, stop, bytes);
            return new ReferenceLease(this, stop, bytes);
        }
        static void AdjustReference(Window start, Window? stop, long bytes)
        { for (var view = start; view != stop; view = view!.Parent) view!.Live = checked(view.Live + bytes); }
        sealed class ReferenceLease(Window window, Window? stop, long bytes) : IDisposable
        {
            bool _disposed;
            public void Dispose() { if (_disposed) return; _disposed = true; AdjustReference(window, stop, -bytes); }
        }
        internal void ChargeExternal(long bytes)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (bytes < 0 || bytes > _owner.Remaining) throw new MapSceneCapacityException(_owner.Capacity(bytes));
            _owner._external += bytes; _external += bytes; Adjust(this, bytes);
            _owner.PeakBytes = Math.Max(_owner.PeakBytes, _owner.WorkspaceBytes + _owner.RetainedBytes + _owner._external);
            _owner.Notify();
        }
        public void Dispose()
        {
            if (_disposed) return;
            if (_owner._window != this) throw new InvalidOperationException("Preparation windows must close in nesting order.");
            _disposed = true; _owner._window = Parent;
            _owner._external -= _external; Adjust(this, -_external);
        }
    }
}
