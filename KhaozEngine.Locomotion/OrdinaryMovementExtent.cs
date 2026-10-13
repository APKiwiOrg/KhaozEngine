using System.Numerics;

namespace KhaozEngine.Locomotion;

/// <summary>Installer-declared absolute resident extent and optional certified analytic height range.</summary>
public readonly record struct OrdinaryMovementExtent
{
    public Vector3 Minimum { get; }
    public Vector3 Maximum { get; }
    public float? AnalyticMinimumY { get; }
    public float? AnalyticMaximumY { get; }

    public OrdinaryMovementExtent(Vector3 minimum, Vector3 maximum,
        float? analyticMinimumY = null, float? analyticMaximumY = null)
    {
        ValidateBounds(minimum, maximum);
        MovementEnvironmentValidation.Require(analyticMinimumY.HasValue == analyticMaximumY.HasValue,
            nameof(analyticMinimumY));
        if (analyticMinimumY is { } lower && analyticMaximumY is { } upper)
            MovementEnvironmentValidation.Require(float.IsFinite(lower) && float.IsFinite(upper) &&
                lower <= upper && lower >= minimum.Y && upper <= maximum.Y, nameof(analyticMinimumY));
        Minimum = minimum;
        Maximum = maximum;
        AnalyticMinimumY = analyticMinimumY;
        AnalyticMaximumY = analyticMaximumY;
    }

    internal static void ValidateBounds(Vector3 minimum, Vector3 maximum) =>
        MovementEnvironmentValidation.Require(MovementEnvironmentValidation.Finite(minimum) &&
            MovementEnvironmentValidation.Finite(maximum) && minimum.X <= maximum.X &&
            minimum.Y <= maximum.Y && minimum.Z <= maximum.Z, nameof(minimum));
}
