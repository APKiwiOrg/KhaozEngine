using System;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE PER-LIST STAGING ARENA (M-M8, section 9.3), device-free: the size classes, the sub-allocation, the
    /// recycling boundary and the retention cap.
    ///
    /// <para><b>WHAT A RED RUN MEANS, and the two failures are opposites.</b> Recycling too EARLY hands a block
    /// back while a submitted blit is still reading it, which corrupts an upload several frames from its cause
    /// and which nothing on the device reports. Recycling too LATE, or not pooling at all, is the incumbent's
    /// allocate-and-release per upload, which is a native allocation per record-time <c>UpdateBuffer</c> and is
    /// the cost this type exists to remove. <see cref="MetalStagingArena.BlocksCreated"/> is the number that
    /// separates them.</para>
    /// </summary>
    public sealed class MetalStagingArenaTests : IDisposable
    {
        readonly MetalRingHarness _harness = new();

        /// <inheritdoc/>
        public void Dispose() => _harness.Dispose();

        /// <summary>
        /// A DEAD DEVICE LEASES NOTHING AND ALLOCATES NOTHING (M-F6). Block creation ends in
        /// <c>-newBufferWithLength:options:</c> on the device that owns this arena, and it was the one
        /// native-resource path in this backend with no liveness check in front of it. What comes back is an
        /// invalid lease rather than an exception, because every other write on a dead device is a silent no-op:
        /// the seam has no recovery path and the frame loop above it is not written to handle one.
        /// </summary>
        [Fact]
        public void ADeadDeviceLeasesNothingAndCreatesNoBlock()
        {
            using MetalStagingArena arena = _harness.NewArena();

            // One live lease first, so the assertion below is about the flip rather than about an arena that
            // never worked, and so there is an OPEN block a dead request could still have bumped into.
            Assert.True(arena.Take(128).IsValid);

            _harness.Liveness.MarkDead();

            MetalStagingLease afterDeath = arena.Take(128);

            Assert.False(afterDeath.IsValid);
            Assert.Equal(IntPtr.Zero, afterDeath.Mapped);
            Assert.Equal(1, arena.BlocksCreated);
            Assert.Single(_harness.Staging.Created);
        }

        /// <summary>AND THE REFUSALS STILL RUN FIRST on a dead device, because a zero or misaligned size is the
        /// caller's mistake either way and a no-op there would hide it.</summary>
        [Fact]
        public void ADeadDeviceStillRefusesAMisalignedOrZeroLease()
        {
            using MetalStagingArena arena = _harness.NewArena();
            _harness.Liveness.MarkDead();

            Assert.Throws<ArgumentOutOfRangeException>(() => arena.Take(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => arena.Take(6));
        }

        /// <summary>
        /// AND THE RECORD-TIME STAGED WRITE BECOMES A NO-OP THROUGH IT, which is the caller's half of the same
        /// guard: no encoder opened, no copy emitted, nothing thrown. The ring path is not affected, because a
        /// uniform write is a memcpy into memory the device already handed out and takes no arena at all.
        /// </summary>
        [Fact]
        public void ARecordTimeStagedWriteOnADeadDeviceRecordsNothing()
        {
            using MetalStagingArena arena = _harness.NewArena();
            var calls = new FakeMetalEncoderCalls();
            var encoders = new MetalEncoderScope(new FakeMetalEncoderSink(calls));
            encoders.BeginRecording(new IntPtr(0x100));

            _harness.Liveness.MarkDead();

            MetalBufferUpload.Record(ring: null, 0, new IntPtr(0xDEAD), 1024, 0, new byte[64], encoders, arena,
                _harness.Blit);

            Assert.Equal(0, calls.EncoderBoundaries);
            Assert.Equal(MetalEncoderKind.None, encoders.Open);
            Assert.Empty(_harness.Blit.Copies);
            Assert.Equal(0, arena.BlocksCreated);
        }

        [Fact]
        public void ARunOfSmallUploadsSharesOneBlock()
        {
            using MetalStagingArena arena = _harness.NewArena();

            for (int i = 0; i < 64; i++) arena.Take(128);

            Assert.Equal(1, arena.BlocksCreated);
            Assert.Equal(1, arena.OpenBlockCount);
            Assert.Single(_harness.Staging.Created);
            Assert.Equal(MetalStagingArena.DefaultBlockBytes, _harness.Staging.Created[0]);
        }

        /// <summary>Leases inside one block do not overlap and each is four-byte aligned, which is what the copy
        /// selector requires of its source offset on macOS.</summary>
        [Fact]
        public void LeasesInOneBlockAreDisjointAndAligned()
        {
            using MetalStagingArena arena = _harness.NewArena();

            MetalStagingLease first = arena.Take(MetalStagingArena.AlignedCopyBytes(6));
            MetalStagingLease second = arena.Take(MetalStagingArena.AlignedCopyBytes(6));
            MetalStagingLease third = arena.Take(MetalStagingArena.AlignedCopyBytes(6));

            Assert.Equal(first.Buffer, second.Buffer);
            Assert.Equal(0ul, first.OffsetBytes % MetalStagingArena.CopyAlignment);
            Assert.Equal(0ul, second.OffsetBytes % MetalStagingArena.CopyAlignment);
            Assert.Equal(0ul, third.OffsetBytes % MetalStagingArena.CopyAlignment);

            Assert.True(first.OffsetBytes + first.SizeBytes <= second.OffsetBytes);
            Assert.True(second.OffsetBytes + second.SizeBytes <= third.OffsetBytes);
        }

        /// <summary>A request larger than the floor takes the next power of two, which is what gives the pool a
        /// small number of classes a load revisits rather than one entry per distinct upload size.</summary>
        [Theory]
        [InlineData(1ul, 64ul * 1024)]
        [InlineData(64ul * 1024, 64ul * 1024)]
        [InlineData(64ul * 1024 + 1, 128ul * 1024)]
        [InlineData(700ul * 1024, 1024ul * 1024)]
        public void ABlockTakesTheNextPowerOfTwoAtOrAboveTheRequest(ulong request, ulong expected)
            => Assert.Equal(expected, MetalStagingArena.BlockSizeFor(request, MetalStagingArena.DefaultBlockBytes));

        /// <summary>
        /// A SLOT'S BLOCKS COME BACK ONLY ONCE THE SUBMISSION THAT READ THEM HAS COMPLETED, which is the whole of
        /// M-M8's recycling boundary on this backend: there is no command-pool ring here to inherit a proof from
        /// (M-R2), so the arena carries the timeline value itself.
        /// </summary>
        [Fact]
        public void ASlotIsRecycledOnlyAfterItsSubmissionCompleted()
        {
            using MetalStagingArena arena = _harness.NewArena();

            arena.BeginSlot(0, 0);
            arena.Take(128);
            arena.RecordSubmitted(7);

            Assert.Equal(1, arena.OpenBlockCount);
            Assert.Equal(7ul, arena.SlotValue(0));

            // Back on slot 0 with the GPU still short of 7: the block stays out, because handing it back would
            // hand back memory the submitted blit is reading.
            arena.BeginSlot(0, 6);
            Assert.Equal(1, arena.OpenBlockCount);
            Assert.Equal(0, arena.FreeBlockCount);

            // And now the GPU has passed it.
            arena.BeginSlot(0, 7);
            Assert.Equal(0, arena.OpenBlockCount);
            Assert.Equal(1, arena.FreeBlockCount);
            Assert.Equal(0ul, arena.SlotValue(0));
        }

        /// <summary>A slot nothing was submitted from has nothing to wait for, so it recycles at its next visit
        /// with a completion value of zero. That is the load-time and never-submitted case, and gating it would
        /// pin blocks for the life of a list that recorded and never submitted.</summary>
        [Fact]
        public void ASlotWithNothingSubmittedRecyclesImmediately()
        {
            using MetalStagingArena arena = _harness.NewArena();

            arena.BeginSlot(0, 0);
            arena.Take(128);

            arena.BeginSlot(0, 0);

            Assert.Equal(0, arena.OpenBlockCount);
            Assert.Equal(1, arena.FreeBlockCount);
        }

        /// <summary>A recycled block is REUSED rather than reallocated, which is the point of the pool: the
        /// incumbent's shape is one native allocation per upload and this is what makes the count stop
        /// climbing.</summary>
        [Fact]
        public void ARecycledBlockIsTakenAgainRatherThanReallocated()
        {
            using MetalStagingArena arena = _harness.NewArena();

            arena.BeginSlot(0, 0);
            arena.Take(128);
            Assert.Equal(1, arena.BlocksCreated);

            for (int frame = 0; frame < 32; frame++)
            {
                arena.BeginSlot(0, 0);
                arena.Take(128);
            }

            Assert.Equal(1, arena.BlocksCreated);
            Assert.Equal(0, arena.BlocksDestroyed);
        }

        /// <summary>
        /// THE RETENTION CAP RELEASES THE LARGEST BLOCKS FIRST, which is the direction that keeps the pool
        /// useful: the small classes are the ones a load revisits thousands of times, and the one enormous block
        /// a single vertex stream needed is the one worth giving back.
        /// </summary>
        [Fact]
        public void TheRetentionCapReleasesTheLargestBlocksFirst()
        {
            // A cap of one floor block, so the second retained block has to go.
            using MetalStagingArena arena = _harness.NewArena(retentionBytes: MetalStagingArena.DefaultBlockBytes);

            arena.BeginSlot(0, 0);
            arena.Take(128);                                    // one floor-sized block
            arena.Take(MetalStagingArena.DefaultBlockBytes * 4); // one much larger block
            Assert.Equal(2, arena.BlocksCreated);

            arena.BeginSlot(0, 0);

            Assert.Equal(1, arena.FreeBlockCount);
            Assert.Equal(MetalStagingArena.DefaultBlockBytes, arena.RetainedBytes);
            Assert.Equal(1, arena.BlocksDestroyed);
            Assert.Equal(MetalStagingArena.DefaultBlockBytes * 4, _harness.Staging.Destroyed[0]);
        }

        /// <summary>A retention of zero keeps nothing, which is the incumbent's own shape and is constructible so
        /// the difference is pinned rather than described.</summary>
        [Fact]
        public void ARetentionOfZeroKeepsNothing()
        {
            using MetalStagingArena arena = _harness.NewArena(retentionBytes: 0);

            arena.BeginSlot(0, 0);
            arena.Take(128);
            arena.BeginSlot(0, 0);

            Assert.Equal(0, arena.FreeBlockCount);
            Assert.Equal(1, arena.BlocksDestroyed);
        }

        /// <summary>Each slot's blocks are its own, so opening one slot does not touch another's. That is what
        /// makes the per-slot timeline value sufficient: a slot is only ever recycled against the submission
        /// that read the blocks IN it.</summary>
        [Fact]
        public void OpeningOneSlotLeavesTheOtherSlotsBlocksAlone()
        {
            using MetalStagingArena arena = _harness.NewArena();

            arena.BeginSlot(0, 0);
            arena.Take(128);
            arena.RecordSubmitted(4);

            arena.BeginSlot(1, 0);
            arena.Take(128);
            arena.RecordSubmitted(5);

            Assert.Equal(2, arena.OpenBlockCount);

            // Slot 2 has never held anything, and opening it recycles nothing at all.
            arena.BeginSlot(2, 0);

            Assert.Equal(2, arena.OpenBlockCount);
            Assert.Equal(4ul, arena.SlotValue(0));
            Assert.Equal(5ul, arena.SlotValue(1));
        }

        /// <summary>The highest value wins for a slot, because a list re-Begun without the ring wrapping stays on
        /// the same slot and its blocks are then read by both submissions.</summary>
        [Fact]
        public void ASlotTakesTheHighestValueSubmittedWhileItWasOpen()
        {
            using MetalStagingArena arena = _harness.NewArena();

            arena.BeginSlot(0, 0);
            arena.RecordSubmitted(9);
            arena.RecordSubmitted(4);

            Assert.Equal(9ul, arena.SlotValue(0));
        }

        [Fact]
        public void ASlotThatDoesNotExistIsRefusedByName()
        {
            using MetalStagingArena arena = _harness.NewArena();

            Assert.Throws<ArgumentOutOfRangeException>(() => arena.BeginSlot(-1, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => arena.BeginSlot(arena.Depth, 0));
        }

        [Fact]
        public void AZeroByteLeaseIsRefused()
        {
            using MetalStagingArena arena = _harness.NewArena();

            Assert.Throws<ArgumentOutOfRangeException>(() => arena.Take(0));
        }

        /// <summary>
        /// A LEASE IS TAKEN AT A MULTIPLE OF FOUR, as a PRECONDITION rather than something the arena rounds, and
        /// that is what makes a block the exact size of a request enough. An arena that rounded the block instead
        /// would spend a whole size class on every power-of-two request, which is a doubling of the staging
        /// footprint for the largest uploads, the ones where it costs most.
        /// </summary>
        [Fact]
        public void AnUnalignedLeaseSizeIsRefusedByName()
        {
            using MetalStagingArena arena = _harness.NewArena();

            ArgumentOutOfRangeException thrown = Assert.Throws<ArgumentOutOfRangeException>(() => arena.Take(6));

            Assert.Contains("AlignedCopyBytes", thrown.Message, StringComparison.Ordinal);
        }

        /// <summary>A request of exactly one size class takes exactly that class, which is the arithmetic the
        /// precondition above buys.</summary>
        [Fact]
        public void ARequestOfExactlyOneSizeClassTakesThatClass()
        {
            using MetalStagingArena arena = _harness.NewArena();

            arena.Take(MetalStagingArena.DefaultBlockBytes);

            Assert.Equal(MetalStagingArena.DefaultBlockBytes, _harness.Staging.Created[0]);
        }

        [Fact]
        public void DisposalReleasesEveryBlockOpenAndPooled()
        {
            MetalStagingArena arena = _harness.NewArena();

            arena.BeginSlot(0, 0);
            arena.Take(128);
            arena.BeginSlot(1, 0);
            arena.Take(MetalStagingArena.DefaultBlockBytes * 2);

            arena.Dispose();

            Assert.Equal(arena.BlocksCreated, arena.BlocksDestroyed);
            Assert.Equal(_harness.Staging.Created.Count, _harness.Staging.Destroyed.Count);

            // IDEMPOTENT, because a list disposed twice is a teardown-order accident rather than a defect, and a
            // second pass would release every block a second time.
            arena.Dispose();
            Assert.Equal(_harness.Staging.Created.Count, _harness.Staging.Destroyed.Count);
        }

        [Fact]
        public void ADepthOutsideTheKnobsRangeIsRefusedByName()
        {
            ArgumentOutOfRangeException thrown = Assert.Throws<ArgumentOutOfRangeException>(
                () => new MetalStagingArena(_harness.Staging, MetalFramesInFlight.Maximum + 1));

            Assert.Contains("slots", thrown.Message, StringComparison.Ordinal);
        }
    }
}
