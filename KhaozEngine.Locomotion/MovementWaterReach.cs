using System;

namespace KhaozEngine.Locomotion;

/// <summary>What a water boundary reach certificate says about one capsule path.</summary>
public enum MovementWaterReachKind
{
    /// <summary>No certificate. The boundary question takes the full coverage trace.</summary>
    Declined = 0,
    /// <summary>No water column lies within the path's reach, so its trace can report no contact.</summary>
    Empty = 1,
    /// <summary>Every column within the path's reach has the same free surface, <see cref="MovementWaterReach.SurfaceY"/>.</summary>
    OneFreeSurface = 2,
}

/// <summary>A capture certificate for the water boundary question of one capsule path. The default value declines.
/// A producer reports <see cref="SurfaceY"/> in absolute metres. The ordinary pin converts it to the frame as it
/// converts every trace contact's nominal surface.</summary>
public readonly record struct MovementWaterReach
{
    public MovementWaterReachKind Kind { get; }
    /// <summary>The surface every counted column shares, when <see cref="Kind"/> is
    /// <see cref="MovementWaterReachKind.OneFreeSurface"/>. Zero otherwise.</summary>
    public float SurfaceY { get; }

    MovementWaterReach(MovementWaterReachKind kind, float surfaceY)
    {
        Kind = kind;
        SurfaceY = surfaceY;
    }

    public static MovementWaterReach Declined => default;
    public static MovementWaterReach Empty => new(MovementWaterReachKind.Empty, 0);

    /// <exception cref="ArgumentOutOfRangeException"><paramref name="surfaceY"/> is not finite.</exception>
    public static MovementWaterReach FreeSurface(float surfaceY)
    {
        if (!float.IsFinite(surfaceY)) throw new ArgumentOutOfRangeException(nameof(surfaceY));
        return new(MovementWaterReachKind.OneFreeSurface, surfaceY);
    }
}
