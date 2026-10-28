using DemandDiagnostics = KhaozEngine.Diagnostics.CapacityDemandDiagnostics;
using KhaozEngine.Diagnostics;
using System;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Support;

/// <summary>A bounded vertical-column query over canonical support geometry and acquired space records. Selection after
/// acquisition is the shared <see cref="MapPreparedSupport"/> kernel, so a point query and prepared support agree.</summary>
public sealed class MapSupportQuery
{
    readonly MapPreparedSupport _prepared;

    public MapSupportQuery(MapScopedSurfaces scoped)
    {
        ArgumentNullException.ThrowIfNull(scoped);
        _prepared = new(scoped);
    }

    public MapSupportResult Select(MapSupportRequest request) => MapSupportSelection.Compute(_prepared, request, null, false).Result;
    internal MapSupportResult Select(MapSupportRequest request, MapBoundFaceWork work) => MapSupportSelection.Compute(_prepared, request, work, false).Result;

    /// <summary>Samples the named surface owner directly, without inventing a reference height or search interval.</summary>
    public MapSupportResult SampleSurface(MapFramePoint point, string surfaceId) =>
        MapSupportSelection.Compute(_prepared, new(point, surfaceId, null, null, 0, 0, null), null, true).Result;

    /// <summary>Samples the named surface owner at an exact world point.</summary>
    public MapSupportResult SampleSurfaceExact(MapExactPoint world, string surfaceId) => MapSupportSelection.Compute(_prepared,
        new(MapEffectiveGeometry.Presentation(_prepared.View, world), surfaceId, null, null, 0, 0, null), null, true, world).Result;
    internal MapSupportResult SelectExact(MapSupportRequest request, MapExactPoint world) =>
        MapSupportSelection.Compute(_prepared, request, null, false, world).Result;

    public MapSupportCandidateSet EnumerateCandidates(MapSupportRequest request, Span<MapSupportCandidate> buffer)
    {
        MapSupportSelection.Outcome outcome = MapSupportSelection.Compute(_prepared, request, null, false);
        int count = Math.Min(buffer.Length, outcome.Candidates.Count);
        for (int i = 0; i < count; i++)
        {
            MapSupportSelection.Candidate candidate = outcome.Candidates[i];
            buffer[i] = new(candidate.Face.Key, candidate.SpaceId!, candidate.Height.ToSingle(), candidate.Face.Normal, candidate.Via);
        }
        return new(DemandDiagnostics.Check(this, "MapSupportQuery.SupportCandidateBuffer.1", "SupportCandidateBuffer", 0, outcome.Candidates.Count, buffer.Length, outcome.Candidates.Count > buffer.Length, CapacityDemandLifetime.Inventory) ? MapSupportStatus.CapacityExceeded : outcome.Result.Status,
            count, outcome.Candidates.Count, _prepared.View.ReadWitness);
    }
}
