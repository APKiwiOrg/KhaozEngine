using System;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Render3D;

public sealed partial class Scene3D
{
    readonly CoverageMotionHistory _coverageMotion = new();
    CoverageTransitionMask? _coverageTransition;
    ulong _coverageMotionRevision;
    long _coverageMotionFrame = -1;
    bool _coverageMotionCommitted;

    void ReserveCoverageMotion()
    {
        _coverageMotion.Reserve(_coverageConformers.Count);
        _coverageSubmissionState.Scene = this;
        if (_coverageConformers.Count + _coverageMotion.PreviousSurfaces.Length == 0) return;
        _coverageTransition ??= new(_gd);
        _coverageTransition.Reserve(checked(_coverageConformers.Count + _coverageMotion.PreviousSurfaces.Length));
    }

    void BeginCoverageMotion()
    {
        if (_coverageMotionFrame == CurrentFrameView.FrameIndex) return;
        _coverageMotionFrame = CurrentFrameView.FrameIndex;
        _coverageMotionCommitted = false;
        _coverageEncodingComplete = false;
        _coverageMotion.Begin(_coverageMotionFrame, ActiveCamera,
            PreviousFrameView is { } prior && prior.FrameIndex == _coverageMotionFrame - 1
            && Finite(prior.ViewProjection) && Finite(CurrentFrameView.ViewProjection));
        _coverageTransition?.Clear();
    }

    static bool SameCoverageIdentity(CoveragePreparation p)
    {
        var owner = p.Owner;
        if (owner.MotionRevision == 0 || owner.MotionCommandCount != p.CommandCount) return false;
        for (int c = 0; c < p.CommandCount; c++)
        {
            var now = p.Commands[c];
            bool found = false;
            for (int old = 0; old < owner.MotionCommandCount; old++)
            {
                var last = owner.MotionCommands[old];
                found |= now.Id == last.Id && now.EquipmentInstanceId == last.EquipmentInstanceId
                    && now.RegionIndex == last.RegionIndex && ReferenceEquals(now.Support, last.Support)
                    && ReferenceEquals(now.Topology, last.Topology);
            }
            if (!found) return false;
        }
        return true;
    }

    // The GPU decision controls correspondence. CPU metadata only admits complete main participation.
    void PrepareCoverageMotion()
    {
        if (_coverageMotionCommitted) return;
        foreach (var p in _coverageArena.Preparations)
        {
            p.MotionPresented = p.MotionEligible = false;
            if (p.Outcome.Presentation != CoveragePresentation.Dispatched) continue;
            bool presented = _frameHasDisplayArea;
            for (int slice = 0; slice < p.Draws.Length; slice++)
            {
                bool found = false;
                if (p.Draws[slice] is not { } submitted || !CoverageHasMainFragments(submitted.Dissolve, submitted.DissolveComplement))
                { presented = false; continue; }
                var range = p.Owner.Topology.MaterialRanges[slice];
                foreach (var draw in _coverageDraws)
                    found |= draw.Actor == p.Actor && draw.FirstCorner == range.FirstIndex && draw.VisibleMain;
                presented &= found;
            }
            p.MotionEligible = presented;
            var bounds = p.WorldEnvelope;
            if (presented)
                _coverageTransition?.AddGpuChangedBounds(new(bounds, PreviousCoverageBounds(p.Actor), false),
                    _coverageGpu!.Batch!.Current, p.GpuReservation.CurrentBase + 15);
            else if (VisibleCoverageBounds(p))
                _coverageTransition?.AddChangedBounds(new(bounds, PreviousCoverageBounds(p.Actor), false));
        }
        foreach (ref readonly var previous in _coverageMotion.PreviousSurfaces)
        {
            bool replaced = false;
            foreach (var p in _coverageArena.Preparations) replaced |= p.MotionEligible && p.Actor == previous.Actor;
            if (!replaced) _coverageTransition?.AddChangedBounds(new(UncoveredCoverageBounds(previous.Actor), previous.Bounds, false));
        }
    }

    bool VisibleCoverageBounds(CoveragePreparation p)
    {
        foreach (var draw in _coverageDraws)
            if (draw.Actor == p.Actor && draw.VisibleMain) return true;
        return false;
    }

    void StageCoverageHistory(CoveragePreparation p, KhaozEngine.Gpu.IGpuCommandList commands, Span<uint> frame)
    {
        p.MotionPresented = p.MotionReusable = false;
        bool main = _frameHasDisplayArea;
        foreach (var submitted in p.Draws)
            main &= submitted is { } draw && !draw.ShadowOnly && CoverageHasMainFragments(draw.Dissolve, draw.DissolveComplement);
        if (FrustumCulling)
        {
            var absoluteVp = Matrix4x4.CreateTranslation(-CurrentFrameView.RenderOrigin) * CurrentFrameView.ViewProjection;
            main &= FrustumPlanes.Extract(absoluteVp).IntersectsSphere(p.Sphere.Center, p.Sphere.Radius);
        }
        frame[13] = main ? 2u : 0u;
        p.MotionRevision = SameCoverageIdentity(p) ? p.Owner.MotionRevision : ++_coverageMotionRevision;
        frame[14] = (uint)p.MotionRevision; frame[15] = (uint)(p.MotionRevision >> 32);
        if (!main || !MotionHistoryValid || !_coverageMotion.TryGetPrevious(p.Actor, p.MotionRevision, out var previous)
            || previous.Reservation.SourceCount != p.GpuReservation.SourceCount
            || previous.Reservation.VertexCount != p.GpuReservation.VertexCount
            || !previous.TryRebase(CurrentFrameView.RenderOrigin, PreviousFrameView!.Value.ViewProjection, out _)
            || p.Owner.Gpu.HistoryBatch is null) return;
        p.Owner.Gpu.CopyHistory(commands, _coverageGpu!.Batch!, p.GpuReservation);
        p.MotionReusable = true;
        frame[13] |= 1;
    }

    Matrix4x4 CoveragePreviousModel(MotionKey actor, Matrix4x4 current)
    {
        foreach (var p in _coverageArena.Preparations)
            if (p.Actor == actor && p.MotionReusable && _coverageMotion.TryGetPrevious(actor, p.MotionRevision, out var previous))
                return ToRender(previous.Model);
        return current;
    }

    // A submitted draw can encode successfully while its colour shader discards every fragment.
    static bool CoverageHasMainFragments(float dissolve, float complement) => complement > .5f ? dissolve > 0 : dissolve < 1;

    CoverageSurfaceBounds? PreviousCoverageBounds(MotionKey actor)
    {
        foreach (ref readonly var surface in _coverageMotion.PreviousSurfaces)
            if (surface.Actor == actor) return surface.Bounds;
        return null;
    }

    // A restored ordinary surface has no correspondence with last frame's refined final vertices.
    bool RestoringCoverageSurface(MotionKey actor) => !actor.IsNone && PreviousCoverageBounds(actor) is not null;

    CoverageSurfaceBounds? UncoveredCoverageBounds(MotionKey actor)
    {
        var bounds = new CoverageSurfaceBoundsBuilder(_frameOrigin);
        var items = _skinnedInstances.Items;
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item.Motion != actor || item.ShadowOnly || !_skinnedSlots.IsValid(item.Mesh.Index, item.Mesh.Generation)
                || !CoverageHasMainFragments(item.DissolveThreshold, item.DissolveComplement)) continue;
            if (TryFrozenCoverage(item.Coverage, out var preparation)
                && preparation.Outcome.Presentation == CoveragePresentation.Dispatched) continue;
            if (!_coveragePaletteBounds.TryGetValue((item.Mesh.Index, item.Mesh.Generation), out var envelope))
                return CoverageSurfaceBounds.FromHomogeneousRender(default, _frameOrigin, CoverageIntervals.Invalid);
            int count = _skinnedMeshes[item.Mesh.Index]!.InverseBind.Length;
            var bones = CollectionsMarshal.AsSpan(_boneMatrices).Slice(i * SkinningMath.MaxBonesPerDraw, count);
            Matrix4x4 renderModel = ToRender(item.World);
            bounds.Add(envelope.BoundOrdinary(bones, renderModel,
                UseCoverageVertexSkinning(item.Mesh, item.Coverage.Preparation is not null)));
        }
        return bounds.Build();
    }

    // Encoding only marks readiness. The stable submission participant publishes history.
    void CompleteCoverageEncoding()
    {
        foreach (var p in _coverageArena.Preparations)
        {
            if (!p.MotionEligible) continue;
            _coverageMotion.Record(new(p.Actor, p.MotionRevision, p.GpuReservation, p.Model, p.WorldEnvelope, true));
            p.MotionPresented = true;
        }
        _coverageEncodingComplete = true;
    }

    bool _coverageEncodingComplete;

    internal void AbandonCoverageMotion()
    {
        _coverageEncodingComplete = false;
        _coverageMotion.DiscardPending();
        foreach (var p in _coverageArena.Preparations) p.MotionPresented = false;
    }

    internal void CommitCoverageMotion()
    {
        if (!_coverageEncodingComplete || _coverageMotionCommitted || _coverageMotionFrame != CurrentFrameView.FrameIndex) return;
        foreach (var p in _coverageArena.Preparations)
        {
            if (!p.MotionPresented) continue;
            p.Owner.Gpu.CommitHistory(_coverageGpu!.Batch!, p.GpuReservation);
            p.Owner.MotionRevision = p.MotionRevision;
            p.Owner.MotionCommandCount = p.CommandCount;
            p.Commands.AsSpan(0, p.CommandCount).CopyTo(p.Owner.MotionCommands);
        }
        foreach (var owner in _coverageConformers)
            if (owner.Gpu.HistoryBatch is not null && !ReferenceEquals(owner.Gpu.HistoryBatch, _coverageGpu?.Batch))
                owner.Gpu.ReleaseHistory();
        _coverageGpu?.Batch?.CommitFinals();
        _coverageMotion.Commit();
        _coverageEncodingComplete = false;
        _coverageMotionCommitted = true;
    }

    static bool Finite(Matrix4x4 matrix)
    {
        foreach (float value in MemoryMarshal.Cast<Matrix4x4, float>(MemoryMarshal.CreateReadOnlySpan(in matrix, 1)))
            if (!float.IsFinite(value)) return false;
        return true;
    }
}
