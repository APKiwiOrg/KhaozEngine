using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    // The seam's deferred-disposal queue, behind Scene3D.UnloadMesh and SpriteBatch's set eviction. It replaces the
    // per-unload WaitForIdle that made every terrain chunk unload and LOD flip drain the whole device on the frame
    // thread. The contract these tests pin: retiring costs nothing, a resource is only ever destroyed once the GPU
    // is provably done with it (the lavapipe use-after-free rule the per-unload drain was protecting), and no path
    // frees a batch out of order.
    //
    // Three ripeness policies live here, and all three are covered. With a barrier (a device that signals a fence on
    // GPU COMPLETION: Metal, Vulkan) a batch dies on the first frame boundary its fence polls signaled and nothing
    // ever drains. Without one (Direct3D11, OpenGL) a batch waits out FrameDelay frame boundaries and dies behind
    // one WaitForIdle, which is exactly what every backend did before fences. The no-barrier tests below are the
    // original suite, unchanged on purpose: the fallback is meant to be bit-for-bit the old behaviour. Third,
    // FrameCountOnly waits out the same frame count and skips the drain entirely, for a caller that is inside the
    // frame's recording and can neither mint a fence nor afford a stall (#84).
    public class GpuRetireQueueTests
    {
        sealed class FakeResource : IDisposable
        {
            public int DisposeCount;
            public void Dispose() => DisposeCount++;
        }

        // A fence whose signal the test drives by hand, so ripeness is asserted at an exact frame instead of raced.
        sealed class FakeFence : IGpuFence
        {
            public bool Signaled { get; set; }
            public int Resets;
            public int Disposes;
            public void Reset() { Signaled = false; Resets++; }
            public void Dispose() => Disposes++;
        }

        // Mirrors GpuRetireBarrier: hands out a fence per sealed batch, recycles the ones handed back.
        sealed class FakeBarrier : IRetireBarrier
        {
            readonly Stack<FakeFence> _free = new();
            public readonly List<FakeFence> Issued = new();
            public int Submits, Releases, Disposes;
            /// <summary>Makes Submit return null, the way a real barrier would if it could not issue a fence. The
            /// batch then falls back to the frame count even though a barrier exists.</summary>
            public bool CannotIssue;

            public IGpuFence? Submit()
            {
                Submits++;
                if (CannotIssue) return null;
                FakeFence f;
                if (_free.Count > 0) { f = _free.Pop(); f.Reset(); }
                else f = new FakeFence();
                Issued.Add(f);
                return f;
            }

            public void Release(IGpuFence fence) { Releases++; _free.Push((FakeFence)fence); }
            public void Dispose() => Disposes++;
        }

        // Records the drain order against disposals so a test can assert nothing was destroyed before the drain.
        sealed class Recorder
        {
            public int Drains;
            public int DisposalsAtLastDrain;
        }

        [Fact]
        public void Retire_neither_drains_nor_disposes()
        {
            int drains = 0;
            var pool = new GpuRetireQueue(() => drains++, frameDelay: 2);
            var res = new FakeResource();

            pool.Retire(res);

            Assert.Equal(0, drains);
            Assert.Equal(0, res.DisposeCount);
            Assert.Equal(1, pool.PendingCount);
        }

        [Fact]
        public void BeginFrame_holds_the_resource_until_the_frame_delay_elapses()
        {
            int drains = 0;
            var pool = new GpuRetireQueue(() => drains++, frameDelay: 3);
            var res = new FakeResource();
            pool.Retire(res);

            pool.BeginFrame();
            pool.BeginFrame();
            Assert.Equal(0, res.DisposeCount);
            Assert.Equal(0, drains);

            pool.BeginFrame();

            Assert.Equal(1, res.DisposeCount);
            Assert.Equal(1, drains);
            Assert.Equal(0, pool.PendingCount);
        }

        [Fact]
        public void A_whole_batch_is_freed_behind_one_drain()
        {
            int drains = 0;
            var pool = new GpuRetireQueue(() => drains++, frameDelay: 1);
            var batch = new FakeResource[16];
            for (int i = 0; i < batch.Length; i++) { batch[i] = new FakeResource(); pool.Retire(batch[i]); }

            pool.BeginFrame();

            Assert.Equal(1, drains);   // one drain for sixteen resources, not sixteen drains
            foreach (FakeResource r in batch) Assert.Equal(1, r.DisposeCount);
        }

        [Fact]
        public void Nothing_is_disposed_before_the_drain()
        {
            var rec = new Recorder();
            int disposals = 0;
            var pool = new GpuRetireQueue(() => { rec.Drains++; rec.DisposalsAtLastDrain = disposals; }, frameDelay: 1);
            var a = new CountingResource(() => disposals++);
            var b = new CountingResource(() => disposals++);
            pool.Retire(a);
            pool.Retire(b);

            pool.BeginFrame();

            Assert.Equal(1, rec.Drains);
            Assert.Equal(0, rec.DisposalsAtLastDrain);   // the drain ran first, with nothing yet destroyed
            Assert.Equal(2, disposals);
        }

        sealed class CountingResource : IDisposable
        {
            readonly Action _onDispose;
            public CountingResource(Action onDispose) => _onDispose = onDispose;
            public void Dispose() => _onDispose();
        }

        [Fact]
        public void An_idle_frame_does_not_drain()
        {
            int drains = 0;
            var pool = new GpuRetireQueue(() => drains++, frameDelay: 1);

            for (int i = 0; i < 100; i++) pool.BeginFrame();

            Assert.Equal(0, drains);   // no retirements, so a frame boundary costs nothing
        }

        [Fact]
        public void Later_retirements_wait_their_own_delay()
        {
            int drains = 0;
            var pool = new GpuRetireQueue(() => drains++, frameDelay: 2);
            var first = new FakeResource();
            var second = new FakeResource();

            pool.Retire(first);
            pool.BeginFrame();
            pool.Retire(second);
            pool.BeginFrame();          // first is ripe here, second is not

            Assert.Equal(1, first.DisposeCount);
            Assert.Equal(0, second.DisposeCount);
            Assert.Equal(1, pool.PendingCount);

            pool.BeginFrame();

            Assert.Equal(1, second.DisposeCount);
            Assert.Equal(2, drains);
        }

        [Fact]
        public void FlushAll_frees_every_pending_resource_behind_one_drain()
        {
            int drains = 0;
            var pool = new GpuRetireQueue(() => drains++, frameDelay: 1000);
            var a = new FakeResource();
            var b = new FakeResource();
            pool.Retire(a);
            pool.Retire(b);

            pool.FlushAll();

            Assert.Equal(1, drains);
            Assert.Equal(1, a.DisposeCount);
            Assert.Equal(1, b.DisposeCount);
            Assert.Equal(0, pool.PendingCount);
        }

        [Fact]
        public void FlushAll_with_nothing_pending_does_not_drain()
        {
            int drains = 0;
            var pool = new GpuRetireQueue(() => drains++);

            pool.FlushAll();

            Assert.Equal(0, drains);
        }

        [Fact]
        public void Retiring_null_is_ignored()
        {
            int drains = 0;
            var pool = new GpuRetireQueue(() => drains++, frameDelay: 1);

            pool.Retire(null);

            Assert.Equal(0, pool.PendingCount);
            pool.BeginFrame();
            Assert.Equal(0, drains);
        }

        [Fact]
        public void A_frame_delay_below_one_still_defers_past_the_retiring_frame()
        {
            int drains = 0;
            var pool = new GpuRetireQueue(() => drains++, frameDelay: 0);
            var res = new FakeResource();

            pool.Retire(res);
            Assert.Equal(0, res.DisposeCount);   // never destroyed inside the call that retired it

            pool.BeginFrame();

            Assert.Equal(1, res.DisposeCount);
            Assert.Equal(1, drains);
        }

        // ---- the fence path ----

        [Fact]
        public void A_batch_is_sealed_behind_one_fence_at_the_frame_boundary_after_it_was_retired()
        {
            var barrier = new FakeBarrier();
            var pool = new GpuRetireQueue(() => Assert.Fail("the fence path must never drain"), barrier);
            for (int i = 0; i < 16; i++) pool.Retire(new FakeResource());

            Assert.Equal(0, barrier.Submits);   // retiring submits nothing

            pool.BeginFrame();

            Assert.Equal(1, barrier.Submits);   // one fence for sixteen resources
            Assert.Equal(1, pool.SealedBatchCount);
            Assert.Equal(16, pool.PendingCount);
        }

        [Fact]
        public void A_sealed_batch_is_held_until_its_fence_signals_and_then_freed_without_a_drain()
        {
            var barrier = new FakeBarrier();
            var pool = new GpuRetireQueue(() => Assert.Fail("the fence path must never drain"), barrier);
            var res = new FakeResource();
            pool.Retire(res);

            pool.BeginFrame();                            // seals, fence unsignaled
            for (int i = 0; i < 50; i++) pool.BeginFrame();
            Assert.Equal(0, res.DisposeCount);            // no frame count can free it, only the fence
            Assert.Equal(1, pool.PendingCount);

            barrier.Issued[0].Signaled = true;
            pool.BeginFrame();

            Assert.Equal(1, res.DisposeCount);
            Assert.Equal(0, pool.PendingCount);
            Assert.Equal(0, pool.SealedBatchCount);
        }

        [Fact]
        public void An_older_unsignaled_batch_holds_back_a_younger_signaled_one()
        {
            // The ordering that makes "this batch died" imply "every older batch died first". Freeing the younger
            // batch early would destroy resources whose own submission the GPU may not have reached.
            var barrier = new FakeBarrier();
            var pool = new GpuRetireQueue(() => Assert.Fail("the fence path must never drain"), barrier);
            var older = new FakeResource();
            var younger = new FakeResource();

            pool.Retire(older);
            pool.BeginFrame();          // batch 0
            pool.Retire(younger);
            pool.BeginFrame();          // batch 1

            barrier.Issued[1].Signaled = true;   // the YOUNGER fence signals first
            pool.BeginFrame();

            Assert.Equal(0, younger.DisposeCount);
            Assert.Equal(0, older.DisposeCount);

            barrier.Issued[0].Signaled = true;
            pool.BeginFrame();

            Assert.Equal(1, older.DisposeCount);
            Assert.Equal(1, younger.DisposeCount);   // both go once the prefix is clear
        }

        [Fact]
        public void An_idle_frame_seals_nothing_and_submits_no_fence()
        {
            var barrier = new FakeBarrier();
            var pool = new GpuRetireQueue(() => Assert.Fail("the fence path must never drain"), barrier);

            for (int i = 0; i < 100; i++) pool.BeginFrame();

            Assert.Equal(0, barrier.Submits);   // a frame that retired nothing costs no submission at all
        }

        [Fact]
        public void Fences_are_recycled_rather_than_allocated_per_batch()
        {
            var barrier = new FakeBarrier();
            var pool = new GpuRetireQueue(() => Assert.Fail("the fence path must never drain"), barrier);

            for (int i = 0; i < 20; i++)
            {
                pool.Retire(new FakeResource());
                pool.BeginFrame();                                    // seal batch i
                foreach (FakeFence f in barrier.Issued) f.Signaled = true;
                pool.BeginFrame();                                    // free it, handing the fence back
            }

            Assert.Equal(20, barrier.Submits);
            Assert.Equal(20, barrier.Releases);
            Assert.Single(barrier.Issued.Distinct());   // one device fence served all twenty batches
        }

        [Fact]
        public void A_barrier_that_cannot_issue_a_fence_falls_back_to_the_frame_count_and_drains()
        {
            var barrier = new FakeBarrier { CannotIssue = true };
            int drains = 0;
            var pool = new GpuRetireQueue(() => drains++, barrier, frameDelay: 3);
            var res = new FakeResource();
            pool.Retire(res);

            pool.BeginFrame();
            pool.BeginFrame();
            Assert.Equal(0, res.DisposeCount);

            pool.BeginFrame();

            Assert.Equal(1, res.DisposeCount);
            Assert.Equal(1, drains);            // the unfenced batch is destroyed behind a drain, as before
            Assert.Equal(0, barrier.Releases);  // nothing to recycle: there was no fence
        }

        [Fact]
        public void FlushAll_still_drains_on_the_fence_path()
        {
            // Teardown keeps the drain: correctness over speed, and a poll would have to spin.
            var barrier = new FakeBarrier();
            int drains = 0;
            var pool = new GpuRetireQueue(() => drains++, barrier);
            var a = new FakeResource();
            var b = new FakeResource();
            pool.Retire(a);
            pool.BeginFrame();      // a is sealed behind an unsignaled fence
            pool.Retire(b);         // b is not even sealed

            pool.FlushAll();

            Assert.Equal(1, drains);
            Assert.Equal(1, a.DisposeCount);
            Assert.Equal(1, b.DisposeCount);
            Assert.Equal(0, pool.PendingCount);
            Assert.Equal(0, pool.SealedBatchCount);
            Assert.Equal(1, barrier.Releases);   // the in-flight fence goes back to the barrier, not to the floor
        }

        // ---- the frame-count-only path (no fence, no drain) ----

        [Fact]
        public void FrameCountOnly_frees_on_the_frame_count_with_no_drain_at_all()
        {
            var queue = new GpuRetireQueue(() => Assert.Fail("FrameCountOnly must never drain on the frame path"),
                barrier: null, fallback: GpuRetireFallback.FrameCountOnly, frameDelay: 4);
            var res = new FakeResource();
            queue.Retire(res);

            for (int i = 0; i < 3; i++) queue.BeginFrame();
            Assert.Equal(0, res.DisposeCount);   // still inside the deferral window

            queue.BeginFrame();

            Assert.Equal(1, res.DisposeCount);   // freed on the count alone, and the drain above never ran
            Assert.Equal(0, queue.PendingCount);
        }

        [Fact]
        public void FrameCountOnly_still_frees_batches_oldest_first()
        {
            var queue = new GpuRetireQueue(() => Assert.Fail("FrameCountOnly must never drain on the frame path"),
                barrier: null, fallback: GpuRetireFallback.FrameCountOnly, frameDelay: 2);
            var older = new FakeResource();
            var younger = new FakeResource();

            queue.Retire(older);
            queue.BeginFrame();
            queue.Retire(younger);
            queue.BeginFrame();          // older is ripe here, younger is not

            Assert.Equal(1, older.DisposeCount);
            Assert.Equal(0, younger.DisposeCount);

            queue.BeginFrame();
            Assert.Equal(1, younger.DisposeCount);
        }

        [Fact]
        public void FrameCountOnly_still_drains_once_at_teardown()
        {
            // The one WaitForIdle this policy keeps: a tail that would otherwise be destroyed with no argument at
            // all, at a moment where the stall costs nothing.
            int drains = 0;
            var queue = new GpuRetireQueue(() => drains++, barrier: null,
                fallback: GpuRetireFallback.FrameCountOnly, frameDelay: 1000);
            var res = new FakeResource();
            queue.Retire(res);

            queue.Dispose();

            Assert.Equal(1, drains);
            Assert.Equal(1, res.DisposeCount);
        }

        [Fact]
        public void FlushAll_inside_an_open_recording_is_refused()
        {
            // FlushAll opens with a WaitForIdle, and a drain waits out work that was SUBMITTED. An open recording
            // has not been, so the drain says nothing about the draws in that list and the disposals behind it are
            // a use-after-free with a drain in front of it. The teardown path therefore refuses mid-recording by
            // name, the way the seam refuses a nested recording (#424), rather than reading as safe in review.
            var device = new SpyGpuDevice(new FakeGpuDevice());
            GpuRetireQueue queue = GpuRetireQueue.CreateFrameCounted(device, 4);
            var res = new FakeResource();
            queue.Retire(res);
            GpuRetireQueue empty = GpuRetireQueue.CreateFrameCounted(device, 4);

            using (GpuRecording.Open(device, device.Factory.CreateCommandList(), "the window's frame list"))
            {
                var ex = Assert.Throws<GpuDrainDuringRecordingException>(() => queue.FlushAll());
                Assert.Equal("the window's frame list", ex.Owner);
                // And it does not depend on there being anything to free: a call that happens to find the queue
                // empty is the same mistake, and a guard that only fires sometimes is worse than one that always does.
                Assert.Throws<GpuDrainDuringRecordingException>(() => empty.FlushAll());
            }

            Assert.Equal(0, device.WaitForIdleCalls);   // refused BEFORE the drain, not after it
            Assert.Equal(0, res.DisposeCount);
            Assert.Equal(1, queue.PendingCount);
        }

        [Fact]
        public void FlushAll_outside_a_recording_drains_and_frees()
        {
            // The other half of the guard: with nothing recording, teardown is exactly what it always was.
            var device = new SpyGpuDevice(new FakeGpuDevice());
            GpuRetireQueue queue = GpuRetireQueue.CreateFrameCounted(device, 4);
            var res = new FakeResource();
            queue.Retire(res);

            queue.FlushAll();

            Assert.Equal(1, device.WaitForIdleCalls);
            Assert.Equal(1, res.DisposeCount);
            Assert.Equal(0, queue.PendingCount);
        }

        [Fact]
        public void Dispose_inherits_the_refusal_and_frees_nothing()
        {
            // Dispose is FlushAll plus the barrier, so tearing a renderer down from inside the frame's own
            // recording is the same use-after-free, and is refused whole rather than done half way.
            var device = new SpyGpuDevice(new FakeGpuDevice());
            GpuRetireQueue queue = GpuRetireQueue.CreateFrameCounted(device, 4);
            var res = new FakeResource();
            queue.Retire(res);

            using (GpuRecording.Open(device, device.Factory.CreateCommandList(), "an offscreen 2D capture"))
                Assert.Throws<GpuDrainDuringRecordingException>(queue.Dispose);

            Assert.Equal(0, res.DisposeCount);

            queue.Dispose();   // outside the recording it is the ordinary teardown
            Assert.Equal(1, device.WaitForIdleCalls);
            Assert.Equal(1, res.DisposeCount);
        }

        [Fact]
        public void Dispose_flushes_the_tail_and_frees_the_barrier()
        {
            var barrier = new FakeBarrier();
            int drains = 0;
            var pool = new GpuRetireQueue(() => drains++, barrier);
            var res = new FakeResource();
            pool.Retire(res);
            pool.BeginFrame();

            pool.Dispose();

            Assert.Equal(1, drains);
            Assert.Equal(1, res.DisposeCount);
            Assert.Equal(1, barrier.Disposes);
        }
    }
}
