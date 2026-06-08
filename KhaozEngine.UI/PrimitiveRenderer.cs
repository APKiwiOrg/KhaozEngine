using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace KhaozEngine.UI;

/// <summary>
/// Provides primitive shape rendering (rectangles, lines, circles) using a
/// 1x1 white pixel texture. Used as the primary rendering approach until
/// textures/sprites are added later.
/// Subscribes to GraphicsDevice.DeviceReset to recreate the pixel texture
/// if the device is lost (window resize, GPU switch, background/foreground).
/// </summary>
public sealed class PrimitiveRenderer
{
    private readonly GraphicsDevice _graphicsDevice;
    private Texture2D _pixel;

    /// <summary>
    /// Creates a new PrimitiveRenderer and initializes the 1x1 pixel texture.
    /// </summary>
    /// <param name="graphicsDevice">The graphics device to create the texture on.</param>
    public PrimitiveRenderer(GraphicsDevice graphicsDevice)
    {
        _graphicsDevice = graphicsDevice;
        _pixel = CreatePixel(graphicsDevice);
        graphicsDevice.DeviceReset += OnDeviceReset;
    }

    private void OnDeviceReset(object? sender, EventArgs e)
    {
        if (_pixel.IsDisposed)
            _pixel = CreatePixel(_graphicsDevice);
    }

    private static Texture2D CreatePixel(GraphicsDevice gd)
    {
        var pixel = new Texture2D(gd, 1, 1);
        pixel.SetData([Color.White]);
        return pixel;
    }

    /// <summary>
    /// Draws a filled rectangle.
    /// </summary>
    /// <param name="spriteBatch">The active SpriteBatch (must be within Begin/End).</param>
    /// <param name="bounds">The rectangle bounds in virtual coordinates.</param>
    /// <param name="color">Fill color.</param>
    public void DrawFilledRect(SpriteBatch spriteBatch, Rectangle bounds, Color color)
    {
        spriteBatch.Draw(_pixel, bounds, color);
    }

    /// <summary>
    /// Draws a filled rectangle with a float-based bounds for sub-pixel positioning.
    /// </summary>
    /// <param name="spriteBatch">The active SpriteBatch.</param>
    /// <param name="position">Top-left corner position.</param>
    /// <param name="size">Width and height.</param>
    /// <param name="color">Fill color.</param>
    public void DrawFilledRect(SpriteBatch spriteBatch, Vector2 position, Vector2 size, Color color)
    {
        spriteBatch.Draw(_pixel, new Rectangle(
            (int)position.X, (int)position.Y,
            (int)size.X, (int)size.Y), color);
    }

    /// <summary>
    /// Draws a rectangle outline (border only, no fill).
    /// </summary>
    /// <param name="spriteBatch">The active SpriteBatch.</param>
    /// <param name="bounds">The rectangle bounds.</param>
    /// <param name="color">Border color.</param>
    /// <param name="thickness">Border thickness in pixels.</param>
    public void DrawRect(SpriteBatch spriteBatch, Rectangle bounds, Color color, int thickness = 1)
    {
        // Top
        spriteBatch.Draw(_pixel, new Rectangle(bounds.X, bounds.Y, bounds.Width, thickness), color);
        // Bottom
        spriteBatch.Draw(_pixel, new Rectangle(bounds.X, bounds.Bottom - thickness, bounds.Width, thickness), color);
        // Left
        spriteBatch.Draw(_pixel, new Rectangle(bounds.X, bounds.Y, thickness, bounds.Height), color);
        // Right
        spriteBatch.Draw(_pixel, new Rectangle(bounds.Right - thickness, bounds.Y, thickness, bounds.Height), color);
    }

    /// <summary>
    /// Draws a line between two points.
    /// </summary>
    /// <param name="spriteBatch">The active SpriteBatch.</param>
    /// <param name="start">Start point.</param>
    /// <param name="end">End point.</param>
    /// <param name="color">Line color.</param>
    /// <param name="thickness">Line thickness in pixels.</param>
    public void DrawLine(SpriteBatch spriteBatch, Vector2 start, Vector2 end, Color color, int thickness = 1)
    {
        Vector2 edge = end - start;
        float angle = MathF.Atan2(edge.Y, edge.X);
        float length = edge.Length();

        spriteBatch.Draw(_pixel,
            new Rectangle((int)start.X, (int)start.Y, (int)length, thickness),
            null, color, angle, Vector2.Zero, SpriteEffects.None, 0f);
    }

    /// <summary>
    /// Draws a circle outline using line segments.
    /// </summary>
    /// <param name="spriteBatch">The active SpriteBatch.</param>
    /// <param name="center">Center point.</param>
    /// <param name="radius">Radius in pixels.</param>
    /// <param name="color">Circle color.</param>
    /// <param name="segments">Number of line segments (higher = smoother).</param>
    /// <param name="thickness">Line thickness.</param>
    public void DrawCircle(SpriteBatch spriteBatch, Vector2 center, float radius, Color color, int segments = 32, int thickness = 1)
    {
        float angleStep = MathHelper.TwoPi / segments;
        Vector2 previous = center + new Vector2(radius, 0);

        for (int i = 1; i <= segments; i++)
        {
            float angle = angleStep * i;
            Vector2 current = center + new Vector2(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius);
            DrawLine(spriteBatch, previous, current, color, thickness);
            previous = current;
        }
    }

    /// <summary>
    /// Draws a filled circle using stacked horizontal lines.
    /// </summary>
    /// <param name="spriteBatch">The active SpriteBatch.</param>
    /// <param name="center">Center point.</param>
    /// <param name="radius">Radius in pixels.</param>
    /// <param name="color">Fill color.</param>
    public void DrawFilledCircle(SpriteBatch spriteBatch, Vector2 center, float radius, Color color)
    {
        int intRadius = (int)radius;
        for (int y = -intRadius; y <= intRadius; y++)
        {
            int halfWidth = (int)MathF.Sqrt(radius * radius - y * y);
            spriteBatch.Draw(_pixel, new Rectangle(
                (int)center.X - halfWidth,
                (int)center.Y + y,
                halfWidth * 2, 1), color);
        }
    }

    /// <summary>
    /// Draws a vertical gradient by rendering horizontal strips with linearly
    /// interpolated colors between top and bottom.
    /// </summary>
    /// <param name="spriteBatch">The active SpriteBatch.</param>
    /// <param name="bounds">The area to fill with the gradient.</param>
    /// <param name="topColor">Color at the top of the gradient.</param>
    /// <param name="bottomColor">Color at the bottom of the gradient.</param>
    /// <param name="bands">Number of horizontal strips (higher = smoother). Default 12.</param>
    public void DrawVerticalGradient(SpriteBatch spriteBatch, Rectangle bounds,
        Color topColor, Color bottomColor, int bands = 12)
    {
        if (bands < 1) bands = 1;
        float bandHeight = bounds.Height / (float)bands;

        for (int i = 0; i < bands; i++)
        {
            float t = i / (float)(bands - 1 == 0 ? 1 : bands - 1);
            Color color = Color.Lerp(topColor, bottomColor, t);
            int y = bounds.Y + (int)(i * bandHeight);
            int h = (i == bands - 1) ? bounds.Bottom - y : (int)MathF.Ceiling(bandHeight);
            spriteBatch.Draw(_pixel, new Rectangle(bounds.X, y, bounds.Width, h), color);
        }
    }

    /// <summary>
    /// Draws a progress bar (filled rectangle with background and border).
    /// </summary>
    /// <param name="spriteBatch">The active SpriteBatch.</param>
    /// <param name="bounds">Outer bounds of the progress bar.</param>
    /// <param name="progress">Fill amount (0 to 1).</param>
    /// <param name="fillColor">Color of the filled portion.</param>
    /// <param name="backgroundColor">Color of the unfilled portion.</param>
    /// <param name="borderColor">Color of the border.</param>
    /// <param name="borderThickness">Border thickness.</param>
    public void DrawProgressBar(SpriteBatch spriteBatch, Rectangle bounds, float progress,
        Color fillColor, Color backgroundColor, Color borderColor, int borderThickness = 1)
    {
        // Background
        DrawFilledRect(spriteBatch, bounds, backgroundColor);

        // Fill (geometry capped so short/thin bars keep a visible fill area).
        (Rectangle fill, int effectiveBorder) = ComputeProgressBarLayout(bounds, progress, borderThickness);
        if (fill.Width > 0 && fill.Height > 0)
        {
            DrawFilledRect(spriteBatch, fill, fillColor);
        }

        // Border (skipped when the bar is too small to fit one without hiding the fill).
        if (effectiveBorder > 0)
        {
            DrawRect(spriteBatch, bounds, borderColor, effectiveBorder);
        }
    }

    /// <summary>
    /// Computes the inner fill rectangle and the effective border thickness for a
    /// progress bar. Pure geometry, extracted so it can be unit tested headlessly.
    /// </summary>
    /// <remarks>
    /// The requested border is capped so the inner fill area never collapses below
    /// 1px in either dimension. Without this, a short bar (e.g. a zoomed-out HP bar
    /// only 2px tall with a 1px border) has zero inner height: the fill never draws
    /// and the border alone covers the whole bar, rendering as a solid line in the
    /// border color. Capping the border lets the fill win on tiny bars.
    /// </remarks>
    /// <param name="bounds">Outer bounds of the progress bar.</param>
    /// <param name="progress">Fill amount (clamped to 0..1).</param>
    /// <param name="borderThickness">Requested border thickness.</param>
    /// <returns>The inner fill rectangle and the border thickness actually usable.</returns>
    internal static (Rectangle Fill, int EffectiveBorder) ComputeProgressBarLayout(
        Rectangle bounds, float progress, int borderThickness)
    {
        float clampedProgress = MathHelper.Clamp(progress, 0f, 1f);

        // Largest border that still leaves >= 1px of inner space on the smaller axis.
        int maxBorder = Math.Max(0, (Math.Min(bounds.Width, bounds.Height) - 1) / 2);
        int effectiveBorder = Math.Clamp(borderThickness, 0, maxBorder);

        int innerWidth = bounds.Width - effectiveBorder * 2;
        int innerHeight = bounds.Height - effectiveBorder * 2;
        int fillWidth = (int)(innerWidth * clampedProgress);

        return (
            new Rectangle(bounds.X + effectiveBorder, bounds.Y + effectiveBorder, fillWidth, innerHeight),
            effectiveBorder);
    }
}
