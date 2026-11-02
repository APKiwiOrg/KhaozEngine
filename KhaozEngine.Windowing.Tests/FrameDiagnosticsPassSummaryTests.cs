using System;
using System.Linq;
using System.Text.Json;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Windowing;

public sealed class FrameDiagnosticsPassSummaryTests
{
    [Fact]
    public void Signature_spans_use_correlated_clocks_and_keep_missing_stages_out_of_whole_spans()
    {
        var passes = PassTimingSnapshot.Unavailable() with
        {
            Supported = true,
            ClockPairs = new([
                new(0, 0, 0, 0, 1_000_000, 100), new(72, 72, 72, 72, 11_000_000, 1100)], 0, 0),
            Encoders = new([
                new(1, 0, 0, "render", 0, 100, 200, 150, 400, null, null, "valid"),
                new(2, 1, 0, "render", 0, null, 200, 200, 300, null, null, "missing"),
                new(3, 2, 0, "compute", -1, null, null, null, null, 400, 900, "valid")], 0, 0),
        };
        var summaries = FrameDiagnosticsPassSummary.Encoders(passes);
        Assert.Equal(2, summaries.Length);
        var render = Assert.Single(summaries, s => s.Kind == "render");
        Assert.Equal(1, render.VertexMs.Count);
        Assert.Equal(1, render.VertexMs.Mean);
        Assert.Equal(2, render.FragmentMs.Count);
        Assert.Equal(1.75, render.FragmentMs.Mean);
        Assert.Equal(2.5, render.FragmentMs.P95);
        Assert.Equal(2.5, render.FragmentMs.P99);
        Assert.Equal(2.5, render.FragmentMs.Worst);
        Assert.Equal(1, render.EncoderMs.Count);
        Assert.Equal(3, render.EncoderMs.Median);
        Assert.Equal(5, Assert.Single(summaries, s => s.Kind == "compute").EncoderMs.Mean);
        var uncalibrated = FrameDiagnosticsPassSummary.Encoders(passes with { ClockPairs = new([], 0, 0) });
        Assert.All(uncalibrated, s => Assert.Equal(0, s.EncoderMs.Count));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(passes, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("encoders").GetProperty("records")[1].GetProperty("startVertex").ValueKind);
    }

    [Fact]
    public void Completion_intervals_exclude_block_transitions_missing_callbacks_and_other_buffers()
    {
        var submits = new[]
        {
            new FrameTimingSubmit(1, 8, 1, 0, FrameTimingCommandKind.FrameRecording),
            new FrameTimingSubmit(2, 8, 2, 1, FrameTimingCommandKind.FrameRecording),
            new FrameTimingSubmit(3, 8, 3, 72, FrameTimingCommandKind.FrameRecording),
            new FrameTimingSubmit(4, 8, 4, 73, FrameTimingCommandKind.FrameRecording),
            new FrameTimingSubmit(5, 8, 5, 75, FrameTimingCommandKind.FrameRecording),
            new FrameTimingSubmit(6, 8, 6, 76, FrameTimingCommandKind.SetupFlush),
        };
        var completions = new[]
        {
            new FrameTimingCompletion(1, 8, 11, 4, 1, 1.001),
            new FrameTimingCompletion(2, 8, 12, 4, 1.002, 1.004),
            new FrameTimingCompletion(3, 8, 13, 4, 2, 2.001),
            new FrameTimingCompletion(4, 8, 14, 4, 2.002, 2.005),
            new FrameTimingCompletion(5, 8, 15, 4, 2.010, 2.020),
            new FrameTimingCompletion(6, 8, 16, 4, 2.021, 2.030),
        };
        var timing = new FrameTimingSnapshot(new(submits, 0, 0), new(completions, 0, 0), new([], 0, 0), new([], 0, 0), 0);
        var passes = PassTimingSnapshot.Unavailable() with { Blocks = [new(0, 72, 0, 72, true), new(72, 144, 72, 144, false)] };
        var summary = FrameDiagnosticsGpuSummary.Create(timing, 144).WithPassTiming(timing, passes);
        Assert.Equal(1, summary.TimedFrameCompletionIntervalMs.Count);
        Assert.Equal(3, summary.TimedFrameCompletionIntervalMs.Mean, 8);
        Assert.Equal(1, summary.UntimedFrameCompletionIntervalMs.Count);
        Assert.Equal(4, summary.UntimedFrameCompletionIntervalMs.Mean, 8);
    }

    [Fact]
    public void Discard_states_split_frame_intervals_and_only_directional_encoder_spans_by_block()
    {
        var submits = new[]
        {
            new FrameTimingSubmit(1, 8, 1, 0, FrameTimingCommandKind.FrameRecording),
            new FrameTimingSubmit(2, 8, 2, 1, FrameTimingCommandKind.FrameRecording),
            new FrameTimingSubmit(3, 8, 3, 72, FrameTimingCommandKind.FrameRecording),
            new FrameTimingSubmit(4, 8, 4, 73, FrameTimingCommandKind.FrameRecording),
        };
        var completions = new[]
        {
            new FrameTimingCompletion(1, 8, 11, 4, 1, 1.001),
            new FrameTimingCompletion(2, 8, 12, 4, 1.002, 1.009),
            new FrameTimingCompletion(3, 8, 13, 4, 2, 2.001),
            new FrameTimingCompletion(4, 8, 14, 4, 2.002, 2.003),
        };
        var timing = new FrameTimingSnapshot(new(submits, 0, 0), new(completions, 0, 0), new([], 0, 0), new([], 0, 0), 0);
        var passes = PassTimingSnapshot.Unavailable() with
        {
            ClockPairs = new([new(0, 0, 0, 0, 1_000_000, 100), new(144, 144, 144, 144, 11_000_000, 1100)], 0, 0),
            Encoders = new([
                new(1, 0, 3, "render", 1, 100, 300, 200, 500, null, null, "valid", 5, 0, true, PassStoreResolution.DescriptorStore),
                new(3, 72, 3, "render", 1, 100, 200, 200, 300, null, null, "valid", 5, 0, true, PassStoreResolution.DontCare),
                new(3, 72, 4, "render", 2, 100, 900, 100, 900, null, null, "valid", 6, 0, false, null),
                new(1, 0, 4, "blit", -1, null, null, null, null, 100, 900, "valid", 6, 0, false, null)], 0, 0),
        };
        var experiment = PassExperimentSnapshot.Unavailable() with
        {
            Blocks = [new(0, 72, 0, 72, false), new(72, 144, 72, 144, true)],
        };
        var states = FrameDiagnosticsGpuSummary.Create(timing, 144).WithDiscardStates(timing, passes, experiment).CascadeDepthDiscard;
        var off = Assert.Single(states, s => !s.Discard);
        var on = Assert.Single(states, s => s.Discard);
        Assert.Equal(8, off.FrameCompletionIntervalMs.Mean, 8);
        Assert.Equal(2, on.FrameCompletionIntervalMs.Mean, 8);
        Assert.Equal(1, off.DirectionalEncoderMs.Count);
        Assert.Equal(4, off.DirectionalEncoderMs.Mean, 8);
        Assert.Equal(2, off.DirectionalVertexMs.Mean, 8);
        Assert.Equal(3, off.DirectionalFragmentMs.Mean, 8);
        Assert.Equal(1, on.DirectionalEncoderMs.Count);
        Assert.Equal(2, on.DirectionalEncoderMs.Mean, 8);
        Assert.Equal(1, on.DirectionalVertexMs.Mean, 8);
        Assert.Equal(1, on.DirectionalFragmentMs.Mean, 8);
    }

    [Fact]
    public void Schema_nine_serializes_labels_transfers_and_the_experiment_with_their_documented_names()
    {
        var json = JsonSerializer.SerializeToElement(new
        {
            passLabels = new PassLabelsSnapshot(["shadow.directional"], new([new(3, 9, 2, "begin", 0, 1)], 4, 0), 1, 2, 3, 5, 6, 7),
            transfer = new PassTransfer(3, 9, 1, PassTransferKind.BufferUpload, 64, 11, 0, 1),
            encoder = new PassTimingEncoder(9, 3, 2, "render", 0, null, null, null, null, null, null, "valid", 0, 1, true, PassStoreResolution.DontCare),
            experiment = PassExperimentSnapshot.Unavailable() with { Blocks = [new(72, 144, 1, 2, true)], DontCareApplied = 8 },
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var labels = json.GetProperty("passLabels");
        Assert.Equal("shadow.directional", labels.GetProperty("labels")[0].GetString());
        var label = labels.GetProperty("events").GetProperty("records")[0];
        Assert.Equal(3, label.GetProperty("callbackIndex").GetInt32());
        Assert.Equal(9, label.GetProperty("commandBuffer").GetInt64());
        Assert.Equal(2, label.GetProperty("encoderOrdinal").GetInt32());
        Assert.Equal("begin", label.GetProperty("kind").GetString());
        Assert.Equal(0, label.GetProperty("labelId").GetInt32());
        Assert.Equal(1, label.GetProperty("occurrence").GetInt32());
        Assert.Equal(4, labels.GetProperty("events").GetProperty("overflow").GetInt64());
        Assert.Equal(1, labels.GetProperty("labelOverflow").GetInt64());
        var transfer = json.GetProperty("transfer");
        Assert.Equal("bufferUpload", transfer.GetProperty("kind").GetString());
        Assert.Equal(64UL, transfer.GetProperty("bytes").GetUInt64());
        Assert.Equal(11, transfer.GetProperty("destination").GetInt64());
        Assert.Equal(1, transfer.GetProperty("encoderOrdinal").GetInt32());
        Assert.Equal(0, transfer.GetProperty("labelId").GetInt32());
        var encoder = json.GetProperty("encoder");
        Assert.Equal(0, encoder.GetProperty("labelId").GetInt32());
        Assert.Equal(1, encoder.GetProperty("labelOccurrence").GetInt32());
        Assert.True(encoder.GetProperty("directionalScope").GetBoolean());
        Assert.Equal("dontCare", encoder.GetProperty("storeResolution").GetString());
        var experiment = json.GetProperty("experiment");
        Assert.Equal("cascadeDepthDiscard", experiment.GetProperty("name").GetString());
        Assert.Equal(72, experiment.GetProperty("blockCallbacks").GetInt32());
        Assert.True(experiment.GetProperty("blocks")[0].GetProperty("discard").GetBoolean());
        Assert.Equal(8, experiment.GetProperty("dontCareApplied").GetInt64());
    }
}
