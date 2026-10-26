using System;
using KhaozEngine.MapDoc.Scene;

namespace KhaozEngine.MapDoc.Interiors;

/// <summary>Membership evidence starts before admission and closes after publication or unwind.</summary>
internal sealed class MapMembershipPreparationInterval : IDisposable
{
    readonly MapSceneAdmission _admission;
    readonly MapPreparationLedger.PeakObservation _membership;
    MapPreparationLedger.PeakObservation? _stage;
    internal MapMembershipPreparationInterval(MapSceneAdmission admission, MapRoofPreparationCounts counts)
    { _admission = admission; _membership = admission.ObservePeak(counts.Membership); }
    internal void Stage(MapPreparationStageCounts counts)
    { _stage?.Dispose(); _stage = _admission.ObservePeak(counts); }
    public void Dispose() { _stage?.Dispose(); _membership.Dispose(); }
}
