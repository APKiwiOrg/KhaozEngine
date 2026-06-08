namespace KhaozEngine.Ecs;

/// <summary>
/// Marker interface for component types. Components are plain classes holding data;
/// attach them to entities with <see cref="World.Set{T}(Entity, T)"/>.
/// </summary>
public interface IComponent { }
