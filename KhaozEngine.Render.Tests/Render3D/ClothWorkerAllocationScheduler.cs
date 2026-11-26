using System;
using System.Threading;
using KhaozEngine.Simulation;

namespace KhaozEngine.Tests.Render3D;

// The real scheduler also drains jobs on its caller. Keep both participants inside their first
// callback until two jobs were taken, then direct the selected entry to the dedicated worker.
internal sealed class ClothWorkerAllocationScheduler : IJobScheduler, IDisposable
{
    private readonly WorkerJobScheduler _scheduler = new(1);
    private readonly ManualResetEventSlim _callerDone = new();
    private readonly ManualResetEventSlim _workerDone = new();
    private readonly Action<int> _run;
    private Action<int> _body = null!;
    private int _callerId;
    private bool _count;

    internal ClothWorkerAllocationScheduler() => _run = Visit;
    internal bool UsedDedicatedWorker { get; private set; }
    internal long WorkerBytes { get; private set; }

    internal void StartCounting()
    {
        _count = true;
        WorkerBytes = 0;
        UsedDedicatedWorker = false;
    }

    public void For(int count, Action<int> body)
    {
        if (count != 2) throw new ArgumentOutOfRangeException(nameof(count));
        _body = body;
        _callerId = Environment.CurrentManagedThreadId;
        _callerDone.Reset();
        _workerDone.Reset();
        _scheduler.For(count, _run);
    }

    private void Visit(int index)
    {
        if (_callerId == Environment.CurrentManagedThreadId)
        {
            try { _body(0); }
            finally { _callerDone.Set(); }
            _workerDone.Wait();
            return;
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        try { _body(1); }
        finally
        {
            if (_count)
            {
                WorkerBytes += GC.GetAllocatedBytesForCurrentThread() - before;
                UsedDedicatedWorker = true;
            }
            _workerDone.Set();
            _callerDone.Wait();
        }
    }

    public void Dispose()
    {
        _scheduler.Dispose();
        _callerDone.Dispose();
        _workerDone.Dispose();
    }
}
