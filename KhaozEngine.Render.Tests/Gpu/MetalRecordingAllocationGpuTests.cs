using System;
using System.Runtime.Versioning;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// STEADY-STATE RECORDING ON A REAL NATIVE METAL DEVICE ALLOCATES NO MANAGED MEMORY (#1114).
    /// <see cref="MetalStagingAllocationTests"/> proves the arena and the upload decision device-free. This proves
    /// the whole record path on hardware: the list, the encoder scope, the real sink, the real staging source and
    /// the real blit seam.
    /// <para>
    /// <b>ONLY THE RECORDING IS MEASURED.</b> Each frame reads the counter around <c>Begin</c> to <c>End</c> and
    /// submits and drains outside that window, because the submit path and the drain are not what #1114 changed,
    /// and a drain per frame is what makes the arena's recycling deterministic. The retry policy is
    /// <see cref="AllocAssert"/>'s, spelled out here because the window is split across frames.
    /// </para>
    /// <para>
    /// <b>ALLOCSENSITIVE, WHICH ALSO SERIALISES THE DEVICE IT BUILDS</b>: the collection disables parallelization,
    /// so the device built beside the suite's own never overlaps another device-building class either.
    /// </para>
    /// </summary>
    [Collection("AllocSensitive")]
    public sealed class MetalRecordingAllocationGpuTests
    {
        const int WarmFrames = 6;
        const int MeasuredFrames = 24;
        const int UploadsPerFrame = 8;
        const uint UploadStride = 512;
        const uint SmallBufferBytes = 4096;
        const int SmallPayloadBytes = 250;

        // Over MetalStagingArena.DefaultRetentionBytes, so the block it takes is released at every recycle.
        const uint OverCapBytes = 9u * 1024 * 1024;

        readonly ITestOutputHelper _output;

        public MetalRecordingAllocationGpuTests(ITestOutputHelper output) => _output = output;

        [GpuFact]
        public void SteadyStagedUploadsUnderTheRetentionCapAllocateNothing()
        {
            if (!Available()) return;

            using MetalGpuDevice device = CreateHeadless();
            using IGpuBuffer buffer = device.Factory.CreateBuffer(
                new GpuBufferDescription(SmallBufferBytes, GpuBufferUsage.VertexBuffer));
            using MetalCommandList list = device.CreateCommandList();
            byte[] payload = Payload(SmallPayloadBytes);

            StagedFrames(device, list, buffer, payload, UploadsPerFrame, WarmFrames);

            long first = StagedFrames(device, list, buffer, payload, UploadsPerFrame, MeasuredFrames);
            long retry = first == 0 ? 0 : StagedFrames(device, list, buffer, payload, UploadsPerFrame, MeasuredFrames);

            Assert.True(retry == 0,
                $"{MeasuredFrames} frames of {UploadsPerFrame} staged uploads allocated {first} bytes while "
                + $"recording on the first pass and {retry} on the retry, expected zero on at least one");
            Assert.Null(device.Diagnostics.DeviceLossReason);

            _output.WriteLine($"{MeasuredFrames} steady frames allocated {first} managed bytes while recording, "
                + $"with {list.Arena.BlocksCreated} staging blocks created in total");
        }

        /// <summary>
        /// OVER THE CAP THE NATIVE BLOCK CHURNS BY DESIGN AND NOTHING MANAGED DOES. The retention cap releases
        /// the one enormous block at every recycle and the next frame takes a fresh one, which is M-M8's own
        /// shape. What #1114 owes is that the churn is native only.
        /// </summary>
        [GpuFact]
        public void AnUploadOverTheRetentionCapChurnsNativeBlocksButNoManagedMemory()
        {
            if (!Available()) return;

            using MetalGpuDevice device = CreateHeadless();
            using IGpuBuffer buffer = device.Factory.CreateBuffer(
                new GpuBufferDescription(OverCapBytes, GpuBufferUsage.VertexBuffer));
            using MetalCommandList list = device.CreateCommandList();
            byte[] payload = Payload((int)OverCapBytes);

            StagedFrames(device, list, buffer, payload, 1, WarmFrames);
            int createdBefore = list.Arena.BlocksCreated;

            long first = StagedFrames(device, list, buffer, payload, 1, MeasuredFrames);
            long retry = first == 0 ? 0 : StagedFrames(device, list, buffer, payload, 1, MeasuredFrames);

            Assert.True(retry == 0,
                $"{MeasuredFrames} frames of one over-cap staged upload allocated {first} managed bytes while "
                + $"recording on the first pass and {retry} on the retry, expected zero on at least one");
            Assert.True(list.Arena.BlocksCreated - createdBefore >= MeasuredFrames,
                "the over-cap upload was pooled, so this row measured the under-cap path instead");
            Assert.Null(device.Diagnostics.DeviceLossReason);
        }

        // Sum of the recording windows only. The submit and the drain sit outside every window.
        static long StagedFrames(MetalGpuDevice device, MetalCommandList list, IGpuBuffer buffer, byte[] payload,
            int uploads, int frames)
        {
            long recorded = 0;
            for (int frame = 0; frame < frames; frame++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();

                list.Begin();
                for (int i = 0; i < uploads; i++)
                    list.UpdateBuffer(buffer, (uint)i * UploadStride, (ReadOnlySpan<byte>)payload);
                list.End();

                recorded += GC.GetAllocatedBytesForCurrentThread() - before;

                device.Submit(list);
                device.WaitForIdle();
            }

            return recorded;
        }

        static byte[] Payload(int length)
        {
            var bytes = new byte[length];
            for (int i = 0; i < length; i++) bytes[i] = (byte)(i + 1);
            return bytes;
        }

        [SupportedOSPlatformGuard("macos")]
        bool Available()
        {
            if (KhaozEngineMetal.IsPlatformSupported) return true;

            MetalDormancy.ThrowIfRequired("this is not macOS at all");
            _output.WriteLine("dormant: not macOS, so there is no Metal device to record against.");
            return false;
        }

        static MetalGpuDevice CreateHeadless()
            => (MetalGpuDevice)new MetalBackendProvider().CreateHeadless().Device;
    }
}
