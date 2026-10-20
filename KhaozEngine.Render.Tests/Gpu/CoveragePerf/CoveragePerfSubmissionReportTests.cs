using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class CoveragePerfSubmissionReportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyControlsReportRealCountsForEveryFrameAndBlock(bool legacy)
    {
        using var probe = legacy ? CoveragePerfSceneProbe.OpenDirect(0)
            : CoveragePerfSceneProbe.Open(0, false, completeOutcomes: true);
        _ = probe.Warm();
        CoveragePerfFrameSample[] samples = [probe.Frame(CoveragePerfManifest.WarmupFrameCount),
            probe.Frame(CoveragePerfManifest.WarmupFrameCount + 1)];
        probe.Session.CompleteBatch(samples);
        var report = Report(legacy, probe, samples);
        using var json = JsonDocument.Parse(CoveragePerfReportJson.Serialize(report));
        var raw = json.RootElement.GetProperty("report").GetProperty("blocks")[legacy ? 0 : 1];
        foreach (var sample in raw.GetProperty("warmup").EnumerateArray().Concat(raw.GetProperty("samples").EnumerateArray()))
        {
            Count(sample, "submissions", 1);
            Count(sample, "submittedCompletionFences", 0);
            Count(sample, "submissionParticipants", legacy ? 0 : 1);
            Count(sample, "completionConsumers", 0);
        }
        var summary = json.RootElement.GetProperty("summaries")[legacy ? 0 : 1];
        Summary(summary.GetProperty("measuredSubmission"), legacy);
        Summary(summary.GetProperty("warmupSubmission"), legacy);
        var observations = json.RootElement.GetProperty("verdict").GetProperty("sharedSubmissionObservations");
        var observation = observations.EnumerateArray().Single(value => value.GetProperty("block").GetInt32() == (legacy ? 0 : 1));
        Summary(observation.GetProperty("measured"), legacy);
        Summary(observation.GetProperty("warmup"), legacy);
        var markdown = CoveragePerfReportJson.Markdown(report);
        Assert.Contains(legacy ? "DirectOff measured" : "SharedOff measured", markdown);
        Assert.Contains($"2 submissions, 0 completion fences, {(legacy ? 0 : 2)} participants, 0 consumers, 0 production waits", markdown);
        var copy = CoveragePerfReportJson.Deserialize(json.RootElement.GetRawText());
        Assert.Equal(json.RootElement.GetRawText(), CoveragePerfReportJson.Serialize(copy));
    }

    [Fact]
    public void AnAddedConsumerAndFenceAreObservedRatherThanReportedAsZero()
    {
        using var probe = CoveragePerfSceneProbe.Open(0, false, completeOutcomes: true);
        var consumer = new Consumer();
        probe.OnReadbacks = frame => Assert.True(frame.Submission.TryRetain(consumer, out _));
        var sample = probe.Frame(0);
        using var json = JsonDocument.Parse(CoveragePerfReportJson.Serialize(Report(false, probe, [sample])));
        var raw = json.RootElement.GetProperty("report").GetProperty("blocks")[1].GetProperty("samples")[0];
        Count(raw, "submittedCompletionFences", 1);
        Count(raw, "submissionParticipants", 1);
        Count(raw, "completionConsumers", 1);
    }

    [Theory]
    [InlineData(0, "submittedCompletionFences")]
    [InlineData(0, "completionConsumers")]
    [InlineData(0, "submissionParticipants")]
    [InlineData(1, "submittedCompletionFences")]
    [InlineData(1, "completionConsumers")]
    public void AConsumerOrFenceInAnEmptyControlRefusesSharedHostProof(int block, string field)
    {
        var report = CoveragePerfReportVectors.Create("C0");
        var json = JsonNode.Parse(CoveragePerfReportJson.Serialize(report))!;
        json["report"]!["blocks"]![block]!["samples"]![0]![field] = 1;
        var verdict = CoveragePerfBudgets.Evaluate(CoveragePerfReportJson.Deserialize(json.ToJsonString()));
        Assert.Contains(Assert.Single(verdict.SharedSubmission).Reasons,
            reason => reason.Contains("shared-host control", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EndAndPreSubmitIsAnOrderedChildInsideTheAggregateOnBothHosts(bool legacy)
    {
        using var probe = legacy ? CoveragePerfSceneProbe.OpenDirect(0) : CoveragePerfSceneProbe.Open(0, false);
        long start = -1, stop = -1;
        probe.OnTrace = name =>
        {
            if (name == "end-and-pre-submit-start") start = probe.Ticks;
            if (name == "end-and-pre-submit-stop") stop = probe.Ticks;
        };
        var sample = probe.Frame(0);
        var span = Assert.Single(sample.CpuChildren, child => child.Name == "host.endAndPreSubmit");
        Assert.True(start >= 0 && stop > start);
        Assert.Equal(stop - start, span.Ticks);
        Assert.InRange(probe.Events.IndexOf("end-and-pre-submit-start"), 1, probe.Events.IndexOf("commands-end") - 1);
        Assert.True(probe.Events.IndexOf("commands-end") < probe.Events.IndexOf("end-and-pre-submit-stop"));
        Assert.True(probe.Events.IndexOf("end-and-pre-submit-stop") < probe.Events.IndexOf(legacy ? "submit-direct" : "submit-span"));
        Assert.True(probe.Events.IndexOf(legacy ? "submit-direct" : "submit-span") < probe.Events.IndexOf("aggregate-stop"));
        Assert.Equal(probe.TickAtPresentEntry - probe.TickAtStart, sample.CpuFramePreparationTicks);
        Assert.True(span.Ticks < sample.CpuFramePreparationTicks);
    }

    [Fact]
    public void SharedCostDisclosureIsFixedInJsonAndMarkdownWithoutChangingArithmetic()
    {
        string[] disclosure =
        [
            "SharedOff registers the zero-row scene participant and runs its submission notification, including CommitCoverageMotion and conformer traversal. DirectOff skips these.",
            "Shared submission uses the span Submit overload. Direct submission uses the parameterless Submit overload. Vulkan validates completions on the span path.",
            "The production host includes overload, capture and submission-owner checks and the abandonment catch path. The legacy control copies RunPlain.",
            "Shared teardown disposes and drains the submission owner outside frame preparation."
        ];
        var report = CoveragePerfReportVectors.Cpu(CoveragePerfReportVectors.Create("C0"), 1, 11);
        using var json = JsonDocument.Parse(CoveragePerfReportJson.Serialize(report));
        var verdict = json.RootElement.GetProperty("verdict");
        Assert.True(verdict.TryGetProperty("sharedSubmissionDisclosure", out var actual));
        Assert.Equal(disclosure, actual.EnumerateArray().Select(value => value.GetString()));
        foreach (string line in disclosure) Assert.Contains("- " + line, CoveragePerfReportJson.Markdown(report));
        var comparison = Assert.Single(CoveragePerfBudgets.Evaluate(report).SharedSubmission);
        Assert.Equal(1000, comparison.Microseconds);
        Assert.Equal(10, comparison.Percent);
        Assert.Equal(CoveragePerfBudgetStatus.Unproved, comparison.Status);
    }

    static CoveragePerfReport Report(bool legacy, CoveragePerfSceneProbe probe, CoveragePerfFrameSample[] samples) =>
        CoveragePerfReportVectors.ChangeBlock(CoveragePerfReportVectors.Create("C0"), legacy ? 0 : 1,
            block => block with { Session = probe.Session.ReadInfo(), Warmup = samples.ToImmutableArray(), Samples = samples.ToImmutableArray() });

    static void Count(JsonElement value, string field, int expected)
    {
        Assert.True(value.TryGetProperty(field, out var count), field);
        Assert.Equal(expected, count.GetInt32());
    }

    static void Summary(JsonElement summary, bool legacy)
    {
        Count(summary, "frames", 2);
        Count(summary, "submissions", 2);
        Count(summary, "submittedCompletionFences", 0);
        Count(summary, "submissionParticipants", legacy ? 0 : 2);
        Count(summary, "completionConsumers", 0);
        Count(summary, "productionWaits", 0);
    }

    sealed class Consumer : IGpuFrameCompletionConsumer
    {
        public bool HasPending(IGpuFence completion) => false;
        public void DiscardUnsubmitted(IGpuFence completion) { }
        public void ReleaseCompleted(IGpuFence completion) { }
    }
}
