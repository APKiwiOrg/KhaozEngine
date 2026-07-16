namespace KhaozEngine.Navigation;

/// <summary>
/// Tuning knobs for a single <see cref="IPathPlanner.FindPath"/> call: how much search effort to spend
/// and how far an endpoint may be nudged onto a passable cell. Value type, cheap to construct per query.
/// </summary>
public readonly struct PathQueryBudget
{
    /// <summary>Upper bound on nodes the A* search may expand before giving up and returning a
    /// <see cref="NavPathStatus.Partial"/> path.</summary>
    public int MaxExpandedNodes { get; init; }

    /// <summary>Max world-unit distance an endpoint may be nudged from its query point onto the
    /// nearest passable cell. An endpoint further than this from any passable cell fails to snap and
    /// the query returns <see cref="NavPath.Unreachable"/>.</summary>
    public float SnapRadius { get; init; }

    /// <summary>Default budget: 4096 expanded nodes, a 3 world-unit snap radius.</summary>
    public static PathQueryBudget Default => new() { MaxExpandedNodes = 4096, SnapRadius = 3f };
}
