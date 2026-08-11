namespace KhaozEngine.Diagnostics;

/// <summary>
/// WHICH SIGNAL PRODUCED A REPORT, and therefore which file-name stem and which retention pool it belongs to.
///
/// <para><b>THE TWO ARE DELIBERATELY NOT THE SAME ARTIFACT.</b> An <see cref="System.AppDomain.UnhandledException"/>
/// is the process dying. A <see cref="System.Threading.Tasks.TaskScheduler.UnobservedTaskException"/> is a
/// faulted task nobody awaited, arriving from the FINALIZER thread at a garbage collection, long after the code
/// that produced it ran and usually while the game is still running perfectly well. Writing both under one stem
/// meant "the crash file" could easily be a task fault from ten minutes earlier, and a tester asked for the crash
/// file would hand over the wrong artifact without either of them noticing. Separate stems keep the crash file
/// meaning the crash, and separate pools stop a fault storm from evicting the crash that matters.</para>
/// </summary>
public enum CrashReportKind
{
    /// <summary>An unhandled exception: the process is going down. Written under <c>{label}-crash-</c>.</summary>
    Unhandled,

    /// <summary>
    /// A task exception nobody observed, raised from the finalizer thread. Written under
    /// <c>{label}-taskfault-</c>, with its own retention pool.
    /// </summary>
    UnobservedTask,
}
