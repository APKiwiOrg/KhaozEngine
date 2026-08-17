namespace KhaozEngine.Diagnostics;

/// <summary>
/// Configuration for <see cref="SessionLog.Configure(SessionLogOptions)"/> - the one-call session-log +
/// crash-capture bootstrap a game runs once per process entry point.
/// </summary>
public sealed class SessionLogOptions
{
    /// <summary>
    /// Directory the session log file is created in (required). The game owns this path - typically a
    /// <c>logs</c> subdir of its <c>KhaozEngine.App.AppDataPaths.BaseDirectory</c>. Created on open if absent.
    /// </summary>
    public string Directory { get; set; } = string.Empty;

    /// <summary>
    /// Name used in the startup identity line, e.g. <c>"MyGame"</c> or <c>"MyGame.Server"</c>, so one publisher's
    /// several heads stay distinguishable in a handed-over log.
    /// </summary>
    public string ProcessLabel { get; set; } = "App";

    /// <summary>
    /// Session log files beyond this count (newest by last-write time kept) are pruned on startup, so a
    /// long-lived install does not accumulate logs without bound. Clamped to at least 1. Default 10.
    /// </summary>
    public int MaxRetainedSessions { get; set; } = 10;

    /// <summary>
    /// Base name of each per-launch log file: the file is <c>{FilePrefix}-{yyyyMMdd-HHmmss}.log</c> and prune
    /// only ever touches files matching <c>{FilePrefix}-*.log</c>. Default <c>"session"</c>.
    /// </summary>
    public string FilePrefix { get; set; } = "session";

    /// <summary>Minimum level for the configured manager. Runtime-adjustable via <see cref="Log.MinimumLevel"/>. Default <see cref="LogLevel.Info"/>.</summary>
    public LogLevel MinimumLevel { get; set; } = LogLevel.Info;

    /// <summary>Category used by the <see cref="Log"/> facade + the startup identity line. Default <c>"Boot"</c>.</summary>
    public string DefaultCategory { get; set; } = "Boot";

    /// <summary>When true, a <see cref="ConsoleSink"/> is attached alongside the file sink. Default true.</summary>
    public bool Console { get; set; } = true;

    /// <summary>
    /// When true, <see cref="CrashHandler.Install()"/> is called so an unhandled or unobserved-task exception
    /// lands as a <see cref="LogLevel.Fatal"/> entry and is flushed before the process dies. Default true.
    /// <para>That install resolves the configured manager when the crash is reported rather than capturing this
    /// one, so a game that later calls <see cref="Log.Configure(LoggerOptions)"/> to swap its sink set keeps its
    /// crash line, in the new manager, with no re-install (#633).</para>
    /// </summary>
    public bool InstallCrashHandler { get; set; } = true;

    /// <summary>
    /// Optional game build/display version added to the startup identity line (the engine version is read off
    /// the engine assembly and always included). Null/blank omits the game-version segment.
    /// </summary>
    public string? BuildVersion { get; set; }
}
