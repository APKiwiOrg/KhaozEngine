using System;
using System.Collections.Generic;
using KhaozEngine.Platform;

namespace KhaozEngine.Tests.App
{
    /// <summary>
    /// In-memory <see cref="IProcessControl"/> for the <see cref="KhaozEngine.App.AppRelaunch"/> tests: it
    /// records the launch request and wait calls (and the ordering of spawn vs. shutdown via
    /// <see cref="Events"/>) instead of touching real processes, so the relaunch orchestration is verified
    /// with no fork.
    /// </summary>
    internal sealed class FakeProcessControl : IProcessControl
    {
        public string? CurrentExecutablePath { get; set; } = "/apps/Game.bin";
        public int CurrentProcessId { get; set; } = 4242;
        public IReadOnlyList<string> CurrentCommandLineArguments { get; set; } = Array.Empty<string>();

        /// <summary>Ordered log of side effects. StartDetached appends "start"; the test's shutdown callback appends "shutdown".</summary>
        public readonly List<string> Events = new();

        public ProcessStartRequest? LastStart { get; private set; }
        public int StartCount { get; private set; }
        /// <summary>When true, <see cref="StartDetached"/> throws to model the OS refusing to launch the successor.</summary>
        public bool StartThrows { get; set; }

        public int? LastWaitPid { get; private set; }
        public int? LastWaitTimeoutMs { get; private set; }
        public int WaitCount { get; private set; }
        /// <summary>What <see cref="WaitForProcessExit"/> returns: true = predecessor exited, false = timed out.</summary>
        public bool WaitReturns { get; set; } = true;

        public void StartDetached(ProcessStartRequest request)
        {
            StartCount++;
            LastStart = request;
            Events.Add("start");
            if (StartThrows)
            {
                throw new InvalidOperationException("simulated launch failure");
            }
        }

        public bool WaitForProcessExit(int processId, int timeoutMilliseconds)
        {
            WaitCount++;
            LastWaitPid = processId;
            LastWaitTimeoutMs = timeoutMilliseconds;
            return WaitReturns;
        }
    }
}
