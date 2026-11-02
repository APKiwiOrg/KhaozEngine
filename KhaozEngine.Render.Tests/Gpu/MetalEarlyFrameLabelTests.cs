using System;
using System.Linq;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class MetalEarlyFrameLabelTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Labels_and_transfers_follow_the_tail_and_both_timing_slots_retire_even_after_stop(bool samplingAtCompletion)
    {
        using var timing = new MetalPassTimingCapture(new PassTimingTestNative(), slots: 2, samples: 16);
        IntPtr prefix = new(1), tail = new(2);
        timing.Boundary(72, 72);
        timing.ObservePass(prefix, 72, PassLabelEventKind.Begin, "outer", 3);
        timing.Begin(MetalEncoderKind.Blit, prefix, default, 72, true);
        timing.ContinueFrame(72, prefix, tail);
        IntPtr encoder = timing.Begin(MetalEncoderKind.Blit, tail, default, 72, true);
        timing.Transfer(encoder, 72, "copyTexture", 64, new(9));
        timing.ObservePass(tail, 72, PassLabelEventKind.End, "outer", 3);
        timing.Complete(prefix, samplingAtCompletion);
        Assert.Equal(1, timing.Snapshot().PendingCommandBuffers);
        timing.Complete(tail, samplingAtCompletion);
        Assert.Equal(0, timing.Snapshot().PendingCommandBuffers);
        var labels = timing.LabelsSnapshot();
        Assert.Equal(0, labels.UnbalancedEnds);
        Assert.Equal(0, labels.AbandonedScopes);
        var transfer = Assert.Single(timing.TransfersSnapshot().Records);
        Assert.Equal(tail.ToInt64(), transfer.CommandBuffer);
        Assert.Equal(3, transfer.LabelOccurrence);
        if (samplingAtCompletion)
        {
            var encoders = timing.Snapshot().Encoders.Records;
            Assert.Equal(2, encoders.Length);
            Assert.Equal(new[] { prefix.ToInt64(), tail.ToInt64() }, encoders.Select(e => e.CommandBuffer));
            Assert.All(encoders, e => Assert.Equal(3, e.LabelOccurrence));
        }
        else Assert.Empty(timing.Snapshot().Encoders.Records);
    }
}
