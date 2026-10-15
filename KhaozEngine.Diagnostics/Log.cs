using System;
using System.Threading;

namespace KhaozEngine.Diagnostics;

/// <summary>
/// Ambient logging facade over async-flow overrides and a configured process <see cref="LogManager"/>. Games call
/// <see cref="Configure(LoggerOptions)"/> once at startup, then log via <see cref="For{T}"/> /
/// <see cref="Get(string)"/> or the convenience methods. Calls with neither a scope nor a process manager are
/// safe no-ops. <see cref="Use"/> borrows a manager without changing the process default or its lifetime.
///
/// <para><b>A LOGGER FROM THIS FACADE FOLLOWS THE FACADE, FOR AS LONG AS IT IS HELD (#616).</b>
/// <see cref="For{T}"/> and <see cref="Get(string)"/> hand back a logger bound to a CATEGORY and to nothing
/// else, which reads the innermost scope or current process manager on every call. So a logger resolved BEFORE
/// <see cref="Configure(LoggerOptions)"/> starts writing the moment configuration lands, and a logger resolved
/// before a RECONFIGURE follows the new manager instead of the shut-down one.</para>
///
/// <para>That is load-bearing rather than a nicety. Caching a category logger in a <c>static readonly</c> field
/// is the natural way to write a logging call site, and 23 types across the GPU packages do exactly that. A
/// facade that pinned the manager at resolution time turned every one of those fields into a permanently silent
/// logger whenever its type was touched before the process configured logging, because
/// <see cref="Configure(LoggerOptions)"/> SHUTS DOWN the manager it replaces and shutdown disposes and clears
/// that manager's sinks. The result had no symptom at all: the logger still reported itself enabled, still
/// submitted, and the entries went nowhere, and a dropped log line looks exactly like a clean run.</para>
///
/// <para>A logger from <see cref="LogManager.GetLogger(string)"/> is the deliberate opposite. It is bound to THAT
/// manager for its whole life, which is what makes the injected path (DI, and every test that asserts against a
/// manager it owns) mean what it says.</para>
/// </summary>
public static class Log
{
    private static readonly object gate = new();
    private static LogManager? manager;
    private static readonly AmbientScope<LogManager?> scopes = new();

    /// <summary>
    /// The logger the category-free convenience methods write through. One instance for the process: it carries
    /// no manager and no category, so it stays correct across every <see cref="Configure(LoggerOptions)"/>.
    /// </summary>
    private static readonly AmbientCategoryLogger defaultLogger = new(string.Empty);

    /// <summary>
    /// Per-category-type cache for <see cref="For{T}"/>. An ambient logger holds nothing but its category
    /// string, so one instance per <typeparamref name="T"/> is correct forever and <see cref="For{T}"/> stops
    /// allocating after the first call for that type. A generic static keeps that lookup free of a dictionary,
    /// a lock and any unbounded growth (the key space is the closed generic types the program actually uses).
    /// </summary>
    private static class Ambient<T>
    {
        internal static readonly AmbientCategoryLogger Instance = new(typeof(T).Name);
    }

    /// <summary>
    /// The process manager, independent of async-flow scopes. Preserve the volatile publication read for
    /// process mutation and process crash reporting without taking <see cref="gate"/>.
    /// </summary>
    internal static LogManager? ProcessManager => Volatile.Read(ref manager);

    /// <summary>The innermost async-flow manager, then the current process default.</summary>
    internal static LogManager? Current => scopes.GetValueOrDefault(ProcessManager);

    /// <summary>True when this flow has a scoped or process manager.</summary>
    public static bool IsConfigured => Current is not null;

    /// <summary>The selected async-flow or process manager, or <c>null</c>.</summary>
    public static LogManager? Manager => Current;

    /// <summary>
    /// Borrows a manager for this async flow, across awaits and in child tasks. Dispose in reverse creation
    /// order in each flow. Disposing an active outer scope throws without changing selection. Disposal after
    /// removal is a no-op in that flow. Inherited copies dispose independently. The manager is never shut down
    /// or disposed by the scope. Process configuration and mutation stay process-wide.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="manager"/> is null.</exception>
    public static IDisposable Use(LogManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        return scopes.Use(manager);
    }

    /// <summary>Builds and adopts a process manager from <paramref name="options"/>.</summary>
    public static void Configure(LoggerOptions options) => Configure(new LogManager(options));

    /// <summary>Adopts a process manager (for example one built via DI). Shuts down the previous process manager.</summary>
    public static void Configure(LogManager newManager)
    {
        LogManager? previous;
        lock (gate)
        {
            previous = manager;
            Volatile.Write(ref manager, newManager);
        }
        previous?.Shutdown();
    }

    /// <summary>
    /// Reads the selected async-flow manager's level, or <see cref="LogLevel.Info"/> when unconfigured.
    /// Assignment always changes the process manager's level and is a no-op without a process manager.
    /// </summary>
    public static LogLevel MinimumLevel
    {
        get { return Current?.MinimumLevel ?? LogLevel.Info; }
        set { var m = ProcessManager; if (m is not null) m.MinimumLevel = value; }
    }

    /// <summary>
    /// Returns a logger for category <c>typeof(T).Name</c>. Safe to cache in a field: it resolves the selected
    /// manager per call, so it is never left pointing at a shut-down one (see the note on <see cref="Log"/>).
    /// </summary>
    public static ILogger For<T>() => Ambient<T>.Instance;

    /// <summary>
    /// Returns a logger for <paramref name="category"/>, or the configured <see cref="LogManager.DefaultCategory"/>
    /// when it is null or empty. Safe to cache in a field, on the same terms as <see cref="For{T}"/>.
    /// </summary>
    public static ILogger Get(string category)
        => string.IsNullOrEmpty(category) ? defaultLogger : new AmbientCategoryLogger(category);

    /// <summary>Logs at <see cref="LogLevel.Trace"/> under the default category.</summary>
    public static void Trace(string message, Exception? exception = null) => defaultLogger.Trace(message, exception);
    /// <summary>Logs at <see cref="LogLevel.Debug"/> under the default category.</summary>
    public static void Debug(string message, Exception? exception = null) => defaultLogger.Debug(message, exception);
    /// <summary>Logs at <see cref="LogLevel.Info"/> under the default category.</summary>
    public static void Info(string message, Exception? exception = null) => defaultLogger.Info(message, exception);
    /// <summary>Logs at <see cref="LogLevel.Warn"/> under the default category.</summary>
    public static void Warn(string message, Exception? exception = null) => defaultLogger.Warn(message, exception);
    /// <summary>Logs at <see cref="LogLevel.Error"/> under the default category.</summary>
    public static void Error(string message, Exception? exception = null) => defaultLogger.Error(message, exception);
    /// <summary>Logs at <see cref="LogLevel.Fatal"/> under the default category.</summary>
    public static void Fatal(string message, Exception? exception = null) => defaultLogger.Fatal(message, exception);

    /// <summary>Flushes the selected async-flow or process manager (no-op when unconfigured).</summary>
    public static void Flush() => Current?.Flush();

    /// <summary>Shuts down and detaches the process manager. Borrowed scoped managers stay selected and alive.</summary>
    public static void Shutdown()
    {
        LogManager? previous;
        lock (gate)
        {
            previous = manager;
            Volatile.Write(ref manager, null);
        }
        previous?.Shutdown();
    }
}
