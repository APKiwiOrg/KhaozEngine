using System.Collections.Immutable;
using System.Linq;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Totals are observations over the declared frames, not assumed per-frame constants.</summary>
internal sealed record CoveragePerfSubmissionSummary(int Frames, int Submissions, int SubmittedCompletionFences,
    int SubmissionParticipants, int CompletionConsumers, int ProductionWaits);
internal sealed record CoveragePerfSharedSubmissionObservation(int Block, CoveragePerfCondition Condition,
    CoveragePerfSubmissionSummary Warmup, CoveragePerfSubmissionSummary Measured);

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
}
