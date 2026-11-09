using System;
using System.Runtime.CompilerServices;
using KhaozEngine.MapDoc.Scene;

namespace KhaozEngine.MapDoc.Interiors;

/// <summary>Membership evidence starts before admission and closes after publication or unwind.</summary>
internal sealed class MapMembershipPreparationInterval : IDisposable
{
    readonly MapSceneAdmission _admission;
    readonly MapPreparationLedger.PeakObservation _membership;
    MapPreparationLedger.PeakObservation? _stage;
    // Kept out of line so the interval is a heap object at every JIT tier. A refusal's allocation is then the same
    // on every run, whichever tier compiled it.
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal MapMembershipPreparationInterval(MapSceneAdmission admission, MapRoofPreparationCounts counts)
    { _admission = admission; _membership = admission.ObservePeak(counts.Membership); }
    internal void Stage(MapPreparationStageCounts counts)
    { _stage?.Dispose(); _stage = _admission.ObservePeak(counts); }
    public void Dispose() { _stage?.Dispose(); _membership.Dispose(); }
}
