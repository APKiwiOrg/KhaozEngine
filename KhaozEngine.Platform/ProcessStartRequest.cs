using System;
using System.Collections.Generic;

namespace KhaozEngine.Platform;

/// <summary>
/// A request to start a process through <see cref="IProcessControl.StartDetached"/>. Each argument in
/// <see cref="Arguments"/> is passed as a separate argv entry (via <c>ProcessStartInfo.ArgumentList</c>),
/// so there is no shell-quoting or splitting to reason about.
/// </summary>
public sealed class ProcessStartRequest
{
    /// <summary>The executable to run (the full path is used for a self-relaunch).</summary>
    public required string FileName { get; init; }

    /// <summary>The arguments, each a separate argv entry. Defaults to none.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();

    /// <summary>The working directory for the new process. Null inherits the current one.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Whether to start through the OS shell. Defaults to true, matching the proven self-relaunch idiom
    /// (a shell-executed launch starts a genuine new top-level instance on both Windows and a macOS
    /// <c>.app</c> bundle's inner binary). Set false to start the binary directly with redirected handles.
    /// </summary>
    public bool UseShellExecute { get; init; } = true;
}
