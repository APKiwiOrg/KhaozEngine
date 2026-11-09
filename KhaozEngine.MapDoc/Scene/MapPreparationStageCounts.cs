using System;
using System.Collections.Generic;

namespace KhaozEngine.MapDoc.Scene;

/// <summary>Numerical evidence for one admission domain. Totals include its already live baseline.</summary>
public sealed class MapPreparationDemandCounts
{
    internal long WindowOrdinal { get; set; }
    public bool Observed { get; private set; }
    public long AllowanceBytes { get; private set; }
    public long ReusedBytes { get; private set; }
    public long BaselineBytes { get; private set; }
    public long AdmittedPeakBytes { get; private set; }
    public decimal RequestedBytes { get; private set; }
    public decimal RefusedBytes { get; private set; }
    public decimal LargestIncrementBytes { get; private set; }
    public long Requests { get; private set; }
    internal void Initialize(long allowance, long reused, long baseline)
    {
        if (!Observed)
        { Observed = true; AllowanceBytes = allowance; ReusedBytes = reused; BaselineBytes = baseline; }
        Sample(baseline);
    }
    internal void Sample(long live) => AdmittedPeakBytes = Math.Max(AdmittedPeakBytes, live);
    internal void Request(decimal total, decimal increment, bool refused)
    {
        RequestedBytes = Math.Max(RequestedBytes, total);
        LargestIncrementBytes = Math.Max(LargestIncrementBytes, increment);
        if (refused) RefusedBytes = Math.Max(RefusedBytes, total);
        Requests++;
    }
}

/// <summary>Root and window interval evidence, never a prediction or an additional reservation.</summary>
public sealed class MapPreparationStageCounts
{
    readonly List<MapPreparationDemandCounts> _windows = new();
    readonly IReadOnlyList<MapPreparationDemandCounts> _view;
    public MapPreparationStageCounts() => _view = _windows.AsReadOnly();
    public MapPreparationDemandCounts Root { get; } = new();
    public IReadOnlyList<MapPreparationDemandCounts> Windows => _view;
    /// <summary>The observed window with the least headroom at its largest request or admitted total.</summary>
    public MapPreparationDemandCounts? BindingWindow
    {
        get
        {
            MapPreparationDemandCounts? binding = null;
            decimal headroom = decimal.MaxValue;
            foreach (var window in _windows)
            {
                decimal remaining = window.AllowanceBytes - Math.Max(window.RequestedBytes, window.AdmittedPeakBytes);
                if (remaining <= headroom) { binding = window; headroom = remaining; }
            }
            return binding;
        }
    }
    internal MapPreparationDemandCounts Window(long ordinal)
    {
        foreach (var window in _windows) if (window.WindowOrdinal == ordinal) return window;
        var result = new MapPreparationDemandCounts { WindowOrdinal = ordinal };
        _windows.Add(result);
        return result;
    }
}
