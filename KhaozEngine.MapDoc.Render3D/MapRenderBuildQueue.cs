using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.MapDoc.Render3D;

/// <summary>Bounded CPU preparation. Cancellation fences entries and completion, while readers retain their leases.</summary>
public sealed class MapRenderBuildQueue
{
    readonly MapRenderBudgets _budgets;
    readonly MapRenderLifetime _lifetime;
    readonly TaskScheduler _scheduler;
    readonly List<Job> _jobs = [];
    internal MapRenderBuildQueue(MapRenderBudgets budgets, MapRenderLifetime lifetime, TaskScheduler scheduler)
    { _budgets = budgets; _lifetime = lifetime; _scheduler = scheduler; }
    internal int Count => _jobs.Count;
    internal bool CanEnqueue(int plans, long bytes) => _jobs.Count < _budgets.MaxWorkers &&
        _jobs.Sum(j => j.Plans) <= _budgets.MaxQueuedPlans - plans && bytes <= _budgets.MaxQueuedBytes - _lifetime.QueuedBytes;
    internal Job Enqueue(int plans, long bytes, Func<CancellationToken, MapRenderPlan[]> prepare, IDisposable charge)
    {
        if (!CanEnqueue(plans, bytes)) throw new InvalidOperationException("Render preparation queue is full.");
        var job = new Job(plans, bytes, charge); _jobs.Add(job); _lifetime.Queued(bytes);
        job.Completion = Task.Factory.StartNew(() =>
        {
            _lifetime.Worker(1);
            try { job.Cancellation.Token.ThrowIfCancellationRequested(); return prepare(job.Cancellation.Token); }
            finally { _lifetime.Worker(-1); }
        }, CancellationToken.None, TaskCreationOptions.DenyChildAttach, _scheduler);
        return job;
    }
    internal void Remove(Job job)
    {
        if (!job.Completion.IsCompleted) throw new InvalidOperationException("A worker still owns its input readers.");
        if (!_jobs.Remove(job)) return;
        _lifetime.Queued(-job.Bytes); job.Cancellation.Dispose();
    }
    internal sealed class Job(int plans, long bytes, IDisposable charge)
    {
        internal readonly CancellationTokenSource Cancellation = new();
        internal readonly IDisposable Charge = charge;
        internal int Plans => plans;
        internal long Bytes => bytes;
        internal Task<MapRenderPlan[]> Completion = null!;
    }
}
