using System.Collections.Immutable;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class CoveragePerfSubmissionCountTests
{
    [Theory]
    [InlineData(0, "0 frames: 0 submissions, 0 completion fences, 0 participants, 0 consumers, 0 production waits.")]
    [InlineData(1, "1 frame: 1 submission, 1 completion fence, 1 participant, 1 consumer, 1 production wait.")]
    [InlineData(2, "2 frames: 2 submissions, 2 completion fences, 2 participants, 2 consumers, 2 production waits.")]
    public void Shared_submission_totals_use_the_observed_count_for_every_noun(int count, string totals)
    {
        var report = CoveragePerfReportVectors.ChangeBlock(CoveragePerfReportVectors.Create("C0"), 1, block =>
        {
            var samples = ImmutableArray.CreateBuilder<CoveragePerfFrameSample>(count);
            for (int i = 0; i < count; i++) samples.Add(block.Samples[0] with
            {
                FrameIndex = i,
                Submissions = 1,
                SubmittedCompletionFences = 1,
                SubmissionParticipants = 1,
                CompletionConsumers = 1,
                ProductionWaits = 1,
            });
            return block with { Warmup = samples.ToImmutable(), Samples = samples.ToImmutable() };
        });
        string markdown = CoveragePerfReportJson.Markdown(report);
        Assert.Contains("- Block 1 SharedOff measured, " + totals, markdown);
        Assert.Contains("- Block 1 SharedOff warm-up, " + totals, markdown);
    }
}
