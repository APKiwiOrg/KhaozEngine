namespace KhaozEngine.Locomotion;

/// <summary>Portable water-origin state. It carries no environment selection, support handle or
/// lease. Explicit movement clears the excursion only after proving supported footing.</summary>
public enum WaterExcursionState : byte
{
    None = 0,
    Surface = 1,
    AirborneFromWater = 2
}
