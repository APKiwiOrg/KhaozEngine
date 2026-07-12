using System;
using System.Collections.Generic;
using System.Globalization;
using KhaozEngine.Platform;

namespace KhaozEngine.App;

/// <summary>
/// Forces a clean restart of the running application: starts a fresh instance of the current executable
/// and then asks the current one to shut down through its normal cooperative exit path (never a hard
/// process kill that would skip save/dispose hooks). Consumers use this to apply changes that only a
/// fresh boot can pick up - a signed-out session that wiped the local save, a restored cloud save that
/// must be loaded from scratch, or a setting that only takes effect at startup.
/// </summary>
/// <remarks>
/// <para>
/// The successor is started BEFORE the current app shuts down, carrying a predecessor-wait handshake so
/// it blocks early in its own boot (see <see cref="AwaitPredecessor"/>) until this process has fully
/// exited. That ordering is what makes the seam safe when the current app writes its save file during
/// shutdown: the fresh instance never reads or overwrites a file the old one still holds.
/// </para>
/// <para>
/// The process operations go through <see cref="IProcessControl"/> so the whole flow is headless-testable
/// with a fake; the shipping path uses <see cref="ProcessControl.System"/>. This is the generalized form
/// of the auto-updater's parent-pid-wait relaunch (<c>KhaozEngine.Updates</c>); the updater keeps its own
/// tuned environment (antivirus/image-race retry, elevation, relocation), so the two share the pattern
/// rather than the code.
/// </para>
/// </remarks>
public static class AppRelaunch
{
    /// <summary>
    /// The argument flag that carries the predecessor process id from a relaunch to its successor. It is
    /// followed by the predecessor's pid as the next argument. Public so a consumer can recognise and skip
    /// it if it parses arguments before calling <see cref="AwaitPredecessor"/>.
    /// </summary>
    public const string PredecessorWaitFlag = "--ke-await-predecessor";

    /// <summary>
    /// Default cap on how long <see cref="AwaitPredecessor"/> blocks for the predecessor to exit. A generous
    /// bound: a clean shutdown is near-instant, and if the predecessor hangs the successor still boots rather
    /// than wedging forever (it just risks the file race the handshake normally prevents).
    /// </summary>
    public static readonly TimeSpan DefaultPredecessorTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Starts a fresh instance of the application, then requests a clean shutdown of the current one.
    /// </summary>
    /// <param name="request">How to launch the successor and shut the current app down.</param>
    /// <param name="process">
    /// The process seam to drive; null uses <see cref="ProcessControl.System"/>. Injected in tests.
    /// </param>
    /// <returns>
    /// <see cref="RelaunchResult.Started"/> when the successor launched and shutdown was requested;
    /// <see cref="RelaunchResult.ExecutableUnresolved"/> or <see cref="RelaunchResult.StartFailed"/> when it
    /// could not, in which case the current app is left running and <see cref="RelaunchRequest.RequestShutdown"/>
    /// is NOT invoked.
    /// </returns>
    public static RelaunchResult Restart(RelaunchRequest request, IProcessControl? process = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        IProcessControl pc = process ?? ProcessControl.System;

        string? executable = request.ExecutablePath ?? pc.CurrentExecutablePath;
        if (string.IsNullOrEmpty(executable))
        {
            // Cannot relaunch without an executable path: leave the app running rather than shut it down
            // with no successor to take its place.
            return RelaunchResult.ExecutableUnresolved;
        }

        var arguments = new List<string>(request.Arguments ?? pc.CurrentCommandLineArguments);
        // Drop any handshake already present in the forwarded arguments so a relaunch-of-a-relaunch does not
        // accumulate flags or carry a stale predecessor pid, then append a fresh one for THIS process.
        RemovePredecessorFlag(arguments);
        if (request.WaitForPredecessorExit)
        {
            arguments.Add(PredecessorWaitFlag);
            arguments.Add(pc.CurrentProcessId.ToString(CultureInfo.InvariantCulture));
        }

        try
        {
            pc.StartDetached(new ProcessStartRequest
            {
                FileName = executable,
                Arguments = arguments,
                WorkingDirectory = request.WorkingDirectory,
            });
        }
        catch (Exception)
        {
            // The OS refused to start the successor: do not shut down, or the player is left with nothing
            // running. The caller can surface the failed result and keep the current session alive.
            return RelaunchResult.StartFailed;
        }

        // The successor is up and (by default) waiting on this process's exit. Now trigger the normal
        // cooperative shutdown so the frame loop unwinds and save/dispose hooks run before we exit.
        request.RequestShutdown?.Invoke();
        return RelaunchResult.Started;
    }

    /// <summary>
    /// Called early in a fresh boot, before the app touches any file a predecessor might still hold (its
    /// save file): if the launch arguments carry a predecessor-wait handshake (from <see cref="Restart"/>),
    /// blocks until that predecessor exits or <paramref name="timeout"/> elapses. On a normal boot with no
    /// handshake it is a fast no-op. Safe to call unconditionally at the top of <c>Main</c>.
    /// </summary>
    /// <param name="arguments">The process launch arguments (typically <c>Main</c>'s <c>args</c>).</param>
    /// <param name="timeout">How long to wait; null uses <see cref="DefaultPredecessorTimeout"/>.</param>
    /// <param name="process">The process seam to drive; null uses <see cref="ProcessControl.System"/>.</param>
    /// <returns>
    /// The wait outcome and the arguments with the handshake token stripped (forward
    /// <see cref="PredecessorWait.Arguments"/> into your own option parsing).
    /// </returns>
    public static PredecessorWait AwaitPredecessor(
        IReadOnlyList<string> arguments,
        TimeSpan? timeout = null,
        IProcessControl? process = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        IProcessControl pc = process ?? ProcessControl.System;

        var cleaned = new List<string>(arguments);
        if (!RemovePredecessorFlag(cleaned, out int predecessorPid))
        {
            // Normal boot: no predecessor to wait for, so it is safe to proceed immediately.
            return new PredecessorWait(waitPerformed: false, predecessorExited: true, cleaned);
        }

        double ms = (timeout ?? DefaultPredecessorTimeout).TotalMilliseconds;
        int timeoutMs = ms >= int.MaxValue ? int.MaxValue : (int)Math.Max(0, ms);
        bool exited = pc.WaitForProcessExit(predecessorPid, timeoutMs);
        return new PredecessorWait(waitPerformed: true, predecessorExited: exited, cleaned);
    }

    /// <summary>Removes every predecessor-wait handshake from <paramref name="arguments"/> in place.</summary>
    static void RemovePredecessorFlag(List<string> arguments) => RemovePredecessorFlag(arguments, out _);

    /// <summary>
    /// Removes every predecessor-wait handshake (the flag and, when present, the integer pid that follows it)
    /// from <paramref name="arguments"/> in place. Reports whether a valid pid was found and returns the
    /// first one via <paramref name="predecessorPid"/>. A dangling flag with no valid pid is still stripped.
    /// </summary>
    static bool RemovePredecessorFlag(List<string> arguments, out int predecessorPid)
    {
        predecessorPid = 0;
        bool found = false;
        for (int i = 0; i < arguments.Count;)
        {
            if (!string.Equals(arguments[i], PredecessorWaitFlag, StringComparison.Ordinal))
            {
                i++;
                continue;
            }
            arguments.RemoveAt(i); // remove the flag
            if (i < arguments.Count
                && int.TryParse(arguments[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid))
            {
                if (!found)
                {
                    predecessorPid = pid;
                    found = true;
                }
                arguments.RemoveAt(i); // remove its pid value
            }
            // Leave i where it is: the next element has shifted into this slot.
        }
        return found;
    }
}
