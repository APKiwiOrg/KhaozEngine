using System;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D;

public sealed partial class Scene3D
{
    CoverageGpuResources? _coverageGpu;
    internal CoverageGpuBatch? CoverageGpuBatchForTests => _coverageGpu?.Batch;

    bool UseCoverageVertexSkinning(SkinnedMeshHandle mesh, bool staged) => UseGpuSkinning || staged
        || _coverageGpu?.Available == false && _coverageHostUploads.ContainsKey((mesh.Index, mesh.Generation));

    internal void ReserveCoverageSupports(SkinnedHostConformer conformer, ReadOnlySpan<CoverageSupportSurface> supports)
    {
        RequireCoverageThread();
        if (conformer.Gpu.HasSupports(supports)) return;
        if (GpuRecording.OpenOwner(_gd) is not null)
            throw new InvalidOperationException("Reserve coverage supports before opening a command list.");
        if (!_coverageArena.Closed && (_framePrepared
            || conformer.Request.Generation == _coverageArena.Generation && _coverageArena.Generation != 0))
            throw new InvalidOperationException("Reserve supports at cold equip before staging or frame preparation.");
        _coverageGpu!.ReserveSupports(conformer.Gpu, supports);
    }

    void ReserveCoverageGpuFrame()
    {
        _coverageGpu?.PrepareFrame();
        ReserveCoverageOutcomes();
        int draws = _skinnedInstances.Items.Count, vertices = 0;
        foreach (var item in _skinnedInstances.Items)
            if (!UseCoverageVertexSkinning(item.Mesh, item.Coverage.Preparation is not null) && _skinnedCpuVerts[item.Mesh.Index] is { } source)
                vertices = checked(vertices + source.Length);
        _cpuSkinnedVerts.EnsureCapacity(vertices); _cpuSkinnedInstances.EnsureCapacity(draws); _cpuSkinnedDraws.EnsureCapacity(draws);
        _cpuSkinnedPrevious.EnsureCapacity(vertices);
        if (vertices > 0) _model.ReserveCoveredSkinning(vertices, draws, vertices);
        _gpuSkinnedDraws.EnsureCapacity(draws);
        int coveredDraws = 0;
        foreach (var p in _coverageArena.Preparations) coveredDraws = checked(coveredDraws + p.Owner.Meshes.Length);
        _coverageDraws.EnsureCapacity(coveredDraws); _coverageInstances.EnsureCapacity(coveredDraws);
        _coverageMaterials.EnsureCapacity(coveredDraws);
        if (_coverageGpu is { Available: true, Batch: { } batch })
        {
            _model.ReserveCoverage(batch, coveredDraws);
            _targetOutlines.ReserveCoverage(batch, _meshOutlinePartCount);
            _pointShadows?.ReserveCoverage(batch, coveredDraws);
        }
        draws = checked(draws + coveredDraws);
        _coveragePointMatrices.EnsureCapacity(checked(_lights.Count * 12));
        _model.EnsureSkinnedMainCapacity((uint)draws);
        _model.EnsureSkinnedBonePaletteCapacity((uint)draws);
        _model.EnsureSkinnedShadowCapacity((uint)draws * ShadowSettings.MaxCascades);
        _pointShadows?.EnsureSkinnedCasterCapacity((uint)draws);
        _pointShadows?.EnsureFaceCapacity(checked(_lights.Count * 2));
    }

    void ResolveCoverageGpu(IGpuCommandList commands)
    {
        CoverageAllocationObserverForTests?.Invoke(true);
        if (_coverageArena.Preparations.Count > 0 && _coverageArena.Closed)
            throw new InvalidOperationException("Coverage must be staged again for a new render view.");
        BeginCoverageMotion();
        _coverageView++;
        CoverageSphereEvaluationsForTests = DispatchedHostPreparationCount = 0;
        foreach (var p in _coverageArena.Preparations)
        {
            p.View = _coverageView;
            p.Sphere = ComputeCoverageSphere(p); p.SphereView = _coverageView;
        }
        PreparePointShadowSchedule();
        foreach (var p in _coverageArena.Preparations)
        {
            CoverageFailureCode code = p.Owner.Disposed ? CoverageFailureCode.StaleFrame
                : !CoverageHostLive(p) || !Complete(p) ? CoverageFailureCode.IdentityMismatch
                : _coverageGpu?.Available != true ? CoverageFailureCode.UnsupportedAdapter : CoverageFailureCode.None;
            if (code != CoverageFailureCode.None)
            {
                p.Outcome = new(CoveragePresentation.UncoveredFallback, new(code, p.Owner.Topology.HostIdentity, 0, 0, 0, 0));
                continue;
            }
            StageGpu(p, commands);
        }
        if (_coverageGpu is { Available: true, Batch.Count: > 0 } resources)
        {
            resources.Batch.UploadFrame(commands);
            resources.Record(commands);
            foreach (var p in _coverageArena.Preparations)
                if (p.Outcome.Presentation == CoveragePresentation.Pending)
                { p.Outcome = new(CoveragePresentation.Dispatched, default); DispatchedHostPreparationCount++; }
        }
        _coverageArena.Closed = true;
        CoverageAllocationObserverForTests?.Invoke(false);
    }

    bool Complete(CoveragePreparation p)
    {
        for (int slice = 0; slice < p.Draws.Length; slice++)
            if (p.Draws[slice] is null && p.Outlines[slice] is null) return false;
        for (int c = 0; c < p.CommandCount; c++)
        {
            var equipment = p.Equipment[c];
            for (int slice = 0; slice < equipment.Count; slice++)
                if (!_slots.IsValid(equipment.Draws[slice].Mesh.Index, equipment.Draws[slice].Mesh.Generation)
                    || !equipment.Submitted[slice] && !equipment.Outlined[slice]) return false;
        }
        return true;
    }

    void StageGpu(CoveragePreparation p, IGpuCommandList commands)
    {
        var actor = p.Owner.Gpu; var h = actor.Host.Upload; var uniforms = p.Owner.Uniforms;
        var batch = _coverageGpu!.Batch!;
        var r = actor.Reservation with { ActorSlot = batch.Count };
        p.GpuReservation = r;
        Span<uint> frame = actor.Frame; frame.Clear();
        frame[0] = r.StaticBase; frame[1] = r.WorkBase; frame[2] = r.CurrentBase; frame[3] = r.PreviousBase;
        frame[4] = (uint)r.ActorSlot * 16; frame[5] = (uint)h.SourceCount; frame[6] = (uint)h.VertexCount;
        frame[7] = (uint)h.TriangleCount; frame[8] = (uint)h.PositionGroupCount; frame[9] = (uint)h.AliasCount;
        frame[10] = (uint)h.ShadingGroupCount; frame[11] = (uint)h.BoneCount; frame[12] = (uint)p.CommandCount;
        for (int bone = 0; bone < h.BoneCount; bone++) CoverageGpuUpload.WriteMatrix(uniforms.ComposedPalette[bone], frame[(16 + bone * 16)..]);
        int start = 16 + h.BoneCount * 16;
        for (int slot = 0; slot < p.CommandCount; slot++)
        {
            var command = p.Commands[uniforms.Frames[slot].Command];
            if (!actor.TrySupport(command.Support, out uint offset)) throw new InvalidOperationException("Reserved coverage support disappeared.");
            CoverageGpuUpload.WriteCommand(uniforms.Frames[slot], command, offset, frame[(start + slot * 80)..]);
            frame[start + 320 + slot] = (uint)uniforms.Order[slot];
        }
        for (int region = 0; region < 32; region++) frame[start + 324 + region] = unchecked((uint)uniforms.RegionCommands[region]);
        batch.Add(r, frame);
        Pair(0, p.Generation); Pair(2, p.View); Pair(4, p.Actor.Value); Pair(6, p.Id);
        commands.UpdateBuffer(batch.Results, (uint)r.ActorSlot * 64, p.ResultIdentity);
        void Pair(int word, ulong value) { p.ResultIdentity[word] = (uint)value; p.ResultIdentity[word + 1] = (uint)(value >> 32); }
    }
}
