namespace KhaozEngine.Movement;

/// <summary>How <see cref="PhysicsNavBake"/> builds one capsule class's profile. The default bakes a ground profile
/// exactly as the two-argument <c>BuildProfile</c> overload does.</summary>
public sealed record GroundProfileOptions
{
    private float _maxDropMetres;

    /// <summary>Maximum certified descent below launch support, in metres. Finite and nonnegative.
    /// Zero disables Drop generation and preserves the existing ground identity. Positive values require
    /// non-aquatic OrdinaryContact navigation. This grants route topology, not a manual fall or damage limit.</summary>
    public float MaxDropMetres { get => _maxDropMetres; init => _maxDropMetres = value == 0 ? 0 : value; }

    /// <summary>A ground profile.</summary>
    public static GroundProfileOptions Default { get; } = new();

    /// <summary>Replaces each swim-deep captured surface with a float surface where the body rests while swimming,
    /// proven by swim holds and swim steps through the live medium. Needs a capture that sampled water and swim
    /// fractions ordered <c>Exit &lt;= Submersion &lt;= Enter</c>. Bank edges wade slowly, so large cells or slow
    /// water zones must raise <see cref="PhysicsNavBakeOptions.MaxEdgeProbeSteps"/>, as <c>BuildProfile</c> states.</summary>
    public bool Aquatic { get; init; }
}
