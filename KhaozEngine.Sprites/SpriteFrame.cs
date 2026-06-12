using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace KhaozEngine.Sprites;

/// <summary>
/// One drawable animation frame: a <see cref="Texture"/> and the <see cref="Source"/> rectangle
/// within it. Frames carry their own texture so a single animation can span a packed sheet (all
/// frames share one texture, different source rects) or a set of loose per-frame textures (each
/// frame its own texture). The texture is only dereferenced when drawing, so timing-only logic and
/// headless tests may leave it null.
/// </summary>
public readonly struct SpriteFrame
{
    /// <summary>The texture this frame is drawn from. Null is allowed for non-rendering scenarios.</summary>
    public Texture2D? Texture { get; }

    /// <summary>The source rectangle within <see cref="Texture"/>.</summary>
    public Rectangle Source { get; }

    /// <summary>Creates a frame from a texture and a source rectangle within it.</summary>
    public SpriteFrame(Texture2D? texture, Rectangle source)
    {
        Texture = texture;
        Source = source;
    }

    /// <summary>Creates a frame covering an entire texture.</summary>
    public static SpriteFrame Whole(Texture2D texture) =>
        new(texture, new Rectangle(0, 0, texture.Width, texture.Height));
}
