using System;
using System.Threading.Tasks;

namespace KhaozEngine.Diagnostics;

/// <summary>
/// Wires process-level crash signals (<see cref="AppDomain.UnhandledException"/> and
/// <see cref="TaskScheduler.UnobservedTaskException"/>) to a <see cref="LogManager"/>: logs a
/// <see cref="LogLevel.Fatal"/> entry under category <c>Crash</c> and flushes. On a terminating
/// unhandled exception it also shuts the manager down so the log file is closed cleanly.
/// </summary>
public static class CrashHandler
{
    private static readonly object gate = new();
    private static LogManager? target;
    private static UnhandledExceptionEventHandler? domainHandler;
    private static EventHandler<UnobservedTaskExceptionEventArgs>? taskHandler;

    /// <summary>Installs handlers that route crashes to <paramref name="manager"/>. Idempotent.</summary>
    public static void Install(LogManager manager)
    {
        if (manager is null) return;
        lock (gate)
        {
            UninstallCore();
            target = manager;
            domainHandler = (_, e) => OnUnhandled(e.ExceptionObject, e.IsTerminating);
            taskHandler = (_, e) => { Report("Unobserved task exception", e.Exception, e.Exception); e.SetObserved(); };
            AppDomain.CurrentDomain.UnhandledException += domainHandler;
            TaskScheduler.UnobservedTaskException += taskHandler;
        }
    }

    /// <summary>Installs handlers routed to the ambient <see cref="Log.Manager"/> (no-op if none).</summary>
    public static void Install()
    {
        var m = Log.Manager;
        if (m is not null) Install(m);
    }

    /// <summary>Removes any installed handlers.</summary>
    public static void Uninstall()
    {
        lock (gate) { UninstallCore(); }
    }

    private static void UninstallCore()
    {
        if (domainHandler is not null) AppDomain.CurrentDomain.UnhandledException -= domainHandler;
        if (taskHandler is not null) TaskScheduler.UnobservedTaskException -= taskHandler;
        domainHandler = null;
        taskHandler = null;
        target = null;
    }

    private static void OnUnhandled(object exceptionObject, bool isTerminating)
    {
        string context = isTerminating ? "Unhandled exception (terminating)" : "Unhandled exception";
        Report(context, exceptionObject as Exception, exceptionObject);
        if (isTerminating)
        {
            LogManager? m;
            lock (gate) { m = target; }
            try { m?.Shutdown(); }
            catch { /* crash path: never throw */ }
        }
    }

    /// <summary>
    /// Logs a fatal crash entry and flushes. Exposed for testing; safe when uninstalled, and never throws
    /// (it runs from the runtime's crash path, where an exception would be catastrophic) even if the
    /// captured manager was already shut down.
    /// </summary>
    internal static void Report(string context, Exception? exception, object? raw)
    {
        LogManager? m;
        lock (gate) { m = target; }
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
