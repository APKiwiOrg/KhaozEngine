using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace KhaozEngine.Render3D;

/// <summary>Owned final surfaces. Begin discards uncommitted writes. Only a completed scene encoding commits.</summary>
internal sealed class CoverageMotionHistory
{
    internal readonly record struct Surface(MotionKey Actor, ulong Revision, int Start, int Count,
        Matrix4x4 Model, MeshBounds Bounds);

    sealed class Generation
    {
        internal Vector3[] Positions = [];
        internal Surface[] Surfaces = [];
        internal int Count, Used;
        internal void Reserve(int actors, int vertices)
        {
            if (Surfaces.Length < actors) Array.Resize(ref Surfaces, actors);
            if (Positions.Length < vertices) Array.Resize(ref Positions, vertices);
        }
        internal void Clear() { Count = Used = 0; }
    }

    Generation previous = new(), pending = new();
    long committedFrame = -1, frame;
    object? committedView, view;
    bool valid, committed;
    internal ReadOnlySpan<Surface> PreviousSurfaces => previous.Surfaces.AsSpan(0, previous.Count);
    internal ReadOnlySpan<Surface> PendingSurfaces => pending.Surfaces.AsSpan(0, pending.Count);

    internal void Reserve(int actors, int vertices)
    {
        previous.Reserve(actors, vertices);
        pending.Reserve(actors, vertices);
    }

    internal void Begin(long frameIndex, object viewIdentity, bool compatible)
    {
        frame = frameIndex; view = viewIdentity;
        valid = compatible && committedFrame == frameIndex - 1 && ReferenceEquals(viewIdentity, committedView);
        pending.Clear();
        committed = false;
    }

    internal void DiscardPending()
    {
        pending.Clear();
        valid = false;
    }

    internal bool TryGetPrevious(MotionKey actorKey, ulong revision, out CoveragePreviousSurface surface)
    {
        foreach (ref readonly var entry in PreviousSurfaces)
            if (valid && !actorKey.IsNone && entry.Actor == actorKey && entry.Revision == revision)
            {
                surface = new(previous.Positions.AsSpan(entry.Start, entry.Count), entry.Model);
                return true;
            }
        surface = default;
        return false;
    }

    internal void Record(MotionKey actorKey, ulong revision, ReadOnlySpan<Vector3> finalPositions,
        in Matrix4x4 absoluteModel)
    {
        int start = Start(actorKey, finalPositions.Length);
        finalPositions.CopyTo(pending.Positions.AsSpan(start));
        Finish(actorKey, revision, start, finalPositions.Length, absoluteModel);
    }

    internal void RecordVertices(MotionKey actorKey, ulong revision, ReadOnlySpan<ModelVertex> finalVertices,
        in Matrix4x4 absoluteModel, double worldError = 0)
    {
        int start = Start(actorKey, finalVertices.Length);
        for (int i = 0; i < finalVertices.Length; i++) pending.Positions[start + i] = finalVertices[i].Position;
        Finish(actorKey, revision, start, finalVertices.Length, absoluteModel, worldError);
    }

    int Start(MotionKey actor, int count)
    {
        if (committed || actor.IsNone || count < 1 || count > HostCoverageMap.MaxSurfaceVertices
            || pending.Count == pending.Surfaces.Length || count > pending.Positions.Length - pending.Used)
            throw new InvalidOperationException("Coverage history requires pre-record capacity and one live actor surface.");
        foreach (ref readonly var entry in PendingSurfaces)
            if (entry.Actor == actor) throw new InvalidOperationException("Coverage motion keys must be unique within a view.");
        return pending.Used;
    }

    void Finish(MotionKey actor, ulong revision, int start, int count, Matrix4x4 model, double worldError = 0)
    {
        Vector3 min = new(float.PositiveInfinity), max = new(float.NegativeInfinity);
        for (int i = 0; i < count; i++)
        {
            Vector3 p = Vector3.Transform(pending.Positions[start + i], model);
            min = Vector3.Min(min, p); max = Vector3.Max(max, p);
        }
        Vector3 error = new((float)worldError);
        min -= error; max += error;
        for (int axis = 0; axis < 3; axis++)
        { min[axis] = MathF.BitDecrement(min[axis]); max[axis] = MathF.BitIncrement(max[axis]); }
        pending.Surfaces[pending.Count++] = new(actor, revision, start, count, model, new(min, max));
        pending.Used += count;
    }

    internal void Commit()
    {
        if (committed) throw new InvalidOperationException("Coverage history commits once per rendered view.");
        (previous, pending) = (pending, previous);
        committedFrame = frame; committedView = view;
        committed = true;
    }

    internal void Reset()
    {
        previous.Clear(); pending.Clear();
        committedFrame = -1; committedView = null; valid = false;
    }
}

/// <summary>Borrowed owned storage, valid until the next history commit or reset.</summary>
internal readonly ref struct CoveragePreviousSurface(ReadOnlySpan<Vector3> positions, Matrix4x4 absoluteModel)
{
    internal ReadOnlySpan<Vector3> Positions { get; } = positions;
    internal Matrix4x4 AbsoluteModel { get; } = absoluteModel;

    internal bool TryRebase(Vector3 origin, in Matrix4x4 previousViewProjection, out Matrix4x4 renderModel)
    {
        renderModel = AbsoluteModel;
        renderModel.Translation -= origin;
        if (!CoverageAffine.TryFrom(renderModel, out var affine) || !(affine.Determinant > 0)
            || !affine.TryInverse(out var inverse) || affine.SpectralBound * inverse.SpectralBound > 1e6
            || !Matrix4x4.Invert(previousViewProjection, out var inverseView)) return false;
        foreach (float value in MemoryMarshal.Cast<Matrix4x4, float>(MemoryMarshal.CreateReadOnlySpan(in previousViewProjection, 1)))
            if (!float.IsFinite(value)) return false;
        foreach (float value in MemoryMarshal.Cast<Matrix4x4, float>(MemoryMarshal.CreateReadOnlySpan(in inverseView, 1)))
            if (!float.IsFinite(value)) return false;
        foreach (Vector3 position in Positions)
        {
            Vector3 render = Vector3.Transform(position, renderModel);
            Vector4 clip = Vector4.Transform(new Vector4(render, 1), previousViewProjection);
            if (!float.IsFinite(render.X) || !float.IsFinite(render.Y) || !float.IsFinite(render.Z)
                || !float.IsFinite(clip.X) || !float.IsFinite(clip.Y) || !float.IsFinite(clip.Z) || !float.IsFinite(clip.W)) return false;
        }
        return true;
    }
}
