using Microsoft.Xna.Framework;

namespace KhaozEngine.Effects;

/// <summary>
/// Read-only snapshot of a live particle, for headless tests and custom rendering.
/// <see cref="Size"/> is the current draw size (after the size-over-life curve);
/// <see cref="Color"/> is the base color before alpha fade.
/// </summary>
public readonly record struct ParticleView(
    Vector2 Position, Vector2 Velocity, Color Color, float Size, float Life, float MaxLife);
