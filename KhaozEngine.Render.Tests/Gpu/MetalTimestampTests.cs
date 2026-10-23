using System;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    public sealed class MetalTimestampTests
    {
        [Theory]
        [InlineData(true, false)]
        [InlineData(true, true)]
        [InlineData(false, true)]
        public void StageOnlyDevicesSampleThroughPassAttachments(bool stage, bool blit)
        {
            var native = new MetalTimestampTestNative { Stage = stage, Blit = blit };
            IMetalTimestampApi api = new MetalTimestampApi(native);
            Assert.True(api.SupportsTimestampCounters);
            Assert.Single(native.Released); // The creation probe is immediately released.
            using var harness = new MetalRingHarness();
            using var pool = new MetalTimestampPool(api, harness.Timeline, harness.Liveness, 4);
            using var commands = harness.NewList(new object(), timestampTimeline: harness.Timeline);
            Assert.Equal(stage ? 4 : 0, native.Attachments.Count);
            Assert.Equal(stage ? new[] { (ulong.MaxValue, 0ul), (ulong.MaxValue, 1ul),
                (ulong.MaxValue, 2ul), (ulong.MaxValue, 3ul) } : Array.Empty<(ulong, ulong)>(), native.Attachments);
            int allocations = native.Allocations;
            commands.Begin();
            commands.Encoders.EnsureComputeEncoder();
            ulong priorEpoch = commands.Encoders.Epoch;
            native.BeforeBoundary = () => Assert.Equal(MetalEncoderKind.None, commands.Encoders.Open);
            commands.BeginTimestamps(pool, 1);
            native.Trace.Clear();
            commands.WriteTimestamp(pool, 0);
            Assert.True(commands.Encoders.Epoch > priorEpoch);
            Assert.Equal(stage
                ? new[] { "signal 1", "wait 1", "stage blit", "copy 4", "end blit", "signal 2", "wait 2" }
                : new[] { "signal 1", "wait 1", "blit", "copy 4", "sample 0 barrier=True", "end blit", "signal 2", "wait 2" }, native.Trace);
            Assert.Equal(new ulong[] { 1, 2 }, native.Signals);
            commands.Encoders.EnsureComputeEncoder();
            commands.EndTimestamps(pool);
            Assert.Equal(MetalEncoderKind.None, commands.Encoders.Open);
            Assert.Equal(new[] { "blit", "resolve 1", "end blit" }, native.Trace.TakeLast(3));
            commands.End();
            using var fence = harness.Timeline.CreateFence();
            fence.Arm(1);
            harness.Event.Completed = 1;
            Assert.Equal(GpuTimestampStatus.Ready, pool.TryResolve(fence, new ulong[1], out _));
            commands.Begin();
            commands.BeginTimestamps(pool, 1);
            commands.WriteTimestamp(pool, 0);
            commands.EndTimestamps(pool);
            commands.End();
            Assert.Equal(new ulong[] { 1, 2, 3, 4 }, native.Signals);
            Assert.Equal(allocations, native.Allocations);
            Assert.Equal(0, native.UnsupportedCalls);
            Assert.Equal(0ul, harness.Timeline.LastAllocated);
            Assert.Equal(0, harness.Event.WaitCount);
        }

        [Theory]
        [InlineData(false, true, true, false)]
        [InlineData(true, false, false, false)]
        [InlineData(true, true, false, true)]
        public void MissingCountersBoundariesOrProbeBufferRefusesSupport(bool set, bool stage, bool blit, bool fail)
        {
            var native = new MetalTimestampTestNative { CounterSet = set, Stage = stage, Blit = blit, FailSamples = fail };
            var api = new MetalTimestampApi(native);
            Assert.False(api.SupportsTimestampCounters);
            using var harness = new MetalRingHarness();
            Assert.Throws<NotSupportedException>(() => new MetalTimestampPool(api, harness.Timeline, harness.Liveness, 4));
            Assert.Equal(0, native.Allocations);
        }

        [Fact]
        public void MarkersPreserveDeferredClearsAndReemitEncoderState()
        {
            var native = new MetalTimestampTestNative();
            using var harness = new MetalRingHarness();
            using var pool = new MetalTimestampPool(Api(native), harness.Timeline, harness.Liveness, 2);
            var render = new FakeMetalRenderCalls();
            using var commands = harness.NewList(new object(), render: render, timestampTimeline: harness.Timeline);
            commands.Begin();
            commands.Passes.SetFramebuffer(new MetalBoundFramebuffer(1, 16, 16,
                new[] { new MetalAttachment(new IntPtr(1), GpuPixelFormat.R8G8B8A8UNorm) }, default, false));
            commands.Passes.ClearColourTarget(0, Color.Black);
            commands.BeginTimestamps(pool, 2);
            commands.WriteTimestamp(pool, 0);
            Assert.True(commands.Passes.HasPendingClears);
            Assert.Empty(render.Passes);
            IntPtr first = commands.Passes.PrepareDraw();
            Assert.False(commands.Passes.HasPendingClears);
            Assert.Equal(Color.Black, render.ClearOn(0));
            Assert.Single(render.Viewports);
            commands.WriteTimestamp(pool, 1);
            Assert.NotEqual(first, commands.Passes.PrepareDraw());
            Assert.Equal(2, render.Viewports.Count);
            commands.EndTimestamps(pool);
            commands.End();
        }

        [Fact]
        public void DisposalReleasesEveryPoolObjectExactlyOnce()
        {
            var native = new MetalTimestampTestNative();
            using var harness = new MetalRingHarness();
            var pool = new MetalTimestampPool(Api(native), harness.Timeline, harness.Liveness, 4);
            Assert.Equal(10, native.Allocations); // Probe, samples, result, two markers, event, four descriptors.
            pool.Dispose();
            pool.Dispose();
            Assert.Equal(10, native.Released.Count);
            Assert.Equal(10, native.Released.Distinct().Count());
        }

        [Fact]
        public void PeriodComesFromCorrelatedHardwareClocks()
        {
            var native = new MetalTimestampTestNative();
            using var harness = new MetalRingHarness();
            using var pool = Recorded(native, harness);
            using var fence = harness.Timeline.CreateFence();
            ulong[] ticks = { 91, 92, 93, 94 };
            Assert.Equal(GpuTimestampStatus.Pending, pool.TryResolve(fence, ticks, out double period));
            Assert.Equal(0d, period);
            Assert.Equal(new ulong[] { 91, 92, 93, 94 }, ticks);
            Assert.Equal(1, native.ClockReads);
            Assert.Equal(0, native.Copies);
            fence.Arm(1);
            harness.Event.Completed = 1;
            Assert.Equal(GpuTimestampStatus.Ready, pool.TryResolve(fence, ticks, out period));
            Assert.Equal(new ulong[] { 0, 25, 50, 94 }, ticks);
            Assert.Equal(2d, period);
            native.ClockError = true;
            native.Raw[0] = 0;
            Assert.Equal(GpuTimestampStatus.Ready, pool.TryResolve(fence, ticks, out period));
            Assert.Equal(new ulong[] { 0, 25, 50, 94 }, ticks);
            Assert.Equal(2d, period);
            Assert.Equal(2, native.ClockReads);
            Assert.Equal(1, native.Copies);
            Assert.Equal(0, harness.Event.WaitCount);
        }

        [Theory]
        [InlineData(1000ul, 600ul, 150ul)]
        [InlineData(999ul, 600ul, 150ul)]
        [InlineData(2000ul, 100ul, 150ul)]
        [InlineData(2000ul, 99ul, 150ul)]
        [InlineData(2000ul, 600ul, 99ul)]
        [InlineData(2000ul, 600ul, 601ul)]
        [InlineData(2000ul, 600ul, 149ul)]
        [InlineData(2000ul, 600ul, 0ul)]
        [InlineData(2000ul, 600ul, ulong.MaxValue)]
        public void UnusableCalibrationOrCounterIsCachedInvalid(ulong cpu, ulong gpu, ulong raw)
        {
            var native = new MetalTimestampTestNative { CpuEnd = cpu, GpuEnd = gpu, Raw = new[] { 150ul, 175ul, raw } };
            using var harness = new MetalRingHarness();
            using var pool = Recorded(native, harness);
            using var fence = harness.Timeline.CreateFence();
            fence.Arm(1);
            harness.Event.Completed = 1;
            ulong[] ticks = { 91, 92, 93 };
            Assert.Equal(GpuTimestampStatus.Invalid, pool.TryResolve(fence, ticks, out double period));
            Assert.Equal(0d, period);
            Assert.Equal(new ulong[] { 91, 92, 93 }, ticks);
            native.ClockError = true;
            Assert.Equal(GpuTimestampStatus.Invalid, pool.TryResolve(fence, ticks, out period));
            Assert.Equal(0d, period);
            Assert.Equal(new ulong[] { 91, 92, 93 }, ticks);
            Assert.Equal(2, native.ClockReads);
            Assert.Equal(0, harness.Event.WaitCount);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void NativeClockFailureOrDeviceDeathNeverReadsSharedResults(bool dead)
        {
            var native = new MetalTimestampTestNative();
            using var harness = new MetalRingHarness();
            using var pool = Recorded(native, harness);
            using var fence = harness.Timeline.CreateFence();
            fence.Arm(1);
            harness.Event.Completed = 1;
            if (dead) harness.Event.OnRead = harness.Liveness.MarkDead;
            else native.ClockError = true;
            ulong[] ticks = { 91, 92, 93 };
            Assert.Throws<InvalidOperationException>(() => pool.TryResolve(fence, ticks, out _));
            Assert.Equal(new ulong[] { 91, 92, 93 }, ticks);
            Assert.Equal(0, native.Copies);
            Assert.Equal(0, harness.Event.WaitCount);
        }

        static IMetalTimestampApi Api(MetalTimestampTestNative native) => new MetalTimestampApi(native);

        static MetalTimestampPool Recorded(MetalTimestampTestNative native, MetalRingHarness harness)
        {
            var pool = new MetalTimestampPool(Api(native), harness.Timeline, harness.Liveness, 4);
            pool.Begin(new IntPtr(1), 3);
            pool.Write(new IntPtr(1), 0);
            pool.Write(new IntPtr(1), 1);
            pool.Write(new IntPtr(1), 2);
            pool.End(new IntPtr(1));
            return pool;
        }
    }
}
