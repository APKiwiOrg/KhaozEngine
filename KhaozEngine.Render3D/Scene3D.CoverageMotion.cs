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
        int vertices = 0;
        foreach (var conformer in _coverageConformers) vertices = checked(vertices + conformer.Topology.VertexCount);
        _coverageMotion.Reserve(_coverageConformers.Count, vertices);
        if (_coverageConformers.Count + _coverageMotion.PreviousSurfaces.Length == 0) return;
        _coverageTransition ??= new(_gd);
        _coverageTransition.Reserve(checked(_coverageConformers.Count + _coverageMotion.PreviousSurfaces.Length));
    }

    void BeginCoverageMotion()
    {
        if (_coverageMotionFrame == CurrentFrameView.FrameIndex) return;
        _coverageMotionFrame = CurrentFrameView.FrameIndex;
        _coverageMotionCommitted = false;
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

    // Called after stream construction. Pending storage copies only the exact main-pass upload slices.
    void PrepareCoverageMotion()
    {
        if (_coverageMotionCommitted) return;
        foreach (var p in _coverageArena.Preparations)
        {
            p.MotionReusable = p.MotionPresented = false;
            if (p.Outcome.Presentation != CoveragePresentation.Covered || p.Owner.Disposed || !_frameHasDisplayArea) continue;
            bool presented = true;
            for (int slice = 0; slice < p.Owner.Meshes.Length; slice++)
            {
                bool found = false;
                foreach (var draw in _cpuSkinnedDraws)
                    found |= draw.MeshIndex < 0 && draw.VisibleMain && draw.Motion == p.Actor
                        && ReferenceEquals(draw.Ib, p.Owner.Indices[slice]) && draw.BaseVertex == p.BaseVertices[slice];
                presented &= found;
            }
            if (!presented)
            {
                if (VisibleCoverageBounds(p) is { } visible)
                    _coverageTransition?.AddChangedBounds(new(visible, PreviousCoverageBounds(p.Actor), false));
                continue;
            }
            p.MotionRevision = SameCoverageIdentity(p) ? p.Owner.MotionRevision : ++_coverageMotionRevision;
            p.MotionReusable = ValidCoveragePrevious(p);
            ReadOnlySpan<ModelVertex> uploaded = CollectionsMarshal.AsSpan(_cpuSkinnedVerts)
                .Slice(p.BaseVertices[0], p.Owner.Topology.VertexCount);
            _coverageMotion.RecordVertices(p.Actor, p.MotionRevision, uploaded, p.Model, p.Certificate.WorldErrorBound,
                CurrentFrameView.RenderOrigin);
            p.MotionPresented = true;
            if (!p.MotionReusable)
                _coverageTransition?.AddChangedBounds(new(_coverageMotion.PendingSurfaces[^1].Bounds,
                    PreviousCoverageBounds(p.Actor), false));
        }
        foreach (ref readonly var previous in _coverageMotion.PreviousSurfaces)
        {
            bool replaced = false;
            foreach (var p in _coverageArena.Preparations) replaced |= p.MotionPresented && p.Actor == previous.Actor;
            if (!replaced) _coverageTransition?.AddChangedBounds(new(UncoveredCoverageBounds(previous.Actor), previous.Bounds, false));
        }
    }

    bool ValidCoveragePrevious(CoveragePreparation p)
    {
        if (!MotionHistoryValid || !_coverageMotion.TryGetPrevious(p.Actor, p.MotionRevision, out var previous)
            || previous.Positions.Length != p.Owner.Topology.VertexCount) return false;
        return previous.TryRebase(CurrentFrameView.RenderOrigin, PreviousFrameView!.Value.ViewProjection, out _);
    }

    bool AppendCoveragePrevious(in CpuSkinnedDraw draw)
    {
        foreach (var p in _coverageArena.Preparations)
        {
            if (!p.MotionReusable || draw.Motion != p.Actor) continue;
            for (int slice = 0; slice < p.Owner.Meshes.Length; slice++)
            {
                if (draw.BaseVertex != p.BaseVertices[slice] || !ReferenceEquals(draw.Ib, p.Owner.Indices[slice])) continue;
                var range = p.Owner.Topology.MaterialRanges[slice];
                if (!_coverageMotion.TryGetPrevious(p.Actor, p.MotionRevision, out var previous)) return false;
                Matrix4x4 model = ToRender(previous.AbsoluteModel);
                foreach (Vector3 position in previous.Positions.Slice(range.FirstVertex, range.VertexCount))
                    _cpuSkinnedPrevious.Add(Vector3.Transform(position, model));
                return true;
            }
        }
        return false;
    }

    CoverageSurfaceBounds? VisibleCoverageBounds(CoveragePreparation p)
    {
        var bounds = new CoverageSurfaceBoundsBuilder(_frameOrigin);
        Matrix4x4 renderModel = ToRender(p.Model);
        foreach (var draw in _cpuSkinnedDraws)
        {
            if (draw.MeshIndex >= 0 || !draw.VisibleMain || draw.Motion != p.Actor) continue;
            foreach (var indices in p.Owner.Indices)
            {
                if (!ReferenceEquals(indices, draw.Ib)) continue;
                foreach (var vertex in CollectionsMarshal.AsSpan(_cpuSkinnedVerts).Slice(draw.BaseVertex, draw.VertexCount))
                    bounds.Add(CoverageShaderPosition.Transform(renderModel, CoveragePositionBounds.From(vertex.Position)));
            }
        }
        return bounds.Build(p.Certificate.WorldErrorBound);
    }

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
                || _skinnedCpuVerts[item.Mesh.Index] is not { } source) continue;
            if (TryFrozenCoverage(item.Coverage, out var preparation)
                && preparation.Outcome.Presentation == CoveragePresentation.Covered) continue;
            var bones = CollectionsMarshal.AsSpan(_boneMatrices).Slice(i * SkinningMath.MaxBonesPerDraw, SkinningMath.MaxBonesPerDraw);
            Matrix4x4 renderModel = ToRender(item.World);
            foreach (var vertex in source)
                bounds.Add(UseGpuSkinning ? CoverageShaderPosition.Skinned(vertex, bones, renderModel)
                    : CoverageShaderPosition.Transform(renderModel,
                        CoveragePositionBounds.From(SkinningMath.SkinVertex(vertex, bones).Position)));
        }
        return bounds.Build();
    }

    // This observes successful scene command encoding. The caller still owns End, Submit and Present.
    void CompleteCoverageEncoding()
    {
        if (_coverageMotionCommitted || _coverageMotionFrame != CurrentFrameView.FrameIndex) return;
        foreach (var p in _coverageArena.Preparations)
        {
            if (!p.MotionPresented) continue;
            p.Owner.MotionRevision = p.MotionRevision;
            p.Owner.MotionCommandCount = p.CommandCount;
            p.Commands.AsSpan(0, p.CommandCount).CopyTo(p.Owner.MotionCommands);
        }
        _coverageMotion.Commit();
        _coverageMotionCommitted = true;
    }

    static bool Finite(Matrix4x4 matrix)
    {
        foreach (float value in MemoryMarshal.Cast<Matrix4x4, float>(MemoryMarshal.CreateReadOnlySpan(in matrix, 1)))
            if (!float.IsFinite(value)) return false;
        return true;
    }
}
