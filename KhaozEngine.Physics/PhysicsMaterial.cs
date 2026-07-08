namespace KhaozEngine.Physics;

/// <summary>Surface response for a body. Friction and restitution are mostly exercised by dynamic
/// bodies (sub-project 2); static-world collision in sub-project 1 carries the default.
/// <para><see cref="Restitution"/> (0..1) drives an approximate, deterministic game-feel bounce, NOT a
/// true physical coefficient of restitution: the bounce apex decays geometrically as restitution rises, but
/// the backend applies it as a bounded post-solve velocity reflection that can over-restitute by up to the
/// contact recovery velocity, and the exact apex is not analytically pinned. Use it to dial how bouncy a
/// surface feels, not to predict an exact rebound height.</para></summary>
public readonly record struct PhysicsMaterial(float Friction, float Restitution)
{
    /// <summary>Full friction, no bounce.</summary>
    public static readonly PhysicsMaterial Default = new(1f, 0f);
}
