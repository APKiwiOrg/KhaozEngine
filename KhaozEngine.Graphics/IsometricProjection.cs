using System;
using Microsoft.Xna.Framework;

namespace KhaozEngine.Graphics;

/// <summary>
/// Render-only isometric projection for a 2:1-style diamond tile grid. Maps continuous world
/// coordinates (a consumer's own grid, gameplay-agnostic) to screen space at draw time and back
/// again for picking. Holds no grid, no tiles, no pathfinding: the consumer keeps its world model
/// and projects on the way to the screen.
/// </summary>
/// <remarks>
/// The tile footprint is configurable; the default 64x32 is the classic 2:1 diamond. <c>z</c> is a
/// real input on <see cref="WorldToScreen(float, float, float)"/> even though v1 callers pass 0:
/// it lifts a point up the screen by <c>z * HeightScale</c>, the seam for terrain height later.
/// <see cref="ScreenToGround"/> inverts the projection at <c>z = 0</c>, returning the continuous
/// world point under the cursor on the ground plane.
/// </remarks>
public sealed class IsometricProjection
{
    /// <summary>Tile footprint width in pixels (the diamond's full horizontal span).</summary>
    public float TileWidth { get; }

    /// <summary>Tile footprint height in pixels (the diamond's full vertical span). Half the
    /// width for the default 2:1 footprint.</summary>
    public float TileHeight { get; }

    /// <summary>Screen pixels a point rises per unit of <c>z</c>. Defaults to <see cref="TileHeight"/>
    /// (one tile-height per z-level) when not specified.</summary>
    public float HeightScale { get; }

    /// <summary>
    /// Creates a projection for the given tile footprint. Defaults to a 64x32 (2:1) diamond.
    /// </summary>
    /// <param name="tileWidth">Tile footprint width in pixels. Must be &gt; 0.</param>
    /// <param name="tileHeight">Tile footprint height in pixels. Must be &gt; 0.</param>
    /// <param name="heightScale">Screen pixels per unit of <c>z</c>. Defaults to
    /// <paramref name="tileHeight"/> when null. Must be &gt; 0 if specified.</param>
    public IsometricProjection(float tileWidth = 64f, float tileHeight = 32f, float? heightScale = null)
    {
        if (tileWidth <= 0f) throw new ArgumentOutOfRangeException(nameof(tileWidth), tileWidth, "Tile width must be positive.");
        if (tileHeight <= 0f) throw new ArgumentOutOfRangeException(nameof(tileHeight), tileHeight, "Tile height must be positive.");
        float hs = heightScale ?? tileHeight;
        if (hs <= 0f) throw new ArgumentOutOfRangeException(nameof(heightScale), hs, "Height scale must be positive.");

        TileWidth = tileWidth;
        TileHeight = tileHeight;
        HeightScale = hs;
    }

    /// <summary>
    /// Projects a world point to screen space:
    /// <c>sx = (wx - wy) * TileWidth / 2</c>, <c>sy = (wx + wy) * TileHeight / 2 - z * HeightScale</c>.
    /// The result is in projection-local pixels (origin at world <c>(0,0,0)</c>); offset it by your
    /// camera/draw origin. Increasing <c>z</c> moves the point up the screen.
    /// </summary>
    public Vector2 WorldToScreen(float wx, float wy, float z = 0f)
    {
        float sx = (wx - wy) * TileWidth * 0.5f;
        float sy = (wx + wy) * TileHeight * 0.5f - z * HeightScale;
        return new Vector2(sx, sy);
    }

    /// <summary>Vector overload of <see cref="WorldToScreen(float, float, float)"/>.</summary>
    public Vector2 WorldToScreen(Vector2 world, float z = 0f) => WorldToScreen(world.X, world.Y, z);

    /// <summary>
    /// Inverts the projection on the ground plane (<c>z = 0</c>), returning the continuous world
    /// point under <paramref name="screen"/>. Continuous (not rounded) so callers can floor to a
    /// tile or keep the sub-tile fraction for picking. Pass screen coordinates already relative to
    /// the same origin <see cref="WorldToScreen(float, float, float)"/> produced.
    /// </summary>
    public Vector2 ScreenToGround(Vector2 screen)
    {
        // sx = (wx - wy) * TileWidth/2  =>  a := sx / TileWidth  = (wx - wy) / 2
        // sy = (wx + wy) * TileHeight/2 =>  b := sy / TileHeight = (wx + wy) / 2
        // wx = a + b, wy = b - a
        float a = screen.X / TileWidth;
        float b = screen.Y / TileHeight;
        return new Vector2(b + a, b - a);
    }
}
