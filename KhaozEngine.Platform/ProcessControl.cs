using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace KhaozEngine.Platform;

/// <summary>
/// The real <see cref="IProcessControl"/>: reads the running process's identity from
/// <see cref="Environment"/> and drives spawn/wait through <see cref="Process"/>. Use the shared
/// <see cref="System"/> instance; construct one directly only if a distinct instance is wanted.
/// </summary>
public sealed class ProcessControl : IProcessControl
{
    /// <summary>The shared instance used by the shipping self-relaunch path.</summary>
    public static ProcessControl System { get; } = new ProcessControl();

    /// <inheritdoc/>
    public string? CurrentExecutablePath => Environment.ProcessPath;

    /// <inheritdoc/>
    public int CurrentProcessId => Environment.ProcessId;

    /// <inheritdoc/>
    public string? CurrentManagedEntryPath
    {
        get
        {
            // Element 0 is the managed entry assembly in BOTH shapes: the app's .dll for a self-contained
            // apphost as well as for `dotnet <app>.dll`. Only the muxer case needs it, and AppRelaunch is what
            // decides that, so this stays a plain accessor with no shape-sniffing of its own.
            string[] all = Environment.GetCommandLineArgs();
            return all.Length > 0 ? all[0] : null;
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<string> CurrentCommandLineArguments
    {
        get
        {
            // GetCommandLineArgs()[0] is the executable; the launch arguments are the rest.
            string[] all = Environment.GetCommandLineArgs();
            if (all.Length <= 1) return Array.Empty<string>();
            var args = new string[all.Length - 1];
            Array.Copy(all, 1, args, 0, args.Length);
            return args;
        }
    }

    /// <inheritdoc/>
    public void StartDetached(ProcessStartRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var info = new ProcessStartInfo
        {
            FileName = request.FileName,
            UseShellExecute = request.UseShellExecute,
        };
        if (request.WorkingDirectory is not null)
        {
            info.WorkingDirectory = request.WorkingDirectory;
        }
        foreach (string arg in request.Arguments)
        {
            info.ArgumentList.Add(arg);
        }

        // Fire-and-forget: dispose our handle immediately. The child is a detached top-level process
        // (UseShellExecute) and keeps running after this process exits, which is the whole point of a
        // relaunch - the successor outlives the predecessor.
        Process.Start(info)?.Dispose();
    }

    /// <inheritdoc/>
    public bool WaitForProcessExit(int processId, int timeoutMilliseconds)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return process.WaitForExit(timeoutMilliseconds);
        }
        catch (ArgumentException)
        {
            // No process with that id: already gone, nothing to wait for.
            return true;
        }
        catch (InvalidOperationException)
        {
            // The process exited and its association was torn down between lookup and wait: also gone.
            return true;
        }
    }
}
