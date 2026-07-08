namespace KhaozEngine.Locomotion;

/// <summary>
/// The fluid medium a character occupies at one world sample, supplied by the game through the optional medium
/// provider <c>(x, z, feetY) -> MovementMedium</c> threaded into <see cref="CharacterMovement"/> and (via NetWorld)
/// <c>PlayerMoveSimulator</c>, alongside the existing <c>groundHeight</c> delegate. It is a PURE, DETERMINISTIC read
/// of the game's world (a lake plane, a river volume, a swamp zone): the engine never computes water itself, it only
/// asks. The provider MUST return the same medium for the same
/// <c>(x, z, feetY)</c> on BOTH heads - the authoritative server tick and the client's prediction replay - or the
/// two desync (the same both-heads contract the ground delegate carries).
///
/// <para><c>default</c> is dry land: <see cref="InWater"/> false, no wading. A null provider means dry land
/// everywhere, so the movement step is bit-identical to the pre-medium behaviour.</para>
/// </summary>
public readonly record struct MovementMedium
{
    /// <summary>Dry land: not in water, no wade scaling. The value a null provider stands in for at every sample.</summary>
    public static MovementMedium Dry => default;

    /// <summary>Water surface height (world Y). Only meaningful when <see cref="InWater"/> is true; the submersion
    /// depth used by the wade ramp is <c>WaterSurfaceY - feetY</c> (feet = capsule centre minus half-height).</summary>
    public float WaterSurfaceY { get; init; }

    /// <summary>True when this sample sits in water. When false the medium contributes nothing (dry-land behaviour,
    /// <see cref="WadeSpeedScale"/> ignored). Task 2's swim mode reads the submersion depth this flag gates.</summary>
    public bool InWater { get; init; }

    /// <summary>An extra per-sample horizontal-speed multiplier the game composes ON TOP of the depth-driven wade
    /// ramp (a zone dial: a thick swamp &lt; 1 drags, a shallow tide-pool 1 leaves the ramp alone). 1 (the default)
    /// is a no-op. Applied only while <see cref="InWater"/>; the effective wade scale is the ramp value times this.</summary>
    public float WadeSpeedScale { get; init; } = 1f;

    /// <summary>Constructs a medium sample. <paramref name="wadeSpeedScale"/> defaults to 1 (no extra scaling).</summary>
    public MovementMedium(float waterSurfaceY, bool inWater, float wadeSpeedScale = 1f)
    {
        WaterSurfaceY = waterSurfaceY;
        InWater = inWater;
        WadeSpeedScale = wadeSpeedScale;
    }
}
