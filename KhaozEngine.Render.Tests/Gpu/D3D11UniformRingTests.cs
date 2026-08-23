using System;
using System.Threading;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11;
using KhaozEngine.Gpu.D3D11.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE CONSTANT-BUFFER RING'S OWN HALF (decisions U1, U2, U3 and U4): which buffers are ring-backed, how a
    /// buffer's bytes are laid out across frame segments, when the mapping is taken and released, where a write
    /// lands, and which writes go to the ring rather than to the recording's payload arena.
    /// <para>
    /// All of it is engine arithmetic and one interface, so every test here is an ordinary <c>[Fact]</c> that runs
    /// on macOS and Linux as well as Windows. What is left on the far side of
    /// <see cref="ID3D11RingMemory"/> is one <c>Map</c> and one <c>Unmap</c>, and the segment recycling that
    /// decides when those are SAFE is in <see cref="D3D11RingRecyclingTests"/>.
    /// </para>
    /// <para>
    /// WHY THE RING EXISTS AT ALL, since none of these numbers mean much without it. Veldrid put a partial write
    /// to a default-usage constant buffer on a pooled staging path whose map blocks until the GPU is done with the
    /// buffer being recycled, and zero renderer sites ask for a dynamic buffer, so every per-frame uniform write
    /// in the engine takes that path. A reporting client paid 22 blocking maps a frame and 12 to 17 ms a pass for
    /// it. Here the same write is a memcpy into memory that is already mapped.
    /// </para>
    /// </summary>
    public sealed class D3D11UniformRingTests
    {
        // ---- which buffers are ring-backed, and the U3 creation invariant ---------------------------------

        /// <summary>A uniform buffer is ring-backed and takes the constant-buffer bind flag alone. Nothing else
        /// is, which is the first half of decision U3: a structured buffer's full-range RAW view is created once
        /// over the whole allocation, so it would address the first segment forever.</summary>
        [Fact]
        public void OnlyUniformBuffers_AreRingBacked()
        {
            D3D11BufferViewPlan uniform = D3D11ViewPolicy.ForBuffer(GpuBufferUsage.UniformBuffer);
            Assert.True(uniform.Ring);
            Assert.Equal(D3D11BindUsage.ConstantBuffer, uniform.Bind);

            Assert.False(D3D11ViewPolicy.ForBuffer(GpuBufferUsage.VertexBuffer).Ring);
            Assert.False(D3D11ViewPolicy.ForBuffer(GpuBufferUsage.IndexBuffer).Ring);
            Assert.False(D3D11ViewPolicy.ForBuffer(GpuBufferUsage.IndirectBuffer).Ring);
            Assert.False(D3D11ViewPolicy.ForBuffer(GpuBufferUsage.StructuredBufferReadOnly).Ring);
            Assert.False(D3D11ViewPolicy.ForBuffer(GpuBufferUsage.StructuredBufferReadWrite).Ring);
            Assert.False(D3D11ViewPolicy.ForBuffer(GpuBufferUsage.Staging).Ring);
        }

        /// <summary>
        /// THE BACKEND-DIVERGENT CREATION FAILURE (decision U3). A uniform buffer combined with any other way of
        /// binding the same bytes throws HERE, at creation, rather than rendering one frame's data as another's.
        /// The structured combination is the one the design names, and the vertex, index and indirect ones fail
        /// for exactly the same reason: no bind but the constant-buffer bind carries the ring's per-frame base, so
        /// the other one would read segment zero while the uniform read read segment N.
        /// <para>
        /// This combination is ACCEPTED by <see cref="GpuBackendKind.Direct3D11"/>, which is what makes it a
        /// divergence rather than a bug fix, and it is vacuous in the engine today: no renderer call site combines
        /// the uniform bit with anything.
        /// </para>
        /// </summary>
        [Theory]
        [InlineData(GpuBufferUsage.UniformBuffer | GpuBufferUsage.StructuredBufferReadOnly)]
        [InlineData(GpuBufferUsage.UniformBuffer | GpuBufferUsage.StructuredBufferReadWrite)]
        [InlineData(GpuBufferUsage.UniformBuffer | GpuBufferUsage.VertexBuffer)]
        [InlineData(GpuBufferUsage.UniformBuffer | GpuBufferUsage.IndexBuffer)]
        [InlineData(GpuBufferUsage.UniformBuffer | GpuBufferUsage.IndirectBuffer)]
        public void AUniformBufferCombinedWithAnotherBinding_IsRefusedAtCreation(GpuBufferUsage usage)
        {
            ArgumentException ex = Assert.Throws<ArgumentException>(() => D3D11ViewPolicy.ForBuffer(usage));

            // The message has to name the divergence, because the combination is legal on the seam and a
            // consumer meeting the refusal has no other way to find out why this backend will not take it.
            // The Veldrid backend that used to accept it was deleted in 18.0.0, which the message says too.
            Assert.Contains("documented divergence", ex.Message, StringComparison.Ordinal);
        }

        /// <summary>The dynamic bit is the one thing a uniform buffer may be combined with, because the ring IS
        /// the dynamic path and a caller asking for both is asking for the same resource twice. No renderer passes
        /// it, which is exactly why every per-frame uniform buffer took the stalling path on the incumbent.</summary>
        [Fact]
        public void AUniformBufferMayAlsoBeDeclaredDynamic()
        {
            D3D11BufferViewPlan plan = D3D11ViewPolicy.ForBuffer(
                GpuBufferUsage.UniformBuffer | GpuBufferUsage.Dynamic);

            Assert.True(plan.Ring);
            Assert.True(plan.Dynamic);
            Assert.Equal(D3D11BindUsage.ConstantBuffer, plan.Bind);
        }

        // ---- the segment geometry (U1) --------------------------------------------------------------------

        /// <summary>
        /// A SEGMENT IS THE BUFFER ROUNDED UP TO 256 BYTES, and that is a hard Direct3D requirement rather than a
        /// rounding habit: <c>*SetConstantBuffers1</c> wants its first constant on a 16-constant boundary, so a
        /// frame base that was not a multiple of 256 would be unbindable. Every real engine stride is already
        /// 256-aligned (256, 768, 8448, 9472) and the round-up is what keeps the rule true for one that is not.
        /// </summary>
        [Theory]
        [InlineData(256u, 256u)]
        [InlineData(768u, 768u)]
        [InlineData(8448u, 8448u)]
        [InlineData(16u, 256u)]
        [InlineData(272u, 512u)]
        public void ASegmentStride_IsTheBufferRoundedUpToTheOffsetBoundary(uint sizeInBytes, uint expectedStride)
        {
            Assert.Equal(expectedStride, D3D11UniformRing.SegmentStrideFor(sizeInBytes));
            Assert.Equal(expectedStride * 3, D3D11UniformRing.TotalBytesFor(sizeInBytes, 3));
        }

        /// <summary>The whole allocation is the stride times the frame count, which is the one number the seam's
        /// caller never learns about their own buffer. Three segments is the default and the M3 bet.</summary>
        [Theory]
        [InlineData(1, 256u)]
        [InlineData(3, 768u)]
        [InlineData(4, 1024u)]
        public void TheAllocation_IsOneSegmentPerFrameInFlight(int framesInFlight, uint expectedTotal)
        {
            using var harness = new D3D11RingHarness(sizeInBytes: 256, framesInFlight: framesInFlight);

            Assert.Equal(expectedTotal, harness.Ring.TotalBytes);
            Assert.Equal(256u, harness.Ring.SegmentStrideBytes);
            Assert.Equal(256u, harness.Ring.SizeInBytes);
            Assert.Equal(framesInFlight, harness.Ring.FramesInFlight);
        }

        /// <summary>Each frame's base is its segment index times the stride, and asking for a segment that does
        /// not exist is a caller bug rather than a wrap.</summary>
        [Fact]
        public void EverySegment_StartsAtItsOwnBase()
        {
            using var harness = new D3D11RingHarness(sizeInBytes: 768, framesInFlight: 3);

            Assert.Equal(0u, harness.Ring.FrameBaseBytes(0));
            Assert.Equal(768u, harness.Ring.FrameBaseBytes(1));
            Assert.Equal(1536u, harness.Ring.FrameBaseBytes(2));
            Assert.Throws<ArgumentOutOfRangeException>(() => { _ = harness.Ring.FrameBaseBytes(3); });
        }

        // ---- the bind-time arithmetic (U1, U3) ------------------------------------------------------------

        /// <summary>
        /// THE FRAME BASE IS AN ADDEND AT BIND TIME, which is the whole reason a resource set's pinned
        /// <see cref="GpuBufferRange"/> survives the ring across all 68 sites that build one at load time. The set
        /// resolved a buffer plus an offset plus a size once, at creation, and the segment is added here alongside
        /// the per-draw dynamic offset.
        /// </summary>
        [Theory]
        [InlineData(0u, 0u, 0u, 0u)]
        [InlineData(768u, 0u, 0u, 48u)]
        [InlineData(768u, 256u, 0u, 64u)]
        [InlineData(768u, 256u, 512u, 96u)]
        public void TheFirstConstant_IsTheFrameBasePlusTheRangeAndTheDynamicOffset(
            uint frameBase, uint rangeOffset, uint dynamicOffset, uint expected)
            => Assert.Equal(expected, D3D11ConstantRange.FirstConstant(frameBase, rangeOffset, dynamicOffset));

        /// <summary>The size in constants does not know about the ring at all, and must not: a bind names the
        /// caller's window, and the segment only moves where that window starts.</summary>
        [Fact]
        public void TheConstantCount_IsTheWindowAndNeverTheWholeRing()
        {
            using var harness = new D3D11RingHarness(sizeInBytes: 768, framesInFlight: 3);

            Assert.Equal(48u, D3D11ConstantRange.ConstantCount(harness.Ring.SizeInBytes));
            Assert.Equal(48u * 3, harness.Ring.TotalBytes / D3D11ConstantRange.ConstantSizeBytes);
        }

        /// <summary>A frame base computed off the allocator is bindable by construction, because the stride it is
        /// a multiple of is the 256-byte boundary a first constant may start on. Asserted over every segment of a
        /// buffer whose own size is NOT aligned, which is the case the round-up exists for.</summary>
        [Fact]
        public void EveryFrameBase_LandsOnTheConstantBufferOffsetBoundary()
        {
            using var harness = new D3D11RingHarness(sizeInBytes: 272, framesInFlight: 4);

            for (int segment = 0; segment < 4; segment++)
            {
                uint frameBase = harness.Ring.FrameBaseBytes(segment);
                Assert.Equal(0u, frameBase % D3D11ConstantRange.OffsetAlignmentBytes);
                Assert.Equal(0u, D3D11ConstantRange.FirstConstant(frameBase, 0, 0) % 16);
            }
        }

        // ---- the mapping lifecycle (U2) -------------------------------------------------------------------

        /// <summary>
        /// TWO NATIVE CALLS PER RING PER SUBMIT, WHICH IS THE FLOOR (decision U2). The first write of a record
        /// phase maps <c>NO_OVERWRITE</c>, every later write reuses that mapping, and the start of the next submit
        /// unmaps. Under the incumbent each of those writes was its own blocking staging map.
        /// </summary>
        [Fact]
        public void TheFirstWriteMaps_TheRestReuseIt_AndTheSubmitUnmaps()
        {
            using var harness = new D3D11RingHarness(sizeInBytes: 256, framesInFlight: 3);

            Assert.Equal(0, harness.Memory.MapCount);

            harness.Ring.Write(0, new byte[] { 1, 2, 3, 4 });
            harness.Ring.Write(16, new byte[] { 5, 6, 7, 8 });
            harness.Ring.Write(32, new byte[] { 9 });

            Assert.Equal(1, harness.Memory.MapCount);
            Assert.Equal(0, harness.Memory.UnmapCount);
            Assert.True(harness.Ring.IsMapped);
            Assert.Equal(1, harness.Allocator.MappedRingCount);

            harness.Allocator.UnmapMappedRings();

            Assert.Equal(1, harness.Memory.UnmapCount);
            Assert.False(harness.Ring.IsMapped);
            Assert.Equal(IntPtr.Zero, harness.Ring.MappedPointer);
            Assert.Equal(0, harness.Allocator.MappedRingCount);

            // The next record phase maps again, which is the second pair rather than a second mapping of the
            // first.
            harness.Ring.Write(0, new byte[] { 1 });
            Assert.Equal(2, harness.Memory.MapCount);
        }

        /// <summary>Unmapping when nothing is mapped costs nothing and is not an error, which is every submit of
        /// a frame that wrote no uniforms. The fake refuses a double unmap by name, so this would fail loudly if
        /// the registry kept a ring it had already released.</summary>
        [Fact]
        public void UnmappingTwice_DoesNothingTheSecondTime()
        {
            using var harness = new D3D11RingHarness(sizeInBytes: 256, framesInFlight: 3);

            harness.Ring.Write(0, new byte[] { 1 });
            harness.Allocator.UnmapMappedRings();
            harness.Allocator.UnmapMappedRings();

            Assert.Equal(1, harness.Memory.UnmapCount);
        }

        /// <summary>The map is a context call, so it takes the device's submit lock for the call and nothing
        /// else. The copy that follows does not, which is what keeps recording lock-free: acquiring the mapping
        /// happens once per ring per record phase and writing into it happens thousands of times.</summary>
        [Fact]
        public void TheMapAndTheUnmap_TakeTheSubmitLock()
        {
            using var harness = new D3D11RingHarness(sizeInBytes: 256, framesInFlight: 3);
            harness.Memory.SubmitLock = harness.SubmitLock;

            harness.Ring.Write(0, new byte[] { 1 });
            Assert.True(harness.Memory.LastMapHeldTheSubmitLock);

            harness.Allocator.UnmapMappedRings();
            Assert.True(harness.Memory.LastUnmapHeldTheSubmitLock);
        }

        /// <summary>A zero-length write maps nothing at all. It is not a special case anyone writes deliberately,
        /// it is what an empty span reaching <c>UpdateBuffer</c> looks like, and taking a mapping for it would
        /// cost two native calls a frame for no bytes.</summary>
        [Fact]
        public void AnEmptyWrite_TakesNoMapping()
        {
            using var harness = new D3D11RingHarness(sizeInBytes: 256, framesInFlight: 3);

            harness.Ring.Write(0, ReadOnlySpan<byte>.Empty);

            Assert.Equal(0, harness.Memory.MapCount);
            Assert.False(harness.Ring.IsMapped);
        }

        // ---- where a write lands ---------------------------------------------------------------------------

        /// <summary>
        /// A WRITE LANDS AT <c>frameBase + offset</c> IN THE CURRENT SEGMENT, byte for byte. This is the assertion
        /// the whole fake exists for: the ring writes through a raw pointer into memory the driver handed back, so
        /// the only way to say it went to the right place is to read the bytes.
        /// </summary>
        [Fact]
        public void AWrite_LandsInTheCurrentSegmentAtItsOwnOffset()
        {
            using var harness = new D3D11RingHarness(sizeInBytes: 256, framesInFlight: 3);

            harness.Ring.Write(0, new byte[] { 0xA1, 0xA2 });
            harness.Ring.Write(64, new byte[] { 0xB1 });

            Assert.Equal((byte)0xA1, harness.Memory.Bytes[0]);
            Assert.Equal((byte)0xA2, harness.Memory.Bytes[1]);
            Assert.Equal((byte)0xB1, harness.Memory.Bytes[64]);

            // Frame 1 writes the second segment, and frame 0's bytes are untouched, which is the entire point of
            // the ring: the GPU may still be reading them.
            harness.Allocator.UnmapMappedRings();
            harness.Allocator.BeginFrame();
            Assert.Equal(1, harness.Allocator.CurrentSegment);

            harness.Ring.Write(0, new byte[] { 0xC1, 0xC2 });

            Assert.Equal((byte)0xC1, harness.Memory.Bytes[256]);
            Assert.Equal((byte)0xC2, harness.Memory.Bytes[257]);
            Assert.Equal((byte)0xA1, harness.Memory.Bytes[0]);
            Assert.Equal((byte)0xA2, harness.Memory.Bytes[1]);
        }

        /// <summary>
        /// A WRITE PAST THE LOGICAL END IS REFUSED, and it has to be checked rather than left to the allocation's
        /// real size. The native buffer IS big enough to absorb it, so without the check the overflow would land
        /// in the NEXT frame's segment, which is memory the GPU may be reading, and present as another frame's
        /// uniforms being subtly wrong.
        /// </summary>
        [Fact]
        public void AWritePastTheLogicalEnd_IsRefused()
        {
            using var harness = new D3D11RingHarness(sizeInBytes: 256, framesInFlight: 3);

            Assert.Throws<ArgumentOutOfRangeException>(() => harness.Ring.Write(248, new byte[16]));
            Assert.Throws<ArgumentOutOfRangeException>(() => harness.Ring.Write(256, new byte[1]));

            // The last byte of the buffer is still writable, so the bound is exact rather than conservative.
            harness.Ring.Write(255, new byte[1] { 0xFF });
            Assert.Equal((byte)0xFF, harness.Memory.Bytes[255]);
        }

        // ---- the write-scoped fallback (D3D11RingMapScope.PerWrite) ---------------------------------------

        /// <summary>
        /// THE WRITE-SCOPED SCOPE MAPS AND UNMAPS AROUND EVERY WRITE. It was the immediate driver's degradation
        /// until work-breakdown row 9 built a flush point: that driver issues draws as the seam is called and
        /// Direct3D 11 does not permit a draw against a mapped resource, so with nowhere to hang an unmap the only
        /// window a mapping could survive was one write.
        /// <para>
        /// NO DRIVER SELECTS IT NOW. <c>MapScopeFor</c> answers <c>AcrossRecording</c> for both, and
        /// <c>D3D11BindFlush</c> unmaps before every DRAW and every DISPATCH on the immediate one, which is the
        /// per-FLUSH shape the spec names. Not at a pipeline switch: the hazard is a draw against a mapped
        /// resource, the switch's drain only binds constant buffers, and the next draw unmaps before it issues.
        /// This scope stays constructible and tested because it is the only one that holds the map, the copy and
        /// the unmap atomically.
        /// </para>
        /// </summary>
        [Fact]
        public void UnderTheWriteScopedFallback_EveryWriteMapsAndUnmaps()
        {
            using var harness = new D3D11RingHarness(sizeInBytes: 256, framesInFlight: 3,
                mapScope: D3D11RingMapScope.PerWrite);

            harness.Ring.Write(0, new byte[] { 1 });
            harness.Ring.Write(16, new byte[] { 2 });

            Assert.Equal(2, harness.Memory.MapCount);
            Assert.Equal(2, harness.Memory.UnmapCount);
            Assert.False(harness.Ring.IsMapped);
            Assert.Equal(0, harness.Allocator.MappedRingCount);

            // The bytes still land where the deferred driver would have put them. The scope decides when the
            // mapping is released and nothing else.
            Assert.Equal((byte)1, harness.Memory.Bytes[0]);
            Assert.Equal((byte)2, harness.Memory.Bytes[16]);
        }

        /// <summary>
        /// AND THAT WRITE IS ONE CRITICAL SECTION: the map, the copy and the unmap happen under the submit lock
        /// together. A mapping held while no lock is held is a mapping another thread can withdraw mid-copy, and
        /// this scope unmaps at the end of every write, so the copy would be running through a pointer the
        /// runtime has already taken back. That atomicity is the property this scope has and
        /// <c>AcrossRecording</c> does not, which is why it is kept rather than deleted with its last caller.
        /// <para>
        /// Asserted from the outside, which is the only place a lock scope is visible: a thread that holds the
        /// submit lock can never catch the ring mapped, because being mapped means the writer is inside the
        /// section this thread is currently holding.
        /// </para>
        /// </summary>
        [Fact]
        public void UnderTheWriteScopedFallback_TheMapTheCopyAndTheUnmap_AreOneCriticalSection()
        {
            using var harness = new D3D11RingHarness(sizeInBytes: 256, framesInFlight: 3,
                mapScope: D3D11RingMapScope.PerWrite);

            const int writes = 2000;
            using var finished = new ManualResetEventSlim();
            var writer = new Thread(() =>
            {
                for (int i = 0; i < writes; i++) harness.Ring.Write(0, new byte[] { 0x11 });
                finished.Set();
            }) { IsBackground = true };

            writer.Start();

            bool sawMappedWhileHoldingTheLock = false;
            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (!finished.IsSet && !sawMappedWhileHoldingTheLock && DateTime.UtcNow < deadline)
            {
                lock (harness.SubmitLock) sawMappedWhileHoldingTheLock = harness.Ring.IsMapped;
            }

            Assert.True(finished.Wait(TimeSpan.FromSeconds(30)), "The writing thread never finished.");
            Assert.False(sawMappedWhileHoldingTheLock,
                "A ring was mapped while the submit lock was held by another thread, so a write-scoped mapping is "
                + "released outside the lock and a concurrent unmap can withdraw it mid-copy.");
            Assert.Equal(writes, harness.Memory.MapCount);
            Assert.Equal(writes, harness.Memory.UnmapCount);
            Assert.Equal((byte)0x11, harness.Memory.Bytes[0]);
        }

        /// <summary>
        /// BOTH DRIVERS KEEP THE MAPPING ACROSS THE RECORD PHASE, which is what changed when work-breakdown row 9
        /// built the flush point. The immediate driver cannot hold a mapping across a DRAW, and it does not have
        /// to hold one per WRITE to avoid that: <see cref="D3D11BindFlush"/> unmaps before every draw, dispatch
        /// and pipeline switch, which is the per-FLUSH degradation the spec names.
        /// <para>
        /// Asserted for both modes rather than for the one that changed, because the pairing is what matters: the
        /// immediate driver going back to <see cref="D3D11RingMapScope.PerWrite"/> while the flush still unmaps
        /// for it would map and unmap twice per write and measure a handicap into milestone M1.
        /// </para>
        /// </summary>
        [Fact]
        public void TheMapScope_IsAcrossTheRecordPhaseOnBothDrivers()
        {
            Assert.Equal(D3D11RingMapScope.AcrossRecording,
                D3D11RingAllocator.MapScopeFor(D3D11RecordMode.Deferred));
            Assert.Equal(D3D11RingMapScope.AcrossRecording,
                D3D11RingAllocator.MapScopeFor(D3D11RecordMode.Immediate));
        }

        /// <summary>And the other half of that pairing: the ring allocator reaches the bind flush on the immediate
        /// driver and NOT on the deferred one, whose <c>Submit</c> already unmaps inside the lock it replays
        /// under. Wiring it on both would cost an uncontended lock per draw for a call that can never do anything
        /// there, and would contradict decision T2's "zero Map or Unmap during replay" by trying.</summary>
        [Fact]
        public void OnlyTheImmediateDriver_UnmapsTheRingsAtTheFlushPoint()
        {
            using var harness = new D3D11RingHarness(sizeInBytes: 256, framesInFlight: 3);

            Assert.Same(harness.Allocator,
                D3D11BindFlush.RingsFor(D3D11RecordMode.Immediate, harness.Allocator));
            Assert.Null(D3D11BindFlush.RingsFor(D3D11RecordMode.Deferred, harness.Allocator));
        }

        // ---- the routing (U4) ------------------------------------------------------------------------------

        /// <summary>
        /// A RECORD-TIME UNIFORM WRITE RECORDS NO OP AT ALL. It goes straight into the mapped segment, so the
        /// memcpy the caller already asked for IS the memcpy into GPU-visible memory: no staging buffer, no copy
        /// command, no arena byte, and nothing left for the replay to do. That is what section 5.1 sizes the
        /// command stream against, and it is the pathology of 6.1 gone.
        /// </summary>
        [Fact]
        public void ARecordTimeUniformWrite_RecordsNothingAndLandsInTheRing()
        {
            using var harness = new D3D11RingHarness(sizeInBytes: 256, framesInFlight: 3);
            var buffer = new FakeRingBackedBuffer(harness.Ring);

            using D3D11CommandRecorder<D3D11StreamEmitter> list = D3D11CommandDrivers.CreateDeferred();
            D3D11CommandStream stream = list.Emitter.Stream;
            list.Begin();
            list.UpdateBuffer<byte>(buffer, 32, new byte[] { 0xD1, 0xD2 });
            list.End();

            Assert.Equal(0, stream.Count);
            Assert.Equal(0, stream.PayloadLength);
            Assert.Equal(0, stream.ReferenceCount);
            Assert.Equal((byte)0xD1, harness.Memory.Bytes[32]);
            Assert.Equal((byte)0xD2, harness.Memory.Bytes[33]);
        }

        /// <summary>A bulk write is unchanged and still takes the arena, because the caller's span is dangling by
        /// the time the list is submitted. Decision U4's split is one type test rather than a rule to remember at
        /// three call sites.</summary>
        [Fact]
        public void ABulkWrite_StillTakesTheRecordingsArena()
        {
            var buffer = new FakeBuffer(1024);

            using D3D11CommandRecorder<D3D11StreamEmitter> list = D3D11CommandDrivers.CreateDeferred();
            D3D11CommandStream stream = list.Emitter.Stream;
            list.Begin();
            list.UpdateBuffer<byte>(buffer, 64, new byte[] { 1, 2, 3, 4 });
            list.End();

            Assert.Equal(1, stream.Count);
            Assert.Equal(4, stream.PayloadLength);
            Assert.Equal(D3D11OpCode.UpdateBuffer, stream.Ops[0].Code);
            Assert.Equal(64u, stream.Ops[0].Arg0);
        }

        // ---- the frames-in-flight lever (M3) ---------------------------------------------------------------

        /// <summary>Three segments unless something says otherwise, which is decision U1's number and milestone
        /// M3's bet.</summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void NoValue_LeavesThreeSegments(string? envValue)
        {
            Assert.Equal(3, D3D11FramesInFlight.Resolve(envValue, out string? unrecognized));
            Assert.Null(unrecognized);
        }

        /// <summary>A whole number in range is taken as it stands, whitespace and all. One is legal on purpose:
        /// every frame then waits for the previous frame's submission, which is the degenerate case that proves
        /// the backpressure counter is measuring something real.</summary>
        [Theory]
        [InlineData("1", 1)]
        [InlineData("2", 2)]
        [InlineData(" 4 ", 4)]
        [InlineData("16", 16)]
        public void ANumberInRange_IsTaken(string envValue, int expected)
        {
            Assert.Equal(expected, D3D11FramesInFlight.Resolve(envValue, out string? unrecognized));
            Assert.Null(unrecognized);
        }

        /// <summary>
        /// A value that is not a number, or is outside the range, comes back verbatim so the caller can WARN, and
        /// the default is used. That branch matters more here than for an ordinary setting: this variable exists
        /// to settle a measurement, so a mistyped value that silently left three segments would produce a capture
        /// that reads as evidence about four and was taken on three.
        /// </summary>
        [Theory]
        [InlineData("0")]
        [InlineData("-1")]
        [InlineData("17")]
        [InlineData("three")]
        [InlineData("3.5")]
        public void AValueThatIsNotAUsableCount_WarnsAndKeepsTheDefault(string envValue)
        {
            Assert.Equal(3, D3D11FramesInFlight.Resolve(envValue, out string? unrecognized));
            Assert.Equal(envValue, unrecognized);
            Assert.Contains(envValue, D3D11FramesInFlight.UnrecognizedWarning(envValue), StringComparison.Ordinal);
        }

        /// <summary>The active line names the count either way, so a soak capture proves the number its
        /// backpressure counter was measured against rather than resting on the tester believing they set the
        /// variable.</summary>
        [Fact]
        public void TheActiveLine_NamesTheSegmentCount()
        {
            Assert.Contains(D3D11FramesInFlight.EnvVarName, D3D11FramesInFlight.ActiveDescription(3),
                StringComparison.Ordinal);
            Assert.Contains("4", D3D11FramesInFlight.ActiveDescription(4), StringComparison.Ordinal);
        }

        /// <summary>An allocator cannot be built outside the range the lever clamps to, so a caller that read the
        /// variable itself and skipped the clamp fails at construction rather than allocating a ring with no
        /// segments.</summary>
        [Theory]
        [InlineData(0)]
        [InlineData(17)]
        public void AnAllocatorOutsideTheSegmentRange_IsRefused(int framesInFlight)
            => Assert.Throws<ArgumentOutOfRangeException>(
                () => new D3D11RingAllocator(framesInFlight, new FakeD3D11Completion(), new object()));

        // ---- the platform boundary -------------------------------------------------------------------------

        /// <summary>
        /// THE CLAIM DECISION P1 RESTS ON, checked for everything this row added: driving the whole ring surface
        /// must not put the Direct3D interop into the process on a platform that has none. That is what lets these
        /// be plain facts rather than <c>[GpuFact]</c>s, and it holds only while the ring, the allocator and the
        /// routing stay free of Vortice, with the two native calls behind
        /// <see cref="ID3D11RingMemory"/>.
        /// </summary>
        [Fact]
        public void OffWindows_TheWholeRingSurfaceLoadsNoDirect3DInterop()
        {
            if (KhaozEngineD3D11.IsPlatformSupported) return;   // on Windows it loads, by design

            using (var harness = new D3D11RingHarness(sizeInBytes: 272, framesInFlight: 3))
            {
                harness.Ring.Write(0, new byte[] { 1, 2, 3, 4 });
                _ = harness.Ring.CurrentFrameBaseBytes;
                _ = harness.Ring.MappedPointer;
                _ = D3D11ConstantRange.FirstConstant(harness.Ring.CurrentFrameBaseBytes, 0, 0);
                harness.Allocator.UnmapMappedRings();
                harness.Allocator.OnSubmitted(1);
                harness.Allocator.BeginFrame();

                // Drives the off-timeline write's fence gate into a DEFERRAL rather than past it, and then drives
                // the replay, so the patch storage, the apply at the frame boundary and the counters are inside
                // the no-interop claim too. Segment 0 is owned by 1 and the GPU has not reached it, so the write
                // below queues a patch for it, and the boundary after the completion drains it.
                harness.Allocator.UpdateBuffer(harness.Ring, 16, new byte[] { 5 });
                harness.Completion.Completed = 1;
                harness.Allocator.BeginFrame();
                harness.Allocator.BeginFrame();
                _ = harness.Allocator.LastFrameBackpressure;
                _ = harness.Allocator.OffTimelinePatches;
                harness.Allocator.Forget(harness.Ring);
            }

            _ = D3D11FramesInFlight.FromEnvironment(out _);
            _ = D3D11ViewPolicy.ForBuffer(GpuBufferUsage.UniformBuffer);

            D3D11InteropLoad.AssertNotLoaded();
        }
    }
}
