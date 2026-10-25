using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using KhaozEngine.Gpu;
using KhaozEngine.Windowing;
using Xunit;
using static KhaozEngine.Tests.Windowing.ShadowStageProbeFrameTests;

namespace KhaozEngine.Tests.Windowing;

public sealed class ShadowStageProbeRecorderTests : IDisposable
{
    readonly string _directory = Path.Combine("/tmp/khaozengine-mac-frame-performance/shadow-stage-probe", "recorder-" + Guid.NewGuid().ToString("N"));
    string Pathname => Path.Combine(_directory, "result.json");
    static FrameDiagnosticsWindowState Window => new(true, false, false, 800, 600, 0, false);
    readonly Frame _frame = new();
    public ShadowStageProbeRecorderTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);

    FrameDiagnosticsRecorder Start(int capacity = 1, ShadowStageProbeMode mode = ShadowStageProbeMode.Counts)
    {
        var recorder = new FrameDiagnosticsRecorder(Pathname, 1000, capacity, mode);
        File.WriteAllText(Pathname + ".start", "");
        Observe(recorder, 0);
        Assert.Equal(default, recorder.NextShadowStageRequest);
        Observe(recorder, 1000);
        return recorder;
    }

    void Observe(FrameDiagnosticsRecorder recorder, long timestamp, bool suppressed = false) =>
        recorder.Observe(.016, timestamp, Window with { RenderSuppressed = suppressed }, default,
            _frame.CompletedShadowStageCallback);

    void Complete(ShadowStageProbeRequest request, int count = 7, ShadowStageProbeConfiguration configuration = default)
    {
        _frame.BeginShadowStageCallback(request);
        _frame.PublishShadowStageSnapshot(Payload(request, count, configuration));
        _frame.CompleteShadowStageCallback();
    }

    async Task<JsonDocument> Finish(FrameDiagnosticsRecorder recorder)
    {
        await recorder.Publication;
        Assert.Null(recorder.Failure);
        return JsonDocument.Parse(File.ReadAllText(Pathname));
    }
    static JsonElement Stage(JsonDocument report, int index = 0) => report.RootElement.GetProperty("samples")[index].GetProperty("shadowStage");
    static int Count(JsonElement stage) => stage.GetProperty("callback").GetProperty("snapshot").GetProperty("counters").GetProperty("staticRequests").GetInt32();
    static void Status(JsonElement stage, ShadowStageProbeStatus expected) => Assert.Equal((int)expected, stage.GetProperty("status").GetInt32());

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("off")]
    public void Inactive_startup_without_a_path_needs_no_recorder(string? mode)
        => Assert.Null(FrameDiagnosticsRecorder.FromSettings(null, mode));

    [Theory]
    [InlineData("counts")]
    [InlineData("timings")]
    public void Active_startup_without_a_usable_recorder_is_an_explicit_initialization_failure(string mode)
    {
        var error = Assert.Throws<ArgumentException>(() => FrameDiagnosticsRecorder.FromSettings(null, mode));
        Assert.Contains("KE_SHADOW_STAGE_MODE", error.Message);
        Assert.Contains("KE_FRAME_DIAGNOSTICS_PATH", error.Message);
        Assert.False(File.Exists(Pathname));
    }

    [Theory]
    [InlineData("Counts")]
    [InlineData("timing")]
    [InlineData("1")]
    [InlineData(" counts")]
    public void Malformed_startup_mode_is_rejected_instead_of_silently_disabled(string mode)
    {
        var error = Assert.Throws<ArgumentException>(() => FrameDiagnosticsRecorder.FromSettings(Pathname, mode));
        Assert.Contains("KE_SHADOW_STAGE_MODE", error.Message);
        Assert.False(File.Exists(Pathname));
    }

    [Fact]
    public async Task Trigger_and_baseline_pair_sequence_one_with_the_first_completed_interval()
    {
        using var recorder = Start();
        var request = recorder.NextShadowStageRequest;
        Assert.NotEqual(Guid.Empty, request.Epoch);
        Assert.Equal(1, request.Sequence);
        Complete(request);
        Observe(recorder, 1016);
        using var report = await Finish(recorder);
        Assert.Equal(1, report.RootElement.GetProperty("sampleCount").GetInt32());
        Assert.Equal(.016, report.RootElement.GetProperty("samples")[0].GetProperty("monotonicIntervalSeconds").GetDouble());
        Assert.Equal(1, Stage(report).GetProperty("request").GetProperty("sequence").GetInt64());
        Status(Stage(report), ShadowStageProbeStatus.Valid);
        Assert.Equal(7, Count(Stage(report)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Last_payload_is_frozen_before_duration_or_capacity_publication(bool duration)
    {
        using var recorder = Start(duration ? 8 : 1);
        Complete(recorder.NextShadowStageRequest);
        Observe(recorder, duration ? 11000 : 1016);
        Assert.Equal(default, recorder.NextShadowStageRequest);
        Complete(Request(99), 99);
        using var report = await Finish(recorder);
        Assert.Equal(duration ? "duration" : "capacity", report.RootElement.GetProperty("stopReason").GetString());
        Assert.Equal(7, Count(Stage(report)));
    }

    [Theory]
    [InlineData("epoch", ShadowStageProbeStatus.IdentityMismatch)]
    [InlineData("sequence", ShadowStageProbeStatus.IdentityMismatch)]
    [InlineData("mode", ShadowStageProbeStatus.ModeMismatch)]
    [InlineData("duplicate", ShadowStageProbeStatus.DuplicateProducer)]
    [InlineData("acknowledgement", ShadowStageProbeStatus.IncompleteCallback)]
    [InlineData("clock", ShadowStageProbeStatus.InvalidClock)]
    public async Task Wrong_epoch_sequence_mode_or_duplicate_producer_invalidates_stage_coverage(string dimension, ShadowStageProbeStatus expected)
    {
        using var recorder = Start(mode: dimension == "clock" ? ShadowStageProbeMode.Timings : ShadowStageProbeMode.Counts);
        var request = recorder.NextShadowStageRequest;
        _frame.BeginShadowStageCallback(request);
        var payloadRequest = dimension switch
        {
            "epoch" => request with { Epoch = Guid.NewGuid() },
            "sequence" => request with { Sequence = 8 },
            "mode" => request with { Mode = ShadowStageProbeMode.Timings },
            _ => request,
        };
        var payload = Payload(payloadRequest);
        if (dimension == "clock") payload = payload with { Timings = payload.Timings!.Value with { Frequency = 10000000 } };
        _frame.PublishShadowStageSnapshot(payload);
        if (dimension == "duplicate") _frame.PublishShadowStageSnapshot(payload);
        if (dimension != "acknowledgement") _frame.CompleteShadowStageCallback();
        Observe(recorder, 1016);
        using var report = await Finish(recorder);
        Status(Stage(report), expected);
        Assert.Equal(62.5, report.RootElement.GetProperty("summary").GetProperty("framesPerSecond").GetDouble());
        Assert.Equal(0, report.RootElement.GetProperty("shadowStages").GetProperty("validRows").GetInt32());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Suppressed_or_missing_scene_rows_are_explicit_and_never_inherit_old_counts(bool suppressed)
    {
        using var recorder = Start(2);
        Complete(recorder.NextShadowStageRequest);
        Observe(recorder, 1016, suppressed);
        _frame.BeginShadowStageCallback(recorder.NextShadowStageRequest);
        _frame.CompleteShadowStageCallback();
        Observe(recorder, 1032);
        using var report = await Finish(recorder);
        Status(Stage(report, 1), suppressed ? ShadowStageProbeStatus.NotRendered : ShadowStageProbeStatus.MissingProducer);
        Assert.Equal(JsonValueKind.Null, Stage(report, 1).GetProperty("callback").GetProperty("snapshot").ValueKind);
        Assert.Equal(1, report.RootElement.GetProperty("shadowStages").GetProperty("validRows").GetInt32());
    }

    [Theory]
    [InlineData(ShadowStageProbeMode.Off, 0)]
    [InlineData(ShadowStageProbeMode.Counts, 1)]
    [InlineData(ShadowStageProbeMode.Timings, 2)]
    public async Task Modes_and_configuration_changes_serialize_truthfully(ShadowStageProbeMode mode, int numeric)
    {
        using var recorder = Start(2, mode);
        Complete(recorder.NextShadowStageRequest);
        Observe(recorder, 1016);
        Complete(recorder.NextShadowStageRequest, configuration: new ShadowStageProbeConfiguration { HudTiming = true });
        Observe(recorder, 1032);
        using var report = await Finish(recorder);
        var snapshot = Stage(report).GetProperty("callback").GetProperty("snapshot");
        Assert.Equal(numeric, snapshot.GetProperty("request").GetProperty("mode").GetInt32());
        Assert.Equal(mode == ShadowStageProbeMode.Off, snapshot.GetProperty("counters").ValueKind == JsonValueKind.Null);
        Assert.Equal(mode != ShadowStageProbeMode.Timings, snapshot.GetProperty("timings").ValueKind == JsonValueKind.Null);
        if (mode != ShadowStageProbeMode.Off) Status(Stage(report, 1), ShadowStageProbeStatus.ConfigurationChanged);
    }

    [Fact]
    public async Task No_valid_stage_rows_still_publishes_an_invalid_report_with_no_divide_by_zero()
    {
        using var recorder = Start();
        Observe(recorder, 1016);
        using var report = await Finish(recorder);
        var summary = report.RootElement.GetProperty("shadowStages");
        Assert.Equal(0, summary.GetProperty("validRows").GetInt32());
        Assert.Equal(1, summary.GetProperty("invalidRows").GetInt32());
        Assert.False(summary.GetProperty("comparable").GetBoolean());
        Assert.Equal(JsonValueKind.Null, summary.GetProperty("meanScenePrefixMs").ValueKind);
    }

    [Fact]
    public async Task Unsupported_gpu_counters_do_not_erase_valid_stage_payloads()
    {
        using var recorder = Start();
        Complete(recorder.NextShadowStageRequest);
        Observe(recorder, 1016);
        using var report = await Finish(recorder);
        Assert.Equal(JsonValueKind.Null, report.RootElement.GetProperty("summary").GetProperty("counterDelta").ValueKind);
        Status(Stage(report), ShadowStageProbeStatus.Valid);
        Assert.Equal(7, Count(Stage(report)));
    }
}
