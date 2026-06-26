using System;

namespace KhaozEngine.Simulation;

/// <summary>
/// The engine's worker-pool seam: runs a fixed number of independent jobs, possibly across cores, and returns when
/// all have completed. One small abstraction reused by the whole parallel-job-system program - layer 1 fans
/// independent cell ticks across it, layers 2-3 (parallel <c>ForEach</c>, the system scheduler) partition rows and
/// run non-conflicting systems over the same seam. Deterministic by default: the inline
/// <see cref="SingleThreadedJobScheduler"/> runs jobs in index order, so a parallel result can always be asserted
/// equal to the single-threaded one.
/// </summary>
public interface IJobScheduler
{
    /// <summary>
    /// Invokes <paramref name="body"/> once for each index in <c>[0, count)</c> and blocks until all invocations
    /// complete. The jobs must be independent (no shared mutable state) - the scheduler may run them on any thread,
    /// in any order, or all inline. <paramref name="count"/> &lt;= 0 does nothing.
    /// </summary>
    void For(int count, Action<int> body);
}
