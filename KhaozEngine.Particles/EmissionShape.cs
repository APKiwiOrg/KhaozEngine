namespace KhaozEngine.Particles;

/// <summary>
/// The volume a burst spawns across, as an offset from the emit origin. <see cref="Point"/> (value 0) is the
/// default, so a zero-default config spawns every particle exactly at the origin (legacy behaviour).
/// </summary>
public enum EmissionShape : byte
{
    /// <summary>All particles spawn at the origin (legacy).</summary>
    Point = 0,

    /// <summary>Inside (or on) a sphere of <c>ShapeRadius</c>. <c>ShapeShell</c> 0 fills the volume, 1 is the surface.</summary>
    Sphere,

    /// <summary>A sphere folded to the <c>Direction</c> half-space (dome), <c>+Y</c> when Direction is ~zero.</summary>
    Hemisphere,

    /// <summary>A flat disc perpendicular to <c>Direction</c> (<c>+Y</c> when ~zero). <c>ShapeShell</c> 1 is the ring edge.</summary>
    Disc,
}

/// <summary>
/// How a spawned particle's initial velocity direction is chosen. <see cref="Cone"/> (value 0) is the
/// default legacy spread cone.
/// </summary>
public enum ParticleVelocityMode : byte
{
    /// <summary>The legacy spread cone around <c>Direction</c> with <c>SpreadDegrees</c> half-angle.</summary>
    Cone = 0,

    /// <summary>Outward from the origin through the spawn point (explosions, shockwaves).</summary>
    Radial,
}
