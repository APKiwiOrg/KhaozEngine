using System;
using System.Collections.Generic;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Fake clock and environment for driver accounting tests. One tick is one millisecond.</summary>
internal static class CoverageCostFakes
{
    internal static CoverageCostClock Clock()
    {
        long now = 0;
        return new CoverageCostClock(() => ++now, 1000);
    }

    /// <summary>A complete same-run identity, so pairing rules can be exercised without a hosted runner.</summary>
    internal static IReadOnlyList<KeyValuePair<string, string>> Environment(string commit = "abc123", string cpu = "fake cpu",
        string runId = "run-1") =>
    [
        new("runId", runId), new("processId", "42"), new("commit", commit), new("runtime", ".NET fake"), new("os", "fake os"),
        new("architecture", "Arm64"), new("cpu", cpu), new("processorCount", "4"), new("runner", "fake runner"),
        new("gc", "server=False latency=Interactive"), new("tieredCompilation", "default"), new("tieredPgo", "default"),
        new("stopwatchFrequency", "1000"), new("stopwatchHighResolution", "True"),
    ];
}

/// <summary>
/// A session that reports fixed spans and never renders. Ticks are milliseconds under <see cref="CoverageCostFakes.Clock"/>.
/// The third measured frame encodes a little slower. By default one actor per frame declines and one falls back, so a
/// budget must refuse it. <see cref="AllCovered"/> gives the only shape a coverage-on budget may accept.
/// </summary>
internal sealed class CoverageCostFakeSession : ICoverageCostSession
{
    internal const int ActorsPerFrame = 32;
    internal int Warmup { get; init; } = 4;
    internal bool Coverage { get; init; } = true;
    internal bool Synthetic { get; init; } = true;
    internal bool NativeDevice { get; init; } = true;
    internal bool ObservesSkinnedPreparation { get; init; } = true;
    internal bool AllCovered { get; init; }
    internal bool AllDeclined { get; init; }
    internal bool AllFallback { get; init; }
    internal long PrepareTicks { get; init; } = 12;
    internal long OrdinarySkinningTicks { get; init; } = 2;
    /// <summary>The whole skinned preparation parent span, containing surface, ordinary skinning and upload.</summary>
    internal long SkinnedPreparationTicks { get; init; } = 4;
    internal long MotionCommitTicks { get; init; } = 1;
    internal string Adapter { get; init; } = "fake adapter";
    internal CoverageCostTopology Topology { get; init; } =
        new(2121, 2000, 200, 96, 2900, 2600, 2, 2, "2", 2, "fingerprint");
    /// <summary>Measured frame index whose frame wall exceeds a 50 ms limit, or a negative warm-up index.</summary>
    internal int SlowFrame { get; init; } = int.MinValue;
    internal int ThrowAt { get; init; } = -1;
    internal bool ThrowDiagnostics { get; init; }
    internal bool ThrowDispose { get; init; }
    internal int Frames { get; private set; }
    internal bool Disposed { get; private set; }
    internal Action<CoverageCostFakeSession>? OnFrame { get; init; }
    internal Action? OnRead { get; init; }
    internal Action? OnDispose { get; init; }
    bool ICoverageCostSession.Synthetic => Synthetic;
    bool ICoverageCostSession.NativeDevice => NativeDevice;
    bool ICoverageCostSession.ObservesSkinnedPreparation => ObservesSkinnedPreparation;

    public CoverageCostSessionInfo Info => new("VulkanNative", Adapter, "unclassified", "camera", "light", "layout", "timing", Topology);

    public void Frame(bool measured, ref CoverageCostFrameSample sample)
    {
        int frame = Frames++;
        OnFrame?.Invoke(this);
        if (frame == ThrowAt) throw new InvalidOperationException("fake failure");
        int measuredIndex = frame - Warmup;
        sample.PrepareTicks = PrepareTicks;
        sample.SkinningTicks = Coverage ? 3 : 0;
        sample.GeometryTicks = Coverage ? 4 : 0;
        sample.GeometryCertificationTicks = Coverage ? 1 : 0;
        sample.SubmissionTicks = 2;
        sample.ReserveTicks = 1;
        sample.EncodeTicks = measuredIndex == 2 ? 32 : 30;
        sample.ResolutionTicks = 9;
        sample.CertificationTicks = Coverage ? 2 : 0;
        sample.SurfaceTicks = Coverage ? 1 : 0;
        sample.OrdinarySkinningTicks = Coverage ? 0 : OrdinarySkinningTicks;
        sample.SkinnedUploadTicks = 1;
        sample.SkinnedPreparationTicks = SkinnedPreparationTicks;
        sample.MotionCommitTicks = MotionCommitTicks;
        sample.OrdinaryCpuSkinnedVertices = Coverage ? 0 : 1092 * ActorsPerFrame;
        sample.SubmitWaitTicks = 40;
        sample.FrameTicks = measuredIndex == SlowFrame ? 51 : 49;
        sample.ActorFrames = ActorsPerFrame;
        if (Coverage)
        {
            sample.Accepted = AllDeclined ? 0 : AllCovered || AllFallback ? ActorsPerFrame : ActorsPerFrame - 1;
            sample.Declined = ActorsPerFrame - sample.Accepted;
            sample.Covered = AllDeclined || AllFallback ? 0 : AllCovered ? ActorsPerFrame : ActorsPerFrame - 2;
            sample.Fallback = sample.Accepted - sample.Covered;
            sample.Skinned = sample.Evaluated = sample.GeometryCertified = sample.Accepted;
            sample.Certified = sample.Accepted;
        }
        sample.Passes = 10;
        sample.Stats.DrawCalls = 7;
        sample.Stats.Triangles = 5000;
        sample.Stats.AddSkinnedUpload(64);
        sample.Stats.AddInstanceUpload(32);
    }

    public IReadOnlyList<CoverageCostMetric> ReadCapacities()
    {
        OnRead?.Invoke();
        return ThrowDiagnostics ? throw new InvalidOperationException("capacity read failed")
            : [CoverageCostMetric.Measured("actor.scratch.vertices", "elements", 2900, "fake")];
    }

    public IReadOnlyList<KeyValuePair<string, int>> ReadRefusals() => ThrowDiagnostics
        ? throw new InvalidOperationException("refusal read failed")
        : Coverage && !AllCovered ? [new("decline:CapacityExceeded", 2), new("decline:IdentityMismatch", 1), new("fallback:UnsupportedTransform", 3)] : [];

    public IReadOnlyList<CoverageCostMetric> ReadEvidence() => ThrowDiagnostics
        ? throw new InvalidOperationException("evidence read failed")
        : [CoverageCostMetric.Measured("certifiedContext.host.main.total", "count", 3, "fake")];

    public void Dispose()
    {
        Disposed = true;
        OnDispose?.Invoke();
        if (ThrowDispose) throw new InvalidOperationException("dispose failed");
    }
}
