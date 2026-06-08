namespace KhaozEngine.Ecs;

/// <summary>
/// A lightweight handle to an entity: just a stable integer id. Components are stored
/// in the <see cref="World"/>, keyed by this id, not on the entity itself.
/// </summary>
/// <param name="Id">The entity's unique id within its <see cref="World"/>.</param>
public readonly record struct Entity(int Id);
