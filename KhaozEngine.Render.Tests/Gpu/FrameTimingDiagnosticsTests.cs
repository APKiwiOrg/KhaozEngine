using System;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

[Collection("NativeDeviceLifecycle")]
public sealed class FrameTimingDiagnosticsTests
{
    [Fact]
    public void Bounded_storage_preserves_first_records_and_counts_every_overflow()
    {
        var buffer = new FrameTimingBuffer<int>(2);
        buffer.Add(11);
        buffer.Add(22);
        buffer.Add(33);
        buffer.Add(44);
        var snapshot = buffer.Snapshot();
        Assert.Equal(new[] { 11, 22 }, snapshot.Records);
        Assert.Equal(2, snapshot.Overflow);
        Assert.Equal(0, snapshot.IncompleteWrites);
    }

    [Fact]
    public void Native_read_and_handler_paths_are_inactive_until_sampling_and_after_stop()
    {
        var reader = new TimingReader { RefuseAccess = true };
        MetalFrameTimingProbe.ObserveCompletion(new IntPtr(1), new IntPtr(2), 4, ref reader);
        MetalFrameTimingProbe.ObservePresent(new IntPtr(1), new IntPtr(3), ref reader);
        MetalFrameTimingProbe.ObservePresented(new IntPtr(3), ref reader);
        Assert.False(reader.AccessedOutsideSampling);
        using var capture = new FrameTimingCapture(null, 8);
        capture.Start();
        Assert.True(MetalFrameTimingProbe.Sampling);
        reader.RefuseAccess = false;
        MetalFrameTimingProbe.ObserveCompletion(new IntPtr(1), new IntPtr(2), 4, ref reader);
        MetalFrameTimingProbe.ObservePresent(new IntPtr(1), new IntPtr(3), ref reader);
        MetalFrameTimingProbe.ObservePresented(new IntPtr(3), ref reader);
        capture.Stop();
        Assert.False(MetalFrameTimingProbe.Sampling);
        reader.RefuseAccess = true;
        MetalFrameTimingProbe.ObserveCompletion(new IntPtr(1), new IntPtr(2), 4, ref reader);
        MetalFrameTimingProbe.ObservePresent(new IntPtr(1), new IntPtr(3), ref reader);
        MetalFrameTimingProbe.ObservePresented(new IntPtr(3), ref reader);
        Assert.False(reader.AccessedOutsideSampling);
        var snapshot = capture.Snapshot();
        var completion = Assert.Single(snapshot.Completions.Records);
        Assert.Equal(2.0, completion.GpuStartTime);
        Assert.Equal(2.004, completion.GpuEndTime);
        Assert.Equal(4, completion.Status);
        Assert.Equal(73UL, Assert.Single(snapshot.PresentCalls.Records).DrawableId);
        Assert.Equal(73UL, Assert.Single(snapshot.Presented.Records).DrawableId);
        Assert.Equal(2.010, snapshot.Presented.Records[0].PresentedTime);
        Assert.True(reader.HandlerAttached);
    }

    [Fact]
    public void Snapshot_clips_native_entries_to_inclusive_callback_boundaries()
    {
        var snapshot = new FrameTimingSnapshot(new([new(1, 8, 99, 0, FrameTimingCommandKind.Other),
                new(2, 8, 100, 0, FrameTimingCommandKind.FrameRecording)], 0, 0),
            new([new(2, 8, 200, 4, 1, 2), new(3, 8, 201, 4, 2, 3)], 0, 0),
            new([new(7, 3, 2, 110, 0)], 0, 0), new([new(7, 190, 1.5)], 0, 0), 0).Window(100, 200);
        Assert.Equal(100, Assert.Single(snapshot.Submissions.Records).Timestamp);
        Assert.Equal(200, Assert.Single(snapshot.Completions.Records).Timestamp);
        Assert.Equal(110, Assert.Single(snapshot.PresentCalls.Records).Timestamp);
        Assert.Equal(190, Assert.Single(snapshot.Presented.Records).Timestamp);
    }

    struct TimingReader : IMetalFrameTimingReader
    {
        internal bool RefuseAccess;
        internal bool HandlerAttached;
        internal bool AccessedOutsideSampling;
        void Check()
        {
            if (!RefuseAccess) return;
            AccessedOutsideSampling = true;
            throw new InvalidOperationException("Native access outside sampling.");
        }
        public void ReadGpuTimes(IntPtr buffer, out double start, out double end)
        { Check(); start = 2; end = 2.004; }
        public ulong DrawableId(IntPtr drawable) { Check(); return 73; }
        public double PresentedTime(IntPtr drawable) { Check(); return 2.010; }
        public bool AttachPresentedHandler(IntPtr drawable) { Check(); HandlerAttached = true; return true; }
    }
}
