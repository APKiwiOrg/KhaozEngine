namespace KhaozEngine.Physics;

/// <summary>Surface response for a body. Friction and restitution are mostly exercised by dynamic
/// bodies (sub-project 2); static-world collision in sub-project 1 carries the default.</summary>
public readonly record struct PhysicsMaterial(float Friction, float Restitution)
{
    /// <summary>Full friction, no bounce.</summary>
    public static readonly PhysicsMaterial Default = new(1f, 0f);
}
