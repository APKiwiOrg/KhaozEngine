using System;

namespace KhaozEngine.Diagnostics;

/// <summary>
/// An <see cref="ILogger"/> bound to one category, delegating to its owning <see cref="LogManager"/>. Handed out
/// by <see cref="LogManager.GetLogger(string)"/>, which is the INJECTED path: this logger belongs to that
/// manager for its whole life, which is what lets a caller (a test, a DI container) hold a manager and assert
/// what reached its sinks.
/// <para>
/// It is deliberately NOT what the ambient <see cref="Log"/> facade hands out. A pinned logger cached in a
/// <c>static readonly</c> field goes permanently silent once its manager is replaced, because
/// <see cref="Log.Configure(LoggerOptions)"/> shuts the replaced one down and shutdown disposes its sinks. See
/// <see cref="AmbientCategoryLogger"/>, which is what <see cref="Log.For{T}"/> returns, and #616 for the failure.
/// </para>
/// </summary>
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
