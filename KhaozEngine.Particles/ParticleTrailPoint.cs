using System.Numerics;

namespace KhaozEngine.Particles;

/// <summary>
/// One sampled point of a particle's motion history, captured at fixed intervals while the particle is alive.
/// Read back oldest-to-newest via <see cref="ParticleSystem.GetTrail"/> to draw a velocity tail.
/// </summary>
/// <param name="Position">World position at capture time.</param>
/// <param name="Age">The particle's age (seconds) when this point was captured.</param>
public readonly record struct ParticleTrailPoint(Vector3 Position, float Age);
