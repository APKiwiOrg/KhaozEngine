using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace KhaozEngine.Graphics;

/// <summary>
/// Game-agnostic 2D matrix camera. <see cref="Position"/> is the world point shown at the
/// center of the viewport; <see cref="Zoom"/> and <see cref="Rotation"/> scale and roll the
/// view about that point. The core transform methods take an explicit <see cref="Viewport"/>
/// so the math requires no <c>GraphicsDevice</c> and is fully headless. Convenience no-arg
/// overloads use the settable <see cref="Viewport"/> property.
/// </summary>
public sealed class Camera2D
{
    /// <summary>World point shown at the center of the viewport. Publicly settable so a
    /// follow-cam can drive it each frame.</summary>
    public Vector2 Position { get; set; } = Vector2.Zero;

    /// <summary>Uniform scale; greater than 1 zooms in. Must be &gt; 0: a value &lt;= 0 makes
    /// the view matrix singular, so <see cref="ScreenToWorld(Vector2, Viewport)"/> (which
    /// inverts it) returns NaN.</summary>
    public float Zoom { get; set; } = 1f;

    /// <summary>Camera roll in radians, counter-clockwise.</summary>
    public float Rotation { get; set; }

    /// <summary>Viewport used by the no-arg overloads. Set once and refresh on resize
    /// (e.g. <c>Window.ClientSizeChanged</c>). The per-call overloads ignore this.</summary>
    public Viewport Viewport { get; set; }
}
