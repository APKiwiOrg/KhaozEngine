using System;
using System.Collections.Immutable;
using System.Linq;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Totals are observations over the declared frames, not assumed per-frame constants.</summary>
internal sealed record CoveragePerfSubmissionSummary(int Frames, int Submissions, int SubmittedCompletionFences,
    int SubmissionParticipants, int CompletionConsumers, int ProductionWaits);
internal sealed record CoveragePerfSharedSubmissionObservation(int Block, CoveragePerfCondition Condition,
    CoveragePerfSubmissionSummary Warmup, CoveragePerfSubmissionSummary Measured);
internal sealed record CoveragePerfWarmupSubmissionFrame(int FrameIndex, int Submissions, int SubmittedCompletionFences);
/// <summary>
/// Warm-up frames whose submissions or completion fences differ from the measured steady schedule the block's route fixes.
/// Cold setup outside every gate. A null steady schedule means the route is unsupported, so nothing is listed.
/// </summary>
internal sealed record CoveragePerfWarmupSubmissionDeviations(int Block, CoveragePerfCondition Condition,
    int? SteadySubmissions, int? SteadyCompletionFences, ImmutableArray<CoveragePerfWarmupSubmissionFrame> Frames);

/// <summary>Per-frame submission participants and completion consumers, and the measured completion fence count.</summary>
internal readonly record struct CoveragePerfRouteSchedule(int Participants, int Consumers, int CompletionFences);

internal static class CoveragePerfSharedSubmission
{
    internal static readonly ImmutableArray<string> Disclosure =
    [
        "SharedOff registers the zero-row scene participant and runs its submission notification, including CommitCoverageMotion and conformer traversal. DirectOff skips these.",
        "Shared submission uses the span Submit overload. Direct submission uses the parameterless Submit overload. Vulkan validates completions on the span path.",
        "The production host includes overload, capture and submission-owner checks and the abandonment catch path. The legacy control copies RunPlain.",
        "Shared teardown disposes and drains the submission owner outside frame preparation."
    ];

    internal static CoveragePerfSubmissionSummary Summarize(ImmutableArray<CoveragePerfFrameSample> frames) =>
        new(frames.Length, frames.Sum(s => s.Submissions), frames.Sum(s => s.SubmittedCompletionFences),
            frames.Sum(s => s.SubmissionParticipants), frames.Sum(s => s.CompletionConsumers), frames.Sum(s => s.ProductionWaits));

    internal static ImmutableArray<CoveragePerfSharedSubmissionObservation> Observe(CoveragePerfReport report) =>
        report.Blocks.Where(b => b.Block.TimerLayout == CoveragePerfTimerLayout.CpuOnly &&
            b.Block.Condition != CoveragePerfCondition.SharedOn && (report.Manifest?.Preset != "C0" || b.Block.Actors == 0))
        .Select(b => new CoveragePerfSharedSubmissionObservation(b.Block.Index, b.Block.Condition,
            Summarize(b.Warmup), Summarize(b.Samples))).ToImmutableArray();

    /// <summary>
    /// The schedule a route fixes on every frame, read from code. DirectOff never begins a frame submission, so the probe
    /// reads no participant or consumer and the parameterless Submit carries no fence. A shared host registers the scene
    /// participant. A covered actor row retains the slot fence. A GPU timer layout registers and retains the timestamp
    /// ring. Every consumer shares the one slot fence the frame submission carries. DirectOff has no GPU layout route.
    /// </summary>
    internal static CoveragePerfRouteSchedule? Route(CoveragePerfBlock block)
    {
        bool gpu = block.TimerLayout != CoveragePerfTimerLayout.CpuOnly;
        if (block.Condition == CoveragePerfCondition.DirectOff) return gpu ? null : new(0, 0, 0);
        int consumers = (gpu ? 1 : 0) + (block.Condition == CoveragePerfCondition.SharedOn && block.Actors > 0 ? 1 : 0);
        return new(gpu ? 2 : 1, consumers, consumers > 0 ? 1 : 0);
    }

    internal static string Count(int value, string noun) => FormattableString.Invariant($"{value} {noun}{(value == 1 ? "" : "s")}");

    // Built from the completed report at publication, never inside a frame or the aggregate. Measured frames carry one
    // frame submission and the route's fence count, so the steady schedule comes from the route, not the observations.
    internal static ImmutableArray<CoveragePerfWarmupSubmissionDeviations> WarmupDeviations(CoveragePerfReport report) =>
        report.Blocks.Select(b =>
        {
            if (Route(b.Block) is not { } route)
                return new CoveragePerfWarmupSubmissionDeviations(b.Block.Index, b.Block.Condition, null, null, []);
            ImmutableArray<CoveragePerfFrameSample> warmup = b.Warmup.IsDefault ? [] : b.Warmup;
            return new CoveragePerfWarmupSubmissionDeviations(b.Block.Index, b.Block.Condition, 1, route.CompletionFences,
                warmup.Where(s => s.Submissions != 1 || s.SubmittedCompletionFences != route.CompletionFences)
                    .Select(s => new CoveragePerfWarmupSubmissionFrame(s.FrameIndex, s.Submissions, s.SubmittedCompletionFences))
                    .ToImmutableArray());
        }).ToImmutableArray();
}
