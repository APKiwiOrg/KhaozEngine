using System;

namespace KhaozEngine.Navigation;

/// <summary>
/// Opts a follower or mover into fixed-quota region planning over a prewarmed workspace pool.
/// The settings are immutable. The caller owns the pool and must serialize calls into it.
/// </summary>
public sealed class PathPlanningConfig
{
    /// <summary>Creates planning settings for use with <see cref="PathFollowConfig.Planning"/>.</summary>
    /// <param name="quota">The work allowed on each planning call, at least <see cref="MinimumQuotaUnits"/>.</param>
    /// <param name="workspaces">
    /// A caller-owned pool for the owner's navigation profile. The follower or mover validates its lifetime,
    /// profile and query budget at construction. Keep it alive while the owner uses it.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="quota"/> has fewer units than <see cref="MinimumQuotaUnits"/>, including a default quota.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="workspaces"/> is null.</exception>
    public PathPlanningConfig(PathWorkBudget quota, PathPlanWorkspaces workspaces)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(quota.Units, PathWorkWeights.Largest, nameof(quota));
        Quota = quota;
        Workspaces = workspaces ?? throw new ArgumentNullException(nameof(workspaces));
    }

    /// <summary>The smallest accepted quota in work units. It covers the largest indivisible operation.</summary>
    /// <remarks>Work units and this minimum can change between engine versions. Read it at run time, and check a
    /// stored or tuned quota against it after an upgrade.</remarks>
    public static int MinimumQuotaUnits => PathWorkWeights.Largest;

    /// <summary>
    /// The work quota per planning call, including the first call if a workspace is free.
    /// See <see cref="PathWorkBudget"/> for the work bound.
    /// </summary>
    public PathWorkBudget Quota { get; }

    /// <summary>The caller-owned pool from which the follower or mover leases a workspace when planning.</summary>
    public PathPlanWorkspaces Workspaces { get; }
}
