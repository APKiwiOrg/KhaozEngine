using System;
using System.IO;
using System.Threading;

namespace KhaozEngine.Game
{
    /// <summary>
    /// Last-chance crash net for a Windows game head running with NO console - a <c>WinExe</c> launched from
    /// Explorer/Start where an uncaught startup exception would otherwise vanish with no window and no terminal.
    /// <see cref="GameApp"/> installs it automatically only in that case (see the ctor). It is the floor, not a
    /// replacement: a terminal launch shows the exception on stderr (the console being attached), and a game that
    /// wires <c>KhaozEngine.Diagnostics.CrashHandler</c> gets its richer, category-tagged <c>game.log</c> as well.
    /// This just guarantees the crash is written SOMEWHERE discoverable even before (or without) any of that.
    /// Writes the fatal exception to a timestamped file under the per-user local app-data dir. Best effort and
    /// never throws (it runs on the runtime crash path, where a throw would be catastrophic).
    /// </summary>
    internal static class StartupCrashLog
    {
        private static int installed;

        /// <summary>
        /// Wire an <see cref="AppDomain.UnhandledException"/> handler that appends the fatal exception to a crash
        /// file. Idempotent (installs once per process). <paramref name="appName"/> names the file so a publisher
        /// with several games keeps them apart.
        /// </summary>
        internal static void InstallForNoConsole(string? appName)
        {
            if (Interlocked.Exchange(ref installed, 1) != 0) return;
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                try { Write(appName, e.ExceptionObject as Exception, e.ExceptionObject); }
                catch { /* crash path: never throw */ }
            };
        }

        private static void Write(string? appName, Exception? exception, object? raw)
        {
            string dir = ResolveCrashDir();
            Directory.CreateDirectory(dir);
            string safe = Sanitize(appName);
            string path = Path.Combine(dir, $"{safe}-crash-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.log");
            string body = exception?.ToString() ?? raw?.ToString() ?? "Unknown fatal error.";
            File.WriteAllText(
                path,
                $"[{DateTime.UtcNow:o}] Fatal unhandled exception in {(string.IsNullOrWhiteSpace(appName) ? "game" : appName)}:"
                    + Environment.NewLine + body + Environment.NewLine);
        }

        private static string ResolveCrashDir()
        {
            string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(baseDir)) baseDir = Path.GetTempPath();
            return Path.Combine(baseDir, "KhaozEngine", "crash");
        }

        private static string Sanitize(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "game";
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }
    }
}
