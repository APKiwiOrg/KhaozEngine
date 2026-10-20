using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Gpu.Vulkan.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// <see cref="GpuReadbackRing"/> on a real device. Completion is polled through each slot's fence with short
    /// sleeps, never drained, and the no-drain claim is asserted through the device's drain counters the way
    /// <see cref="FencedReadbackGpuTests"/> reads them. Skipped unless KE_GPU_TESTS is set.
    /// </summary>
    public sealed class GpuReadbackRingGpuTests
    {
        const int W = 13, H = 7;
        const int PollTimeoutMs = 5000;

        [GpuFact(RequiresCompletionFences = true)]
        public void A_copied_render_target_reads_back_exactly_through_a_polled_fence()
        {
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = ctx.GpuDevice;
            byte[] pattern = Pattern(0);
            using IGpuTexture source = CreateSource(gd, pattern);
            using var ring = new GpuReadbackRing(gd, W, H);
            using IGpuCommandList commands = gd.Factory.CreateCommandList();

            IGpuFence? fence;
            using (GpuRecording.Open(gd, commands, nameof(GpuReadbackRingGpuTests)))
                Assert.True(ring.TryRecordCopy(commands, source, 42, out fence));
            gd.Submit(commands, fence);

            var clock = Stopwatch.StartNew();
            while (!fence.Signaled && clock.ElapsedMilliseconds < PollTimeoutMs) Thread.Sleep(1);
            Assert.True(fence.Signaled, "the slot fence did not signal within " + PollTimeoutMs + " ms");

            var read = new byte[W * H * 4];
            Assert.True(ring.TryReadCompleted(read, out long tag));
            Assert.Equal(42, tag);
            Assert.Equal(pattern, read);

            gd.WaitForIdle();
        }

        [GpuFact(RequiresCompletionFences = true)]
        public void Three_slots_cycle_through_six_frames_without_a_device_wait()
        {
            const int Frames = 6;
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = ctx.GpuDevice;

            // One source per frame, all uploaded before the loop: an upload is off the command timeline, so
            // rewriting one source while an earlier frame's copy of it is in flight would race the copy.
            var patterns = new byte[Frames][];
            var sources = new List<IGpuTexture>();
            for (int i = 0; i < Frames; i++)
            {
                patterns[i] = Pattern(i + 1);
                sources.Add(CreateSource(gd, patterns[i]));
            }

            using var ring = new GpuReadbackRing(gd, W, H);
            using IGpuCommandList commands = gd.Factory.CreateCommandList();
            var read = new byte[W * H * 4];
            var tags = new List<long>();
            var mismatches = new List<string>();

            void ReadCompleted()
            {
                while (ring.TryReadCompleted(read, out long tag))
                {
                    tags.Add(tag);
                    if (!read.AsSpan().SequenceEqual(patterns[tag])) mismatches.Add("frame " + tag);
                }
            }

            // A frame that finds every slot in flight polls the oldest one free, as the clip tap would drop it.
            void PollUntilFree()
            {
                var clock = Stopwatch.StartNew();
                while (!ring.HasFreeSlot && clock.ElapsedMilliseconds < PollTimeoutMs)
                {
                    ReadCompleted();
                    if (!ring.HasFreeSlot) Thread.Sleep(1);
                }
                Assert.True(ring.HasFreeSlot, "no slot freed within " + PollTimeoutMs + " ms");
            }

            long drainsBefore = DrainCalls(gd);
            long countedBefore = gd.Counters.DrainCount;

            for (int frame = 0; frame < Frames; frame++)
            {
                PollUntilFree();
                IGpuFence? fence;
                using (GpuRecording.Open(gd, commands, nameof(GpuReadbackRingGpuTests)))
                    Assert.True(ring.TryRecordCopy(commands, sources[frame], frame, out fence));
                gd.Submit(commands, fence);
                ReadCompleted();
            }

            var settle = Stopwatch.StartNew();
            while (ring.PendingCount > 0 && settle.ElapsedMilliseconds < PollTimeoutMs)
            {
                ReadCompleted();
                if (ring.PendingCount > 0) Thread.Sleep(1);
            }

            long drainsAfter = DrainCalls(gd);
            long countedAfter = gd.Counters.DrainCount;

            Assert.Equal(new long[] { 0, 1, 2, 3, 4, 5 }, tags);
            Assert.Empty(mismatches);
            Assert.Equal(drainsBefore, drainsAfter);
            Assert.Equal(countedBefore, countedAfter);

            gd.WaitForIdle();
            foreach (IGpuTexture source in sources) source.Dispose();
        }

        // Every WaitForIdle on the device's timeline, including one that found nothing to wait for. Direct3D 11
        // counts every drain in DrainCount already, so its reading is that.
        static long DrainCalls(IGpuDevice gd) => gd switch
        {
            MetalGpuDevice metal => metal.Timeline.WaitForIdleCalls,
            VulkanGpuDevice vulkan => vulkan.Timeline.WaitForIdleCalls,
            _ => gd.Counters.DrainCount,
        };

        static IGpuTexture CreateSource(IGpuDevice gd, byte[] pattern)
        {
            IGpuTexture texture = gd.Factory.CreateTexture(GpuTextureDescription.Texture2D(
                W, H, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            gd.UpdateTexture(texture, pattern, 0, 0, W, H);
            return texture;
        }

        // A value that changes from each byte to the next, alpha included, and differs per seed, so a shifted row,
        // a swapped channel or another frame's bytes fail the comparison.
        static byte[] Pattern(int seed)
        {
            var bytes = new byte[W * H * 4];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)((i * 37 + seed * 71 + 11) & 0xFF);
            return bytes;
        }
    }
}
