using System;

namespace KhaozEngine.Navigation;

/// <summary>
/// The quota one <see cref="PathPlanRequest.Advance"/> call may spend, in abstract work units. Each indivisible
/// operation of a request (a footprint point test, a candidate member, a heap pop, a parent step, a copied
/// waypoint and so on) has a fixed integer weight of at least one unit. A call performs whole operations in
/// order and stops before one that would pass its quota, except that a call's first operation always runs.
/// Every call on a pending request that holds a workspace therefore makes progress and spends at most the
/// larger of the quota and the largest weight. The excess shows in <see cref="PathPlanRequest.WorkSpent"/> and
/// is never repaid, so a request's total work is the same for every quota schedule. The quota may differ on every call.
/// </summary>
/// <remarks>
/// A quota bounds work. Equal inputs spend equal units on every machine. A request holding a workspace
/// completes within W owner calls, where W is its final <see cref="PathPlanRequest.WorkSpent"/>, and within
/// ceil(W / (q - L + 1)) calls at a fixed quota q of at least the largest operation weight L.
/// Elapsed time per unit varies with the machine, runtime tier, cache and heap state, interruption, open set
/// size, column depth and the cost of a caller's region predicate, which counts as one operation whatever it
/// costs. A quota does not bound first use or JIT time.
/// </remarks>
internal readonly struct PathWorkBudget
{
    /// <summary>Creates a quota of <paramref name="units"/> work units.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="units"/> is below one.</exception>
    public PathWorkBudget(int units)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(units, 1);
        Units = units;
    }

    /// <summary>The quota in work units, at least one. The default value holds zero and
    /// <see cref="PathPlanRequest.Advance"/> refuses it.</summary>
    public int Units { get; }
}
