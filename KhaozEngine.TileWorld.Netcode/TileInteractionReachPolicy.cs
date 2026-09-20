using System;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>Optional additions to the default cardinal, non-overlapping interaction reach rule.</summary>
[Flags]
public enum TileInteractionReachPolicy : byte
{
    /// <summary>Cardinal perimeter tiles only, with overlap denied. The existing interaction behavior.</summary>
    Default = 0,

    /// <summary>Add the four corner neighbours when the collision map permits the diagonal without corner cutting.</summary>
    IncludeDiagonals = 1,

    /// <summary>Add valid standing anchors whose actor footprint overlaps the target footprint.</summary>
    IncludeOverlap = 2,
}
