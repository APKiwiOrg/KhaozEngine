using System.Linq;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class CoveragePerfRouteScheduleTests
{
    const string Control = "shared-host control requires zero completion fences/consumers and the direct/shared participant schedule.";
    const string WarmupControl = "shared-host control requires zero completion consumers and the direct/shared participant schedule.";

    // The per-frame schedule each preset block's route fixes: submission participants, completion consumers and the
    // measured completion fence count. Every frame carries one frame submission on measured frames.
    [Theory]
    [InlineData("C0", new[] { 0, 0, 0, 1, 0, 0, 1, 0, 0, 1, 1, 1 })]
    [InlineData("C1", new[] { 0, 0, 0, 1, 0, 0, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0 })]
    [InlineData("G1", new[] { 2, 1, 1, 2, 2, 1, 2, 1, 1, 2, 2, 1, 2, 1, 1, 2, 2, 1, 2, 2, 1, 2, 1, 1 })]
    [InlineData("G2", new[] { 2, 1, 1, 2, 2, 1, 2, 1, 1, 2, 2, 1, 2, 1, 1, 2, 2, 1, 2, 2, 1, 2, 1, 1 })]
    [InlineData("G3", new[] { 2, 1, 1, 2, 2, 1, 2, 1, 1, 2, 2, 1, 2, 1, 1, 2, 2, 1, 2, 2, 1, 2, 1, 1 })]
    public void SteadyRouteSchedulesAreValidForEveryPreset(string preset, int[] schedule)
    {
        var report = CoveragePerfReportVectors.Create(preset);
        Assert.Equal(schedule.Length, report.Blocks.Length * 3);
        Assert.Empty(CoveragePerfValidity.Reasons(report));
        foreach (var block in report.Blocks)
        {
            int i = block.Block.Index * 3;
            Assert.All(block.Warmup.Concat(block.Samples), s => Assert.Equal((1, schedule[i], schedule[i + 1], schedule[i + 2]),
                (s.Submissions, s.SubmissionParticipants, s.CompletionConsumers, s.SubmittedCompletionFences)));
        }
    }

    public static TheoryData<string, int, bool, string, int, string?> Deviations()
    {
        var data = new TheoryData<string, int, bool, string, int, string?>();
        // preset, block, participants, consumers, fences, reason on a measured frame, on a warm-up frame, fence reason.
        (string, int, int, int, int, string, string?, string)[] routes =
        [
            ("C0", 0, 0, 0, 0, Control, WarmupControl, Control),
            ("C0", 1, 1, 0, 0, Control, WarmupControl, Control),
            ("C0", 2, 1, 0, 0, Control, WarmupControl, Control),
            ("C1", 0, 0, 0, 0, Control, WarmupControl, Control),
            ("C1", 1, 1, 0, 0, Control, WarmupControl, Control),
            ("C0", 3, 1, 1, 1, "SharedOn CpuOnly route requires 1 submission participant and 1 completion consumer.", null,
                "SharedOn CpuOnly route requires 1 completion fence."),
            ("C1", 2, 1, 1, 1, "SharedOn CpuOnly route requires 1 submission participant and 1 completion consumer.", null,
                "SharedOn CpuOnly route requires 1 completion fence."),
            ("G1", 0, 2, 1, 1, "SharedOff Phases route requires 2 submission participants and 1 completion consumer.", null,
                "SharedOff Phases route requires 1 completion fence."),
            ("G1", 1, 2, 2, 1, "SharedOn Phases route requires 2 submission participants and 2 completion consumers.", null,
                "SharedOn Phases route requires 1 completion fence."),
            ("G1", 4, 2, 1, 1, "SharedOff Total route requires 2 submission participants and 1 completion consumer.", null,
                "SharedOff Total route requires 1 completion fence."),
            ("G1", 5, 2, 2, 1, "SharedOn Total route requires 2 submission participants and 2 completion consumers.", null,
                "SharedOn Total route requires 1 completion fence."),
        ];
        foreach (var (preset, block, participants, consumers, fences, measuredReason, warmupReason, fenceReason) in routes)
        {
            foreach (bool measured in new[] { false, true })
            {
                string reason = measured ? measuredReason : warmupReason ?? measuredReason;
                foreach (int value in Wrong(participants)) data.Add(preset, block, measured, "participants", value, reason);
                foreach (int value in Wrong(consumers)) data.Add(preset, block, measured, "consumers", value, reason);
                // Warm-up fences are cold setup. Only measured frames carry the route's steady fence count.
                foreach (int value in Wrong(fences)) data.Add(preset, block, measured, "fences", value, measured ? fenceReason : null);
            }
        }
        return data;

        static int[] Wrong(int steady) => steady == 0 ? [1] : [steady - 1, steady + 1];
    }

    [Theory]
    [MemberData(nameof(Deviations))]
    public void AFrameOffItsRouteScheduleIsInvalid(string preset, int block, bool measured, string field, int value, string? reason)
    {
        var report = CoveragePerfReportVectors.Create(preset);
        report = CoveragePerfReportVectors.ChangeBlock(report, block, b =>
        {
            var frames = measured ? b.Samples : b.Warmup;
            frames = frames.SetItem(1, field switch
            {
                "participants" => frames[1] with { SubmissionParticipants = value },
                "consumers" => frames[1] with { CompletionConsumers = value },
                _ => frames[1] with { SubmittedCompletionFences = value },
            });
            return measured ? b with { Samples = frames } : b with { Warmup = frames };
        });
        int frame = measured ? report.Blocks[block].Samples[1].FrameIndex : 1;
        var reasons = CoveragePerfValidity.Reasons(report);
        if (reason is null) Assert.Empty(reasons);
        else Assert.Equal(new[] { $"Block {block}: {(measured ? "measured" : "warm-up")} frame {frame}: {reason}" }, reasons.ToArray());
    }
}
