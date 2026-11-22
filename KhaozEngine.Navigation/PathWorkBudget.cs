using System;

namespace KhaozEngine.Navigation;

/// <summary>
/// An immutable quota in abstract work units for a region planning call. Each operation, such as testing a
/// candidate cell or copying a waypoint, has a fixed positive weight. Planning runs whole operations in order
/// and stops before the next one would exceed the quota. The first operation always runs, so a call that holds
/// a workspace spends at most the larger of the quota and the largest operation weight.
/// </summary>
/// <remarks>
/// <para><see cref="PathPlanningConfig.MinimumQuotaUnits"/> covers the largest operation, so a follower or mover
/// configured with at least that quota stays within it. A request waiting for a workspace spends nothing.</para>
/// <para>Equal frozen inputs and equal workspace stamp history spend equal total units on every machine and
/// under any quota schedule. Once a request holds a workspace, it finishes on the call that runs its last
/// operation, or its first call if it has no operations. With total work W greater than zero and fixed quota
/// q at least the largest weight L, it finishes within <c>ceil(W / (q - L + 1))</c> planning calls.</para>
/// <para>Elapsed time varies with the machine, compilation tier, cache and heap state, interruptions, open set
/// size, column depth and the cost of a caller's region predicate. A quota also leaves first-use and JIT time
/// unbounded. This value owns no resources and can be copied between threads.</para>
/// <para>The quota covers the sliced planning work only. An owner tick can also check current goal membership,
/// validate the start of a result that completed on an earlier call and run steering. Validating changed feet
/// can resolve both start footprints synchronously. Measure the whole owner tick when sizing a simulation
/// budget.</para>
/// </remarks>
public readonly struct PathWorkBudget
{
    /// <summary>Creates a quota of <paramref name="units"/> work units.</summary>
    /// <param name="units">The positive number of work units allowed per planning call.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="units"/> is below one.</exception>
    public PathWorkBudget(int units)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(units, 1);
        Units = units;
    }

    /// <summary>The quota in work units. A default value has zero units and is refused by
    /// <see cref="PathPlanningConfig"/>.</summary>
    public int Units { get; }
}
