using System;

namespace KhaozEngine.Render3D;

public sealed partial class Scene3D
{
    MotionKey _coverageVerificationActor;
    int _coverageVerificationInterval;
    CoverageVerificationReadbacks? _coverageVerification, _coverageVerificationTail;
    internal CoverageVerificationReadbacks? CoverageVerificationForTests => _coverageVerification;

    /// <summary>Enable asynchronous construction diagnostics for one explicit actor every N coverage frames.
    /// None or zero disables verification. Negative intervals throw. Configure outside command recording.
    /// This diagnostic does not certify geometry and never changes drawing or history.</summary>
    public void ConfigureCoverageVerification(MotionKey actor, int everyNFrames)
    {
        RequireCoverageThread();
        ArgumentOutOfRangeException.ThrowIfNegative(everyNFrames);
        if (KhaozEngine.Gpu.GpuRecording.OpenOwner(_gd) is not null)
            throw new InvalidOperationException("Configure coverage verification before opening a command list.");
        if (actor.IsNone || everyNFrames == 0) { actor = MotionKey.None; everyNFrames = 0; }
        if (_coverageVerificationActor == actor && _coverageVerificationInterval == everyNFrames) return;
        DisposeCoverageVerification();
        _coverageVerificationActor = actor; _coverageVerificationInterval = everyNFrames;
    }

    void ReserveCoverageVerification()
    {
        if (_coverageVerificationInterval == 0 || !_gd.Capabilities.SupportsCompletionFences) return;
        _coverageVerificationTail?.BeginFrame();
        if (!CoverageDiagnosticCompletionHeldForTests)
            for (var state = _coverageVerification; state is not null; state = state.Next)
                while (state.TryRead(out _)) { }
        foreach (var p in _coverageArena.Preparations)
        {
            if (p.Actor != _coverageVerificationActor || ReferenceEquals(_coverageVerificationTail?.Topology, p.Owner.Topology)) continue;
            var replacement = new CoverageVerificationReadbacks(_gd, p.Owner.Topology, p.Palette.Length);
            if (_coverageVerificationTail is null) _coverageVerification = replacement;
            else _coverageVerificationTail.Next = replacement;
            _coverageVerificationTail = replacement;
        }
        while (_coverageVerification is { Next: { } next, PendingCount: 0 } old)
        { _coverageVerification = next; old.Dispose(); }
    }

    void StageCoverageVerification(CoveragePreparation p, ReadOnlySpan<uint> upload, CoverageGpuBatch batch)
    {
        if (_coverageVerificationInterval != 0 && p.Actor == _coverageVerificationActor
            && p.Generation % (ulong)_coverageVerificationInterval == 0)
            _coverageVerificationTail?.Stage(p, CurrentFrameView.FrameIndex, upload, batch.Current);
    }

    void DisposeCoverageVerification()
    {
        for (var state = _coverageVerification; state is not null; state = state.Next) state.Dispose();
        _coverageVerification = _coverageVerificationTail = null;
    }
}
