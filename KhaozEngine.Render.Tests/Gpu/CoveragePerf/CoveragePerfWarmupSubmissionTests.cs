using System;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class CoveragePerfWarmupSubmissionTests
{
    const string NotOne = "submission schedule is not one frame submission.";
    const string Control = "shared-host control requires zero completion fences/consumers and the direct/shared participant schedule.";
    const string WarmupControl = "shared-host control requires zero completion consumers and the direct/shared participant schedule.";
    const string Rule = "Warm-up frames may carry cold-setup submissions and completion fences outside every gate. Measured frames require one frame submission and the route fence schedule.";

    [Fact]
    public void ASharedOffWarmupFrameWithAColdSetupSubmissionAndFenceIsValid()
    {
        var report = Warmup(CoveragePerfReportVectors.Create("C0"), 2, 0, s => s with { Submissions = 2, SubmittedCompletionFences = 1 });
        Assert.Empty(CoveragePerfValidity.Reasons(report));
        Assert.NotNull(CoveragePerfBudgets.Evaluate(report).OneActor!.Microseconds);
    }

    [Fact]
    public void TheSameSubmissionsAndFenceOnAMeasuredFrameAreInvalid()
    {
        var report = CoveragePerfReportVectors.ChangeSample(CoveragePerfReportVectors.Create("C0"), 2,
            s => s with { Submissions = 2, SubmittedCompletionFences = 1 });
        int frame = report.Blocks[2].Samples[0].FrameIndex;
        var reasons = CoveragePerfValidity.Reasons(report);
        Assert.Contains($"Block 2: measured frame {frame}: {NotOne}", reasons);
        Assert.Contains($"Block 2: measured frame {frame}: {Control}", reasons);
        Assert.Null(CoveragePerfBudgets.Evaluate(report).OneActor!.Microseconds);
    }

    [Fact]
    public void ASharedOnWarmupFrameWithTwoSubmissionsAndTwoFencesIsValid()
    {
        var report = Warmup(CoveragePerfReportVectors.Create("C0"), 3, 0, s => s with { Submissions = 2, SubmittedCompletionFences = 2 });
        Assert.Empty(CoveragePerfValidity.Reasons(report));
        Assert.NotNull(CoveragePerfBudgets.Evaluate(report).OneActor!.Microseconds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void AWarmupFrameWithoutASubmissionIsInvalid(int block)
    {
        var report = Warmup(CoveragePerfReportVectors.Create("C0"), block, 0, s => s with { Submissions = 0 });
        Assert.Contains($"Block {block}: warm-up frame 0: submission schedule has no frame submission.", CoveragePerfValidity.Reasons(report));
    }

    [Theory]
    [InlineData(0, "consumer")]
    [InlineData(1, "consumer")]
    [InlineData(2, "consumer")]
    [InlineData(0, "participant")]
    [InlineData(1, "participant")]
    [InlineData(2, "participant")]
    public void AControlWarmupFrameWithAConsumerOrAWrongParticipantCountIsInvalid(int block, string field)
    {
        var report = Warmup(CoveragePerfReportVectors.Create("C0"), block, 0, s => field == "consumer"
            ? s with { CompletionConsumers = 1 } : s with { SubmissionParticipants = s.SubmissionParticipants + 1 });
        Assert.Contains($"Block {block}: warm-up frame 0: {WarmupControl}", CoveragePerfValidity.Reasons(report));
    }

    [Theory]
    [InlineData(3, "fences")]
    [InlineData(3, "consumers")]
    [InlineData(2, "participants")]
    public void NegativeWarmupSubmissionObservationsStayInvalid(int block, string field)
    {
        var report = Warmup(CoveragePerfReportVectors.Create("C0"), block, 0, s => field switch
        {
            "fences" => s with { SubmittedCompletionFences = -1 },
            "consumers" => s with { CompletionConsumers = -1 },
            _ => s with { SubmissionParticipants = -1 },
        });
        Assert.Contains($"Block {block}: warm-up frame 0: submission observations cannot be negative.", CoveragePerfValidity.Reasons(report));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(5)]
    public void AGpuLayoutWarmupFrameWithAColdSetupSubmissionIsValid(int block)
    {
        var report = Warmup(CoveragePerfReportVectors.Create("G1"), block, 0,
            s => s with { Submissions = 2, SubmittedCompletionFences = s.SubmittedCompletionFences + 1 });
        Assert.Empty(CoveragePerfValidity.Reasons(report));
        Assert.Equal(CoveragePerfBudgetStatus.Pass, CoveragePerfBudgets.Evaluate(report).Gpu.Status);
    }

    [Fact]
    public void AGpuLayoutMeasuredFrameWithTwoSubmissionsStaysInvalid()
    {
        var report = CoveragePerfReportVectors.ChangeSample(CoveragePerfReportVectors.Create("G1"), 4, s => s with { Submissions = 2 });
        int frame = report.Blocks[4].Samples[0].FrameIndex;
        Assert.Contains($"Block 4: measured frame {frame}: {NotOne}", CoveragePerfValidity.Reasons(report));
    }

    [Fact]
    public void WarmupDeviationsRoundTripThroughJsonAndAppearInMarkdownIncludingNone()
    {
        var report = Warmup(CoveragePerfReportVectors.Create("C0"), 2, 0, s => s with { Submissions = 2, SubmittedCompletionFences = 1 });
        report = Warmup(report, 2, 5, s => s with { SubmittedCompletionFences = 1 });
        report = Warmup(report, 3, 0, s => s with { Submissions = 2, SubmittedCompletionFences = 2 });
        Assert.Empty(CoveragePerfValidity.Reasons(report));
        string json = CoveragePerfReportJson.Serialize(report);
        using (var document = JsonDocument.Parse(json))
        {
            var deviations = document.RootElement.GetProperty("verdict").GetProperty("warmupSubmissionDeviations");
            Assert.Equal(4, deviations.GetArrayLength());
            Deviation(deviations[0], 0, "DirectOff", 1, 0);
            Deviation(deviations[1], 1, "SharedOff", 1, 0);
            Deviation(deviations[2], 2, "SharedOff", 1, 0, (0, 2, 1), (5, 1, 1));
            Deviation(deviations[3], 3, "SharedOn", 1, 1, (0, 2, 2));
        }
        Assert.Equal(json, CoveragePerfReportJson.Serialize(CoveragePerfReportJson.Deserialize(json)));

        string markdown = CoveragePerfReportJson.Markdown(report);
        string[] lines =
        [
            Rule,
            "- Block 0 DirectOff warm-up cold setup outside every gate: none. Every warm-up frame matches the measured steady schedule of 1 submission and 0 completion fences.",
            "- Block 1 SharedOff warm-up cold setup outside every gate: none. Every warm-up frame matches the measured steady schedule of 1 submission and 0 completion fences.",
            "- Block 2 SharedOff warm-up cold setup outside every gate, measured steady schedule 1 submission and 0 completion fences: frame 0 with 2 submissions and 1 completion fence, frame 5 with 1 submission and 1 completion fence.",
            "- Block 3 SharedOn warm-up cold setup outside every gate, measured steady schedule 1 submission and 1 completion fence: frame 0 with 2 submissions and 2 completion fences.",
        ];
        int section = markdown.IndexOf("Shared submission", StringComparison.Ordinal), wall = markdown.IndexOf("Frame wall", StringComparison.Ordinal);
        foreach (string line in lines)
        {
            int at = markdown.IndexOf(Environment.NewLine + line + Environment.NewLine, StringComparison.Ordinal);
            Assert.True(at > section && at < wall, line);
        }
    }

    [Fact]
    public void AWarmupFrameThatDiffersOnlyInSubmissionsIsListed()
    {
        var report = Warmup(CoveragePerfReportVectors.Create("C0"), 2, 3, s => s with { Submissions = 2 });
        report = Warmup(report, 3, 4, s => s with { Submissions = 2 });
        Assert.Equal((0, 1), (report.Blocks[2].Warmup[3].SubmittedCompletionFences, report.Blocks[3].Warmup[4].SubmittedCompletionFences));
        Assert.Empty(CoveragePerfValidity.Reasons(report));
        var deviations = CoveragePerfSharedSubmission.WarmupDeviations(report);
        Assert.Equal(new[] { new CoveragePerfWarmupSubmissionFrame(3, 2, 0) }, deviations[2].Frames.ToArray());
        Assert.Equal(new[] { new CoveragePerfWarmupSubmissionFrame(4, 2, 1) }, deviations[3].Frames.ToArray());
        string markdown = CoveragePerfReportJson.Markdown(report);
        Assert.Contains("- Block 2 SharedOff warm-up cold setup outside every gate, measured steady schedule 1 submission and 0 completion fences: frame 3 with 2 submissions and 0 completion fences.", markdown);
        Assert.Contains("- Block 3 SharedOn warm-up cold setup outside every gate, measured steady schedule 1 submission and 1 completion fence: frame 4 with 2 submissions and 1 completion fence.", markdown);
    }

    [Theory]
    [InlineData("fences")]
    [InlineData("submissions")]
    public void TheSteadyScheduleComesFromTheRouteWhenAMeasuredFrameIsOffIt(string field)
    {
        var report = CoveragePerfReportVectors.ChangeSample(CoveragePerfReportVectors.Create("C0"), 3,
            s => field == "fences" ? s with { SubmittedCompletionFences = 2 } : s with { Submissions = 2 });
        report = Warmup(report, 3, 0, s => s with { Submissions = 2, SubmittedCompletionFences = 2 });
        var block = CoveragePerfSharedSubmission.WarmupDeviations(report)[3];
        Assert.Equal((1, 1), (block.SteadySubmissions, block.SteadyCompletionFences));
        Assert.Equal(new[] { new CoveragePerfWarmupSubmissionFrame(0, 2, 2) }, block.Frames.ToArray());
    }

    [Fact]
    public void ACoveredBlockWithoutActorsHasNoSteadyFence()
    {
        var report = CoveragePerfReportVectors.ChangeBlock(CoveragePerfReportVectors.Create("C0"), 3, b => b with { Block = b.Block with { Actors = 0 } });
        var block = CoveragePerfSharedSubmission.WarmupDeviations(report)[3];
        Assert.Equal((1, 0), (block.SteadySubmissions, block.SteadyCompletionFences));
        Assert.Equal(CoveragePerfManifest.WarmupFrameCount, block.Frames.Length);
    }

    [Fact]
    public void AnUnsupportedRouteListsNoWarmupDeviation()
    {
        var report = CoveragePerfReportVectors.ChangeBlock(CoveragePerfReportVectors.Create("G1"), 0,
            b => b with { Block = b.Block with { Condition = CoveragePerfCondition.DirectOff } });
        report = Warmup(report, 0, 0, s => s with { Submissions = 2, SubmittedCompletionFences = 2 });
        string json = CoveragePerfReportJson.Serialize(report);
        using (var document = JsonDocument.Parse(json))
        {
            var block = document.RootElement.GetProperty("verdict").GetProperty("warmupSubmissionDeviations")[0];
            Assert.Equal(JsonValueKind.Null, block.GetProperty("steadySubmissions").ValueKind);
            Assert.Equal(JsonValueKind.Null, block.GetProperty("steadyCompletionFences").ValueKind);
            Assert.Equal(0, block.GetProperty("frames").GetArrayLength());
        }
        Assert.Contains("- Block 0 DirectOff warm-up cold setup outside every gate: the route has no measured steady schedule, so no warm-up deviation is listed.",
            CoveragePerfReportJson.Markdown(report));
    }

    static CoveragePerfReport Warmup(CoveragePerfReport report, int block, int frame,
        Func<CoveragePerfFrameSample, CoveragePerfFrameSample> change) =>
        CoveragePerfReportVectors.ChangeBlock(report, block, b => b with { Warmup = b.Warmup.SetItem(frame, change(b.Warmup[frame])) });

    static void Deviation(JsonElement value, int block, string condition, int submissions, int fences,
        params (int Frame, int Submissions, int Fences)[] frames)
    {
        Assert.Equal(block, value.GetProperty("block").GetInt32());
        Assert.Equal(condition, value.GetProperty("condition").GetString());
        Assert.Equal(submissions, value.GetProperty("steadySubmissions").GetInt32());
        Assert.Equal(fences, value.GetProperty("steadyCompletionFences").GetInt32());
        Assert.Equal(frames, value.GetProperty("frames").EnumerateArray().Select(f => (f.GetProperty("frameIndex").GetInt32(),
            f.GetProperty("submissions").GetInt32(), f.GetProperty("submittedCompletionFences").GetInt32())).ToArray());
    }
}
