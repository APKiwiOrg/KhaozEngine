using System;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    [Collection("AllocSensitive")]
    public sealed class GpuBufferReadbackRingTests
    {
        [Fact]
        public void FullRingDoesNotWaitAndPreservesFifoTags()
        {
            var gpu = new RingGpu(3);
            using var source = new RingBuffer(16);
            Write(source, 1, 2, 3, 4);
            var first = new RingFence();
            var second = new RingFence();
            var third = new RingFence();
            var fourth = new RingFence();
            using var ring = new GpuBufferReadbackRing(gpu, 3, 16);
            Assert.Equal(3, gpu.CreatedCount);
            foreach (RingBuffer staging in gpu.Buffers)
            {
                Assert.Equal(16u, staging.SizeInBytes);
                Assert.Equal(GpuBufferUsage.Staging, staging.Usage);
            }

            Assert.True(ring.TryRecord(gpu, source, 0, 16, 100ul, first));
            Write(source, 5, 6, 7, 8);
            Assert.True(ring.TryRecord(gpu, source, 0, 16, 101ul, second));
            Write(source, 9, 10, 11, 12);
            Assert.True(ring.TryRecord(gpu, source, 0, 16, 102ul, third));
            using var region = AllocationRegion.Start("full readback ring refusal");
            long before = GC.GetAllocatedBytesForCurrentThread();
            bool full = ring.TryRecord(gpu, source, 0, 16, 103ul, fourth);
            long allocationDelta = GC.GetAllocatedBytesForCurrentThread() - before;
            region.Dispose();
            Assert.False(full);
            Assert.Equal(0L, allocationDelta);
            Assert.Equal(3, gpu.CopyCalls);
            Assert.Equal(3, gpu.CreatedCount);

            byte[] destination = Filled(16);
            Assert.False(ring.TryRead(destination, out ulong tag, out uint count));
            Assert.Equal(0ul, tag);
            Assert.Equal(0u, count);
            Assert.Equal(Filled(16), destination);
            Assert.Equal(0, second.PollCalls);
            second.Signaled = true;
            Assert.False(ring.TryRead(destination, out tag, out count));
            Assert.Equal(0ul, tag);
            Assert.Equal(0u, count);
            Assert.Equal(Filled(16), destination);
            Assert.Equal(3, ring.Capacity);
            Assert.Equal(3, ring.PendingCount);

            first.Signaled = true;
            Read(ring, destination, 100ul, 1, 2, 3, 4);
            Write(source, 13, 14, 15, 16);
            Assert.True(ring.TryRecord(gpu, source, 0, 16, 103ul, fourth));
            third.Signaled = fourth.Signaled = true;
            Read(ring, destination, 101ul, 5, 6, 7, 8);
            Read(ring, destination, 102ul, 9, 10, 11, 12);
            Read(ring, destination, 103ul, 13, 14, 15, 16);
            Assert.Equal(0, ring.PendingCount);
            Assert.False(ring.TryRead(destination, out tag, out count));
            Assert.Equal(0ul, tag);
            Assert.Equal(0u, count);
            Assert.Equal(0, gpu.WaitCalls);
            Assert.Equal(0, gpu.FlushCalls);
            Assert.Equal(0, gpu.SubmitCalls);
            Assert.All(new[] { first, second, third, fourth }, fence =>
            {
                Assert.Equal(0, fence.ResetCalls);
                Assert.Equal(0, fence.DisposeCalls);
            });
        }

        [Fact]
        public void DiscardedRecordingReleasesOnlyItsUnsubmittedSlots()
        {
            var gpu = new RingGpu(3);
            using var source = new RingBuffer(16);
            var abandoned = new RingFence();
            var retained = new RingFence();
            using var ring = new GpuBufferReadbackRing(gpu, 3, 16);
            Assert.True(ring.TryRecord(gpu, source, 0, 16, 200ul, abandoned));
            Assert.True(ring.TryRecord(gpu, source, 0, 16, 201ul, abandoned));
            Write(source, 21, 22, 23, 24);
            Assert.True(ring.TryRecord(gpu, source, 0, 16, 202ul, retained));
            ring.DiscardUnsubmitted(abandoned);
            Assert.Equal(1, ring.PendingCount);
            Assert.Equal(0, gpu.ReadCalls);
            Assert.Equal(0, abandoned.PollCalls);
            Assert.Equal(0, abandoned.ResetCalls);
            Assert.Equal(0, abandoned.DisposeCalls);
            Assert.All(gpu.Buffers, buffer => Assert.Equal(0, buffer.DisposeCalls));
            Write(source, 31, 32, 33, 34);
            Assert.True(ring.TryRecord(gpu, source, 0, 16, 203ul, abandoned));
            Assert.True(ring.TryRecord(gpu, source, 0, 16, 204ul, abandoned));
            retained.Signaled = true;
            Read(ring, new byte[16], 202ul, 21, 22, 23, 24);
            ring.DiscardUnsubmitted(abandoned);
            ring.Dispose();
            Assert.All(gpu.Buffers, buffer => Assert.Equal(1, buffer.DisposeCalls));
            Assert.Equal(0, source.DisposeCalls);
            Assert.Equal(0, retained.ResetCalls);
            Assert.Equal(0, retained.DisposeCalls);
        }

        [Fact]
        public void DiscardKeepsSurvivorOrderAndCanRemoveNonAdjacentEntries()
        {
            var gpu = new RingGpu(4);
            using var source = new RingBuffer(16);
            var abandoned = new RingFence();
            var first = new RingFence { Signaled = true };
            var second = new RingFence { Signaled = true };
            using var ring = new GpuBufferReadbackRing(gpu, 4, 16);
            Assert.True(ring.TryRecord(gpu, source, 0, 4, 1, first));
            Assert.True(ring.TryRecord(gpu, source, 0, 4, 2, abandoned));
            Assert.True(ring.TryRecord(gpu, source, 0, 4, 3, second));
            Assert.True(ring.TryRecord(gpu, source, 0, 4, 4, abandoned));
            ring.DiscardUnsubmitted(new RingFence());
            Assert.Equal(4, ring.PendingCount);
            ring.DiscardUnsubmitted(abandoned);
            Assert.Equal(2, ring.PendingCount);
            Assert.True(ring.TryRecord(gpu, source, 0, 4, 5, first));
            Assert.True(ring.TryRecord(gpu, source, 0, 4, 6, second));
            var destination = new byte[4];
            foreach (ulong expected in new ulong[] { 1, 3, 5, 6 })
            {
                Assert.True(ring.TryRead(destination, out ulong tag, out uint count));
                Assert.Equal(expected, tag);
                Assert.Equal(4u, count);
            }
        }

        [Theory]
        [InlineData(0, 16u, "slotCount")]
        [InlineData(-1, 16u, "slotCount")]
        [InlineData(3, 0u, "slotSizeInBytes")]
        public void InvalidCapacityIsRefusedBeforeAllocation(int slots, uint bytes, string parameter)
        {
            var gpu = new RingGpu(3);
            Assert.Equal(parameter, Assert.Throws<ArgumentOutOfRangeException>(
                () => new GpuBufferReadbackRing(gpu, slots, bytes)).ParamName);
            Assert.Equal(0, gpu.CreatedCount);
        }

        [Theory]
        [InlineData(0u, 17u, "sizeInBytes")]
        [InlineData(0u, 0u, "sizeInBytes")]
        [InlineData(3u, 4u, "sourceOffsetBytes")]
        [InlineData(20u, 4u, "sourceOffsetBytes")]
        [InlineData(12u, 8u, "sizeInBytes")]
        [InlineData(4294967292u, 8u, "sourceOffsetBytes")]
        public void InvalidCopyDoesNotRecordOrReserveASlot(uint offset, uint bytes, string parameter)
        {
            var gpu = new RingGpu(1);
            using var source = new RingBuffer(16);
            var fence = new RingFence();
            using var ring = new GpuBufferReadbackRing(gpu, 1, 16);
            Assert.Equal(parameter, Assert.Throws<ArgumentOutOfRangeException>(
                () => ring.TryRecord(gpu, source, offset, bytes, 1, fence)).ParamName);
            Assert.Equal(0, gpu.CopyCalls);
            Assert.Equal(0, ring.PendingCount);
            Assert.True(ring.TryRecord(gpu, source, 0, 16, 2, fence));
            ring.DiscardUnsubmitted(fence);
        }

        [Fact]
        public void CopyLargerThanASpanIsRefusedBeforeItCanBecomePending()
        {
            // These existing inert handles store only the logical size, with no large backing allocation.
            var gpu = new FakeGpuDevice();
            using var source = new FakeBuffer(uint.MaxValue);
            using IGpuCommandList commands = gpu.Factory.CreateCommandList();
            var fence = new RingFence();
            using var ring = new GpuBufferReadbackRing(gpu, 1, uint.MaxValue);
            try
            {
                Assert.Equal("sizeInBytes", Assert.Throws<ArgumentOutOfRangeException>(
                    () => ring.TryRecord(commands, source, 0, (uint)int.MaxValue + 1, 1, fence)).ParamName);
                Assert.Equal(0, ring.PendingCount);
            }
            finally
            {
                ring.DiscardUnsubmitted(fence);
            }
        }

        [Fact]
        public void SmallDestinationAndBusyReadRetainTheEntryAndLeaveBytesUntouched()
        {
            var gpu = new RingGpu(1);
            using var source = new RingBuffer(16);
            Write(source, 1, 2, 3, 4);
            var fence = new RingFence { Signaled = true };
            using var ring = new GpuBufferReadbackRing(gpu, 1, 16);
            Assert.True(ring.TryRecord(gpu, source, 4, 8, 300ul, fence));
            byte[] small = Filled(7);
            Assert.Equal("destination", Assert.Throws<ArgumentException>(
                () => ring.TryRead(small, out _, out _)).ParamName);
            Assert.Equal(Filled(7), small);
            Assert.Equal(0, gpu.ReadCalls);
            Assert.Equal(1, ring.PendingCount);

            byte[] destination = Filled(16);
            gpu.Busy = true;
            Assert.False(ring.TryRead(destination, out ulong tag, out uint count));
            Assert.Equal(0ul, tag);
            Assert.Equal(0u, count);
            Assert.Equal(Filled(16), destination);
            Assert.Equal(1, ring.PendingCount);
            gpu.Busy = false;
            Assert.True(ring.TryRead(destination, out tag, out count));
            Assert.Equal(300ul, tag);
            Assert.Equal(8u, count);
            Assert.Equal(new uint[] { 2, 3 }, MemoryMarshal.Cast<byte, uint>(destination.AsSpan(0, 8)).ToArray());
            Assert.Equal(Filled(8), destination.AsSpan(8).ToArray());
        }

        [Fact]
        public void FailedCopyLeavesTheSlotAvailable()
        {
            var gpu = new RingGpu(1) { FailCopy = true };
            using var source = new RingBuffer(16);
            var fence = new RingFence();
            using var ring = new GpuBufferReadbackRing(gpu, 1, 16);
            Assert.Throws<InvalidOperationException>(() => ring.TryRecord(gpu, source, 0, 16, 1, fence));
            Assert.Equal(0, ring.PendingCount);
            gpu.FailCopy = false;
            Assert.True(ring.TryRecord(gpu, source, 0, 16, 2, fence));
            ring.DiscardUnsubmitted(fence);
        }

        [Fact]
        public void PendingDisposalRefusesWithoutFreeingBuffersOrFences()
        {
            var gpu = new RingGpu(1);
            using var source = new RingBuffer(16);
            var fence = new RingFence();
            using var ring = new GpuBufferReadbackRing(gpu, 1, 16);
            Assert.True(ring.TryRecord(gpu, source, 0, 16, 1, fence));
            Assert.Throws<InvalidOperationException>(() => ring.Dispose());
            Assert.Equal(0, gpu.Buffers[0].DisposeCalls);
            Assert.Equal(1, ring.PendingCount);
            fence.Signaled = true;
            Assert.True(ring.TryRead(new byte[16], out _, out _));
            ring.Dispose();
            ring.Dispose();
            Assert.Equal(1, gpu.Buffers[0].DisposeCalls);
            Assert.Equal(0, fence.ResetCalls);
            Assert.Equal(0, fence.DisposeCalls);
            Assert.Throws<ObjectDisposedException>(() => ring.TryRecord(gpu, source, 0, 16, 2, fence));
            Assert.Throws<ObjectDisposedException>(() => ring.TryRead(new byte[16], out _, out _));
            Assert.Throws<ObjectDisposedException>(() => ring.DiscardUnsubmitted(fence));
        }

        [Fact]
        public void ConstructorFailureDisposesOnlyAlreadyCreatedStagingBuffers()
        {
            var gpu = new RingGpu(3) { FailCreateAt = 2 };
            Assert.Throws<InvalidOperationException>(() => new GpuBufferReadbackRing(gpu, 3, 16));
            Assert.Equal(1, gpu.CreatedCount);
            Assert.Equal(1, gpu.Buffers[0].DisposeCalls);
        }

        [Fact]
        public void NullArgumentsAreRefusedWithoutReservingSlots()
        {
            var gpu = new RingGpu(1);
            using var source = new RingBuffer(16);
            var fence = new RingFence();
            Assert.Throws<ArgumentNullException>(() => new GpuBufferReadbackRing(null!, 1, 16));
            using var ring = new GpuBufferReadbackRing(gpu, 1, 16);
            Assert.Throws<ArgumentNullException>(() => ring.TryRecord(null!, source, 0, 16, 1, fence));
            Assert.Throws<ArgumentNullException>(() => ring.TryRecord(gpu, null!, 0, 16, 1, fence));
            Assert.Throws<ArgumentNullException>(() => ring.TryRecord(gpu, source, 0, 16, 1, null!));
            Assert.Throws<ArgumentNullException>(() => ring.DiscardUnsubmitted(null!));
            Assert.Equal(0, ring.PendingCount);
            Assert.Equal(0, gpu.CopyCalls);
        }

        [Fact]
        public void WarmRecordAndReadAllocateNothing()
        {
            var gpu = new RingGpu(1);
            using var source = new RingBuffer(16);
            Write(source, 1, 2, 3, 4);
            var fence = new RingFence();
            var destination = new byte[16];
            using var ring = new GpuBufferReadbackRing(gpu, 1, 16);
            Assert.True(ring.TryRecord(gpu, source, 0, 16, 1, fence));
            fence.Signaled = true;
            Read(ring, destination, 1, 1, 2, 3, 4);
            fence.Reset();

            using var region = AllocationRegion.Start("warm readback record and read");
            long before = GC.GetAllocatedBytesForCurrentThread();
            bool recorded = ring.TryRecord(gpu, source, 0, 16, 2, fence);
            fence.Signaled = true;
            bool read = ring.TryRead(destination, out ulong tag, out uint count);
            long allocationDelta = GC.GetAllocatedBytesForCurrentThread() - before;
            region.Dispose();

            Assert.True(recorded);
            Assert.True(read);
            Assert.Equal(2ul, tag);
            Assert.Equal(16u, count);
            Assert.Equal(0L, allocationDelta);
            Assert.Equal(1, gpu.CreatedCount);
        }

        static void Write(RingBuffer buffer, params uint[] words)
            => words.AsSpan().CopyTo(MemoryMarshal.Cast<byte, uint>(buffer.Bytes.AsSpan()));

        static void Read(GpuBufferReadbackRing ring, byte[] destination, ulong expectedTag, params uint[] words)
        {
            Assert.True(ring.TryRead(destination, out ulong tag, out uint count));
            Assert.Equal(expectedTag, tag);
            Assert.Equal(16u, count);
            Assert.Equal(words, MemoryMarshal.Cast<byte, uint>(destination).ToArray());
        }

        static byte[] Filled(int length)
        {
            var bytes = new byte[length];
            bytes.AsSpan().Fill(0xcc);
            return bytes;
        }

        sealed class RingFence : IGpuFence
        {
            bool _signaled;
            internal int PollCalls, ResetCalls, DisposeCalls;
            public bool Signaled { get { PollCalls++; return _signaled; } set => _signaled = value; }
            public void Reset() { ResetCalls++; _signaled = false; }
            public void Dispose() => DisposeCalls++;
        }

        sealed class RingBuffer : IGpuBuffer
        {
            internal readonly byte[] Bytes;
            internal readonly GpuBufferUsage Usage;
            internal int DisposeCalls;
            internal RingBuffer(uint bytes, GpuBufferUsage usage = GpuBufferUsage.StructuredBufferReadWrite)
            { Bytes = new byte[bytes]; Usage = usage; }
            public uint SizeInBytes => (uint)Bytes.Length;
            public void Dispose() => DisposeCalls++;
        }

        // The only data operations are the ring's recorded copy and completed non-blocking read. Unused
        // commands throw, so a hidden map, submission or drain cannot accidentally satisfy the assertions.
        sealed class RingGpu : IGpuDevice, IGpuResourceFactory, IGpuCommandList
        {
            internal readonly RingBuffer[] Buffers;
            internal int CreatedCount, CopyCalls, ReadCalls, WaitCalls, FlushCalls, SubmitCalls;
            internal int FailCreateAt;
            internal bool Busy, FailCopy;
            internal RingGpu(int capacity) => Buffers = new RingBuffer[capacity];
            public IGpuResourceFactory Factory => this;
            public GpuBackendKind Backend => GpuBackendKind.MetalNative;
            public GpuCapabilities Capabilities => new(false, true, "RingGpu", supportsCompletionFences: true);
            public IGpuFramebuffer? SwapchainFramebuffer => null;
            public IGpuSampler PointSampler => throw new NotSupportedException();
            public IGpuSampler LinearSampler => throw new NotSupportedException();
            public bool SyncToVerticalBlank { get; set; }

            public IGpuBuffer CreateBuffer(in GpuBufferDescription d)
            {
                if (FailCreateAt == CreatedCount + 1) throw new InvalidOperationException("planned creation failure");
                var buffer = new RingBuffer(d.SizeInBytes, d.Usage);
                Buffers[CreatedCount++] = buffer;
                return buffer;
            }

            public void CopyBuffer(IGpuBuffer src, uint srcOffsetBytes, IGpuBuffer dst, uint dstOffsetBytes, uint sizeInBytes)
            {
                if (FailCopy) throw new InvalidOperationException("planned copy failure");
                CopyCalls++;
                ((RingBuffer)src).Bytes.AsSpan((int)srcOffsetBytes, (int)sizeInBytes)
                    .CopyTo(((RingBuffer)dst).Bytes.AsSpan((int)dstOffsetBytes, (int)sizeInBytes));
            }

            public bool TryReadBuffer(IGpuBuffer staging, IGpuFence completion, uint offsetBytes, Span<byte> destination)
            {
                ReadCalls++;
                if (!completion.Signaled || Busy) return false;
                ((RingBuffer)staging).Bytes.AsSpan((int)offsetBytes, destination.Length).CopyTo(destination);
                return true;
            }

            public void Submit(IGpuCommandList cl) { SubmitCalls++; throw new NotSupportedException(); }
            public void Submit(IGpuCommandList cl, IGpuFence fence) { SubmitCalls++; throw new NotSupportedException(); }
            public void WaitForIdle() { WaitCalls++; FlushCalls++; throw new NotSupportedException(); }
            public MappedData Map(IGpuBuffer staging, GpuMapMode mode) { WaitCalls++; throw new NotSupportedException(); }
            public MappedData Map(IGpuTexture staging, GpuMapMode mode) => throw new NotSupportedException();
            public void Unmap(IGpuBuffer staging) => throw new NotSupportedException();
            public void Unmap(IGpuTexture staging) => throw new NotSupportedException();
            public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, ReadOnlySpan<T> data) where T : unmanaged => throw new NotSupportedException();
            public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, T[] data) where T : unmanaged => throw new NotSupportedException();
            public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, in T data) where T : unmanaged => throw new NotSupportedException();
            public void UpdateTexture(IGpuTexture t, byte[] data, uint x, uint y, uint w, uint h) => throw new NotSupportedException();
            public void UpdateTexture(IGpuTexture t, byte[] data, uint x, uint y, uint w, uint h, uint mip, uint layer) => throw new NotSupportedException();
            public void ResizeSwapchain(uint w, uint h) => throw new NotSupportedException();
            public void Present() { FlushCalls++; throw new NotSupportedException(); }
            public IGpuTexture CreateTexture(in GpuTextureDescription d) => throw new NotSupportedException();
            public IGpuFramebuffer CreateFramebuffer(IGpuTexture? depth, params IGpuTexture[] colour) => throw new NotSupportedException();
            public IGpuSampler CreateSampler(in GpuSamplerDescription d) => throw new NotSupportedException();
            public IGpuResourceLayout CreateResourceLayout(in GpuResourceLayoutDescription d) => throw new NotSupportedException();
            public IGpuResourceSet CreateResourceSet(in GpuResourceSetDescription d) => throw new NotSupportedException();
            public IGpuShaderSet CreateShadersFromSpirv(string v, string f) => throw new NotSupportedException();
            public IGpuComputeShader CreateComputeShaderFromSpirv(string c) => throw new NotSupportedException();
            public IGpuPipeline CreateGraphicsPipeline(in GpuPipelineDescription d) => throw new NotSupportedException();
            public IGpuComputePipeline CreateComputePipeline(in GpuComputePipelineDescription d) => throw new NotSupportedException();
            public IGpuCommandList CreateCommandList() => throw new NotSupportedException();
            public IGpuFence CreateFence() => throw new NotSupportedException();
            public void Begin() => throw new NotSupportedException();
            public void End() => throw new NotSupportedException();
            public void SetFramebuffer(IGpuFramebuffer fb) => throw new NotSupportedException();
            public void ClearColorTarget(uint index, Color rgba) => throw new NotSupportedException();
            public void ClearDepthStencil(float depth) => throw new NotSupportedException();
            public void SetPipeline(IGpuPipeline p) => throw new NotSupportedException();
            public void SetGraphicsResourceSet(uint slot, IGpuResourceSet set) => throw new NotSupportedException();
            public void SetGraphicsResourceSet(uint slot, IGpuResourceSet set, uint dynamicOffset) => throw new NotSupportedException();
            public void SetVertexBuffer(uint slot, IGpuBuffer b) => throw new NotSupportedException();
            public void SetVertexBuffer(uint slot, IGpuBuffer b, uint offsetBytes) => throw new NotSupportedException();
            public void SetIndexBuffer(IGpuBuffer b, GpuIndexFormat fmt) => throw new NotSupportedException();
            public void SetScissorRect(uint index, uint x, uint y, uint w, uint h) => throw new NotSupportedException();
            public void SetFullScissorRects() => throw new NotSupportedException();
            public void Draw(uint count, uint instances, uint start, uint instanceStart) => throw new NotSupportedException();
            public void Draw(uint count) => throw new NotSupportedException();
            public void DrawIndexed(uint count, uint instances, uint start, int offset, uint instanceStart) => throw new NotSupportedException();
            public void CopyTexture(IGpuTexture src, IGpuTexture dst) => throw new NotSupportedException();
            public void CopyTextureSubresource(IGpuTexture src, uint mip, uint layer, IGpuTexture dst, uint w, uint h) => throw new NotSupportedException();
            public void CopyTextureSubresource(IGpuTexture src, uint srcMip, uint srcLayer, IGpuTexture dst,
                uint dstMip, uint dstLayer, uint w, uint h) => throw new NotSupportedException();
            public void GenerateMipmaps(IGpuTexture texture) => throw new NotSupportedException();
            public void ResolveTexture(IGpuTexture src, IGpuTexture dst) => throw new NotSupportedException();
            public void SetComputePipeline(IGpuComputePipeline p) => throw new NotSupportedException();
            public void SetComputeResourceSet(uint slot, IGpuResourceSet set) => throw new NotSupportedException();
            public void SetComputeResourceSet(uint slot, IGpuResourceSet set, uint offset) => throw new NotSupportedException();
            public void Dispatch(uint x, uint y, uint z) => throw new NotSupportedException();
            public void Dispose() { }
        }
    }
}
