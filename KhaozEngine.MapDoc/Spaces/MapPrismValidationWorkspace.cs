using System;
using System.Collections.Generic;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;

namespace KhaozEngine.MapDoc.Spaces;

/// <summary>Validation-only ownership. Returned raw pieces and contexts outlive individual refinement kernels.</summary>
internal sealed class MapPrismValidationWorkspace : IDisposable
{
    [ThreadStatic] static MapPrismValidationWorkspace? t_current;
    readonly MapPrismValidationWorkspace? _previous;
    readonly MapSceneAdmission _admission;
    readonly MapPreparationScope _scope;
    readonly MapRoofPreparationCounts _counts;
    internal static MapSceneAdmission? Admission => t_current?._admission;
    internal MapPrismValidationWorkspace(MapSceneAdmission admission, MapRoofPreparationCounts counts, int records)
    {
        _admission = admission; _counts = counts; _previous = t_current;
        _scope = admission.BeginPreparation();
        try { _scope.Reserve(checked(32768L + 256L * records)); }
        catch { _scope.Dispose(); throw; }
        t_current = this;
    }
    internal static void Reserve(long bytes)
    {
        if (t_current is not { } current) return;
        current._scope.Reserve(bytes);
        current.Observe();
    }
    internal static MapPreparationReservation? Kernel(long vertices, long rows = 0)
    {
        var owner = t_current?._admission.Workspace();
        try { owner?.Reserve(checked(4096L + 4096L * vertices + 256L * rows)); }
        catch { owner?.Dispose(); throw; }
        t_current?.Observe();
        return owner;
    }
    internal static MapPreparationReservation? FootprintCell(MapScopedSurfaces view, MapSpaceFootprint footprint, int cell)
    {
        if (t_current is null) return null;
        var owner = Kernel(8);
        try
        {
            var extent = MapEffectiveGeometry.Bounds(MapEffectiveGeometry.CellPolygon(view, footprint.Lattice, cell));
            Bound(footprint.Lower); Bound(footprint.Upper);
            return owner;
            void Bound(MapBoundRef bound)
            {
                string? id = bound.SurfaceId;
                if (bound.Opening is { } reference && view.TryRecord(reference, out var record, out _, borrow: true) && record is MapHorizontalOpening opening)
                    id = opening.Patch.SurfaceId;
                if (bound.Kind == MapBoundKind.OpenTop || id is null || MapMembershipPreflight.FindSurface(view, id) is null) return;
                var range = MapEffectiveGeometry.CellRange(view, id, new(extent.MinX, extent.MinZ), new(extent.MaxX, extent.MaxZ));
                owner?.Reserve(checked(256L * (range.MaxXExclusive - range.MinX) * (range.MaxZExclusive - range.MinZ)));
            }
        }
        catch { owner?.Dispose(); throw; }
    }
    internal static void RawPiece(int vertices)
    {
        Reserve(checked(1024L + 256L * vertices));
        if (t_current is not { } current) return;
        current._counts.ValidationRawPieces++;
        current._counts.ValidationVertices += vertices;
    }
    internal static void Candidates(long count, bool uncovered = false)
    {
        if (t_current is not { } current) return;
        if (uncovered) current._counts.PeakValidationUncovered = Math.Max(current._counts.PeakValidationUncovered, count);
        else current._counts.PeakValidationCandidates = Math.Max(current._counts.PeakValidationCandidates, count);
        current.Observe();
    }
    void Observe() => _counts.ValidationPeakBytes = Math.Max(_counts.ValidationPeakBytes, _admission.WorkspaceBytes + _admission.RetainedBytes);
    public void Dispose() { Observe(); _scope.Dispose(); t_current = _previous; }
}
