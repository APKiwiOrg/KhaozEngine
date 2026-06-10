using System;

namespace KhaozEngine.Diagnostics;

/// <summary>An <see cref="ILogger"/> bound to one category, delegating to its owning <see cref="LogManager"/>.</summary>
internal sealed class CategoryLogger : ILogger
{
    private readonly LogManager owner;

    public string Category { get; }

    public CategoryLogger(LogManager owner, string category)
    {
        this.owner = owner;
        Category = category ?? string.Empty;
    }

    public bool IsEnabled(LogLevel level) => owner.IsEnabled(level);

    public void Log(LogLevel level, string message, Exception? exception = null)
    {
        if (!owner.IsEnabled(level)) return;
        owner.Submit(new LogEntry(owner.Now, level, Category, message ?? string.Empty, exception));
    }

    public void Trace(string message, Exception? exception = null) => Log(LogLevel.Trace, message, exception);
    public void Debug(string message, Exception? exception = null) => Log(LogLevel.Debug, message, exception);
    public void Info(string message, Exception? exception = null)  => Log(LogLevel.Info,  message, exception);
    public void Warn(string message, Exception? exception = null)  => Log(LogLevel.Warn,  message, exception);
    public void Error(string message, Exception? exception = null) => Log(LogLevel.Error, message, exception);
    public void Fatal(string message, Exception? exception = null) => Log(LogLevel.Fatal, message, exception);
}
