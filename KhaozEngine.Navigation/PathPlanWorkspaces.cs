using System;

namespace KhaozEngine.Navigation;

/// <summary>
/// A caller-owned pool of prewarmed workspaces for fixed-quota region planning. Create one from the ground
/// navigation profile the followers or movers use. Synchronous queries use the planner's own working memory.
/// </summary>
/// <remarks>
/// <para>A pool belongs to its creating profile and serves that profile and its bound route straighteners.
/// A follower or mover constructor rejects a pool from another profile. Keep it alive while its owners use it,
/// and reset each owner before discarding it. Dispose the pool last.</para>
/// <para>Serialize construction of owners, ticks, resets, pool reads and disposal for each shared pool, and
/// keep region predicates pure. The pool detects only a call that overlaps another call into the pool itself,
/// such as a region predicate that reads the pool while a request advances, or a second thread that enters
/// the pool at the same moment. That call throws <see cref="InvalidOperationException"/>, and so does an
/// owner's reset when its cancellation overlaps. Owner ticks that overlap outside the pool go undetected,
/// so serialization stays the caller's duty. A pool can move to another thread between calls.</para>
/// <para>Each follower or mover holds at most one request. Use one workspace per sharing owner to avoid
/// waiting. In a smaller pool, new requests queue in order and spend no work until granted a workspace.
/// Completion or reset passes the workspace immediately to the oldest waiter, which can spend its quota on
/// its next tick. Tick order determines whether that happens in the same simulation tick or the next.</para>
/// <para>Each workspace preallocates node and membership storage for every layer cell, reconstruction storage
/// up to the smaller of the node count and the expansion limit, and frontier storage for possible pushes
/// within that limit. These buffers stay fixed during planning. Publishing a nonempty route allocates its
/// result.</para>
/// </remarks>
public abstract class PathPlanWorkspaces : IDisposable
{
    /// <summary>Creates an empty pool shell. The planner that implements it supplies the workspaces.</summary>
    private protected PathPlanWorkspaces()
    {
    }

    /// <summary>The number of workspaces, at least one and fixed at creation.</summary>
    /// <exception cref="InvalidOperationException">This read overlaps another call into the pool.</exception>
    public abstract int Count { get; }

    /// <summary>The cumulative number of requests queued for a workspace. Zero means none has waited.</summary>
    /// <exception cref="InvalidOperationException">This read overlaps another call into the pool.</exception>
    public abstract long Waits { get; }

    /// <summary>
    /// Cancels leased and queued requests. Completed results awaiting adoption remain available. A later
    /// attempt to configure an owner with the pool or begin another request throws
    /// <see cref="ObjectDisposedException"/>. Repeated disposal has no effect.
    /// </summary>
    /// <exception cref="InvalidOperationException">Disposal overlaps another call into the pool.</exception>
    public abstract void Dispose();
}
