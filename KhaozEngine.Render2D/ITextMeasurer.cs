using System;
using System.Numerics;

namespace KhaozEngine.Render2D
{
    /// <summary>
    /// Text measurement seam: the layout math in <see cref="TextLayout"/> depends only on this, so it can
    /// be unit-tested headlessly with a fake measurer (no GPU device / real font). <see cref="SpriteFont"/>
    /// implements it.
    /// </summary>
    public interface ITextMeasurer
    {
        /// <summary>Recommended line advance (pixels) at the baked size.</summary>
        float LineHeight { get; }

        /// <summary>Width/height (pixels) the string occupies at the baked size.</summary>
        Vector2 Measure(string text);

        /// <summary>
        /// As <see cref="Measure(string)"/>, but from a span - lets a hot layout path (e.g.
        /// <see cref="TextLayout.Wrap"/> measuring a candidate line while word-wrapping) measure without first
        /// allocating a string just to throw it away when the candidate does not fit. The default
        /// implementation allocates a string and forwards to <see cref="Measure(string)"/>, so any existing
        /// implementer keeps compiling and behaving identically without overriding this; <see cref="SpriteFont"/>
        /// overrides it with a genuinely allocation-free span walk.
        /// </summary>
        Vector2 Measure(ReadOnlySpan<char> text) => Measure(text.ToString());
    }
}
