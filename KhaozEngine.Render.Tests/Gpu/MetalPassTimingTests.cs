using System;
using System.Linq;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class MetalPassTimingTests
{
    [Fact]
    public void Sampling_blocks_clear_reused_descriptors_and_preserve_untimed_factories()
    {
        var native = new PassTimingTestNative();
        using var timing = new MetalPassTimingCapture(native, 1, 8, 2);
        timing.Begin(MetalEncoderKind.Render, new(1), new(7), 0, false);
        Assert.False(native.Attached);
        timing.Boundary(0, 10);
        timing.Begin(MetalEncoderKind.Render, new(1), new(7), 0, true);
        Assert.True(native.TimedFactory);
        Assert.False(native.Attached);
        timing.Complete(new(1), true);
        timing.Boundary(72, 20);
        timing.Begin(MetalEncoderKind.Compute, new(2), default, 72, true);
        Assert.False(native.TimedFactory);
        Assert.Equal(IntPtr.Zero, native.OpenDescriptor);
        timing.Begin(MetalEncoderKind.Render, new(3), new(7), 72, true);
        Assert.False(native.Attached);
        timing.Boundary(144, 30);
        timing.Begin(MetalEncoderKind.Blit, new(4), default, 144, true);
        Assert.True(native.TimedFactory);
        timing.Complete(new(4), true);
        timing.End(145, 40);
        var result = timing.Snapshot();
        Assert.Equal(new[] { true, false, true }, result.Blocks.Select(b => b.Timed));
        Assert.Equal(4, result.ClockPairs.Records.Length);
        Assert.Equal(2, result.Encoders.Records.Length);
        Assert.Equal(3, result.UntimedEncoders);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(2)]
    public void Failed_encoder_creation_clears_attachments(int kind)
    {
        var native = new PassTimingTestNative { ThrowOnOpen = true };
        Assert.Throws<InvalidOperationException>(() => MetalPassTimingEncoder.Begin(native, (MetalEncoderKind)kind, new(1), new(7), new(new(8), new(9), new(10)), 0));
        Assert.False(native.Attached);
    }

    [Fact]
    public void Sample_buffer_creation_failure_disables_only_pass_timing()
    {
        using var timing = new MetalPassTimingCapture(new PassTimingTestNative { CreationFailure = true });
        Assert.False(timing.Snapshot().Supported);
        Assert.NotEmpty(timing.Snapshot().Reason!);
        Assert.Equal(0, timing.Snapshot().SlotCount);
    }

    [Fact]
    public void Exhaustion_and_capacity_skip_without_overwriting_in_flight_samples()
    {
        var native = new PassTimingTestNative();
        using var timing = new MetalPassTimingCapture(native, 1, 4, 2);
        timing.Boundary(0, 10);
        timing.Begin(MetalEncoderKind.Render, new(1), new(7), 0, true);
        timing.Begin(MetalEncoderKind.Blit, new(1), default, 0, true);
        timing.Begin(MetalEncoderKind.Blit, new(2), default, 0, true);
        timing.Begin(MetalEncoderKind.Compute, new(2), default, 0, true);
        timing.Complete(new(2), true);
        timing.Complete(new(1), true);
        timing.Begin(MetalEncoderKind.Blit, new(3), default, 0, true);
        timing.Complete(new(3), true);
        var result = timing.Snapshot();
        Assert.Equal(3, result.UntimedEncoders);
        Assert.Equal(1, result.SkippedCommandBuffers);
        Assert.Equal(1, result.CapacityOverflow);
        Assert.Equal(new long[] { 1, 3 }, result.Encoders.Records.Select(e => e.CommandBuffer));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Resolve_failures_and_counter_error_values_are_missing_evidence(bool failure)
    {
        var native = new PassTimingTestNative { ResolveFailure = failure, ErrorValue = !failure };
        using var timing = new MetalPassTimingCapture(native, 1, 8, 2);
        timing.Boundary(0, 10);
        timing.Begin(MetalEncoderKind.Render, new(1), new(7), 0, true);
        timing.Complete(new(1), true);
        var result = timing.Snapshot();
        var encoder = Assert.Single(result.Encoders.Records);
        Assert.Null(encoder.StartVertex);
        Assert.Equal(failure ? "resolveFailure" : "missing", encoder.Status);
        Assert.Equal(failure ? 1 : 0, result.ResolveFailures);
        Assert.Equal(failure ? 0 : 1, result.ErrorValues);
        // A failed resolve must also return its slot to the pool.
        timing.Begin(MetalEncoderKind.Blit, new(2), default, 0, true);
        timing.Complete(new(2), true);
        Assert.Equal(0, timing.Snapshot().SkippedCommandBuffers);
    }

    [Fact]
    public void Signature_dedup_is_bounded_and_disabled_support_keeps_the_ordinary_path()
    {
        var native = new PassTimingTestNative();
        using var timing = new MetalPassTimingCapture(native, 1, 16, 1);
        timing.Boundary(0, 10);
        timing.Begin(MetalEncoderKind.Render, new(1), new(7), 0, true);
        timing.Begin(MetalEncoderKind.Render, new(1), new(7), 0, true);
        timing.Begin(MetalEncoderKind.Render, new(1), new(8), 0, true);
        timing.Complete(new(1), true);
        var result = timing.Snapshot();
        Assert.Single(result.Signatures);
        Assert.Equal(new[] { 0, 0, -1 }, result.Encoders.Records.Select(e => e.SignatureId));
        Assert.Equal(1, result.SignatureOverflow);
        using var unsupported = new MetalPassTimingCapture(new PassTimingTestNative { Supported = false });
        unsupported.Boundary(0, 10);
        unsupported.Begin(MetalEncoderKind.Compute, new(9), default, 0, true);
        Assert.False(unsupported.Snapshot().Supported);
        Assert.NotEmpty(unsupported.Snapshot().Reason!);
        Assert.Empty(unsupported.Snapshot().Encoders.Records);
    }
}
