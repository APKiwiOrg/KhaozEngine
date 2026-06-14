namespace KhaozEngine.Sprites;

/// <summary>
/// How a sprite's draw position maps to a point in the frame, when no explicit origin is given.
/// </summary>
public enum SpriteAnchor
{
    /// <summary>Position is the centre of the frame (the default, unchanged orthographic behaviour).</summary>
    Center = 0,

    /// <summary>
    /// Position is the bottom-centre of the frame, i.e. the middle of the footprint's front edge.
    /// A tall isometric sprite drawn at its tile's (z-lifted) screen point then stands on the tile
    /// instead of being bisected by it.
    /// </summary>
    FootprintBottomCenter = 1,
}
