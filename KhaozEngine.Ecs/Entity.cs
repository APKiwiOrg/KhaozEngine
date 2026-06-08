namespace KhaozEngine.Ecs;

/// <summary>
/// A versioned handle to an entity. <see cref="Id"/> indexes the world's record table;
/// <see cref="Version"/> distinguishes a live entity from a stale handle to a recycled id.
/// </summary>
public readonly record struct Entity(int Id, uint Version);
