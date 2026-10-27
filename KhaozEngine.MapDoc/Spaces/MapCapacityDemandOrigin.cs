using KhaozEngine.Diagnostics;

namespace KhaozEngine.MapDoc.Spaces;

/// <summary>The original profile behind a remaining allowance passed into a shared context gate.</summary>
internal readonly record struct MapCapacityDemandOrigin(string LimitName, decimal Consumed, decimal Allowance, object? Identity = null)
{
    internal void Report(object admission, string site, decimal increment, CapacityDemandLifetime lifetime)
    {
        if (!CapacityDemandDiagnostics.IsObserved) return;
        CapacityDemandDiagnostics.Check(admission, site, LimitName, Consumed, increment, Allowance,
            increment > Allowance - Consumed, lifetime, Identity);
    }
}
