using System;
using System.IO;

namespace KhaozEngine.Updates;

/// <summary>
/// The reusable updater-shim entry. A game's external updater exe becomes a one-liner:
/// <c>return KhaozEngine.Updates.UpdaterShim.Main(args);</c>. It opens an autoflush log next to the
/// apply-config file and forwards to <see cref="UpdateApplier.Run(string[], IUpdaterEnvironment, System.Func{IUpdaterUi})"/>
/// with a real <see cref="SystemUpdaterEnvironment"/> and the per-OS progress-window factory
/// (<see cref="SystemUpdaterUi.CreateForCurrentOs"/>). The apply-config contract stays engine-owned, so
/// the writer (UpdateService) and reader (this shim) never drift, and the consumer's shim gains no
/// surface: the whole window is engine code driven by the config's optional <c>Ui</c> block.
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

    /// <summary>Opens the log, runs the staged apply (with the per-OS progress window), returns the exit code.</summary>
    public static int Main(string[] args)
    {
        // The shim ships as a Windows WinExe (Windows-subsystem) so applying an update never flashes a console
        // window over the game. That leaves the best-effort Console.WriteLine below with nowhere to go when a
        // developer runs the updater from a terminal, so attach the parent console first (no-op off Windows / for
        // a console exe / with no parent console; never throws). The file log is always written regardless.
        KhaozEngine.Platform.WindowsConsole.EnsureParentConsoleAttached();

        string logPath = ResolveLogPath(args);
        using var log = new StreamWriter(logPath, append: false) { AutoFlush = true };
        return UpdateApplier.Run(
            args,
            new SystemUpdaterEnvironment(msg =>
            {
                try { Console.WriteLine(msg); } catch { /* no console attached (GUI subsystem) */ }
                log.WriteLine(msg);
            }),
            SystemUpdaterUi.CreateForCurrentOs);
    }
}
