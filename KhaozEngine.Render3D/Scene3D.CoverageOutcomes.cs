using System;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D;

/// <summary>A completed GPU diagnostic attributed to the immutable identities captured for its submission.</summary>
public readonly record struct CoverageGpuOutcome(ulong FrameId, ulong ViewId, MotionKey Actor, CoverageFrameOutcome Outcome);

public sealed partial class Scene3D
{
    /// <summary>Drain submitted work before destroying scene resources.</summary>
    public void Dispose() => DisposeResources(null);

    // Share the host owner teardown drain with the scene resource teardown. Public scene disposal keeps its drain.
    internal void DisposeWithSubmission(GpuFrameSubmission submission) => DisposeResources(submission);

    CoverageOutcomeReadbacks? _coverageOutcomes, _coverageOutcomesTail;
    readonly CoverageSubmissionState _coverageSubmissionState = new();
    CoverageOutcomeIdentity[] _coverageOutcomeIdentities = [];
    internal bool CoverageDiagnosticCompletionHeldForTests { get; set; }

    /// <summary>Poll completed coverage diagnostics without waiting. Availability never changes rendering or history.
    /// On Direct3D11 completion becomes visible after the host's normal Present boundary.</summary>
    public bool TryReadCoverageOutcome(out CoverageGpuOutcome outcome)
    {
        RequireCoverageThread();
        if (CoverageDiagnosticCompletionHeldForTests) { outcome = default; return false; }
        while (_coverageOutcomes is { } state)
        {
            if (state.TryRead(out outcome))
            {
                if (!state.HasOutcomes && state.Next is { } next) { _coverageOutcomes = next; state.Dispose(); }
                return true;
            }
            if (state.HasOutcomes || state.Next is null) break;
            _coverageOutcomes = state.Next;
            state.Dispose();
        }
        outcome = default;
        return false;
    }

    void ReserveCoverageOutcomes()
    {
        if (!_gd.Capabilities.SupportsCompletionFences || _coverageGpu?.Batch is not { } batch) return;
        int capacity = checked((int)batch.Results.SizeInBytes / 64);
        if (_coverageOutcomesTail is { } tail && tail.Capacity >= capacity) return;
        var replacement = new CoverageOutcomeReadbacks(_gd, capacity);
        if (_coverageOutcomesTail is null) _coverageOutcomes = replacement;
        else _coverageOutcomesTail.Next = replacement;
        _coverageOutcomesTail = replacement;
        _coverageOutcomeIdentities = new CoverageOutcomeIdentity[capacity];
    }

    internal void RecordCoverageReadbacks(IGpuCommandList commands, GpuFrameSubmission submission)
    {
        _coverageSubmissionState.Outcomes = _coverageOutcomes;
        if (!submission.TryRegister(_coverageSubmissionState)) { _coverageSubmissionState.Abandoned(); return; }
        if (_coverageOutcomesTail is not { } state || _coverageGpu?.Batch is not { Count: > 0 } batch) return;
        foreach (var p in _coverageArena.Preparations)
        {
            if (p.Outcome.Presentation != CoveragePresentation.Dispatched) continue;
            _coverageOutcomeIdentities[p.GpuReservation.ActorSlot] = new(p.Generation, p.View, p.Actor, p.Id,
                p.Owner.Topology.HostIdentity)
            {
                Command0 = Command(p, 0),
                Command1 = Command(p, 1),
                Command2 = Command(p, 2),
                Command3 = Command(p, 3)
            };
        }
        state.Record(commands, submission, batch.Results, _coverageOutcomeIdentities.AsSpan(0, batch.Count));
        static CoverageOutcomeCommand Command(CoveragePreparation p, int index) => index < p.CommandCount
            ? new(p.Commands[index].EquipmentInstanceId, p.Commands[index].Id) : default;
    }

    void DisposeCoverageOutcomes()
    {
        for (var state = _coverageOutcomes; state is not null; state = state.Next) state.Dispose();
    }
}
