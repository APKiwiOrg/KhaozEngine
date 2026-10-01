using System;
using System.Collections.Generic;

namespace KhaozEngine.NetWorld;

/// <summary>
/// The per-viewer visibility step both NetWorld serve loops run on a client's interest set, between the interest
/// query and the delta or snapshot writer. See <see cref="WorldServerConfig.EntityVisibleToSlot"/> and
/// <see cref="ShardedWorldServerConfig.EntityVisibleToSlot"/> for the consumer contract.
/// </summary>
/// <remarks>
/// <see cref="HashSet{T}.RemoveWhere"/> takes a predicate of one argument, so the viewer and the rule have to reach
/// it some other way than a capturing lambda, which would allocate a closure per call. They ride thread-static
/// fields read by one cached static delegate. Thread-static rather than static because several servers may tick on
/// different threads in one process, and saved and restored around the call so a rule that itself filters cannot
/// clobber the outer call's state, and so the rule is not kept alive past the call.
/// </remarks>
internal static class InterestVisibility
{
    [ThreadStatic] private static Func<int, long, bool>? currentRule;
    [ThreadStatic] private static int currentViewerSlot;
    [ThreadStatic] private static long currentViewerNetId;

    private static readonly Predicate<long> Hidden = static netId =>
        netId != currentViewerNetId && !currentRule!(currentViewerSlot, netId);

    /// <summary>Removes from <paramref name="interest"/> every net id <paramref name="visible"/> rejects for
    /// <paramref name="viewerSlot"/>, except <paramref name="viewerNetId"/>, which is never offered to the rule. A null
    /// rule returns before touching the set. Allocates nothing.</summary>
    internal static void Filter(HashSet<long> interest, int viewerSlot, long viewerNetId, Func<int, long, bool>? visible)
    {
        if (visible is null) return;
        Func<int, long, bool>? outerRule = currentRule;
        int outerSlot = currentViewerSlot;
        long outerNetId = currentViewerNetId;
        currentRule = visible;
        currentViewerSlot = viewerSlot;
        currentViewerNetId = viewerNetId;
        try
        {
            interest.RemoveWhere(Hidden);
        }
        finally
        {
            currentRule = outerRule;
            currentViewerSlot = outerSlot;
            currentViewerNetId = outerNetId;
        }
    }
}
