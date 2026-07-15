using System.Collections.Generic;
using KhaozEngine.Render2D;
using Xunit;

namespace KhaozEngine.Tests.Render2D
{
    /// <summary>
    /// Headless coverage for the re-bake decision behind <see cref="DpiFont"/> (the device-free
    /// <see cref="DpiRebakeCache{T}"/>): the atlas must be baked once and reused while the DPI scale holds, and
    /// re-baked (disposing the old value) only when the scale actually moves. This is what makes "bake at
    /// points*dpiScale, re-bake on DPI change only" true without a GPU in the test.
    /// </summary>
    public sealed class DpiFontTests
    {
        [Fact]
        public void Bakes_once_and_reuses_while_the_scale_holds()
        {
            int bakes = 0;
            var cache = new DpiRebakeCache<object>(_ => { bakes++; return new object(); }, _ => { });

            object a = cache.For(2f);
            object b = cache.For(2f);
            object c = cache.For(2.0005f);   // within epsilon -> same bake

            Assert.Same(a, b);
            Assert.Same(a, c);
            Assert.Equal(1, bakes);
            Assert.Equal(1, cache.Count);
        }

        [Fact]
        public void Rebakes_and_disposes_the_old_font_when_the_scale_changes()
        {
            int bakes = 0;
            var disposed = new List<object>();
            var cache = new DpiRebakeCache<object>(_ => { bakes++; return new object(); }, disposed.Add);

            object a = cache.For(1f);
            object b = cache.For(2f);        // Retina: past epsilon -> re-bake

            Assert.NotSame(a, b);
            Assert.Equal(2, bakes);
            Assert.Single(disposed);
            Assert.Same(a, disposed[0]);     // the superseded font is released, not leaked
            Assert.Equal(2f, cache.Key, 3);
        }

        [Fact]
        public void Dispose_releases_the_currently_cached_font_exactly_once()
        {
            var disposed = new List<object>();
            var cache = new DpiRebakeCache<object>(_ => new object(), disposed.Add);

            cache.For(2f);
            cache.Dispose();
            cache.Dispose();                 // idempotent: no double-dispose

            Assert.Single(disposed);
        }

        [Fact]
        public void Multi_slot_keeps_several_scales_and_does_not_thrash_on_alternating_requests()
        {
            // The boot screen draws a title and a smaller label from one face, so it asks the same DpiFont for two
            // different device scales every frame. With enough slots each is baked once and reused - not re-baked as
            // a single slot would (baking twice per frame).
            int bakes = 0;
            var disposed = new List<object>();
            var cache = new DpiRebakeCache<object>(_ => { bakes++; return new object(); }, disposed.Add, capacity: 4);

            object title = cache.For(1.7f);   // 0.85 * dpiScale 2
            object label = cache.For(1.2f);   // 0.60 * dpiScale 2
            for (int frame = 0; frame < 10; frame++)
            {
                Assert.Same(title, cache.For(1.7f));
                Assert.Same(label, cache.For(1.2f));
            }

            Assert.Equal(2, bakes);           // one bake per distinct scale, none after warm-up
            Assert.Equal(2, cache.LiveCount);
            Assert.Empty(disposed);           // both stay live, nothing evicted
        }

        [Fact]
        public void Multi_slot_evicts_the_least_recently_used_past_capacity()
        {
            var made = new Dictionary<float, object>();
            var disposed = new List<object>();
            var cache = new DpiRebakeCache<object>(s => { var o = new object(); made[s] = o; return o; }, disposed.Add, capacity: 2);

            cache.For(1f);
            cache.For(2f);
            cache.For(1f);                    // touch 1 so 2 is now the least-recently used
            cache.For(3f);                    // over capacity -> evict the LRU (2)

            Assert.Equal(2, cache.LiveCount);
            Assert.Single(disposed);
            Assert.Same(made[2f], disposed[0]);
            Assert.Equal(3, cache.Count);     // three distinct bakes over the cache's life
        }
    }
}
