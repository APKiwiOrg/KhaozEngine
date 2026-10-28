using System.Collections.Generic;
using System.Runtime.CompilerServices;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.MapDoc.Scene;

internal sealed partial class MapPreparationLedger
{
    internal void ReportRetained() => CapacityDemandDiagnostics.Check(this, "MapPreparationLedger.FinalRetained",
        limitName, RetainedBytes, 0, allowance, false, CapacityDemandLifetime.FinalInventory, allowanceSource: source);

    /// <summary>The originating fields of <see cref="Remaining"/>: the root allowance's origins and every open window.</summary>
    internal CapacityDemandOrigin[] RemainingOrigins()
    {
        var origins = new List<CapacityDemandOrigin>(CapacityDemandDiagnostics.Origins(source, limitName, allowance, LiveBytes));
        for (var window = _window; window is not null; window = window.Parent)
            origins.Add(new(window.LimitName, window.Total, window.Allowance, window));
        return origins.ToArray();
    }

    internal bool CheckDemand(decimal bytes, bool refused, [CallerMemberName] string site = "")
    {
        ObserveDemand(bytes, bytes, _window, null, null, site);
        return refused;
    }

    void ObserveDemand(decimal rootIncrement, decimal windowIncrement, Window? start, Window? stop,
        ICollection<object>? covered, string site)
    {
        if (!CapacityDemandDiagnostics.IsObserved) return;
        try
        {
            CapacityDemandDiagnostics.Check(this, "MapPreparationLedger." + site, limitName, LiveBytes,
                rootIncrement, allowance, rootIncrement < 0 || rootIncrement > allowance - LiveBytes,
                CapacityDemandLifetime.Transient, allowanceSource: source);
            for (var window = _window; window is not null; window = window.Parent)
            {
                decimal increment = 0;
                for (var charged = start; charged != stop; charged = charged!.Parent)
                    if (charged == window && covered?.Contains(window) != true) { increment = windowIncrement; break; }
                CapacityDemandDiagnostics.Check(this, "MapPreparationLedger.Window." + site, window.LimitName,
                    window.Total, increment, window.Allowance, increment < 0 || increment > window.Remaining,
                    CapacityDemandLifetime.Transient, window);
            }
        }
        catch { /* Observation cannot interrupt the ledger. */ }
    }
}
