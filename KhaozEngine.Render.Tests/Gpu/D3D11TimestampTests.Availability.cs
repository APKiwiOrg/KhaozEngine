using System;
using System.Linq;
using KhaozEngine.Gpu.D3D11.Internal;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    // Direct3D 11 query data can trail a completed later fence (WARP rehearsal run 40628799283, issue 1635).
    public sealed partial class D3D11TimestampTests
    {
        [Fact]
        public void DrainedBatchWhoseQueriesTrailTheFenceIsPendingWithoutTheWait()
        {
            using var rig = new AvailabilityRig(notReadyReads: 1);
            Assert.Equal(GpuTimestampStatus.Pending, rig.Pool.TryResolve(rig.Fence, new ulong[2], out _));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(3)]
        public void AvailabilityWaitFlushesUntilEveryQueryIsReadyAndRecordsItsPolls(int notReady)
        {
            using var rig = new AvailabilityRig(notReady);
            long tick = 10;
            GpuTimestampAvailability wait = rig.Wait(() => tick++, long.MaxValue);

            Assert.True(wait.Ready);
            Assert.Equal(notReady, wait.Polls);
            Assert.Equal(10, wait.StartTicks);
            Assert.Equal(11 + notReady, wait.EndTicks);
            Assert.Equal((1ul, 1ul, 1ul), (wait.FenceTarget, wait.FenceCompleted, wait.DrainTarget));
            Assert.Equal(new[] { "disjoint", "timestamp.0", "timestamp.1" }, wait.Queries.Select(q => q.Query));
            Assert.All(wait.Queries, q => Assert.Equal((notReady, 0), (q.NotReady, q.FinalResult)));
            foreach (object query in new object[] { "disjoint", 0, 1 })
                Assert.Equal(new[] { false }.Concat(Enumerable.Repeat(true, notReady)),
                    rig.Api.Availability.Where(a => a.Query.Equals(query)).Select(a => a.Flush));
            ulong[] ticks = [90, 91];
            Assert.Equal(GpuTimestampStatus.Ready, rig.Pool.TryResolve(rig.Fence, ticks, out double period));
            Assert.Equal(new ulong[] { 0, 25 }, ticks);
            Assert.Equal(100d, period);
            Assert.Equal(1, rig.Timeline.Flushes);
        }

        [Fact]
        public void NeverAvailableQueriesStopAtTheBoundAndStayPending()
        {
            using var rig = new AvailabilityRig(int.MaxValue);
            long tick = 0;
            GpuTimestampAvailability wait = rig.Wait(() => tick++, 3);

            Assert.False(wait.Ready);
            Assert.Equal(2, wait.Polls);
            Assert.Equal((0L, 4L), (wait.StartTicks, wait.EndTicks));
            Assert.All(wait.Queries, q => Assert.Equal((2, 1), (q.NotReady, q.FinalResult)));
            ulong[] ticks = [90, 91];
            Assert.Equal(GpuTimestampStatus.Pending, rig.Pool.TryResolve(rig.Fence, ticks, out double period));
            Assert.Equal(new ulong[] { 90, 91 }, ticks);
            Assert.Equal(0d, period);
        }

        [Fact]
        public void ElapsedBoundStillReadsOnceWithoutFlushing()
        {
            using var rig = new AvailabilityRig(notReadyReads: 1);
            GpuTimestampAvailability wait = rig.Wait(() => 5, 5);

            Assert.False(wait.Ready);
            Assert.Equal(0, wait.Polls);
            Assert.All(wait.Queries, q => Assert.Equal((0, 1), (q.NotReady, q.FinalResult)));
            Assert.All(rig.Api.Availability, a => Assert.False(a.Flush));
        }

        [Fact]
        public void UnsignalledFenceIsNeverRead()
        {
            using var rig = new AvailabilityRig(notReadyReads: 0, completed: 0);
            GpuTimestampAvailability wait = rig.Wait(() => 0, long.MaxValue);

            Assert.False(wait.Ready);
            Assert.Equal(0, wait.Polls);
            Assert.Empty(wait.Queries);
            Assert.Empty(rig.Api.Availability);
            Assert.Equal(GpuTimestampStatus.Pending, rig.Pool.TryResolve(rig.Fence, new ulong[2], out _));
        }

        [Fact]
        public void AvailabilityReadsCarryTheirFlushFlagAndHresult()
        {
            var native = new D3D11TimestampTestQueries();
            var api = new D3D11TimestampApi(native);
            Assert.Equal(0, api.ReadAvailability(2, flush: false));
            Assert.Equal(0, api.ReadAvailability(3, flush: true));
            Assert.Equal(new[] { (0, 1u), (0, 0u) }, native.Reads);
            native.Failure = 1;
            Assert.Equal(1, api.ReadAvailability(2, flush: true));
            native.Failure = unchecked((int)0x80004005);
            Assert.Throws<InvalidOperationException>(() => api.ReadAvailability(2, flush: true));
        }

        sealed class AvailabilityRig : IDisposable
        {
            internal readonly D3D11TimestampTestApi Api;
            internal readonly D3D11TimestampTestTimeline Timeline;
            internal readonly D3D11FenceSubsystem Fences;
            internal readonly D3D11TimestampPool Pool;
            internal readonly D3D11GpuFence Fence;
            readonly IGpuCommandList _commands;

            internal AvailabilityRig(int notReadyReads, ulong completed = 1)
            {
                Api = new D3D11TimestampTestApi { NotReadyReads = notReadyReads };
                Timeline = new D3D11TimestampTestTimeline { Completed = 1 };
                object gate = new();
                Fences = new D3D11FenceSubsystem(Timeline, gate);
                Pool = new D3D11TimestampPool(Api, Fences, gate, 2);
                Fence = (D3D11GpuFence)Fences.CreateFence();
                _commands = Recorder(Fences);
                Record(_commands, Pool);
                Fence.Arm(1);
                Fences.WaitForIdle();
                Timeline.Completed = completed;
            }

            internal GpuTimestampAvailability Wait(Func<long> now, long deadline)
                => ((IGpuTimestampAvailabilityWait)Pool).AwaitAvailability(Fence, now, deadline);

            public void Dispose() { _commands.Dispose(); Fence.Dispose(); Pool.Dispose(); Fences.Dispose(); }
        }
    }
}
