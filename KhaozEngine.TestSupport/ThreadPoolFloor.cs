using System;
using System.Threading;

namespace KhaozEngine.Tests;

/// <summary>
/// Raises a test host's thread-pool floor before any test runs.
///
/// The pool starts at one worker per core and, once every worker is busy, injects further threads at roughly one
/// per 500 ms. The server and map editor test assemblies are full of tests that block a worker in a poll loop (the
/// netcode round-trips, the sharding end-to-end, the MCP stdio harness), so on a hosted two-to-four core runner
/// executing the full suite the injection ramp can leave a completion queued for seconds. That is what turned the
/// loopback listener tests into intermittent timeouts on the Windows leg (#720): nothing was wrong with the
/// listener, its completion simply did not get a thread inside the client's budget. The starvation watchdog then
/// caught the map editor host, which had no floor, starving for 8.3 s at startup on the same leg (#553).
///
/// Each host calls <see cref="Raise"/> from a module initializer, which runs once, at module load, ahead of every
/// test, so it writes its process-global state with no test running beside it and needs no DisableParallelization
/// collection. It only ever raises the floor, so a host that already runs with a higher one keeps it.
/// </summary>
public static class ThreadPoolFloor
{
    /// <summary>Raises the worker and completion port minimums to four per core, and at least 32.</summary>
    public static void Raise()
    {
        ThreadPool.GetMinThreads(out int workers, out int completionPorts);
        int floor = Math.Max(Environment.ProcessorCount * 4, 32);
        ThreadPool.SetMinThreads(Math.Max(workers, floor), Math.Max(completionPorts, floor));
    }
}
