using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Gpu.Vulkan.Internal;
using Silk.NET.Vulkan;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    public sealed class VulkanTimestampTests
    {
        [Fact]
        public void ProbeUsesTheSelectedQueueAndResolveNeverWaits()
        {
            QueueFamilyProperties[] queues =
            [
                new() { QueueFlags = QueueFlags.ComputeBit, TimestampValidBits = 64 },
                new() { QueueFlags = QueueFlags.GraphicsBit | QueueFlags.ComputeBit, TimestampValidBits = 0 },
                new() { QueueFlags = QueueFlags.GraphicsBit, TimestampValidBits = 64 },
            ];
            Assert.Equal(1u, VulkanPhysicalDeviceReader.FirstGraphicsQueueFamily(queues, out int bits));
            Assert.False(Capabilities(bits, 2.5).SupportsTimestamps);
            queues[1].TimestampValidBits = 36;
            Assert.Equal(1u, VulkanPhysicalDeviceReader.FirstGraphicsQueueFamily(queues, out bits));
            Assert.Equal(36, bits);
            Assert.False(Capabilities(bits, 0).SupportsTimestamps);
            Assert.True(Capabilities(bits, 2.5).SupportsTimestamps);
            Assert.Equal(uint.MaxValue, VulkanPhysicalDeviceReader.FirstGraphicsQueueFamily([], out bits));
            Assert.Equal(0, bits);

            var rig = new VulkanResourceFixture();
            using var native = new VulkanTimestampNativeContext(rig.Trace);
            using var vk = new Vk(native);
            var alive = new DeviceLiveness();
            var api = new VulkanTimestampApi(vk, new Device(1), new VulkanDeviceLossLatch(alive), alive);
            using var pool = new VulkanTimestampPool(api, rig.Timeline, rig.Owner, 4, 8, 2.5);
            using var list = List(rig);
            using var target = rig.Factory.CreateTexture(GpuTextureDescription.Texture2D(
                4, 4, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget));
            using var framebuffer = rig.Factory.CreateFramebuffer(null, target);
            list.Begin();
            list.SetFramebuffer(framebuffer);
            list.PrepareDraw();
            rig.Trace.Clear();
            list.BeginTimestamps(pool, 3);
            for (uint i = 0; i < 3; i++)
            {
                list.PrepareDraw();
                list.WriteTimestamp(pool, i);
            }
            list.EndTimestamps(pool);
            list.End();
            Assert.Equal("EndRendering", rig.Trace[rig.Trace.IndexOf("Reset(3)") - 1]);
            for (uint i = 0; i < 3; i++)
            {
                int write = rig.Trace.IndexOf($"Write({i})");
                Assert.True(write > rig.Trace.IndexOf("Reset(3)"));
                Assert.Equal("EndRendering", rig.Trace[write - 1]);
            }
            Assert.Equal(QueryType.Timestamp, native.QueryType);
            Assert.Equal(4u, native.Capacity);
            Assert.Equal(0u, native.ResetFirst);
            Assert.Equal(3u, native.ResetCount);
            Assert.Equal(PipelineStageFlags2.AllCommandsBit, native.WriteStage);
            Assert.Equal(0, native.ReadCalls);

            using var fence = rig.Timeline.CreateFence();
            ulong[] ticks = [90, 91, 92, 93];
            Assert.Equal(GpuTimestampStatus.Pending, pool.TryResolve(fence, ticks, out double period));
            fence.Arm(1);
            Assert.Equal(GpuTimestampStatus.Pending, pool.TryResolve(fence, ticks, out period));
            Assert.Equal(0, native.ReadCalls);
            rig.Semaphore.Completed = 1;
            native.Values = [250, 5, 9];
            native.Availability = [1, 0, 1];
            Assert.Equal(GpuTimestampStatus.Pending, pool.TryResolve(fence, ticks, out period));
            Assert.Equal(new ulong[] { 90, 91, 92, 93 }, ticks);
            Assert.Equal(0d, period);
            Assert.Equal(1, native.ReadCalls);
            native.Availability = [1, 1, 1];
            Assert.Equal(GpuTimestampStatus.Ready, pool.TryResolve(fence, ticks, out period));
            Assert.Equal(new ulong[] { 0, 11, 15, 93 }, ticks);
            Assert.Equal(2.5d, period);
            Assert.Equal(5u, (uint)native.ReadFlags);
            Assert.Equal(16ul, native.ReadStride);
            Assert.Equal(48u, native.ReadBytes);
            Assert.Equal(0u, native.ReadFirst);
            Assert.Equal(3u, native.ReadCount);
            Assert.Equal(2, native.ReadCalls);
            native.Values = [1, 2, 3];
            ulong[] repeated = [7, 8, 9];
            Assert.Equal(GpuTimestampStatus.Ready, pool.TryResolve(fence, repeated, out period));
            Assert.Equal(new ulong[] { 0, 11, 15 }, repeated);
            Assert.Equal(2.5d, period);
            Assert.Equal(2, native.ReadCalls);
            Assert.Equal(0, rig.Semaphore.WaitCount);
            Assert.Equal(0, rig.Timeline.TotalDrain.Count);
        }

        [Theory]
        [InlineData(0, 2.5)]
        [InlineData(65, 2.5)]
        [InlineData(36, 0)]
        [InlineData(36, -1)]
        [InlineData(36, double.NaN)]
        [InlineData(36, double.PositiveInfinity)]
        public void UnusableProbeRefusesCreation(int bits, double period)
        {
            var rig = new VulkanResourceFixture();
            var api = new FakeTimestampApi();
            Assert.False(Capabilities(bits, period).SupportsTimestamps);
            var factory = Factory(rig, api, bits, period);
            Assert.Throws<NotSupportedException>(() => factory.CreateTimestampPool(3));
            Assert.Equal(0, api.CreateCalls);
        }

        [Fact]
        public void FactoryCreatesFixedPoolsAndValidationPrecedesNativeAccess()
        {
            var rig = new VulkanResourceFixture();
            var api = new FakeTimestampApi();
            var factory = Factory(rig, api);
            Assert.Throws<ArgumentOutOfRangeException>(() => factory.CreateTimestampPool(0));
            Assert.Equal(0, api.CreateCalls);
            using var pool = factory.CreateTimestampPool(3);
            using var list = List(rig);
            using var fence = rig.Timeline.CreateFence();
            Assert.Equal(3u, pool.Capacity);
            Assert.Throws<InvalidOperationException>(() => pool.TryResolve(fence, new ulong[3], out _));
            Assert.Throws<InvalidOperationException>(() => list.BeginTimestamps(pool, 3));
            list.Begin();
            Assert.Throws<ArgumentOutOfRangeException>(() => list.BeginTimestamps(pool, 4));
            list.BeginTimestamps(pool, 3);
            Assert.Throws<InvalidOperationException>(() => list.EndTimestamps(pool));
            Assert.Throws<InvalidOperationException>(() => list.WriteTimestamp(pool, 1));
            list.WriteTimestamp(pool, 0);
            Assert.Throws<InvalidOperationException>(() => list.WriteTimestamp(pool, 0));
            list.WriteTimestamp(pool, 1);
            list.WriteTimestamp(pool, 2);
            list.EndTimestamps(pool);
            Assert.Throws<InvalidOperationException>(() => list.BeginTimestamps(pool, 3));
            fence.Arm(1);
            int polls = rig.Semaphore.ReadCount;
            Assert.Throws<ArgumentException>(() => pool.TryResolve(fence, new ulong[2], out _));
            Assert.Equal(polls, rig.Semaphore.ReadCount);
            Assert.Throws<ArgumentNullException>(() => pool.TryResolve(null!, new ulong[3], out _));
            var other = new VulkanResourceFixture();
            using var foreignFence = other.Timeline.CreateFence();
            Assert.Throws<ArgumentException>(() => pool.TryResolve(foreignFence, new ulong[3], out _));
            using var foreignList = List(other);
            foreignList.Begin();
            Assert.Throws<ArgumentException>(() => foreignList.BeginTimestamps(pool, 1));
            fence.Dispose();
            Assert.Throws<ObjectDisposedException>(() => pool.TryResolve(fence, new ulong[3], out _));
            Assert.Equal(0, api.ReadCalls);
        }

        [Fact]
        public void OverflowIsRepeatablyInvalidAndAConsumedPoolCanBeReused()
        {
            var rig = new VulkanResourceFixture();
            var api = new FakeTimestampApi { Values = [0, ulong.MaxValue, 0] };
            using var pool = new VulkanTimestampPool(api, rig.Timeline, rig.Owner, 3, 64, 1);
            using var list = List(rig);
            using var fence = rig.Timeline.CreateFence();
            Record(list, pool, 3);
            fence.Arm(1);
            rig.Semaphore.Completed = 1;
            ulong[] ticks = [70, 71, 72];
            Assert.Equal(GpuTimestampStatus.Invalid, pool.TryResolve(fence, ticks, out double period));
            Assert.Equal(new ulong[] { 70, 71, 72 }, ticks);
            Assert.Equal(0d, period);
            api.Values = [1, 2, 3];
            Assert.Equal(GpuTimestampStatus.Invalid, pool.TryResolve(fence, ticks, out period));
            Assert.Equal(new ulong[] { 70, 71, 72 }, ticks);
            Assert.Equal(0d, period);
            Assert.Equal(1, api.ReadCalls);
            Record(list, pool, 1);
            fence.Reset();
            Assert.Equal(GpuTimestampStatus.Pending, pool.TryResolve(fence, ticks, out period));
            Assert.Equal(new ulong[] { 70, 71, 72 }, ticks);
            Assert.Equal(0d, period);
            fence.Arm(2);
            rig.Semaphore.Completed = 2;
            Assert.Equal(GpuTimestampStatus.Ready, pool.TryResolve(fence, ticks, out period));
            Assert.Equal(new ulong[] { 0, 71, 72 }, ticks);
            Assert.Equal(1d, period);
        }

        [Fact]
        public void DisposalRetiresBehindTheOwnerWithoutWaiting()
        {
            var rig = new VulkanResourceFixture();
            var api = new FakeTimestampApi();
            var pool = new VulkanTimestampPool(api, rig.Timeline, rig.Owner, 3, 36, 2.5);
            using var list = List(rig);
            Record(list, pool, 3);
            ulong submitted = rig.Timeline.NextSubmitValue();
            rig.Timeline.RegisterSubmitted(submitted);
            pool.Dispose();
            pool.Dispose();
            Assert.Equal(0, api.DestroyCalls);
            Assert.Equal(0, rig.Retired.Drain(0));
            Assert.Equal(1, rig.Retired.Drain(submitted));
            Assert.Equal(1, api.DestroyCalls);
            using var fence = rig.Timeline.CreateFence();
            Assert.Throws<ObjectDisposedException>(() => pool.TryResolve(fence, new ulong[3], out _));
            Assert.Equal(0, rig.Semaphore.WaitCount);
        }

        [Theory]
        [InlineData(Result.NotReady, false)]
        [InlineData(Result.Success, true)]
        public void NativeQueryStatusDoesNotImplyAvailability(Result result, bool expected)
        {
            using var native = new VulkanTimestampNativeContext() { ReadResult = result };
            using var vk = new Vk(native);
            var alive = new DeviceLiveness();
            var api = new VulkanTimestampApi(vk, new Device(1), new VulkanDeviceLossLatch(alive), alive);
            ulong handle = api.CreatePool(3);
            Assert.Equal(expected, api.TryRead(handle, 3, new ulong[3], new ulong[3]));
            api.DestroyPool(handle);
            Assert.Equal(1, native.DestroyCalls);
        }

        [Fact]
        public void QueryLossLatchesTheSiteAndDeadDevicesRefuseRecordingAndResolution()
        {
            using var native = new VulkanTimestampNativeContext();
            using var vk = new Vk(native);
            var alive = new DeviceLiveness();
            var loss = new VulkanDeviceLossLatch(alive);
            var api = new VulkanTimestampApi(vk, new Device(1), loss, alive);
            ulong handle = api.CreatePool(3);
            native.ReadResult = Result.ErrorDeviceLost;
            Assert.Throws<InvalidOperationException>(() => api.TryRead(handle, 3, new ulong[3], new ulong[3]));
            Assert.True(alive.IsDead);
            Assert.Contains("vkGetQueryPoolResults", loss.Site);
            Assert.Throws<InvalidOperationException>(() => api.Reset(1, handle, 3));
            Assert.Throws<InvalidOperationException>(() => api.Write(1, handle, 0));
            api.DestroyPool(handle);
            Assert.Equal(0, native.DestroyCalls);

            var rig = new VulkanResourceFixture();
            using var pool = new VulkanTimestampPool(new FakeTimestampApi(), rig.Timeline, rig.Owner, 3, 36, 2.5);
            using var list = List(rig);
            using var fence = rig.Timeline.CreateFence();
            Record(list, pool, 3);
            rig.Liveness.Kill();
            Assert.Throws<InvalidOperationException>(() => pool.TryResolve(fence, new ulong[3], out _));
        }

        static GpuCapabilities Capabilities(int bits, double period)
            => VulkanCapabilityRead.Assemble("probe", false, false, GpuSampleCounts.One, 4,
                new VulkanTimestampSupport(bits, period));

        static VulkanResourceFactory Factory(VulkanResourceFixture rig, IVulkanTimestampApi api,
            int bits = 36, double period = 2.5)
            => new(rig.Owner, rig.Rings, rig.Setup, rig.Descriptors, rig.Modules, rig.Pipelines,
                () => List(rig), () => rig.Timeline.CreateFence(), Capabilities(bits, period),
                createTimestampApi: () => api, timestamps: new VulkanTimestampSupport(bits, period));

        static VulkanCommandList List(VulkanResourceFixture rig)
            => new(new VulkanCommandPoolRing(rig.CommandApi, 3, rig.Timeline, rig.Backpressure),
                rig.Retired, render: rig.RenderApi, timestampTimeline: rig.Timeline);

        static void Record(VulkanCommandList list, IGpuTimestampPool pool, uint count)
        {
            list.Begin();
            list.BeginTimestamps(pool, count);
            for (uint i = 0; i < count; i++) list.WriteTimestamp(pool, i);
            list.EndTimestamps(pool);
            list.End();
        }

        sealed class FakeTimestampApi : IVulkanTimestampApi
        {
            internal ulong[] Values { get; set; } = [250, 5, 9];
            internal int CreateCalls { get; private set; }
            internal int ReadCalls { get; private set; }
            internal int DestroyCalls { get; private set; }
            public ulong CreatePool(uint capacity) { CreateCalls++; return 7; }
            public void DestroyPool(ulong pool) => DestroyCalls++;
            public void Reset(ulong commandBuffer, ulong pool, uint count) { }
            public void Write(ulong commandBuffer, ulong pool, uint index) { }
            public bool TryRead(ulong pool, uint count, Span<ulong> values, Span<ulong> availability)
            {
                ReadCalls++;
                Values.AsSpan(0, (int)count).CopyTo(values);
                availability.Fill(1);
                return true;
            }
        }
    }
}
