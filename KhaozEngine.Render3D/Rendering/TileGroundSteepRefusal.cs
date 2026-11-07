namespace KhaozEngine.Render3D;

/// <summary>The stable reasons TileGroundSteep input is refused.</summary>
public enum TileGroundSteepRefusal
{
    /// <summary>Refuses a layer using itself as its steep companion.</summary>
    CompanionIsSelf,

    /// <summary>Refuses a companion slot outside the render-layer set.</summary>
    CompanionOutsideSet,

    /// <summary>Refuses a steep band not rising strictly inside a right angle.</summary>
    BandOutsideRightAngle,

    /// <summary>Refuses a steep band whose endpoints pack to the same cosine.</summary>
    BandCosinesNotDistinct,
}
