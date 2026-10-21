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
/// Warm-up frames whose submissions or completion fences differ from the block's measured steady schedule. Cold setup
/// outside every gate. A null steady schedule means the measured frames do not share one, so nothing is listed.
/// </summary>
internal sealed record CoveragePerfWarmupSubmissionDeviations(int Block, CoveragePerfCondition Condition,
    int? SteadySubmissions, int? SteadyCompletionFences, ImmutableArray<CoveragePerfWarmupSubmissionFrame> Frames);

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

    // Built from the completed report at publication, never inside a frame or the aggregate.
    internal static ImmutableArray<CoveragePerfWarmupSubmissionDeviations> WarmupDeviations(CoveragePerfReport report) =>
        report.Blocks.Select(b =>
        {
            ImmutableArray<CoveragePerfFrameSample> measured = b.Samples.IsDefault ? [] : b.Samples;
            ImmutableArray<CoveragePerfFrameSample> warmup = b.Warmup.IsDefault ? [] : b.Warmup;
            var schedules = measured.Select(s => (s.Submissions, s.SubmittedCompletionFences)).Distinct().ToArray();
            if (schedules.Length != 1)
                return new CoveragePerfWarmupSubmissionDeviations(b.Block.Index, b.Block.Condition, null, null, []);
            var (submissions, fences) = schedules[0];
            return new CoveragePerfWarmupSubmissionDeviations(b.Block.Index, b.Block.Condition, submissions, fences,
                warmup.Where(s => s.Submissions != submissions || s.SubmittedCompletionFences != fences)
                    .Select(s => new CoveragePerfWarmupSubmissionFrame(s.FrameIndex, s.Submissions, s.SubmittedCompletionFences))
                    .ToImmutableArray());
        }).ToImmutableArray();
}
