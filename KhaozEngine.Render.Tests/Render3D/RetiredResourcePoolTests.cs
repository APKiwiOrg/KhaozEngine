using System;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    // The deferred-disposal pool behind Scene3D.UnloadMesh. It replaces the per-unload WaitForIdle that made every
    // terrain chunk unload and LOD flip drain the whole device on the frame thread. The contract these tests pin:
    // retiring costs nothing, a resource is only ever destroyed AFTER a drain (the lavapipe use-after-free rule the
    // per-unload drain was protecting), and one drain covers a whole batch instead of one per resource.
    public class RetiredResourcePoolTests
    {
        sealed class FakeResource : IDisposable
        {
            public int DisposeCount;
            public void Dispose() => DisposeCount++;
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
            var pool = new RetiredResourcePool(() => drains++, frameDelay: 2);
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
            var pool = new RetiredResourcePool(() => drains++, frameDelay: 3);
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
            var pool = new RetiredResourcePool(() => drains++, frameDelay: 1);
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
            var pool = new RetiredResourcePool(() => { rec.Drains++; rec.DisposalsAtLastDrain = disposals; }, frameDelay: 1);
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
            var pool = new RetiredResourcePool(() => drains++, frameDelay: 1);

            for (int i = 0; i < 100; i++) pool.BeginFrame();

            Assert.Equal(0, drains);   // no retirements, so a frame boundary costs nothing
        }

        [Fact]
        public void Later_retirements_wait_their_own_delay()
        {
            int drains = 0;
            var pool = new RetiredResourcePool(() => drains++, frameDelay: 2);
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
            var pool = new RetiredResourcePool(() => drains++, frameDelay: 1000);
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
            var pool = new RetiredResourcePool(() => drains++);

            pool.FlushAll();

            Assert.Equal(0, drains);
        }

        [Fact]
        public void Retiring_null_is_ignored()
        {
            int drains = 0;
            var pool = new RetiredResourcePool(() => drains++, frameDelay: 1);

            pool.Retire(null);

            Assert.Equal(0, pool.PendingCount);
            pool.BeginFrame();
            Assert.Equal(0, drains);
        }

        [Fact]
        public void A_frame_delay_below_one_still_defers_past_the_retiring_frame()
        {
            int drains = 0;
            var pool = new RetiredResourcePool(() => drains++, frameDelay: 0);
            var res = new FakeResource();

            pool.Retire(res);
            Assert.Equal(0, res.DisposeCount);   // never destroyed inside the call that retired it

            pool.BeginFrame();

            Assert.Equal(1, res.DisposeCount);
            Assert.Equal(1, drains);
        }
    }
}
