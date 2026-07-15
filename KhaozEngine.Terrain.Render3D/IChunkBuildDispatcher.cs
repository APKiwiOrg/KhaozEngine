using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Terrain
{
    /// <summary>How the async chunk-build pipeline runs its CPU mesh builds off the frame thread. A build body only
    /// samples the analytic <see cref="TerrainField"/> and fills CPU buffers (no GPU device), so it is safe to run on
    /// a worker thread. The production dispatcher (<see cref="TaskChunkBuildDispatcher"/>) fans each build onto the
    /// thread pool. A test dispatcher can queue the bodies and run them in a controlled order to exercise
    /// out-of-order completion. Injected into <see cref="ChunkBuildScheduler{T}"/> (and thus into
    /// <see cref="TerrainStreamer"/>); leave it null to get the default.</summary>
    public interface IChunkBuildDispatcher
    {
        /// <summary>Run a build body. In production it executes off the caller's thread. A test dispatcher may queue it
        /// for later. The body reports its own completion (it enqueues its result into the scheduler when it finishes),
        /// so this method returns nothing.</summary>
        void Schedule(Action build);

        /// <summary>Block until every body handed to <see cref="Schedule"/> so far has finished running. Backs the
        /// scheduler's deterministic drain (<see cref="ChunkBuildScheduler{T}.Flush"/>) so a caller can force all
        /// outstanding builds to complete synchronously.</summary>
        void Drain();
    }

    /// <summary>Default <see cref="IChunkBuildDispatcher"/>: each build runs on the thread pool via
    /// <see cref="Task.Run(Action)"/>. <see cref="Drain"/> waits for every outstanding build to finish. Thread-safe:
    /// <see cref="Schedule"/> may be called only from the frame thread, but the completion bookkeeping is guarded so a
    /// build finishing on a worker thread never races the next <see cref="Schedule"/>.</summary>
    public sealed class TaskChunkBuildDispatcher : IChunkBuildDispatcher
    {
        readonly object _gate = new();
        readonly HashSet<Task> _outstanding = new();

        public void Schedule(Action build)
        {
            ArgumentNullException.ThrowIfNull(build);
            Task task = Task.Run(build);
            lock (_gate) _outstanding.Add(task);
            // Remove the task from the outstanding set once it finishes, so the set stays bounded by the number of
            // in-flight builds rather than every build ever scheduled. Runs inline on the completing worker thread.
            task.ContinueWith(static (done, state) =>
            {
                var self = (TaskChunkBuildDispatcher)state!;
                lock (self._gate) self._outstanding.Remove(done);
            }, this, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        public void Drain()
        {
            Task[] pending;
            lock (_gate) pending = _outstanding.Count == 0 ? Array.Empty<Task>() : new List<Task>(_outstanding).ToArray();
            // WaitAll on the build tasks: each build task completes only after its body has enqueued its result, so
            // after this returns every outstanding result is available to the scheduler's next Pump. A body that threw
            // surfaces here as an AggregateException; the scheduler also records the fault on its completion record so
            // the error re-surfaces deterministically on the frame thread even without a Drain.
            if (pending.Length > 0) Task.WaitAll(pending);
        }
    }
}
