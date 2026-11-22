using System;

namespace KhaozEngine.Navigation;

/// <summary>Opts a region follower into one fixed-quota advance per tick over a compatible prewarmed pool.</summary>
internal sealed class PathPlanningConfig
{
    /// <summary>Configures the quota and pool. The follower validates ownership and capacity against its planner.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The quota is smaller than the largest indivisible operation.</exception>
    /// <exception cref="ArgumentNullException">The pool is null.</exception>
    internal PathPlanningConfig(PathWorkBudget quota, PathPlanWorkspaces workspaces)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(quota.Units, PathWorkWeights.Largest, nameof(quota));
        Quota = quota;
        Workspaces = workspaces ?? throw new ArgumentNullException(nameof(workspaces));
    }

    /// <summary>The work allowed on each planning tick, including the begin tick if a lease is immediately available.</summary>
    internal PathWorkBudget Quota { get; }

    /// <summary>The pool shared by the planners and bodies that own these requests.</summary>
    internal PathPlanWorkspaces Workspaces { get; }
}
