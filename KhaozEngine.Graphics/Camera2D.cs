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

    /// <summary>
    /// Builds the view (world-to-screen) transform for the given viewport:
    /// translate so <see cref="Position"/> is at the origin, apply <see cref="Rotation"/>,
    /// scale by <see cref="Zoom"/>, then translate to the viewport center. The world thus
    /// rotates and scales about <see cref="Position"/>, which lands at screen center.
    /// </summary>
    public Matrix GetViewMatrix(Viewport viewport)
    {
        return Matrix.CreateTranslation(-Position.X, -Position.Y, 0f)
            * Matrix.CreateRotationZ(Rotation)
            * Matrix.CreateScale(Zoom, Zoom, 1f)
            * Matrix.CreateTranslation(viewport.Width * 0.5f, viewport.Height * 0.5f, 0f);
    }

    /// <summary>Transforms a world position to screen space using the given viewport.</summary>
    public Vector2 WorldToScreen(Vector2 world, Viewport viewport)
    {
        return Vector2.Transform(world, GetViewMatrix(viewport));
    }

    /// <summary>Transforms a screen position back to world space using the given viewport.
    /// Requires <see cref="Zoom"/> &gt; 0 (otherwise the matrix is singular and the result is
    /// NaN).</summary>
    public Vector2 ScreenToWorld(Vector2 screen, Viewport viewport)
    {
        Matrix inverseView = Matrix.Invert(GetViewMatrix(viewport));
        return Vector2.Transform(screen, inverseView);
    }

    /// <summary>View matrix using the stored <see cref="Viewport"/> property.</summary>
    public Matrix GetViewMatrix() => GetViewMatrix(Viewport);

    /// <summary>World-to-screen using the stored <see cref="Viewport"/> property.</summary>
    public Vector2 WorldToScreen(Vector2 world) => WorldToScreen(world, Viewport);

    /// <summary>Screen-to-world using the stored <see cref="Viewport"/> property.</summary>
    public Vector2 ScreenToWorld(Vector2 screen) => ScreenToWorld(screen, Viewport);

    /// <summary>
    /// Returns <paramref name="desired"/> clamped so the visible world rectangle
    /// (viewport size divided by <see cref="Zoom"/>) stays inside <paramref name="worldBounds"/>.
    /// On an axis where the world is smaller than the view, the result is centered on that
    /// axis. Does not mutate <see cref="Position"/> — the caller assigns the result if wanted.
    /// </summary>
    /// <remarks>
    /// Uses the axis-aligned visible rect and ignores <see cref="Rotation"/>: exact when
    /// <see cref="Rotation"/> is 0 (the typical platformer/scroller case); approximate with a
    /// rotated camera, where the true visible area is a rotated quad.
    /// </remarks>
    public Vector2 ClampPosition(Vector2 desired, Rectangle worldBounds, Viewport viewport)
    {
        float halfW = viewport.Width / (2f * Zoom);
        float halfH = viewport.Height / (2f * Zoom);

        float x = worldBounds.Width >= 2f * halfW
            ? MathHelper.Clamp(desired.X, worldBounds.Left + halfW, worldBounds.Right - halfW)
            : worldBounds.Left + worldBounds.Width / 2f;

        float y = worldBounds.Height >= 2f * halfH
            ? MathHelper.Clamp(desired.Y, worldBounds.Top + halfH, worldBounds.Bottom - halfH)
            : worldBounds.Top + worldBounds.Height / 2f;

        return new Vector2(x, y);
    }
}
