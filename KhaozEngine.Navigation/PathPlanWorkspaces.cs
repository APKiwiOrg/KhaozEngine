using System;

namespace KhaozEngine.Navigation;

/// <summary>
/// A fixed set of search workspaces that <see cref="PathPlanRequest"/>s lease, created and prewarmed by
/// <see cref="IIncrementalRegionPathPlanner.CreateWorkspaces"/> with an explicit count and largest query budget.
/// Synchronous queries keep the planner's own working memory and never touch a pool.
/// </summary>
/// <remarks>
/// <para>Ownership. A pool belongs to the planner that created it. A begin from another planner throws
/// <see cref="ArgumentException"/>, and a planner over an area-excluding view is another planner.</para>
/// <para>One owner sequence. Calls into a pool, its begins and its requests' advances and cancels, must not
/// overlap. An overlapping call, from a second thread or from a region predicate that re-enters the planner,
/// throws <see cref="InvalidOperationException"/>. No thread is pinned, so a pool created on a loading thread may
/// be used from the simulation thread.</para>
/// <para>Grant at begin. A request takes a free workspace at begin only when nothing waits, and spends from the
/// owner's advance in that same call. Otherwise it joins the back of a first in, first out queue and spends
/// nothing until it holds one.</para>
/// <para>Hand-over. A released workspace passes at once to the oldest waiter, which spends from its own next
/// advance: later in the same tick if its owner comes after the releasing owner in the consumer's call order, else
/// in the next tick.</para>
/// <para>Release. A request releases its workspace when it completes, before its owner adopts the result, and when
/// it is cancelled or disposed. A cancelled waiter leaves the queue. A cancelled request holds no lease and cannot
/// release another's. Disposing the pool cancels every request it holds or queues, and a later begin throws
/// <see cref="ObjectDisposedException"/>.</para>
/// <para>Sizing. The count has no default because the engine cannot know how many owners share a pool. One
/// workspace per sharing owner means no request ever waits. A smaller pool makes each request's publication call
/// depend on the requests ahead of it: their begins, completions and releases and the call order within a tick.
/// Each workspace preallocates node and region membership storage for all layer cells, reconstruction storage
/// up to the expansion cap or node count, whichever is smaller, and frontier storage for possible pushes
/// within that cap. Its buffers never grow during a request. An owner dropped while its request is pending keeps
/// the lease, so cancel or reset an owner first. <see cref="Waits"/> lets a consumer check that nothing waited.</para>
/// </remarks>
internal abstract class PathPlanWorkspaces : IDisposable
{
    /// <summary>Creates an empty pool shell. The planner that implements it supplies the workspaces.</summary>
    private protected PathPlanWorkspaces()
    {
    }

    /// <summary>The number of workspaces, at least one and fixed at creation.</summary>
    public abstract int Count { get; }

    /// <summary>How many requests have joined the queue because no workspace was free at their begin, since the
    /// pool was created. Zero means no request has waited.</summary>
    public abstract long Waits { get; }

    /// <summary>Cancels every request the pool holds or queues and refuses later begins. Idempotent.</summary>
    public abstract void Dispose();
}
