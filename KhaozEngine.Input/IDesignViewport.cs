using Microsoft.Xna.Framework;

namespace KhaozEngine.Input;

/// <summary>
/// The design-space viewport a screen draws into: its current size, the virtual-to-screen
/// <see cref="Scale"/>, and the <see cref="ScaleMatrix"/> to pass to <c>SpriteBatch.Begin</c>.
/// <see cref="VirtualResolution"/> is the concrete implementation; the interface exists so
/// consumers can render and lay out against a small, fakeable seam (headless tests supply a
/// fixed-size stub) instead of wrapping <see cref="VirtualResolution"/> themselves.
/// </summary>
public interface IDesignViewport
{
    /// <summary>Current virtual width (design-space pixels).</summary>
    int Width { get; }

    /// <summary>Current virtual height (design-space pixels).</summary>
    int Height { get; }

    /// <summary>Scale factor from virtual coordinates to screen pixels.</summary>
    float Scale { get; }

    /// <summary>Transform to pass to <c>SpriteBatch.Begin</c> to map virtual coordinates to screen pixels.</summary>
    Matrix ScaleMatrix { get; }
}
