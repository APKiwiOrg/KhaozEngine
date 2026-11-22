namespace KhaozEngine.Attachments;

/// <summary>Per-chain stiffness in radians per second, damping per second, gravity and trail multipliers, bend limit in radians, and collision radius in metres.</summary>
public readonly record struct ChainSettings(float Stiffness, float Damping, float Gravity, float Trail, float AngleLimit, float Radius)
{
    internal string? FirstInvalidMember()
    {
        if (!float.IsFinite(Stiffness) || Stiffness < 0)
            return "stiffness";
        if (!float.IsFinite(Damping) || Damping < 0)
            return "damping";
        if (!(Gravity >= 0 && Gravity <= 10))
            return "gravity";
        if (!(Trail >= 0 && Trail <= 1))
            return "trail";
        if (!(AngleLimit > 0 && AngleLimit <= System.MathF.PI))
            return "angleLimit";
        if (!float.IsFinite(Radius) || Radius < 0)
            return "radius";
        return null;
    }
}
