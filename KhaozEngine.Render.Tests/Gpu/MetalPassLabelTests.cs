using System;
using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class MetalPassLabelTests
{
    static readonly IntPtr Buffer = new(1);

    [Fact]
    public void Labels_intern_to_stable_ids_and_a_full_table_counts_overflow_without_losing_the_directional_id()
    {
        using var timing = new MetalPassTimingCapture(new PassTimingTestNative(), 1, 8, 2, labels: 2, labelEvents: 16);
        timing.ObservePass(Buffer, 0, PassLabelEventKind.Begin, "a", 0);
        timing.ObservePass(Buffer, 0, PassLabelEventKind.End, "a", 0);
        timing.ObservePass(Buffer, 0, PassLabelEventKind.Skip, "b", 0);
        timing.ObservePass(Buffer, 0, PassLabelEventKind.Skip, MetalPassLabels.DirectionalPass, 0);
        var result = timing.LabelsSnapshot();
        Assert.Equal(new[] { MetalPassLabels.DirectionalPass, "a" }, result.Labels);
        Assert.Equal(new[] { 1, 1, -1, MetalPassLabels.DirectionalLabel }, result.Events.Records.Select(e => e.LabelId));
        Assert.Equal(1, result.LabelOverflow);
    }

    [Fact]
    public void Event_stream_keeps_order_and_names_each_encoder_with_its_innermost_open_label()
    {
        var native = new PassTimingTestNative();
        using var timing = new MetalPassTimingCapture(native, 1, 32, 2, labelEvents: 16);
        timing.Boundary(0, 10);
        timing.ObservePass(Buffer, 0, PassLabelEventKind.Begin, "outer", 2);
        timing.Begin(MetalEncoderKind.Blit, Buffer, default, 0, true);
        timing.ObservePass(Buffer, 0, PassLabelEventKind.Begin, "inner", 5);
        timing.ObservePass(Buffer, 0, PassLabelEventKind.Skip, "skipped", 1);
        timing.Begin(MetalEncoderKind.Render, Buffer, new(7), 0, true);
        timing.ObservePass(Buffer, 0, PassLabelEventKind.End, "inner", 5);
        timing.ObservePass(Buffer, 0, PassLabelEventKind.End, "outer", 2);
        timing.Begin(MetalEncoderKind.Compute, Buffer, default, 0, true);
        timing.Complete(Buffer, true);
        var labels = timing.LabelsSnapshot();
        Assert.Equal(new[] { "begin", "begin", "skip", "end", "end" }, labels.Events.Records.Select(e => e.Kind));
        Assert.Equal(new[] { 0, 1, 1, 2, 2 }, labels.Events.Records.Select(e => e.EncoderOrdinal));
        Assert.All(labels.Events.Records, e => Assert.Equal(Buffer.ToInt64(), e.CommandBuffer));
        var encoders = timing.Snapshot().Encoders.Records;
        Assert.Equal("outer", labels.Labels[encoders[0].LabelId]);
        Assert.Equal(2, encoders[0].LabelOccurrence);
        Assert.Equal("inner", labels.Labels[encoders[1].LabelId]);
        Assert.Equal(5, encoders[1].LabelOccurrence);
        Assert.Equal(-1, encoders[2].LabelId);
    }

    [Fact]
    public void Scope_stack_is_bounded_and_a_new_recording_forgets_scopes_left_open()
    {
        var native = new PassTimingTestNative();
        using var timing = new MetalPassTimingCapture(native, 1, 32, 2, labelEvents: 512);
        timing.Boundary(0, 10);
        for (int i = 0; i < 70; i++) timing.ObservePass(Buffer, 0, PassLabelEventKind.Begin, "deep", i);
        for (int i = 69; i >= 64; i--) timing.ObservePass(Buffer, 0, PassLabelEventKind.End, "deep", i);
        timing.ObservePass(Buffer, 0, PassLabelEventKind.End, "never-begun", 0);
        timing.ObservePass(Buffer, 1, PassLabelEventKind.End, "deep", 63);
        timing.Begin(MetalEncoderKind.Blit, Buffer, default, 1, true);
        timing.Complete(Buffer, true);
        var result = timing.LabelsSnapshot();
        Assert.Equal(6, result.StackOverflow);
        Assert.Equal(2, result.UnbalancedEnds);
        Assert.Equal(64, result.AbandonedScopes);
        Assert.Equal(-1, Assert.Single(timing.Snapshot().Encoders.Records).LabelId);
    }

    [Fact]
    public void Transfers_name_their_blit_encoder_and_label_and_other_encoders_are_ignored()
    {
        var native = new PassTimingTestNative();
        using var timing = new MetalPassTimingCapture(native, 1, 32, 2, labelEvents: 16, transfers: 2);
        timing.Boundary(0, 10);
        timing.ObservePass(Buffer, 0, PassLabelEventKind.Begin, "uploads", 3);
        timing.Begin(MetalEncoderKind.Render, Buffer, new(7), 0, true);
        native.OpenEncoder = new(2000);
        IntPtr blit = timing.Begin(MetalEncoderKind.Blit, Buffer, default, 0, true);
        timing.Transfer(blit, 0, PassTransferKind.BufferUpload, 64, new(11));
        timing.Transfer(new(9999), 0, PassTransferKind.BufferUpload, 8, new(12));
        timing.Transfer(blit, 1, PassTransferKind.BufferUpload, 8, new(13));
        timing.ObservePass(Buffer, 0, PassLabelEventKind.End, "uploads", 3);
        timing.Transfer(blit, 0, PassTransferKind.TextureToTexture, 16, new(14));
        timing.Transfer(blit, 0, PassTransferKind.TextureToBuffer, 32, new(15));
        var result = timing.TransfersSnapshot();
        Assert.Equal(1, result.Overflow);
        var upload = result.Records[0];
        Assert.Equal((PassTransferKind.BufferUpload, 64UL, 11L, 1, 0, Buffer.ToInt64(), 3),
            (upload.Kind, upload.Bytes, upload.Destination, upload.EncoderOrdinal, upload.CallbackIndex, upload.CommandBuffer, upload.LabelOccurrence));
        Assert.Equal("uploads", timing.LabelsSnapshot().Labels[upload.LabelId]);
        Assert.Equal(PassTransferKind.TextureToTexture, result.Records[1].Kind);
        Assert.Equal(-1, result.Records[1].LabelId);
    }

    [Theory]
    [InlineData((int)MetalTransferCase.TextureToTexture, PassTransferKind.TextureToTexture)]
    [InlineData((int)MetalTransferCase.TextureToBuffer, PassTransferKind.TextureToBuffer)]
    [InlineData((int)MetalTransferCase.BufferToTexture, PassTransferKind.BufferToTexture)]
    [InlineData((int)MetalTransferCase.BufferToBuffer, PassTransferKind.StagingToStaging)]
    public void Texture_copy_cases_report_their_own_kind(int copy, string kind)
        => Assert.Equal(kind, PassTransferKind.For((MetalTransferCase)copy));

    [Theory]
    [InlineData((int)GpuPixelFormat.R8G8B8A8UNorm, 128UL)]
    [InlineData(9999, 0UL)] // A format the staging table lacks reads as zero rather than failing the copy.
    public void Texture_copy_bytes_come_from_the_staging_layout(int format, ulong bytes)
        => Assert.Equal(bytes, PassTransferKind.TextureBytes((GpuPixelFormat)format, 8, 4));
}

[Collection("AllocSensitive")]
public sealed class MetalPassLabelAllocationTests
{
    [Fact]
    public void Warm_label_and_transfer_recording_allocates_nothing()
    {
        var native = new PassTimingTestNative();
        using var timing = new MetalPassTimingCapture(native, 1, 32, 2, labelEvents: 64, transfers: 16);
        IntPtr buffer = new(1);
        void Frame(int callback)
        {
            timing.ObservePass(buffer, callback, PassLabelEventKind.Begin, "a", 0);
            timing.ObservePass(buffer, callback, PassLabelEventKind.Skip, "b", 1);
            timing.Transfer(new(1000), callback, PassTransferKind.BufferUpload, 64, new(2));
            timing.ObservePass(buffer, callback, PassLabelEventKind.End, "a", 0);
        }
        Frame(0);
        long bytes;
        using (AllocationRegion.Start("pass label and transfer recording"))
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            Frame(1);
            bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        }
        Assert.Equal(0, bytes);
    }
}

[Collection("NativeDeviceLifecycle")]
public sealed class MetalPassLabelProbeTests
{
    [Fact]
    public void Only_the_frame_list_recording_on_the_frame_thread_feeds_labels_and_uploads()
    {
        using var harness = new MetalRingHarness();
        var calls = new FakeMetalEncoderCalls();
        var native = new PassTimingTestNative();
        var scope = new MetalEncoderScope(new FakeMetalEncoderSink(calls));
        scope.BeginRecording(new(44));
        using var arena = harness.NewArena();
        using var capture = new FrameTimingCapture(null, 8, new MetalPassTimingCapture(native, 1, 32, 2, labelEvents: 16, transfers: 8));
        var passes = capture.PassTiming!;
        Assert.False(MetalFrameTimingProbe.Sampling);
        MetalFrameTimingProbe.ObservePass(null!, PassLabelEventKind.Begin, "before", 0);
        Assert.Empty(passes.LabelsSnapshot().Events.Records);
        passes.Boundary(0, 10);
        capture.Start();
        // No frame list is registered, so this list is foreign and nothing reaches the stack.
        using var device = new FakeGpuDevice();
        MetalFrameTimingProbe.ObservePass(device.Factory.CreateCommandList(), PassLabelEventKind.Begin, "foreign", 0);
        var foreign = passes.LabelsSnapshot();
        Assert.Empty(foreign.Events.Records);
        Assert.Equal(1, foreign.ForeignEvents);
        // A staged upload through the real path, into the blit encoder this recording opened.
        native.OpenEncoder = scope.EnsureBlitEncoder();
        passes.ObservePass(new(44), 0, PassLabelEventKind.Begin, "uploads", 0);
        passes.Begin(MetalEncoderKind.Blit, new(44), default, 0, true);
        MetalBufferUpload.Record(null, 0, new(77), 64, 8, new byte[6], scope, arena, harness.Blit);
        capture.Stop();
        var upload = Assert.Single(passes.TransfersSnapshot().Records);
        Assert.Equal((PassTransferKind.BufferUpload, 8UL, 77L), (upload.Kind, upload.Bytes, upload.Destination));
        scope.EnsureNoEncoder();
    }

    [Fact]
    public async Task The_frame_list_binds_labels_and_encoders_to_its_recording_and_another_thread_is_foreign()
    {
        using var harness = new MetalRingHarness();
        var list = harness.NewList(new object(), calls: new FakeMetalEncoderCalls(), render: new FakeMetalRenderCalls());
        var native = new PassTimingTestNative();
        using var capture = new FrameTimingCapture(list, 8, new MetalPassTimingCapture(native, 1, 32, 2, labelEvents: 16));
        var passes = capture.PassTiming!;
        try
        {
            Assert.Equal(IntPtr.Zero, capture.FrameCommandBuffer);
            list.Begin();
            IntPtr buffer = capture.FrameCommandBuffer;
            Assert.NotEqual(IntPtr.Zero, buffer);
            passes.Boundary(0, 10);
            capture.Start();
            MetalFrameTimingProbe.BeginEncoder(MetalEncoderKind.Blit, buffer, default);
            MetalFrameTimingProbe.ObservePass(list, PassLabelEventKind.Begin, "frame", 1);
            MetalFrameTimingProbe.BeginEncoder(MetalEncoderKind.Blit, buffer, default);
            await Task.Run(() => MetalFrameTimingProbe.ObservePass(list, PassLabelEventKind.Begin, "off-thread", 0)).ConfigureAwait(true);
            capture.Stop();
            var labels = passes.LabelsSnapshot();
            var label = Assert.Single(labels.Events.Records);
            Assert.Equal((0, buffer.ToInt64(), 1, "frame"), (label.CallbackIndex, label.CommandBuffer, label.EncoderOrdinal, labels.Labels[label.LabelId]));
            Assert.Equal(1, labels.ForeignEvents);
            passes.Complete(buffer, true);
            Assert.Equal(new[] { -1, label.LabelId }, passes.Snapshot().Encoders.Records.Select(e => e.LabelId));
        }
        finally { capture.Stop(); list.Dispose(); }
    }
}
