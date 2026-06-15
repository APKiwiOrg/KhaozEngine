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
    }
}
