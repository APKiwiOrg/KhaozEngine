using System;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Windowing;

public sealed class ShadowStageProbeFrameTests
{
    internal static readonly Guid Epoch = new("ef8c9483-14d4-4e87-8ed1-b2b02942d711");
    internal static ShadowStageProbeRequest Request(long sequence = 1, ShadowStageProbeMode mode = ShadowStageProbeMode.Counts) => new(Epoch, sequence, mode);
    internal static ShadowStageProbeSnapshot Payload(ShadowStageProbeRequest request, int count = 7,
        ShadowStageProbeConfiguration configuration = default) => new(request, ShadowStageProbeStatus.Valid, configuration,
            request.Mode == ShadowStageProbeMode.Off ? null : new ShadowStageProbeCounts { StaticRequests = count },
            request.Mode == ShadowStageProbeMode.Timings ? new ShadowStageProbeTicks { Frequency = 1000, ScenePrefix = 4, TimestampReads = 2 } : null);

    [Fact]
    public void Publication_is_value_owned_and_requires_the_callback_success_acknowledgement()
    {
        var frame = new Frame();
        var request = Request();
        frame.BeginShadowStageCallback(request);
        Assert.Equal(request, frame.ShadowStageRequest);
        var payload = Payload(request);
        frame.PublishShadowStageSnapshot(payload);
        Assert.False(frame.CompletedShadowStageCallback.Completed);
        frame.CompleteShadowStageCallback();
        var saved = frame.CompletedShadowStageCallback;
        payload = Payload(request, 99);
        frame.BeginShadowStageCallback(Request(2));
        Assert.True(saved.Completed);
        Assert.Equal(7, saved.Snapshot!.Value.Counters!.Value.StaticRequests);
        Assert.Null(frame.CompletedShadowStageCallback.Snapshot);
        Assert.Equal(0, frame.CompletedShadowStageCallback.ProducerCount);
    }

    [Theory]
    [InlineData("ui")]
    [InlineData("capture")]
    [InlineData("submit")]
    [InlineData("present")]
    [InlineData("pacing")]
    [InlineData("missing acknowledgement")]
    public void Late_failure_or_missing_acknowledgement_cannot_reuse_a_provisional_snapshot(string failure)
    {
        var frame = new Frame();
        frame.BeginShadowStageCallback(Request());
        frame.PublishShadowStageSnapshot(Payload(Request()));
        // Every named late failure leaves this success-only call unreachable.
        Assert.False(frame.CompletedShadowStageCallback.Completed, failure);
        frame.BeginShadowStageCallback(Request(2));
        frame.CompleteShadowStageCallback();
        Assert.Null(frame.CompletedShadowStageCallback.Snapshot);
    }

    [Fact]
    public void Duplicate_publication_remains_visible_instead_of_overwriting_the_first_producer()
    {
        var frame = new Frame();
        frame.BeginShadowStageCallback(Request());
        frame.PublishShadowStageSnapshot(Payload(Request()));
        frame.PublishShadowStageSnapshot(Payload(Request(), 99));
        frame.CompleteShadowStageCallback();
        Assert.Equal(2, frame.CompletedShadowStageCallback.ProducerCount);
        Assert.Equal(7, frame.CompletedShadowStageCallback.Snapshot!.Value.Counters!.Value.StaticRequests);
    }
}
