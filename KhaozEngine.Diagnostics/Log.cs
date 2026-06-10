using System;

namespace KhaozEngine.Diagnostics;

/// <summary>
/// Process-wide ambient logging facade over a single configured <see cref="LogManager"/>. Games call
/// <see cref="Configure(LoggerOptions)"/> once at startup, then log via <see cref="For{T}"/> /
/// <see cref="Get(string)"/> or the convenience methods. Calls before configuration are safe no-ops.
/// </summary>
public static class Log
{
    private static readonly object gate = new();
    private static LogManager? manager;

    /// <summary>True once a manager has been configured.</summary>
    public static bool IsConfigured { get { lock (gate) { return manager is not null; } } }

    /// <summary>The configured manager, or <c>null</c>.</summary>
    public static LogManager? Manager { get { lock (gate) { return manager; } } }

    /// <summary>Builds and adopts a manager from <paramref name="options"/>.</summary>
    public static void Configure(LoggerOptions options) => Configure(new LogManager(options));

    /// <summary>Adopts an existing manager (for example one built via DI). Shuts down any previous manager.</summary>
    public static void Configure(LogManager newManager)
    {
        LogManager? previous;
        lock (gate)
        {
            previous = manager;
            manager = newManager;
        }
        previous?.Shutdown();
    }

    /// <summary>Minimum level of the configured manager (no-op getter returns <see cref="LogLevel.Info"/> when unconfigured).</summary>
    public static LogLevel MinimumLevel
    {
        get { return Manager?.MinimumLevel ?? LogLevel.Info; }
        set { var m = Manager; if (m is not null) m.MinimumLevel = value; }
    }

    /// <summary>Returns a logger for category <c>typeof(T).Name</c>, or a no-op logger when unconfigured.</summary>
    public static ILogger For<T>() => Manager?.GetLogger<T>() ?? NullLogger.Instance;

    /// <summary>Returns a logger for <paramref name="category"/>, or a no-op logger when unconfigured.</summary>
    public static ILogger Get(string category) => Manager?.GetLogger(category) ?? NullLogger.Instance;

    private static ILogger Default()
    {
        var m = Manager;
        return m is null ? NullLogger.Instance : m.GetLogger(m.DefaultCategory);
    }

    /// <summary>Logs at <see cref="LogLevel.Trace"/> under the default category.</summary>
    public static void Trace(string message, Exception? exception = null) => Default().Trace(message, exception);
    /// <summary>Logs at <see cref="LogLevel.Debug"/> under the default category.</summary>
    public static void Debug(string message, Exception? exception = null) => Default().Debug(message, exception);
    /// <summary>Logs at <see cref="LogLevel.Info"/> under the default category.</summary>
    public static void Info(string message, Exception? exception = null) => Default().Info(message, exception);
    /// <summary>Logs at <see cref="LogLevel.Warn"/> under the default category.</summary>
    public static void Warn(string message, Exception? exception = null) => Default().Warn(message, exception);
    /// <summary>Logs at <see cref="LogLevel.Error"/> under the default category.</summary>
    public static void Error(string message, Exception? exception = null) => Default().Error(message, exception);
    /// <summary>Logs at <see cref="LogLevel.Fatal"/> under the default category.</summary>
    public static void Fatal(string message, Exception? exception = null) => Default().Fatal(message, exception);

    /// <summary>Flushes the configured manager (no-op when unconfigured).</summary>
    public static void Flush() => Manager?.Flush();

    /// <summary>Shuts down and detaches the configured manager.</summary>
    public static void Shutdown()
    {
        LogManager? previous;
        lock (gate)
        {
            previous = manager;
            manager = null;
        }
        previous?.Shutdown();
    }
}
