using System;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// A COMPUTE RESULT COPIED INTO STAGING AND COLLECTED THROUGH <c>IGpuDevice.TryReadBuffer</c>, on whatever
    /// real device the host resolves. The dispatch and the copy are one command list, the copy names a non-zero
    /// offset on both sides, and the read names the same offset, so a backend that ignored either offset or read
    /// before the copy completed hands back the wrong words. The test drains once, itself, so the final assertion
    /// is deterministic. The production read never drains, which the device-free fact below pins by call tracing.
    /// </summary>
    public sealed class ComputeAsyncReadbackGpuTests
    {
        const string WriteWords = @"#version 450
layout(local_size_x = 1) in;
layout(std430, set = 0, binding = 0) buffer WordBuf { uint Words[]; };
void main() {
    Words[0] = 7u;
    Words[1] = 11u;
    Words[2] = 13u;
    Words[3] = 17u;
}";

        const uint Bytes = 4 * sizeof(uint);
        const uint SliceOffset = sizeof(uint);
        const uint SliceBytes = 3 * sizeof(uint);

        readonly ITestOutputHelper _output;

        public ComputeAsyncReadbackGpuTests(ITestOutputHelper output) => _output = output;

        [GpuFact]
        public void RecordedCopyReadsTheRequestedSliceAfterCompletion()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice device = gpu.GpuDevice;
            _output.WriteLine("backend: " + device.Backend + " (" + device.Capabilities.DeviceName + ")");
            Assert.True(device.Capabilities.SupportsCompute, $"{device.Backend} reports no compute support");
            Assert.True(device.Capabilities.SupportsCompletionFences,
                $"{device.Backend} reports no completion fences");
            IGpuResourceFactory f = device.Factory;

            using IGpuBuffer words = f.CreateBuffer(new GpuBufferDescription(
                Bytes, GpuBufferUsage.StructuredBufferReadWrite, sizeof(uint)));
            using IGpuBuffer staging = f.CreateBuffer(new GpuBufferDescription(Bytes, GpuBufferUsage.Staging));
            using IGpuComputeShader shader = f.CreateComputeShaderFromSpirv(WriteWords);
            using IGpuResourceLayout layout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("WordBuf", GpuResourceKind.StructuredBufferReadWrite,
                    GpuShaderStages.Compute)));
            using IGpuResourceSet set = f.CreateResourceSet(new GpuResourceSetDescription(layout, words));
            using IGpuComputePipeline pipeline = f.CreateComputePipeline(
                new GpuComputePipelineDescription(shader, layout));
            using IGpuFence completion = f.CreateFence();

            var destination = new byte[SliceBytes];
            using IGpuCommandList cl = f.CreateCommandList();
            cl.Begin();
            cl.SetComputePipeline(pipeline);
            cl.SetComputeResourceSet(0, set);
            cl.Dispatch(1, 1, 1);
            cl.CopyBuffer(words, SliceOffset, staging, SliceOffset, SliceBytes);
            cl.End();

            // Nothing has been submitted against the fence, so it is unsignalled and nothing is read.
            Assert.False(device.TryReadBuffer(staging, completion, SliceOffset, destination));
            Assert.Equal(new byte[SliceBytes], destination);

            device.Submit(cl, completion);
            device.WaitForIdle();

            Assert.True(device.TryReadBuffer(staging, completion, SliceOffset, destination));
            uint[] words32 = MemoryMarshal.Cast<byte, uint>(destination).ToArray();
            Assert.Equal(new uint[] { 11u, 13u, 17u }, words32);
        }

        /// <summary>
        /// EVERY BUFFER REFUSAL FIRES BEFORE ANY READ, on whatever real device the host resolves: a non-staging
        /// source, a disposed staging buffer, an unaligned offset and a window that runs off the end. Each throws
        /// the type and names the parameter the seam documents, and leaves the destination as it was. The
        /// foreign-device refusals are covered device-free only.
        /// </summary>
        [GpuFact]
        public void RefusedReadsThrowBeforeTouchingTheDestination()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice device = gpu.GpuDevice;
            _output.WriteLine("backend: " + device.Backend + " (" + device.Capabilities.DeviceName + ")");
            Assert.True(device.Capabilities.SupportsCompletionFences,
                $"{device.Backend} reports no completion fences");
            IGpuResourceFactory f = device.Factory;

            using IGpuBuffer words = f.CreateBuffer(new GpuBufferDescription(
                Bytes, GpuBufferUsage.StructuredBufferReadWrite, sizeof(uint)));
            using IGpuBuffer staging = f.CreateBuffer(new GpuBufferDescription(Bytes, GpuBufferUsage.Staging));
            IGpuBuffer disposed = f.CreateBuffer(new GpuBufferDescription(Bytes, GpuBufferUsage.Staging));
            disposed.Dispose();
            using IGpuFence completion = f.CreateFence();

            byte[] four = Filled(4);
            ArgumentException nonStaging = Assert.Throws<ArgumentException>(
                () => device.TryReadBuffer(words, completion, 0, four));
            Assert.Equal("staging", nonStaging.ParamName);
            Assert.Equal(Filled(4), four);

            Assert.Throws<ObjectDisposedException>(() => device.TryReadBuffer(disposed, completion, 0, four));
            Assert.Equal(Filled(4), four);

            ArgumentOutOfRangeException unaligned = Assert.Throws<ArgumentOutOfRangeException>(
                () => device.TryReadBuffer(staging, completion, 3, four));
            Assert.Equal("offsetBytes", unaligned.ParamName);
            Assert.Equal(Filled(4), four);

            byte[] eight = Filled(8);
            ArgumentOutOfRangeException pastEnd = Assert.Throws<ArgumentOutOfRangeException>(
                () => device.TryReadBuffer(staging, completion, 12, eight));
            Assert.Equal("destination", pastEnd.ParamName);
            Assert.Equal(Filled(8), eight);
        }

        static byte[] Filled(int length)
        {
            var bytes = new byte[length];
            bytes.AsSpan().Fill(0xcc);
            return bytes;
        }

        /// <summary>
        /// AN OLD SUBMISSION THAT HAS COMPLETED IS READ WITHOUT THE DEVICE-WIDE DRAIN, even while later unrelated
        /// work is still pending. The read asks the fence it was given and nothing broader, so the second
        /// submission's outstanding value never selects a wait or a flush.
        /// </summary>
        [Fact]
        public void ACompletedOldSubmission_IsReadWithoutTheDrainPathWhileLaterWorkIsPending()
        {
            var submitLock = new object();
            var liveness = new FakeD3D11DeviceLiveness();
            using var timeline = new FakeD3D11FenceTimeline { SubmitLock = submitLock };
            using var memory = new FakeD3D11StagingMemory(bytes: (int)Bytes) { SubmitLock = submitLock };
            using var fences = new D3D11FenceSubsystem(timeline, submitLock, liveness);
            var reads = new D3D11CompletedReads(new D3D11StagingAccess(memory, submitLock), fences, submitLock,
                liveness);
            using var staging = new FakeStagingBuffer(Bytes, owner: liveness);
            MemoryMarshal.Cast<byte, uint>(memory.Bytes.AsSpan())[1] = 11u;

            IGpuFence old = fences.CreateFence();
            fences.SignalEndOfReplay(old);
            fences.SignalEndOfReplay(null);
            timeline.Completed = 1;

            var destination = new byte[sizeof(uint)];
            Assert.True(reads.TryReadBuffer(staging, old, SliceOffset, destination));

            Assert.Equal(11u, MemoryMarshal.Read<uint>(destination));
            Assert.Equal(0, timeline.WaitCallCount);
            Assert.Equal(0, timeline.FlushCount);
            Assert.Equal(
                new[] { nameof(ID3D11StagingMemory.TryMapBufferRead), nameof(ID3D11StagingMemory.UnmapBuffer) },
                memory.Calls);
        }
    }
}
