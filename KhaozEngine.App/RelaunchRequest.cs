using System;
using System.Collections.Generic;

namespace KhaozEngine.App;

/// <summary>
/// Describes a self-relaunch performed by <see cref="AppRelaunch.Restart"/>: how to start the fresh
/// instance and how to shut the current one down cleanly.
/// </summary>
public sealed class RelaunchRequest
{
    /// <summary>
    /// Invoked to shut the current app down through its normal cooperative exit path AFTER the successor
    /// has started (e.g. <c>AppWindow.Close</c> or <c>GameApp.Quit</c>), so save/dispose hooks still run.
    /// Leave null only if the caller drives its own exit; otherwise the current instance keeps running and
    /// the successor blocks waiting for it. <see cref="AppRelaunch.Restart"/> never force-terminates the
    /// process, so a clean shutdown must be requested here.
    /// </summary>
    public Action? RequestShutdown { get; init; }

    /// <summary>
    /// The arguments to launch the successor with, excluding the executable. Null (the default) carries
    /// the current process's own launch arguments forward, reproducing the same invocation. The
    /// predecessor-wait handshake is appended automatically and is not part of this list.
    /// </summary>
    public IReadOnlyList<string>? Arguments { get; init; }

    /// <summary>
    /// The executable to relaunch. Null (the default) uses the current process's own executable
    /// (<c>Environment.ProcessPath</c>). Override to point at a different binary.
    /// </summary>
    public string? ExecutablePath { get; init; }

    /// <summary>The working directory for the successor. Null inherits the current one.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Whether the successor should wait for this process to exit before it proceeds. True (the default)
    /// appends the predecessor-wait handshake so the fresh boot blocks in <see cref="AppRelaunch.AwaitPredecessor"/>
    /// until this process is fully gone, avoiding a race on files this process writes during shutdown (a
    /// save file). Set false only when the successor shares no files with this process.
    /// </summary>
    public bool WaitForPredecessorExit { get; init; } = true;
}

/// <summary>The outcome of an <see cref="AppRelaunch.Restart"/> call.</summary>
public enum RelaunchResult
{
    /// <summary>The successor was started and a clean shutdown of the current app was requested.</summary>
    Started,

    /// <summary>
    /// No executable could be resolved (no override given and <c>Environment.ProcessPath</c> was null), so
    /// nothing was started and the current app was left running rather than shut down with no successor.
    /// </summary>
    ExecutableUnresolved,

    /// <summary>
    /// The successor failed to start (the OS refused to launch it). The current app was left running - it
    /// is never shut down unless the successor actually started.
    /// </summary>
    StartFailed,
}

/// <summary>
/// The result of <see cref="AppRelaunch.AwaitPredecessor"/>: whether a predecessor was waited on, whether
/// it had exited by the time the wait returned, and the launch arguments with the handshake token removed.
/// </summary>
public readonly struct PredecessorWait
{
    /// <summary>Constructs a result.</summary>
    public PredecessorWait(bool waitPerformed, bool predecessorExited, IReadOnlyList<string> arguments)
    {
        WaitPerformed = waitPerformed;
        PredecessorExited = predecessorExited;
        Arguments = arguments;
    }

    /// <summary>
    /// True when a predecessor-wait handshake was present in the arguments and a wait was performed. False
    /// on a normal boot (no handshake), where the call is a fast no-op.
    /// </summary>
    public bool WaitPerformed { get; }

    /// <summary>
    /// True when the predecessor had exited (or there was none to wait for); false only when the wait timed
    /// out while the predecessor was still alive. A false here means a file the predecessor held may not yet
    /// be released.
    /// </summary>
    public bool PredecessorExited { get; }

    /// <summary>
    /// The launch arguments with the predecessor-wait handshake token stripped, safe to forward into the
    /// app's own option parsing. Equal to the input when no handshake was present.
    /// </summary>
    public IReadOnlyList<string> Arguments { get; }
}
