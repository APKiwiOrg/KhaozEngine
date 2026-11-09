using System;
using System.Numerics;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D;

public sealed partial class Scene3D
{
    Action<MotionKey, SkinnedMeshHandle>? _ordinaryCoverageOutlineRecorded;

    internal void RegisterCoverageAttempt(SkinnedHostCoverage host)
    {
        var p = host.Request;
        if (_coverageArena.Generation == 0 || _framePrepared || p.AttemptGeneration == _coverageArena.Generation) return;
        p.AttemptGeneration = _coverageArena.Generation;
        // Binding-level refusals can precede the lower host preparation. Their public binding result keeps
        // the precise reason. This registration admits only an exact subsequently submitted host draw.
        p.Failure = new(CoverageFailureCode.IdentityMismatch, host.Topology.HostIdentity, 0, 0, 0, 0);
    }

    internal bool TryQueueCoverageFallback(in SkinnedInstanceDraw draw, ReadOnlySpan<Matrix4x4> palette, bool outline = false)
    {
        if (draw.Coverage.Preparation is not null || draw.Motion.IsNone) return false;
        foreach (var owner in _coverageHosts)
        {
            var p = owner.Request;
            int slice = CoverageSlice(p, draw.Mesh);
            if (owner.Actor != draw.Motion || p.AttemptGeneration != _coverageArena.Generation || slice < 0) continue;
            if (_framePrepared) throw StaleCoverage(p);
            if (_coverageBatch?.DeviceRefused == true) return false;
            if ((outline ? p.Outlines[slice] : p.Draws[slice]) is not null) throw StaleCoverage(p);
            if (p.PublicationGeneration == _coverageArena.Generation
                && (draw.Model != p.Model || !palette.SequenceEqual(p.Palette))) throw StaleCoverage(p);
            if (p.Generation == _coverageArena.Generation)
            {
                p.TokenFree = true;
                p.Failure = new(CoverageFailureCode.IdentityMismatch, owner.Topology.HostIdentity, 0, 0, 0, 0);
            }
            if (p.PublicationGeneration != _coverageArena.Generation)
            {
                if (palette.Length != p.Palette.Length) return false;
                palette.CopyTo(p.Palette); p.Model = draw.Model;
                p.Raw = p.Failure.Code == CoverageFailureCode.TableHole;
                p.PublicationGeneration = _coverageArena.Generation;
            }
            if (outline) p.Outlines[slice] = draw;
            else p.Draws[slice] = draw;
            return true;
        }
        return false;
    }

    void PrepareCoverageFrame()
    {
        if (_coverageBatch is null) return;
        if (GpuRecording.OpenOwner(_gd) is { } owner)
            throw new GpuNestedRecordingException(GpuNestedRecordingRefusal.RecordingAlreadyOpen,
                GpuNestedRecordingException.BuildMessage(owner, "coverage preparation"))
            { Owner = owner, Attempted = "coverage preparation" };
        if (_coverageBatch.DeviceRefused)
        {
            foreach (var host in _coverageHosts) host.Request.Failure = new(CoverageFailureCode.UnsupportedAdapter,
                host.Topology.HostIdentity, 0, 0, 0, 0);
            return;
        }
        _coverageParticipant ??= new(this);
        if (_coverageSubmission is null || !_coverageSubmission.TryRegister(_coverageParticipant))
        {
            foreach (var host in _coverageHosts) host.Request.Failure = new(CoverageFailureCode.CapacityExceeded,
                host.Topology.HostIdentity, 0, 0, 1, 0);
            // No callback can observe this frame. Preserve history even if it submits.
            // The next covered motion may be one frame stale after this rare capacity refusal.
            return;
        }
        _coverageRegistered = true;
        _targetOutlines.OrdinarySkinnedDrawn = _ordinaryCoverageOutlineRecorded ??= MarkOrdinaryCoverage;
        // Retention failure never vetoes logical submission. A later fence proves earlier uses complete.
        _coverageSubmission.TryRetain(_coverageBatch, out _coverageCompletion);
        int draws = 0;
        foreach (var host in _coverageHosts)
        {
            var p = host.Request;
            bool accepted = p.Generation == _coverageArena.Generation && !p.TokenFree;
            if (!accepted && p.PublicationGeneration != _coverageArena.Generation) continue;
            bool complete = true, any = false;
            for (int i = 0; i < p.Draws.Length; i++)
            {
                any |= p.Draws[i] is not null || p.Outlines[i] is not null;
                complete &= p.Draws[i] is not null || p.Outlines[i] is not null;
            }
            if (!any) continue;
            if (!complete || accepted && !CompleteCoverageEquipment(p))
            {
                p.Failure = new(CoverageFailureCode.IdentityMismatch, host.Topology.HostIdentity, 0, 0, 0, 0);
                continue;
            }
            if (!accepted && !p.Raw) continue;
            if (!_coverageBatch.TryAcquireOutput(host, out int output, out var failure))
            { p.Failure = failure; continue; }
            bool staged = p.Raw
                ? _coverageBatch.TryStageRaw(host, output, p.Palette, p.Model, out failure)
                : _coverageBatch.TryStage(host, p.EntryIndex, output, p.Palette, p.Model, host.Uniforms.Commands, out failure);
            if (!staged) { _coverageBatch.CancelOutput(host); p.Failure = failure; continue; }
            p.OutputSlot = output;
            p.PreviousSlot = p.History.Corresponds(p, ActiveCamera, _coverageSerial) ? p.History.Committed.Slot : -1;
            p.Surface = _coverageBatch.Surface(host, output, p.PreviousSlot);
            p.PublicationRevision = p.Revision;
            draws = checked(draws + host.Meshes.Length);
        }
        if (draws == 0) return;
        _coverageDraws.EnsureCapacity(draws); _coverageInstances.EnsureCapacity(draws); _coverageMaterials.EnsureCapacity(draws);
        // Sets and dynamic windows are cold-reserved before the host opens its list.
        try
        {
            _model.ReserveCoverage(draws);
            _targetOutlines.ReserveCoverage(Math.Max(draws, _meshOutlinePartCount));
            _pointShadows?.ReserveCoverage(draws);
            // Every graphics program is queued before any owner joins its CPU work. Native realization
            // stays at bind, and no actor advertises a final surface until this transaction is ready.
            _model.CompleteCoverageCompilation();
            _targetOutlines.CompleteCoverageCompilation();
            _pointShadows?.CompleteCoverageCompilation();
            _pointShadows?.EnsureSkinnedCasterCapacity((uint)(_skinnedInstances.Items.Count + draws));
            _model.EnsureSkinnedMainCapacity((uint)(_skinnedInstances.Items.Count + draws));
            int slot = 0;
            foreach (var host in _coverageHosts)
            {
                var p = host.Request;
                p.Published = p.OutputSlot >= 0;
                if (!p.Published) continue;
                p.BindingStart = slot;
                for (int i = 0; i < host.Meshes.Length; i++, slot++)
                {
                    _model.ReserveCoverageSurface(slot, p.Surface);
                    _pointShadows?.ReserveCoverageSurface(slot, p.Surface);
                }
            }
            ReserveCoverageOutlineSurfaces();
        }
        catch (Exception error) when (error is NotSupportedException or ShaderValidationException)
        { RefuseCoverageDevice(); }
    }

    void ValidateCoverageDeviceReadiness()
    {
        if (_model.CoverageRefused && _coverageBatch is { DeviceRefused: false }) RefuseCoverageDevice();
    }

    void RefuseCoverageDevice()
    {
        _coverageBatch?.RefuseDevice();
        _model.RefuseCoverage();
        _targetOutlines.RefuseCoverage(_retired);
        _pointShadows?.RefuseCoverage(_retired);
        foreach (var host in _coverageHosts)
        {
            host.Request.Published = false;
            host.Request.Failure = new(CoverageFailureCode.UnsupportedAdapter, host.Topology.HostIdentity, 0, 0, 0, 0);
        }
    }

    bool CompleteCoverageEquipment(CoveragePreparation p)
    {
        for (int c = 0; c < p.CommandCount; c++)
        {
            var stage = p.Equipment[c];
            if (stage is null) return false;
            for (int i = 0; i < stage.Count; i++)
                if (!stage.Submitted[i] && !stage.Outlined[i]
                    || !_slots.IsValid(stage.Draws[i].Mesh.Index, stage.Draws[i].Mesh.Generation)) return false;
        }
        return true;
    }

    CoveragePreparation? PublishedCoverage(MotionKey actor, SkinnedMeshHandle mesh)
    {
        foreach (var host in _coverageHosts)
            if (host.Actor == actor && host.Request.Published && CoverageSlice(host.Request, mesh) >= 0) return host.Request;
        return null;
    }

    void MarkOrdinaryCoverage(MotionKey actor, SkinnedMeshHandle mesh)
    {
        if (!_coverageRegistered || _coverageBatch?.DeviceRefused == true) return;
        foreach (var host in _coverageHosts)
            if (host.Actor == actor && host.Request.AttemptGeneration == _coverageArena.Generation
                && CoverageSlice(host.Request, mesh) >= 0) host.Request.OrdinaryEncoded = true;
    }
}
