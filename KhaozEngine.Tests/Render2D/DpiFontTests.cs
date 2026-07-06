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
    }
}
