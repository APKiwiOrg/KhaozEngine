using System;
using System.Linq;
using System.Threading;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Gpu.Vulkan.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE DEFERRAL HALF OF <see cref="IVulkanStagingSource.Destroy"/>'s CONTRACT, which row 8 wrote down and left
    /// for this row to prove. Its own arena tests could not: a fake source has no timeline to defer against, so an
    /// immediate native free and a correctly deferred one look identical there. Here the source is the real one
    /// over the real retire list, and the difference is the whole test.
    ///
    /// <para><b>WHY IT MATTERS RATHER THAN BEING TIDINESS.</b> <see cref="VulkanStagingArena.Dispose"/> destroys
    /// every block it holds UNGATED, because an arena has no way to know what is in flight. A source that freed
    /// immediately would therefore free a block an in-flight submission is still reading, which is exactly the
    /// corruption class the arena's own slot gate exists to prevent, arriving through the one call the arena trusts
    /// to be safe.</para>
    /// </summary>
    public sealed class VulkanStagingSourceTests
    {
        /// <summary>
        /// A BLOCK IS A HOST-VISIBLE, MAPPED, TRANSFER-CAPABLE BUFFER out of the UPLOAD ladder, which is
        /// host-visible and coherent and preferably NOT device-local: bytes on their way to VRAM should not be
        /// occupying VRAM.
        /// </summary>
        [Fact]
        public void ABlock_IsAMappedTransferBuffer()
        {
            var fixture = new VulkanResourceFixture();

            VulkanStagingBlock block = fixture.StagingSource.Create(4096);

            Assert.True(block.IsValid);
            Assert.Equal(4096UL, block.SizeBytes);
            Assert.NotEqual(0, block.Mapped);
            Assert.Equal(VulkanBufferBinding.TransferSrc | VulkanBufferBinding.TransferDst,
                fixture.ResourceApi.BindingOf(block.Buffer));
            Assert.Equal(1, fixture.StagingSource.LiveBlockCount);
        }

        /// <summary>
        /// DESTROY DEFERS THROUGH THE RETIRE LIST RATHER THAN FREEING, which is the contract's own sentence. The
        /// observable difference is that the buffer is still live the instant Destroy returns, and stops being live
        /// only when the drain runs it.
        /// </summary>
        [Fact]
        public void Destroy_DefersTheNativeFreeThroughTheRetireList()
        {
            var fixture = new VulkanResourceFixture();

            VulkanStagingBlock block = fixture.StagingSource.Create(4096);
            Assert.Contains(block.Buffer, fixture.ResourceApi.Live);

            fixture.StagingSource.Destroy(block);

            // HELD, not freed. This is the assertion the fake source could not make.
            Assert.Contains(block.Buffer, fixture.ResourceApi.Live);
            Assert.Equal(1, fixture.Retired.Count);
            Assert.Equal(1, fixture.StagingSource.DeferredDestroyCount);
            Assert.Equal(0, fixture.StagingSource.LiveBlockCount);

            Assert.Equal(1, fixture.Drain());
            Assert.DoesNotContain(block.Buffer, fixture.ResourceApi.Live);
        }

        /// <summary>
        /// THE HELD DESTROY IS GATED ON A VALUE AT OR ABOVE EVERY LIVE SUBMISSION'S, so a block whose submission
        /// has not completed is NOT released by a drain. The contract asks for the highest submitted value and the
        /// source uses the highest ALLOCATED one, which is at or above it and closes the window a submission
        /// between taking its value and registering it would otherwise leave open.
        /// </summary>
        [Fact]
        public void AHeldDestroy_WaitsForTheTimelineToPassIt()
        {
            var fixture = new VulkanResourceFixture();

            VulkanStagingBlock block = fixture.StagingSource.Create(4096);

            // A submission takes value 1 and has not completed.
            ulong submitted = fixture.Timeline.NextSubmitValue();
            fixture.Timeline.RegisterSubmitted(submitted);

            fixture.StagingSource.Destroy(block);

            // The counter is still at 0, so the drain releases nothing and the block survives.
            Assert.Equal(0, fixture.Drain());
            Assert.Contains(block.Buffer, fixture.ResourceApi.Live);

            fixture.Semaphore.Completed = submitted;
            Assert.Equal(1, fixture.Drain());
            Assert.DoesNotContain(block.Buffer, fixture.ResourceApi.Live);
        }

        /// <summary>
        /// THE DEFERRED DESTROY IS TERMINAL: it destroys the buffer INLINE and frees the suballocation INLINE, and
        /// retires nothing of its own. The only further generation is the CHUNK the free may retire, which is the
        /// one the device's teardown already drains twice for, so the depth is bounded at two by construction
        /// rather than by a loop with a guard on it.
        /// </summary>
        [Fact]
        public void TheDeferredDestroy_IsTerminal()
        {
            var fixture = new VulkanResourceFixture();

            VulkanStagingBlock block = fixture.StagingSource.Create(4096);
            fixture.StagingSource.Destroy(block);
            fixture.Drain();

            // The buffer went and its memory went with it, in ONE entry. Nothing new was appended by running it,
            // because the pool keeps its last chunk.
            Assert.DoesNotContain(block.Buffer, fixture.ResourceApi.Live);
            Assert.Equal(0, fixture.Retired.Count);
        }

        /// <summary>
        /// A DEAD DEVICE ABANDONS RATHER THAN FREES. The block's buffer and its memory went with the device, so a
        /// destroy now is a call against memory the driver already released, which aborts the process through the
        /// Vulkan loader rather than failing quietly. Nothing is retired either, because nothing may run.
        /// </summary>
        [Fact]
        public void ADeadDevice_AbandonsRatherThanFrees()
        {
            var fixture = new VulkanResourceFixture();

            VulkanStagingBlock block = fixture.StagingSource.Create(4096);
            fixture.Liveness.Kill();

            fixture.StagingSource.Destroy(block);

            Assert.Equal(0, fixture.Retired.Count);
            Assert.Equal(1, fixture.StagingSource.AbandonedDestroyCount);
            Assert.Equal(0, fixture.StagingSource.DeferredDestroyCount);
            Assert.Equal(0, fixture.StagingSource.LiveBlockCount);
        }

        /// <summary>
        /// DESTROYING THE SAME BLOCK TWICE RETIRES ONCE. The arena is idempotent about its own disposal, and a
        /// second destroy that queued a second entry would double-destroy the buffer, which the fake seam refuses
        /// by name.
        /// </summary>
        [Fact]
        public void DestroyingTheSameBlockTwice_RetiresOnce()
        {
            var fixture = new VulkanResourceFixture();

            VulkanStagingBlock block = fixture.StagingSource.Create(4096);

            fixture.StagingSource.Destroy(block);
            fixture.StagingSource.Destroy(block);

            Assert.Equal(1, fixture.Retired.Count);
            Assert.Equal(1, fixture.Drain());
        }

        /// <summary>
        /// AN ARENA'S WHOLE DISPOSAL GOES THROUGH THE DEFERRAL, which is the shape the contract was written for:
        /// the arena calls Destroy ungated on every block it holds and the source is what makes each of those safe.
        /// </summary>
        [Fact]
        public void AnArenasDisposal_DefersEveryBlock()
        {
            var fixture = new VulkanResourceFixture();
            var arena = new VulkanStagingArena(fixture.StagingSource, framesInFlight: 3, blockBytes: 1024);

            arena.BeginSlot(0);
            arena.Take(900);
            arena.BeginSlot(1);
            arena.Take(900);

            Assert.Equal(2, fixture.StagingSource.LiveBlockCount);

            arena.Dispose();

            Assert.Equal(2, fixture.StagingSource.DeferredDestroyCount);
            Assert.Equal(2, fixture.Retired.Count);
            Assert.Equal(2, fixture.ResourceApi.Live.Count(_ => true));

            Assert.Equal(2, fixture.Drain());
            Assert.Empty(fixture.ResourceApi.Live);
        }

        /// <summary>
        /// A BLOCK CAN BE WRITTEN THROUGH AND READ BACK, which is the whole reason the mapping has to be real: the
        /// sub-allocation arithmetic is what places one upload's bytes after another's inside one block, and a
        /// pretend pointer would make that untestable.
        /// </summary>
        [Fact]
        public void TwoLeasesInOneBlock_LandAtDifferentOffsets()
        {
            var fixture = new VulkanResourceFixture();
            var arena = new VulkanStagingArena(fixture.StagingSource, framesInFlight: 3, blockBytes: 4096);

            arena.BeginSlot(0);

            VulkanStagingLease first = arena.Take(16);
            VulkanStagingLease second = arena.Take(16);

            Assert.Equal(first.Buffer, second.Buffer);
            Assert.NotEqual(first.OffsetBytes, second.OffsetBytes);

            first.Write([1, 2, 3, 4]);
            second.Write([9, 9, 9, 9]);

            unsafe
            {
                Assert.Equal(1, ((byte*)first.Mapped)[0]);
                Assert.Equal(9, ((byte*)second.Mapped)[0]);
            }

            arena.Dispose();
            fixture.Drain();
        }

        // ---- The counters, under the concurrency the type documents ----

        /// <summary>
        /// THE DEFERRED COUNT SURVIVES EVERY ARENA DESTROYING AT ONCE
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/551), which is the concurrency this type already
        /// claims: one source serves the device's own arena and one per command list, each on its own thread, and
        /// the block ledger takes a lock for exactly that reason. The counters were the one piece of state left
        /// outside it, so a plain increment could drop a destroy and present as a leak nobody can attribute, which
        /// is precisely what the reading exists to rule out.
        ///
        /// <para><b>THE LIVENESS TOKEN IS THE RENDEZVOUS, and that is what makes this a proof rather than a
        /// hope.</b> <c>Destroy</c> reads it immediately before it increments, so a token that parks every reader
        /// until all of them have arrived puts every thread on the increment together, on every round. A stress
        /// loop without it spends its time queueing on the ledger lock, which serialises the very instruction the
        /// test is about.</para>
        /// </summary>
        [Fact]
        public void TheDeferredDestroyCount_CountsEveryDestroyWhenEveryThreadDestroysAtOnce()
        {
            var fixture = new VulkanResourceFixture();
            var source = new VulkanStagingSource(fixture.Owner, new RendezvousLiveness(dead: false, Threads));

            DestroyFromEveryThreadAtOnce(source);

            Assert.Equal(Threads * Rounds, source.DeferredDestroyCount);
            Assert.Equal(0, source.AbandonedDestroyCount);
            Assert.Equal(0, source.LiveBlockCount);
            Assert.Equal(Threads * Rounds, fixture.Retired.Count);

            Assert.Equal(Threads * Rounds, fixture.Drain());
        }

        /// <summary>AND SO DOES THE ABANDONED COUNT, which is the arm with nothing after it: the deferred one at
        /// least takes the retire list's lock on its way out, and this one increments and returns. It is also the
        /// reading that matters most, because a large number says a consumer was still recording uploads after the
        /// device had gone.</summary>
        [Fact]
        public void TheAbandonedDestroyCount_CountsEveryDestroyWhenEveryThreadDestroysAtOnce()
        {
            var fixture = new VulkanResourceFixture();
            var source = new VulkanStagingSource(fixture.Owner, new RendezvousLiveness(dead: true, Threads));

            DestroyFromEveryThreadAtOnce(source);

            Assert.Equal(Threads * Rounds, source.AbandonedDestroyCount);
            Assert.Equal(0, source.DeferredDestroyCount);
            Assert.Equal(0, source.LiveBlockCount);
            Assert.Equal(0, fixture.Retired.Count);
        }

        // ---- Fixtures ----

        const int Threads = 8;
        const int Rounds = 250;

        // Every block created up front on one thread, because the ALLOCATOR is single-threaded by construction and
        // is not what this is about, then one thread per participant destroying its own slice. A dedicated Thread
        // rather than a pool task: the barrier needs all eight running at once, and a saturated pool would hand
        // them over one injection at a time.
        static void DestroyFromEveryThreadAtOnce(VulkanStagingSource source)
        {
            var blocks = new VulkanStagingBlock[Threads * Rounds];
            for (int i = 0; i < blocks.Length; i++) blocks[i] = source.Create(256);

            var failures = new Exception?[Threads];
            var threads = new Thread[Threads];

            for (int t = 0; t < Threads; t++)
            {
                int slice = t;
                threads[t] = new Thread(() =>
                {
                    try
                    {
                        for (int i = 0; i < Rounds; i++) source.Destroy(blocks[(slice * Rounds) + i]);
                    }
                    catch (Exception ex)
                    {
                        failures[slice] = ex;
                    }
                })
                {
                    IsBackground = true,
                    Name = "staging-destroy-" + slice.ToString(System.Globalization.CultureInfo.InvariantCulture),
                };
            }

            foreach (Thread thread in threads) thread.Start();

            foreach (Thread thread in threads)
            {
                Assert.True(thread.Join(TimeSpan.FromSeconds(60)), thread.Name + " never finished its slice.");
            }

            Assert.All(failures, failure => Assert.Null(failure));
        }

        // A LIVENESS TOKEN THAT PARKS EVERY READER AT THE SAME INSTRUCTION. The real one is a lock-free bool read
        // and the interface says an implementation must never take a lock, which this deliberately breaks: it is
        // the only place in Destroy where a fake can reach in between the ledger lock and the increment, and
        // holding every thread there is what turns a lost update from a rare interleaving into the ordinary one.
        sealed class RendezvousLiveness : IDeviceLiveness
        {
            readonly Barrier _gate;
            readonly bool _dead;

            internal RendezvousLiveness(bool dead, int participants)
            {
                _dead = dead;
                _gate = new Barrier(participants);
            }

            /// <inheritdoc/>
            public bool IsDead
            {
                get
                {
                    if (!_gate.SignalAndWait(TimeSpan.FromSeconds(10)))
                    {
                        throw new TimeoutException(
                            "A destroy waited ten seconds for the other threads to reach the same read.");
                    }

                    return _dead;
                }
            }
        }
    }
}
