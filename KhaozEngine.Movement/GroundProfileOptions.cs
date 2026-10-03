namespace KhaozEngine.Movement;

/// <summary>How <see cref="PhysicsNavBake"/> builds one capsule class's profile. The default bakes a ground profile
/// exactly as the two-argument <c>BuildProfile</c> overload does.</summary>
public sealed record GroundProfileOptions
{
    /// <summary>A ground profile.</summary>
    public static GroundProfileOptions Default { get; } = new();

    /// <summary>Replaces each swim-deep captured surface with a float surface where the body rests while swimming,
    /// proven by swim holds and swim steps through the live medium. Needs a capture that sampled water and swim
    /// fractions ordered <c>Exit &lt;= Submersion &lt;= Enter</c>. Bank edges wade slowly, so large cells or slow
    /// water zones must raise <see cref="PhysicsNavBakeOptions.MaxEdgeProbeSteps"/>, as <c>BuildProfile</c> states.</summary>
    public bool Aquatic { get; init; }
}
