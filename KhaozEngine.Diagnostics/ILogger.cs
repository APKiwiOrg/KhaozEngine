using System;

namespace KhaozEngine.Diagnostics;

/// <summary>Logs messages under a fixed category. Obtain one from <see cref="LogManager.GetLogger(string)"/> or the static <c>Log</c> facade.</summary>
public interface ILogger
{
    /// <summary>The category/component tag this logger stamps on every entry.</summary>
    string Category { get; }

    /// <summary>True when entries at <paramref name="level"/> would be recorded.</summary>
    bool IsEnabled(LogLevel level);

    /// <summary>Logs a message at an explicit level.</summary>
    void Log(LogLevel level, string message, Exception? exception = null);

    /// <summary>Logs at <see cref="LogLevel.Trace"/>.</summary>
    void Trace(string message, Exception? exception = null);

    /// <summary>Logs at <see cref="LogLevel.Debug"/>.</summary>
    void Debug(string message, Exception? exception = null);

    /// <summary>Logs at <see cref="LogLevel.Info"/>.</summary>
    void Info(string message, Exception? exception = null);

    /// <summary>Logs at <see cref="LogLevel.Warn"/>.</summary>
    void Warn(string message, Exception? exception = null);

    /// <summary>Logs at <see cref="LogLevel.Error"/>.</summary>
    void Error(string message, Exception? exception = null);

    /// <summary>Logs at <see cref="LogLevel.Fatal"/>.</summary>
    void Fatal(string message, Exception? exception = null);
}
