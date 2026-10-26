using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using KhaozEngine.Gpu;
using KhaozEngine.Windowing;
using Xunit;
using static KhaozEngine.Tests.Windowing.RecordingWaitProbeTestData;

namespace KhaozEngine.Tests.Windowing;

public sealed partial class RecordingWaitProbeRecorderTests : IDisposable
{
    readonly string _directory = Path.Combine("/tmp/khaozengine-mac-frame-performance/ring-wait-probe/implementation", "recorder-" + Guid.NewGuid().ToString("N"));
    string Pathname => Path.Combine(_directory, "result.json");
    public RecordingWaitProbeRecorderTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);

    [Theory]
    [InlineData(null, false)]
    [InlineData("0", false)]
    [InlineData("1", true)]
    public void Once_parsed_switch_accepts_only_unset_zero_or_one(string? value, bool enabled)
        => Assert.Equal(enabled, FrameDiagnosticsRecorder.ParseRecordingWaitProbe(value));

    [Theory]
    [InlineData("")]
    [InlineData("true")]
    [InlineData("2")]
    [InlineData(" 1")]
    public void Malformed_switch_is_a_configuration_error(string value)
        => Assert.Throws<ArgumentException>(() => FrameDiagnosticsRecorder.ParseRecordingWaitProbe(value));

    [Fact]
    public void Enabled_probe_requires_shadow_mode_only_when_a_recorder_is_configured()
    {
        Assert.Null(FrameDiagnosticsRecorder.FromSettings(null, null, "1"));
        Assert.Throws<ArgumentException>(() => FrameDiagnosticsRecorder.FromSettings(Pathname, "off", "1"));
        using var counts = FrameDiagnosticsRecorder.FromSettings(Pathname, "counts", "1");
        using var timings = FrameDiagnosticsRecorder.FromSettings(Pathname, "timings", "1");
        Assert.NotNull(counts); Assert.NotNull(timings);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task First_and_last_callback_and_both_boundaries_are_owned(bool duration)
    {
        using var recorder = new FrameDiagnosticsRecorder(Pathname, 1000, duration ? 10 : 2, ShadowStageProbeMode.Counts, true);
        Assert.Equal(default, recorder.NextRecordingWaitRequest);
        File.WriteAllText(Pathname + ".start", "");
        recorder.Observe(.016, 0, Window(), default);
        Assert.Equal(default, recorder.NextRecordingWaitRequest);
        recorder.Observe(.016, 1000, Window(), default, default, default, Boundary());
        var first = recorder.NextRecordingWaitRequest;
        Assert.Equal(recorder.NextShadowStageRequest.Epoch, first.Epoch);
        Assert.Equal(1, first.Sequence); Assert.True(first.Enabled);
        recorder.Observe(.016, 1016, Window(1), default, default, Callback(first), Boundary(2, 12));
        var last = recorder.NextRecordingWaitRequest;
        recorder.Observe(.016, duration ? 11000 : 1032, Window(2), default, default, Callback(last), Boundary(4, 24));
        Assert.Equal(default, recorder.NextRecordingWaitRequest);
        await recorder.Publication; Assert.Null(recorder.Failure);
        using var json = JsonDocument.Parse(File.ReadAllText(Pathname));
        var report = json.RootElement;
        Assert.Equal(4, report.GetProperty("schemaVersion").GetInt32());
        var rows = report.GetProperty("samples");
        Assert.Equal(1, rows[0].GetProperty("recordingWait").GetProperty("callback").GetProperty("request").GetProperty("sequence").GetInt64());
        Assert.Equal(2, rows[1].GetProperty("recordingWait").GetProperty("callback").GetProperty("request").GetProperty("sequence").GetInt64());
        var summary = report.GetProperty("recordingWaits");
        Assert.True(summary.GetProperty("fullyAttributed").GetBoolean());
        Assert.Equal(2, summary.GetProperty("validRoleRows").GetInt32());
        Assert.Equal(3, summary.GetProperty("availableBoundaries").GetInt32());
        Assert.Equal(10, summary.GetProperty("prefix").GetProperty("ticks").GetInt64());
        Assert.Equal(14, summary.GetProperty("main").GetProperty("ticks").GetInt64());
    }

    [Theory]
    [InlineData("epoch", RecordingWaitProbeStatus.IdentityMismatch)]
    [InlineData("sequence", RecordingWaitProbeStatus.IdentityMismatch)]
    [InlineData("mode", RecordingWaitProbeStatus.ModeChanged)]
    [InlineData("declaration", RecordingWaitProbeStatus.DuplicateDeclaration)]
    [InlineData("missing-declaration", RecordingWaitProbeStatus.MissingDeclaration)]
    [InlineData("role", RecordingWaitProbeStatus.DuplicateRole)]
    [InlineData("missing-main", RecordingWaitProbeStatus.MissingRole)]
    [InlineData("missing-prefix", RecordingWaitProbeStatus.MissingRole)]
    [InlineData("ack", RecordingWaitProbeStatus.AcknowledgementCardinality)]
    [InlineData("missing-ack", RecordingWaitProbeStatus.IncompleteCallback)]
    [InlineData("device", RecordingWaitProbeStatus.InvalidReceipt)]
    [InlineData("clock", RecordingWaitProbeStatus.InvalidReceipt)]
    [InlineData("depth", RecordingWaitProbeStatus.InvalidReceipt)]
    [InlineData("attempt", RecordingWaitProbeStatus.InvalidReceipt)]
    [InlineData("same-list", RecordingWaitProbeStatus.InvalidReceipt)]
    [InlineData("same-ordinal", RecordingWaitProbeStatus.InvalidReceipt)]
    [InlineData("negative", RecordingWaitProbeStatus.InvalidReceipt)]
    [InlineData("zero-inconsistent", RecordingWaitProbeStatus.InvalidReceipt)]
    [InlineData("pairing", RecordingWaitProbeStatus.PairingMismatch)]
    public void Corrupt_callback_fields_cannot_be_accepted(string corrupt, RecordingWaitProbeStatus status)
    {
        var request = Request(); var c = Callback(request);
        c = corrupt switch
        {
            "epoch" => c with { Request = request with { Epoch = Guid.NewGuid() } },
            "sequence" => c with { Request = request with { Sequence = 2 } },
            "mode" => c with { Request = request with { Mode = ShadowStageProbeMode.Timings } },
            "declaration" => c with { DeclarationCount = 2 },
            "missing-declaration" => c with { DeclarationCount = 0 },
            "role" => c with { MainObservations = 2 },
            "missing-main" => c with { MainObservations = 0 },
            "missing-prefix" => c with { PrefixObservations = 0 },
            "ack" => c with { AcknowledgementCount = 2 },
            "missing-ack" => c with { AcknowledgementCount = 0 },
            _ => c,
        };
        var main = c.Main.Receipt!.Value;
        main = corrupt switch
        {
            "device" => main with { DeviceIdentity = Guid.NewGuid() },
            "clock" => main with { Frequency = 0 },
            "depth" => main with { Depth = 3 },
            "attempt" => main with { Attempt = 0 },
            "same-list" => main with { ListIdentity = PrefixList },
            "same-ordinal" => main with { RecordingOrdinal = 1 },
            "negative" => main with { Ticks = -1 },
            "zero-inconsistent" => main with { Count = 0 },
            _ => main,
        };
        c = c with { Main = c.Main with { Receipt = main } };
        Assert.Equal(status, RecordingWaitProbeAccounting.Validate(request, c, false, corrupt != "pairing"));
    }

    [Fact]
    public void Suppression_legacy_and_observed_zero_keep_distinct_states()
    {
        Assert.Equal(RecordingWaitProbeStatus.NotRendered, RecordingWaitProbeAccounting.Validate(Request(), default, true));
        var legacy = Callback(Request(), false, true);
        Assert.Equal(RecordingWaitObservationStatus.NotApplicable, legacy.Prefix.Status);
        Assert.Equal(RecordingWaitProbeStatus.Valid, RecordingWaitProbeAccounting.Validate(Request(), legacy, false));
        Assert.Equal(0, legacy.Main.Receipt!.Value.Ticks);
    }
}
