using System;

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
    /// (it owns the currently-baked <see cref="SpriteFont"/>).
    /// </para>
    /// </summary>
    public sealed class DpiFont : IDisposable
    {
        readonly DpiRebakeCache<SpriteFont> _cache;

        /// <summary>The logical (point) pixel height the font is authored at; the DPI scale multiplies it at bake time.</summary>
        public float PixelHeight { get; }

        internal DpiFont(float pixelHeight, Func<float, SpriteFont> bake)
        {
            PixelHeight = pixelHeight;
            _cache = new DpiRebakeCache<SpriteFont>(bake, f => f.Dispose());
        }

        /// <summary>
        /// The <see cref="SpriteFont"/> baked for <paramref name="dpiScale"/> (device px per logical point). Re-bakes
        /// only when the scale changes beyond a small epsilon; otherwise returns the cached font. Scales below 1 are
        /// clamped (the atlas is never baked below the logical density). Draw the result 1:1 in a point-space batch.
        /// </summary>
        public SpriteFont For(float dpiScale) => _cache.For(dpiScale < 1f ? 1f : dpiScale);

        /// <summary>The DPI scale the currently-cached font was baked at (0 before the first <see cref="For"/>).</summary>
        public float BakedDpiScale => _cache.Key;

        /// <summary>How many times the atlas has been (re)baked over this font's life. For tests / diagnostics.</summary>
        public int BakeCount => _cache.Count;

        public void Dispose() => _cache.Dispose();
    }

    /// <summary>
    /// Caches one value keyed by a float scale, re-invoking the factory (and disposing the previous value) only when
    /// the requested scale moves beyond <c>epsilon</c> from the baked one. The DPI-keyed re-bake logic behind
    /// <see cref="DpiFont"/>, kept device-free so the "re-bake only on change" behaviour is unit-testable headlessly.
    /// </summary>
    internal sealed class DpiRebakeCache<T> : IDisposable where T : class
    {
        readonly Func<float, T> _make;
        readonly Action<T> _dispose;
        readonly float _epsilon;
        T? _current;

        /// <summary>The scale the current value was made for (0 before the first <see cref="For"/>).</summary>
        public float Key { get; private set; }
        /// <summary>How many times the factory has run (for tests).</summary>
        public int Count { get; private set; }

        public DpiRebakeCache(Func<float, T> make, Action<T> dispose, float epsilon = 1e-3f)
        {
            _make = make ?? throw new ArgumentNullException(nameof(make));
            _dispose = dispose ?? throw new ArgumentNullException(nameof(dispose));
            _epsilon = epsilon;
        }

        public T For(float scale)
        {
            if (_current is null || MathF.Abs(scale - Key) > _epsilon)
            {
                if (_current is not null) _dispose(_current);
                _current = _make(scale);
                Key = scale;
                Count++;
            }
            return _current;
        }

        public void Dispose()
        {
            if (_current is not null) { _dispose(_current); _current = null; }
        }
    }
}
