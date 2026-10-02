using System;

namespace KhaozEngine.Locomotion;

public readonly partial record struct MoveTuning
{
    /// <summary>Chooses a jump launch velocity for a requested apex under the gravity-first fixed step used by
    /// <see cref="CharacterMovement"/>. Computes <c>sqrt(2 * gravity * apexMetres) + gravity * stepSeconds / 2</c>
    /// with double intermediates, then returns a float.</summary>
    /// <remarks>Use the same gravity and step duration in the movement tuning and simulation. In free flight under
    /// constant gravity, without collision or other vertical intervention, the corrected parabola peaks at the
    /// requested height. Tick sampling lowers its peak by at most <c>gravity * stepSeconds^2 / 8</c>, before float
    /// rounding. This does not compensate for variable steps, swimming or other forces, or change <see cref="Default"/>.</remarks>
    /// <param name="apexMetres">Finite nonnegative height above the launch position, in metres.</param>
    /// <param name="gravity">Finite positive downward acceleration magnitude, in metres per second squared.</param>
    /// <param name="stepSeconds">Finite positive fixed step duration, in seconds.</param>
    /// <returns>The upward launch velocity to assign to <see cref="JumpSpeed"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An argument is nonfinite or outside its stated domain.</exception>
    /// <exception cref="OverflowException">The computed launch exceeds the finite float range.</exception>
    public static float JumpSpeedForApex(float apexMetres, float gravity, float stepSeconds)
    {
        if (!float.IsFinite(apexMetres) || apexMetres < 0f)
            throw new ArgumentOutOfRangeException(nameof(apexMetres), apexMetres, "Apex must be finite and nonnegative.");
        if (!float.IsFinite(gravity) || gravity <= 0f)
            throw new ArgumentOutOfRangeException(nameof(gravity), gravity, "Gravity must be finite and positive.");
        if (!float.IsFinite(stepSeconds) || stepSeconds <= 0f)
            throw new ArgumentOutOfRangeException(nameof(stepSeconds), stepSeconds, "Step duration must be finite and positive.");

        double launch = Math.Sqrt(2d * gravity * apexMetres) + (double)gravity * stepSeconds / 2d;
        if (launch > float.MaxValue)
            throw new OverflowException("Jump launch exceeds the finite float range.");
        return (float)launch;
    }
}
