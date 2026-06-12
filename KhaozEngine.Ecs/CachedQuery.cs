namespace KhaozEngine.Ecs;

/// <summary>
/// Reuses a single <see cref="Query"/> across calls to avoid per-tick allocation, rebuilding it only
/// when the backing <see cref="World"/> instance changes (consumers that recreate the World on reset).
/// The underlying Query still self-refreshes its matched-archetype list on ArchetypeGen changes.
/// </summary>
public sealed class CachedQuery
{
    private readonly System.Func<World, Query> _build;
    private World? _world;
    private Query? _query;

    public CachedQuery(System.Func<World, Query> build) => _build = build;

    /// <summary>Returns the reusable Query for <paramref name="world"/>, rebuilding on a World swap.</summary>
    public Query For(World world)
    {
        if (!ReferenceEquals(world, _world))
        {
            _world = world;
            _query = _build(world);
        }
        return _query!;
    }
}
