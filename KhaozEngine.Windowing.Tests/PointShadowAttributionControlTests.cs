using System;
using KhaozEngine.Windowing;
using Xunit;
using static KhaozEngine.Tests.Windowing.ShadowStageProbeFrameTests;

namespace KhaozEngine.Tests.Windowing;

public sealed class PointShadowAttributionControlTests
{
    static PointShadowAttributionCapture Capture(Guid epoch) => new(epoch, new(4, 4, 8, 8, 16, 8, 4, 8));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Missing_or_same_epoch_foreign_producer_is_rejected_before_value_storage(bool foreign)
    {
        var owner = Capture(Epoch);
        var other = Capture(Epoch);
        Assert.True(owner.BeginFrame(Epoch, 1, 1));
        Assert.True(other.BeginFrame(Epoch, 1, 1));
        var expected = owner.EndFrame(PointAttributionAvailability.Empty);
        Assert.Equal(expected, other.EndFrame(PointAttributionAvailability.Empty));
        var frame = new Frame();
        frame.BeginShadowStageCallback(Request(), owner);
        var snapshot = Payload(Request()) with { PointAttribution = expected };
        frame.PublishShadowStageSnapshot(snapshot, foreign ? other : null);
        frame.CompleteShadowStageCallback();
        Assert.Equal(ShadowStageProbeStatus.InvalidInputs, frame.CompletedShadowStageCallback.Snapshot!.Value.Status);
    }

    [Fact]
    public void Callback_control_is_transient_and_the_first_producer_evidence_survives_duplicates_and_late_publication()
    {
        var capture = Capture(Epoch);
        var frame = new Frame();
        frame.BeginShadowStageCallback(Request(), capture);
        Assert.Same(capture, frame.ShadowStagePointAttribution);
        frame.PublishShadowStageSnapshot(Payload(Request()), capture);
        frame.PublishShadowStageSnapshot(Payload(Request(), 99), capture);
        frame.CompleteShadowStageCallback();
        frame.PublishShadowStageSnapshot(Payload(Request(), 123), capture);
        var saved = frame.CompletedShadowStageCallback;
        Assert.Equal(3, saved.ProducerCount);
        Assert.Equal(7, saved.Snapshot!.Value.Counters!.Value.StaticRequests);
        frame.BeginShadowStageCallback(Request(2));
        Assert.Null(frame.ShadowStagePointAttribution);
        Assert.Null(frame.CompletedShadowStageCallback.Snapshot);
        Assert.Equal(7, saved.Snapshot.Value.Counters!.Value.StaticRequests);
    }
}
