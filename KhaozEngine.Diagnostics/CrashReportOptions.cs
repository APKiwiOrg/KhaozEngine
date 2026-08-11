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
    /// deleted first. Defaults to <see cref="CrashReport.DefaultMaxRetainedReports"/>. Counted per stem, so
    /// crashes and unobserved task faults (see <see cref="IncludeUnobservedTaskExceptions"/>) each keep this
    /// many and a fault storm cannot evict the crash that matters.
    /// </summary>
    public int MaxRetainedReports { get; set; } = CrashReport.DefaultMaxRetainedReports;

    /// <summary>
    /// When true (THE DEFAULT), a <see cref="System.Threading.Tasks.TaskScheduler.UnobservedTaskException"/>
    /// is recorded as well as an <see cref="System.AppDomain.UnhandledException"/>. On by default because the
    /// alternative loses the evidence of a faulted task by default, which is the harder failure to get back:
    /// nothing else in the process records it at all.
    /// <para>
    /// IT IS A DIFFERENT EVENT AND IT GETS A DIFFERENT FILE. This one is raised from the FINALIZER thread when
    /// a faulted task is collected, so it arrives at a garbage collection rather than at the failure, usually
    /// long after the code that produced it ran and while the game is still running perfectly well. Those
    /// reports are written under <c>{ProcessLabel}-taskfault-</c> rather than <c>{ProcessLabel}-crash-</c>,
    /// with their own retention pool, so "the crash file" still means the crash and a tester cannot collect
    /// the wrong artifact. It is also the noisier of the two hooks, and its own
    /// <see cref="MaxRetainedReports"/> pool is what bounds it.
    /// </para>
    /// <para>
    /// <see cref="CrashReport"/> never marks such an exception observed: whether the process treats it as
    /// handled stays the game's decision.
    /// </para>
    /// </summary>
    public bool IncludeUnobservedTaskExceptions { get; set; } = true;
}
