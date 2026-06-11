namespace KhaozEngine.Effects;

/// <summary>How a particle's initial velocity direction is chosen at emit time.</summary>
public enum ParticleEmission
{
    /// <summary>Random direction over the full circle (outward burst).</summary>
    Radial,

    /// <summary>Along <see cref="ParticleEmitterConfig.Direction"/>, jittered by the spread cone.</summary>
    Directional,
}
