using System;
using System.Linq;
using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// <see cref="GpuReadbackRing"/> on a device-free fake. The contract pinned here is the one the clip path
    /// leans on: a copy is recorded only into a free slot, a slot is read only once its fence polls signaled, reads
    /// come back oldest first, and nothing ever waits on the device.
    /// </summary>
    public sealed class GpuReadbackRingTests
    {
        const int W = 8, H = 4;

        static FakeTexture Source(uint w = W, uint h = H, GpuPixelFormat format = GpuPixelFormat.R8G8B8A8UNorm)
            => new(w, h, 1, 1, format, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled);

        static byte[] Frame() => new byte[W * H * 4];

        [Fact]
        public void A_device_without_completion_fences_is_refused()
            => Assert.Throws<NotSupportedException>(() => new GpuReadbackRing(new FakeGpuDevice(), 8, 4));

        [Fact]
        public void A_device_without_the_fenced_read_map_is_refused()
        {
            var device = new OpenListTrackingGpuDevice(completionFences: true);
            Assert.True(device.Capabilities.SupportsCompletionFences);
            Assert.False((IGpuDevice)device is IGpuFencedReadback);

            Assert.Throws<NotSupportedException>(() => new GpuReadbackRing(device, 8, 4));
            Assert.Equal(0, device.TexturesCreated);
            Assert.Equal(0, device.FencesCreated);
        }

        [Fact]
        public void TryRecordCopy_records_one_copy_and_returns_an_unsignaled_fence()
        {
            var device = new ReadbackRingFakeDevice();
            using var ring = new GpuReadbackRing(device, W, H);
            IGpuCommandList commands = device.Factory.CreateCommandList();

            Assert.True(ring.TryRecordCopy(commands, Source(), 1, out IGpuFence? fence));

            Assert.Equal(1, device.CopyTextureCalls);
            Assert.False(fence.Signaled);
            Assert.Equal(1, ring.PendingCount);
        }

        [Fact]
        public void A_tick_with_every_slot_in_flight_is_refused_and_records_nothing()
        {
            var device = new ReadbackRingFakeDevice();
            using var ring = new GpuReadbackRing(device, W, H);
            IGpuCommandList commands = device.Factory.CreateCommandList();
            FakeTexture source = Source();

            for (int i = 0; i < 3; i++) Assert.True(ring.TryRecordCopy(commands, source, i, out _));
            Assert.False(ring.TryRecordCopy(commands, source, 3, out _));

            Assert.Equal(3, device.CopyTextureCalls);
            Assert.False(ring.HasFreeSlot);
            Assert.Equal(3, ring.PendingCount);
        }

        [Fact]
        public void Nothing_is_read_until_a_fence_signals_and_nothing_ever_waits()
        {
            var device = new ReadbackRingFakeDevice();
            using var ring = new GpuReadbackRing(device, W, H);
            IGpuCommandList commands = device.Factory.CreateCommandList();
            byte[] frame = Frame();

            Assert.True(ring.TryRecordCopy(commands, Source(), 5, out IGpuFence? fence));
            Assert.False(ring.TryReadCompleted(frame, out _));
            Assert.Equal(0, device.MapForReadCalls);
            Assert.Equal(0, device.Maps);
            Assert.Equal(0, device.WaitForIdleCalls);
            Assert.Equal(1, ring.PendingCount);

            ((ManualFence)fence).Signal();
            Assert.True(ring.TryReadCompleted(frame, out long tag));

            Assert.Equal(5, tag);
            Assert.Equal(1, device.MapForReadCalls);
            Assert.Equal(1, device.Unmaps);
            Assert.Equal(0, device.Maps);
            Assert.Equal(0, device.WaitForIdleCalls);
            Assert.Equal(0, ring.PendingCount);
        }

        [Fact]
        public void Completed_slots_are_read_oldest_first_with_their_tags()
        {
            var device = new ReadbackRingFakeDevice();
            using var ring = new GpuReadbackRing(device, W, H);
            IGpuCommandList commands = device.Factory.CreateCommandList();
            FakeTexture source = Source();
            var fences = new ManualFence[3];
            for (int i = 0; i < 3; i++)
            {
                Assert.True(ring.TryRecordCopy(commands, source, 7 + i, out IGpuFence? fence));
                fences[i] = (ManualFence)fence;
            }

            // Signaled newest first: a later slot is never read ahead of an earlier one still in flight.
            fences[2].Signal();
            fences[1].Signal();
            byte[] frame = Frame();
            Assert.False(ring.TryReadCompleted(frame, out _));
            Assert.Equal(0, device.MapForReadCalls);

            fences[0].Signal();
            for (long expected = 7; expected <= 9; expected++)
            {
                Assert.True(ring.TryReadCompleted(frame, out long tag));
                Assert.Equal(expected, tag);
            }
            Assert.False(ring.TryReadCompleted(frame, out _));
        }

        [Fact]
        public void A_read_slot_is_reset_and_reused()
        {
            var device = new ReadbackRingFakeDevice();
            using var ring = new GpuReadbackRing(device, W, H);
            IGpuCommandList commands = device.Factory.CreateCommandList();
            FakeTexture source = Source();
            Assert.True(ring.TryRecordCopy(commands, source, 1, out IGpuFence? first));
            Assert.True(ring.TryRecordCopy(commands, source, 2, out _));
            Assert.True(ring.TryRecordCopy(commands, source, 3, out _));

            ((ManualFence)first).Signal();
            Assert.True(ring.TryReadCompleted(Frame(), out _));
            Assert.True(ring.HasFreeSlot);

            Assert.True(ring.TryRecordCopy(commands, source, 4, out IGpuFence? fourth));
            Assert.Same(first, fourth);
            Assert.Equal(1, ((ManualFence)fourth).Resets);
            Assert.False(fourth.Signaled);
            Assert.Equal(3, device.Fences.Count);
        }

        [Fact]
        public void Rows_are_destrided_from_the_mapped_pitch()
        {
            var device = new ReadbackRingFakeDevice();
            using var ring = new GpuReadbackRing(device, W, H);
            IGpuCommandList commands = device.Factory.CreateCommandList();
            Assert.True(ring.TryRecordCopy(commands, Source(), 1, out IGpuFence? fence));
            ((ManualFence)fence).Signal();

            // One byte longer than a frame, so the tail after the packed rows is checked untouched as well.
            var destination = new byte[W * H * 4 + 1];
            destination[^1] = 0x5A;
            Assert.True(ring.TryReadCompleted(destination, out _));

            byte[] expected = device.PackedPattern(device.LastMapped!);
            Assert.Equal(expected, destination[..(W * H * 4)]);
            Assert.DoesNotContain(ReadbackRingFakeDevice.PadByte, destination);
            Assert.Equal(0x5A, destination[^1]);
        }

        [Fact]
        public void A_source_of_the_wrong_shape_is_refused()
        {
            var device = new ReadbackRingFakeDevice();
            using var ring = new GpuReadbackRing(device, W, H);
            IGpuCommandList commands = device.Factory.CreateCommandList();

            Assert.Throws<ArgumentException>(() => ring.TryRecordCopy(commands, Source(W, H + 1), 1, out _));
            Assert.Throws<ArgumentException>(
                () => ring.TryRecordCopy(commands, Source(format: GpuPixelFormat.B8G8R8A8UNorm), 1, out _));
            Assert.Throws<ArgumentException>(() => ring.TryRecordCopy(
                commands, new FakeTexture(W, H, 2, 1, GpuPixelFormat.R8G8B8A8UNorm), 1, out _));
            Assert.Throws<ArgumentException>(() => ring.TryRecordCopy(
                commands, new FakeTexture(W, H, 1, 4, GpuPixelFormat.R8G8B8A8UNorm), 1, out _));

            Assert.Equal(0, device.CopyTextureCalls);
            Assert.Equal(0, ring.PendingCount);
        }

        [Fact]
        public void A_destination_shorter_than_one_frame_is_refused()
        {
            var device = new ReadbackRingFakeDevice();
            using var ring = new GpuReadbackRing(device, W, H);
            IGpuCommandList commands = device.Factory.CreateCommandList();
            Assert.True(ring.TryRecordCopy(commands, Source(), 1, out IGpuFence? fence));
            ((ManualFence)fence).Signal();

            Assert.Throws<ArgumentException>(() => ring.TryReadCompleted(new byte[W * H * 4 - 1], out _));

            Assert.Equal(0, device.MapForReadCalls);
            Assert.Equal(1, ring.PendingCount);
        }

        [Fact]
        public void Dispose_with_slots_in_flight_frees_every_slot()
        {
            var device = new ReadbackRingFakeDevice();
            var ring = new GpuReadbackRing(device, W, H);
            IGpuCommandList commands = device.Factory.CreateCommandList();
            FakeTexture source = Source();
            Assert.True(ring.TryRecordCopy(commands, source, 1, out _));
            Assert.True(ring.TryRecordCopy(commands, source, 2, out _));

            ring.Dispose();

            FakeTexture[] staging = device.Textures.Where(t => t.Usage == GpuTextureUsage.Staging).ToArray();
            Assert.Equal(3, staging.Length);
            Assert.All(staging, t => Assert.True(t.Disposed));
            Assert.Equal(3, device.Fences.Count);
            Assert.All(device.Fences, f => Assert.True(f.Disposed));
            Assert.Equal(0, device.WaitForIdleCalls);
            Assert.False(source.Disposed);
        }
    }
}
