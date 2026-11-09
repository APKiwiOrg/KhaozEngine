using System;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Water;

namespace KhaozEngine.MapDoc.Surfaces;

/// <summary>One reusable owner for refinement kernels. Published polygons move into their enclosing product.</summary>
internal sealed class MapPaintPreparation : IDisposable
{
    readonly MapPreparationReservation? _workspace = MapSceneAdmission.Current?.Workspace();
    long _source, _face;
    internal void BeginSource(long faces)
    {
        // Three edges per source face bound the boundary maps, endpoint indexes and simultaneous slab arrays.
        _source = checked(512L + 4096L * faces);
        _workspace?.Reserve(_source);
    }
    internal void EndSource() { _workspace?.Release(_source); _source = 0; }
    internal void BeforeSourcePolygon() => _workspace?.Reserve(384);
    internal void DiscardSourcePolygon() => _workspace?.Release(384);
    internal void PublishSource(int vertices)
    {
        if (_workspace is null) return;
        long bytes = checked(128L + 64L * vertices);
        MapSceneAdmission.Current!.PreparationScope!.AdoptWorkspace(_workspace, bytes);
        _workspace.Release(384 - bytes);
    }
    internal void BeginFace(int candidates, long edges, MapRefinementLimits limits)
    {
        _face = WorkspaceForFace(candidates, edges, limits);
        _workspace?.Reserve(_face);
    }
    internal static long WorkspaceForFace(int candidates, long edges, MapRefinementLimits? limits = null)
    {
        // One candidate has no conflict or subtraction phase. Multiple convex candidates form a planar
        // arrangement of these edge lines. Four simultaneous lists cover candidates, accepted, old and new pieces.
        decimal e = edges;
        decimal cells = e * e + e + 1, vertices = 4 * e * e + 4 * e + 3;
        if (limits is not null)
        {
            cells = Math.Min((decimal)limits.MaxRefinementFaces + 1, cells);
            vertices = Math.Min((decimal)limits.MaxRefinementVertices + e + 3, vertices);
        }
        decimal bytes = candidates <= 1 ? 512 + 1024 * e : 512 + 4 * (128 * cells + 64 * vertices) + 256 * e;
        if (bytes > long.MaxValue && Scene.MapSceneAdmission.Current is { } admission)
            throw new MapSceneCapacityException(admission.PreparedByteCapacity(bytes));
        return checked((long)bytes);
    }
    internal void EndFace() { _workspace?.Release(_face); _face = 0; }
    internal void BeforeEmit(int vertices)
    {
        _workspace?.Reserve(checked(48L * vertices));
        MapSceneAdmission.Current?.ReserveBytes(checked(128L + 48L * vertices));
    }
    internal void AfterEmit(int vertices) => _workspace?.Release(checked(48L * vertices));
    public void Dispose() => _workspace?.Dispose();
}
