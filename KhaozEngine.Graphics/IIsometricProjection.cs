using Microsoft.Xna.Framework;

namespace KhaozEngine.Graphics;

/// <summary>
/// Render-only isometric projection seam: world &lt;-&gt; screen mapping for a diamond tile grid.
/// Implemented by <see cref="IsometricProjection"/>; consumers depend on this so they can swap the
/// projection or substitute a fake/stub in headless screen tests (mirrors the <c>IDesignViewport</c>
/// seam in <c>KhaozEngine.Input</c>). Holds no grid/tiles/pathfinding - purely the coordinate map.
/// </summary>
public interface IIsometricProjection
{
    /// <summary>Tile footprint width in pixels (the diamond's full horizontal span).</summary>
    float TileWidth { get; }

    /// <summary>Tile footprint height in pixels (the diamond's full vertical span).</summary>
    float TileHeight { get; }

    /// <summary>Screen pixels a point rises per unit of <c>z</c>.</summary>
    float HeightScale { get; }

    /// <summary>Projects a world point (with height <paramref name="z"/>) to screen space.</summary>
    Vector2 WorldToScreen(float wx, float wy, float z = 0f);

    /// <summary>Vector overload of <see cref="WorldToScreen(float, float, float)"/>.</summary>
    Vector2 WorldToScreen(Vector2 world, float z = 0f);

    /// <summary>Inverts the projection on the ground plane (<c>z = 0</c>) for picking.</summary>
    Vector2 ScreenToGround(Vector2 screen);

    /// <summary>
    /// Inverts the projection on the horizontal plane at height <paramref name="z"/>: the continuous
    /// world point that, drawn at that height, lands under <paramref name="screen"/>.
    /// </summary>
    Vector2 ScreenToWorld(Vector2 screen, float z);
}
