namespace KhaozEngine.Diagnostics;

/// <summary>
/// How <see cref="CrashReport"/> writes its file: where it goes, what names it, and how many are kept.
/// Every field has a working default, so <c>new CrashReportOptions { ProcessLabel = "MyGame" }</c> is a
/// complete configuration.
/// </summary>
public sealed class CrashReportOptions
{
    /// <summary>
    /// Directory the crash file is written to. Null, empty or whitespace means
    /// <see cref="CrashReport.DefaultDirectory"/>, which is the OS location a tester already looks in for
    /// crashes (on macOS that is <c>~/Library/Logs/KhaozEngine</c>, beside the system's own
    /// <c>DiagnosticReports</c> folder). The directory is created on demand.
    /// </summary>
    public string? Directory { get; set; }

    /// <summary>
    /// Name of the crashing process, used both in the file name and in the report's header line. Defaults to
    /// <c>game</c>. Characters a file name cannot carry are replaced.
    /// <para>
    /// TWO LABELS THAT SANITISE IDENTICALLY SHARE ONE RETENTION POOL, so choose distinct ones. A space becomes
    /// a hyphen, which means <c>My Game</c> and <c>My-Game</c> produce the same file-name stem and each head's
    /// <see cref="MaxRetainedReports"/> then counts the other's reports. This is by construction rather than an
    /// oversight: once both have written the same stem there is nothing left in the name to tell them apart. A
    /// label that merely STARTS with another's is fine, because retention matches the whole generated shape.
    /// </para>
    /// </summary>
    public string ProcessLabel { get; set; } = "game";

    /// <summary>
    /// How many crash files are kept in <see cref="Directory"/> for this <see cref="ProcessLabel"/>, oldest
    /// deleted first. Defaults to <see cref="CrashReport.DefaultMaxRetainedReports"/>.
    /// </summary>
    public int MaxRetainedReports { get; set; } = CrashReport.DefaultMaxRetainedReports;

    /// <summary>
    /// When true (the default), a <see cref="System.Threading.Tasks.TaskScheduler.UnobservedTaskException"/>
    /// is written as well as an <see cref="System.AppDomain.UnhandledException"/>. An unobserved task
    /// exception does not terminate the process, so this is the noisier of the two hooks, and
    /// <see cref="MaxRetainedReports"/> is what bounds it. <see cref="CrashReport"/> never marks such an
    /// exception observed: whether the process treats it as handled stays the game's decision.
    /// </summary>
    public bool IncludeUnobservedTaskExceptions { get; set; } = true;
}
