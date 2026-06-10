using System;

namespace KhaozEngine.Diagnostics;

/// <summary>A logger that discards everything. Returned by <see cref="Log"/> before configuration.</summary>
internal sealed class NullLogger : ILogger
{
    public static readonly NullLogger Instance = new();

    private NullLogger() { }

    public string Category => string.Empty;
    public bool IsEnabled(LogLevel level) => false;
    public void Log(LogLevel level, string message, Exception? exception = null) { }
    public void Trace(string message, Exception? exception = null) { }
    public void Debug(string message, Exception? exception = null) { }
    public void Info(string message, Exception? exception = null) { }
    public void Warn(string message, Exception? exception = null) { }
    public void Error(string message, Exception? exception = null) { }
    public void Fatal(string message, Exception? exception = null) { }
}
