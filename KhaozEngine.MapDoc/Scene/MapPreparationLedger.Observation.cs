using System;
using System.Runtime.CompilerServices;
using System.Collections.Generic;

namespace KhaozEngine.MapDoc.Scene;

internal sealed partial class MapPreparationLedger
{
    long _nextWindow;
    internal Action<decimal>? RequestingBytes { get; set; }
    internal Action<long>? AllocationAdmitted { get; set; }
    long LiveBytes => checked(WorkspaceBytes + RetainedBytes + _external);
    internal PeakObservation ObservePeak(MapPreparationStageCounts? counts = null) => new(this, counts ?? new());
    internal MapSceneCapacity Refusal(decimal additional)
    {
        Request(additional, additional, _window, null, true);
        return Capacity(additional);
    }
    void Request(decimal rootIncrement, decimal windowIncrement, Window? start, Window? stop, bool refused,
        ICollection<object>? covered = null, [CallerMemberName] string site = "")
    {
        ObserveDemand(rootIncrement, windowIncrement, start, stop, covered, site);
        for (var observer = _observation; observer is not null; observer = observer.Previous)
            observer.Request(rootIncrement, windowIncrement, start, stop, refused, covered);
        RequestingBytes?.Invoke(LiveBytes + rootIncrement);
    }
    internal sealed class PeakObservation : IDisposable
    {
        readonly MapPreparationLedger _owner;
        readonly Dictionary<Window, MapPreparationDemandCounts> _windows = new();
        internal readonly PeakObservation? Previous;
        internal MapPreparationStageCounts Counts { get; }
        internal long Bytes { get; private set; }
        bool _disposed;
        internal PeakObservation(MapPreparationLedger owner, MapPreparationStageCounts counts)
        {
            _owner = owner; Previous = owner._observation; Counts = counts;
            Counts.Root.Initialize(owner.Allowance, 0, owner.LiveBytes);
            Sample(owner.LiveBytes);
            owner._observation = this;
        }
        internal void Sample(long live)
        {
            Bytes = Math.Max(Bytes, live);
            Counts.Root.Sample(live);
            for (var window = _owner._window; window is not null; window = window.Parent)
                Capture(window).Sample(window.Total);
        }
        MapPreparationDemandCounts Capture(Window window)
        {
            if (_windows.TryGetValue(window, out var counts)) return counts;
            counts = Counts.Window(window.Ordinal);
            counts.Initialize(window.Allowance, window.Reused, window.Total);
            _windows.Add(window, counts);
            return counts;
        }
        internal void Request(decimal rootIncrement, decimal windowIncrement, Window? start, Window? stop, bool refused,
            ICollection<object>? covered)
        {
            Counts.Root.Request(_owner.LiveBytes + rootIncrement, rootIncrement, refused);
            for (var window = _owner._window; window is not null; window = window.Parent)
            {
                decimal increment = 0;
                for (var charged = start; charged != stop; charged = charged!.Parent)
                    if (charged == window && covered?.Contains(window) != true) { increment = windowIncrement; break; }
                Capture(window).Request(window.Total + increment, increment, refused);
            }
        }
        public void Dispose()
        {
            if (_disposed) return;
            if (_owner._observation != this) throw new InvalidOperationException("Peak observations must close in nesting order.");
            Sample(_owner.LiveBytes);
            _owner._observation = Previous; _disposed = true;
        }
    }
}
