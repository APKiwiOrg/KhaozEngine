using System;
using System.Threading.Tasks;

namespace KhaozEngine.Diagnostics;

/// <summary>
/// Wires process-level crash signals (<see cref="AppDomain.UnhandledException"/> and
/// <see cref="TaskScheduler.UnobservedTaskException"/>) into a log: writes a <see cref="LogLevel.Fatal"/> entry
/// under category <c>Crash</c> and flushes. On a terminating unhandled exception it also shuts the manager down
/// so the log file is closed cleanly.
///
/// <para><b>THE AMBIENT INSTALL RESOLVES THE MANAGER WHEN THE CRASH HAPPENS, NOT WHEN IT IS INSTALLED (#633).</b>
/// <see cref="Install()"/> arms the handlers and captures nothing, and the report reads
/// <see cref="Log.Current"/> on the crash path, the same volatile read every ambient logger does since #616. So
/// install order stopped mattering in both directions: installing BEFORE <see cref="Log.Configure(LoggerOptions)"/>
/// arms normally and starts reporting the moment configuration lands, and a later reconfigure moves the crash
/// line to the new manager with it.</para>
///
/// <para>What that fixes is the highest-value line the engine writes going missing without a trace.
/// <see cref="SessionLog"/> configures a manager and installs this handler right after, so a game that later
/// swapped its sink set left this pinned to the manager that call replaced. Replacing a manager SHUTS THE OLD ONE
/// DOWN, which disposes and clears its sinks, so the fatal entry went to a disposed queue or to an empty sink
/// list, and the live session log, the file a player actually sends, said nothing about the crash.</para>
///
/// <para><b><see cref="Install(LogManager)"/> PINS, ON PURPOSE.</b> A caller who hands in a manager owns it, and
/// the crash line belongs in THAT manager's sinks whatever the ambient facade is doing. It is the same split as
/// <see cref="LogManager.GetLogger(string)"/> against <see cref="Log.For{T}"/>: injected is bound, ambient
/// follows.</para>
///
/// <para>Everything here runs on the runtime's crash path, so nothing throws, and a report with no manager to
/// write through (never configured, or already shut down) does nothing at all.</para>
/// </summary>
public static class CrashHandler
{
    private static readonly object gate = new();

    /// <summary>
    /// The manager an <see cref="Install(LogManager)"/> caller handed in, or <c>null</c> for an ambient install,
    /// which resolves <see cref="Log.Current"/> per report instead.
    /// </summary>
    private static LogManager? pinned;

    /// <summary>
    /// Whether handlers are currently armed. Kept apart from <see cref="pinned"/> because an ambient install has
    /// no manager to hold, and "armed with nothing pinned" and "not armed" must not read the same.
    /// </summary>
    private static bool installed;

    private static UnhandledExceptionEventHandler? domainHandler;
    private static EventHandler<UnobservedTaskExceptionEventArgs>? taskHandler;

    /// <summary>
    /// Installs handlers that route crashes to <paramref name="manager"/>, and to that manager for as long as
    /// they stay installed: this overload PINS, so an injected manager and its sinks keep meaning what they say
    /// even across an ambient <see cref="Log.Configure(LoggerOptions)"/>. Use <see cref="Install()"/> for the
    /// ambient path. Idempotent, and a null manager is ignored.
    /// </summary>
    /// <param name="manager">The manager every crash entry is written to. Null is ignored.</param>
    public static void Install(LogManager manager)
    {
        if (manager is null) return;
        InstallCore(manager);
    }

    /// <summary>
    /// Installs handlers that route crashes to the ambient <see cref="Log"/>, resolved when a crash is reported.
    /// Safe to call before <see cref="Log.Configure(LoggerOptions)"/> (it arms, and reports once logging is
    /// configured) and unaffected by any later reconfigure. Idempotent.
    /// </summary>
    public static void Install() => InstallCore(null);

    /// <summary>Removes any installed handlers.</summary>
    public static void Uninstall()
    {
        lock (gate) { UninstallCore(); }
    }

    private static void InstallCore(LogManager? manager)
    {
        lock (gate)
        {
            UninstallCore();
            pinned = manager;
            installed = true;
            domainHandler = (_, e) => OnUnhandled(e.ExceptionObject, e.IsTerminating);
            taskHandler = (_, e) => { Report("Unobserved task exception", e.Exception, e.Exception); e.SetObserved(); };
            AppDomain.CurrentDomain.UnhandledException += domainHandler;
            TaskScheduler.UnobservedTaskException += taskHandler;
        }
    }

    private static void UninstallCore()
    {
        if (domainHandler is not null) AppDomain.CurrentDomain.UnhandledException -= domainHandler;
        if (taskHandler is not null) TaskScheduler.UnobservedTaskException -= taskHandler;
        domainHandler = null;
        taskHandler = null;
        pinned = null;
        installed = false;
    }

    /// <summary>
    /// The manager this report is written to: the injected one when there is one, otherwise whatever the ambient
    /// facade holds right now. Null when nothing is installed or nothing is configured, which is the
    /// do-nothing case. Takes <see cref="gate"/> only for the two fields, and never while calling into
    /// <see cref="Log"/>, so the crashing thread cannot meet a <see cref="Log.Configure(LoggerOptions)"/> holding
    /// the facade's own gate.
    /// </summary>
    private static LogManager? Resolve()
    {
        LogManager? injected;
        bool armed;
        lock (gate)
        {
            injected = pinned;
            armed = installed;
        }

        if (!armed) return null;
        return injected ?? Log.Current;
    }

    private static void OnUnhandled(object exceptionObject, bool isTerminating)
    {
        string context = isTerminating ? "Unhandled exception (terminating)" : "Unhandled exception";
        Report(context, exceptionObject as Exception, exceptionObject);
        if (isTerminating)
        {
            try { Resolve()?.Shutdown(); }
            catch { /* crash path: never throw */ }
        }
    }

    /// <summary>
    /// Logs a fatal crash entry and flushes. Exposed for testing, safe when uninstalled or unconfigured, and
    /// never throws (it runs from the runtime's crash path, where an exception would be catastrophic) even if the
    /// manager it resolves was already shut down.
    /// </summary>
    internal static void Report(string context, Exception? exception, object? raw)
    {
        LogManager? m = Resolve();
        if (m is null) return;

        try
        {
            var log = m.GetLogger("Crash");
            if (exception is not null) log.Fatal(context, exception);
            else log.Fatal($"{context}: {raw}");
            m.Flush();
        }
        catch { /* crash path: never throw */ }
    }
}
