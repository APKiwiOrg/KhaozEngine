using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Vulkan.Internal;
using Silk.NET.Vulkan;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// A STAGED RECORD-TIME UPLOAD ALLOCATES NOTHING, device-free: <see cref="VulkanBufferUpload.Record"/> driven
    /// with struct sinks, the way <see cref="VulkanListUploads"/> drives it with the real ones, and measured with
    /// <c>GC.GetAllocatedBytesForCurrentThread</c>.
    /// <para>
    /// <b>THE COPY SINK WAS BOXED ON EVERY UPLOAD.</b> The recorder took it as the interface, and the real
    /// <see cref="VulkanCopySink"/> is a readonly struct, so each staged upload allocated 32 bytes. A temporal frame
    /// stages two or three uploads more than the same frame without it, and the cross-backend frame reading failed on
    /// the native Vulkan leg alone by exactly that many boxes per frame. The real sink needs a real <c>Vk</c>, so
    /// this reading hands the recorder a struct of its own, which an interface parameter would box the same way.
    /// </para>
    /// <para>
    /// <b>ONE OPEN SLOT, AND THAT BOUND IS DELIBERATE.</b> Every upload measured by
    /// <see cref="AStagedUploadAllocatesNothing"/> bumps the block the warm-up opened, so that reading is the
    /// recorder's own. <see cref="ASteadyRotationOfArenaSlotsAllocatesNothing"/> is the arena's slot rotation, which
    /// is what every list <c>Begin</c> does before the frame's first staged upload.
    /// </para>
    /// <para>
    /// <b>THE SINKS AND THE SCOPE ARE LOCAL AND SILENT</b>, because the shared fakes log every call into a growing
    /// list, which is right for a routing test and exactly the allocation this one would then measure.
    /// </para>
    /// </summary>
    [Collection("AllocSensitive")]
    public sealed class VulkanStagingAllocationTests
    {
        const int UploadsPerPass = 16;
        const int PayloadBytes = 256;
        const uint DestinationBytes = UploadsPerPass * PayloadBytes;
        const ulong BlockBytes = 256UL * 1024;

        readonly byte[] _payload = new byte[PayloadBytes];

        [Theory]
        [InlineData(GpuBufferUsage.VertexBuffer)]
        [InlineData(GpuBufferUsage.StructuredBufferReadOnly)]
        public void AStagedUploadAllocatesNothing(GpuBufferUsage usage)
        {
            using var source = new NativeStagingSource();
            using var arena = new VulkanStagingArena(source, framesInFlight: 2, blockBytes: BlockBytes);
            arena.BeginSlot(0);

            var counts = new Counts();
            var scope = new CountingScope();
            var destination = new FakeVulkanUploadBuffer(0xDEAD, DestinationBytes, usage);

            // The warm-up opens the one block every measured upload bumps, and compiles the path.
            Pass(arena, counts, scope, destination);

            AllocAssert.NoPerCallAllocation($"{UploadsPerPass} staged uploads to a {usage} buffer",
                () => Pass(arena, counts, scope, destination));

            Assert.Equal(1, arena.BlocksCreated);
            Assert.Equal(1, arena.OpenBlockCount);
            Assert.True(counts.Copies >= 2 * UploadsPerPass,
                "fewer copies were recorded than uploads were made, so the reading above measured less than it claims");
            Assert.Equal(2 * counts.Copies, counts.Barriers);
            Assert.Equal(counts.Copies, scope.Ended);
        }

        /// <summary>
        /// Each frame opens the next slot, as a list <c>Begin</c> does, and stages uploads into it. The slot gives
        /// its block back to the pool and the first upload takes it out again, so after the warm-up no block is
        /// created and the measured frames must allocate nothing. A per-block record held as an object cost one
        /// allocation every time a slot reopened, which is once per recording on a steady frame.
        /// </summary>
        [Fact]
        public void ASteadyRotationOfArenaSlotsAllocatesNothing()
        {
            const int FramesInFlight = 3, MeasuredFrames = 24;
            using var source = new NativeStagingSource();
            using var arena = new VulkanStagingArena(source, FramesInFlight, blockBytes: BlockBytes);

            var counts = new Counts();
            var scope = new CountingScope();
            var destination = new FakeVulkanUploadBuffer(0xDEAD, DestinationBytes, GpuBufferUsage.VertexBuffer);
            int frame = 0;

            void Frames(int count)
            {
                for (int i = 0; i < count; i++, frame++)
                {
                    arena.BeginSlot(frame % FramesInFlight);
                    Pass(arena, counts, scope, destination);
                }
            }

            Frames(FramesInFlight * 2);

            AllocAssert.NoPerCallAllocation($"{MeasuredFrames} frames of arena slot rotation with staged uploads",
                () => Frames(MeasuredFrames));

            // One block per slot, recycled through the pool ever after, so the reading above is about the per-block
            // records rather than about blocks being created.
            Assert.Equal(FramesInFlight, arena.BlocksCreated);
            Assert.Equal(FramesInFlight, arena.OpenBlockCount);
            Assert.True(counts.Copies >= UploadsPerPass * MeasuredFrames,
                "fewer copies were recorded than uploads were made, so the reading above measured less than it claims");
        }

        void Pass(VulkanStagingArena arena, Counts counts, CountingScope scope, FakeVulkanUploadBuffer destination)
        {
            for (int i = 0; i < UploadsPerPass; i++)
            {
                VulkanBufferUpload.Record(new SilentCmdSink(counts), new SilentCopySink(counts), arena, scope,
                    destination, (ulong)i * PayloadBytes, _payload);
            }
        }

        /// <summary>What the two sinks saw, as counts rather than a log.</summary>
        sealed class Counts
        {
            internal int Copies { get; set; }

            internal int Barriers { get; set; }
        }

        /// <summary>The rendering scope, counting the pass ends the upload path owes.</summary>
        sealed class CountingScope : IVulkanRenderingScope
        {
            internal int Ended { get; private set; }

            public void EndActiveRendering() => Ended++;
        }

        /// <summary>The copy side, as the same shape the real one has: a readonly struct over one reference.</summary>
        readonly struct SilentCopySink : IVulkanUploadSink
        {
            readonly Counts _counts;

            internal SilentCopySink(Counts counts) => _counts = counts;

            public void CopyBuffer(ulong source, ulong sourceOffsetBytes, ulong destination,
                ulong destinationOffsetBytes, ulong sizeBytes) => _counts.Copies++;
        }

        /// <summary>The barrier side. The upload path records barriers and nothing else through it, so every other
        /// member refuses by name if anything ever routes one here.</summary>
        readonly struct SilentCmdSink : IVkCmdSink
        {
            readonly Counts _counts;

            internal SilentCmdSink(Counts counts) => _counts = counts;

            public void BindDescriptorSets(PipelineBindPoint bindPoint, PipelineLayout layout, uint firstSet,
                ReadOnlySpan<DescriptorSet> sets, ReadOnlySpan<uint> dynamicOffsets) => throw NotUpload();

            public void Draw(uint vertexCount, uint instanceCount, uint firstVertex, uint firstInstance)
                => throw NotUpload();

            public void DrawIndexed(uint indexCount, uint instanceCount, uint firstIndex, int vertexOffset,
                uint firstInstance) => throw NotUpload();

            public void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ) => throw NotUpload();

            public void PipelineBarrier(in DependencyInfo dependency) => _counts.Barriers++;

            static InvalidOperationException NotUpload()
                => new("A staged buffer upload records two barriers and one copy, and nothing else.");
        }

        /// <summary>Staging blocks over native memory, so the lease's raw-pointer write lands somewhere real and
        /// nothing needs pinning.</summary>
        sealed unsafe class NativeStagingSource : IVulkanStagingSource, IDisposable
        {
            readonly Dictionary<ulong, nint> _live = new();
            ulong _next = 1;

            public VulkanStagingBlock Create(ulong sizeBytes)
            {
                nint mapped = (nint)NativeMemory.AllocZeroed((nuint)sizeBytes);
                ulong handle = _next++;
                _live[handle] = mapped;
                return new VulkanStagingBlock(handle, mapped, sizeBytes);
            }

            public void Destroy(in VulkanStagingBlock block)
            {
                if (_live.Remove(block.Buffer, out nint mapped)) NativeMemory.Free((void*)mapped);
            }

            public void Dispose()
            {
                foreach (nint mapped in _live.Values) NativeMemory.Free((void*)mapped);
                _live.Clear();
            }
        }
    }
}
