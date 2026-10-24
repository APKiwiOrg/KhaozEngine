using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11.Internal;
using KhaozEngine.Gpu.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    public sealed partial class D3D11TimestampTests
    {
        [D3D11TimestampGpuFact]
        public void FeatureLevel10OrAboveGuaranteesNativeTimestampBatch()
        {
            if (!OperatingSystem.IsWindows())
                throw new InvalidOperationException("The running Direct3D 11 backend requires Windows.");
            VerifyNativeTimestampBatchWindows();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        static void VerifyNativeTimestampBatchWindows()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            Assert.Equal(GpuBackendKind.Direct3D11Native, gpu.GpuDevice.Backend);
            var device = Assert.IsType<D3D11GpuDevice>(gpu.GpuDevice);
            uint featureLevel = device.NativeFeatureLevel;
            Assert.True(featureLevel >= 0xa000u,
                $"The Direct3D 11 feature level 0x{featureLevel:X4} is below the 10_0 timestamp guarantee.");
            Assert.True(device.Capabilities.SupportsTimestamps,
                $"Direct3D 11 guarantees timestamp and disjoint queries at feature level 10_0 and above (actual 0x{featureLevel:X4}).");

            IGpuResourceFactory factory = device.Factory;
            using IGpuTimestampPool pool = factory.CreateTimestampPool(2);
            using IGpuFence completion = factory.CreateFence();
            using IGpuComputeShader shader = factory.CreateComputeShaderFromSpirv(@"#version 450
layout(local_size_x = 1) in;
void main() { }");
            using IGpuComputePipeline pipeline = factory.CreateComputePipeline(new GpuComputePipelineDescription(shader));
            using IGpuCommandList commands = factory.CreateCommandList();
            commands.Begin();
            commands.SetComputePipeline(pipeline);
            commands.BeginTimestamps(pool, 2);
            commands.WriteTimestamp(pool, 0);
            commands.Dispatch(1, 1, 1);
            commands.WriteTimestamp(pool, 1);
            commands.EndTimestamps(pool);
            commands.End();
            device.Submit(commands, completion);
            device.WaitForIdle(); // This native correctness proof drains once, outside the timestamp interval.

            Span<ulong> ticks = stackalloc ulong[2];
            Assert.Equal(GpuTimestampStatus.Ready, pool.TryResolve(completion, ticks, out double period));
            Assert.Equal(0ul, ticks[0]);
            Assert.True(ticks[1] >= ticks[0]);
            Assert.True(double.IsFinite(period) && period > 0d);
        }

        [Fact]
        public void DisjointAndUnavailableQueriesNeverProduceMeasuredTime()
        {
            var api = new D3D11TimestampTestApi();
            var timeline = new D3D11TimestampTestTimeline();
            object gate = new();
            using var fences = new D3D11FenceSubsystem(timeline, gate);
            using var pool = new D3D11TimestampPool(api, fences, gate, 2);
            using var fence = (D3D11GpuFence)fences.CreateFence();
            using var commands = Recorder(fences);
            Record(commands, pool);
            ulong[] ticks = [90, 91, 92];
            Assert.Equal(GpuTimestampStatus.Pending, pool.TryResolve(fence, ticks, out double period));
            Assert.Equal(0d, period);
            Assert.DoesNotContain(api.Calls, x => x.StartsWith("Read", StringComparison.Ordinal));
            fence.Arm(1);
            timeline.Completed = 1;
            api.Unavailable = 1;
            Assert.Equal(GpuTimestampStatus.Pending, pool.TryResolve(fence, ticks, out period));
            Assert.Equal(new ulong[] { 90, 91, 92 }, ticks);
            Assert.Equal(0d, period);
            api.Unavailable = -1;
            Assert.Equal(GpuTimestampStatus.Ready, pool.TryResolve(fence, ticks, out period));
            Assert.Equal(new ulong[] { 0, 25, 92 }, ticks);
            Assert.Equal(100d, period);
            int reads = api.Calls.Count;
            Assert.Equal(GpuTimestampStatus.Ready, pool.TryResolve(fence, ticks, out period));
            Assert.Equal(reads, api.Calls.Count);
            Assert.Equal(100d, period);
            Assert.Equal(0, timeline.Flushes);
            Assert.Equal(0, timeline.Waits);

            foreach (bool disjoint in new[] { true, false })
            {
                Record(commands, pool);
                api.Disjoint = disjoint;
                api.Frequency = disjoint ? 10_000_000ul : 0ul;
                ticks = [90, 91];
                Assert.Equal(GpuTimestampStatus.Invalid, pool.TryResolve(fence, ticks, out period));
                Assert.Equal(0d, period);
                Assert.Equal(new ulong[] { 90, 91 }, ticks);
                reads = api.Calls.Count;
                Assert.Equal(GpuTimestampStatus.Invalid, pool.TryResolve(fence, ticks, out period));
                Assert.Equal(reads, api.Calls.Count);
            }
        }

        [Theory]
        [InlineData(-2)]
        [InlineData(0)]
        [InlineData(1)]
        public void EveryUnavailableQueryPreservesTheDestination(int unavailable)
        {
            var api = new D3D11TimestampTestApi { Unavailable = unavailable };
            object gate = new();
            using var fences = new D3D11FenceSubsystem(new D3D11TimestampTestTimeline { Completed = 1 }, gate);
            using var pool = new D3D11TimestampPool(api, fences, gate, 2);
            using var fence = (D3D11GpuFence)fences.CreateFence();
            fence.Arm(1);
            using var commands = Recorder(fences);
            Record(commands, pool);
            ulong[] ticks = [90, 91];
            Assert.Equal(GpuTimestampStatus.Pending, pool.TryResolve(fence, ticks, out double period));
            Assert.Equal(0d, period);
            Assert.Equal(new ulong[] { 90, 91 }, ticks);
            Assert.Equal(new[] { "Read(disjoint)", "Read(0)", "Read(1)" }, api.Calls.Where(x => x.StartsWith("Read", StringComparison.Ordinal)));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void DeferredReplayRetainsTheQueriesAndTheirOrdering(bool immediate)
        {
            var api = new D3D11TimestampTestApi();
            object gate = new();
            using var fences = new D3D11FenceSubsystem(new D3D11TimestampTestTimeline(), gate);
            using var pool = new D3D11TimestampPool(api, fences, gate, 2);
            var log = new D3D11NativeCallLog();
            var emitter = new D3D11TimestampTestEmitter(log, api);
            using IGpuCommandList commands = D3D11CommandDrivers.Create(immediate ? D3D11RecordMode.Immediate : D3D11RecordMode.Deferred, emitter, fences);
            Record(commands, pool, () => commands.Dispatch(1, 1, 1));
            if (commands is D3D11CommandRecorder<D3D11StreamEmitter> deferred)
            {
                Assert.Empty(api.Calls);
                Assert.Empty(log.Trace);
                var stream = deferred.Emitter.Stream;
                Assert.Equal(1, stream.ReferenceCount);
                for (int i = 0; i < stream.ReferenceCount; i++)
                    Assert.Same(pool, stream.Reference<IGpuTimestampPool>(i));
            }
            if (commands is D3D11CommandRecorder<D3D11StreamEmitter> recorded)
            {
                var counts = new D3D11EmitterCallLog();
                var counter = new D3D11CountingEmitter(counts);
                recorded.Emitter.Stream.Replay(ref counter);
                Assert.Equal(new[] { "Begin()", "BeginTimestamps(r0,2)", "WriteTimestamp(r0,0)", "Dispatch(1,1,1)",
                    "WriteTimestamp(r0,1)", "EndTimestamps(r0)", "End()" }, counts.Trace);
                Assert.Empty(api.Calls);
            }
            D3D11CommandDrivers.Submit(gate, commands, ref emitter);
            Assert.Equal(new[] { "Begin(disjoint)", "End(0)", "Dispatch(1,1,1)", "End(1)", "End(disjoint)" }, api.Calls);
            Assert.Equal(new[] { "ClearState()", "Begin(r0)", "End(r1)", "Dispatch(1,1,1)", "End(r2)", "End(r0)" }, log.Trace);
            commands.Dispose();
            if (commands is D3D11CommandRecorder<D3D11StreamEmitter> disposed)
                Assert.Equal(0, disposed.Emitter.Stream.ReferenceCount);
        }

        [Fact]
        public void NativeAdapterUsesOnlyNonflushingReadsAndActualQueryKinds()
        {
            var native = new D3D11TimestampTestQueries();
            var api = new D3D11TimestampApi(native);
            Assert.True(D3D11TimestampApi.Probe(api));
            Assert.Equal(new[] { 2, 3 }, native.Created);
            Assert.Equal(new object[] { 3, 2 }, native.Destroyed);
            Assert.True(api.TryReadTimestamp(2, out ulong ticks));
            Assert.Equal(125ul, ticks);
            Assert.True(api.TryReadDisjoint(3, out ulong frequency, out bool disjoint));
            Assert.Equal(10_000_000ul, frequency);
            Assert.False(disjoint);
            Assert.Equal(new[] { (8, 1u), (16, 1u) }, native.Reads);
            native.Failure = 1;
            Assert.False(api.TryReadTimestamp(2, out _));
            native.Failure = unchecked((int)0x80004005);
            Assert.Throws<InvalidOperationException>(() => api.TryReadTimestamp(2, out _));
        }

        [Theory]
        [InlineData(2)]
        [InlineData(3)]
        public void UnsupportedProbeCleansUpAndOtherFaultsPropagate(int queryType)
        {
            var native = new D3D11TimestampTestQueries { UnsupportedType = queryType };
            Assert.False(D3D11TimestampApi.Probe(new D3D11TimestampApi(native)));
            Assert.Equal(new[] { 2, 3 }, native.Created);
            Assert.Equal(queryType == 3 ? new object[] { 2 } : new object[] { 3 }, native.Destroyed);
            native.UnsupportedType = -1;
            native.Failure = unchecked((int)0x80004005);
            Assert.Throws<COMException>(() => D3D11TimestampApi.Probe(new D3D11TimestampApi(native)));
        }

        [Fact]
        public void ValidationPrecedesNativeAccessAndDisposeReleasesEachQueryOnce()
        {
            var api = new D3D11TimestampTestApi();
            object gate = new();
            using var fences = new D3D11FenceSubsystem(new D3D11TimestampTestTimeline(), gate);
            using var other = new D3D11FenceSubsystem(new D3D11TimestampTestTimeline(), new object());
            var pool = new D3D11TimestampPool(api, fences, gate, 2);
            using var commands = Recorder(fences);
            Assert.Throws<InvalidOperationException>(() => commands.BeginTimestamps(pool, 2));
            commands.Begin();
            Assert.Throws<ArgumentOutOfRangeException>(() => commands.BeginTimestamps(pool, 0));
            commands.BeginTimestamps(pool, 2);
            Assert.Throws<InvalidOperationException>(() => commands.WriteTimestamp(pool, 1));
            commands.WriteTimestamp(pool, 0);
            Assert.Throws<InvalidOperationException>(() => commands.EndTimestamps(pool));
            commands.WriteTimestamp(pool, 1);
            commands.EndTimestamps(pool);
            commands.End();
            using var fence = fences.CreateFence();
            using var foreign = other.CreateFence();
            Assert.Throws<ArgumentException>(() => pool.TryResolve(fence, new ulong[1], out _));
            Assert.Throws<ArgumentException>(() => pool.TryResolve(foreign, new ulong[2], out _));
            commands.Begin();
            Assert.Throws<InvalidOperationException>(() => commands.BeginTimestamps(pool, 2));
            commands.End();
            pool.Dispose();
            pool.Dispose();
            Assert.Equal(3, api.Destroyed.Distinct().Count());
            Assert.Throws<ObjectDisposedException>(() => pool.TryResolve(fence, new ulong[2], out _));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void NativeDeviceRemovalIsLatchedAndNeverBecomesUnsupported(bool duringRead)
        {
            var liveness = new DeviceLiveness();
            var native = new D3D11TimestampTestQueries { Failure = unchecked((int)0x887A0005) };
            var loss = new D3D11DeviceLossLatch(liveness, new RemovedReason());
            var api = new D3D11TimestampApi(native, liveness, loss);
            if (duringRead)
                Assert.Throws<ObjectDisposedException>(() => api.TryReadDisjoint(3, out _, out _));
            else
                Assert.Throws<ObjectDisposedException>(() => D3D11TimestampApi.Probe(api));
            Assert.True(loss.IsLost);
            Assert.True(liveness.IsDead);
            Assert.Equal(duringRead ? "timestamp GetData" : "timestamp query creation", loss.Site);
            api.Destroy(2);
            Assert.Empty(native.Destroyed);
        }

        [Fact]
        public void DeviceLossDuringFencePollNeverQueriesTheDevice()
        {
            var liveness = new DeviceLiveness();
            var api = new D3D11TimestampTestApi();
            var timeline = new D3D11TimestampTestTimeline { Completed = 1, OnPoll = liveness.MarkDead };
            object gate = new();
            using var fences = new D3D11FenceSubsystem(timeline, gate, liveness);
            using var pool = new D3D11TimestampPool(api, fences, gate, 2);
            using var commands = Recorder(fences);
            Record(commands, pool);
            using var fence = (D3D11GpuFence)fences.CreateFence();
            fence.Arm(1);
            Assert.Throws<ObjectDisposedException>(() => pool.TryResolve(fence, new ulong[2], out _));
            Assert.Empty(api.Calls);
        }

        sealed class RemovedReason : ID3D11RemovedReason
        {
            public int GetDeviceRemovedReason() => unchecked((int)0x887A0005);
        }

        static D3D11CommandRecorder<D3D11NativeTraceEmitter> Recorder(D3D11FenceSubsystem fences)
            => new(new D3D11NativeTraceEmitter(new D3D11DeviceState(), new D3D11NativeCallLog()), fences);

        static void Record(IGpuCommandList commands, IGpuTimestampPool pool, Action? dispatch = null)
        {
            commands.Begin();
            commands.BeginTimestamps(pool, 2);
            commands.WriteTimestamp(pool, 0);
            dispatch?.Invoke();
            commands.WriteTimestamp(pool, 1);
            commands.EndTimestamps(pool);
            commands.End();
        }
    }
}
