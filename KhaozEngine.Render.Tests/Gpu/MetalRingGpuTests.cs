using System;
using System.Linq;
using System.Runtime.Versioning;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE UNIFORM RING AND THE STAGING ARENA ON REAL HARDWARE: a real <c>MTLBuffer</c> of
    /// <c>stride * FramesInFlight</c> in Shared memory, a real <c>contents()</c> pointer the CPU writes through,
    /// and a real <c>copyFromBuffer:sourceOffset:toBuffer:destinationOffset:size:</c> that the GPU executes. Row
    /// 8 of <c>docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md</c>.
    ///
    /// <para><b>WHAT A RED RUN MEANS.</b> Everything the ring and the arena DECIDE is covered device-free by
    /// <see cref="MetalUniformRingTests"/>, <see cref="MetalStagingArenaTests"/>,
    /// <see cref="MetalBufferUploadTests"/>, <see cref="MetalRingStrideTests"/> and the shared rows in
    /// <see cref="GpuUniformRingSharedTests"/>. A failure here is about the two things no fake can prove: that a
    /// Shared buffer sized for the whole ring really is one allocation the CPU can address at every segment base,
    /// and that the driver accepts and RUNS the buffer-to-buffer copy the arena feeds it. An ABI error in the
    /// second presents as a crash or a validation failure rather than as a wrong pixel, which is the one
    /// comforting property of that risk.</para>
    ///
    /// <para><b>DORMANT OFF macOS RATHER THAN SKIPPED</b>, which is phase 3's row-19 lesson: under
    /// <c>KE_GPU_TESTS=1</c> the Vulkan and Direct3D 11 legs run this assembly in strict mode where a skip is a
    /// failure, so each row returns early with the platform recorded instead.</para>
    ///
    /// <para><b>IT SITS IN <c>NativeDeviceLifecycle</c></b> because it builds a whole <c>MTLDevice</c> and queue
    /// beside the suite's own and registers that queue into the same four-slot process-static completion
    /// table.</para>
    /// </summary>
    [Collection("NativeDeviceLifecycle")]
    public sealed class MetalRingGpuTests
    {
        readonly ITestOutputHelper _output;

        public MetalRingGpuTests(ITestOutputHelper output) => _output = output;

        /// <summary>
        /// A UNIFORM BUFFER IS ONE ALLOCATION N SEGMENTS LONG, and the seam still sees the size it asked for.
        /// That split is the whole reason the ring is invisible through <see cref="IGpuBuffer"/>: one identity,
        /// one logical size, and the frame base applied at bind.
        /// </summary>
        [GpuFact]
        public void ARingBackedUniformBufferIsOneAllocationOfStrideTimesTheDepth()
        {
            if (!Available()) return;

            using MetalGpuDevice device = CreateHeadless();
            using var buffer = (MetalBuffer)device.Factory.CreateBuffer(
                new GpuBufferDescription(200, GpuBufferUsage.UniformBuffer));

            Assert.NotNull(buffer.Ring);
            Assert.Equal(200u, buffer.SizeInBytes);
            Assert.Equal(256u, buffer.Ring!.SegmentStrideBytes);

            // The DRIVER's own reading of the allocation, not the engine's arithmetic repeated back.
            Assert.Equal((nuint)buffer.Ring.TotalBytes, ActualLength(buffer));

            // And a NON-uniform buffer of the same size is not ring-backed at all (M-M6), which is what stops the
            // reading above being true of everything.
            using var vertices = (MetalBuffer)device.Factory.CreateBuffer(
                new GpuBufferDescription(200, GpuBufferUsage.VertexBuffer));

            Assert.Null(vertices.Ring);
            Assert.Equal((nuint)200, ActualLength(vertices));

            _output.WriteLine($"a 200-byte uniform buffer allocated {ActualLength(buffer)} bytes across "
                + $"{buffer.Ring.FramesInFlight} segments, and a 200-byte vertex buffer allocated "
                + $"{ActualLength(vertices)}");
        }

        /// <summary>
        /// A RECORD-TIME UNIFORM WRITE LANDS IN THE SEGMENT THE NEXT SUBMIT BINDS, read back through the driver's
        /// own pointer. <c>Map</c> on a ring-backed buffer answers the CURRENT segment, which is the only answer
        /// that means anything: the caller asked for a buffer of that size and the segment IS that buffer as far
        /// as the seam is concerned.
        /// </summary>
        [GpuFact]
        public void ARecordTimeUniformWriteLandsInTheCurrentSegmentAndIsReadableThroughMap()
        {
            if (!Available()) return;

            using MetalGpuDevice device = CreateHeadless();
            using IGpuBuffer buffer = device.Factory.CreateBuffer(
                new GpuBufferDescription(256, GpuBufferUsage.UniformBuffer));
            using MetalCommandList list = device.CreateCommandList();

            byte[] payload = Payload(64, seed: 3);

            // Two frames, so the segment the second write lands in is not segment 0 and the assertion is about
            // the base rather than about the start of the allocation.
            for (int frame = 0; frame < 2; frame++)
            {
                list.Begin();
                list.UpdateBuffer(buffer, 0, (ReadOnlySpan<byte>)payload);
                list.Encoders.EnsureBlitEncoder();
                list.End();
                device.Submit(list);
            }

            device.WaitForIdle();

            Assert.Equal(2, device.Rings.CurrentSegment);

            MappedData mapped = device.Map(buffer, GpuMapMode.Read);
            Assert.Equal(payload, Read(mapped, payload.Length));
            device.Unmap(buffer);

            Assert.Null(device.Diagnostics.DeviceLossReason);

            _output.WriteLine($"segment {device.Rings.CurrentSegment} of a 256-byte ring carried the payload "
                + $"after {device.Rings.RecordingIndex} frames, with {device.Rings.StallCount} stalls");
        }

        /// <summary>
        /// A DEVICE-LEVEL WRITE REACHES EVERY SEGMENT, which is
        /// https://github.com/APKiwiOrg/KhaozEngine/issues/484's rule on a real allocation. Reaching the current
        /// segment alone was a shipped defect on another backend for one release: a load-time write held only
        /// until the frame index wrapped, so two frames in three bound memory nothing had ever written.
        /// </summary>
        [GpuFact]
        public void ADeviceLevelUniformWriteReachesEverySegmentOfTheRealAllocation()
        {
            if (!Available()) return;

            using MetalGpuDevice device = CreateHeadless();
            using var buffer = (MetalBuffer)device.Factory.CreateBuffer(
                new GpuBufferDescription(256, GpuBufferUsage.UniformBuffer));

            byte[] payload = Payload(32, seed: 4);
            device.UpdateBuffer(buffer, 16, (ReadOnlySpan<byte>)payload);

            for (int segment = 0; segment < buffer.Ring!.FramesInFlight; segment++)
            {
                Assert.Equal(payload, buffer.Ring.ReadSegment(segment, 16, payload.Length));
            }

            _output.WriteLine($"one device-level write reached all {buffer.Ring.FramesInFlight} segments of a "
                + $"{buffer.Ring.TotalBytes}-byte allocation");
        }

        /// <summary>
        /// AND A DISPOSED RING-BACKED BUFFER TAKES NEITHER WRITE PATH, which is the one disposal case that would
        /// CORRUPT rather than fail. The ring holds the <c>contents()</c> pointer the buffer took at creation and
        /// disposal releases the <c>MTLBuffer</c> under it, so a write that still reached the ring would be a
        /// <c>memcpy</c> into memory the driver has taken back. Both paths read
        /// <see cref="MetalBuffer.Ring"/> BEFORE any disposal check of their own, so the property answering null is
        /// what routes them onto the device write's named refusal and the record path's nil-handle no-op.
        /// <para>
        /// IT IS A <c>[GpuFact]</c> BECAUSE A <see cref="MetalBuffer"/> CANNOT BE BUILT WITHOUT AN
        /// <c>MTLDevice</c>: its only constructor runs behind <c>MetalBuffer.Create</c>, which allocates. What
        /// each path DOES with the null ring is device-free, in <c>MetalBufferUploadTests</c> for the record fork
        /// and in this file's siblings for the ring itself.
        /// </para>
        /// </summary>
        [GpuFact]
        public void ADisposedRingBackedBufferIsRefusedByTheDeviceWriteAndRecordsNothing()
        {
            if (!Available()) return;

            using MetalGpuDevice device = CreateHeadless();
            var buffer = (MetalBuffer)device.Factory.CreateBuffer(
                new GpuBufferDescription(256, GpuBufferUsage.UniformBuffer));
            using MetalCommandList list = device.CreateCommandList();

            Assert.NotNull(buffer.Ring);

            buffer.Dispose();
            Assert.Null(buffer.Ring);

            byte[] payload = Payload(32, seed: 12);

            // THE DEVICE PATH: the null ring falls through to MetalBuffer.Write, which is disposal-guarded.
            Assert.Throws<ObjectDisposedException>(
                () => device.UpdateBuffer(buffer, 0, (ReadOnlySpan<byte>)payload));

            // THE RECORD PATH: the null ring falls through to the staging fork, which finds the nil handle and
            // records nothing. No block leased, no encoder opened, and above all no write through the ring.
            list.Begin();
            list.UpdateBuffer(buffer, 0, (ReadOnlySpan<byte>)payload);
            list.End();
            device.Submit(list);
            device.WaitForIdle();

            Assert.Equal(0, list.Arena.BlocksCreated);
            Assert.Null(device.Diagnostics.DeviceLossReason);

            _output.WriteLine("a disposed ring-backed buffer refused the device write by name and recorded "
                + $"nothing, leasing {list.Arena.BlocksCreated} staging blocks");
        }

        /// <summary>
        /// THE ARENA'S COPY, EXECUTED BY THE GPU. This is the row that answers the native call: the payload goes
        /// into a pooled Shared block, one
        /// <c>copyFromBuffer:sourceOffset:toBuffer:destinationOffset:size:</c> is encoded into the list's blit
        /// encoder, the buffer is committed, and the bytes are read back out of the DESTINATION after a drain.
        /// Nothing device-free can prove the driver ran it.
        /// </summary>
        [GpuFact]
        public void ARecordTimeBulkUploadIsCopiedByTheGpuIntoTheDestination()
        {
            if (!Available()) return;

            using MetalGpuDevice device = CreateHeadless();
            using IGpuBuffer buffer = device.Factory.CreateBuffer(
                new GpuBufferDescription(512, GpuBufferUsage.VertexBuffer));
            using MetalCommandList list = device.CreateCommandList();

            byte[] first = Payload(64, seed: 5);
            byte[] second = Payload(48, seed: 6);

            list.Begin();
            list.UpdateBuffer(buffer, 0, (ReadOnlySpan<byte>)first);
            list.UpdateBuffer(buffer, 256, (ReadOnlySpan<byte>)second);
            list.End();
            device.Submit(list);

            device.WaitForIdle();

            MappedData mapped = device.Map(buffer, GpuMapMode.Read);
            byte[] contents = Read(mapped, 512);
            device.Unmap(buffer);

            Assert.Equal(first, contents.Take(first.Length).ToArray());
            Assert.Equal(second, contents.Skip(256).Take(second.Length).ToArray());
            Assert.Null(device.Diagnostics.DeviceLossReason);

            // BOTH uploads shared ONE block and ONE encoder, which is what the arena and the Ensure buy.
            Assert.Equal(1, list.Arena.BlocksCreated);

            _output.WriteLine($"two record-time uploads shared {list.Arena.BlocksCreated} staging block and "
                + $"landed at offsets 0 and 256 of a 512-byte vertex buffer");
        }

        /// <summary>
        /// A FRAME LOOP DOES NOT ALLOCATE A STAGING BLOCK PER UPLOAD, which is the cost M-M8 exists to remove:
        /// the incumbent allocates and releases a whole <c>MTLBuffer</c> per record-time <c>UpdateBuffer</c> and
        /// its own source carries a TODO asking for them to be pooled. The block count staying flat across many
        /// frames is the whole reading.
        /// </summary>
        [GpuFact]
        public void AFrameLoopOfBulkUploadsReusesItsStagingBlocks()
        {
            if (!Available()) return;

            using MetalGpuDevice device = CreateHeadless();
            using IGpuBuffer buffer = device.Factory.CreateBuffer(
                new GpuBufferDescription(512, GpuBufferUsage.VertexBuffer));
            using MetalCommandList list = device.CreateCommandList();

            byte[] payload = Payload(128, seed: 7);

            for (int frame = 0; frame < 24; frame++)
            {
                list.Begin();
                list.UpdateBuffer(buffer, 0, (ReadOnlySpan<byte>)payload);
                list.End();
                device.Submit(list);

                // A drain per frame, so the slot the arena is recycling onto has provably completed and the
                // steady state is reached rather than approximated.
                device.WaitForIdle();
            }

            Assert.True(list.Arena.BlocksCreated <= MetalFramesInFlight.Default,
                $"24 record-time uploads created {list.Arena.BlocksCreated} staging blocks, which is the "
                + "incumbent's allocate-per-upload shape rather than a pool");
            Assert.Equal(0, device.Rings.StallCount);
            Assert.Null(device.Diagnostics.DeviceLossReason);

            _output.WriteLine($"24 frames of record-time uploads created {list.Arena.BlocksCreated} staging "
                + $"blocks and stalled {device.Rings.StallCount} times");
        }

        /// <summary>
        /// THE RING ROTATES AND NEVER STALLS AT THE DEFAULT DEPTH ON A FRAME LOOP THAT SUBMITS AND DRAINS, which
        /// is MM4's exit criterion in miniature. A non-zero count here would say three segments are not enough
        /// for a loop that waits for the GPU every frame, which would mean the gate is reading the wrong value
        /// rather than that the depth is wrong.
        /// </summary>
        [GpuFact]
        public void TheRingRotatesThroughItsSegmentsWithoutStalling()
        {
            if (!Available()) return;

            using MetalGpuDevice device = CreateHeadless();
            using IGpuBuffer buffer = device.Factory.CreateBuffer(
                new GpuBufferDescription(256, GpuBufferUsage.UniformBuffer));
            using MetalCommandList list = device.CreateCommandList();

            for (int frame = 0; frame < 12; frame++)
            {
                list.Begin();
                list.UpdateBuffer(buffer, 0, frame);
                list.Encoders.EnsureBlitEncoder();
                list.End();
                device.Submit(list);
                device.WaitForIdle();

                Assert.Equal((frame + 1) % MetalFramesInFlight.Default, device.Rings.CurrentSegment);
            }

            Assert.Equal(12ul, device.Rings.RecordingIndex);
            Assert.Equal(0, device.Rings.StallCount);
            Assert.Equal(0, device.BackpressureTotals.Count);
            Assert.Null(device.Diagnostics.DeviceLossReason);

            _output.WriteLine($"12 frames rotated to segment {device.Rings.CurrentSegment} with "
                + $"{device.Rings.StallCount} stalls at a depth of {MetalFramesInFlight.Default}");
        }

        /// <summary>
        /// A SMOKE OBSERVATION AND NOT THE WIRING PROOF, which is what this row can honestly be. It runs the
        /// frame loop ahead of the GPU and asserts that the two readings AGREE, and it passes at zero stalls,
        /// so it cannot tell "never blocked" from "never wired" and must not be cited as though it could.
        /// <para>
        /// THE WIRING PROOF IS DEVICE-FREE and it is
        /// <see cref="MetalUniformRingTests.TheGateReadsCompletionAndNotTheSubmitReceipt"/>, which registers a
        /// submission, leaves the completion counter behind it, wraps the ring and asserts a stall count of
        /// exactly one. That is deterministic and it runs on every leg. What THIS row adds is that the same
        /// mechanism survives a real driver: a real completion value, a real wait, and a real elapsed time, none
        /// of which a fake shared event can produce.
        /// </para>
        /// <para>
        /// AND THE COUNT IT PRINTS IS NONDETERMINISTIC. How many wraps actually block depends on how fast the GPU
        /// retires an empty command buffer, and the same machine has produced twenty-two stalls on one run and
        /// eleven on another. So the assertion is on the PAIR being consistent, and the number in the output is
        /// one observation rather than a measurement anything should be compared against.
        /// </para>
        /// </summary>
        [GpuFact]
        public void TheStallCounterCountsARealSegmentWait()
        {
            if (!Available()) return;

            using MetalGpuDevice device = CreateHeadless();
            using IGpuBuffer buffer = device.Factory.CreateBuffer(
                new GpuBufferDescription(256, GpuBufferUsage.UniformBuffer));
            using MetalCommandList list = device.CreateCommandList();

            // No drain anywhere: the frame loop runs as far ahead as the ring lets it, which is the whole point
            // of the gate. Whether any single wrap actually blocks depends on how fast the GPU retires an empty
            // buffer, so the assertion is on the PAIR being consistent rather than on a count.
            for (int frame = 0; frame < 64; frame++)
            {
                list.Begin();
                list.UpdateBuffer(buffer, 0, frame);
                list.Encoders.EnsureBlitEncoder();
                list.End();
                device.Submit(list);
            }

            device.WaitForIdle();

            MetalWaitTotals totals = device.BackpressureTotals;
            Assert.Equal(device.Rings.StallCount, (int)totals.Count);
            Assert.True(totals.Count == 0 || totals.TotalMs > 0,
                "a stall was counted with no time against it, so the wait was recorded without having blocked");
            Assert.Null(device.Diagnostics.DeviceLossReason);

            _output.WriteLine($"64 undrained recordings stalled {device.Rings.StallCount} times for "
                + $"{totals.TotalMs:F3} ms in total, which is ONE observation of a nondeterministic number and "
                + "not a measurement to compare against");
        }

        // The driver's own -length, which is the reading the engine's arithmetic is checked AGAINST rather than
        // compared with itself.
        [SupportedOSPlatform("macos")]
        static nuint ActualLength(MetalBuffer buffer) => buffer.Handle.Length();

        static byte[] Read(MappedData mapped, int length)
        {
            var bytes = new byte[length];
            unsafe
            {
                new ReadOnlySpan<byte>((void*)mapped.Data, length).CopyTo(bytes);
            }

            return bytes;
        }

        static byte[] Payload(int length, byte seed)
        {
            var bytes = new byte[length];
            for (int i = 0; i < length; i++) bytes[i] = (byte)((seed * 17) + i + 1);
            return bytes;
        }

        // A [SupportedOSPlatformGuard] rather than an inline check at every row, which is the same mechanism the
        // package itself uses and what lets CA1416 see that every call below is on a macOS-only path.
        [SupportedOSPlatformGuard("macos")]
        bool Available()
        {
            if (KhaozEngineMetal.IsPlatformSupported) return true;

            _output.WriteLine("dormant: not macOS, so there is no Metal device to record against.");
            return false;
        }

        static MetalGpuDevice CreateHeadless()
            => (MetalGpuDevice)new MetalBackendProvider().CreateHeadless().Device;
    }
}
