using System;
using System.Threading;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11.Internal;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Gpu.Vulkan.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE NON-BLOCKING READ OF A COMPLETED STAGING COPY, device-free. <c>IGpuDevice.TryReadBuffer</c> exists so
    /// a consumer can collect a small result a few frames after recording its copy without ever stalling, so the
    /// facts worth pinning off a GPU are the negative ones: a pending completion or a busy immediate context
    /// answers false with the destination untouched, and neither ever reaches a drain, a flush or a native map.
    /// Direct3D 11 is the backend whose whole read path sits behind fakes (<see cref="FakeD3D11StagingMemory"/>
    /// and <see cref="FakeD3D11FenceTimeline"/>), so it carries the call tracing. Metal and Vulkan contribute
    /// their fence ownership refusals, which are the device-free half of their own paths.
    /// </summary>
    public sealed class GpuAsyncBufferReadTests
    {
        const uint StagingBytes = 16;

        sealed class Rig : IDisposable
        {
            internal Rig()
            {
                Timeline = new FakeD3D11FenceTimeline { SubmitLock = SubmitLock };
                Memory = new FakeD3D11StagingMemory(bytes: (int)StagingBytes) { SubmitLock = SubmitLock };
                Fences = new D3D11FenceSubsystem(Timeline, SubmitLock, Liveness);
                Reads = new D3D11CompletedReads(new D3D11StagingAccess(Memory, SubmitLock), Fences, SubmitLock,
                    Liveness);
                Staging = new FakeStagingBuffer(StagingBytes, owner: Liveness);
                for (int i = 0; i < Memory.Bytes.Length; i++) Memory.Bytes[i] = (byte)(0x10 + i);
            }

            internal object SubmitLock { get; } = new();
            internal FakeD3D11DeviceLiveness Liveness { get; } = new();
            internal FakeD3D11FenceTimeline Timeline { get; }
            internal FakeD3D11StagingMemory Memory { get; }
            internal D3D11FenceSubsystem Fences { get; }
            internal D3D11CompletedReads Reads { get; }
            internal FakeStagingBuffer Staging { get; }

            // A fence armed by a real end-of-replay signal, exactly as Submit arms one.
            internal IGpuFence SubmittedFence()
            {
                IGpuFence fence = Fences.CreateFence();
                Fences.SignalEndOfReplay(fence);
                return fence;
            }

            public void Dispose()
            {
                Fences.Dispose();
                Memory.Dispose();
            }
        }

        static byte[] Untouched() => new byte[] { 0xcc, 0xcc, 0xcc, 0xcc };

        [Fact]
        public void TheSeamDefault_RefusesRatherThanPretendingToRead()
        {
            IGpuDevice device = new FakeGpuDevice();
            using var staging = new FakeStagingBuffer(StagingBytes);
            var destination = new byte[4];

            Assert.Throws<NotSupportedException>(
                () => device.TryReadBuffer(staging, new RecordedFence(), 0, destination));
        }

        [Fact]
        public void PendingAndBusyReadsNeitherTouchMemoryNorDrain()
        {
            using var rig = new Rig();
            IGpuFence completion = rig.SubmittedFence();
            byte[] destination = Untouched();

            // ---- Completion pending: the GPU has not reached the fence's value ---------------------------------
            bool read = rig.Reads.TryReadBuffer(rig.Staging, completion, 0, destination);
            int waitCalls = rig.Timeline.WaitCallCount;
            int flushCalls = rig.Timeline.FlushCount;
            int nativeMapCalls = rig.Memory.Calls.Count;

            Assert.False(read);
            Assert.Equal(new byte[] { 0xcc, 0xcc, 0xcc, 0xcc }, destination);
            Assert.Equal(0, waitCalls);
            Assert.Equal(0, flushCalls);
            Assert.Equal(0, nativeMapCalls);
            Assert.Equal(1, rig.Timeline.PollCount);

            // ---- Completion reached, but another thread holds the immediate context -------------------------
            rig.Timeline.Completed = rig.Timeline.Issued;
            using var held = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var holder = new Thread(() =>
            {
                lock (rig.SubmitLock)
                {
                    held.Set();
                    release.Wait();
                }
            });
            holder.Start();
            held.Wait();

            bool busy;
            try
            {
                busy = rig.Reads.TryReadBuffer(rig.Staging, completion, 0, destination);
            }
            finally
            {
                release.Set();
                holder.Join();
            }

            Assert.False(busy);
            Assert.Equal(Untouched(), destination);
            Assert.Equal(0, rig.Timeline.WaitCallCount);
            Assert.Equal(0, rig.Timeline.FlushCount);
            Assert.Empty(rig.Memory.Calls);
            Assert.Equal(1, rig.Timeline.PollCount);
        }

        [Fact]
        public void ARuntimeStillDrawingTheResource_AnswersFalseAndLeavesNothingMapped()
        {
            using var rig = new Rig();
            IGpuFence completion = rig.SubmittedFence();
            rig.Timeline.Completed = rig.Timeline.Issued;
            rig.Memory.TryMapResult = D3D11StagingAccess.WasStillDrawing;
            byte[] destination = Untouched();

            Assert.False(rig.Reads.TryReadBuffer(rig.Staging, completion, 0, destination));

            Assert.Equal(Untouched(), destination);
            Assert.Equal(new[] { nameof(ID3D11StagingMemory.TryMapBufferRead) }, rig.Memory.Calls);
            Assert.False(rig.Memory.IsMapped(rig.Staging.MapTarget));
            Assert.Equal(0, rig.Timeline.WaitCallCount);
            Assert.Equal(0, rig.Timeline.FlushCount);

            // And the next attempt is a fresh one rather than a double map.
            rig.Memory.TryMapResult = D3D11DeviceLossCodes.Ok;
            Assert.True(rig.Reads.TryReadBuffer(rig.Staging, completion, 0, destination));
        }

        [Fact]
        public void ACompletedRead_CopiesTheSliceUnderTheLockAndClosesTheMapBeforeReturning()
        {
            using var rig = new Rig();
            IGpuFence completion = rig.SubmittedFence();
            rig.Timeline.Completed = rig.Timeline.Issued;
            var destination = new byte[8];

            Assert.True(rig.Reads.TryReadBuffer(rig.Staging, completion, 4, destination));

            Assert.Equal(new byte[] { 0x14, 0x15, 0x16, 0x17, 0x18, 0x19, 0x1a, 0x1b }, destination);
            Assert.Equal(
                new[] { nameof(ID3D11StagingMemory.TryMapBufferRead), nameof(ID3D11StagingMemory.UnmapBuffer) },
                rig.Memory.Calls);
            Assert.True(rig.Memory.EveryCallHeldTheSubmitLock);
            Assert.False(rig.Memory.IsMapped(rig.Staging.MapTarget));
            Assert.False(Monitor.IsEntered(rig.SubmitLock));
            Assert.Equal(0, rig.Timeline.WaitCallCount);
            Assert.Equal(0, rig.Timeline.FlushCount);
        }

        [Fact]
        public void AMapFailureOtherThanStillDrawing_FollowsTheDeviceLossPath()
        {
            using var rig = new Rig();
            IGpuFence completion = rig.SubmittedFence();
            rig.Timeline.Completed = rig.Timeline.Issued;
            rig.Memory.TryMapResult = D3D11DeviceLossCodes.DeviceRemoved;
            byte[] destination = Untouched();

            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
                () => rig.Reads.TryReadBuffer(rig.Staging, completion, 0, destination));

            Assert.Contains(D3D11StagingMaps.MapSite, ex.Message, StringComparison.Ordinal);
            Assert.Equal(Untouched(), destination);
            Assert.False(Monitor.IsEntered(rig.SubmitLock));
        }

        [Fact]
        public void EveryRefusal_IsNamedBeforeAnyPollOrPointer()
        {
            using var rig = new Rig();
            IGpuFence completion = rig.SubmittedFence();
            rig.Timeline.Completed = rig.Timeline.Issued;
            var four = new byte[4];

            void NothingReached()
            {
                Assert.Empty(rig.Memory.Calls);
                Assert.Equal(0, rig.Timeline.PollCount);
            }

            // A foreign fence: another D3D11 device's, and another backend's.
            using var otherTimeline = new FakeD3D11FenceTimeline();
            using var otherFences = new D3D11FenceSubsystem(otherTimeline, new object());
            IGpuFence foreign = otherFences.CreateFence();
            otherFences.SignalEndOfReplay(foreign);
            Assert.Equal("completion", Assert.Throws<ArgumentException>(
                () => rig.Reads.TryReadBuffer(rig.Staging, foreign, 0, four)).ParamName);
            Assert.Equal("completion", Assert.Throws<ArgumentException>(
                () => rig.Reads.TryReadBuffer(rig.Staging, new RecordedFence(), 0, four)).ParamName);
            NothingReached();

            // A disposed fence.
            IGpuFence disposedFence = rig.SubmittedFence();
            disposedFence.Dispose();
            Assert.Throws<ObjectDisposedException>(
                () => rig.Reads.TryReadBuffer(rig.Staging, disposedFence, 0, four));
            NothingReached();

            // A non-staging source, a buffer from another device, another backend's buffer and a disposed one.
            using var dynamic = new FakeStagingBuffer(StagingBytes, staging: false, owner: rig.Liveness);
            Assert.Equal("staging", Assert.Throws<ArgumentException>(
                () => rig.Reads.TryReadBuffer(dynamic, completion, 0, four)).ParamName);
            using var otherDevices = new FakeStagingBuffer(StagingBytes, owner: new FakeD3D11DeviceLiveness());
            Assert.Equal("staging", Assert.Throws<ArgumentException>(
                () => rig.Reads.TryReadBuffer(otherDevices, completion, 0, four)).ParamName);
            Assert.Equal("staging", Assert.Throws<ArgumentException>(
                () => rig.Reads.TryReadBuffer(new FakeBuffer(StagingBytes), completion, 0, four)).ParamName);
            var disposedStaging = new FakeStagingBuffer(StagingBytes, owner: rig.Liveness);
            disposedStaging.Dispose();
            Assert.Throws<ObjectDisposedException>(
                () => rig.Reads.TryReadBuffer(disposedStaging, completion, 0, four));
            NothingReached();

            // Offsets: unaligned, past the end, and a window that runs off the end.
            Assert.Equal("offsetBytes", Assert.Throws<ArgumentOutOfRangeException>(
                () => rig.Reads.TryReadBuffer(rig.Staging, completion, 3, four)).ParamName);
            Assert.Equal("offsetBytes", Assert.Throws<ArgumentOutOfRangeException>(
                () => rig.Reads.TryReadBuffer(rig.Staging, completion, 20, Span<byte>.Empty)).ParamName);
            Assert.Equal("destination", Assert.Throws<ArgumentOutOfRangeException>(
                () => rig.Reads.TryReadBuffer(rig.Staging, completion, 12, new byte[8])).ParamName);
            NothingReached();

            // A dead or disposed device, which is checked FIRST because a dead device's fence reads signalled.
            rig.Liveness.IsDead = true;
            Assert.Throws<ObjectDisposedException>(
                () => rig.Reads.TryReadBuffer(rig.Staging, completion, 0, four));
            NothingReached();
        }

        [Fact]
        public void MetalAndVulkanFences_RefuseAForeignOwnerAndADisposedFence()
        {
            var metalOwner = new MetalTimeline(new FakeMetalSharedEvent(), new FakeMetalDeviceLiveness());
            var metalOther = new MetalTimeline(new FakeMetalSharedEvent(), new FakeMetalDeviceLiveness());
            var metal = new MetalGpuFence(metalOwner);
            metal.RequireOwner(metalOwner);
            Assert.Equal("completion", Assert.Throws<ArgumentException>(
                () => metal.RequireOwner(metalOther)).ParamName);
            metal.Dispose();
            Assert.Throws<ObjectDisposedException>(() => metal.RequireOwner(metalOwner));

            using var vulkanOwner = new VulkanTimeline(new FakeVulkanTimelineSemaphore());
            using var vulkanOther = new VulkanTimeline(new FakeVulkanTimelineSemaphore());
            VulkanGpuFence vulkan = vulkanOwner.CreateFence();
            vulkan.RequireOwner(vulkanOwner);
            Assert.Equal("completion", Assert.Throws<ArgumentException>(
                () => vulkan.RequireOwner(vulkanOther)).ParamName);
            vulkan.Dispose();
            Assert.Throws<ObjectDisposedException>(() => vulkan.RequireOwner(vulkanOwner));
        }

        // A fence from no backend at all, for the seam default and the foreign-backend refusal.
        sealed class RecordedFence : IGpuFence
        {
            public bool Signaled => true;

            public void Reset()
            {
            }

            public void Dispose()
            {
            }
        }
    }
}
