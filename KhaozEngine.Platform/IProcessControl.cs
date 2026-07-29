using System.Collections.Generic;

namespace KhaozEngine.Platform;

/// <summary>
/// The OS process operations behind cooperative self-relaunch: reading the running process's own
/// identity, spawning a fresh detached instance, and waiting for a process to exit. Abstracted as a
/// seam so the relaunch orchestration (<c>KhaozEngine.App.AppRelaunch</c>) is headless-testable with
/// no real fork; <see cref="ProcessControl.System"/> is the real implementation the shipping desktop
/// heads use.
/// </summary>
/// <remarks>
/// This is the generalized form of the parent-pid-wait pattern the desktop auto-updater uses
/// (<c>KhaozEngine.Updates</c>'s <c>IUpdaterEnvironment.WaitForParentExit</c> / <c>Relaunch</c>): a
/// freshly-started successor must not touch files the predecessor still holds (the game writes its
/// save during shutdown) until that predecessor has actually exited. The updater keeps its own tuned
/// environment seam (antivirus/image-race retry, elevation, relocation, and a deliberately
/// non-truthful <c>IsProcessAlive</c> on POSIX), so it is not retrofitted onto this primitive; the
/// two share the pattern, not the code.
/// </remarks>
public interface IProcessControl
{
    /// <summary>
    /// The full path of the running executable (<see cref="System.Environment.ProcessPath"/>), or null
    /// when the host cannot resolve one. A relaunch cannot proceed without it, so a null result must
    /// leave the current app running rather than shut it down with no successor.
    /// </summary>
    string? CurrentExecutablePath { get; }

    /// <summary>The running process's own id (<see cref="System.Environment.ProcessId"/>).</summary>
    int CurrentProcessId { get; }

    /// <summary>
    /// The MANAGED entry path (<c>Environment.GetCommandLineArgs()[0]</c>), which is the application's
    /// <c>.dll</c> in both shipped shapes, or null when it cannot be resolved.
    /// <para>
    /// It matters for exactly one of them. Launched as a self-contained apphost,
    /// <see cref="CurrentExecutablePath"/> is the app and this is redundant. Launched as
    /// <c>dotnet &lt;app&gt;.dll</c>, <see cref="CurrentExecutablePath"/> is the shared dotnet MUXER and the
    /// dll is the only thing that says WHICH app to run, yet <see cref="CurrentCommandLineArguments"/> drops
    /// it along with element 0. Relaunching on the muxer without it would start a bare <c>dotnet</c> holding
    /// the app's arguments, so <c>AppRelaunch</c> reads this to put it back.
    /// </para>
    /// <para>
    /// Defaulted to null so an existing external implementation of this interface keeps compiling. A null
    /// simply means the muxer shape cannot be repaired, which is the pre-17.23.0 behaviour.
    /// </para>
    /// </summary>
    string? CurrentManagedEntryPath => null;

    /// <summary>
    /// The command-line arguments the running process was launched with, excluding the executable
    /// itself (i.e. <see cref="System.Environment.GetCommandLineArgs"/> without element 0). Carried
    /// forward to the successor by default so a relaunch reproduces the same invocation.
    /// </summary>
    IReadOnlyList<string> CurrentCommandLineArguments { get; }

    /// <summary>
    /// Starts a new process from <paramref name="request"/> and detaches from it (fire-and-forget): the
    /// caller does not wait on or hold a handle to the child, so it survives the caller's own exit. This
    /// is how the relaunch successor is spawned before the current app shuts down.
    /// </summary>
    void StartDetached(ProcessStartRequest request);

    /// <summary>
    /// Blocks until the process with <paramref name="processId"/> exits or <paramref name="timeoutMilliseconds"/>
    /// elapses. Returns true when the process was observed to exit (or was already gone), false when the
    /// timeout elapsed first while it was still running. A process id that no longer exists returns true
    /// immediately (nothing to wait for).
    /// </summary>
    bool WaitForProcessExit(int processId, int timeoutMilliseconds);
}
