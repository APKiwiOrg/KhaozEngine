using System;
using System.IO;

namespace KhaozEngine.Updates;

/// <summary>
/// The reusable updater-shim entry. A game's external updater exe becomes a one-liner:
/// <c>return KhaozEngine.Updates.UpdaterShim.Main(args);</c>. It opens an autoflush log next to the
/// apply-config file and forwards to <see cref="UpdateApplier.Run"/> with a real
/// <see cref="SystemUpdaterEnvironment"/>. The apply-config contract stays engine-owned, so the writer
/// (UpdateService) and reader (this shim) never drift.
/// </summary>
public static class UpdaterShim
{
    /// <summary>
    /// The log path: <c>updater.log</c> beside the apply-config file passed as <c>args[1]</c> (the value
    /// after <c>--apply</c>); the current directory when no path is present.
    /// </summary>
    public static string ResolveLogPath(string[] args)
    {
        string baseRef = args.Length > 1 ? args[1] : ".";
        string dir = Path.GetDirectoryName(baseRef) ?? ".";
        if (dir.Length == 0) dir = ".";
        return Path.Combine(dir, "updater.log");
    }

    /// <summary>Opens the log, runs the staged apply, returns the process exit code.</summary>
    public static int Main(string[] args)
    {
        string logPath = ResolveLogPath(args);
        using var log = new StreamWriter(logPath, append: false) { AutoFlush = true };
        return UpdateApplier.Run(args, new SystemUpdaterEnvironment(msg =>
        {
            try { Console.WriteLine(msg); } catch { /* no console attached (GUI subsystem) */ }
            log.WriteLine(msg);
        }));
    }
}
