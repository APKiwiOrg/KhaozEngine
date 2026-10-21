using System;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class CoveragePerfWarmupSubmissionTests
{
    const string NotOne = "submission schedule is not one frame submission.";
    const string Control = "shared-host control requires zero completion fences/consumers and the direct/shared participant schedule.";

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
        Assert.Contains(CoveragePerfValidity.Reasons(report),
            r => r.StartsWith($"Block {block}: warm-up frame 0: submission schedule", StringComparison.Ordinal));
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
        Assert.Contains(CoveragePerfValidity.Reasons(report),
            r => r.StartsWith($"Block {block}: warm-up frame 0: shared-host control", StringComparison.Ordinal));
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
            "- Block 0 DirectOff warm-up cold setup outside every gate: none. Every warm-up frame matches the measured steady schedule of 1 submissions and 0 completion fences.",
            "- Block 1 SharedOff warm-up cold setup outside every gate: none. Every warm-up frame matches the measured steady schedule of 1 submissions and 0 completion fences.",
            "- Block 2 SharedOff warm-up cold setup outside every gate, measured steady schedule 1 submissions and 0 completion fences: frame 0 with 2 submissions and 1 completion fences, frame 5 with 1 submissions and 1 completion fences.",
            "- Block 3 SharedOn warm-up cold setup outside every gate, measured steady schedule 1 submissions and 1 completion fences: frame 0 with 2 submissions and 2 completion fences.",
        ];
        int section = markdown.IndexOf("Shared submission", StringComparison.Ordinal), wall = markdown.IndexOf("Frame wall", StringComparison.Ordinal);
        foreach (string line in lines)
        {
            int at = markdown.IndexOf(line, StringComparison.Ordinal);
            Assert.True(at > section && at < wall, line);
        }
    }

    [Fact]
    public void AnUnresolvedMeasuredScheduleListsNoWarmupDeviation()
    {
        var report = CoveragePerfReportVectors.ChangeSample(CoveragePerfReportVectors.Create("C0"), 3, s => s with { SubmittedCompletionFences = 2 });
        report = Warmup(report, 3, 0, s => s with { Submissions = 2, SubmittedCompletionFences = 2 });
        string json = CoveragePerfReportJson.Serialize(report);
        using (var document = JsonDocument.Parse(json))
        {
            var block = document.RootElement.GetProperty("verdict").GetProperty("warmupSubmissionDeviations")[3];
            Assert.Equal(JsonValueKind.Null, block.GetProperty("steadySubmissions").ValueKind);
            Assert.Equal(JsonValueKind.Null, block.GetProperty("steadyCompletionFences").ValueKind);
            Assert.Equal(0, block.GetProperty("frames").GetArrayLength());
        }
        Assert.Contains("- Block 3 SharedOn warm-up cold setup outside every gate: the measured steady schedule is unresolved, so no warm-up deviation is listed.",
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
