using System;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Gpu.Metal.Internal.ObjC;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// STEADY STAGING ALLOCATES NOTHING (#1114), device-free: the arena's slot rotation, and a frame of staged
    /// record-time uploads through <see cref="MetalBufferUpload.Record"/>, measured with
    /// <c>GC.GetAllocatedBytesForCurrentThread</c>.
    /// <para>
    /// <b>TWO ALLOCATIONS HID HERE AND NEITHER SHOWS IN A PICTURE.</b> The arena made one block record OBJECT every
    /// time a slot opened a block, which on a steady frame is every frame, and the upload built the alignment
    /// refusal's message on every call, refusal or not. Grimhollow's water path paid both per plane per frame.
    /// </para>
    /// <para>
    /// <b>THE SINK AND THE BLIT SEAM ARE LOCAL AND SILENT</b>, because the shared fakes log every call into a
    /// growing list, which is right for a routing test and exactly the allocation this one would then measure.
    /// </para>
    /// </summary>
    [Collection("AllocSensitive")]
    public sealed class MetalStagingAllocationTests : IDisposable
    {
        const int UploadsPerFrame = 8;
        const int MeasuredFrames = 20;
        const uint DestinationSize = 4096;
        const uint UploadStride = 512;

        static readonly IntPtr Destination = new(0xDEAD);
        static readonly IntPtr CommandBuffer = new(0x100);

        readonly MetalRingHarness _harness = new();
        readonly byte[] _payload = new byte[250];

        /// <inheritdoc/>
        public void Dispose() => _harness.Dispose();

        [Fact]
        public void ASteadyRotationOfArenaSlotsAllocatesNothing()
        {
            using MetalStagingArena arena = _harness.NewArena();

            for (int frame = 0; frame < _harness.FramesInFlight * 2; frame++) LeaseFrame(arena, frame);

            AllocAssert.NoPerCallAllocation($"{MeasuredFrames} frames of arena slot rotation", () =>
            {
                for (int frame = 0; frame < MeasuredFrames; frame++) LeaseFrame(arena, frame);
            });

            // One block per slot, reused ever after, so the reading above is about the block RECORDS rather than
            // about blocks being created.
            Assert.Equal(_harness.FramesInFlight, arena.BlocksCreated);
        }

        [Fact]
        public void ASteadyFrameOfStagedRecordTimeUploadsAllocatesNothing()
        {
            using MetalStagingArena arena = _harness.NewArena();
            var blit = new CountingBlitApi();
            var encoders = new MetalEncoderScope(new SilentEncoderSink());

            for (int frame = 0; frame < _harness.FramesInFlight * 2; frame++)
                StagedFrame(arena, encoders, blit, frame);

            AllocAssert.NoPerCallAllocation($"{MeasuredFrames} frames of {UploadsPerFrame} staged uploads", () =>
            {
                for (int frame = 0; frame < MeasuredFrames; frame++) StagedFrame(arena, encoders, blit, frame);
            });

            Assert.Equal(_harness.FramesInFlight, arena.BlocksCreated);
            Assert.True(blit.BufferCopies > 0, "no staged copy was emitted, so the reading above measured nothing");
        }

        /// <summary>
        /// AND THE REFUSAL STILL SAYS WHAT IT REFUSED. The message moved behind the alignment check, so this pins
        /// that a misaligned staged upload still names its own size and the offset it was given.
        /// </summary>
        [Fact]
        public void AMisalignedStagedUploadStillNamesItsSizeInTheRefusal()
        {
            ArgumentOutOfRangeException thrown = Assert.Throws<ArgumentOutOfRangeException>(
                () => MetalBufferUpload.CopyBytesFor(offsetBytes: 6, lengthBytes: 16, DestinationSize));

            Assert.Contains("A record-time upload of 16 bytes to a non-uniform native Metal buffer",
                thrown.Message, StringComparison.Ordinal);
            Assert.Contains("destination offset of 6", thrown.Message, StringComparison.Ordinal);
        }

        static void LeaseFrame(MetalStagingArena arena, int frame)
        {
            arena.BeginSlot(frame % arena.Depth, 0);
            for (int i = 0; i < UploadsPerFrame; i++) arena.Take(MetalStagingArena.AlignedCopyBytes(250));
        }

        void StagedFrame(MetalStagingArena arena, MetalEncoderScope encoders, CountingBlitApi blit, int frame)
        {
            arena.BeginSlot(frame % arena.Depth, 0);
            encoders.BeginRecording(CommandBuffer);

            for (int i = 0; i < UploadsPerFrame; i++)
            {
                MetalBufferUpload.Record(ring: null, 0, Destination, DestinationSize, (uint)i * UploadStride,
                    _payload, encoders, arena, blit);
            }

            encoders.EnsureNoEncoder();
        }

        // AN ENCODER SINK THAT ALLOCATES NOTHING. Every handle is one fabricated number and every emission is
        // dropped, because what this class measures is the staging path above the seam.
        readonly struct SilentEncoderSink : IMetalEncoderSink
        {
            static readonly IntPtr Encoder = new(0xE0C0);

            public IntPtr BeginRenderEncoder(IntPtr commandBuffer, IntPtr descriptor) => Encoder;

            public IntPtr BeginBlitEncoder(IntPtr commandBuffer) => Encoder;

            public IntPtr BeginComputeEncoder(IntPtr commandBuffer) => Encoder;

            public void EndEncoding(MetalEncoderKind kind, IntPtr encoder) { }

            public void SetBuffers(MetalShaderStage stage, IntPtr encoder, ReadOnlySpan<IntPtr> buffers,
                ReadOnlySpan<nuint> offsets, uint firstIndex) { }

            public void SetTextures(MetalShaderStage stage, IntPtr encoder, ReadOnlySpan<IntPtr> textures,
                uint firstIndex) { }

            public void SetSamplerStates(MetalShaderStage stage, IntPtr encoder, ReadOnlySpan<IntPtr> samplers,
                uint firstIndex) { }

            public void SetBufferOffset(MetalShaderStage stage, IntPtr encoder, nuint offset, uint index) { }

            public void Draw(IntPtr encoder, MTLPrimitiveType topology, uint vertexStart, uint vertexCount,
                uint instanceCount, uint baseInstance) { }

            public void DrawIndexed(IntPtr encoder, MTLPrimitiveType topology, uint indexCount, IntPtr indexBuffer,
                nuint indexBufferOffset, bool sixteenBitIndices, uint instanceCount, int baseVertex,
                uint baseInstance) { }

            public void Dispatch(IntPtr encoder, uint groupCountX, uint groupCountY, uint groupCountZ,
                uint threadsPerGroupX, uint threadsPerGroupY, uint threadsPerGroupZ) { }
        }

        // THE BLIT SEAM, COUNTED RATHER THAN LOGGED. Only the buffer copy is on the staging path, so the other four
        // refuse by name if anything ever routes one here.
        sealed class CountingBlitApi : IMetalBlitApi
        {
            internal int BufferCopies { get; private set; }

            public void CopyBufferToBuffer(IntPtr encoder, IntPtr source, ulong sourceOffsetBytes,
                IntPtr destination, ulong destinationOffsetBytes, ulong sizeBytes) => BufferCopies++;

            public void CopyTextureToTexture(IntPtr encoder, IntPtr source, IntPtr destination,
                in MetalTextureRegion region) => throw NotStaging();

            public void CopyTextureToBuffer(IntPtr encoder, IntPtr source, IntPtr destination,
                in MetalBufferImageRegion region) => throw NotStaging();

            public void CopyBufferToTexture(IntPtr encoder, IntPtr source, IntPtr destination,
                in MetalBufferImageRegion region) => throw NotStaging();

            public void GenerateMipmaps(IntPtr encoder, IntPtr texture) => throw NotStaging();

            static InvalidOperationException NotStaging()
                => new("A staged record-time buffer upload emits one buffer-to-buffer copy and nothing else.");
        }
    }
}
