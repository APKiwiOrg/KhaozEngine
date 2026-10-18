using System;
using System.Collections.Generic;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.Tests.BugReports.Relay.Support;

internal sealed class RecordingLogger : ILogger
{
    public List<(LogLevel Level, string Message)> Entries { get; } = new();
    public string Category => "BugReports.Relay.Tests";
    public bool IsEnabled(LogLevel level) => true;
    public void Log(LogLevel level, string message, Exception? exception = null) => Entries.Add((level, message));
    public void Trace(string message, Exception? exception = null) => Log(LogLevel.Trace, message, exception);
    public void Debug(string message, Exception? exception = null) => Log(LogLevel.Debug, message, exception);
    public void Info(string message, Exception? exception = null) => Log(LogLevel.Info, message, exception);
    public void Warn(string message, Exception? exception = null) => Log(LogLevel.Warn, message, exception);
    public void Error(string message, Exception? exception = null) => Log(LogLevel.Error, message, exception);
    public void Fatal(string message, Exception? exception = null) => Log(LogLevel.Fatal, message, exception);
}
