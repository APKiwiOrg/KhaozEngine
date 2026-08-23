using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace KhaozEngine.Tests;

/// <summary>
/// Raises this test host's thread-pool floor before any test runs.
///
/// The pool starts at one worker per core and, once every worker is busy, injects further threads at roughly one
/// per 500 ms. This assembly is full of tests that block a worker in a poll loop (the netcode round-trips, the
/// sharding end-to-end), so on a hosted two-to-four core runner executing the full suite the injection ramp can
/// leave a socket completion queued for seconds. That is what turned the loopback listener tests into intermittent
/// timeouts on the Windows leg (#720): nothing was wrong with the listener, its completion simply did not get a
/// thread inside the client's budget.
///
/// A module initializer runs once, at module load, ahead of every test, so this writes its process-global state
/// with no test running beside it and needs no DisableParallelization collection. It only ever raises the floor,
/// so a host that already runs with a higher one keeps it.
/// </summary>
internal static class ThreadPoolFloor
{
    [ModuleInitializer]
    internal static void Raise()
    {
        ThreadPool.GetMinThreads(out int workers, out int completionPorts);
        int floor = Math.Max(Environment.ProcessorCount * 4, 32);
        ThreadPool.SetMinThreads(Math.Max(workers, floor), Math.Max(completionPorts, floor));
    }
}
