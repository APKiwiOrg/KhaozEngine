using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace KhaozEngine.Render3D;

/// <summary>CPU identity and conservative bounds only. Submission owns the commit boundary.</summary>
internal sealed class CoverageMotionHistory
{
    internal readonly record struct Surface(MotionKey Actor, ulong Revision, CoverageGpuReservation Reservation,
        Matrix4x4 Model, CoverageSurfaceBounds Bounds, bool Presented)
    {
        internal bool TryRebase(Vector3 origin, in Matrix4x4 previousViewProjection, out Matrix4x4 renderModel)
        {
            renderModel = Model;
            renderModel.Translation -= origin;
            if (!CoverageAffine.TryFrom(renderModel, out var affine) || !(affine.Determinant > 0)
                || !affine.TryInverse(out var inverse) || affine.SpectralBound * inverse.SpectralBound > 1e6
                || !Matrix4x4.Invert(previousViewProjection, out var inverseView)
                || !Finite(previousViewProjection) || !Finite(inverseView) || !Bounds.Weight.IsFinite) return false;
            var bounds = Bounds;
            if (bounds.Weight.Min < -float.MaxValue || bounds.Weight.Max > float.MaxValue) return false;
            var position = new CoveragePositionBounds(
                RebaseAxis(bounds.Relative.Min.X, bounds.Relative.Max.X, bounds.Origin.X, origin.X, bounds.Weight),
                RebaseAxis(bounds.Relative.Min.Y, bounds.Relative.Max.Y, bounds.Origin.Y, origin.Y, bounds.Weight),
                RebaseAxis(bounds.Relative.Min.Z, bounds.Relative.Max.Z, bounds.Origin.Z, origin.Z, bounds.Weight), bounds.Weight);
            // The shared dot bound also refuses overflowing binary32 products and partial sums before cancellation.
            return CoverageShaderPosition.Transform(previousViewProjection, position).IsFinite;
        }

        static CoverageScalarBounds RebaseAxis(float min, float max, float anchor, float origin, CoverageScalarBounds weight) =>
            CoverageIntervals.Add(new(min, max), CoverageIntervals.Multiply(
                CoverageIntervals.Subtract(new(anchor, anchor), new(origin, origin)), weight));
    }

    Surface[] previous = [], pending = [];
    int previousCount, pendingCount;
    long committedFrame = -1, frame;
    object? committedView, view;
    bool valid, committed;
    internal ReadOnlySpan<Surface> PreviousSurfaces => previous.AsSpan(0, previousCount);
    internal ReadOnlySpan<Surface> PendingSurfaces => pending.AsSpan(0, pendingCount);

    internal void Reserve(int actors)
    {
        if (previous.Length < actors) Array.Resize(ref previous, actors);
        if (pending.Length < actors) Array.Resize(ref pending, actors);
    }

    internal void Begin(long frameIndex, object viewIdentity, bool compatible)
    {
        frame = frameIndex; view = viewIdentity;
        valid = compatible && committedFrame == frameIndex - 1 && ReferenceEquals(viewIdentity, committedView);
        pendingCount = 0;
        committed = false;
    }

    internal void DiscardPending() { pendingCount = 0; valid = false; }

    internal bool TryGetPrevious(MotionKey actor, ulong revision, out Surface surface)
    {
        foreach (ref readonly var entry in PreviousSurfaces)
            if (valid && !actor.IsNone && entry.Actor == actor && entry.Revision == revision && entry.Presented)
            { surface = entry; return true; }
        surface = default;
        return false;
    }

    internal void Record(in Surface surface)
    {
        if (committed || surface.Actor.IsNone || pendingCount == pending.Length)
            throw new InvalidOperationException("Coverage history requires pre-record capacity and one live actor surface.");
        foreach (ref readonly var entry in PendingSurfaces)
            if (entry.Actor == surface.Actor) throw new InvalidOperationException("Coverage motion keys must be unique within a view.");
        pending[pendingCount++] = surface;
    }

    internal void Commit()
    {
        if (committed) throw new InvalidOperationException("Coverage history commits once per rendered view.");
        (previous, pending) = (pending, previous);
        previousCount = pendingCount; pendingCount = 0;
        committedFrame = frame; committedView = view;
        committed = true;
    }

    internal void Reset()
    {
        previousCount = pendingCount = 0;
        committedFrame = -1; committedView = null; valid = false;
    }

    static bool Finite(Matrix4x4 matrix)
    {
        foreach (float value in MemoryMarshal.Cast<Matrix4x4, float>(MemoryMarshal.CreateReadOnlySpan(in matrix, 1)))
            if (!float.IsFinite(value)) return false;
        return true;
    }
}
