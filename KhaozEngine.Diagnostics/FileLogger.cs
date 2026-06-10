using System;
using System.IO;
using System.Text;

namespace KhaozEngine.Diagnostics;

/// <summary>
/// Thread-safe file logger that writes timestamped, level-tagged messages to a caller-supplied
/// log file. On <see cref="Initialize"/> the previous session's log is rotated aside (when a
/// previous-log path is supplied), so the most recent run is always in the primary file.
/// Designed for diagnosing silent crashes and startup failures. Pure <c>System.IO</c> — no
/// MonoGame dependency.
/// </summary>
/// <remarks>
/// The log file location is the caller's concern: each game resolves its own app-data path and
/// passes the resolved paths to <see cref="Initialize"/>. Every method swallows IO failures so
/// logging can never crash the game.
/// </remarks>
public sealed class FileLogger : IDisposable
{
    private readonly object gate = new();
    private string? logPath;
    private StreamWriter? writer;

    /// <summary>Full path to the active log file, or <c>null</c> if not initialized.</summary>
    public string? LogPath
    {
        get { lock (gate) { return logPath; } }
    }

    /// <summary>
    /// Opens (or creates) the log file at <paramref name="logFilePath"/>. When
    /// <paramref name="previousLogFilePath"/> is supplied and a log already exists, the existing
    /// log is copied there first so the most recent run is always in the primary file.
    /// Safe to call multiple times; calls after the first are ignored until <see cref="Shutdown"/>.
    /// </summary>
    /// <param name="logFilePath">Destination path for the active log file.</param>
    /// <param name="previousLogFilePath">
    /// Optional path the existing log is rotated to. Pass <c>null</c> to skip rotation.
    /// </param>
    public void Initialize(string logFilePath, string? previousLogFilePath = null)
    {
        lock (gate)
        {
            if (writer is not null)
            {
                return;
            }

            try
            {
                // Ensure directory exists.
                string? logDir = Path.GetDirectoryName(logFilePath);
                if (!string.IsNullOrWhiteSpace(logDir))
                {
                    Directory.CreateDirectory(logDir);
                }

                // Rotate previous log if a target was supplied.
                if (!string.IsNullOrWhiteSpace(previousLogFilePath) && File.Exists(logFilePath))
                {
                    try
                    {
                        File.Copy(logFilePath, previousLogFilePath, overwrite: true);
                    }
                    catch
                    {
                        // Best-effort rotation.
                    }
                }

                writer = new StreamWriter(logFilePath, append: false, Encoding.UTF8)
                {
                    AutoFlush = true
                };
                logPath = logFilePath;

                writer.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [INFO] Log started");
            }
            catch
            {
                // If we cannot open a log file, fall back silently.
                writer = null;
                logPath = null;
            }
        }
    }

    /// <summary>Logs an informational message.</summary>
    public void Info(string message) => Write("INFO", message);

    /// <summary>Logs a warning message.</summary>
    public void Warn(string message) => Write("WARN", message);

    /// <summary>Logs an error message.</summary>
    public void Error(string message) => Write("ERROR", message);

    /// <summary>Logs an exception with a context message.</summary>
    public void Error(string message, Exception ex) => Write("ERROR", $"{message}: {ex}");

    /// <summary>Flushes and closes the log file.</summary>
    public void Shutdown()
    {
        lock (gate)
        {
            if (writer is null)
            {
                return;
            }

            try
            {
                writer.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [INFO] Log closed");
                writer.Flush();
                writer.Dispose();
            }
            catch
            {
                // Best-effort.
            }

            writer = null;
            // logPath is intentionally retained so callers can still report where the log lives.
        }
    }

    /// <summary>Closes the log file. Equivalent to <see cref="Shutdown"/>.</summary>
    public void Dispose() => Shutdown();

    private void Write(string level, string message)
    {
        lock (gate)
        {
            try
            {
                writer?.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}");
            }
            catch
            {
                // Never let logging crash the game.
            }
        }
    }
}
