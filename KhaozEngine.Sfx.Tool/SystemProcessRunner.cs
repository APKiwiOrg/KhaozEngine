using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Sfx;

/// <summary>Real <see cref="IProcessRunner"/> over <see cref="System.Diagnostics.Process"/>. Both output pipes are
/// drained concurrently and the wait is bounded, so neither a chatty child nor a wedged one can hang a bake.</summary>
public sealed class SystemProcessRunner : IProcessRunner
{
    /// <summary>How long <see cref="Run"/> waits for a child before killing it, when no other value is given.
    /// Generous next to any single encode this tool asks for, and far short of a bake that never returns.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    readonly TimeSpan _timeout;

    /// <summary>A runner that gives each child <see cref="DefaultTimeout"/> to finish.</summary>
    public SystemProcessRunner() : this(DefaultTimeout) { }

    /// <summary>A runner that gives each child <paramref name="timeout"/> to finish. Pass
    /// <see cref="Timeout.InfiniteTimeSpan"/> to wait forever, which only a caller with its own outer bound
    /// should do.</summary>
    public SystemProcessRunner(TimeSpan timeout)
    {
        if (timeout != Timeout.InfiniteTimeSpan && timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "the timeout must be positive");
        _timeout = timeout;
    }

    /// <inheritdoc/>
    public bool ToolExists(string exe)
    {
        string? pathVar = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVar)) return false;

        bool isWindows = OperatingSystem.IsWindows();
        foreach (string dir in pathVar.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            if (File.Exists(Path.Combine(dir, exe))) return true;
            if (isWindows && (File.Exists(Path.Combine(dir, exe + ".exe")) || File.Exists(Path.Combine(dir, exe + ".cmd"))))
                return true;
        }
        return false;
    }

    /// <inheritdoc/>
    /// <exception cref="TimeoutException">The child did not finish within this runner's timeout. It is killed,
    /// together with anything it started, before the exception is thrown.</exception>
    public ProcessResult Run(string exe, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"failed to start {exe}");
        // Both pipes are drained CONCURRENTLY. Reading them one after the other deadlocks as soon as the child
        // fills the buffer of the pipe nobody is emptying (commonly 64 KB): the child blocks writing stderr while
        // this side blocks reading stdout, and neither ever moves. ffmpeg, the tool this actually launches, is
        // exactly the sort to write a lot of stderr on a bad or unusual input file, which is the case a bake
        // operator most wants a message about rather than a hang.
        Task<string> stdout = proc.StandardOutput.ReadToEndAsync();
        Task<string> stderr = proc.StandardError.ReadToEndAsync();
        int budgetMs = _timeout == Timeout.InfiniteTimeSpan ? Timeout.Infinite : (int)_timeout.TotalMilliseconds;

        // The reads finish when the child closes its pipes, so waiting on them first also covers a child that
        // exited while a grandchild still holds the write end open. Then the exit itself, which is immediate by
        // that point. A blown budget kills the tree rather than leaving an orphan wedged against a full pipe.
        if (!Task.WhenAll(stdout, stderr).Wait(budgetMs) || !proc.WaitForExit(budgetMs))
        {
            KillTree(proc);
            throw new TimeoutException($"{exe} did not finish within {_timeout} and was killed");
        }

        return new ProcessResult(proc.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }

    // Best effort: the child may have exited in the gap between the timeout and the kill, and a platform may
    // refuse the tree walk. Neither is worth losing the TimeoutException the caller is about to see.
    static void KillTree(Process proc)
    {
        try { proc.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (NotSupportedException) { }
        catch (AggregateException) { }
        catch (Win32Exception) { }
    }
}
