using System;
using System.Collections.Generic;

namespace KhaozEngine.Render2D
{
    /// <summary>
    /// A logical-size font that stays crisp under HiDPI by baking its atlas at the live device-pixel scale and
    /// re-baking only when that scale changes. Author UI at a logical <c>pixelHeight</c> (points); each frame call
    /// <see cref="For"/> with the current DPI scale (device px per logical point, e.g. <c>Frame.DpiScale</c>) and
    /// draw the returned <see cref="SpriteFont"/> 1:1 in a point-space batch - the glyph atlas is baked at
    /// <c>pixelHeight * dpiScale</c> so it maps texel-for-texel onto the framebuffer. Because the DPI scale is
    /// stable for a given display (it changes on a monitor / OS-scale change, not on window resize), the atlas is
    /// re-baked only on those rare changes, not every frame or resize.
    /// <para>
    /// Build one via <c>Render2DSurface.LoadDpiFont(...)</c> or <c>Render2DSnapshot.LoadDpiFont(...)</c>. Dispose it
    /// (it owns the baked <see cref="SpriteFont"/>s).
    /// </para>
    /// <para>
    /// <b>Multiple live scales.</b> Pass <c>cacheSlots</c> &gt; 1 when the SAME face is drawn at several different
    /// effective scales within one pass and each must be texel-exact - e.g. a boot screen that renders a title and a
    /// smaller step label from one font: call <c>For(titleScale * dpiScale)</c> and <c>For(labelScale * dpiScale)</c>
    /// and both atlases stay baked, instead of thrashing a single slot (baking twice every frame). The default of 1
    /// keeps the single-display-scale behaviour (one atlas, re-baked only on a DPI change).
    /// </para>
    /// </summary>
    public sealed class DpiFont : IDisposable
    {
        readonly DpiRebakeCache<SpriteFont> _cache;

        /// <summary>The logical (point) pixel height the font is authored at; the DPI scale multiplies it at bake time.</summary>
        public float PixelHeight { get; }

        internal DpiFont(float pixelHeight, Func<float, SpriteFont> bake, int cacheSlots = 1)
        {
            PixelHeight = pixelHeight;
            _cache = new DpiRebakeCache<SpriteFont>(bake, f => f.Dispose(), cacheSlots);
        }

        /// <summary>
        /// The <see cref="SpriteFont"/> baked for <paramref name="dpiScale"/> (device px per logical point). Re-bakes
        /// only when the scale is not already cached (within a small epsilon). Otherwise returns the cached font.
        /// Scales below 1 are clamped (the atlas is never baked below the logical density). Draw the result 1:1 in a
        /// point-space batch. With <c>cacheSlots</c> &gt; 1 several scales stay baked at once (LRU eviction).
        /// </summary>
        public SpriteFont For(float dpiScale) => _cache.For(dpiScale < 1f ? 1f : dpiScale);

        /// <summary>The scale of the MOST RECENTLY requested cached font (0 before the first <see cref="For"/>).</summary>
        public float BakedDpiScale => _cache.Key;

        /// <summary>How many times the atlas has been (re)baked over this font's life. For tests / diagnostics.</summary>
        public int BakeCount => _cache.Count;

        /// <summary>How many distinct scales are baked and live right now (&lt;= <c>cacheSlots</c>). For tests / diagnostics.</summary>
        public int LiveCount => _cache.LiveCount;

        public void Dispose() => _cache.Dispose();
    }

    /// <summary>
    /// Caches values keyed by a float scale, re-invoking the factory only when the requested scale is not already
    /// held (within <c>epsilon</c>). Keeps up to <c>capacity</c> entries (default 1) and evicts the least-recently
    /// used, disposing it. The DPI-keyed re-bake logic behind <see cref="DpiFont"/>, kept device-free so the "re-bake
    /// only on change" behaviour is unit-testable headlessly. Capacity 1 is a single slot: any scale change re-bakes
    /// and disposes the prior value (the UI single-display-scale contract).
    /// </summary>
    internal sealed class DpiRebakeCache<T> : IDisposable where T : class
    {
        readonly Func<float, T> _make;
        readonly Action<T> _dispose;
        readonly float _epsilon;
        readonly int _capacity;
        // MRU-ordered (front = most recently used). Small (capacity is a handful), so a linear scan is cheapest.
        readonly List<(float Key, T Value)> _entries = new();

        /// <summary>The scale of the most-recently-used entry (0 before the first <see cref="For"/>).</summary>
        public float Key { get; private set; }
        /// <summary>How many times the factory has run (for tests).</summary>
        public int Count { get; private set; }
        /// <summary>How many entries are live right now (for tests).</summary>
        public int LiveCount => _entries.Count;

        public DpiRebakeCache(Func<float, T> make, Action<T> dispose, int capacity = 1, float epsilon = 1e-3f)
        {
            _make = make ?? throw new ArgumentNullException(nameof(make));
            _dispose = dispose ?? throw new ArgumentNullException(nameof(dispose));
            _capacity = Math.Max(1, capacity);
            _epsilon = epsilon;
        }

        public T For(float scale)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                if (MathF.Abs(scale - _entries[i].Key) <= _epsilon)
                {
                    (float, T) hit = _entries[i];
                    if (i != 0) { _entries.RemoveAt(i); _entries.Insert(0, hit); }   // promote to MRU
                    Key = _entries[0].Key;
                    return _entries[0].Value;
                }
            }

            T value = _make(scale);
            Count++;
            _entries.Insert(0, (scale, value));
            Key = scale;
            while (_entries.Count > _capacity)
            {
                int last = _entries.Count - 1;
                _dispose(_entries[last].Value);
                _entries.RemoveAt(last);
            }
            return value;
        }

        public void Dispose()
        {
            foreach ((float _, T value) in _entries) _dispose(value);
            _entries.Clear();
        }
    }
}
