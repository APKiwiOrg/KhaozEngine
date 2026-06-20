using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.Render2D.Vfx
{
    /// <summary>
    /// Read-only snapshot of one live 2D particle, for headless tests and custom rendering. <see cref="Size"/> and
    /// <see cref="Color"/> are the current (post-curve) draw values; <see cref="Life"/>/<see cref="MaxLife"/> are
    /// the remaining and original lifetimes in seconds.
    /// </summary>
    public readonly record struct Particle2DView(
        Vector2 Position,
        Vector2 Velocity,
        float Rotation,
        float Size,
        Color Color,
        float Life,
        float MaxLife,
        BlendMode Blend);
}
