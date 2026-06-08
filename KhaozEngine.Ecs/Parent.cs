namespace KhaozEngine.Ecs;

/// <summary>
/// Built-in component holding an entity's parent in the hierarchy. Set and cleared via
/// <see cref="World.SetParent"/> / <see cref="World.Detach"/>, which also keep the World's
/// children index consistent. Serializes like any component (the parent reference is preserved).
/// </summary>
public struct Parent : IComponent { public Entity Value; }
