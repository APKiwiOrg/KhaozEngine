using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;

namespace KhaozEngine.Tests;

/// <summary>
/// Opt-in thread pool starvation watchdog for a test host (#720, #553). Off unless <c>KE_POOL_WATCH=1</c>, and
/// then the only cost when it is off is the one environment read in <see cref="StartIfEnabled"/>.
///
/// <para>When armed it runs on its own dedicated thread, never a pool thread, so a starved pool cannot silence
/// it. Every <see cref="ProbeInterval"/> it queues one probe work item on the pool and reads how long that item
/// has waited. A wait of <see cref="EpisodeThreshold"/> or more is a starvation episode, reported as one line when
/// the pool recovers (or at process exit if it never does) to stderr and to a log file. The first time a still
/// queued probe has waited <see cref="DumpThreshold"/>, or an episode has lasted that long, the watchdog launches
/// the runtime's own <c>createdump</c> against this process, once per process, with the heap included so SOS can
/// walk the managed stacks of the threads that were holding the pool.</para>
///
/// <para>Output goes to the directory named by <c>KE_POOL_WATCH_DIR</c>, or <c>ke-pool-watch</c> under the temp
/// directory when that is unset. The log file also gets one <c>pool-watch-armed</c> line per process, so an empty
/// artifact can be told apart from an instrument that never ran.</para>
/// </summary>
public static class ThreadPoolStarvationWatchdog
{
    /// <summary>Set to <c>1</c> to arm the watchdog.</summary>
    public const string EnableVariable = "KE_POOL_WATCH";

    /// <summary>Directory for the log file and the dump.</summary>
    public const string DirectoryVariable = "KE_POOL_WATCH_DIR";

    private static readonly TimeSpan ProbeInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan EpisodeThreshold = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DumpThreshold = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DumpBudget = TimeSpan.FromMinutes(3);

    private static int started;

    /// <summary>
    /// Starts the watchdog for this process when <c>KE_POOL_WATCH=1</c>, and does nothing otherwise. Idempotent.
    /// Call it from a test assembly's module initializer so it is running before the first test.
    /// </summary>
    /// <param name="host">The test assembly's name, which labels every line and the dump file.</param>
    public static void StartIfEnabled(string host)
    {
        if (Environment.GetEnvironmentVariable(EnableVariable) != "1") return;
        if (Interlocked.Exchange(ref started, 1) != 0) return;

        string directory = Environment.GetEnvironmentVariable(DirectoryVariable) is { Length: > 0 } configured
            ? configured
            : Path.Combine(Path.GetTempPath(), "ke-pool-watch");
        var watch = new Watch(host, directory);
        var thread = new Thread(watch.Run)
        {
            IsBackground = true,
            Name = "ke-pool-watch",
            // It does almost nothing, and on a saturated runner it has to get a slice to notice anything at all.
            Priority = ThreadPriority.AboveNormal,
        };
        thread.Start();
    }

    private sealed class Probe
    {
        public Probe(long queuedAt) => QueuedAt = queuedAt;

        public long QueuedAt { get; }

        private long completedAt;

        public long CompletedAt => Volatile.Read(ref completedAt);

        public void Complete() => Volatile.Write(ref completedAt, Stopwatch.GetTimestamp());
    }

    private sealed class Watch
    {
        private readonly string host;
        private readonly string directory;
        private readonly string processName;
        private readonly int pid;
        private readonly string logPath;
        private readonly object gate = new();
        private readonly StarvationEpisodeTracker tracker = new(EpisodeThreshold, DumpThreshold);

        public Watch(string host, string directory)
        {
            this.host = host;
            this.directory = directory;
            using Process self = Process.GetCurrentProcess();
            processName = self.ProcessName;
            pid = Environment.ProcessId;
            logPath = Path.Combine(directory, $"pool-watch-{host}-{pid}.log");
        }

        public void Run()
        {
            try
            {
                Directory.CreateDirectory(directory);
                ThreadPool.GetMinThreads(out int minWorkers, out _);
                WriteFile($"{Stamp(DateTime.UtcNow)} pool-watch-armed {Identity()} min_workers={minWorkers} " +
                          $"episode_ms={(long)EpisodeThreshold.TotalMilliseconds} dump_ms={(long)DumpThreshold.TotalMilliseconds}");
                AppDomain.CurrentDomain.ProcessExit += (_, _) => ReportOpenAtExit();
                Loop();
            }
            catch (Exception ex)
            {
                // A diagnostic must never take the test host down with it.
                TryWrite($"{Stamp(DateTime.UtcNow)} pool-watch-stopped {Identity()} error={ex.GetType().Name}: {ex.Message}");
            }
        }

        private void Loop()
        {
            Probe? probe = null;
            while (true)
            {
                if (probe is null)
                {
                    probe = new Probe(Stopwatch.GetTimestamp());
                    ThreadPool.UnsafeQueueUserWorkItem(static p => p.Complete(), probe, preferLocal: false);
                }

                Thread.Sleep(ProbeInterval);

                long completedAt = probe.CompletedAt;
                bool completed = completedAt != 0;
                TimeSpan latency = Stopwatch.GetElapsedTime(probe.QueuedAt, completed ? completedAt : Stopwatch.GetTimestamp());
                PoolCounters counters = tracker.IsSlow(latency)
                    ? new PoolCounters(ThreadPool.ThreadCount, ThreadPool.PendingWorkItemCount, ThreadPool.CompletedWorkItemCount)
                    : default;

                StarvationObservation observation;
                lock (gate) observation = tracker.Observe(DateTime.UtcNow, latency, completed, counters);

                if (observation.Closed is { } closed) Report(closed, "closed");
                if (observation.CaptureDump) CaptureDump(latency);
                if (completed) probe = null;
            }
        }

        private void ReportOpenAtExit()
        {
            StarvationEpisode? open;
            lock (gate) open = tracker.Open;
            if (open is not null) Report(open, "open-at-exit");
        }

        private void Report(StarvationEpisode episode, string state)
        {
            ThreadPool.GetMinThreads(out int minWorkers, out _);
            long durationMs = (long)(DateTime.UtcNow - episode.StartUtc).TotalMilliseconds;
            string line =
                $"{Stamp(episode.StartUtc)} pool-starvation {Identity()} state={state} " +
                $"peak_ms={(long)episode.PeakLatency.TotalMilliseconds} duration_ms={durationMs} " +
                $"threads={episode.CountersAtPeak.ThreadCount} pending={episode.CountersAtPeak.PendingWorkItemCount} " +
                $"completed={episode.CountersAtPeak.CompletedWorkItemCount} min_workers={minWorkers}";
            TryWrite(line);
        }

        private void CaptureDump(TimeSpan latency)
        {
            string dumpPath = Path.Combine(directory, $"pool-watch-{host}-{pid}.dmp");
            string createdump = Path.Combine(
                Path.GetDirectoryName(typeof(object).Assembly.Location) ?? string.Empty,
                OperatingSystem.IsWindows() ? "createdump.exe" : "createdump");
            TryWrite($"{Stamp(DateTime.UtcNow)} pool-dump-start {Identity()} latency_ms={(long)latency.TotalMilliseconds} " +
                     $"threads={ThreadPool.ThreadCount} pending={ThreadPool.PendingWorkItemCount} path={dumpPath}");

            var clock = Stopwatch.StartNew();
            string outcome;
            try
            {
                var info = new ProcessStartInfo(createdump)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                info.ArgumentList.Add("--withheap");
                info.ArgumentList.Add("-f");
                info.ArgumentList.Add(dumpPath);
                info.ArgumentList.Add(pid.ToString(CultureInfo.InvariantCulture));

                using Process dumper = Process.Start(info) ?? throw new InvalidOperationException("createdump did not start");
                if (dumper.WaitForExit(DumpBudget))
                {
                    string detail = (dumper.StandardError.ReadToEnd() + " " + dumper.StandardOutput.ReadToEnd())
                        .ReplaceLineEndings(" ").Trim();
                    long bytes = File.Exists(dumpPath) ? new FileInfo(dumpPath).Length : 0;
                    outcome = $"exit={dumper.ExitCode} bytes={bytes}" + (dumper.ExitCode == 0 ? string.Empty : $" detail={detail}");
                }
                else
                {
                    dumper.Kill();
                    outcome = $"exit=timeout budget_ms={(long)DumpBudget.TotalMilliseconds}";
                }
            }
            catch (Exception ex)
            {
                outcome = $"exit=error error={ex.GetType().Name}: {ex.Message}";
            }

            TryWrite($"{Stamp(DateTime.UtcNow)} pool-dump {Identity()} {outcome} elapsed_ms={clock.ElapsedMilliseconds} path={dumpPath}");
        }

        private string Identity() => $"host={host} process={processName} pid={pid}";

        private void TryWrite(string line)
        {
            try
            {
                Console.Error.WriteLine(line);
                WriteFile(line);
            }
            catch (Exception)
            {
                // Best effort: a full disk or a closed stderr is not a reason to fail a test run.
            }
        }

        private void WriteFile(string line)
        {
            lock (gate) File.AppendAllText(logPath, line + Environment.NewLine);
        }

        private static string Stamp(DateTime utc) => utc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
    }
}
