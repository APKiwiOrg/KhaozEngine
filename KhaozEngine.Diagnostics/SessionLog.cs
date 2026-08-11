using System;
using System.IO;

namespace KhaozEngine.Diagnostics;

/// <summary>
/// One-call session-log + crash-capture bootstrap. A game head runs <see cref="Configure(SessionLogOptions)"/>
/// (or the convenience overload) exactly once at its process entry point and gets the whole
/// per-launch-log-file shape: prune old session files, open one fresh timestamped
/// <c>session-{yyyyMMdd-HHmmss}.log</c>, add a console sink alongside it, adopt the pair as the ambient
/// <see cref="Log"/> manager, install <see cref="CrashHandler"/> so an unhandled/unobserved exception flushes as
/// <see cref="LogLevel.Fatal"/>, and write one self-identifying startup line carrying the (optional) game build
/// version plus the engine version read straight off the engine assembly. Collapses the per-game bootstrap the
/// games used to hand-wire three different ways.
/// </summary>
/// <remarks>
/// <para>
/// This is the rich, category-tagged session log. It is orthogonal to the last-chance <see cref="CrashReport"/>
/// file that <c>GameApp</c> arms automatically for every game head: that file exists to catch a crash that
/// happens before (or without) any logging being configured, and it goes to an OS location beside the system's
/// own crash report rather than into the game's log directory. The two do not double-handle a crash into the
/// same file, they write to different destinations for different purposes (the crash file is the floor, this
/// session log is the record). Both an <see cref="AppDomain.UnhandledException"/> handler from
/// <see cref="CrashReport"/> and one from <see cref="CrashHandler"/> may be live at once, which is intentional
/// belt-and-suspenders, each writing its own file.
/// </para>
/// <para>
/// The single-file rotating shape some games used before (<c>game.log</c> -&gt; <c>game.prev.log</c>) is not a
/// mode here: it is one line of existing API - <c>new FileSink(new FileSinkOptions { Path = ..., PreviousPath =
/// ... })</c> - so a game that prefers it keeps building the <see cref="LoggerOptions"/> directly. This helper
/// standardises on the richer per-session shape (a timestamped file per launch, capped) that keeps a tester's
/// crash history rather than just the previous run.
/// </para>
/// </remarks>
public static class SessionLog
{
    /// <summary>Default number of session logs retained (matches the games' prior value).</summary>
    public const int DefaultMaxRetainedSessions = 10;

    /// <summary>
    /// Configures the ambient <see cref="Log"/> for a per-launch session log per <paramref name="options"/> and
    /// returns the full path of the session log file just opened.
    /// </summary>
    /// <param name="options">The bootstrap configuration. <see cref="SessionLogOptions.Directory"/> is required.</param>
    /// <returns>The full path of the session log file opened for this launch.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException"><see cref="SessionLogOptions.Directory"/> is null, empty, or whitespace.</exception>
    public static string Configure(SessionLogOptions options)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(options.Directory))
            throw new ArgumentException("SessionLogOptions.Directory is required.", nameof(options));

        string prefix = string.IsNullOrWhiteSpace(options.FilePrefix) ? "session" : options.FilePrefix;
        PruneOldSessionLogs(options.Directory, options.MaxRetainedSessions, prefix);

        string sessionLogPath = Path.Combine(options.Directory, $"{prefix}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.log");

        var loggerOptions = new LoggerOptions { MinimumLevel = options.MinimumLevel, DefaultCategory = options.DefaultCategory };
        loggerOptions.Sinks.Add(new FileSink(new FileSinkOptions { Path = sessionLogPath }));
        if (options.Console) loggerOptions.Sinks.Add(new ConsoleSink());
        Log.Configure(loggerOptions);
        if (options.InstallCrashHandler) CrashHandler.Install();

        string engineVersion = typeof(LogManager).Assembly.GetName().Version?.ToString(3) ?? "unknown";
        string build = string.IsNullOrWhiteSpace(options.BuildVersion) ? string.Empty : $"{options.BuildVersion} | ";
        Log.Info($"{options.ProcessLabel} | {build}KhaozEngine {engineVersion} | session log: {sessionLogPath}");
        return sessionLogPath;
    }

    /// <summary>
    /// Convenience overload for the common case: a directory, a process label, and (optionally) the game build
    /// version and retained-session count, with every other knob left at its default.
    /// </summary>
    /// <param name="directory">Directory the session log is created in (required).</param>
    /// <param name="processLabel">Process name for the startup identity line, e.g. <c>"MyGame.Server"</c>.</param>
    /// <param name="buildVersion">Optional game build/display version for the identity line.</param>
    /// <param name="maxRetainedSessions">Retained session-log count (default <see cref="DefaultMaxRetainedSessions"/>).</param>
    /// <returns>The full path of the session log file opened for this launch.</returns>
    public static string Configure(
        string directory,
        string processLabel,
        string? buildVersion = null,
        int maxRetainedSessions = DefaultMaxRetainedSessions)
        => Configure(new SessionLogOptions
        {
            Directory = directory,
            ProcessLabel = processLabel,
            BuildVersion = buildVersion,
            MaxRetainedSessions = maxRetainedSessions,
        });

    /// <summary>
    /// Keeps at most <paramref name="maxRetained"/> - 1 EXISTING <c>{prefix}-*.log</c> files (newest by last-write
    /// time), so the directory holds at most <paramref name="maxRetained"/> once <see cref="Configure(SessionLogOptions)"/>
    /// opens its own. Internal so the engine tests can exercise it directly without touching the ambient
    /// <see cref="Log"/>/<see cref="CrashHandler"/> global state. The sweep itself is
    /// <see cref="LogFilePruner.KeepNewest(string, int, string)"/>, shared with <see cref="CrashReport"/>, and is
    /// best-effort: any I/O
    /// failure (locked file, missing/unreadable directory) is swallowed, matching the sinks' "logging never
    /// throws" contract - an odd file-permission setup on a player's machine must not block the game from
    /// starting.
    /// </summary>
    internal static void PruneOldSessionLogs(string directory, int maxRetained, string prefix)
        => LogFilePruner.KeepNewest(directory, maxRetained, prefix);
}
