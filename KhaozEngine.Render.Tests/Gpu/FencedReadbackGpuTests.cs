using System.Diagnostics;
using System.Threading;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Gpu.Vulkan.Internal;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// <see cref="IGpuFencedReadback.MapForRead"/> on a real device. Whether a later submission is still running
    /// when the map happens is up to the GPU, so the no-wait claim is asserted through the device's drain counters
    /// rather than through timing: every <c>WaitForIdle</c> call on the Metal and Vulkan timelines, and the
    /// Direct3D 11 device's <c>DrainCount</c>, which counts every drain. Skipped unless KE_GPU_TESTS is set.
    /// </summary>
    public sealed class FencedReadbackGpuTests
    {
        const uint Width = 13, Height = 7;
        const int PollTimeoutMs = 5000;

        [GpuFact(RequiresCompletionFences = true)]
        public void A_fenced_copy_reads_back_exactly_without_waiting_for_later_work()
        {
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = ctx.GpuDevice;
            IGpuFencedReadback fenced = Assert.IsAssignableFrom<IGpuFencedReadback>(gd);
            IGpuResourceFactory f = gd.Factory;

            byte[] pattern = Pattern();
            using IGpuTexture source = f.CreateTexture(GpuTextureDescription.Texture2D(
                Width, Height, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Sampled));
            gd.UpdateTexture(source, pattern, 0, 0, Width, Height);
            using IGpuTexture staging = f.CreateTexture(GpuTextureDescription.Texture2D(
                Width, Height, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Staging));
            using IGpuTexture laterTarget = f.CreateTexture(GpuTextureDescription.Texture2D(
                1024, 1024, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget));
            using IGpuFramebuffer laterFramebuffer = f.CreateFramebuffer(null, laterTarget);
            using IGpuFence fenceA = f.CreateFence();
            using IGpuFence fenceB = f.CreateFence();

            using (IGpuCommandList copy = f.CreateCommandList())
            using (IGpuCommandList later = f.CreateCommandList())
            {
                using (GpuRecording.Open(gd, copy, nameof(FencedReadbackGpuTests) + ".copy"))
                    copy.CopyTexture(source, staging);
                gd.Submit(copy, fenceA);

                using (GpuRecording.Open(gd, later, nameof(FencedReadbackGpuTests) + ".later"))
                {
                    later.SetFramebuffer(laterFramebuffer);
                    later.ClearColorTarget(0, Color.Black);
                }
                gd.Submit(later, fenceB);

                var clock = Stopwatch.StartNew();
                while (!fenceA.Signaled && clock.ElapsedMilliseconds < PollTimeoutMs) Thread.Sleep(1);
                Assert.True(fenceA.Signaled, "fenceA did not signal within " + PollTimeoutMs + " ms");

                long drainsBefore = DrainCalls(gd);
                long countedBefore = gd.Counters.DrainCount;

                MappedData map = fenced.MapForRead(staging, fenceA);

                long drainsAfter = DrainCalls(gd);
                long countedAfter = gd.Counters.DrainCount;
                byte[] read = Destride(map);
                gd.Unmap(staging);

                Assert.Equal(pattern, read);
                Assert.Equal(drainsBefore, drainsAfter);
                Assert.Equal(countedBefore, countedAfter);

                gd.WaitForIdle();
            }
        }

        // Every WaitForIdle on the device's timeline, including one that found nothing to wait for. Direct3D 11
        // counts every drain in DrainCount already, so its reading is that.
        static long DrainCalls(IGpuDevice gd) => gd switch
        {
            MetalGpuDevice metal => metal.Timeline.WaitForIdleCalls,
            VulkanGpuDevice vulkan => vulkan.Timeline.WaitForIdleCalls,
            _ => gd.Counters.DrainCount,
        };

        // A value that changes from each byte to the next, alpha included, so a shifted row or a swapped channel
        // fails the comparison.
        static byte[] Pattern()
        {
            var bytes = new byte[Width * Height * 4];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)((i * 37 + 11) & 0xFF);
            return bytes;
        }

        static unsafe byte[] Destride(MappedData map)
        {
            var bytes = new byte[Width * Height * 4];
            byte* data = (byte*)map.Data;
            for (uint y = 0; y < Height; y++)
                for (uint x = 0; x < Width * 4; x++)
                    bytes[y * Width * 4 + x] = data[y * map.RowPitch + x];
            return bytes;
        }
    }
}
