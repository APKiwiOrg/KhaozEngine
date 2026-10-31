using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using KhaozEngine.Windowing;
using Xunit;
using static KhaozEngine.Tests.Windowing.ShadowStageProbeFrameTests;

namespace KhaozEngine.Tests.Windowing;

public sealed class PointShadowAttributionRecorderTests : IDisposable
{
    readonly string _directory = Path.Combine(Path.GetTempPath(), "khaozengine-point-attribution-" + Guid.NewGuid());
    string Pathname => Path.Combine(_directory, "result.json");
    static FrameDiagnosticsWindowState Window => new(true, false, false, 32, 24, 0, false);
    public PointShadowAttributionRecorderTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);

    [Theory]
    [InlineData("true")]
    [InlineData(" 1")]
    [InlineData("2")]
    public void Noncanonical_setting_is_refused(string setting)
        => Assert.Throws<ArgumentException>(() => FrameDiagnosticsRecorder.FromSettings(Pathname, "counts", attribution: setting));

    [Fact]
    public async Task Recorder_owns_frozen_schema_six_tables_and_request_wire_shape_stays_unchanged()
    {
        using var recorder = new FrameDiagnosticsRecorder(Pathname, 1000, 1, ShadowStageProbeMode.Counts, pointAttribution: true);
        Assert.Equal(default, recorder.NextShadowStageRequest);
        Assert.Equal(Guid.Empty, recorder.NextShadowStageRequest.Epoch);
        Assert.Equal(0, recorder.NextShadowStageRequest.Sequence);
        Assert.Equal(ShadowStageProbeMode.Off, recorder.NextShadowStageRequest.Mode);
        File.WriteAllText(Pathname + ".start", "");
        recorder.Observe(.016, 0, Window, default);
        recorder.Observe(.016, 1000, Window, default);
        var request = recorder.NextShadowStageRequest;
        var capture = Assert.IsType<PointShadowAttributionCapture>(request.PointAttribution);
        Assert.True(capture.BeginFrame(request.Epoch, request.Sequence, 77));
        capture.AddRow(new() { Key = 39 });
        var range = capture.EndFrame(PointAttributionAvailability.MissingAtlas);
        var snapshot = Payload(request) with { PointAttribution = range };
        recorder.Observe(.016, 1016, Window, default, new(request, true, 1, snapshot));
        Assert.True(capture.Frozen);
        Assert.False(capture.BeginFrame(request.Epoch, 2, 78));
        await recorder.Publication;
        Assert.Null(recorder.Failure);
        using var json = JsonDocument.Parse(File.ReadAllText(Pathname));
        Assert.Equal(6, json.RootElement.GetProperty("schemaVersion").GetInt32());
        var table = json.RootElement.GetProperty("pointShadowAttribution");
        Assert.Equal(39, table.GetProperty("rows")[0].GetProperty("key").GetInt64());
        Assert.Equal(77, table.GetProperty("frames")[0].GetProperty("sceneFrame").GetInt64());
        Assert.Equal(3, json.RootElement.GetProperty("samples")[0].GetProperty("shadowStage").GetProperty("request").EnumerateObject().Count());
    }

    [Fact]
    public void Attribution_is_preallocated_at_recorder_creation_and_new_epochs_do_not_share_tables()
    {
        using var a = FrameDiagnosticsRecorder.FromSettings(Pathname, "counts", attribution: "1")!;
        using var b = FrameDiagnosticsRecorder.FromSettings(Path.Combine(_directory, "other.json"), "counts", attribution: "1")!;
        File.WriteAllText(Pathname + ".start", "");
        File.WriteAllText(Path.Combine(_directory, "other.json.start"), "");
        a.Observe(.016, 0, Window, default); b.Observe(.016, 0, Window, default);
        a.Observe(.016, 1000, Window, default); b.Observe(.016, 1000, Window, default);
        var first = a.NextShadowStageRequest; var second = b.NextShadowStageRequest;
        Assert.NotNull(first.PointAttribution); Assert.NotNull(second.PointAttribution);
        Assert.NotSame(first.PointAttribution, second.PointAttribution);
        Assert.False(first.PointAttribution!.BeginFrame(second.Epoch, 1, 1));
    }
}
