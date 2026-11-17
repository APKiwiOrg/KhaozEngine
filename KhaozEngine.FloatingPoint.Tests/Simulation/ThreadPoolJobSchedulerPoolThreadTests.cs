using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Simulation;
using KhaozEngine.Tests.Sharding;
using Xunit;

namespace KhaozEngine.Tests.Simulation;

/// <summary>
/// Where threads inherit their creator's controls, a fan-out that is the first thread pool use in a process creates
/// the pool's threads from the calling thread. A Linux arm64 probe showed every pool thread then kept a caller's
/// round toward zero after the caller was restored, while a pool that had already run work created its threads from
/// its own threads and leaked nothing. So <see cref="ThreadPoolJobScheduler"/> makes its calling thread canonical for
/// the fan-out, and this test runs the fan-out in a child process whose pool is still cold.
/// </summary>
public sealed class ThreadPoolJobSchedulerPoolThreadTests
{
    internal const string ColdFanOut = "--cold-pool-fan-out";

    // macOS threads did not inherit their creator's controls, so this passes there with or without the scheduler's
    // outer scope.
    [Fact]
    public async Task Pool_threads_created_for_a_first_fan_out_keep_the_default_rounding()
    {
        if (RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64)) return;

        string assembly = typeof(ThreadPoolJobSchedulerPoolThreadTests).Assembly.Location;
        var start = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(assembly);
        start.ArgumentList.Add(ColdFanOut);
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("The child did not start.");
        // A hang guard for the child, not a timing bound on the result.
        using var hang = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        string output, error;
        try
        {
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(hang.Token);
            Task<string> stderr = process.StandardError.ReadToEndAsync(hang.Token);
            await process.WaitForExitAsync(hang.Token);
            (output, error) = (await stdout, await stderr);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }

        Assert.True(process.ExitCode == 0, $"The child exited {process.ExitCode}: {error}");
        string[] report = output.Split('\n').Single(line => line.StartsWith("pool-threads ", StringComparison.Ordinal))
            .Trim().Split(' ');
        int threads = int.Parse(report[1]), changed = int.Parse(report[3]);
        Assert.True(threads > 0, output);
        Assert.Equal(0, changed);
    }

    /// <summary>The child: a first fan-out from a caller rounding toward zero, then plain pool work, outside any
    /// scope, reading the rounding on every pool thread. Prints <c>pool-threads N changed M</c>.</summary>
    internal static int RunColdFanOut(TextWriter report)
    {
        int jobs = Environment.ProcessorCount + 2;
        try
        {
            FpRounding.SetTowardZero();
            // Every body waits until all have started, so the fan-out needs a thread per job.
            using var started = new CountdownEvent(jobs);
            new ThreadPoolJobScheduler().For(jobs, _ => { started.Signal(); started.Wait(); });
        }
        finally
        {
            FpRounding.SetToNearest();
        }

        // One blocking item per pool thread, so each idle pool thread takes one.
        int probes = ThreadPool.ThreadCount;
        var observed = new ConcurrentDictionary<int, int>();
        using (var all = new CountdownEvent(probes))
        {
            Task[] work = new Task[probes];
            for (int i = 0; i < probes; i++)
                work[i] = Task.Run(() =>
                {
                    observed[Environment.CurrentManagedThreadId] = FpRounding.Get();
                    all.Signal();
                    all.Wait();
                });
            Task.WaitAll(work);
        }
        int changed = observed.Values.Count(r => r != FpRounding.ToNearest);
        report.WriteLine($"pool-threads {observed.Count} changed {changed}");
        return 0;
    }
}
