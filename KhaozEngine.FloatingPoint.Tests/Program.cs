using System;
using KhaozEngine.Tests.Simulation;

namespace KhaozEngine.Tests;

/// <summary>The test runner never calls this entry point. A test starts this assembly as a child process to run a
/// scenario that needs a process of its own, such as a thread pool that has not run anything yet.</summary>
internal static class Program
{
    private static int Main(string[] args) =>
        args is [ThreadPoolJobSchedulerPoolThreadTests.ColdFanOut]
            ? ThreadPoolJobSchedulerPoolThreadTests.RunColdFanOut(Console.Out)
            : 2;
}
