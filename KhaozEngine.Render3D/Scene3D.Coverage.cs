using System;
using System.Numerics;

namespace KhaozEngine.Render3D;

public sealed partial class Scene3D
{
    /// <summary>CPU-admitted complete hosts whose GPU coverage commands were encoded in the last view.</summary>
    public int DispatchedHostPreparationCount { get; private set; }
    internal int UncoveredGpuDrawsForTests => _gpuSkinnedDraws.Count;

    internal int CoverageSphereEvaluationsForTests { get; private set; }
    internal (int List, uint Gpu, KhaozEngine.Gpu.IGpuBuffer? Buffer) CoverageMotionReserveForTests =>
        (_cpuSkinnedPrevious.Capacity, _model.CpuPreviousReservationForTests.Capacity, _model.CpuPreviousReservationForTests.Buffer);
    internal CoverageFrameOutcome CoverageOutcomeForTests(SkinnedCoverageFrame frame) =>
        TryFrozenCoverage(frame, out var p) ? p.Outcome : throw new InvalidOperationException("Coverage token is not owned.");

    internal (KhaozEngine.Gpu.IGpuBuffer Vertices, KhaozEngine.Gpu.IGpuBuffer Indices)
        CoverageFallbackSliceForTests(SkinnedCoverageFrame frame, int slice)
    {
        if (!TryFrozenCoverage(frame, out var p)) throw new InvalidOperationException("Coverage token is not owned.");
        var mesh = _skinnedMeshes[p.Owner.Meshes[slice].Index]!;
        return (mesh.Vb, mesh.Ib);
    }
    internal bool IsCoverageIndexBufferForTests(SkinnedCoverageFrame frame, KhaozEngine.Gpu.IGpuBuffer? buffer)
    {
        var p = RequireCoverage(frame);
        foreach (var index in p.Owner.Indices) if (ReferenceEquals(index, buffer)) return true;
        for (int c = 0; c < p.CommandCount; c++)
            foreach (var draw in p.Equipment[c].Draws)
                if (_meshes[draw.Mesh.Index] is { } mesh && ReferenceEquals(mesh.Ib, buffer)) return true;
        return false;
    }
    /// <summary>Returns the owned staging or resolved presentation result. Foreign and stale tokens throw.</summary>
    public CoverageFrameOutcome GetCoverageOutcome(SkinnedCoverageFrame frame) => RequireCoverage(frame).Outcome;

    internal bool StageCoverage(SkinnedHostConformer conformer, ReadOnlySpan<Matrix4x4> palette, in Matrix4x4 model,
        ReadOnlySpan<CoverageCommandPose> commands, MotionKey actor, CoverageFallback fallback,
        out SkinnedCoverageFrame frame, out CoverageFailure failure)
    {
        frame = default;
        CoveragePreparation p = conformer.Request;
        if (_coverageDisposed || _coverageArena.Generation == 0 || _coverageArena.Closed || _framePrepared || actor.IsNone
            || fallback != CoverageFallback.UncoveredCurrentPose || p.Generation == _coverageArena.Generation
            || !conformer.Actor.IsNone && conformer.Actor != actor)
            return Refuse(CoverageFailureCode.StaleFrame, out failure);
        foreach (var handle in conformer.Meshes)
            if (!_skinnedSlots.IsValid(handle.Index, handle.Generation)) return Refuse(CoverageFailureCode.IdentityMismatch, out failure);
        if (palette.Length != p.Palette.Length) return Refuse(CoverageFailureCode.IdentityMismatch, out failure);
        // Identity precedes capacity. Do not index bounded scratch until the whole identity prefix is checked.
        foreach (var command in commands)
            if (command.Id == 0 || command.EquipmentInstanceId == 0 || command.Support is null
                || !ReferenceEquals(command.Topology, conformer.Topology)
                || !conformer.Gpu.TrySupport(command.Support, out _))
                return Refuse(CoverageFailureCode.IdentityMismatch, out failure);
        for (int c = 0; c < commands.Length; c++)
        {
            ref readonly var command = ref commands[c];
            CoverageEquipmentStage? found = null;
            for (int i = 0; i < _coverageArena.EquipmentCount; i++)
            {
                var stage = _coverageArena.Equipment[i];
                if (stage.Actor == actor && stage.Instance == command.EquipmentInstanceId
                    && stage.TryCommand(command.Id, out var support) && ReferenceEquals(support, command.Support)) found = stage;
            }
            if (found is null || command.EquipmentWorld != found.Draws[0].World
                || !ReferenceEquals(command.Topology, conformer.Topology))
                return Refuse(CoverageFailureCode.IdentityMismatch, out failure);
            if (c < CoverageScratch.MaxCommands) p.Equipment[c] = found;
        }
        if (commands.Length is < 1 or > CoverageScratch.MaxCommands)
            return Refuse(CoverageFailureCode.CapacityExceeded, out failure);
        if (!conformer.Uniforms.TryPrepare(palette, conformer.InverseBind, model, commands, out failure)) return false;
        var bounds = conformer.Gpu.Host.Bounds;
        MeshBounds skin = bounds.BoundSkin(conformer.Uniforms.ComposedPalette);
        if (!bounds.TryAdmitOrigin(skin, model, _frameOrigin, commands, out failure)) return false;
        Matrix4x4 relativeModel = model; relativeModel.Translation -= _frameOrigin;
        p.WorldEnvelope = CoverageSurfaceBounds.FromRender(bounds.ExpandWorld(skin, relativeModel, commands), _frameOrigin);
        p.WorldBounds = p.WorldEnvelope.ToWorld();
        if (!CoverageRefinement.Finite(p.WorldBounds.Min) || !CoverageRefinement.Finite(p.WorldBounds.Max))
            return Refuse(CoverageFailureCode.UnsupportedTransform, out failure);
        palette.CopyTo(p.Palette);
        p.Model = model; p.Actor = actor; p.CommandCount = commands.Length;
        commands.CopyTo(p.Commands);
        p.Generation = _coverageArena.Generation; p.Id = ++_coverageArena.NextPreparation; p.View = 0;
        p.Outcome = default; p.Sphere = default; p.SphereView = 0;
        Array.Clear(p.Draws); Array.Clear(p.Outlines);
        conformer.Actor = actor;
        _coverageArena.Preparations.Add(p);
        frame = new(p, p.Generation);
        return true;

        bool Refuse(CoverageFailureCode code, out CoverageFailure refused)
        { refused = new(code, conformer.Topology.HostIdentity, 0, 0, 0, 0); return false; }
    }

    bool CoverageHostLive(CoveragePreparation p)
    {
        foreach (var handle in p.Owner.Meshes)
            if (!_skinnedSlots.IsValid(handle.Index, handle.Generation)) return false;
        return true;
    }

    CoveragePreparation RequireCoverage(SkinnedCoverageFrame frame)
    {
        CoveragePreparation? p = frame.Preparation;
        if (p is null || _coverageDisposed || !ReferenceEquals(p.Owner.Scene, this) || p.Owner.Disposed
            || frame.Generation != _coverageArena.Generation || frame.Generation != p.Generation)
            throw new InvalidOperationException("Coverage token does not belong to this live scene and frame.");
        p.Owner.RequireThread();
        if (!CoverageHostLive(p)) throw new InvalidOperationException("Coverage host resource generation changed.");
        return p;
    }

    // Reads the frozen decision of a token already validated at submission in this scene and frame. Resource
    // liveness is not rechecked here: late disposal or unload is resolved to an explicit fallback before recording.
    bool TryFrozenCoverage(in SkinnedCoverageFrame frame, out CoveragePreparation p)
    {
        p = frame.Preparation!;
        return p is not null && ReferenceEquals(p.Owner.Scene, this) && frame.Generation == _coverageArena.Generation
            && frame.Generation == p.Generation;
    }

    CoveragePreparation? ValidateCoverageDraw(in SkinnedInstanceDraw draw, ReadOnlySpan<Matrix4x4> palette, bool outline)
    {
        if (draw.Coverage.Preparation is null) return null;
        var p = RequireCoverage(draw.Coverage);
        int slice = CoverageSlice(p, draw.Mesh);
        if (_coverageArena.Closed || _framePrepared || slice < 0 || draw.Model != p.Model || draw.Motion != p.Actor
            || !palette.SequenceEqual(p.Palette) || (outline ? p.Outlines[slice] : p.Draws[slice]) is not null)
            throw new InvalidOperationException("Coverage draw changed its frozen pose, actor or material slice.");
        return p;
    }

    static int CoverageSlice(CoveragePreparation p, SkinnedMeshHandle handle)
    {
        for (int i = 0; i < p.Owner.Meshes.Length; i++)
            if (p.Owner.Meshes[i].Index == handle.Index && p.Owner.Meshes[i].Generation == handle.Generation) return i;
        return -1;
    }

    void RecordCoverageDraw(CoveragePreparation? p, in SkinnedInstanceDraw draw, bool outline)
    {
        if (p is null) return;
        int slice = CoverageSlice(p, draw.Mesh);
        if (outline) p.Outlines[slice] = draw;
        else p.Draws[slice] = draw;
    }
}
