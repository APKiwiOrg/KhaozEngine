using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using KhaozEngine.Windowing;
using Xunit;
using static KhaozEngine.Tests.Windowing.ShadowStageProbeFrameTests;

namespace KhaozEngine.Tests.Windowing;

public sealed class PointShadowAttributionRecorderControlTests : IDisposable
{
    readonly string _directory = Path.Combine(Path.GetTempPath(), "khaozengine-point-control-" + Guid.NewGuid());
    string Pathname => Path.Combine(_directory, "report.json");
    static FrameDiagnosticsWindowState Window => new(true, false, false, 32, 24, 0, false);
    public PointShadowAttributionRecorderControlTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);

    FrameDiagnosticsRecorder Start(Frame frame)
    {
        var recorder = new FrameDiagnosticsRecorder(Pathname, 1000, 1, ShadowStageProbeMode.Counts, pointAttribution: true);
        AppWindow.BeginShadowStageCallback(frame, recorder);
        Assert.Equal(default, frame.ShadowStageRequest); Assert.Null(frame.ShadowStagePointAttribution);
        File.WriteAllText(Pathname + ".start", "");
        recorder.Observe(.016, 0, Window, default);
        AppWindow.BeginShadowStageCallback(frame, recorder);
        Assert.Equal(default, frame.ShadowStageRequest); Assert.Null(frame.ShadowStagePointAttribution);
        recorder.Observe(.016, 1000, Window, default);
        AppWindow.BeginShadowStageCallback(frame, recorder);
        Assert.Same(recorder.NextPointAttribution, frame.ShadowStagePointAttribution);
        Assert.NotNull(frame.ShadowStagePointAttribution);
        return recorder;
    }

    [Theory]
    [InlineData("correct", false)]
    [InlineData("correct", true)]
    [InlineData("missing-seed", true)]
    [InlineData("foreign-seed", false)]
    [InlineData("foreign-seed", true)]
    [InlineData("missing-completion", true)]
    [InlineData("foreign-completion", true)]
    [InlineData("missing-producer", true)]
    [InlineData("foreign-producer", false)]
    public async Task Recorder_validates_transient_ownership_including_null_overflow_ranges(string fault, bool overflow)
    {
        var frame = new Frame();
        using var recorder = Start(frame);
        var request = frame.ShadowStageRequest;
        var owner = frame.ShadowStagePointAttribution!;
        var other = new PointShadowAttributionCapture(owner.Epoch, new(2, 2, 4, 4, 8, 4, 4, 4));
        PointAttributionFrame? range;
        if (overflow)
        {
            Assert.False(owner.CheckMotionKeyCapacity(request.Epoch, request.Sequence, int.MaxValue));
            range = null;
        }
        else
        {
            Assert.True(owner.BeginFrame(request.Epoch, request.Sequence, 1));
            range = owner.EndFrame(PointAttributionAvailability.Empty);
            Assert.True(other.BeginFrame(request.Epoch, request.Sequence, 1));
            Assert.Equal(range.Value, other.EndFrame(PointAttributionAvailability.Empty));
        }
        var seed = fault == "missing-seed" ? null : fault == "foreign-seed" ? other : owner;
        frame.BeginShadowStageCallback(request, seed);
        var producer = fault == "missing-producer" ? null : fault is "foreign-producer" or "foreign-seed" ? other : seed;
        frame.PublishShadowStageSnapshot(Payload(request) with { PointAttribution = range }, producer);
        frame.CompleteShadowStageCallback();
        var completion = fault == "missing-completion" ? null : fault == "foreign-completion" ? other : frame.ShadowStagePointAttribution;
        recorder.Observe(.016, 1016, Window, default, frame.CompletedShadowStageCallback, completedPointAttribution: completion);
        await recorder.Publication;
        Assert.True(owner.Frozen); Assert.Null(recorder.NextPointAttribution); Assert.Null(recorder.Failure);
        using var json = JsonDocument.Parse(File.ReadAllText(Pathname));
        var stage = json.RootElement.GetProperty("samples")[0].GetProperty("shadowStage");
        Assert.Equal((int)(fault == "correct" ? ShadowStageProbeStatus.Valid : ShadowStageProbeStatus.InvalidInputs), stage.GetProperty("status").GetInt32());
        AppWindow.BeginShadowStageCallback(frame, recorder);
        Assert.Equal(default, frame.ShadowStageRequest); Assert.Null(frame.ShadowStagePointAttribution);
    }
}
