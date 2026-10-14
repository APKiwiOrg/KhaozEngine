using System.Numerics;

namespace KhaozEngine.Locomotion;

/// <summary>Finite absolute bounds described by a water field.</summary>
public readonly record struct MovementWaterExtent
{
    public Vector3 Minimum { get; }
    public Vector3 Maximum { get; }

    public MovementWaterExtent(Vector3 minimum, Vector3 maximum)
    {
        OrdinaryMovementExtent.ValidateBounds(minimum, maximum);
        Minimum = minimum;
        Maximum = maximum;
    }
}
